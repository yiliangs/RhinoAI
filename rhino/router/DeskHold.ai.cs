using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

namespace Rhino.AI.Router;

// A hold with rhino-desk, the file-locked arbiter that decides which Rhino cores
// may run side by side on one machine's licence (rhino-desk README,
// "Integrating a session").
//
// Every Rhino 8 core on a machine shares one Cloud Zoo lease, and a hidden slot
// started beside a Rhino.Inside host makes both lose the licence about 106 s
// later. So the router takes a `slot` hold before it starts a slot's Rhino.exe,
// attaches the Rhino pid once the process exists, and releases the hold when the
// slot row goes away (SlotStore releases on every delete path). The desk is a
// Windows tool; on other platforms, or with RHINO_DESK_OFF set, no hold is taken.
//
// The desk is driven through its CLI, as its README asks of C# callers: exit 0
// prints the hold id, 3 is a refusal with the blockers on stderr, 4 means the
// desk could not decide.
public sealed class DeskHold(RouterConfig config, ILogger<DeskHold> log, IDeskCli? cli = null, Func<string, string?>? env = null)
{
    // The folder holding rhino-desk.ps1; default %USERPROFILE%\source\repos\rhino-desk.
    public const string PathEnvVar = "RHINO_DESK_PATH";

    // Any value but empty, 0, false, no or off spawns without a hold.
    public const string OffEnvVar = "RHINO_DESK_OFF";

    // Session name recorded as the hold's owner; default <router process>-<pid>.
    public const string OwnerEnvVar = "RHINO_DESK_OWNER";

    public const string ScriptName = "rhino-desk.ps1";

    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;
    private readonly IDeskCli _cli = cli ?? new DeskCliProcess();
    private int _offLogged;

