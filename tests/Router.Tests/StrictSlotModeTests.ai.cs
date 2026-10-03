using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Rhino.AI.Router;
using Rhino.AI.Router.Tools;

namespace Rhino.AI.Router.Tests;

// Strict slot mode (RouterConfig.Strict): a slot id resolves only to a Rhino this
// router process spawned, a slot-less call is refused, and announcements are never
// adopted. Each test builds a strict and a non-strict manager over the same store,
// so the same seeded rows show both behaviours. No real Rhino is launched or
// contacted: rows are seeded straight into the SlotStore, and the one routed call
// goes through a canned HTTP handler.
//
// Isolation follows RhinoManagerResolutionTests: a per-test RHINO_MCP_HOME holds
// the slot db and the listeners dir.
[TestFixture]
public sealed class StrictSlotModeTests
{
    // A pid Windows never hands out (far past the handle-table range), so a row
    // carrying it reads as dead and no live process can be matched by it.
    private const int NeverAllocatedPid = 2147483644;

    private string _homeDir = null!;
    private string? _previousHome;
    private string? _previousStrict;
    private SlotStore _store = null!;
    private readonly int _routerPid = Environment.ProcessId;
    private int _nextPort = 12000;
    private readonly List<TcpListener> _listeners = [];

    [SetUp]
    public void SetUp()
    {
        _homeDir = Path.Combine(Path.GetTempPath(), "rhmcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_homeDir);
        _previousHome = Environment.GetEnvironmentVariable(RouterPaths.HomeOverrideEnvVar);
        Environment.SetEnvironmentVariable(RouterPaths.HomeOverrideEnvVar, _homeDir);
        // A strict setting in the developer's environment must not leak into the
        // non-strict managers built from FromArgs([]).
        _previousStrict = Environment.GetEnvironmentVariable(RouterConfig.StrictEnvVar);
        Environment.SetEnvironmentVariable(RouterConfig.StrictEnvVar, null);

        _store = new SlotStore(NullLogger<SlotStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (TcpListener l in _listeners) { try { l.Stop(); } catch { /* best effort */ } }
        _listeners.Clear();
        _store.Dispose();
        Environment.SetEnvironmentVariable(RouterConfig.StrictEnvVar, _previousStrict);
        Environment.SetEnvironmentVariable(RouterPaths.HomeOverrideEnvVar, _previousHome);
        try { Directory.Delete(_homeDir, recursive: true); } catch { /* best effort */ }
    }

    // --- a. slot-less calls ---------------------------------------------------

    [Test]
    public async Task Strict_slotless_call_returns_slot_required()
    {
        SeedOwnReady("8"); // a default candidate exists, and must still not be used
        ProxyDispatcher dispatcher = Dispatcher(Manager(strict: true), new StubHttpClientFactory());

        JsonNode result = Parse(await dispatcher.CallToolAsync(null, "run_python", new JsonObject()));

        Assert.That(ErrorCode(result), Is.EqualTo("slot_required"));
        Assert.That(result["autoSpawnedSlot"], Is.Null);
        Assert.That(result["pid"], Is.Null);
        Assert.That(result["payload"], Is.Null);
    }

    [Test]
    public void Strict_default_resolution_throws_slot_required()
    {
        SeedOwnReady("8");
        RhinoManager manager = Manager(strict: true);

        Assert.ThrowsAsync<SlotRequiredException>(
            (Func<Task>)(() => manager.GetOrCreateDefaultAsync()));
    }

    [Test]
    public async Task Non_strict_default_resolution_still_reuses_an_own_slot()
    {
        string own = SeedOwnReady("8");

        (ChildRhino child, bool spawned) = await Manager(strict: false).GetOrCreateDefaultAsync();

        Assert.That(child.SlotId, Is.EqualTo(own));
        Assert.That(spawned, Is.False);
    }

    // --- b. foreign and adopted rows -------------------------------------------

    [Test]
    public void Strict_does_not_resolve_a_slot_owned_by_another_router()
    {
        string foreign = SeedForeignReady("8");
        RhinoManager strict = Manager(strict: true);

        Assert.That(strict.Get(foreign), Is.Null);
        Assert.That(strict.Find(foreign), Is.Null);
        Assert.That(Manager(strict: false).Get(foreign)?.SlotId, Is.EqualTo(foreign));
    }

    [Test]
    public void Strict_does_not_resolve_an_adopted_slot()
    {
        string adopted = SeedAdopted("8");
        RhinoManager strict = Manager(strict: true);

        Assert.That(strict.Get(adopted), Is.Null);
        Assert.That(strict.Find(adopted), Is.Null);
        Assert.That(Manager(strict: false).Get(adopted)?.SlotId, Is.EqualTo(adopted));
    }

    [Test]
    public void Strict_resolves_its_own_spawned_slot()
    {
        string own = SeedOwnReady("8");
        RhinoManager strict = Manager(strict: true);

        Assert.That(strict.Get(own)?.SlotId, Is.EqualTo(own));
        Assert.That(strict.Find(own)?.SlotId, Is.EqualTo(own));
    }

    [Test]
    public async Task Strict_dispatch_to_foreign_or_adopted_slot_returns_slot_not_found()
    {
        string foreign = SeedForeignReady("8");
        string adopted = SeedAdopted("8");
        CountingHandler handler = new(CannedSuccess);
        ProxyDispatcher dispatcher = Dispatcher(Manager(strict: true), new HandlerHttpClientFactory(handler));

        foreach (string slot in new[] { foreign, adopted })
        {
            JsonNode result = Parse(await dispatcher.CallToolAsync(slot, "run_python", new JsonObject()));

            Assert.That(ErrorCode(result), Is.EqualTo("slot_not_found"), slot);
            Assert.That(result["error"]!["message"]!.GetValue<string>(),
                Is.EqualTo(RhinoManager.StrictSlotNotFoundMessage(slot)));
            Assert.That(result["pid"], Is.Null);
        }
        Assert.That(handler.Calls, Is.Zero, "no request may reach a Rhino strict mode does not serve");
    }

    [Test]
    public async Task Strict_close_slot_on_foreign_or_adopted_slot_returns_slot_not_found()
    {
        string foreign = SeedForeignReady("8");
        string adopted = SeedAdopted("8");
        RhinoManager strict = Manager(strict: true);
        SpawnSlotTool tool = Tool(strict);

        foreach (string slot in new[] { foreign, adopted })
        {
            JsonNode result = Parse(await tool.CloseAsync(slot));

            Assert.That(ErrorCode(result), Is.EqualTo("slot_not_found"), slot);
            Assert.That(result["pid"], Is.Null);
            Assert.That(await strict.CloseAsync(slot), Is.False);
            Assert.That(_store.Get(slot), Is.Not.Null, "the row must be left alone");
        }
    }

    [Test]
    public async Task Strict_hidden_router_never_closes_a_windowless_adopted_slot()
    {
        // Under --hidden alone, an adopted slot with no visible window is closed.
        // Strict mode does not serve adopted slots at all, so the probe is never
        // asked and the slot stays untouched.
        string adopted = SeedAdopted("8");
        CountingProbe probe = new(WindowVisibility.Hidden);
        RhinoControlClient control = new(new StubHttpClientFactory(), NullLogger<RhinoControlClient>.Instance);
        RhinoManager strict = new(
            RouterConfig.FromArgs(["--hidden", "--strict"]), control, _store,
            NullLogger<RhinoManager>.Instance, probe);

        Assert.That(await strict.CloseAsync(adopted), Is.False);
        Assert.That(probe.Calls, Is.Zero, "strict mode must not consult the window probe");
        Assert.That(_store.Get(adopted), Is.Not.Null, "the row must be left alone");
    }

    private sealed class CountingProbe(WindowVisibility result) : IWindowProbe
    {
        public int Calls { get; private set; }
        public WindowVisibility Probe(int pid) { Calls++; return result; }
    }

    // --- c. no adoption ------------------------------------------------------------

    [Test]
    public void Strict_list_slots_does_not_adopt_an_announced_rhino()
    {
        int port = Listen();
        string annFile = Announce(port);
        SpawnSlotTool tool = Tool(Manager(strict: true));

        JsonNode result = Parse(tool.List());

        Assert.That(result["payload"]!.AsArray(), Is.Empty);
        Assert.That(_store.ListReady().Any(c => c.Adopted), Is.False);
        Assert.That(_store.ListReady().Any(c => c.Port == port), Is.False);
        Assert.That(File.Exists(annFile), Is.True, "strict mode leaves the announcement to other routers");
    }

    [Test]
    public void Non_strict_list_slots_adopts_an_announced_rhino()
    {
        int port = Listen();
        Announce(port);
        SpawnSlotTool tool = Tool(Manager(strict: false));

        JsonNode result = Parse(tool.List());

        ChildRhino? adopted = _store.ListReady().SingleOrDefault(c => c.Port == port);
        Assert.That(adopted, Is.Not.Null);
        Assert.That(adopted!.Adopted, Is.True);
        Assert.That(result["payload"]!.AsArray().Count, Is.EqualTo(1));
    }

    [Test]
    public void Strict_list_returns_only_own_spawned_ready_rows()
    {
        string own = SeedOwnReady("8");
        SeedForeignReady("8");
        SeedAdopted("9");
        _store.ReserveNewNamed("8", _routerPid); // own but still launching

        IReadOnlyCollection<ChildRhino> strict = Manager(strict: true).List();
        IReadOnlyCollection<ChildRhino> lenient = Manager(strict: false).List();

        Assert.That(strict.Select(c => c.SlotId), Is.EquivalentTo(new[] { own }));
        Assert.That(lenient.Count, Is.EqualTo(3));
    }

    [Test]
    public void Strict_list_slots_tool_lists_only_own_rows_alongside_live_foreign_ones()
    {
        // Rows backed by real listeners survive ReapAllDead inside the tool.
        string own = SeedOwnReady("8", port: Listen());
        SeedForeignReady("8", port: Listen());
        SeedAdopted("9", port: Listen());

        JsonNode result = Parse(Tool(Manager(strict: true)).List());

        JsonArray payload = result["payload"]!.AsArray();
        Assert.That(payload.Count, Is.EqualTo(1));
        Assert.That(payload[0]!.ToJsonString(), Does.Contain(own));
    }

    // --- d. pid in the envelope -----------------------------------------------------

    [Test]
    public async Task Routed_call_success_carries_the_slot_pid()
    {
        const int seededPid = 424242;
        string own = SeedOwnReady("8", pid: seededPid);
        CountingHandler handler = new(CannedSuccess);
        ProxyDispatcher dispatcher = Dispatcher(Manager(strict: true), new HandlerHttpClientFactory(handler));

        JsonNode result = Parse(await dispatcher.CallToolAsync(own, "run_python", new JsonObject()));

        Assert.That(result["error"], Is.Null, result.ToJsonString());
        Assert.That(result["pid"]!.GetValue<int>(), Is.EqualTo(seededPid));
        Assert.That(result["payload"]!["ok"]!.GetValue<bool>(), Is.True);
        Assert.That(handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Close_slot_success_carries_the_closed_slot_pid()
    {
        // The row's pid is never allocated and its port is closed, so CloseAsync's
        // quit request is refused, the exit wait returns at once, and the kill path
        // is never reached. No live process is touched.
        Assume.That(IsRunning(NeverAllocatedPid), Is.False);
        string own = SeedOwnReady("8", pid: NeverAllocatedPid, port: ClosedPort());

        JsonNode result = Parse(await Tool(Manager(strict: true)).CloseAsync(own));

        Assert.That(result["error"], Is.Null, result.ToJsonString());
        Assert.That(result["payload"]!["closed"]!.GetValue<bool>(), Is.True);
        Assert.That(result["pid"]!.GetValue<int>(), Is.EqualTo(NeverAllocatedPid));
        Assert.That(_store.Get(own), Is.Null);
    }

    [Test]
    public void Return_result_serializes_pid_at_top_level()
    {
        // spawn_slot sets Pid from the spawned child; SpawnAsync launches Rhino, so
        // this pins the envelope field it relies on instead.
        string json = new ReturnResult(JsonValue.Create("ok"), Pid: 1234).AsJson;
        JsonNode node = Parse(json);

        Assert.That(node["pid"]!.GetValue<int>(), Is.EqualTo(1234));
        Assert.That(new ReturnResult(JsonValue.Create("ok")).AsJson, Does.Not.Contain("\"pid\""));
    }

    // --- helpers -------------------------------------------------------------------------

    private const string CannedSuccess =
        "{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"ok\\\":true}\"}]}}";

    private RhinoManager Manager(bool strict)
    {
        RhinoControlClient control = new(new StubHttpClientFactory(), NullLogger<RhinoControlClient>.Instance);
        return new RhinoManager(
            RouterConfig.FromArgs(strict ? ["--strict"] : []), control, _store, NullLogger<RhinoManager>.Instance);
    }

    private static ProxyDispatcher Dispatcher(RhinoManager manager, IHttpClientFactory factory) =>
        new(manager, factory,
            new RhinoCrashReportFinder(NullLogger<RhinoCrashReportFinder>.Instance),
            NullLogger<ProxyDispatcher>.Instance);

    private static SpawnSlotTool Tool(RhinoManager manager) =>
        new(manager, new RhinoCrashReportFinder(NullLogger<RhinoCrashReportFinder>.Instance));

    private string SeedOwnReady(string version, int? pid = null, int? port = null)
    {
        (_, string id) = _store.ReserveNewNamed(version, _routerPid);
        _store.MarkReady(id, port: port ?? _nextPort++, pid: pid ?? _routerPid);
        return id;
    }

    private string SeedForeignReady(string version, int? port = null)
    {
        (_, string id) = _store.ReserveNewNamed(version, _routerPid + 1);
        _store.MarkReady(id, port: port ?? _nextPort++, pid: _routerPid);
        return id;
    }

    private string SeedAdopted(string version, int? port = null)
    {
        string? id = _store.AdoptIfNew(version, port ?? _nextPort++, pid: _routerPid, routerPid: _routerPid);
        Assert.That(id, Is.Not.Null);
        return id!;
    }

    private int Listen()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        _listeners.Add(l);
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static int ClosedPort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private string Announce(int port)
    {
        Directory.CreateDirectory(RouterPaths.ListenersDir);
        string file = Path.Combine(RouterPaths.ListenersDir, $"{_routerPid}-{port}.json");
        Announcement ann = new(V: 1, Pid: _routerPid, Port: port, Version: "8");
        File.WriteAllText(file, JsonSerializer.Serialize(ann, RouterJsonContext.Default.Announcement));
        return file;
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static string? ErrorCode(JsonNode result) => result["error"]?["code"]?.GetValue<string>();

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
