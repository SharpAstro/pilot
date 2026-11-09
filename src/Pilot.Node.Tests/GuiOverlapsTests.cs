using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>pilot gui overlaps</c> against a fake inspector (a loopback TCP server speaking the inspector's newline JSON-RPC, registered in a temporary
/// registry folder as the registry contract says) and a node in memory: every page at every size, one card with its screenshot per fault, none
/// again while it is open, none on a clean run, one "no frame" card on a window that never answers, and the fallbacks. No test waits on wall time:
/// the one that never gets an answer gives the command a budget of a few hundred milliseconds.
/// </summary>
public sealed class GuiOverlapsTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _registry = TestTemp.NewPath();
    private readonly List<FakeInspector> _fakes = [];

    public GuiOverlapsTests() => Directory.CreateDirectory(_registry);

    public async ValueTask DisposeAsync()
    {
        foreach (var fake in _fakes)
        {
            await fake.DisposeAsync();
        }
        TestTemp.Delete(_registry);
    }

    private FakeInspector Fake(Action<FakeInspector>? setup = null)
    {
        var fake = new FakeInspector(_registry);
        setup?.Invoke(fake);
        fake.Start();
        _fakes.Add(fake);
        return fake;
    }

    private static async Task<(int Exit, string Output, string Error)> OverlapsAsync(NodeHarness node, string registry, params string[] more)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        // A generous budget unless the test gives its own: nothing here is expected to wait on it.
        string[] budget = more.Contains("--timeout") ? [] : ["--timeout", "5"];
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "gui", "overlaps", "--registry", registry, .. budget, .. more], Path.GetTempPath(), output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    private static readonly string[] Pages = ["home", "flights", "settings"];

    [Fact]
    public async Task EveryPageIsVisitedAt500And700And1400WideAndAShortHeightAndTheWindowIsPutBack()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var gui = Fake(f => f.Pages = Pages);

        var (exit, output, error) = await OverlapsAsync(node, _registry);

        exit.ShouldBe(0, output + error);
        foreach (var page in Pages)
        {
            foreach (var width in new[] { 500, 700, 1400 })
            {
                gui.Visits.ShouldContain((page, width, 900), $"{page} at {width} wide");
            }
            gui.Visits.Any(v => v.Page == page && v.Height < 900).ShouldBeTrue($"{page} at a short height");
        }
        node.State.Cards().ShouldBeEmpty();
        // As it was found: maximised, and on the page it showed.
        (gui.Maximized, gui.Page).ShouldBe((true, "home"));
    }

    [Fact]
    public async Task APageThatReportsOneOverlapRaisesExactlyOneCardWithItsScreenshotAttached()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Fake(f => { f.Pages = Pages; f.FaultOn = (page, w, h) => page == "settings" && (w, h) == (700, 900); });

        var (exit, output, error) = await OverlapsAsync(node, _registry);

        exit.ShouldBe(0, output + error);
        var card = node.State.Cards().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe("node");
        card.Title.ShouldBe("Text overlap on the settings page at 700x900");
        card.Detail.ShouldNotBeNull().ShouldContain("Alpha heading");
        card.Detail.ShouldContain("Beta caption");
        card.AttachmentExt.ShouldBe("png");
        var (status, type, bytes) = await node.Client.GetAttachmentAsync(card.Attachment.ShouldNotBeNull(), Ct);
        (status, type).ShouldBe((HttpStatusCode.OK, "image/png"));
        bytes.Take(8).ToArray().ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    }

    [Fact]
    public async Task ASecondRunWithThatCardStillOpenRaisesNone()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Fake(f => { f.Pages = Pages; f.FaultOn = (page, w, h) => page == "settings" && (w, h) == (700, 900); });
        (await OverlapsAsync(node, _registry)).Exit.ShouldBe(0);
        node.State.Cards().Count(c => c.State == CardState.Open).ShouldBe(1);

        var (exit, output, error) = await OverlapsAsync(node, _registry);

        exit.ShouldBe(0, output + error);
        node.State.Cards().Count().ShouldBe(1);
        output.ShouldContain("not raised again");
    }

    [Fact]
    public async Task ACleanRunRaisesNoCardAndSaysSo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Fake(f => f.Pages = Pages);

        var (exit, output, error) = await OverlapsAsync(node, _registry);

        exit.ShouldBe(0, output + error);
        node.State.Cards().ShouldBeEmpty();
        output.ShouldContain("0 faults found");
    }

    [Fact]
    public async Task AScreenshotThatNeverAnswersEndsTheRunWithinItsBudgetWithOneCardSayingTheWindowPresentedNoFrame()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var gui = Fake(f => { f.Pages = Pages; f.ScreenshotNeverAnswers = true; f.FaultOn = (_, _, _) => true; });

        var started = TimeProvider.System.GetTimestamp();
        var (exit, output, error) = await OverlapsAsync(node, _registry, "--timeout", "0.3");
        var took = TimeProvider.System.GetElapsedTime(started);

        exit.ShouldBe(PilotCommand.ExitTimedOut, output + error);
        took.ShouldBeLessThan(TimeSpan.FromSeconds(4), "the run ends within its budget, not the node's or the inspector's");
        var card = node.State.Cards().ShouldHaveSingleItem();
        card.Title.ShouldBe(GuiOverlaps.NoFrameTitle);
        card.Title.ShouldContain("presented no frame");
        card.Title.ShouldContain("is the display off?");
        card.Attachment.ShouldBeNull();
        // It stopped at the first screenshot: the one describe that found the GUI is the only read, and no page was visited at a size.
        gui.Visits.ShouldBe([("home", 1280, 800)]);
        error.ShouldContain("presented no frame");
    }

    [Fact]
    public async Task WithNoGuiRunningTheCommandSaysSoAndRaisesNothing()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var (exit, output, error) = await OverlapsAsync(node, _registry);

        exit.ShouldBe(1, output + error);
        error.ShouldContain("no running 'pilot v2' GUI");
        node.State.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task SeveralGuisAndNoneNamedIsRefusedWithTheirList()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var first = Fake(f => f.Pages = Pages);
        var second = Fake(f => f.Pages = Pages);

        var (exit, output, error) = await OverlapsAsync(node, _registry);

        // Both fakes live in this test process, so they share its pid: the list names each by its port.
        exit.ShouldBe(2, output + error);
        error.ShouldContain(first.Port.ToString());
        error.ShouldContain(second.Port.ToString());
        error.ShouldContain("--instance");
        first.Visits.ShouldBeEmpty();
        second.Visits.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInspectorWithoutTheResizeVerbIsCheckedAtItsCurrentSizeOnlyAndTheOutputNamesIssue154()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var gui = Fake(f => { f.Pages = Pages; f.SupportsResize = false; f.FaultOn = (page, _, _) => page == "flights"; });

        var (exit, output, error) = await OverlapsAsync(node, _registry);

        exit.ShouldBe(0, output + error);
        output.ShouldContain("SdlVulkan.Renderer#154");
        gui.Visits.Select(v => (v.Width, v.Height)).Distinct().ShouldBe([(1280, 800)]);
        gui.Visits.Select(v => v.Page).Distinct().ShouldBe(Pages);
        var card = node.State.Cards().ShouldHaveSingleItem();
        card.Title.ShouldBe("Text overlap on the flights page at 1280x800");
        card.Attachment.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ADryRunListsTheFaultsAndRaisesNoCardAndTakesNoScreenshot()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var gui = Fake(f => { f.Pages = Pages; f.FaultOn = (page, w, _) => page == "home" && w == 500; });

        var (exit, output, error) = await OverlapsAsync(node, _registry, "--dry-run");

        exit.ShouldBe(0, output + error);
        output.ShouldContain("Alpha heading");
        output.ShouldContain("dry run");
        node.State.Cards().ShouldBeEmpty();
        gui.Screenshots.ShouldBe(0);
    }

    /// <summary>
    /// The inspector of a Debug pilot GUI, as far as the sweep uses it: <c>describe</c> (the page it shows, its pages, window size and faults in
    /// <c>appState</c>), <c>postSignal</c> of <c>page</c>, <c>resize</c>, <c>maximize</c> and <c>screenshot</c> (rgba+gzip). One request a connection.
    /// </summary>
    private sealed class FakeInspector : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _registry;
        private readonly object _gate = new();
        private Task _accepting = Task.CompletedTask;
        private string? _descriptor;

        public FakeInspector(string registry) => _registry = registry;

        public string[] Pages { get; set; } = [];

        public bool SupportsResize { get; set; } = true;

        public bool ScreenshotNeverAnswers { get; set; }

        /// <summary>Whether the page has a fault at that window size; the fault is always the same two texts.</summary>
        public Func<string, int, int, bool> FaultOn { get; set; } = (_, _, _) => false;

        public string Page { get; private set; } = "home";

        public int Width { get; private set; } = 1280;

        public int Height { get; private set; } = 800;

        public bool Maximized { get; private set; } = true;

        public int Screenshots { get; private set; }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Each page and window size a <c>describe</c> read, as the sweep's visit to it.</summary>
        public List<(string Page, int Width, int Height)> Visits { get; } = [];

        public void Start()
        {
            _listener.Start();
            // Registered as the contract says: <pid>-<tcpPort>.json, one line, the app, the port, the pid; no processStart, so the pid alone decides.
            _descriptor = Path.Combine(_registry, $"{Environment.ProcessId}-{Port}.json");
            File.WriteAllText(_descriptor, $"{{\"app\":\"pilot v2\",\"kind\":\"sdl\",\"tcpPort\":{Port},\"pid\":{Environment.ProcessId},\"proto\":1,\"title\":\"Pilot\"}}", new UTF8Encoding(false));
            _accepting = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null)
                    {
                        return;
                    }
                    using var request = JsonDocument.Parse(line);
                    var method = request.RootElement.GetProperty("method").GetString()!;
                    var args = request.RootElement.TryGetProperty("params", out var p) ? p.Clone() : default;
                    if (method == "screenshot" && ScreenshotNeverAnswers)
                    {
                        // The window presents no frame: the request is taken and never answered.
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                    var reply = Answer(method, args);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            }
        }

        private string Answer(string method, JsonElement args)
        {
            lock (_gate)
            {
                switch (method)
                {
                    case "describe":
                        Visits.Add((Page, Width, Height));
                        return Result(Describe());
                    case "postSignal":
                        if (args.GetProperty("name").GetString() != "page")
                        {
                            return Error("no signal of that name");
                        }
                        var wanted = args.GetProperty("args").GetProperty("page").GetString()!;
                        if (!Pages.Contains(wanted))
                        {
                            return Error($"no page called '{wanted}'. Pages: {string.Join(", ", Pages)}");
                        }
                        Page = wanted;
                        return Result("\"ok\"");
                    case "resize" when SupportsResize:
                        (Width, Height, Maximized) = (args.GetProperty("width").GetInt32(), args.GetProperty("height").GetInt32(), false);
                        return Result("\"ok\"");
                    case "maximize":
                        (Width, Height, Maximized) = (1280, 800, true);
                        return Result("\"ok\"");
                    case "screenshot":
                        Screenshots++;
                        return Result(Screenshot());
                    default:
                        return Error($"unknown method '{method}'");
                }
            }
        }

        private string Describe()
        {
            var faults = FaultOn(Page, Width, Height)
                ? """[{"kind":"overlap","line":"1","text":"Alpha heading","bounds":{"x":10,"y":20,"w":100,"h":16},"other":"Beta caption","otherBounds":{"x":60,"y":24,"w":90,"h":14}}]"""
                : "[]";
            var count = faults == "[]" ? 0 : 1;
            var state = $$"""
                "page":{{JsonSerializer.Serialize(Page)}},"pages":{{JsonSerializer.Serialize(string.Join(", ", Pages))}},"window":"{{Width}}x{{Height}}","maximized":{{(Maximized ? "true" : "false")}},"pageSize":"{{Width - 300}}x{{Height}}","dpi":1,"faultCount":{{count}},"faults":{{JsonSerializer.Serialize(faults)}}
                """;
            return "{\"regions\":[],\"appState\":{" + state + "}}";
        }

        private static string Screenshot()
        {
            const int w = 8, h = 4;
            var rgba = new byte[w * h * 4];
            Array.Fill(rgba, (byte)200);
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(rgba);
            }
            return $$"""{"width":{{w}},"height":{{h}},"format":"rgba+gzip","base64":"{{Convert.ToBase64String(packed.ToArray())}}"}""";
        }

        private static string Result(string json) => "{\"id\":1,\"result\":" + json + "}";

        private static string Error(string said) => "{\"id\":1,\"error\":" + JsonSerializer.Serialize(said) + "}";

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;
            if (_descriptor is not null)
            {
                File.Delete(_descriptor);
            }
            _stop.Dispose();
        }
    }
}
