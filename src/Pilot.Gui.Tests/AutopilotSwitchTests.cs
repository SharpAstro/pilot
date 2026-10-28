using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Autopilot switch (#208): in the header of every page, lit only while the node in view says autopilot is on, a click on it
/// asks the host to flip it, the host sends <c>set autopilot on|off</c> to the node as the GUI, and the Fleet page says autopilot
/// on each node's row from the node's own answer.
/// </summary>
public sealed class AutopilotSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeSummary Summary(string stream, bool autopilot = false, bool connected = true) => new()
    {
        Connected = connected, Status = connected ? "live" : "waiting for the node on 10.0.0.9:47731; retrying every 2s", Stream = stream, LiveSessions = 3,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream, Autopilot = autopilot },
    };

    private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(Layout.Node tree) =>
        Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 260, 60), CellMeasureContext.PixelAuthored);

    /// <summary>Every page's tree, as the window paints it, from a chrome whose switch is the given one.</summary>
    private static IEnumerable<(string Page, Layout.Node Tree)> Pages(PilotChrome<RgbaImage> chrome)
    {
        var now = Live.At;
        var flights = new FlightsScreen(FlightLine.Build([new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = "t", Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
            Base = "origin/main", State = FlightState.Flying, CreatedAt = now,
        }], [], now), "test", now);
        var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), now)]), "test", now);
        var fleet = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop"))], now);
        yield return ("Home", chrome.Home.Tree(new HomeScreen(flights, sessions, new JobsScreen([], "test", now), CardsScreen.Empty)));
        yield return ("Sessions", chrome.Sessions.Tree(sessions));
        yield return ("Flights", chrome.Flights.Build(flights));
        yield return ("Background tasks", chrome.Jobs.Tree(new JobsScreen([], "test", now)));
        yield return ("Fleet", chrome.Fleet.Tree(fleet));
        yield return ("Repos", chrome.Repos.Tree(new ReposScreen([], "test", now)));
        yield return ("Pull requests", chrome.PullRequests.Tree(new PullRequestsScreen(null, "test", now)));
        yield return ("Settings", chrome.Settings.Tree(now));
        yield return ("Chat", chrome.Chat.Tree());
    }

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    private static Layout.ArrangedNode<int> SwitchIn(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        var found = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: AutopilotSwitch.Action }).ToList();
        found.Count.ShouldBe(1);
        return found[0];
    }

    /// <summary>The switch is in the header of each page, says Autopilot, and is lit when the node says it is on and not when it says off.</summary>
    [Fact]
    public void EveryPagesHeaderHasTheSwitchLitOnlyWhileAutopilotIsOn()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = NewChrome(renderer);
        chrome.Autopilot.OnToggle = _ => { };

        foreach (var on in new[] { false, true })
        {
            chrome.Autopilot.On = on;
            var pages = Pages(chrome).ToList();
            pages.Count.ShouldBe(9);
            foreach (var (page, tree) in pages)
            {
                var arranged = Arrange(tree);
                var found = SwitchIn(arranged);
                var because = $"{page}, autopilot {(on ? "on" : "off")}";

                // The header is the page's top: the switch stands in the same row as the page's first text.
                var title = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text });
                found.Bounds.Y.ShouldBeLessThanOrEqualTo(title.Bounds.Y + 1, because);

                var words = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == AutopilotSwitch.Label
                    && Inside(found.Bounds, a.Bounds)).ToList();
                words.Count.ShouldBe(1, because);
                found.Node.Background.ShouldBe(on ? AutopilotSwitch.LitBg : AutopilotSwitch.PlainBg, because);
                found.Node.Tooltip.ShouldNotBeNull().ShouldContain(on ? "Autopilot is on" : "Autopilot is off", Case.Sensitive, because);
                found.Node.Cursor.ShouldBe(CursorKind.Pointer, because);
            }
        }
    }

    private static bool Inside(Rect<int> outer, Rect<int> inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>The window paints it: the same switch is a clickable region of the painted page, whichever the page.</summary>
    [Fact]
    public void ThePaintedWindowHasTheSwitchOnTwoPages()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        chrome.Autopilot.OnToggle = _ => { };
        chrome.Autopilot.On = true;
        var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), Live.At)]), "test", Live.At);
        var fleet = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", autopilot: true))], Live.At);

        // The Fleet last: PILOT_TEST_CAPTURE keeps its frame, the switch lit in the header and the row saying autopilot on.
        foreach (var page in new[] { PilotPage.Home, PilotPage.Sessions, PilotPage.Fleet })
        {
            chrome.Show(page);
            chrome.Render(sessions, null, null, null, fleet, null, new RectF32(0, 0, 1280, 800));
            var regions = new List<ClickableRegion>();
            foreach (var widget in chrome.Widgets)
            {
                (widget as PixelWidgetBase<RgbaImage>)?.CollectPaintedRegions(regions);
            }
            regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Count(b => b.Action == AutopilotSwitch.Action).ShouldBe(1, page.ToString());
        }
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            File.WriteAllBytes(capture, SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, 1280, 800));
        }
    }

    /// <summary>A click on the switch asks the host to turn it the other way: on from off, off from on. With no host to send it, it is a mark and does not click.</summary>
    [Fact]
    public void AClickAsksForTheOtherState()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = NewChrome(renderer);
        var asked = new List<bool>();
        chrome.Autopilot.OnToggle = asked.Add;

        foreach (var on in new[] { false, true })
        {
            chrome.Autopilot.On = on;
            foreach (var (_, tree) in Pages(chrome).Where(p => p.Page is "Home" or "Fleet" or "Flights"))
            {
                var arranged = Arrange(tree);
                var found = SwitchIn(arranged);
                CellLayout.HitTest(arranged, found.Bounds.X + 1, found.Bounds.Y);
            }
        }
        asked.ShouldBe([true, true, true, false, false, false]);

        chrome.Autopilot.OnToggle = null;
        Arrange(chrome.Home.Tree(new HomeScreen(new FlightsScreen([], "test", Live.At), new SessionsScreen([], "test", Live.At), new JobsScreen([], "test", Live.At), CardsScreen.Empty)))
            .Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().ShouldNotContain(b => b.Action == AutopilotSwitch.Action);
    }

    /// <summary>A node's row says autopilot on or off from its node envelope; a node not reached says nothing it last said.</summary>
    [Fact]
    public void TheFleetRowSaysAutopilotPerNode()
    {
        var on = FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, Summary("s2", autopilot: true));
        var off = FleetLine.From("10.0.0.7:47731", here: false, canCommand: true, null, Summary("s3"));
        var gone = FleetLine.From("10.0.0.5:47731", here: false, canCommand: true, null, Summary("s4", autopilot: true, connected: false));

        on.Autopilot.ShouldBeTrue();
        on.AutopilotWord.ShouldBe("autopilot on");
        off.Autopilot.ShouldBeFalse();
        off.AutopilotWord.ShouldBe("autopilot off");
        gone.Autopilot.ShouldBeTrue();
        gone.LastKnown.ShouldBeTrue();
        gone.AutopilotWord.ShouldBe("autopilot on");
        on.ToDumpLine().ShouldEndWith(" | can command | autopilot on");

        using var renderer = new RgbaImageRenderer(1, 1);
        var fleet = NewChrome(renderer).Fleet;
        var screen = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop")), on, gone], Live.At);
        var rows = CellLayout.Describe(Arrange(fleet.Tree(screen))).Split('\n');

        // The live row and the quiet one (its last word, dimmed) both say on.
        rows.Count(l => l.Contains("autopilot on", StringComparison.Ordinal)).ShouldBe(2);
        rows.Count(l => l.Contains("autopilot off", StringComparison.Ordinal)).ShouldBe(1);
    }
}
