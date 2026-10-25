using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Fleet page: every node at once, each on its own feed and its own account, this window's first; a node heard twice
/// is one row; a node without a grant is watched and offers Connect, any other offers Open.
/// </summary>
public sealed class FleetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeSummary Summary(string stream, string? account = null, bool connected = true, int flying = 0, int rings = 0, int waiting = 0) => new()
    {
        Connected = connected, Status = connected ? "live" : "waiting for the node on 10.0.0.9:47731; retrying every 2s", Stream = stream,
        Account = account, LiveSessions = 3, WaitingOnYou = waiting, Flying = flying, Rings = rings,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream },
    };

    [Fact]
    public void ANodeReadsAsWhoseItIsAndWhatItHas()
    {
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, "SE2 DESKTOP-7", Summary("s2", "bo@example.org (Other, max)", flying: 2, rings: 1, waiting: 1));

        line.ToDumpLine().ShouldBe("SE2 · DESKTOP-7 | 10.0.0.9:47731 | live | bo@example.org (Other, max) | - | 3 live · 1 waiting on you | 2 flying | none | 1 ringing | watch only | autopilot off");
        FleetLine.From("10.0.0.9:47731", false, true, "SE2 DESKTOP-7", Summary("s2", connected: false) with { Node = null }).Machine.ShouldBe("SE2 DESKTOP-7");
    }

    [Fact]
    public async Task TheWatchKeepsAFeedPerNodeAndANodeHeardTwiceIsOneRow()
    {
        var nodes = new Dictionary<string, FakeNode>(StringComparer.OrdinalIgnoreCase) { ["10.0.0.9:47731"] = new(), ["10.0.0.5:47731"] = new() };
        await using var watch = new FleetWatch(endpoint => new NodeFeed(nodes[endpoint].Open, endpoint, TimeSpan.FromMilliseconds(20)));

        watch.Know([("10.0.0.9:47731", "SE2 DESKTOP-7"), ("10.0.0.5:47731", null)]);
        watch.Know([("10.0.0.9:47731", "SE2 DESKTOP-7")]);
        for (var i = 0; i < 500 && nodes.Values.Any(n => n.OpenCount == 0); i++)
        {
            await Task.Delay(10, Ct);
        }
        nodes["10.0.0.9:47731"].Send(Live.Snapshot(1, "desktop", Live.Row("a1b2c3d4", "x", SessionState.Waiting)) with
        {
            Usage = new UsageRow { Account = new AccountRow { Email = "bo@example.org" }, ReadAt = Live.At },
        });
        // The laptop's own node, heard on the LAN too: the same stream as this window's.
        nodes["10.0.0.5:47731"].Send(Live.Snapshot(1, "laptop"));
        var screen = watch.Screen("node.sock", Summary("laptop", "ada@example.org"), e => false, Live.At);
        for (var i = 0; i < 500 && (screen.Lines.Count != 2 || !screen.Lines[1].Connected); i++)
        {
            await Task.Delay(10, Ct);
            screen = watch.Screen("node.sock", Summary("laptop", "ada@example.org"), e => false, Live.At);
        }

        screen.Lines.Select(l => (l.Where, l.Here, l.Account)).ShouldBe([("node.sock", true, "ada@example.org"), ("10.0.0.9:47731", false, "bo@example.org")]);
        screen.Lines[1].Sessions.ShouldBe("1 live · 1 waiting on you");
        nodes["10.0.0.9:47731"].OpenCount.ShouldBe(1);
        screen.Summary.ShouldBe("2 nodes · 2 live");
    }

    [Fact]
    public void ARowOffersOpenAndConnectAsItsGrantAllows()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        var opened = new List<string>();
        var asked = new List<string>();
        fleet.OnOpen = line => opened.Add(line.Where);
        fleet.OnConnect = line => asked.Add(line.Where);
        var screen = new FleetScreen(
        [
            FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", "ada@example.org")),
            FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, null, Summary("desktop", "bo@example.org")),
            FleetLine.From("10.0.0.7:47731", here: false, canCommand: true, null, Summary("nas", "ci@example.org")),
        ], Live.At);

        var arranged = Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        var described = CellLayout.Describe(arranged);
        string[] buttons = [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith("fleet:", StringComparison.Ordinal) && !a.StartsWith("fleet:table:", StringComparison.Ordinal))];

        described.ShouldContain("bo@example.org");
        described.ShouldContain("watch only");
        buttons.ShouldBe(["fleet:10.0.0.9:47731:open", "fleet:10.0.0.9:47731:connect", "fleet:10.0.0.7:47731:open"]);
        var connect = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "fleet:10.0.0.9:47731:connect" });
        CellLayout.HitTest(arranged, connect.Bounds.X + 1, connect.Bounds.Y);
        asked.ShouldBe(["10.0.0.9:47731"]);
    }

    /// <summary>
    /// A node row's "N waiting on you" is a button that lands on the Sessions page filtered to that node, the first session
    /// waiting on you selected and in the list's visible window: also when the list had been scrolled away from it, and
    /// when other nodes have sessions waiting too.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WaitingOnYouOpensThoseSessionsOnTheSessionsPage(int waiting)
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.LinkFleet();
        var here = new NodeMark("node.sock", "node.sock", "SE1", Here: true);
        var remote = new NodeMark("10.0.0.9:47731", "10.0.0.9:47731", "SE2", Here: false);
        chrome.Nodes.Choices = [here, remote];

        // The remote node has more rows than the window holds, and this window's has one waiting on you too, which the
        // pick must leave out.
        var lines = new List<SessionLine>();
        for (var i = 0; i < 25; i++)
        {
            lines.Add(SessionLine.From(Live.Row($"bbbb{i:0000}", $"r-work-{i:00}", SessionState.Working), Live.At) with { Node = remote });
        }
        for (var i = 0; i < waiting; i++)
        {
            lines.Add(SessionLine.From(Live.Row($"cccc{i:0000}", $"r-wait-{i:00}", SessionState.Waiting), Live.At) with { Node = remote });
        }
        lines.Add(SessionLine.From(Live.Row("aaaa0000", "h-wait", SessionState.Waiting), Live.At) with { Node = here });
        lines.Add(SessionLine.From(Live.Row("aaaa0001", "h-work", SessionState.Working), Live.At) with { Node = here });
        var fleet = new FleetScreen(
        [
            FleetLine.From(here.Where, here: true, canCommand: true, null, Summary("laptop", "ada@example.org", waiting: 1)),
            FleetLine.From(remote.Where, here: false, canCommand: true, null, Summary("desktop", "bo@example.org", waiting: waiting)),
        ], Live.At);

        // As the host does each frame: the sessions the window's node selection shows, in order.
        SessionsScreen Screen() => new(SessionLine.Order(lines.Where(l => chrome.Nodes.Shows(l.Node))), "test", Live.At);
        List<ClickableRegion> Painted(IPixelWidget page)
        {
            var regions = new List<ClickableRegion>();
            page.CollectPaintedRegions(regions);
            return regions;
        }
        HashSet<int> Visible() => [.. Painted(chrome.Sessions).Select(r => r.Result).OfType<HitResult.ListItemHit>().Where(h => h.ListId == SessionsView<RgbaImage>.ListId).Select(h => h.Index)];
        void Draw() => chrome.Render(Screen(), null, null, null, fleet, new RectF32(0, 0, 1280, 800));

        // The sessions page, every node: walk the selection down the list until the waiting rows, at its head, are scrolled out.
        chrome.Show(PilotPage.Sessions);
        for (var i = 0; i < 20; i++)
        {
            Draw();
            chrome.Sessions.HandleInput(new InputEvent.KeyDown(InputKey.Down)).ShouldBeTrue();
        }
        Draw();
        Visible().ShouldNotContain(0);

        chrome.Show(PilotPage.Fleet);
        Draw();
        var regions = Painted(chrome.Fleet);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldContain($"fleet:{remote.Where}:waiting");
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == $"fleet:{remote.Where}:waiting").OnClick!.Invoke(InputModifier.None);
        Draw();

        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Nodes.Picked.ShouldBe([remote.Key]);
        var screen = Screen();
        screen.Lines.ShouldAllBe(l => l.Node == remote);
        var firstWaiting = screen.Lines.First(l => l.Row.State == SessionState.Waiting);
        chrome.Sessions.SelectedId.ShouldBe(firstWaiting.Key);
        firstWaiting.Row.Name.ShouldBe("r-wait-00");
        screen.Lines[chrome.Sessions.Selected].Key.ShouldBe(firstWaiting.Key);
        var visible = Visible();
        for (var i = 0; i < waiting; i++)
        {
            visible.ShouldContain(i);
        }
        visible.ShouldContain(chrome.Sessions.Selected);
        visible.ShouldNotContain(screen.Lines.Count - 1);
    }

    /// <summary>
    /// A node held in the grants and never reached (not connected, no hello ever) offers Forget; a reached node, this
    /// window's, or one never reached without a grant does not. Clicking it calls the host's forget with that row.
    /// </summary>
    [Fact]
    public void ANeverReachedGrantedRowOffersForgetAndNoOtherRowDoes()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        var forgotten = new List<string>();
        fleet.OnForget = line => forgotten.Add(line.Where);
        var never = Summary("quiet", connected: false) with { Node = null };
        var screen = new FleetScreen(
        [
            FleetLine.From("node.sock", here: true, canCommand: true, null, never),
            FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, Summary("desktop", "bo@example.org")),
            FleetLine.From("10.0.0.7:47731", here: false, canCommand: true, null, never),
            FleetLine.From("10.0.0.5:47731", here: false, canCommand: false, null, never),
            // Reached once, quiet now: it said hello, so it is no stale grant.
            FleetLine.From("10.0.0.3:47731", here: false, canCommand: true, null, Summary("gone", connected: false)),
        ], Live.At);

        var arranged = Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        string[] forgets = [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.EndsWith(":forget", StringComparison.Ordinal))];
        var pill = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "fleet:10.0.0.7:47731:forget" });
        CellLayout.HitTest(arranged, pill.Bounds.X + 1, pill.Bounds.Y);

        forgets.ShouldBe(["fleet:10.0.0.7:47731:forget"]);
        forgotten.ShouldBe(["10.0.0.7:47731"]);
    }

    /// <summary>Forgetting an endpoint removes its feed: the next screen has no row for it, and the others stay.</summary>
    [Fact]
    public async Task ForgettingAnEndpointDropsItsRowFromTheNextScreen()
    {
        var nodes = new Dictionary<string, FakeNode>(StringComparer.OrdinalIgnoreCase)
        {
            ["10.0.0.9:47731"] = new() { Refuse = new IOException("no node listens") },
            ["10.0.0.5:47731"] = new(),
        };
        await using var watch = new FleetWatch(endpoint => new NodeFeed(nodes[endpoint].Open, endpoint, TimeSpan.FromMilliseconds(20)));
        watch.Know([("10.0.0.9:47731", null), ("10.0.0.5:47731", null)]);
        FleetScreen Screen() => watch.Screen("node.sock", Summary("laptop", "ada@example.org"), e => true, Live.At);

        Screen().Lines.Select(l => l.Where).ShouldBe(["node.sock", "10.0.0.5:47731", "10.0.0.9:47731"]);
        Screen().Lines.Where(l => l.CanForget).Select(l => l.Where).ShouldBe(["10.0.0.5:47731", "10.0.0.9:47731"]);

        watch.Forget("10.0.0.9:47731");
        watch.Forget("10.0.0.1:47731");

        Screen().Lines.Select(l => l.Where).ShouldBe(["node.sock", "10.0.0.5:47731"]);
    }

    private static NodeSummary InMaintenance(string stream, bool connected = true) =>
        Summary(stream, "bo@example.org", connected) with { Node = Summary(stream).Node! with { Maintenance = true } };

    /// <summary>A node whose NodeInfo says maintenance wears 🔧 maintenance on its row; one not reached says nothing it last said.</summary>
    [Fact]
    public void ANodeInMaintenanceIsMarkedOnItsRow()
    {
        var marked = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, InMaintenance("s2"));

        marked.Maintenance.ShouldBeTrue();
        marked.ToDumpLine().ShouldEndWith(" | 🔧 maintenance");
        FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, Summary("s2")).Maintenance.ShouldBeFalse();
        FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, InMaintenance("s2", connected: false)).Maintenance.ShouldBeFalse();

        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        var screen = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", "ada@example.org")), marked], Live.At);
        var described = CellLayout.Describe(Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored));

        described.Split('\n').Count(l => l.Contains("🔧 maintenance", StringComparison.Ordinal)).ShouldBe(1);
    }

    /// <summary>
    /// Maintenance on / off is on the row of this window's node and of a node this user holds a grant for, and only while it
    /// answers; its label says what it does, and it hands the host the row and the way round to switch.
    /// </summary>
    [Fact]
    public void TheMaintenanceToggleIsOfferedForThisNodeAndGrantedNodesOnly()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        var sent = new List<(string Where, bool On)>();
        fleet.OnMaintenance = (line, on) => sent.Add((line.Where, on));
        var screen = new FleetScreen(
        [
            FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", "ada@example.org")),
            FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, null, Summary("desktop", "bo@example.org")),
            FleetLine.From("10.0.0.7:47731", here: false, canCommand: true, null, InMaintenance("nas")),
            FleetLine.From("10.0.0.5:47731", here: false, canCommand: true, null, Summary("quiet", connected: false)),
        ], Live.At);

        var arranged = Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 220, 40), CellMeasureContext.PixelAuthored);
        string[] toggles = [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.EndsWith(":maintenance", StringComparison.Ordinal))];
        var described = CellLayout.Describe(arranged);

        toggles.ShouldBe(["fleet:node.sock:maintenance", "fleet:10.0.0.7:47731:maintenance"]);
        described.ShouldContain("Maintenance on");
        described.ShouldContain("Maintenance off");
        foreach (var where in new[] { "node.sock", "10.0.0.7:47731" })
        {
            var pill = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == $"fleet:{where}:maintenance");
            CellLayout.HitTest(arranged, pill.Bounds.X + 1, pill.Bounds.Y);
        }
        sent.ShouldBe([("node.sock", true), ("10.0.0.7:47731", false)]);

        // The window paints the same pills; PILOT_TEST_CAPTURE keeps the frame as a PNG.
        using var renderer = new RgbaImageRenderer(1280, 400);
        var painted = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath }.Fleet;
        painted.OnMaintenance = (_, _) => { };
        painted.Render(screen, new RectF32(0, 0, 1280, 400));
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            File.WriteAllBytes(capture, PngWriter.Encode(renderer.Surface.Pixels, 1280, 400));
        }
        var regions = new List<ClickableRegion>();
        painted.CollectPaintedRegions(regions);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.EndsWith(":maintenance", StringComparison.Ordinal))
            .ShouldBe(["fleet:node.sock:maintenance", "fleet:10.0.0.7:47731:maintenance"]);

        // No host to send it: no button.
        fleet.OnMaintenance = null;
        Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 220, 40), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().ShouldNotContain(b => b.Action.EndsWith(":maintenance", StringComparison.Ordinal));
    }

    [Fact]
    public void AFleetRowWithNothingWaitingHasNoWaitingButton()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.LinkFleet();
        chrome.Show(PilotPage.Fleet);
        var fleet = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", "ada@example.org"))], Live.At);

        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, fleet, new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        chrome.Fleet.CollectPaintedRegions(regions);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().ShouldNotContain(b => b.Action.EndsWith(":waiting", StringComparison.Ordinal));
        fleet.Lines[0].Sessions.ShouldBe("3 live");
    }
}
