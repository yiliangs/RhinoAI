using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Rhino.AI.Router;
using Rhino.AI.Router.Tools;

namespace Rhino.AI.Router.Tests;

// The rhino-desk slot hold around a spawn: acquired before the Rhino starts,
// the Rhino pid attached after, released when the slot row goes (close, death,
// departure), a refusal surfaced with its blockers, and RHINO_DESK_OFF bypassing
// the desk. The desk CLI is a fake and the Windows launcher is replaced by one
// that opens the slot's port in this process, so nothing here starts Rhino or
// touches the machine's real desk.
[TestFixture]
public sealed class DeskHoldTests
{
    private const string Blocked =
        "rhino-desk: refused (--no-wait); blocked by:\n  host h-12 by paper-cad-96 (ingest meshing alder-quay), pids 4242\n";

    private string _homeDir = null!;
    private string? _previousHome;
    private readonly List<string> _calls = [];
    private readonly List<TcpListener> _listeners = [];
    private readonly int _testPid = Environment.ProcessId;

    [SetUp]
    public void SetUp()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Ignore("rhino-desk applies to Windows spawns only.");
        _homeDir = Path.Combine(Path.GetTempPath(), "rhmcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_homeDir);
        _previousHome = Environment.GetEnvironmentVariable(RouterPaths.HomeOverrideEnvVar);
        Environment.SetEnvironmentVariable(RouterPaths.HomeOverrideEnvVar, _homeDir);
        _calls.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (TcpListener l in _listeners) l.Stop();
        _listeners.Clear();
        Environment.SetEnvironmentVariable(RouterPaths.HomeOverrideEnvVar, _previousHome);
        try { Directory.Delete(_homeDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Spawn_acquires_before_launch_and_attaches_the_rhino_pid_after()
    {
        (RhinoManager manager, SlotStore store) = Make(new FakeDeskCli(_calls));
        using (store)
        {
            ChildRhino child = await manager.SpawnAsync("8");

            Assert.That(_calls.Select(Verb), Is.EqualTo(new[] { "acquire", "launch", "attach" }));
            Assert.That(_calls[0], Does.Contain("--kind slot").And.Contain("--priority pilot")
                .And.Contain($"--pid {_testPid}").And.Contain("--no-wait"));
            Assert.That(_calls[2], Is.EqualTo($"attach --hold h-1 --pid {_testPid}"));
            Assert.That(child.DeskHold, Is.EqualTo("h-1"));
            Assert.That(store.Get(child.SlotId)!.DeskHold, Is.EqualTo("h-1"));
        }
    }

    [Test]
    public async Task Close_releases_the_hold()
    {
        (RhinoManager manager, SlotStore store) = Make(new FakeDeskCli(_calls));
        using (store)
        {
            (_, string slotId) = store.ReserveNewNamed("8", _testPid, "h-7");

            Assert.That(await manager.CloseAsync(slotId), Is.True);

            Assert.That(_calls, Is.EqualTo(new[] { "release --hold h-7" }));
        }
    }

    [Test]
    public void A_dead_slot_releases_its_hold_when_reaped()
    {
        (RhinoManager manager, SlotStore store) = Make(new FakeDeskCli(_calls));
        using (store)
        {
            (_, string slotId) = store.ReserveNewNamed("8", _testPid, "h-8");
            store.MarkReady(slotId, port: 10999, pid: 0x7FFFFFFC); // no such process

            Assert.That(manager.ReapAllDead().Select(c => c.SlotId), Is.EqualTo(new[] { slotId }));
            Assert.That(_calls, Is.EqualTo(new[] { "release --hold h-8" }));
        }
    }

    [Test]
    public void A_departed_listener_releases_its_hold()
    {
        (_, SlotStore store) = Make(new FakeDeskCli(_calls));
        using (store)
        {
            (_, string slotId) = store.ReserveNewNamed("8", _testPid, "h-9");
            store.MarkReady(slotId, port: 10998, pid: 4321);

            Assert.That(store.DeleteByListener(4321, 10998), Is.EqualTo(new[] { slotId }));
            Assert.That(_calls, Is.EqualTo(new[] { "release --hold h-9" }));
        }
    }

    [Test]
    public void A_refusal_names_the_blockers_and_starts_nothing()
    {
        FakeDeskCli cli = new(_calls) { Acquire = _ => new(3, "", Blocked) };
        (RhinoManager manager, SlotStore store) = Make(cli, "--desk-wait", "0");
        using (store)
        {
            DeskHoldException ex = Assert.ThrowsAsync<DeskHoldException>((Func<Task>)(() => manager.SpawnAsync("8")))!;

            Assert.That(ex.Code, Is.EqualTo("desk_refused"));
            Assert.That(ex.Message, Does.Contain("host h-12 by paper-cad-96"));
            Assert.That(_calls.Select(Verb), Is.EqualTo(new[] { "acquire" }));
            Assert.That(store.ListReady(), Is.Empty);

            ErrorInfo error = SpawnSlotTool.Diagnose(ex, new RhinoCrashReportFinder(NullLogger<RhinoCrashReportFinder>.Instance));
            Assert.That(error.Code, Is.EqualTo("desk_refused"));
            Assert.That(error.Message, Does.Contain("host h-12 by paper-cad-96"));
        }
    }

    [Test]
    public void A_busy_desk_is_waited_on_up_to_the_configured_limit()
    {
        FakeDeskCli cli = new(_calls)
        {
            Acquire = args => args.Contains("--no-wait")
                ? new(3, "", Blocked)
                : new(3, "", Blocked.Replace("refused (--no-wait)", "refused after waiting 5 s")),
        };
        (RhinoManager manager, SlotStore store) = Make(cli, "--desk-wait", "5");
        using (store)
        {
            DeskHoldException ex = Assert.ThrowsAsync<DeskHoldException>((Func<Task>)(() => manager.SpawnAsync("8")))!;

            Assert.That(ex.Code, Is.EqualTo("desk_refused"));
            Assert.That(ex.Message, Does.Contain("refused after waiting 5 s"));
            Assert.That(_calls, Has.Count.EqualTo(2));
            Assert.That(_calls[1], Does.Contain("--wait 5").And.Not.Contain("--no-wait"));
        }
    }

    [Test]
    public async Task A_busy_desk_without_a_limit_waits_until_granted()
    {
        FakeDeskCli cli = new(_calls)
        {
            Acquire = args => args.Contains("--no-wait") ? new(3, "", Blocked) : new(0, "h-2\n", ""),
        };
        (RhinoManager manager, SlotStore store) = Make(cli);
        using (store)
        {
            ChildRhino child = await manager.SpawnAsync("8");

            Assert.That(_calls.Select(Verb), Is.EqualTo(new[] { "acquire", "acquire", "launch", "attach" }));
            Assert.That(_calls[1], Does.Not.Contain("--wait").And.Not.Contain("--no-wait"));
            Assert.That(child.DeskHold, Is.EqualTo("h-2"));
        }
    }

    [Test]
    public void A_desk_error_is_reported_as_such()
    {
        FakeDeskCli cli = new(_calls) { Acquire = _ => new(4, "", "rhino-desk: lock busy for 60 s\n") };
        (RhinoManager manager, SlotStore store) = Make(cli);
        using (store)
        {
            DeskHoldException ex = Assert.ThrowsAsync<DeskHoldException>((Func<Task>)(() => manager.SpawnAsync("8")))!;
            Assert.That(ex.Code, Is.EqualTo("desk_error"));
            Assert.That(ex.Message, Does.Contain("lock busy"));
            Assert.That(_calls.Select(Verb), Is.EqualTo(new[] { "acquire" }));
        }
    }

    [Test]
    public void A_missing_desk_names_its_path_and_starts_nothing()
    {
        FakeDeskCli cli = new(_calls) { Present = false };
        (RhinoManager manager, SlotStore store) = Make(cli, env: Env(path: @"C:\nowhere\rhino-desk"));
        using (store)
        {
            DeskHoldException ex = Assert.ThrowsAsync<DeskHoldException>((Func<Task>)(() => manager.SpawnAsync("8")))!;
            Assert.That(ex.Code, Is.EqualTo("desk_unavailable"));
            Assert.That(ex.Message, Does.Contain(@"C:\nowhere\rhino-desk\rhino-desk.ps1").And.Contain("RHINO_DESK_OFF"));
            Assert.That(_calls, Is.Empty);
        }
    }

    [Test]
    public async Task RHINO_DESK_OFF_spawns_without_a_hold()
    {
        FakeDeskCli cli = new(_calls) { Present = false };
        (RhinoManager manager, SlotStore store) = Make(cli, env: Env(off: "1"));
        using (store)
        {
            ChildRhino child = await manager.SpawnAsync("8");

            Assert.That(_calls, Is.EqualTo(new[] { "launch" }));
            Assert.That(child.DeskHold, Is.Null);
            store.Delete(child.SlotId);
            Assert.That(_calls, Is.EqualTo(new[] { "launch" }));
        }
    }

    [TestCase(new string[0], null)]
    [TestCase(new[] { "--desk-wait", "30" }, 30)]
    [TestCase(new[] { "--desk-wait", "0" }, 0)]
    [TestCase(new[] { "--desk-wait", "unlimited" }, null)]
    [TestCase(new[] { "--desk-wait", "-4" }, null)]
    public void Desk_wait_parses(string[] args, int? expected)
    {
        Assert.That(RouterConfig.FromArgs(args).DeskWaitSeconds, Is.EqualTo(expected));
    }

    [Test]
    public void The_shim_defaults_to_the_benchmark_checkout_and_RHINO_DESK_PATH_overrides_it()
    {
        RouterConfig config = RouterConfig.FromArgs([]);
        DeskHold byDefault = new(config, NullLogger<DeskHold>.Instance, env: name => name == "USERPROFILE" ? @"C:\Users\someone" : null);
        DeskHold overridden = new(config, NullLogger<DeskHold>.Instance, env: Env(path: @"D:\desk"));

        Assert.That(byDefault.ScriptPath, Is.EqualTo(Path.Combine(@"C:\Users\someone", "source", "repos", "paper-CAD-benchmark", "scripts", "rhino-desk.ps1")));
        Assert.That(overridden.ScriptPath, Is.EqualTo(Path.Combine(@"D:\desk", "rhino-desk.ps1")));
    }

    // ----- helpers -----------------------------------------------------------

    private static string Verb(string call) => call.Split(' ')[0];

    private static Func<string, string?> Env(string? off = null, string? path = @"C:\desk") => name => name switch
    {
        DeskHold.OffEnvVar => off,
        DeskHold.PathEnvVar => path,
        DeskHold.OwnerEnvVar => "router-tests",
        _ => null,
    };

    private (RhinoManager, SlotStore) Make(FakeDeskCli cli, params string[] args) => Make(cli, Env(), args);

    private (RhinoManager, SlotStore) Make(FakeDeskCli cli, Func<string, string?> env, params string[] args)
    {
#if !DEBUG
        if (!RhinoLocator.IsPluginInstalled("8"))
            Assert.Ignore("A Release build checks the rh-mcp plugin is installed for Rhino 8 before spawning.");
#endif
        // Any existing file stands in for Rhino.exe; the launcher below never runs it.
        string exe = typeof(DeskHoldTests).Assembly.Location;
        RouterConfig config = RouterConfig.FromArgs([.. args, "--rhino-exe", $"8={exe}"]);
        DeskHold desk = new(config, NullLogger<DeskHold>.Instance, cli, env);
        SlotStore store = new(NullLogger<SlotStore>.Instance, desk);
        RhinoControlClient control = new(new StubHttpClientFactory(), NullLogger<RhinoControlClient>.Instance);
        RhinoManager manager = new(config, control, store, desk, NullLogger<RhinoManager>.Instance)
        {
            LaunchWindowsProcess = FakeLaunch,
        };
        return (manager, store);
    }

    // Stands in for Rhino.exe: binds the slot's port here and reports this process,
    // so the spawn path sees a live process with a bound port.
    private Process FakeLaunch(string exe, int port, SpawnWindowMode mode)
    {
        _calls.Add("launch");
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        _listeners.Add(listener);
        return Process.GetCurrentProcess();
    }

    private sealed class FakeDeskCli(List<string> calls) : IDeskCli
    {
        public bool Present { get; init; } = true;
        public Func<IReadOnlyList<string>, DeskCliResult> Acquire { get; init; } = _ => new(0, "h-1\n", "");

        public bool Exists(string scriptPath) => Present;

        public DeskCliResult Run(string scriptPath, IReadOnlyList<string> args)
        {
            lock (calls) calls.Add(string.Join(' ', args));
            return args[0] == "acquire" ? Acquire(args) : new(0, "", "");
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

// A desk that is off, for tests whose spawns must never reach a desk.
internal static class TestDesk
{
    public static DeskHold Off() => new(
        RouterConfig.FromArgs([]), NullLogger<DeskHold>.Instance, env: name => name == DeskHold.OffEnvVar ? "1" : null);
}