    public bool IsOff
    {
        get
        {
            string? raw = _env(OffEnvVar)?.Trim();
            if (string.IsNullOrEmpty(raw)) return false;
            return !(raw == "0" ||
                     raw.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                     raw.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                     raw.Equals("off", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Whether spawns on this machine go through the desk.
    public bool Applies => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !IsOff;

    public string Owner
    {
        get
        {
            string? owner = _env(OwnerEnvVar)?.Trim();
            if (!string.IsNullOrEmpty(owner)) return owner;
            using Process self = Process.GetCurrentProcess();
            return $"{self.ProcessName}-{Environment.ProcessId}";
        }
    }

    public string ScriptPath
    {
        get
        {
            string? dir = _env(PathEnvVar)?.Trim();
            if (string.IsNullOrEmpty(dir))
            {
                string home = _env("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                dir = Path.Combine(home, "source", "repos", "rhino-desk");
            }
            return Path.Combine(dir, ScriptName);
        }
    }

    // Takes a hold of `kind` for `pid`, waiting as long as RouterConfig.DeskWaitSeconds
    // allows (null: no limit). Returns the hold id, or null when the desk is off.
    // Throws DeskHoldException on a refusal, a desk error, or a missing CLI.
    //
    // The first call does not queue, so a contended start can log what it waits
    // on before it waits. A cancelled caller stops waiting at once; the CLI call
    // runs on, and a hold it is granted later is released straight away rather
    // than left queued against the next request.
    public async Task<string?> AcquireAsync(
        string kind, string owner, string purpose, string priority, int pid, bool allowOwner,
        CancellationToken ct = default)
    {
        if (!Applies)
        {
            if (IsOff && Interlocked.Exchange(ref _offLogged, 1) == 0)
                log.LogInformation("rhino-desk is off ({Env} is set); spawning slots without a hold.", OffEnvVar);
            return null;
        }

        List<string> baseArgs =
        [
            "acquire", "--kind", kind, "--owner", owner, "--purpose", purpose,
            "--priority", priority, "--pid", pid.ToString(),
        ];
        if (allowOwner) baseArgs.Add("--allow-owner");

        DeskCliResult first = await RunAsync([.. baseArgs, "--no-wait"]).WaitAsync(ct).ConfigureAwait(false);
        if (first.ExitCode == 0) return HoldId(first);
        if (first.ExitCode != 3) throw DeskError(first);

        int? wait = config.DeskWaitSeconds;
        if (wait == 0) throw Refused(first);

        log.LogWarning("rhino-desk: waiting on {Blockers} before starting a {Kind} ({Wait}).",
            Blockers(first), kind, wait is { } w ? $"up to {w} s" : "no limit");

        List<string> waitArgs = [.. baseArgs];
        if (wait is { } seconds) waitArgs.AddRange(["--wait", seconds.ToString()]);

        Task<DeskCliResult> waiting = RunAsync(waitArgs);
        DeskCliResult second;
        try
        {
            second = await waiting.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = waiting.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.ExitCode == 0)
                    Release(HoldId(t.Result));
            }, TaskScheduler.Default);
            throw;
        }

        if (second.ExitCode == 0) return HoldId(second);
        if (second.ExitCode == 3) throw Refused(second);
        throw DeskError(second);
    }

    // Adds a core's pid to the hold. A failure is logged, not thrown: the core
    // descends from the router, whose pid is already in the hold.
    public void Attach(string holdId, int pid)
    {
        try
        {
            DeskCliResult r = Run(["attach", "--hold", holdId, "--pid", pid.ToString()]);
            if (r.ExitCode != 0)
                log.LogWarning("rhino-desk attach {Hold} pid {Pid} failed (exit {Code}): {Err}",
                    holdId, pid, r.ExitCode, r.StdErr.Trim());
        }
        catch (DeskHoldException ex)
        {
            log.LogWarning("rhino-desk attach {Hold} pid {Pid} failed: {Message}", holdId, pid, ex.Message);
        }
    }

    // Drops the hold. Idempotent on the desk side; a failure is only logged,
    // since the desk drops a hold whose pids are all dead on its own.
    public void Release(string holdId)
    {
        try
        {
            DeskCliResult r = Run(["release", "--hold", holdId]);
            if (r.ExitCode != 0)
                log.LogWarning("rhino-desk release {Hold} failed (exit {Code}): {Err}",
                    holdId, r.ExitCode, r.StdErr.Trim());
            else
                log.LogInformation("rhino-desk: released hold {Hold}.", holdId);
        }
        catch (DeskHoldException ex)
        {
            log.LogWarning("rhino-desk release {Hold} failed: {Message}", holdId, ex.Message);
        }
    }

    private Task<DeskCliResult> RunAsync(IReadOnlyList<string> args) => Task.Run(() => Run(args));

    private DeskCliResult Run(IReadOnlyList<string> args)
    {
        string script = ScriptPath;
        if (!_cli.Exists(script))
        {
            throw new DeskHoldException("desk_unavailable",
                $"rhino-desk was not found at {script}. Set {PathEnvVar} to the rhino-desk folder, " +
                $"or set {OffEnvVar}=1 to spawn slots without a hold.");
        }
        try
        {
            return _cli.Run(script, args);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new DeskHoldException("desk_unavailable",
                $"Could not run rhino-desk ({script}) through pwsh: {ex.Message}. " +
                $"Set {OffEnvVar}=1 to spawn slots without a hold.");
        }
    }

    private static string HoldId(DeskCliResult r)
    {
        string id = r.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        if (id.Length == 0)
            throw new DeskHoldException("desk_error", "rhino-desk granted a hold but printed no hold id.");
        return id;
    }

    // stderr on a refusal: "rhino-desk: <reason>; blocked by:" then one indented line per blocker.
    private static string Blockers(DeskCliResult r)
    {
        string[] lines = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] blockers = lines.Where(l => !l.StartsWith("rhino-desk:", StringComparison.Ordinal)).ToArray();
        return blockers.Length > 0 ? string.Join("; ", blockers) : (lines.FirstOrDefault() ?? "an unnamed blocker");
    }

    private static DeskHoldException Refused(DeskCliResult r)
    {
        const string prefix = "rhino-desk:", suffix = "; blocked by:";
        string reason = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim() ?? "refused";
        if (reason.EndsWith(suffix, StringComparison.Ordinal)) reason = reason[..^suffix.Length];
        return new DeskHoldException("desk_refused",
            $"rhino-desk refused the slot hold ({reason}): {Blockers(r)}. " +
            "No Rhino was started. Wait for those to finish, or run `rhino-desk status` to see the desk.");
    }

    private static DeskHoldException DeskError(DeskCliResult r) => new("desk_error",
        $"rhino-desk could not decide (exit {r.ExitCode}): {r.StdErr.Trim()}. No Rhino was started; nothing on the desk was changed.");
}

// Why a spawn did not get a desk hold. Code is the MCP error code.
public sealed class DeskHoldException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record DeskCliResult(int ExitCode, string StdOut, string StdErr);

// The seam the tests replace: one CLI call, blocking until it exits.
public interface IDeskCli
{
    bool Exists(string scriptPath);
    DeskCliResult Run(string scriptPath, IReadOnlyList<string> args);
}

// Runs rhino-desk.ps1 through pwsh, which picks the desk's Python interpreter.
public sealed class DeskCliProcess : IDeskCli
{
    public bool Exists(string scriptPath) => File.Exists(scriptPath);

    public DeskCliResult Run(string scriptPath, IReadOnlyList<string> args)
    {
        ProcessStartInfo psi = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("pwsh did not start.");
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new DeskCliResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
