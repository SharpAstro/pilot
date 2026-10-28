using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Summary card (PIL221): Needs you and Where it is as the node kept them, at the top of Home and on a flight's page, with
/// when it was made and what it used, an empty state, and a Summarise button that says what a press costs, sends
/// <c>summarise</c> and shows it running until the node answers. PILOT_TEST_CAPTURE keeps the frames.
/// </summary>
public sealed class SummaryCardTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(3);
    private static readonly NodeMark Here = new("a", "a", "SE1", true);
    private static readonly NodeMark Other = new("b", "b", "LE1", false);

    private const string Kept = "## Needs you\n"
        + "- Card c12: PR #7 waits for your merge.\n"
        + "- TIA1611's leg `render` failed its check, and its worker stopped.\n\n"
        + "## Where it is\n"
        + "TIA1611 landed its parser leg; the **render** leg is flying and has ticked 2 of 3 steps. TIA9 is held for TIA1611.";

    private static SummaryRow Row(string scope, string markdown = Kept, long tokens = 8213, double? cost = 0.0041, int minutes = 12) =>
        new() { Scope = scope, Markdown = markdown, MadeAt = Now.AddMinutes(-minutes), Tokens = tokens, Cost = cost };

    private static SummaryShelf Shelf(NodeMark[] marks, params (NodeMark Node, SummaryRow Row)[] rows) =>
        new(marks, rows.ToDictionary(r => (r.Node.Key, r.Row.Scope), r => r.Row));

    private static FlightLine Line(string code = "TIA1611", NodeMark? node = null) =>
        FlightLine.Build([new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = $"flight/{code}",
            Worktree = "/r/tianwen/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        }], [], Now, []).Single() with { Node = node };

    // Summarise as the node reads it: its target and the words after it. A request's Args is an array, which a record compares by reference.
    private static void Says(CommandRequest request, string target, string[] args)
    {
        request.Verb.ShouldBe(Verbs.Summarise);
        request.Target.ShouldBe(target);
        request.Args.ShouldBe(args);
        request.Who.ShouldBe("gui");
    }

    private static SessionsScreen NoSessions => new([], "test", Now);

    private static HomeScreen Home() => new(new FlightsScreen([], "test", Now), NoSessions, new JobsScreen([], "test", Now), CardsScreen.Empty);

    /// <summary>A window with its summaries wired to a node that answers when the test says.</summary>
    private sealed class Window : IDisposable
    {
        private readonly TaskCompletionSource<string?> _answer = new();

        public Window(uint width = 1280, uint height = 800)
        {
            Width = width;
            Height = height;
            Renderer = new RgbaImageRenderer(width, height);
            Chrome = new PilotChrome<RgbaImage>(Renderer, new SessionsView<RgbaImage>(Renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Summaries.Send = ask =>
            {
                Sent.Add(ask);
                return _answer.Task;
            };
            Chrome.Summaries.Track = (run, _) => Tracked = run();
        }

        public uint Width { get; }

        public uint Height { get; }

        public RgbaImageRenderer Renderer { get; }

        public PilotChrome<RgbaImage> Chrome { get; }

        public List<SummaryAsk> Sent { get; } = [];

        public Task? Tracked { get; private set; }

        /// <summary>The node answers: null is a summary made, else why not.</summary>
        public async Task AnswerAsync(string? refused)
        {
            _answer.SetResult(refused);
            await Tracked!;
        }

        public void Draw(FlightsScreen? flights = null) => Chrome.Render(NoSessions, flights, CardsScreen.Empty, new RectF32(0, 0, Width, Height));

        /// <summary>Every word the tree draws, in order, as one string.</summary>
        public string Words(Layout.Node tree) => string.Concat(Arrange(tree).Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value));

        public ImmutableArray<Layout.ArrangedNode<float>> Arrange(Layout.Node tree) =>
            Layout.Engine.Arrange(tree, new Rect<float>(0, 0, Width, Height), new PixelMeasureContext<RgbaImage>(Renderer, SessionsCapture.FontPath, 1f));

        public ClickableRegion Button(IPixelWidget page)
        {
            var regions = new List<ClickableRegion>();
            page.CollectPaintedRegions(regions);
            return regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("summary:", StringComparison.Ordinal));
        }

        public bool HasButton(IPixelWidget page)
        {
            var regions = new List<ClickableRegion>();
            page.CollectPaintedRegions(regions);
            return regions.Any(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("summary:", StringComparison.Ordinal));
        }

        public void Dispose() => Renderer.Dispose();
    }

    [Fact]
    public void HomeShowsTheSummaryFirstWithNeedsYouThenWhereItIs()
    {
        using var window = new Window(1280, 2400);
        window.Chrome.Summaries.Shelf = Shelf([Here], (Here, Row(SummaryRow.FleetScope)));

        var words = window.Words(window.Chrome.Home.Tree(Home()));

        var order = new[] { "Summary", "Needs you", "PR #7 waits for your merge", "Where it is", "the render leg is flying", "Nothing is flying", "Flights" }
            .Select(w => words.IndexOf(w, StringComparison.Ordinal)).ToArray();
        order.ShouldAllBe(i => i >= 0);
        order.ShouldBe(order.Order());
        // The kept Markdown is drawn as Markdown, not as its source.
        words.ShouldNotContain("## ");
        words.ShouldNotContain("**");
    }

    [Fact]
    public void ItSaysWhenItWasMadeAndWhatItUsed()
    {
        using var window = new Window(1280, 2400);
        window.Chrome.Summaries.Shelf = Shelf([Here], (Here, Row(SummaryRow.FleetScope)));

        window.Words(window.Chrome.Home.Tree(Home())).ShouldContain("made 12m ago · 8.21k tokens · $0.0041");
        SummaryCard.Used(Row("*", cost: null, tokens: 8213)).ShouldBe("8.21k tokens");
        SummaryCard.Used(Row("*", tokens: 0, cost: null)).ShouldBe("no call made");
    }

    [Fact]
    public void WithNoSummaryYetItSaysSoAndOffersTheFirstOne()
    {
        using var window = new Window(1280, 2400);

        var words = window.Words(window.Chrome.Home.Tree(Home()));

        words.ShouldContain("none yet");
        words.ShouldContain("Press Summarise");
        words.ShouldContain("Summarise · one Haiku call");
        words.ShouldNotContain("Where it is");
    }

    [Theory]
    [InlineData(8213L, 0.0041, " · ~$0.0041")]
    [InlineData(8213L, 0.1234, " · ~$0.12")]
    [InlineData(8213L, null, " · ~8.21k tokens")]
    [InlineData(0L, null, "")]
    public void TheButtonSaysWhatThePressCostFromTheLastRun(long tokens, double? cost, string label)
    {
        SummaryCard.CostLabel(Row("*", tokens: tokens, cost: cost)).ShouldBe(label);
        SummaryCard.CostLabel(null).ShouldBe(" · one Haiku call");
    }

    [Fact]
    public async Task APressSendsSummariseAndTheCardRunsUntilTheNodeAnswersThenShowsWhatItKept()
    {
        using var window = new Window();
        var board = window.Chrome.Summaries;
        board.Shelf = Shelf([Here]);
        window.Draw();

        var button = window.Button(window.Chrome.Home);
        button.IsDisabled.ShouldBeFalse();
        button.OnClick!.Invoke(InputModifier.None);

        // Out: the card says so, the button is disabled and says what it is doing, and a second press sends nothing.
        window.Sent.Count.ShouldBe(1);
        Says(window.Sent[0].Request("gui"), Verbs.NodeTarget, []);
        window.Draw();
        window.HasButton(window.Chrome.Home).ShouldBeFalse();
        window.Words(window.Chrome.Home.Tree(Home())).ShouldContain("Summarising…");
        window.Words(window.Chrome.Home.Tree(Home())).ShouldContain("the node is making the first one");
        // Nothing to press while it runs, and a press that did reach the board (a key, a stale region) sends nothing either.
        board.Press(window.Sent[0]);
        window.Sent.Count.ShouldBe(1);

        // The node answers; what the card shows is what the node kept (the feed's row), and not before.
        await window.AnswerAsync(null);
        window.Draw();
        window.Button(window.Chrome.Home).IsDisabled.ShouldBeFalse();
        window.Words(window.Chrome.Home.Tree(Home())).ShouldContain("none yet");
        board.Shelf = Shelf([Here], (Here, Row(SummaryRow.FleetScope)));
        window.Words(window.Chrome.Home.Tree(Home())).ShouldContain("Needs you");
    }

    [Fact]
    public async Task ARefusalIsSaidOnTheCardWithWhatToDoAndTheButtonIsLiveAgain()
    {
        using var window = new Window(1280, 2400);
        window.Chrome.Summaries.Shelf = Shelf([Here], (Here, Row(SummaryRow.FleetScope)));
        window.Draw();
        window.Button(window.Chrome.Home).OnClick!.Invoke(InputModifier.None);
        // While it runs the old summary stays, and says it does.
        window.Words(window.Chrome.Home.Tree(Home())).ShouldContain("this one stays until it is done");

        await window.AnswerAsync("The fleet is being summarised already");

        var words = window.Words(window.Chrome.Home.Tree(Home()));
        words.ShouldContain("The node did not make one: The fleet is being summarised already. Press Summarise to try again.");
        words.ShouldContain("Needs you");
        window.Draw();
        window.Button(window.Chrome.Home).IsDisabled.ShouldBeFalse();
    }

    [Fact]
    public void WhatAPressAsksFollowsTheNodesTheWindowShows()
    {
        using var window = new Window();
        var shelf = Shelf([Here, Other]);
        window.Chrome.Nodes.Choices = shelf.Marks;

        // Every node shown: this window's node gathers the fleet.
        var all = shelf.Fleet(window.Chrome.Nodes);
        (all.Scope, all.Node).ShouldBe((SummaryRow.AllNodesScope, Here));
        Says(all.Request("gui"), Verbs.NodeTarget, [SummaryRow.FleetArgument]);

        // One node picked: that node's flights, asked of that node.
        window.Chrome.Nodes.Pick(Other.Key, InputModifier.None);
        var one = shelf.Fleet(window.Chrome.Nodes);
        (one.Scope, one.Node, one.About).ShouldBe((SummaryRow.FleetScope, Other, "the flights in play on LE1"));
        Says(one.Request("gui"), Verbs.NodeTarget, []);

        // A flight: its own scope, on its own node.
        var flight = shelf.Flight("TIA1611", Other);
        Says(flight.Request("gui"), "flight/TIA1611", []);
        shelf.Flight("TIA1611", null).Node.ShouldBe(Here);
        // The one node there is is not named.
        Shelf([Here]).Fleet(null).About.ShouldBe("the flights in play");
    }

    [Fact]
    public void HomeReadsTheRowOfTheNodeItAsksAndNotAnotherNodesOrAnotherScope()
    {
        using var window = new Window(1280, 2400);
        var board = window.Chrome.Summaries;
        board.Shelf = Shelf([Here, Other],
            (Here, Row(SummaryRow.FleetScope, "## Needs you\nNothing.\n\n## Where it is\nSE1-only-words.")),
            (Other, Row(SummaryRow.FleetScope, "## Needs you\nNothing.\n\n## Where it is\nLE1-only-words.")),
            (Here, Row(SummaryRow.AllNodesScope, "## Needs you\nNothing.\n\n## Where it is\nfleet-wide-words.")));
        window.Chrome.Nodes.Choices = board.Shelf.Marks;

        window.Words(window.Chrome.Home.Tree(Home())).ShouldContain("fleet-wide-words");
        window.Chrome.Nodes.Pick(Other.Key, InputModifier.None);
        var words = window.Words(window.Chrome.Home.Tree(Home()));
        words.ShouldContain("LE1-only-words");
        words.ShouldNotContain("SE1-only-words");
        words.ShouldNotContain("fleet-wide-words");
    }

    [Fact]
    public async Task AFlightsPageShowsItsOwnSummaryAndItsButtonAsksForThatFlightOnItsNode()
    {
        using var window = new Window(1280, 2400);
        var line = Line(node: Other);
        var screen = new FlightsScreen([line], "test", Now);
        window.Chrome.Summaries.Shelf = Shelf([Here, Other],
            (Other, Row("TIA1611", "## Needs you\nNothing.\n\n## Where it is\nTIA1611-own-words.")),
            (Here, Row("TIA1611", "## Needs you\nNothing.\n\n## Where it is\nwrong-node-words.")),
            (Other, Row(SummaryRow.FleetScope, "## Needs you\nNothing.\n\n## Where it is\nnode-wide-words.")));
        window.Chrome.Show(PilotPage.Flights);
        window.Chrome.Flights.OpenDetail("TIA1611");

        var words = window.Words(window.Chrome.Flights.Tree(screen));
        words.ShouldContain("TIA1611-own-words");
        words.ShouldContain("flight TIA1611");
        words.ShouldNotContain("wrong-node-words");
        words.ShouldNotContain("node-wide-words");

        window.Chrome.Render(NoSessions, screen, CardsScreen.Empty, new RectF32(0, 0, window.Width, window.Height));
        window.Button(window.Chrome.Flights).OnClick!.Invoke(InputModifier.None);
        Says(window.Sent.Single().Request("gui"), "flight/TIA1611", []);
        window.Sent.Single().Node.ShouldBe(Other);
        await window.AnswerAsync(null);
    }

    [Fact]
    public void ANodeThisUserCannotSteerGetsADisabledButtonThatSaysWhy()
    {
        using var window = new Window();
        var board = window.Chrome.Summaries;
        board.Shelf = Shelf([Here, Other], (Other, Row(SummaryRow.FleetScope)));
        board.CanSteer = node => node?.Here ?? true;
        window.Chrome.Nodes.Choices = board.Shelf.Marks;
        window.Chrome.Nodes.Pick(Other.Key, InputModifier.None);
        window.Draw();

        window.HasButton(window.Chrome.Home).ShouldBeFalse();
        window.Arrange(window.Chrome.Home.Tree(Home())).Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(FleetClients.WatchOnly(Other));
        board.Press(board.Shelf.Fleet(window.Chrome.Nodes));
        window.Sent.ShouldBeEmpty();
    }

    [Fact]
    public void WithNoNodeToAskThereIsNoButtonAndNoCardUnlessThereIsASummary()
    {
        using var window = new Window(1280, 2400);
        window.Chrome.Summaries.Send = null;

        window.Words(window.Chrome.Home.Tree(Home())).ShouldNotContain("Summary");

        window.Chrome.Summaries.Shelf = Shelf([Here], (Here, Row(SummaryRow.FleetScope)));
        var words = window.Words(window.Chrome.Home.Tree(Home()));
        words.ShouldContain("Needs you");
        words.ShouldNotContain("Summarise");
    }

    // WCAG 2.x relative luminance and contrast ratio, as TranscriptPaneTests measures.
    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void EveryColourPairOnTheCardMeetsTheContrastFloorOnWhatItIsDrawnOn()
    {
        var p = Palette.Standard;
        var style = SummaryCard.Style(p);

        // Body, headings, links and code on the card's panel; the status and the labels too.
        foreach (var text in new[] { style.Text, style.Strong, style.Link, style.Muted, p.Bright, p.Body, p.Dim, p.Held, p.Failed })
        {
            Contrast(text, p.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
        }
        Contrast(style.Code, style.CodeBackground).ShouldBeGreaterThanOrEqualTo(4.5);
        // The button: live (on its fill and lit under the pointer) and disabled or running.
        Contrast(p.Bright, p.Button).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(p.Bright, p.ButtonHover).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(p.Body, p.GhostBg).ShouldBeGreaterThanOrEqualTo(4.5);
        // The card is told from the page it sits on, and its fill is not the page's.
        p.PanelBg.ShouldNotBe(p.Ground);
    }

    [Theory]
    [InlineData(1280u)]
    [InlineData(520u)]
    public void TheHeaderRowSharesOneHeightAndNothingRunsPastTheWindow(uint width)
    {
        using var window = new Window(width, 1600);
        window.Chrome.Summaries.Shelf = Shelf([Here], (Here, Row(SummaryRow.FleetScope, Kept + "\n\n" + string.Join(" ", Enumerable.Repeat("a-long-sentence-about-the-fleet-and-what-it-did", 12)))));
        window.Draw();
        var tree = window.Chrome.Home.Tree(Home());

        var arranged = window.Arrange(tree);
        var title = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Summary" } });
        var button = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit b && b.Action.StartsWith("summary:", StringComparison.Ordinal));
        button.Bounds.Height.ShouldBe(SummaryCard.HeaderHeight);
        button.Bounds.Width.ShouldBe(SummaryCard.ButtonWidth);
        // One middle: the title and the button centre on the same line.
        (title.Bounds.Y + title.Bounds.Height / 2).ShouldBe(button.Bounds.Y + button.Bounds.Height / 2, 1.0);
        // Running takes the button's place at its own size, so nothing beside it moves.
        window.Button(window.Chrome.Home).OnClick!.Invoke(InputModifier.None);
        var running = window.Arrange(window.Chrome.Home.Tree(Home())).Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Summarising…" } });
        (running.Bounds.X, running.Bounds.Y, running.Bounds.Width, running.Bounds.Height).ShouldBe((button.Bounds.X, button.Bounds.Y, button.Bounds.Width, button.Bounds.Height));
        // The reading width: a paragraph is never longer than ProseWidth, however wide the window.
        var below = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Nothing is flying" or "1 flight flying" } }).Bounds.Y;
        var body = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && a.Bounds.Y > button.Bounds.Y + SummaryCard.HeaderHeight + SummaryCard.StatusHeight + 8f
            && a.Bounds.Y < below - 8f).ToList();
        body.ShouldNotBeEmpty();
        body.ShouldAllBe(a => a.Bounds.X + a.Bounds.Width <= title.Bounds.X + SummaryCard.ProseWidth + 8f);
        // ...and it uses that width where the window has it: a column squeezed to one word wide is no reading width (the first capture).
        if (width > 800)
        {
            body.Max(a => a.Bounds.X + a.Bounds.Width).ShouldBeGreaterThan(title.Bounds.X + SummaryCard.ProseWidth * 0.75f);
        }
        body.Count.ShouldBeLessThan(160);
        // The card is as tall as its text: the last word sits inside it, above the line the page goes on with (a stack measured the
        // paragraph at the card's whole width and left the text running out of the card).
        var last = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.Contains("what-it-did", StringComparison.Ordinal)).Last();
        (last.Bounds.Y + last.Bounds.Height).ShouldBeLessThanOrEqualTo(below - 14f + 0.5f);
        // Nothing is wider than the window, and nothing is zero-sized.
        foreach (var a in arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }))
        {
            (a.Bounds.X + a.Bounds.Width).ShouldBeLessThanOrEqualTo(width + 0.5f);
            a.Bounds.Width.ShouldBeGreaterThan(0f);
        }
    }

    [Fact]
    public async Task ALongRefusalIsCutWithAnEllipsisAndCarriesItsWholeTextOnHover()
    {
        using var window = new Window(520, 1600);
        window.Chrome.Summaries.Shelf = Shelf([Here]);
        window.Draw();
        window.Button(window.Chrome.Home).OnClick!.Invoke(InputModifier.None);
        var why = "the node on LE1 (192.168.1.20:47731) could not be reached: " + new string('x', 160);
        await window.AnswerAsync(why);

        var whole = $"The node did not make one: {why}. Press Summarise to try again.";
        window.Arrange(window.Chrome.Home.Tree(Home())).Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(whole);
    }

    [Fact]
    public void CapturesOfTheCardOnHomeAndOnAFlightsPageAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var longKept = "## Needs you\n"
            + "- Card c12: PR #7 on tianwen waits for your merge; checks are green and the review passed at head 77018f1c.\n"
            + "- TIA1611's leg `render` failed its check (`dotnet test --filter FullyQualifiedName~PlanetBufferTests` ended 3 failed), and its worker stopped on a permission prompt.\n"
            + "- TIA9 is held for TIA1611 to land; nothing to do until then.\n\n"
            + "## Where it is\n"
            + "TIA1611 has landed its parser leg and the **render** leg is flying with 2 of 3 steps ticked: the buffer and the shader are in, the capture is not. "
            + "TIA9 waits on it. The review of PR #7 passed on 77018f1c; GitHub's checks are green. Nothing else is in play on SE1, and LE1 holds one flight, ORB204, whose "
            + "planner is still reading the repo.";
        foreach (var (width, height) in new[] { (1280, 900), (2000, 900) })
        {
            using var window = new Window((uint)width, (uint)height);
            window.Chrome.Summaries.Shelf = Shelf([Here], (Here, Row(SummaryRow.FleetScope, longKept)), (Here, Row("TIA1611", longKept)));
            var flights = new FlightsScreen([Line()], "test", Now);
            window.Draw(flights);
            File.WriteAllBytes(Path.Combine(dir, $"summary-home-{width}.png"), PngWriter.Encode(window.Renderer.Surface.Pixels, width, height));

            // Running, with the old one standing.
            window.Button(window.Chrome.Home).OnClick!.Invoke(InputModifier.None);
            window.Draw(flights);
            File.WriteAllBytes(Path.Combine(dir, $"summary-home-running-{width}.png"), PngWriter.Encode(window.Renderer.Surface.Pixels, width, height));

            window.Chrome.Show(PilotPage.Flights);
            window.Chrome.Flights.OpenDetail("TIA1611");
            window.Draw(flights);
            File.WriteAllBytes(Path.Combine(dir, $"summary-flight-{width}.png"), PngWriter.Encode(window.Renderer.Surface.Pixels, width, height));
        }
        // Nothing made yet, and a refusal.
        using var empty = new Window(1280, 600);
        empty.Chrome.Summaries.Shelf = Shelf([Here]);
        empty.Draw();
        File.WriteAllBytes(Path.Combine(dir, "summary-home-empty-1280.png"), PngWriter.Encode(empty.Renderer.Surface.Pixels, 1280, 600));
    }
}
