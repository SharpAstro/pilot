using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A flight whose move to another node failed, and a flight moved in from one (pilot #376): the Flights page and Home show the
/// failed one in red, with the reason in wrapped text and a Retry move button that sends <c>move-flight</c>, and the moved-in one
/// says where it came from.
/// </summary>
public sealed class FlightMoveTests
{
    private static readonly DateTimeOffset At = Live.At;

    private const string Reason =
        "No node took TIA1611: DE1 refused it (accept-fleet-flights is off) and SE2 does not answer. The plane at "
        + @"C:\Users\Seb\source\repos\sharpastro\tianwen\.claude\worktrees\tia1611 is clean and pushed. "
        + "Turn accept-fleet-flights on at DE1, or wake SE2, then Retry move.";

    private static FlightRow Flight(string code, string repo, int issue, FlightState state, string title, Func<FlightRow, FlightRow>? with = null)
    {
        var row = new FlightRow
        {
            Code = code, Repo = repo, RepoPath = $"/r/{repo}", Issue = issue, Title = title, Branch = $"flight/{code.ToLowerInvariant()}",
            Worktree = $"/r/{repo}/.claude/worktrees/{code}", Base = "main", State = state, CreatedAt = At.AddHours(-3),
        };
        return with?.Invoke(row) ?? row;
    }

    private static LegRow Leg(string flight, string name, int order, LegState state) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state,
    };

    /// <summary>TIA1611 whose move failed, ORB204 moved in from SE1 after its parser leg, and NAV7 as it always was.</summary>
    private static readonly FlightRow[] Rows =
    [
        Flight("TIA1611", "tianwen", 1611, FlightState.Flying, "Planets on the GPU, with a title long enough to need its hover text", f => f with { MoveFailure = Reason }),
        Flight("ORB204", "orbit", 204, FlightState.Flying, "Orbit render", f => f with { MovedFrom = new FlightMoved { From = "SE1", AfterLeg = "parser", At = At.AddMinutes(-20) } }),
        Flight("NAV7", "navigator", 7, FlightState.Flying, "Chart cache"),
    ];

    private static readonly LegRow[] Legs =
    [
        Leg("TIA1611", "parser", 1, LegState.Done), Leg("TIA1611", "render", 2, LegState.Planned),
        Leg("ORB204", "parser", 1, LegState.Done), Leg("ORB204", "render", 2, LegState.Flying),
        Leg("NAV7", "cache", 1, LegState.Flying),
    ];

    private static FlightsScreen Screen(params FlightRow[] rows) =>
        new(FlightLine.Build(rows, [], At, Legs), "sample", At);

    private static readonly SessionsScreen NoSessions = new([], "sample", At);
    private static readonly JobsScreen NoJobs = new([], "sample", At);

    // WCAG 2.x relative luminance and contrast ratio.
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

    private sealed record Run(string Text, RGBAColor32 Color, Rect<float> Bounds);

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(Layout.Node tree, RgbaImageRenderer renderer, float width, float height) =>
        Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

    private static bool Inside(Rect<float> inner, Rect<float> outer) =>
        inner.X >= outer.X - 0.5f && inner.Y >= outer.Y - 0.5f && inner.X + inner.Width <= outer.X + outer.Width + 0.5f && inner.Y + inner.Height <= outer.Y + outer.Height + 0.5f;

    private static List<Run> Runs(ImmutableArray<Layout.ArrangedNode<float>> arranged, Rect<float>? within = null) =>
        [.. arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && (within is not { } area || Inside(a.Bounds, area)))
            .Select(a => (Text: (Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content, a.Bounds))
            .Select(t => new Run(t.Text.Value, t.Text.Color, t.Bounds))];

    /// <summary>The words of a reason, as they wrapped: every piece joined and its whitespace made single, so a word broken at a separator is whole again.</summary>
    private static string Said(IEnumerable<Run> runs) => Regex.Replace(string.Concat(runs.Select(r => r.Text)), @"\s+", " ").Trim();

    private static readonly string ReasonWords = Regex.Replace(Reason, @"\s+", " ").Trim();

    private static FlightsView<RgbaImage> View(RgbaImageRenderer renderer, Action<FlightAction>? onAction = null, bool canCommand = true) =>
        new(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List, CanCommand = canCommand, OnAction = onAction ?? (_ => { }), OnChat = _ => { } };

    /// <summary>The band: the red node taller than a row, which the page puts above its table.</summary>
    private static Layout.ArrangedNode<float> Band(ImmutableArray<Layout.ArrangedNode<float>> arranged, RGBAColor32 red) =>
        arranged.Where(a => a.Node.Background == red && a.Bounds.Height != 54f && a.Bounds.Height > 40f && a.Bounds.Width > 300f).OrderBy(a => a.Depth).First();

    // NAV7 is the newest, so it is the first row and selected: TIA1611's red row stands unselected in the second.
    private static FlightsScreen NewestFirst() =>
        Screen(Rows[0], Rows[1], Rows[2] with { CreatedAt = At });

    private static bool IsHeading(Run r) => r.Text.StartsWith("⚠", StringComparison.Ordinal);

    [Fact]
    public void FlightMoveARowWhoseMoveFailedIsRedAndSaysSoInWords()
    {
        var screen = NewestFirst();
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = View(renderer);

        var arranged = Arrange(view.Tree(screen), renderer, 1600, 900);

        var rows = arranged.Where(a => a.Node.Background == Palette.Standard.FailedBg && a.Bounds.Height == 54f).ToList();
        rows.Count.ShouldBe(1);
        var words = Runs(arranged, rows[0].Bounds);
        // Said, not only coloured: the row's State column says it under "flying", and the code is the row's.
        words.Select(r => r.Text).ShouldContain("move failed");
        words.Select(r => r.Text).ShouldContain("TIA1611");
        screen.Lines.Single(l => l.Code == "TIA1611").ToDumpLine().ShouldContain("move failed");
        screen.Lines.Where(l => l.Code != "TIA1611").ShouldAllBe(l => l.MoveMark == null && !l.ToDumpLine().Contains("move failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlightMoveEveryWordOnTheRedRowIsAtFourPointFiveToOneOnTheRedItIsDrawnOn(bool selected)
    {
        var screen = selected ? Screen(Rows) : NewestFirst();
        var p = Palette.Standard;
        var ground = selected ? p.FailedFocusBg : p.FailedBg;
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = View(renderer);

        var arranged = Arrange(view.Tree(screen), renderer, 1600, 900);

        var row = arranged.Single(a => a.Node.Background == ground && a.Bounds.Height == 54f);
        // What the keyboard focus lights it with is the same red, never the blue of a plain row: it would paint over this one.
        row.Node.FocusBackground.ShouldBe(p.FailedFocusBg);
        var words = Runs(arranged, row.Bounds);
        words.Count.ShouldBeGreaterThan(3);
        foreach (var word in words.Where(w => w.Text.Length > 0))
        {
            Contrast(word.Color, ground).ShouldBeGreaterThanOrEqualTo(4.5, $"{word.Text} on the {(selected ? "selected " : "")}red row");
        }
    }

    [Fact]
    public void FlightMoveTheRedColoursHoldContrastOnEveryGroundTheyAreDrawnOn()
    {
        var p = Palette.Standard;
        foreach (var ground in new[] { p.FailedBg, p.FailedFocusBg, p.RowHoverBg })
        {
            foreach (var text in new[] { p.Bright, p.Body, p.FailedText })
            {
                Contrast(text, ground).ShouldBeGreaterThanOrEqualTo(4.5);
            }
        }
        // Retry move: its words on its fill (and lit), and its edge against the band, which a control's edge needs at 3:1.
        Contrast(p.RetryText, p.RetryBg).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(p.RetryText, p.RetryHover).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(p.RetryBg, p.FailedBg).ShouldBeGreaterThanOrEqualTo(3.0);
        // The stripe is the red of a failed leg and is a mark, not text: 3:1 against the red it sits on.
        Contrast(p.Failed, p.FailedBg).ShouldBeGreaterThanOrEqualTo(3.0);
        // Night keeps to reds on black and still holds the floor for what is red.
        var n = Palette.Night;
        foreach (var ground in new[] { n.FailedBg, n.FailedFocusBg })
        {
            Contrast(n.FailedText, ground).ShouldBeGreaterThanOrEqualTo(4.5);
        }
        Contrast(n.RetryText, n.RetryBg).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(n.RetryText, n.RetryHover).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(n.RetryBg, n.FailedBg).ShouldBeGreaterThanOrEqualTo(3.0);
    }

    [Theory]
    [InlineData(760)]
    [InlineData(1600)]
    public void FlightMoveTheReasonIsShownWrappedAndWholeBesideRetryMove(int width)
    {
        var p = Palette.Standard;
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var view = View(renderer);

        var arranged = Arrange(view.Tree(Screen(Rows)), renderer, width, 900);

        var band = Band(arranged, p.FailedBg);
        var inBand = Runs(arranged, band.Bounds);
        var retry = inBand.Single(r => r.Text == FlightAction.RetryMove);
        var heading = inBand.Single(IsHeading);
        heading.Text.ShouldContain("move failed");
        var reason = inBand.Where(r => r != retry && r != heading).ToList();
        // Whole, in its words, every piece of it inside the band and clear of the button.
        Said(reason).ShouldBe(ReasonWords);
        reason.ShouldAllBe(r => r.Bounds.X + r.Bounds.Width <= retry.Bounds.X);
        reason.ShouldAllBe(r => Inside(r.Bounds, band.Bounds));
        // Wrapped: a narrow window takes several lines.
        var lines = reason.Select(r => (int)MathF.Round(r.Bounds.Y)).Distinct().Count();
        lines.ShouldBeGreaterThanOrEqualTo(width < 1000 ? 3 : 1);
        reason.ShouldAllBe(r => r.Color == p.Bright);
        // The band is inset by the page's gutter, as the table below it is.
        band.Bounds.X.ShouldBeGreaterThan(0f);
    }

    [Fact]
    public void FlightMoveANarrowerWindowWrapsTheReasonOverMoreLines()
    {
        int Lines(int width)
        {
            using var renderer = new RgbaImageRenderer((uint)width, 900);
            var arranged = Arrange(View(renderer).Tree(Screen(Rows)), renderer, width, 900);
            var band = Band(arranged, Palette.Standard.FailedBg);
            return Runs(arranged, band.Bounds).Where(r => r.Text != FlightAction.RetryMove && !IsHeading(r))
                .Select(r => (int)MathF.Round(r.Bounds.Y)).Distinct().Count();
        }

        Lines(700).ShouldBeGreaterThan(Lines(1900));
    }

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome, bool home)
    {
        var regions = new List<ClickableRegion>();
        if (home)
        {
            chrome.Home.CollectPaintedRegions(regions);
        }
        else
        {
            chrome.Flights.CollectPaintedRegions(regions);
        }
        return regions;
    }

    private static PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer, PilotPage page, FlightsScreen screen, List<FlightAction> sent, bool canCommand = true)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        chrome.Flights.CanCommand = canCommand;
        chrome.Flights.OnAction = sent.Add;
        chrome.Flights.Mode = FlightsViewMode.List;
        chrome.LinkCardTranscripts(() => NoSessions, () => screen, jobs: () => NoJobs);
        chrome.Show(page);
        chrome.Render(NoSessions, screen, CardsScreen.Empty, NoJobs, null, null, new RectF32(0, 0, 1600, 900));
        return chrome;
    }

    [Theory]
    [InlineData(false, "flight:TIA1611:move-flight")]
    [InlineData(true, "home:flight:TIA1611:move-flight")]
    public void FlightMoveRetryMoveSendsMoveFlightForThatFlight(bool home, string id)
    {
        var sent = new List<FlightAction>();
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer, home ? PilotPage.Home : PilotPage.Flights, Screen(Rows), sent);

        var regions = Regions(chrome, home);
        var buttons = regions.Where(r => r.Result is HitResult.ButtonHit { Action: var a } && a.EndsWith(":move-flight", StringComparison.Ordinal)).ToList();
        // One Retry move for the one failed flight, not one in the band and one more in the details panel.
        buttons.Count.ShouldBe(1);
        buttons[0].Result.ShouldBe(new HitResult.ButtonHit(id));
        buttons[0].OnClick!.Invoke(InputModifier.None);

        var action = sent.ShouldHaveSingleItem();
        action.Verb.ShouldBe(Verbs.MoveFlight);
        action.Verb.ShouldBe("move-flight");
        action.Target.ShouldBe(Verbs.FlightTarget("TIA1611"));
        action.Args.ShouldBeEmpty();
        action.Label.ShouldBe("Retry move");
    }

    [Fact]
    public void FlightMoveRetryMoveIsNotOfferedWhereItCannotBeSentOrWhileALegFlies()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);

        // A snapshot has no node to command: the band says what failed and draws no button.
        var snapshot = Chrome(renderer, PilotPage.Flights, Screen(Rows), [], canCommand: false);
        Regions(snapshot, home: false).Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldNotContain("flight:TIA1611:move-flight");
        Said(Runs(Arrange(snapshot.Flights.Tree(Screen(Rows)), renderer, 1600, 900)).Where(r => !IsHeading(r))).ShouldContain(Reason.Split(' ')[0]);

        // The node refuses a move while a leg flies, so the flight offers none then.
        var flying = Screen(Rows[0]).Lines.Single() with { LegRows = [Leg("TIA1611", "render", 2, LegState.Flying)] };
        FlightAction.For(flying).Select(a => a.Verb).ShouldNotContain(Verbs.MoveFlight);
        FlightAction.For(Screen(Rows[0]).Lines.Single()).Select(a => a.Verb).ShouldContain(Verbs.MoveFlight);
        FlightAction.For(Screen(Rows[2]).Lines.Single()).Select(a => a.Verb).ShouldNotContain(Verbs.MoveFlight);
    }

    [Fact]
    public void FlightMoveAFlightsOwnPageShowsTheBandWithItsRetryMoveOnce()
    {
        var sent = new List<FlightAction>();
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer, PilotPage.Flights, Screen(Rows), sent);
        chrome.Flights.OpenDetail("TIA1611");
        chrome.Render(NoSessions, Screen(Rows), CardsScreen.Empty, NoJobs, null, null, new RectF32(0, 0, 1600, 900));

        var retry = Regions(chrome, home: false).Where(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:move-flight" }).ToList();
        retry.Count.ShouldBe(1);
        retry[0].OnClick!.Invoke(InputModifier.None);
        sent.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.MoveFlight);

        var texts = Runs(Arrange(chrome.Flights.Tree(Screen(Rows)), renderer, 1600, 900));
        texts.ShouldContain(r => IsHeading(r) && r.Text.Contains("move failed", StringComparison.Ordinal));
    }

    [Fact]
    public void FlightMoveAMovedInFlightSaysWhereItCameFromOnItsRowItsPageAndHome()
    {
        var screen = Screen(Rows);
        var moved = screen.Lines.Single(l => l.Code == "ORB204");
        moved.Provenance.ShouldBe("moved from SE1 after leg parser");
        moved.RowDetail.ShouldBe("moved from SE1 after leg parser");
        screen.Lines.Where(l => l.Code != "ORB204").ShouldAllBe(l => l.Provenance == null);
        moved.ToDumpLine().ShouldContain("moved from SE1 after leg parser");
        // A flight that came with no leg done says only where from.
        FlightLine.From(Rows[1] with { MovedFrom = new FlightMoved { From = "DE1", At = At } }, [], At).Provenance.ShouldBe("moved from DE1");

        using var renderer = new RgbaImageRenderer(1600, 900);
        // The row: the State column's second line.
        Runs(Arrange(View(renderer).Tree(screen), renderer, 1600, 900)).Select(r => r.Text).ShouldContain("moved from SE1 after leg parser");

        // Its own page, as a line of its own.
        var view = View(renderer);
        view.Tree(screen);
        view.OpenDetail("ORB204");
        Said(Runs(Arrange(view.Tree(screen), renderer, 760, 900))).ShouldContain("moved from SE1 after leg parser");

        // Home says it in its dump.
        var home = new HomeScreen(screen, NoSessions, NoJobs, CardsScreen.Empty);
        home.ToDump().ShouldContain("ORB204: moved from SE1 after leg parser");
    }

    [Fact]
    public void FlightMoveHomeShowsTheFailedFlightRedWithItsReasonWrappedAndItsWaitSaidInWords()
    {
        var screen = Screen(Rows);
        var home = new HomeScreen(screen, NoSessions, NoJobs, CardsScreen.Empty);

        home.FlightChips.Single(c => c.Flight!.Code == "TIA1611").Label.ShouldBe("TIA1611 · move failed");
        home.FlightChips.Single(c => c.Flight!.Code == "TIA1611").Tone.ShouldBe("failed");
        home.FlightChips.Where(c => c.Flight!.Code != "TIA1611").ShouldAllBe(c => c.Tone == "flying");
        home.Waiting.ShouldBe("1 move failed");
        home.Quiet.ShouldBeFalse();
        home.ToDump().ShouldContain($"move failed: TIA1611 | {Reason}");

        using var renderer = new RgbaImageRenderer(760, 900);
        var view = new HomeView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, OnAction = _ => { } };
        var arranged = Arrange(view.Tree(home), renderer, 700, 900);
        var p = Palette.Standard;

        var band = Band(arranged, p.FailedBg);
        var inBand = Runs(arranged, band.Bounds);
        var retry = inBand.Single(r => r.Text == FlightAction.RetryMove);
        var reason = inBand.Where(r => r != retry && !IsHeading(r)).ToList();
        Said(reason).ShouldBe(ReasonWords);
        reason.ShouldAllBe(r => r.Bounds.X + r.Bounds.Width <= retry.Bounds.X);
        reason.Select(r => (int)MathF.Round(r.Bounds.Y)).Distinct().Count().ShouldBeGreaterThan(2);
        reason.ShouldAllBe(r => Contrast(r.Color, p.FailedBg) >= 4.5);
        // The chip is red and its words are on it at the floor.
        var red = CardsView<RgbaImage>.StatusTint(ChoiceStatus.Failing)!.Value.Bg;
        var chip = arranged.Single(a => a.Node.Background == red);
        var chipWords = Runs(arranged, chip.Bounds).Single();
        chipWords.Text.ShouldBe("TIA1611 · move failed");
        Contrast(chipWords.Color, red).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void FlightMoveAStaleFlightsLastKnownFailureIsNotShownAsLiveRed()
    {
        var line = Screen(Rows[0]).Lines.Single() with { Stale = "DE1" };

        line.MoveMark.ShouldBeNull();
        line.MoveFailure.ShouldBeNull();
        line.StaleMark.ShouldBe("stale (DE1 unreachable)");
        FlightAction.For(line).Select(a => a.Verb).ShouldNotContain(Verbs.MoveFlight);
        MoveFailureBand.Build<RgbaImage>(line, Palette.Standard, _ => { }).ShouldBeNull();
        var home = new HomeScreen(new FlightsScreen([line], "sample", At), NoSessions, NoJobs, CardsScreen.Empty);
        home.Waiting.ShouldBe("Nothing waits on you");
        home.FlightChips.Single().Tone.ShouldBe("stale");
    }

    [Fact]
    public void FlightMoveMoreThanThreeFailuresStackThreeBandsAndSayHowManyMore()
    {
        var rows = Enumerable.Range(1, 5).Select(i => Flight($"TIA{i}", "tianwen", i, FlightState.Flying, $"flight {i}", f => f with { MoveFailure = $"DE1 refused TIA{i}. Turn accept-fleet-flights on at DE1, then Retry move." })).ToArray();
        var screen = new FlightsScreen(FlightLine.Build(rows, [], At), "sample", At);
        using var renderer = new RgbaImageRenderer(1600, 900);

        var runs = Runs(Arrange(View(renderer).Tree(screen), renderer, 1600, 900));

        runs.Count(IsHeading).ShouldBe(MoveFailureBand.MaxBands);
        runs.Count(r => r.Text == FlightAction.RetryMove).ShouldBe(MoveFailureBand.MaxBands);
        runs.Select(r => r.Text).ShouldContain("and 2 more flights whose move failed: their rows are red");
    }

    [Fact]
    public void FlightMoveCapturesAtANarrowAndAWideSize()
    {
        var screen = Screen(Rows);
        // Where the captures go, when a person is to look at them; else a folder that goes with the test.
        var given = Environment.GetEnvironmentVariable("PILOT_MOVE_CAPTURES");
        var phase = Environment.GetEnvironmentVariable("PILOT_MOVE_PHASE") is { Length: > 0 } p ? p : "after";
        var folder = given is { Length: > 0 } ? Directory.CreateDirectory(given) : Directory.CreateTempSubdirectory("pilot-move-");
        try
        {
            foreach (var (name, width, height) in new[] { ("narrow", 760, 640), ("wide", 1600, 900) })
            {
                foreach (var (page, label, mode, open) in new (PilotPage, string, FlightsViewMode, string?)[]
                {
                    (PilotPage.Home, "home", FlightsViewMode.Canvas, null),
                    (PilotPage.Flights, "flights-list", FlightsViewMode.List, null),
                    (PilotPage.Flights, "flights-canvas", FlightsViewMode.Canvas, null),
                    (PilotPage.Flights, "flight-page", FlightsViewMode.List, "TIA1611"),
                    (PilotPage.Flights, "flight-page-moved-in", FlightsViewMode.List, "ORB204"),
                })
                {
                    var png = Path.Combine(folder.FullName, $"{phase}-{label}-{name}.png");
                    using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                    var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                    chrome.Play.ShowEverything();
                    chrome.Flights.CanCommand = true;
                    chrome.Flights.Mode = mode;
                    chrome.LinkCardTranscripts(() => NoSessions, () => screen, jobs: () => NoJobs);
                    chrome.Show(page);
                    if (open is not null)
                    {
                        chrome.Flights.OpenDetail(open);
                    }

                    chrome.Render(NoSessions, screen, CardsScreen.Empty, NoJobs, null, null, new RectF32(0, 0, width, height));
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
