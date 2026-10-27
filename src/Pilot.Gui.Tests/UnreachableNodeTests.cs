using System.Globalization;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The watchtower panel and a node the window cannot reach (pilot #325): its cards leave the panel, one dim line per such node
/// says how many it held and since when, the counts read the nodes that answer, a click on the line opens that node's Fleet
/// row, and the node's cards come back from what it reports when it answers again.
/// </summary>
public sealed class UnreachableNodeTests : IAsyncDisposable
{
    private const string De1Where = "10.0.0.7:47731";

    private static readonly DateTimeOffset WentDown = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    // The panel is merged twenty minutes after DE1 went down, the same day wherever the reader is.
    private static readonly DateTimeOffset Now = WentDown.AddMinutes(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNode _se1 = new();
    private readonly FakeNode _de1 = new();
    private readonly NodeFeed _here;
    private readonly FleetWatch _watch;

    public UnreachableNodeTests()
    {
        _here = new NodeFeed(_se1.Open, "node.sock", TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("SE1", "s-se1", "DESKTOP-7")));
        // DE1's feed stamps the moment it goes down from a clock of the test's own.
        _watch = new FleetWatch(endpoint => new NodeFeed(_de1.Open, endpoint, TimeSpan.FromMilliseconds(20),
            nodeInfo: _ => Task.FromResult(Info(DeName, "s-de1", "LAPTOP-3")), clock: () => WentDown));
    }

    /// <summary>The prefix DE1 says it has: a test of a long name changes it before the feed is first asked.</summary>
    private string DeName { get; set; } = "DE1";

    public async ValueTask DisposeAsync()
    {
        await _watch.DisposeAsync();
        await _here.DisposeAsync();
    }

    private static NodeInfo Info(string prefix, string stream, string machine) => new()
    {
        WireVersion = 1, Version = "2.0.9", MachineName = machine, MachinePrefix = prefix, ProcessId = 1, StartedAt = Live.At,
        DataFolder = "/d", SocketPath = "/s", Stream = stream,
    };

    private static CardRow Card(string id, string title, CardBin bin = CardBin.ForYou) => new()
    {
        Id = id, Bin = bin, Kind = "landed", Subject = "flight/X", Title = title, At = Live.At, State = CardState.Open,
    };

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }

    private FleetPages Pages(NodeFilter filter) => _watch.Pages("node.sock", _here, filter, Now);

    /// <summary>SE1 (this window's) with one card for you; DE1 on the LAN with 3 ringing and 2 for you, both live.</summary>
    private async Task BothUp(NodeFilter filter)
    {
        _here.Start();
        _watch.Know([(De1Where, "DE1 LAPTOP-3")]);
        await Until(() => _se1.OpenCount > 0 && _de1.OpenCount > 0);
        _se1.Send(Live.Snapshot(1, "s-se1", Live.Row("aaaa0001", "se1-watchtower", SessionState.Working)) with { Cards = [Card("s1", "TIA1 landed")] });
        _de1.Send(Live.Snapshot(1, "s-de1", Live.Row("bbbb0001", "de1-watchtower", SessionState.Working)) with
        {
            Cards =
            [
                Card("d1", "ORB1 stalled", CardBin.Ring), Card("d2", "ORB2 stalled", CardBin.Ring), Card("d3", "ORB3 stalled", CardBin.Ring),
                Card("d4", "ORB4 landed"), Card("d5", "ORB5 landed"),
            ],
        });
        await Until(() => Pages(filter).Cards.Open.Count == 6);
    }

    /// <summary>DE1's stream drops and every try to open it again is refused, as with the node's machine off.</summary>
    private async Task De1GoesDown(NodeFilter filter)
    {
        _de1.Refuse = new HttpRequestException("no route to host");
        _de1.Close();
        await Until(() => Pages(filter).Cards.Held.Count == 1);
        // A few refused tries later it is still the one moment it went down.
        var opens = _de1.OpenCount;
        await Until(() => _de1.OpenCount > opens + 1);
    }

    private static string Sum(CardsScreen cards) => cards.Summary;

    [Fact]
    public async Task ADownNodesCardsLeaveThePanelAndOneLineSaysWhatItHeldAndSince()
    {
        var filter = new NodeFilter();
        await BothUp(filter);

        Pages(filter).Cards.Held.ShouldBeEmpty();
        Sum(Pages(filter).Cards).ShouldBe("3 ringing · 3 for you");

        await De1GoesDown(filter);
        var cards = Pages(filter).Cards;

        // No DE1 card left among the open ones; SE1's stays.
        cards.Open.Select(c => c.Title).ShouldBe(["TIA1 landed"]);
        cards.Open.Any(c => cards.NodeOf(c)?.Name == "DE1").ShouldBeFalse();
        // One held line for DE1, with its last-known counts and when it went down.
        var held = cards.Held.ShouldHaveSingleItem();
        held.Node.Name.ShouldBe("DE1");
        held.Node.Where.ShouldBe(De1Where);
        held.Rings.ShouldBe(3);
        held.ForYou.ShouldBe(2);
        held.Since.ShouldBe(WentDown);
        held.Line.ShouldBe($"DE1 is unreachable since {WentDown.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} · 3 ringing, 2 for you held");
        held.Cut.ShouldBeFalse();
        cards.HasAny.ShouldBeTrue();
    }

    [Fact]
    public async Task ADownNodeThatWasHeardKeepsItsPillDimAndLabelledLastHeard()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        Pages(filter);

        filter.Choices.Select(c => c.Name).ShouldBe(["SE1", "DE1"]);
        filter.Down.Keys.ShouldBe([De1Where]);
        var stamp = WentDown.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        filter.Line().ShouldBe($"[All nodes] SE1 DE1 · last heard {stamp}");
        NodeFilter.Label(filter.Choices[1], WentDown, true).ShouldBe($"DE1 · last heard {stamp}");
        filter.Pills().Count().ShouldBe(3);

        // Drawn dim: the down pill's text is the dim colour, the live ones' is not.
        foreach (var night in new FlightsPalette?[] { null, FlightsPalette.Night })
        {
            var pills = filter.Pills(night).Select(p => ((Layout.Content.Text)((Layout.Node.Leaf)p).Content)).ToArray();
            var dim = pills[2].Color;
            pills[2].Value.ShouldContain("last heard");
            if (night is not null)
            {
                dim.ShouldBe(night.Dim);
            }
            pills[0].Color.ShouldNotBe(dim);
            pills[1].Color.ShouldNotBe(dim);
            pills[1].Value.ShouldNotContain("last heard");
        }
    }

    [Fact]
    public async Task ANodeNeverHeardIsNotOfferedAsAPill()
    {
        var filter = new NodeFilter();
        _de1.Refuse = new HttpRequestException("no route to host");
        _here.Start();
        _watch.Know([(De1Where, "DE1 LAPTOP-3")]);
        await Until(() => _se1.OpenCount > 0);
        _se1.Send(Live.Snapshot(1, "s-se1", Live.Row("aaaa0001", "se1-watchtower", SessionState.Working)));
        await Until(() => Pages(filter).Cards.Held.Count == 1);

        filter.Choices.Select(c => c.Name).ShouldBe(["SE1"]);
        filter.Down.ShouldBeEmpty();
        filter.Pills().ShouldBeEmpty();
    }

    [Fact]
    public async Task ANodeBackLiveDropsTheLastHeardLabel()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        Pages(filter);
        filter.Down.Count.ShouldBe(1);

        _de1.Refuse = null;
        var opens = _de1.OpenCount;
        await Until(() => _de1.OpenCount > opens);
        _de1.Send(Live.Snapshot(1, "s-de1", Live.Row("bbbb0001", "de1-watchtower", SessionState.Working)));
        await Until(() => Pages(filter).Cards.Held.Count == 0);

        filter.Down.ShouldBeEmpty();
        filter.Line().ShouldBe("[All nodes] SE1 DE1");
    }

    [Fact]
    public async Task TheSummaryAndHomesWaitingCountTheNodesThatAnswerOnly()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);

        var pages = Pages(filter);

        // DE1's 3 rings and 2 for you are not in them: only SE1's one card for you.
        pages.Cards.Summary.ShouldBe("1 for you");
        new HomeScreen(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards).Waiting.ShouldBe("1 for you");
    }

    [Fact]
    public async Task ADownNodeWithNothingHeldIsStillNamedAndSaysNothingAboutCounts()
    {
        var filter = new NodeFilter();
        // DE1 never answers: its first open is refused, which is its first try failing. Set before DE1 is known, else the
        // watch's first open could come first and succeed, and DE1 would never be unreachable (failed once on CI, #340).
        _de1.Refuse = new HttpRequestException("no route to host");
        _here.Start();
        _watch.Know([(De1Where, "DE1 LAPTOP-3")]);
        await Until(() => _se1.OpenCount > 0);
        _se1.Send(Live.Snapshot(1, "s-se1", Live.Row("aaaa0001", "se1-watchtower", SessionState.Working)));
        await Until(() => Pages(filter).Cards.Held.Count == 1);

        var held = Pages(filter).Cards.Held.Single();

        held.Rings.ShouldBe(0);
        held.ForYou.ShouldBe(0);
        held.Counts.ShouldBeEmpty();
        held.Line.ShouldBe("DE1 LAPTOP-3 is unreachable since " + held.When);
        Pages(filter).Cards.Summary.ShouldBe("Nothing waits on you");
    }

    [Fact]
    public async Task WhenTheNodeAnswersAgainItsCardsComeBackFromTheNewSnapshotNotTheStaleCopy()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        Pages(filter).Cards.Held.ShouldHaveSingleItem();

        // DE1 is back, and says what it holds now: one ring that is new, and none of the old five.
        _de1.Refuse = null;
        var opens = _de1.OpenCount;
        await Until(() => _de1.OpenCount > opens);
        _de1.Send(Live.Snapshot(1, "s-de1", Live.Row("bbbb0001", "de1-watchtower", SessionState.Working)) with { Cards = [Card("d9", "ORB9 stalled", CardBin.Ring)] });
        await Until(() => Pages(filter).Cards.Held.Count == 0);

        var cards = Pages(filter).Cards;

        cards.Held.ShouldBeEmpty();
        cards.Open.Select(c => c.Title).ShouldBe(["ORB9 stalled", "TIA1 landed"]);
        cards.NodeOf(cards.Open[0])!.Name.ShouldBe("DE1");
        cards.Summary.ShouldBe("1 ringing · 1 for you");
        // Up again, and so down again later: the new time, not the old one.
        _watch.Feeds("node.sock", _here, Now).Select(f => f.Feed.Reach).ShouldAllBe(r => r.Connected && r.DownSince == null);
    }

    [Fact]
    public async Task TheFleetPagesStateIsTheSameStateThePanelReads()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);

        var summary = _watch.Feeds("node.sock", _here, Now).Single(f => f.Mark.Name == "DE1").Feed.Summary(Now);

        summary.Connected.ShouldBeFalse();
        summary.DownSince.ShouldBe(WentDown);
        Pages(filter).Cards.Held.Single().Since.ShouldBe(summary.DownSince!.Value);
    }

    [Fact]
    public void AHeldLineNamesTheDayWhenTheNodeWentDownBeforeToday()
    {
        var node = new NodeMark(De1Where, De1Where, "DE1", Here: false);

        new HeldNode(node, WentDown, 1, 0, WentDown.AddMinutes(5)).When.ShouldBe(WentDown.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        var later = new HeldNode(node, WentDown, 1, 0, WentDown.AddDays(2));
        later.When.ShouldBe(WentDown.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture));
        later.Line.ShouldBe($"DE1 is unreachable since {later.When} · 1 ringing held");
    }

    private static CardsView<RgbaImage> NewView(out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1280, 800);
        return new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanTell = true };
    }

    [Fact]
    public async Task AClickOnTheHeldLineAsksForTheFleetPageAtThatNode()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var cards = Pages(filter).Cards;
        var view = NewView(out var renderer);
        using var _ = renderer;
        NodeMark? asked = null;
        view.OnFleet = node => asked = node;

        var arranged = Layout.Engine.Arrange(view.Tree(cards), new Rect<int>(0, 0, 38, 60), CellMeasureContext.PixelAuthored);
        var line = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("cards:node:", StringComparison.Ordinal));
        CellLayout.HitTest(arranged, line.Bounds.X + 1, line.Bounds.Y);

        asked.ShouldBe(cards.Held.Single().Node);
        // Drawn as words that wrap, so the line is looked for word by word.
        CellLayout.Describe(arranged).ShouldContain("unreachable");
    }

    [Fact]
    public async Task TheHeldLinesClickInTheWindowShowsTheFleetPageWithThatNodesRowSelected()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var pages = Pages(filter);
        var de1 = pages.Cards.Held.Single().Node;
        var here = _watch.Feeds("node.sock", _here, Now)[0].Mark;
        var fleet = _watch.Screen("node.sock", _here.Summary(Now), _ => true, Now);
        fleet.Lines.Select(l => l.Where).ShouldBe(["node.sock", De1Where]);

        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Nodes.Choices = [here];
        chrome.LinkFleet();
        chrome.Show(PilotPage.Sessions);
        void Draw() => chrome.Render(pages.Sessions, pages.Flights, pages.Cards, null, fleet, new RectF32(0, 0, 1280, 800));
        Draw();

        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == $"cards:node:{de1.Key}").OnClick!.Invoke(InputModifier.None);
        Draw();

        chrome.Page.ShouldBe(PilotPage.Fleet);
        chrome.Fleet.Selected.ShouldBe(1);
    }

    [Fact]
    public async Task ALongNodeNameIsCutWithTheWholeLineOnHoverAndTheCountsStayInSight()
    {
        DeName = "DE1-WORKSTATION-IN-THE-BASEMENT-OF-BUILDING-SEVEN";
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);

        var held = Pages(filter).Cards.Held.Single();

        held.Cut.ShouldBeTrue();
        held.Line.ShouldContain("DE1-WORKSTATION-IN-THE-BASEMENT-OF-BUILDING-SEVEN is unreachable");
        held.Shown.ShouldContain("…");
        held.Shown.Length.ShouldBeLessThanOrEqualTo(HeldNode.LineFits);
        held.Shown.ShouldEndWith("3 ringing, 2 for you held");

        var view = NewView(out var renderer);
        using var _ = renderer;
        var arranged = Layout.Engine.Arrange(view.Tree(Pages(filter).Cards), new Rect<int>(0, 0, 38, 60), CellMeasureContext.PixelAuthored);
        var line = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("cards:node:", StringComparison.Ordinal));
        // The whole line is the hover text, never only the cut one.
        line.Node.Tooltip.ShouldNotBeNull().ShouldContain(held.Line);
    }

    [Fact]
    public async Task ThePanelWithOneNodeDownIsCapturedAtANarrowAndAWideSize()
    {
        DeName = "DE1-WORKSTATION-IN-THE-BASEMENT-OF-BUILDING-SEVEN";
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var pages = Pages(filter);
        // Where the captures go, when a person is to look at them; else a folder that goes with the test.
        var given = Environment.GetEnvironmentVariable("PILOT_UNREACHABLE_CAPTURES");
        var folder = given is { Length: > 0 } ? Directory.CreateDirectory(given) : Directory.CreateTempSubdirectory("pilot-unreachable-");
        try
        {
            foreach (var (name, width, height) in new[] { ("narrow", 760, 640), ("wide", 1600, 900) })
            {
                var png = Path.Combine(folder.FullName, $"panel-one-node-down-{name}.png");

                SessionsCapture.Write(pages.Sessions, png, width, height, cards: pages.Cards);

                File.ReadAllBytes(png).Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
            }
        }
        finally
        {
            if (given is not { Length: > 0 })
            {
                folder.Delete(recursive: true);
            }
        }
    }
}
