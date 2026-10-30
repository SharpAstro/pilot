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

    /// <summary>Each node's last-hour traffic is a line under its machine, drawn in full and clear of the line above, narrow and wide.</summary>
    [Theory]
    [InlineData(1280)]
    [InlineData(700)]
    public void EachNodesLastHourTrafficIsShownAndNeverOverlaps(int width)
    {
        var row = new NodeTrafficRow { Hour = Live.At, HttpIn = 3_000_000, HttpOut = 1_000_000, ChildBytes = 9_000_000, GitRuns = 4, GhRuns = 20 };
        var withTraffic = Summary("s2", "bo@example.org") with { Node = Summary("s2").Node! with { Traffic = row } };
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, withTraffic, Live.At);
        var without = FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("s1", "ada@example.org"), Live.At);

        line.Traffic.ShouldBe("node http 3.0/1.0 MB/h (in/out) · git+gh up to 9.0 MB/h (est.)");
        without.Traffic.ShouldBeNull();
        FleetLine.TrafficWords(row with { ChildBytes = null }).ShouldEndWith("git+gh unknown");
        using var renderer = new RgbaImageRenderer((uint)width, 400);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Fleet);

        var arranged = Layout.Engine.Arrange(chrome.Fleet.Tree(new FleetScreen([line, without], Live.At)), new Rect<float>(0, 0, width, 400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var leaves = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ToArray();
        string TextOf(dynamic a) => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value;
        var previousBottom = leaves.Where(a => TextOf(a).StartsWith("10.0.0.9:47731", StringComparison.Ordinal)).ShouldHaveSingleItem() is var where ? where.Bounds.Y + where.Bounds.Height : 0f;
        line.TrafficLines.Length.ShouldBe(2);
        foreach (var part in line.TrafficLines)
        {
            var leaf = leaves.Where(a => TextOf(a) == part).ShouldHaveSingleItem();
            leaf.Bounds.Y.ShouldBeGreaterThanOrEqualTo(previousBottom - 0.5f, part);
            leaf.Bounds.Width.ShouldBeGreaterThanOrEqualTo(renderer.MeasureText(part.AsSpan(), SessionsCapture.FontPath, 11f).Width - 0.5f, part);
            previousBottom = leaf.Bounds.Y + leaf.Bounds.Height;
        }
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
        FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, InMaintenance("s2", connected: false) with { Node = null }).Maintenance.ShouldBeFalse();

        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        var screen = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", "ada@example.org")), marked], Live.At);
        var described = CellLayout.Describe(Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored));

        described.Split('\n').Count(l => l.Contains("maintenance", StringComparison.Ordinal)).ShouldBe(1);
    }

    /// <summary>A node in maintenance that goes unreachable keeps its mark, dated, its values dimmed and its status line says when it was last heard.</summary>
    [Fact]
    public void AnUnreachableNodeKeepsItsLastKnownRowDimmedAndDated()
    {
        var heard = new DateTimeOffset(2026, 10, 28, 12, 5, 0, TimeSpan.Zero);
        var hhmm = heard.ToLocalTime().ToString("HH:mm");
        var gone = InMaintenance("s2", connected: false) with { DownSince = heard, RetryEvery = TimeSpan.FromSeconds(2), LiveSessions = 3, WaitingOnYou = 1, Flying = 2, Rings = 1 };
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, gone);

        line.LastKnown.ShouldBeTrue();
        line.Connected.ShouldBeFalse();
        line.Maintenance.ShouldBeTrue();
        line.AsOf.ShouldBe($"as of {hhmm}");
        line.State.ShouldBe($"last heard {hhmm} (restarting or unreachable; retrying every 2 s)");
        (line.Account, line.Sessions, line.Flights, line.Rings).ShouldBe(("bo@example.org", "3 live · 1 waiting on you", "2 flying", 1));
        line.WaitingOnYou.ShouldBe(0);
        line.CanSetMaintenance.ShouldBeFalse();
        line.ToDumpLine().ShouldContain($" | {FleetLine.MaintenanceMark} as of {hhmm}");

        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Fleet);
        var arranged = Layout.Engine.Arrange(chrome.Fleet.Tree(new FleetScreen([line], Live.At)), new Rect<int>(0, 0, 1280, 400), CellMeasureContext.PixelAuthored);
        bool Has(string text) => arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == text);
        Has($"{FleetLine.MaintenanceWord} as of {hhmm}").ShouldBeTrue();
        line.StateLines.ShouldBe([$"last heard {hhmm}", "(restarting or unreachable; retrying every 2 s)"]);
        foreach (var part in line.StateLines)
        {
            Has(part).ShouldBeTrue();
        }
        Has("2 flying").ShouldBeTrue();
        arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().ShouldNotContain(b => b.Action.EndsWith(":maintenance", StringComparison.Ordinal));
    }

    /// <summary>The header counts only the rings of rows that answer now; a last-known row still shows its own, dimmed.</summary>
    [Fact]
    public void TheHeaderDoesNotCountALastKnownRowsRings()
    {
        var live = FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", "ada@example.org", rings: 1));
        var gone = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null,
            Summary("s2", "bo@example.org", connected: false, rings: 1) with { DownSince = new DateTimeOffset(2026, 10, 28, 12, 5, 0, TimeSpan.Zero) });

        gone.LastKnown.ShouldBeTrue();
        gone.Rings.ShouldBe(1);
        new FleetScreen([live, gone], Live.At).Summary.ShouldBe("2 nodes · 1 live · 1 ringing");
    }

    /// <summary>A node never heard has nothing last said: dashes, and a status that says why.</summary>
    [Fact]
    public void ANodeNeverHeardShowsDashesAndWhy()
    {
        var never = Summary("quiet", connected: false) with { Node = null };
        var waiting = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, never);
        var refused = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, never with { Status = "no node on 10.0.0.9:47731: refused; retrying every 2s" });

        waiting.LastKnown.ShouldBeFalse();
        (waiting.Account, waiting.Sessions, waiting.Flights, waiting.Jobs, waiting.Rings).ShouldBe(("—", "—", "—", "—", 0));
        waiting.State.ShouldStartWith("no answer yet");
        refused.State.ShouldContain("refused");
        waiting.Maintenance.ShouldBeFalse();
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
        described.ShouldContain("Put into maintenance");
        described.ShouldContain("Take out of maintenance");
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

    /// <summary>
    /// Frames of the whole fleet story (PILOT_TEST_CAPTURE_DIR names the folder; without it nothing is written): a live node, a live
    /// node in maintenance with autopilot and usage, the same node unreachable, and a node never heard; at 900 and 1800 wide.
    /// The Fleet page has the one palette; the node pills are also framed on Flights, in its day and its night palette.
    /// </summary>
    [Theory]
    [InlineData(900)]
    [InlineData(1800)]
    public void TheFleetStoryIsCapturedInBothPalettes(int width)
    {
        var heard = new DateTimeOffset(2026, 10, 28, 5, 34, 0, TimeSpan.Zero);
        var live = new NodeMark("node.sock", "node.sock", "SE1", Here: true);
        var busy = new NodeMark("10.0.0.7:47731", "10.0.0.7:47731", "DE1", Here: false);
        var gone = new NodeMark("10.0.0.9:47731", "10.0.0.9:47731", "DE2", Here: false);
        var quiet = new NodeMark("10.0.0.5:47731", "10.0.0.5:47731", "LE1", Here: false);
        var inMaintenance = InMaintenance("de1", connected: true) with { Usage = "5 h 42% · 7 d 18%", Flying = 1, Rings = 1 };
        inMaintenance = inMaintenance with { Node = inMaintenance.Node! with { Autopilot = true, MachinePrefix = "DE1", MachineName = "NAS-1" } };
        var down = InMaintenance("de2", connected: false) with { DownSince = heard, RetryEvery = TimeSpan.FromSeconds(2), Usage = "5 h 42% · 7 d 18%", Flying = 1, Rings = 1 };
        down = down with { Node = down.Node! with { Autopilot = true, MachinePrefix = "DE2", MachineName = "LAPTOP-3" } };
        var never = Summary("le1", connected: false) with { Node = null, Status = "no node on 10.0.0.5:47731: refused; retrying every 2s" };
        var screen = new FleetScreen(
        [
            FleetLine.From(live.Where, here: true, canCommand: true, null, Summary("se1", "ada@example.org", flying: 2) with { Node = Summary("se1").Node! with { MachinePrefix = "SE1", MachineName = "DESKTOP-7" } }),
            FleetLine.From(busy.Where, here: false, canCommand: true, null, inMaintenance, heard.AddMinutes(30)),
            FleetLine.From(gone.Where, here: false, canCommand: true, null, down, heard.AddMinutes(30)),
            FleetLine.From(quiet.Where, here: false, canCommand: true, null, never, heard.AddMinutes(30)),
        ], heard.AddMinutes(30));

        FleetLine.From(gone.Where, here: false, canCommand: true, null, down).Maintenance.ShouldBeTrue();
        var capture = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is { Length: > 0 } dir ? dir : null;
        using var renderer = new RgbaImageRenderer((uint)width, 760);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.LinkFleet();
        chrome.Nodes.Choices = [live, busy, gone];
        chrome.Nodes.Down = new Dictionary<string, DateTimeOffset?> { [gone.Key] = heard };
        var sessions = new SessionsScreen([], "test", heard);
        FlightLine Flight(string code, int issue, string title, NodeMark node, string? stale = null) =>
            FlightLine.From(new FlightRow { Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = issue, Title = title, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = heard }, [], heard.AddMinutes(30))
                with { Node = node, Stale = stale };
        // One flight from each heard node, so the Flights frames show every node's pill and rows; the never-heard LE1 has none.
        var flights = new FlightsScreen(FlightLine.Order([Flight("TIA1611", 1611, "Planets on the GPU", live), Flight("TIA1612", 1612, "Mount model", busy), Flight("TIA1613", 1613, "Guider calibration", gone, "DE2")]), "test", heard);

        chrome.Show(PilotPage.Fleet);
        chrome.Render(sessions, flights, null, null, screen, new RectF32(0, 0, width, 760));
        void Keep(string name)
        {
            if (capture is not null)
            {
                Directory.CreateDirectory(capture);
                File.WriteAllBytes(Path.Combine(capture, $"{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 760));
            }
        }
        Keep("fleet-day");
        foreach (var (name, night) in new[] { ("flights-day", false), ("flights-night", true) })
        {
            chrome.Flights.Mode = FlightsViewMode.Grid;
            chrome.Night.On = night;
            chrome.Show(PilotPage.Flights);
            chrome.Render(sessions, flights, null, null, screen, new RectF32(0, 0, width, 760));
            Keep(name);
        }
    }

    private static NodeSummary WithWatchtower(WatchtowerHealth? health, bool connected = true) =>
        Summary("s2", connected: connected) with { Node = Summary("s2").Node! with { Watchtower = health } };

    private static WatchtowerHealth Calm => new()
    {
        State = SessionState.Idle, Id = "3a1b2c3d4e5f", StartedAt = Live.At.AddHours(-2), ContextTokens = 41_000, ContextWindow = 200_000, StartsLast24h = 3,
    };

    [Fact]
    public void ACalmWatchtowerReadsInWordsAndIsNotMarked()
    {
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, WithWatchtower(Calm), Live.At);

        line.Watchtower.ShouldBe("watchtower idle · 3a1b2c3d · 2 h · 41k/200k · 3 started in 24 h");
        line.WatchtowerMark.ShouldBeNull();
        line.WatchtowerSummary.ShouldBeNull();
        line.ToDumpLine().ShouldEndWith(" | watchtower idle · 3a1b2c3d · 2 h · 41k/200k · 3 started in 24 h");
        new FleetScreen([line], Live.At).Summary.ShouldBe("1 node · 1 live");
    }

    [Fact]
    public void AChurningWatchtowerIsMarkedWithItsCountLifetimeAndReason()
    {
        var churning = Calm with { Churning = true, StartsLast24h = 9, TypicalLifetimeAfterReply = TimeSpan.FromMinutes(4), LastEndedBecause = "process gone",
            Summary = "12 watchtowers in the last hour; each ended about 2 minutes after its reply" };
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, WithWatchtower(churning), Live.At);
        const string summary = "12 watchtowers in the last hour; each ended about 2 minutes after its reply";
        line.WatchtowerSummary.ShouldBe(summary);
        line.ToDumpLine().ShouldEndWith(" | " + summary);

        line.WatchtowerChurning.ShouldBeTrue();
        line.WatchtowerMark.ShouldBe("⚠ watchtower churning (9 in 24 h)");
        line.Watchtower!.ShouldContain("typically ends 4 min after its last reply");
        line.Watchtower!.ShouldContain("last ended: process gone");
        line.ToDumpLine().ShouldContain("⚠ watchtower churning (9 in 24 h)");
        new FleetScreen([line], Live.At).Summary.ShouldBe("1 node · 1 live · 1 with a churning watchtower");

        // Drawn: the mark is on the row, at a narrow width too.
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Fleet);
        foreach (var width in new[] { 1280, 700 })
        {
            Layout.Engine.Arrange(chrome.Fleet.Tree(new FleetScreen([line], Live.At)), new Rect<int>(0, 0, width, 400), CellMeasureContext.PixelAuthored)
                .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == line.WatchtowerMark).ShouldNotBeEmpty();
            Layout.Engine.Arrange(chrome.Fleet.Tree(new FleetScreen([line], Live.At)), new Rect<int>(0, 0, width, 400), CellMeasureContext.PixelAuthored)
                .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == summary).ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void ANodeNotReachedSaysNothingOfItsWatchtower()
    {
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, WithWatchtower(Calm with { Churning = true }, connected: false), Live.At);

        line.Watchtower.ShouldBe("—");
        line.WatchtowerChurning.ShouldBeFalse();
        line.WatchtowerMark.ShouldBeNull();
    }

    [Fact]
    public void TheWatchtowerCardsFleetLinkOpensTheFleetPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer));
        chrome.Page.ShouldBe(PilotPage.Home);

        chrome.Cards.OnGoTo.ShouldNotBeNull();
        chrome.Cards.OnGoTo(InAppLinks.Fleet);

        chrome.Page.ShouldBe(PilotPage.Fleet);
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

    [Fact]
    public void ANodeRowNamesTheJobHoldingItsQueueOnlyWhileAJobWaitsAndTheLegWaitingOnAFile()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        var queued = Summary("laptop", "ada@example.org") with
        {
            JobHolder = "j14 holds the queue (xy1-orb204-parser), held for 11m 0s",
            LegWait = FlightWaiting.WaitText(new LegWait { Path = @"C:\x\done.txt", Since = Live.At }),
        };
        string Described(NodeSummary summary) => CellLayout.Describe(Layout.Engine.Arrange(
            fleet.Tree(new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, summary)], Live.At)), new Rect<int>(0, 0, 220, 40), CellMeasureContext.PixelAuthored));

        Described(queued).ShouldContain("j14 holds the queue (xy1-orb204-parser), held for 11m 0s");
        Described(queued).ShouldContain(@"waiting for C:\x\done.txt");
        Described(Summary("laptop", "ada@example.org")).ShouldNotContain("holds the queue");
    }

    /// <summary>An unreachable node's status line (last heard, retrying) reads in full: its leaf is as wide as the text, at a narrow and a wide window.</summary>
    [Theory]
    [InlineData(900)]
    [InlineData(1800)]
    public void ALastKnownRowsStatusLineIsNotClipped(int width)
    {
        var down = InMaintenance("de2", connected: false) with { DownSince = new DateTimeOffset(2026, 10, 28, 5, 34, 0, TimeSpan.Zero), RetryEvery = TimeSpan.FromSeconds(2), Usage = "5 h 42% · 7 d 18%" };
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, down, Live.At);
        var never = FleetLine.From("10.0.0.5:47731", here: false, canCommand: true, null,
            Summary("le1", connected: false) with { Node = null, Status = "no node on 10.0.0.5:47731: refused; retrying every 2s" }, Live.At);
        using var renderer = new RgbaImageRenderer((uint)width, 400);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Fleet);

        var arranged = Layout.Engine.Arrange(chrome.Fleet.Tree(new FleetScreen([line, never], Live.At)), new Rect<float>(0, 0, width, 400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        foreach (var text in (string[])[.. line.StateLines, line.Usage, never.State])
        {
            var leaf = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == text).ShouldHaveSingleItem();
            var measured = renderer.MeasureText(text.AsSpan(), SessionsCapture.FontPath, 11f).Width;
            leaf.Bounds.Width.ShouldBeGreaterThanOrEqualTo(measured - 0.5f, text);
        }
    }

    /// <summary>The marks under a row's account (maintenance, churning watchtower, autopilot, usage) each end a gap before the next starts, live and last known, narrow and wide.</summary>
    [Theory]
    [InlineData(false, 1280)]
    [InlineData(false, 700)]
    [InlineData(true, 1280)]
    [InlineData(true, 700)]
    public void TheMarksUnderTheAccountNeverOverlap(bool lastKnown, int width)
    {
        var churning = Calm with { Churning = true, StartsLast24h = 9, Summary = "12 watchtowers in the last hour" };
        var summary = WithWatchtower(churning, connected: !lastKnown) with
        {
            Usage = "5h 41% · 7d 12%",
            DownSince = lastKnown ? new DateTimeOffset(2026, 10, 28, 12, 5, 0, TimeSpan.Zero) : null,
            Node = Summary("s2").Node! with { Maintenance = true, Autopilot = true, Watchtower = churning },
        };
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, summary, Live.At);
        using var renderer = new RgbaImageRenderer((uint)width, 400);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Fleet);

        var arranged = Layout.Engine.Arrange(chrome.Fleet.Tree(new FleetScreen([line], Live.At)), new Rect<float>(0, 0, width, 400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var leaves = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ToArray();
        string TextOf(dynamic a) => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value;
        var icon = leaves.Where(a => TextOf(a) == FleetLine.MaintenanceIcon).ShouldHaveSingleItem();
        var word = leaves.Where(a => TextOf(a).StartsWith(FleetLine.MaintenanceWord, StringComparison.Ordinal)).ShouldHaveSingleItem();
        var marks = new List<(string Name, float Left, float Right)> { ("maintenance", icon.Bounds.X, word.Bounds.X + word.Bounds.Width) };
        foreach (var (name, text) in new[] { ("watchtower", line.WatchtowerMark), ("autopilot", line.AutopilotWord), ("usage", line.Usage) })
        {
            if (!string.IsNullOrEmpty(text))
            {
                var leaf = leaves.Where(a => TextOf(a) == text).ShouldHaveSingleItem();
                // A last known row's usage has a line of its own under the marks, so it is not in the gap row.
                if (name != "usage" || !lastKnown)
                {
                    marks.Add((name, leaf.Bounds.X, leaf.Bounds.X + leaf.Bounds.Width));
                }
                else
                {
                    leaf.Bounds.Y.ShouldBeGreaterThan(icon.Bounds.Y);
                    leaf.Bounds.Width.ShouldBeGreaterThanOrEqualTo(renderer.MeasureText(text.AsSpan(), SessionsCapture.FontPath, 11f).Width - 0.5f);
                }
            }
        }

        marks.Count.ShouldBeGreaterThanOrEqualTo(lastKnown ? 2 : 3);
        if (!lastKnown)
        {
            marks.ShouldContain(m => m.Name == "usage");
        }
        if (lastKnown)
        {
            // The status keeps a line of its own under the marks, so it is not squeezed out by them.
            foreach (var part in line.StateLines)
            {
                leaves.Where(a => TextOf(a) == part).ShouldHaveSingleItem().Bounds.Y.ShouldBeGreaterThan(icon.Bounds.Y);
            }
        }
        icon.Bounds.Width.ShouldBeGreaterThanOrEqualTo(FleetView<RgbaImage>.MarkIconWidth);
        marks.Sort((a, b) => a.Left.CompareTo(b.Left));
        for (var i = 1; i < marks.Count; i++)
        {
            (marks[i].Left - marks[i - 1].Right).ShouldBeGreaterThanOrEqualTo(FleetView<RgbaImage>.MarkGap - 0.01f, $"{marks[i - 1].Name} then {marks[i].Name}");
        }
    }

    private static FleetScreen SortScreen()
    {
        FleetLine Node(string where, string prefix, string usage, int live, int flying, int rings)
        {
            var summary = Summary(where, "ada@example.org", flying: flying, rings: rings) with { LiveSessions = live, Usage = usage };
            return FleetLine.From(where, here: where == "a.sock", canCommand: true, null, summary with { Node = summary.Node! with { MachinePrefix = prefix } });
        }

        return new FleetScreen(
        [
            Node("a.sock", "SE2", "5h 20%", live: 3, flying: 1, rings: 0),
            Node("b.sock", "SE1", "5h 80%", live: 12, flying: 0, rings: 2),
            Node("c.sock", "SE3", "5h 5%", live: 7, flying: 4, rings: 1),
        ], Live.At);
    }

    private static void ClickTitle(FleetView<RgbaImage> fleet, FleetScreen screen, string title)
    {
        var bounds = new RectF32(0f, 0f, 1400f, 800f);
        fleet.Render(screen, bounds);
        var regions = new List<ClickableRegion>();
        fleet.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == "fleet:table:" + title).OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        fleet.Render(screen, bounds);
    }

    [Fact]
    public void AClickOnATitleSortsTheNodesByItsValueAndASecondFlipsIt()
    {
        using var renderer = new RgbaImageRenderer(1400, 800);
        var fleet = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath }.Fleet;
        var screen = SortScreen();

        ClickTitle(fleet, screen, "Node");
        fleet.Shown.ShouldBe(["b.sock", "a.sock", "c.sock"]);
        ClickTitle(fleet, screen, "Node");
        fleet.Shown.ShouldBe(["c.sock", "a.sock", "b.sock"]);

        // Sessions by number (12 above 7 above 3, where the words "12 live" would sort before "3 live"), biggest first.
        ClickTitle(fleet, screen, "Sessions");
        fleet.Shown.ShouldBe(["b.sock", "c.sock", "a.sock"]);

        ClickTitle(fleet, screen, "Account and usage");
        fleet.Shown.ShouldBe(["b.sock", "a.sock", "c.sock"]);
        ClickTitle(fleet, screen, "Account and usage");
        fleet.Shown.ShouldBe(["c.sock", "a.sock", "b.sock"]);

        ClickTitle(fleet, screen, "Rings");
        fleet.Shown.ShouldBe(["b.sock", "c.sock", "a.sock"]);
        fleet.Table.Sort.ShouldBe(new ListTable.SortState("Rings", Descending: true));
    }

    [Fact]
    public void TheSelectedNodeStaysSelectedThroughASort()
    {
        using var renderer = new RgbaImageRenderer(1400, 800);
        var fleet = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath }.Fleet;
        var screen = SortScreen();
        fleet.SelectNode("a.sock");
        fleet.Render(screen, new RectF32(0f, 0f, 1400f, 800f));
        fleet.Selected.ShouldBe(0);

        ClickTitle(fleet, screen, "Node");

        fleet.Shown[fleet.Selected].ShouldBe("a.sock");
        fleet.Selected.ShouldBe(1);
    }
}
