using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Home and Flights pages and a node the window cannot reach (pilot #325): a flight on it is its last known row, marked
/// <c>stale (DE1 unreachable)</c> in words on the Home chip, the list row, the canvas card and the flight's page, its commands
/// dead where they were buttons and menu entries, a reachable node's flights as they were, and the marks gone when the node
/// answers again. The marks and the panel's held line read one state, the feed's reachability.
/// </summary>
public sealed class StaleFlightTests : IAsyncDisposable
{
    private const string De1Where = "10.0.0.7:47731";

    private const string Mark = "stale (DE1 unreachable)";

    private static readonly DateTimeOffset WentDown = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = WentDown.AddMinutes(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNode _se1 = new();
    private readonly FakeNode _de1 = new();
    private readonly NodeFeed _here;
    private readonly FleetWatch _watch;

    public StaleFlightTests()
    {
        _here = new NodeFeed(_se1.Open, "node.sock", TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("SE1", "s-se1", "DESKTOP-7")));
        _watch = new FleetWatch(endpoint => new NodeFeed(_de1.Open, endpoint, TimeSpan.FromMilliseconds(20),
            nodeInfo: _ => Task.FromResult(Info("DE1", "s-de1", "LAPTOP-3")), clock: () => WentDown));
    }

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

    private static FlightRow Flight(string code, FlightState state, string title, params string[] connections) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = title, Branch = "flight/" + code.ToLowerInvariant(), Worktree = "/w/" + code,
        Base = "origin/main", State = state, Connections = connections, CreatedAt = Live.At,
    };

    private static LegRow Leg(string flight, string name, int order, LegState state) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state,
    };

    private static CardRow Ready(string code) => new()
    {
        Id = "c-" + code, Bin = CardBin.Ring, Kind = "pr-ready", Subject = Verbs.FlightTarget(code), Title = $"{code}'s PR #7 is ready to merge",
        At = Now, State = CardState.Open, Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = ["abc1234"] }],
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

    private static FlightLine Of(FleetPages pages, string code) => pages.Flights.Lines.Single(l => l.Code == code);

    /// <summary>SE1 (this window's) with TIA1 flying; DE1 on the LAN with ORB2 flying (a leg flying, one planned) and ORB3 held for it.</summary>
    private async Task BothUp(NodeFilter filter)
    {
        _here.Start();
        _watch.Know([(De1Where, "DE1 LAPTOP-3")]);
        await Until(() => _se1.OpenCount > 0 && _de1.OpenCount > 0);
        _se1.Send(Live.Snapshot(1, "s-se1", Live.Row("aaaa0001", "se1-watchtower", SessionState.Working)) with
        {
            Flights = [Flight("TIA1", FlightState.Flying, "Planets on the GPU")],
            Legs = [Leg("TIA1", "render", 1, LegState.Flying)],
        });
        _de1.Send(De1Snapshot());
        await Until(() => Pages(filter).Flights.Lines.Count == 3);
    }

    private static SnapshotEvent De1Snapshot() => Live.Snapshot(1, "s-de1", Live.Row("bbbb0001", "de1-watchtower", SessionState.Working)) with
    {
        Flights = [Flight("ORB2", FlightState.Flying, "Orbit parser"), Flight("ORB3", FlightState.Held, "Orbit render", "ORB2")],
        Legs = [Leg("ORB2", "parser", 1, LegState.Flying), Leg("ORB2", "render", 2, LegState.Planned)],
    };

    /// <summary>DE1's stream drops and every try to open it again is refused, as with the node's machine off.</summary>
    private async Task De1GoesDown(NodeFilter filter)
    {
        _de1.Refuse = new HttpRequestException("no route to host");
        _de1.Close();
        await Until(() => Pages(filter).Flights.Lines.Any(l => l.Stale is not null));
    }

    private static List<string> Texts(Layout.Node tree, RgbaImageRenderer renderer, float width = 1900f, float height = 900f) =>
        [.. Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];

    private static FlightsView<RgbaImage> View(RgbaImageRenderer renderer, FlightsViewMode mode) =>
        new(renderer) { FontPath = SessionsCapture.FontPath, Mode = mode, CanCommand = true, OnAction = _ => { }, OnChat = _ => { } };

    [Fact]
    public async Task ADownNodesFlightsAreMarkedStaleWithItsNameAndTheReachableNodesAreNot()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        Pages(filter).Flights.Lines.ShouldAllBe(l => l.Stale == null && l.StaleMark == null);

        await De1GoesDown(filter);
        var pages = Pages(filter);

        foreach (var code in new[] { "ORB2", "ORB3" })
        {
            var line = Of(pages, code);
            line.Stale.ShouldBe("DE1");
            line.StaleMark.ShouldBe(Mark);
            line.ShownDetail.ShouldNotBeNull().ShouldStartWith(Mark);
            line.ToDumpLine().ShouldContain(Mark);
        }
        // SE1's flight is as it was: no mark, its detail the one it had.
        var tia = Of(pages, "TIA1");
        tia.Stale.ShouldBeNull();
        tia.ShownDetail.ShouldBe(tia.StateDetail);
        tia.ToDumpLine().ShouldNotContain("stale");
    }

    [Fact]
    public async Task TheMarkAndThePanelsHeldLineReadTheSameFeedState()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);

        var pages = Pages(filter);

        _watch.Feeds("node.sock", _here, Now).Single(f => f.Mark.Name == "DE1").Feed.Reach.Connected.ShouldBeFalse();
        pages.Cards.Held.Single().Node.Name.ShouldBe(Of(pages, "ORB2").Stale);
    }

    [Fact]
    public async Task AStaleFlightsHomeChipReadsStaleAndTheCountsLeaveItOut()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        var live = Pages(filter);
        var before = new HomeScreen(live.Flights, live.Sessions, live.Jobs, live.Cards);
        before.Flying.ShouldStartWith("2 flights flying");
        before.Waiting.ShouldContain("1 flight held");

        await De1GoesDown(filter);
        var pages = Pages(filter);
        var home = new HomeScreen(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards);

        var labels = home.FlightChips.Select(c => c.Label).ToList();
        labels.ShouldContain($"ORB2 · {Mark}");
        labels.ShouldContain($"ORB3 · {Mark}");
        // The reachable node's chip is as it was; the down node keeps its pill, so two nodes are offered and the chip names SE1.
        labels.ShouldContain("SE1 · TIA1 · flying");
        home.FlightChips.Where(c => c.Tone == "stale").Select(c => c.Flight!.Code).ShouldBe(["ORB2", "ORB3"]);
        // A last known row is not what is flying or held now.
        home.Flying.ShouldStartWith("1 flight flying");
        home.Waiting.ShouldNotContain("held");
        home.ToDump().ShouldContain($"ORB2 · {Mark}");

        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = new HomeView<RgbaImage>(renderer);
        Texts(view.Tree(home), renderer).ShouldContain($"ORB2 · {Mark}");
    }

    [Theory]
    [InlineData("List")]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    public async Task TheFlightsPageSaysStaleInItsListRowItsCanvasCardAndItsGridCard(string modeName)
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var pages = Pages(filter);
        using var renderer = new RgbaImageRenderer(1900, 900);
        var view = View(renderer, Enum.Parse<FlightsViewMode>(modeName));

        var texts = Texts(view.Tree(pages.Flights), renderer);

        // Two stale flights: each says it, in words; TIA1's own says nothing of the kind.
        texts.Count(t => t.Contains(Mark, StringComparison.Ordinal)).ShouldBe(2);
        texts.Count(t => t.Contains("stale", StringComparison.Ordinal)).ShouldBe(2);
    }

    private PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.CanCommand = true;
        chrome.Flights.OnAction = _ => { };
        chrome.Show(PilotPage.Flights);
        return chrome;
    }

    private static List<string> ButtonsOf(PilotChrome<RgbaImage> chrome, FleetPages pages, string code, CardsScreen? cards = null)
    {
        chrome.Flights.OpenDetail(code);
        chrome.Render(pages.Sessions, pages.Flights, cards ?? pages.Cards, new RectF32(0, 0, 1600, 900));
        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        return [.. regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith($"flight:{code}:", StringComparison.Ordinal))];
    }

    [Fact]
    public async Task AStaleFlightsPageSaysStaleAndItsCommandButtonsAreDeadWhereAReachableFlightsAreLive()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var pages = Pages(filter);
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        // Both flights have a leg flying, so both offer a Go around: SE1's is a button, DE1's (down) is not.
        FlightAction.For(Of(pages, "ORB2")).Select(a => a.Verb).ShouldBe([Verbs.GoAround, Verbs.RemoveLeg]);

        var stale = ButtonsOf(chrome, pages, "ORB2");
        var texts = Texts(chrome.Flights.Tree(pages.Flights), renderer, 1600f);

        // Said on the page; the commands are drawn, as dead words, and are not buttons.
        // Drawn as words that wrap, so the line is looked for in the page as cells.
        var described = CellLayout.Describe(Layout.Engine.Arrange(chrome.Flights.Tree(pages.Flights), new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored));
        described.ShouldContain("stale");
        described.ShouldContain("unreachable):");
        described.ShouldContain("answers");
        texts.ShouldContain("Redo leg parser");
        texts.ShouldContain("Remove render");
        stale.ShouldNotContain($"flight:ORB2:{Verbs.GoAround}");
        stale.ShouldNotContain($"flight:ORB2:{Verbs.RemoveLeg}");
        // The page's own buttons stay: Back is not a command to the node.
        chrome.Flights.IsDetailOpen.ShouldBeTrue();
        ButtonsOf(chrome, pages, "TIA1").ShouldContain($"flight:TIA1:{Verbs.GoAround}");
    }

    [Fact]
    public async Task AStaleFlightsReadyToMergeShowsItsMergeAsDeadAndAReachableFlightsAsAButton()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var pages = Pages(filter);
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        // The panel itself holds DE1's cards back, so the page is given the card the way a copy that was not yet held out would be.
        var cards = CardsScreen.From([Ready("ORB2"), Ready("TIA1")]);

        var stale = ButtonsOf(chrome, pages, "ORB2", cards);
        var texts = Texts(chrome.Flights.Tree(pages.Flights), renderer, 1600f);

        texts.ShouldContain("ORB2's PR #7 is ready to merge");
        texts.ShouldContain("Merge");
        stale.ShouldNotContain("flight:ORB2:merge-card");
        ButtonsOf(chrome, pages, "TIA1", cards).ShouldContain("flight:TIA1:merge-card");
    }

    [Fact]
    public async Task AStaleFlightsMenuEntriesThatGoToTheNodeAreDisabledWithWhyAndTheOthersStayLive()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var pages = Pages(filter);
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = View(renderer, FlightsViewMode.List);
        view.CopyText = _ => { };
        view.Tree(pages.Flights);
        var press = new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1);

        view.OpenMenu(Of(pages, "ORB2"), 1, press);
        var stale = view.MenuItems.ToList();

        stale.Where(i => i.Value == Verbs.GoAround || i.Value == Verbs.RemoveLeg).ShouldNotBeEmpty();
        stale.Where(i => i.Value == Verbs.GoAround || i.Value == Verbs.RemoveLeg).ShouldAllBe(i => !i.IsEnabled && i.Tooltip == "DE1 is unreachable: its commands wait until it answers");
        // Local things stay: opening the flight's page and copying its code.
        stale.Single(i => i.Value == "flight").IsEnabled.ShouldBeTrue();
        stale.Single(i => i.Value == "copy-code").IsEnabled.ShouldBeTrue();

        view.OpenMenu(Of(pages, "TIA1"), 0, press);

        view.MenuItems.Single(i => i.Value == Verbs.GoAround).IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task WhenTheNodeAnswersAgainTheMarksGoAndTheCommandsAreLive()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        Of(Pages(filter), "ORB2").Stale.ShouldBe("DE1");

        _de1.Refuse = null;
        var opens = _de1.OpenCount;
        await Until(() => _de1.OpenCount > opens);
        _de1.Send(De1Snapshot());
        await Until(() => Pages(filter).Flights.Lines.All(l => l.Stale is null));
        var pages = Pages(filter);

        pages.Flights.Lines.ShouldAllBe(l => l.StaleMark == null && !l.ToDumpLine().Contains("stale", StringComparison.Ordinal));
        var home = new HomeScreen(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards);
        home.FlightChips.Select(c => c.Label).ShouldContain("DE1 · ORB2 · flying");
        home.FlightChips.ShouldAllBe(c => c.Tone != "stale");
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        ButtonsOf(chrome, pages, "ORB2").ShouldContain($"flight:ORB2:{Verbs.GoAround}");
    }

    [Fact]
    public async Task HomeAndFlightsWithOneNodeDownAreCapturedAtANarrowAndAWideSize()
    {
        var filter = new NodeFilter();
        await BothUp(filter);
        await De1GoesDown(filter);
        var pages = Pages(filter);
        // Where the captures go, when a person is to look at them; else a folder that goes with the test.
        var given = Environment.GetEnvironmentVariable("PILOT_STALE_CAPTURES");
        var folder = given is { Length: > 0 } ? Directory.CreateDirectory(given) : Directory.CreateTempSubdirectory("pilot-stale-");
        try
        {
            foreach (var (name, width, height) in new[] { ("narrow", 760, 640), ("wide", 1600, 900) })
            {
                foreach (var (page, label, mode, open) in new (PilotPage, string, FlightsViewMode, string?)[]
                {
                    (PilotPage.Home, "home", FlightsViewMode.Canvas, null),
                    (PilotPage.Flights, "flights-canvas", FlightsViewMode.Canvas, null),
                    (PilotPage.Flights, "flights-list", FlightsViewMode.List, null),
                    (PilotPage.Flights, "flight-page", FlightsViewMode.Canvas, "ORB2"),
                })
                {
                    var png = Path.Combine(folder.FullName, $"{label}-one-node-down-{name}.png");
                    using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                    var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                    chrome.Play.ShowEverything();
                    chrome.Flights.CanCommand = true;
                    chrome.Flights.Mode = mode;
                    chrome.Show(page);
                    if (open is not null)
                    {
                        chrome.Flights.OpenDetail(open);
                    }

                    // The page of a stale flight is given the card that offered its merge, as a copy not yet held out would have it.
                    var cards = open is null ? pages.Cards : CardsScreen.From([Ready(open)]);
                    chrome.Render(pages.Sessions, pages.Flights, cards, pages.Jobs, null, null, new RectF32(0, 0, width, height));
                    File.WriteAllBytes(png, PngWriter.Encode(renderer.Surface.Pixels, width, height));

                    File.ReadAllBytes(png).Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
                }
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
