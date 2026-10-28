using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A flight's own page: its legs in order with state, Done-when, checklist, go-arounds and artifacts at their latest, each openable.</summary>
public sealed class FlightDetailTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1611,
        Title = "Planets on the GPU",
        Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = Live.At,
        PullRequest = 7,
        PullRequestUrl = "https://github.com/x/tianwen/pull/7",
    };

    private static Artifact Shot(string title, string location, int minutes) => new() { Title = title, Location = location, At = Live.At.AddMinutes(minutes) };

    private static LegRow[] Legs() =>
    [
        // Out of order on purpose: the page puts them in the flight's order.
        new()
        {
            Flight = "TIA1611", Name = "render", Order = 2, Goal = "Draw the planets from the GPU buffer.", DoneWhen = "The capture shows all eight.",
            Check = "dotnet test", State = LegState.Flying, GoArounds = 1,
            Checklist = [new CheckItem { Text = "buffer", Done = true }, new CheckItem { Text = "shader" }, new CheckItem { Text = "capture" }],
            Artifacts = [Shot("The planets", @"C:\caps\planets.png", 5), Shot("The planets", @"C:\caps\planets-2.png", 40), Shot("Preview", "https://example.org/preview", 20)],
        },
        new()
        {
            Flight = "TIA1611", Name = "parser", Order = 1, Goal = "Read the ephemeris.", DoneWhen = "Its tests pass.", State = LegState.Done,
            Checklist = [new CheckItem { Text = "read", Done = true }], Report = "all green",
        },
    ];

    private static FlightLine Line() => FlightLine.Build([Flight()], [], Now, Legs()).Single();

    [Fact]
    public void ItsLegsComeInOrderWithTheirStateStepsAndGoArounds()
    {
        var detail = FlightDetail.From(Line(), Now);

        detail.Legs.Select(l => l.Name).ShouldBe(["parser", "render"]);
        var parser = detail.Legs[0];
        parser.State.ShouldBe("done");
        parser.StepsText.ShouldBe("1/1 steps");
        parser.GoArounds.ShouldBeNull();
        parser.Report.ShouldBe("all green");
        parser.Check.ShouldBeNull();
        var render = detail.Legs[1];
        render.State.ShouldBe("flying");
        render.StepsText.ShouldBe("1/3 steps");
        render.GoArounds.ShouldBe("go-around 1");
        render.Goal.ShouldBe("Draw the planets from the GPU buffer.");
        render.DoneWhen.ShouldBe("The capture shows all eight.");
        render.Check.ShouldBe("dotnet test");
        render.Steps.ShouldBe([new StepLine("buffer", true), new StepLine("shader", false), new StepLine("capture", false)]);
    }

    [Fact]
    public void EachArtifactIsAtItsLatestAndSaysHowItOpens()
    {
        var render = FlightDetail.From(Line(), Now).Legs[1];

        // The second capture of one title replaces the first; newest first.
        render.Artifacts.Select(a => (a.Title, a.Location)).ShouldBe([("The planets", @"C:\caps\planets-2.png"), ("Preview", "https://example.org/preview")]);
        var shot = render.Artifacts[0];
        shot.IsUrl.ShouldBeFalse();
        shot.Folder.ShouldBe(@"C:\caps");
        shot.When.ShouldBe("1h 20m ago");
        render.Artifacts[1].IsUrl.ShouldBeTrue();
        render.Artifacts[1].Folder.ShouldBeNull();
    }

    [Fact]
    public void ThePageSaysWhatTheReviewAsksInFullAndThatTheNextRoundIsYoursPastTheCap()
    {
        // PIL294's page showed a "review changes" chip and every step ticked, while the findings sat on a card cut short.
        var asks = "1) CI is red: LayoutDrivenTests rejects JobsView.cs:680. 2) The memory gate takes the smaller of the estimate and job-memory, so a job that measured 8 GB starts with 2 GB free.";
        var flight = Flight() with { Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "du1-tia1611-review", Report = asks, StartedAt = Live.At } };
        var legs = Legs().Select(l => l.Name == "render" ? l with { State = LegState.Done, ReviewGoArounds = 3 } : l).ToArray();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new FlightsView<RgbaImage>(renderer);
        // The prose wraps word by word, one leaf each: read the leaves back as the text a person would see.
        string Said(FlightRow row, LegRow[] rows) => string.Concat(System.Text.RegularExpressions.Regex.Matches(CellLayout.Describe(Layout.Engine.Arrange(
            Layout.Builder.VStack([.. view.ReviewAsks(FlightDetail.From(FlightLine.Build([row], [], Now, rows).Single(), Now))]),
            new Rect<int>(0, 0, 400, 40), CellMeasureContext.PixelAuthored)), @"Leaf Text ""(.*)"" \(")
            .Select(m => m.Groups[1].Value.EndsWith(' ') ? m.Groups[1].Value : m.Groups[1].Value + "\n"));

        var said = Said(flight, legs);

        said.ShouldContain("The review asks for changes:");
        said.ShouldContain("JobsView.cs:680");
        said.ShouldContain("starts with 2 GB free");
        said.ShouldContain("render has been redone 3 times");
        said.ShouldContain("Redo leg render on the review's card once more, or Ready anyway");
        // An earlier capped leg with a later one planned after it is not the person's round: the node still lets the watchtower go around.
        var planned = legs.Append(legs[0] with { Name = "polish", Order = 3, State = LegState.Planned, ReviewGoArounds = 0 }).ToArray();
        Said(flight, planned).ShouldNotContain("the next round is yours");
        // A review that passed, or none, says nothing here.
        Said(Flight(), Legs()).Trim().ShouldBeEmpty();
    }

    // The page's text leaves with the colour each is drawn in, and its clickable regions, at a width where nothing is cut.
    private static (List<Layout.Content.Text> Leaves, List<ClickableRegion> Regions) LeavesOf(FlightRow flight, int width = 1600, Action<string>? onOpen = null, bool listed = true)
    {
        using var vessel = new RgbaImageRenderer((uint)width, 800);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        flights.OnOpen = onOpen;
        // The node lists the flight's PR among its open ones: its link opens the Pull requests page's row, with the ↗ beside it for the browser.
        // Unlisted (merged, or not read yet) it has no row to open, and the link is the browser's.
        flights.PullRequests = !listed ? null : new PullRequestsRow
        {
            Orgs = ["x"], ReadAt = Now, Viewer = "sebgod",
            PullRequests =
            [
                new PullRequestRow
                {
                    Repo = "x/tianwen", Number = 7, Title = flight.Title ?? flight.Code, Url = "https://github.com/x/tianwen/pull/7", Author = "sebgod",
                    Kind = PullRequestKind.ChecksRunning, Label = "Checks running", Flight = flight.Code, UpdatedAt = Now, CreatedAt = Live.At,
                    Head = "flight/tia1611", Base = "main",
                },
            ],
        };
        var screen = new FlightsScreen([FlightLine.Build([flight], [], Now, Legs()).Single()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, width, 800));
        flights.OpenDetail("TIA1611");
        flights.Render(screen, new RectF32(0, 0, width, 800));
        var leaves = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 800), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().ToList();
        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        return (leaves, regions);
    }

    [Theory]
    [InlineData("open", null, null, false)]
    [InlineData("draft", null, null, true)]
    [InlineData("queued", "in the merge queue, position 2", null, false)]
    [InlineData("merged", null, "landed", false)]
    public void TheHeaderCarriesAPrTheNodeDoesNotListAsTheBrowsersLinkWithItsStateAndTheFactsLineNoLonger(string state, string? queue, string? landed, bool draft)
    {
        var flight = Flight() with
        {
            MergeQueue = queue,
            GitHubDraft = draft ? true : null,
            State = landed is null ? FlightState.Flying : FlightState.Landed,
            LandedAt = landed is null ? null : Live.At,
        };
        var opened = new List<string>();

        var (leaves, regions) = LeavesOf(flight, onOpen: opened.Add, listed: false);

        // No row to open on the Pull requests page: "PR #7 ↗" is the one link, and it leaves the app.
        var link = leaves.Single(t => t.Value.StartsWith("PR #7", StringComparison.Ordinal));
        link.Value.ShouldBe("PR #7" + Followable.Away);
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: "pr:TIA1611:browser" }).ShouldBeFalse();
        // Once, in the header: the facts line says the PR no longer.
        leaves.Count(t => t.Value.Contains("PR #7", StringComparison.Ordinal)).ShouldBe(1);
        var said = leaves.Single(t => t.Value == state);
        // The state sits beside the link, after the code and the title.
        leaves.IndexOf(said).ShouldBe(leaves.IndexOf(link) + 1);
        leaves.FindIndex(t => t.Value == "TIA1611").ShouldBeLessThan(leaves.IndexOf(link));
        // A click opens it through the host's opener, a right-click offers Copy URL.
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "pr:TIA1611" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["https://github.com/x/tianwen/pull/7"]);
    }

    [Fact]
    public void TheHeaderCarriesAListedPrAsALinkToItsRowWithTheBrowsersArrowAndTheChipForItsState()
    {
        var opened = new List<string>();

        var (leaves, regions) = LeavesOf(Flight(), onOpen: opened.Add);

        var link = leaves.Single(t => t.Value.StartsWith("PR #7", StringComparison.Ordinal));
        link.Value.ShouldBe("PR #7");
        // "PR #7" opens the PR on the Pull requests page; the ↗ beside it is the one that leaves the app.
        var away = leaves[leaves.IndexOf(link) + 1];
        away.Value.ShouldBe(Followable.Away.Trim());
        // The page's chip says where it stands, in place of the flight's own word.
        leaves[leaves.IndexOf(away) + 1].Value.ShouldBe("Checks running");
        leaves.Any(t => t.Value is "open" or "draft" or "queued" or "merged").ShouldBeFalse();
        leaves.Count(t => t.Value.Contains("PR #7", StringComparison.Ordinal)).ShouldBe(1);
        leaves.FindIndex(t => t.Value == "TIA1611").ShouldBeLessThan(leaves.IndexOf(link));
        // A link that leaves the app: a click opens it through the host's opener, a right-click offers Copy URL.
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "pr:TIA1611:browser" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["https://github.com/x/tianwen/pull/7"]);
    }

    [Fact]
    public void AFlightWithNoPrHasNoLinkAndNoStateInItsHeader()
    {
        var (leaves, _) = LeavesOf(Flight() with { PullRequest = null, PullRequestUrl = null });

        leaves.Any(t => t.Value.StartsWith("PR #", StringComparison.Ordinal)).ShouldBeFalse();
        leaves.Any(t => t.Value is "open" or "draft" or "queued" or "merged").ShouldBeFalse();
    }

    [Fact]
    public void AReportFromAnOlderHeadSaysSoAndIsDimmedWhileOneForTheCurrentHeadIsNot()
    {
        var asks = "1) CI is red: LayoutDrivenTests rejects JobsView.cs:680.";
        var changes = new ReviewRow { State = ReviewState.Changes, Reviewer = "r", Report = asks, StartedAt = Live.At, GoAround = 2, ReviewedHead = "cafe0000aaaa" };

        var (older, _) = LeavesOf(Flight() with { Review = changes, GitHubHead = "beef1111bbbb" });
        var (current, _) = LeavesOf(Flight() with { Review = changes, GitHubHead = "CAFE0000AAAA" });

        // Each word of the report is a leaf of its own colour: the older one is the dim shade, the current one the body's.
        var oldWord = older.Single(t => t.Value == "LayoutDrivenTests ");
        var curWord = current.Single(t => t.Value == "LayoutDrivenTests ");
        oldWord.Color.ShouldNotBe(curWord.Color);
        Contrast(oldWord.Color, FlightsPalette.Standard.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(oldWord.Color, FlightsPalette.Standard.Ground).ShouldBeGreaterThanOrEqualTo(4.5);
        Joined(older).ShouldContain("review of cafe000, round 3; the head is now beef111");
        Joined(current).ShouldContain("review of cafe000, round 3");
        Joined(current).ShouldNotContain("the head is now");
        // The head not yet known (no look at the PR) says nothing of a newer one, and the report is not dimmed.
        var (unknown, _) = LeavesOf(Flight() with { Review = changes });
        Joined(unknown).ShouldNotContain("the head is now");
        unknown.Single(t => t.Value == "LayoutDrivenTests ").Color.ShouldBe(curWord.Color);
        // A review that never recorded its head still says its round.
        var (headless, _) = LeavesOf(Flight() with { Review = changes with { ReviewedHead = null }, GitHubHead = "beef1111bbbb" });
        Joined(headless).ShouldContain("review round 3");
    }

    private static ReviewRow Changes(string report) => new() { State = ReviewState.Changes, Reviewer = "r", Report = report, StartedAt = Live.At, GoAround = 0, ReviewedHead = "cafe0000aaaa" };

    [Fact]
    public void AReportWrittenAsAMarkdownListShowsOneListItemPerFindingWithItsNamesAsCode()
    {
        var report = "1. CI is red: `LayoutDrivenTests` rejects `JobsView.cs:680`.\n2. The memory gate takes the smaller of the estimate and `job-memory`.\n3. A **third** finding.";

        var (leaves, _) = LeavesOf(Flight() with { Review = Changes(report) });

        // One marker per finding, in the list's own column, so the findings do not run together as one paragraph.
        leaves.Where(t => t.Value is "1." or "2." or "3.").Select(t => t.Value).ShouldBe(["1.", "2.", "3."]);
        // A backticked name is drawn in the transcript's code colour, in a leaf of its own, and the prose around it is not.
        var code = leaves.Single(t => t.Value.StartsWith("LayoutDrivenTests", StringComparison.Ordinal));
        code.Color.ShouldBe(TranscriptEntryView.Prose.Code);
        leaves.Single(t => t.Value.StartsWith("job-memory", StringComparison.Ordinal)).Color.ShouldBe(TranscriptEntryView.Prose.Code);
        leaves.Single(t => t.Value == "rejects ").Color.ShouldBe(TranscriptEntryView.Prose.Text);
        leaves.Single(t => t.Value.StartsWith("third", StringComparison.Ordinal)).Color.ShouldBe(TranscriptEntryView.Prose.Strong);
        // The backticks are not shown, and a report that fits has no Show all.
        leaves.Any(t => t.Value.Contains('`')).ShouldBeFalse();
        leaves.Any(t => t.Value is "Show all" or "Show less").ShouldBeFalse();
        // Contrast against the panel the report is drawn on, in the transcript's colours.
        Contrast(code.Color, FlightsPalette.Standard.PanelBg).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void ALongPathOrNameInAReportWrapsAtItsSeparatorsInsidePageWidthAndStaysSelectableAndWhole()
    {
        const string path = "src/Pilot.Ui/SomeVeryLongNamespace/WithNestedFolders/AndMore/FlightsView.Detail.Header.Row.cs:1234";
        const string name = "Some_Very_Long_CheckName_That_Has_Many_Underscores_And_Keeps_Going_On_And_On";
        var report = $"1. `{path}` is wrong, and so is {path}.\n2. The check `{name}` fails, as **{name}**, does.";
        const int width = 900;
        using var vessel = new RgbaImageRenderer(width, 800);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        var screen = new FlightsScreen([FlightLine.Build([Flight() with { Review = Changes(report) }], [], Now, Legs()).Single()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, width, 800));
        flights.OpenDetail("TIA1611");
        flights.Render(screen, new RectF32(0, 0, width, 800));

        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 800), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f));
        var texts = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ToList();

        // Nothing is drawn past the page's right edge: the long words broke at their separators instead of running off it.
        texts.Where(a => a.Bounds.Right > width + 0.5f).Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value).ShouldBeEmpty();
        // Each path is more than one leaf, each of them selectable and none carrying a space the path did not have.
        var pieces = texts.Select(a => (Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Where(t => path.Contains(t.Value.TrimEnd(), StringComparison.Ordinal) && t.Value.Length > 3).ToList();
        pieces.Count.ShouldBeGreaterThan(2);
        pieces.ShouldAllBe(t => t.Selectable);
        // The leaves in reading order, joined, are the report's own words: the path appears whole twice, the name twice.
        var said = string.Concat(texts.Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value));
        said.Split(path).Length.ShouldBe(3);
        said.Split(name).Length.ShouldBe(3);
    }

    [Fact]
    public void ALongReportFoldsEachFindingToItsFirstLinesAndShowAllOpensItAndShowLessFoldsItAgain()
    {
        string Finding(int n) => $"{n}. Finding {n} starts here\n   detail A{n}\n   detail B{n}\n   detail C{n}\n   detail D{n}";
        var report = string.Join("\n", Enumerable.Range(1, 3).Select(Finding));
        using var vessel = new RgbaImageRenderer(1600, 800);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        var screen = new FlightsScreen([FlightLine.Build([Flight() with { Review = Changes(report) }], [], Now, Legs()).Single()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, 1600, 800));
        flights.OpenDetail("TIA1611");
        string Said()
        {
            flights.Render(screen, new RectF32(0, 0, 1600, 800));
            return string.Concat(Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, 1600, 800), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f))
                .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.EndsWith(' ') ? t.Value : t.Value + " "));
        }
        void Press()
        {
            var regions = new List<ClickableRegion>();
            flights.CollectPaintedRegions(regions);
            regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:review-fold" }).OnClick!.Invoke(InputModifier.None);
        }

        var folded = Said();

        // Each finding's first three lines show, the rest wait; nothing is cut without a pill to open it.
        folded.ShouldContain("Finding 1 starts here");
        folded.ShouldContain("detail B1");
        folded.ShouldContain("Finding 3 starts here");
        folded.ShouldNotContain("detail C1");
        folded.ShouldNotContain("detail D3");
        folded.ShouldContain("Show all");
        Press();
        var opened = Said();
        opened.ShouldContain("detail D1");
        opened.ShouldContain("detail D3");
        opened.ShouldContain("Show less");
        opened.ShouldNotContain("Show all");
        Press();
        Said().ShouldNotContain("detail D1");
        // The open state is the flight's: another flight's page starts folded.
        ReviewReport.Fold(report).Folded.ShouldBeTrue();
        ReviewReport.Fold("1. short\n2. short too").ShouldBe(("1. short\n2. short too", false));
    }

    [Fact]
    public void CapturesOfTheReviewReportFoldedOpenedAndOlderAtNarrowAndWideWindowsInBothPalettes()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var report = """
            1. **CI is red on `LayoutDrivenTests`.** It rejects `JobsView.cs:680`: a text leaf there is wider than its column.
               The check the leg names runs `dotnet test Pilot.Gui.Tests -c Release`, and it fails the same way on the PR's head.
               Fix the column width or wrap the leaf with `WrappedProse.Of`; do not loosen the test.
               The failure first showed in round 1 and the second round did not touch it, so the leg's report claiming green is wrong.
            2. **The memory gate takes the wrong minimum.** `JobMemory.Gate` uses the smaller of the estimate and `job-memory`, so a job that measured 8 GB starts with 2 GB free.
               See `src/Pilot.Node/Jobs/JobMemory.cs:112` and the test `AJobThatMeasuredMoreThanItsEstimate` that does not cover it.
            3. **A link in the report is dropped.** [the issue](https://github.com/SharpAstro/pilot/issues/383) is shown as plain words in the transcript's own tree.
               Check `Follow` against `TranscriptEntryView.WebLink`.
               Add a test that clicks it.
            4. Short one: `README.md` still says `pilot review changes` takes one line.
            """;
        foreach (var (width, night, open, older) in new[] { (900, false, false, false), (1800, false, false, false), (900, true, false, false), (1800, true, true, false), (1800, false, true, true) })
        {
            var flight = Flight() with { Review = Changes(report), GitHubHead = older ? "beef1111bbbb" : "cafe0000aaaa" };
            var screen = new FlightsScreen([FlightLine.Build([flight], [], Now, Legs()).Single()], "test", Now);
            using var renderer = new RgbaImageRenderer((uint)width, 1300);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            if (night)
            {
                chrome.Flights.Mode = FlightsViewMode.Night;
            }
            chrome.Show(PilotPage.Flights);
            chrome.Flights.OpenDetail("TIA1611");
            chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, width, 1300));
            if (open)
            {
                var regions = new List<ClickableRegion>();
                chrome.Flights.CollectPaintedRegions(regions);
                regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:review-fold" }).OnClick!.Invoke(InputModifier.None);
                chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, width, 1300));
            }
            File.WriteAllBytes(Path.Combine(dir, $"review-{width}{(night ? "-night" : "")}{(open ? "-open" : "")}{(older ? "-older" : "")}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, 1300));
        }
    }

    [Fact]
    public void AFoldedReportKeepsItsStructureAndAClosedFence()
    {
        var report = "Intro\n\n1. First\n   ```\n   code 1\n   code 2\n   code 3\n   ```\n2. Second";

        var (text, folded) = ReviewReport.Fold(report);

        folded.ShouldBeTrue();
        text.ShouldContain("1. First");
        text.ShouldContain("2. Second");
        text.ShouldNotContain("code 2");
        // The fence the cut left open is closed, so item 2 is not swallowed into it.
        text.Split('\n').Count(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal)).ShouldBe(2);
    }

    // The page's words as one string, a leaf's trailing space as a space: the wrapped prose reads as it does on the page.
    private static string Joined(List<Layout.Content.Text> leaves) => string.Concat(leaves.Select(t => t.Value.EndsWith(' ') ? t.Value : t.Value + " "));

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Lum(RGBAColor32 c) => 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
        var (hi, lo) = (Math.Max(Lum(a), Lum(b)), Math.Min(Lum(a), Lum(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void ADumpListsEveryLegStepAndArtifact()
    {
        var dump = FlightDetail.From(Line(), Now).ToDump();

        dump.ShouldContain("1. parser | done · 1/1 steps");
        dump.ShouldContain("2. render | flying · 1/3 steps · go-around 1");
        dump.ShouldContain("   done when: The capture shows all eight.");
        dump.ShouldContain("   [x] buffer");
        dump.ShouldContain("   [ ] shader");
        dump.ShouldContain(@"   artifact The planets: C:\caps\planets-2.png");
        dump.ShouldNotContain("planets.png");
    }

    [Fact]
    public void SelectingAFlightAndPressingEnterOpensItsPageAndEscapeGoesBack()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        var opened = new List<string>();
        flights.OnOpen = opened.Add;
        var screen = new FlightsScreen([Line()], "test", Now);
        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));

        flights.IsDetailOpen.ShouldBeFalse();
        flights.HandleInput(new InputEvent.KeyDown(InputKey.Enter, InputModifier.None)).ShouldBeTrue();
        flights.OpenCode.ShouldBe("TIA1611");
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain("flight:back");
        buttons.ShouldContain("pr:TIA1611");
        buttons.ShouldContain("artifact:TIA1611:render:The planets");
        buttons.ShouldContain("artifact-folder:TIA1611:render:The planets");
        buttons.ShouldContain("artifact:TIA1611:render:Preview");
        buttons.ShouldNotContain("artifact-folder:TIA1611:render:Preview");

        // A path opens its file and its folder, a URL the browser: the host's opener takes each as it is.
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "artifact:TIA1611:render:The planets" }).OnClick!.Invoke(InputModifier.None);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "artifact-folder:TIA1611:render:The planets" }).OnClick!.Invoke(InputModifier.None);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "artifact:TIA1611:render:Preview" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe([@"C:\caps\planets-2.png", @"C:\caps", "https://example.org/preview"]);

        flights.HandleInput(new InputEvent.KeyDown(InputKey.Escape, InputModifier.None)).ShouldBeTrue();
        flights.IsDetailOpen.ShouldBeFalse();
    }

    [Theory]
    [InlineData("/caps/planets.png", "/caps")]
    [InlineData(@"C:\caps\planets.png", @"C:\caps")]
    [InlineData(@"C:\caps\shots\", @"C:\caps")]
    [InlineData("planets.png", null)]
    public void AnArtifactsFolderIsFoundWhicheverSlashItsPathUses(string path, string? folder)
    {
        var leg = Legs()[0] with { Artifacts = [Shot("shot", path, 1)] };

        LegDetail.From(leg, Now).Artifacts.Single().Folder.ShouldBe(folder);
    }

    [Fact]
    public void AFlightReadyToMergeSaysSoAtTheTopOfItsPageAndItsMergeAnswersTheCardWhichThePanelLights()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        flights.CanCommand = true;
        var ready = new CardRow
        {
            Id = "c9", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/TIA1611", Title = "TIA1611's PR #7 is ready to merge",
            At = Now, State = CardState.Open, Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1611", Args = ["abc1234"] }],
        };
        var other = ready with { Id = "c8", Subject = "flight/TIA9", Title = "TIA9's PR #3 is ready to merge" };
        (CardRow Card, int Number)? answered = null;
        chrome.Cards.OnAnswer = (card, number) => answered = (card, number);
        chrome.Show(PilotPage.Flights);
        flights.OpenDetail("TIA1611");

        chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([Line()], "test", Now), CardsScreen.From([ready, other]), new RectF32(0, 0, 1600, 900));

        // The panel lights the open flight's card, not the other's.
        chrome.Cards.Focus.ShouldBe("TIA1611");
        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:merge-card" }).OnClick!.Invoke(InputModifier.None);
        answered.ShouldBe((ready, 1));

        // While that Merge is with the node, the page's button reads "Merge …" and takes no second press, as the panel's does.
        chrome.Cards.NoteWorking("c9", "card:c9:1");
        chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([Line()], "test", Now), CardsScreen.From([ready, other]), new RectF32(0, 0, 1600, 900));
        regions.Clear();
        flights.CollectPaintedRegions(regions);
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:merge-card" }).ShouldBeFalse();
        chrome.Cards.NoteWorking("c9", null);
    }

    [Fact]
    public void CapturesOfTheFlightPagesMergeIdleAndPendingAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var ready = new CardRow
        {
            Id = "c9", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/TIA1611", Title = "TIA1611's PR #7 is ready to merge",
            At = Now, State = CardState.Open, Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1611", Args = ["abc1234"], Status = ChoiceStatus.Passing }],
        };
        foreach (var (width, height) in new[] { (900, 900), (1800, 900) })
        {
            foreach (var pending in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                chrome.Flights.CanCommand = true;
                chrome.Show(PilotPage.Flights);
                chrome.Flights.OpenDetail("TIA1611");
                chrome.Cards.NoteWorking("c9", pending ? "card:c9:1" : null);
                chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([Line()], "test", Now), CardsScreen.From([ready]), new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"flight-merge-{(pending ? "pending" : "idle")}-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }

    [Fact]
    public void TheArrowAndPageKeysScrollAnOpenPageAndHomeGoesBackToTheTop()
    {
        using var renderer = new RgbaImageRenderer(1280, 260);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        var screen = new FlightsScreen([Line()], "test", Now);
        chrome.Show(PilotPage.Flights);
        flights.OpenDetail("TIA1611");
        // A page short enough that the second leg is below the fold.
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 260));
        flights.DetailOffset.ShouldBe(0);

        bool Press(InputKey key) => flights.HandleInput(new InputEvent.KeyDown(key, InputModifier.None));

        Press(InputKey.Up).ShouldBeTrue();
        flights.DetailOffset.ShouldBe(0);
        Press(InputKey.Down).ShouldBeTrue();
        var line = flights.DetailOffset;
        line.ShouldBeGreaterThan(0);
        Press(InputKey.PageDown).ShouldBeTrue();
        flights.DetailOffset.ShouldBeGreaterThan(line);
        Press(InputKey.PageUp).ShouldBeTrue();
        Press(InputKey.Home).ShouldBeTrue();
        flights.DetailOffset.ShouldBe(0);
        Press(InputKey.End).ShouldBeTrue();
        flights.DetailOffset.ShouldBeGreaterThan(line);
        Press(InputKey.Escape).ShouldBeTrue();
        flights.IsDetailOpen.ShouldBeFalse();
    }

    /// <summary>
    /// A flight's page says who works on it now and opens that session's transcript; one with no legs and nothing running
    /// says it is not planned yet and offers Plan it, where "flying" alone made it look busy (PIL208, 2026-10-24).
    /// </summary>
    [Fact]
    public void ThePageSaysWhoWorksOnTheFlightOrThatItIsNotPlannedYet()
    {
        using var vessel = new RgbaImageRenderer(1280, 800);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        flights.CanCommand = true;
        var acted = new List<FlightAction>();
        var opened = new List<string>();
        flights.OnAction = acted.Add;
        flights.OnSession = (row, _) => opened.Add(row.Id);
        List<ClickableRegion> Open(FlightsScreen screen)
        {
            flights.Render(screen, new RectF32(0, 0, 1280, 800));
            flights.OpenDetail("TIA1611");
            flights.Render(screen, new RectF32(0, 0, 1280, 800));
            var regions = new List<ClickableRegion>();
            flights.CollectPaintedRegions(regions);
            return regions;
        }

        var unplanned = Open(new FlightsScreen([FlightLine.Build([Flight()], [], Now, []).Single()], "live", Now));
        unplanned.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:plan" }).OnClick!.Invoke(InputModifier.None);
        acted.Single().ShouldSatisfyAllConditions(a => a.Verb.ShouldBe(Verbs.PlanFlight), a => a.Target.ShouldBe("flight/TIA1611"));

        flights.CloseDetail();
        var worker = Live.Row("wwww0001", "du1-tia1611-render", SessionState.Working) with { Role = SessionRole.Worker, Flight = "TIA1611", Leg = "render" };
        // Idle beside it: the planner long done and a validator between runs, neither working on it now (PIL208's page).
        var planner = Live.Row("pppp0001", "du1-tia1611-plan", SessionState.Idle) with { Role = SessionRole.Planner, Flight = "TIA1611" };
        var idleValidator = Live.Row("vvvv0001", "du1-tia1611-render-validator", SessionState.Idle) with { Role = SessionRole.Worker, Flight = "TIA1611", Leg = "render-validator" };
        var line = FlightLine.Build([Flight()], [worker, planner, idleValidator], Now, Legs()).Single();
        line.Working.Select(s => s.Id).ShouldBe(["wwww0001"]);
        var flying = Open(new FlightsScreen([line], "live", Now));
        flying.Any(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:plan" }).ShouldBeFalse();
        flying.Count(r => r.Result is HitResult.ButtonHit { Action: var action } && action.Contains(":session:", StringComparison.Ordinal)).ShouldBe(1);
        flying.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:session:wwww0001" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["wwww0001"]);
    }

    [Fact]
    public void AValidatorAtWorkSaysWhatItValidatesAndEachTranscriptButtonSaysWhose()
    {
        using var vessel = new RgbaImageRenderer(1280, 800);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        flights.OnSession = (_, _) => { };
        var worker = Live.Row("wwww0001", "du1-tia1611-render", SessionState.Waiting) with { Role = SessionRole.Worker, Flight = "TIA1611", Leg = "render" };
        var validator = Live.Row("vvvv0001", "du1-tia1611-render-validator", SessionState.Working) with { Role = SessionRole.Worker, Flight = "TIA1611", Leg = "render-validator" };
        var screen = new FlightsScreen([FlightLine.Build([Flight()], [worker, validator], Now, Legs()).Single()], "live", Now);

        flights.Tree(screen);
        flights.OpenDetail("TIA1611");
        var texts = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, 1280, 800), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value).ToList();

        texts.ShouldContain("validating render · du1-tia1611-render-validator · working");
        texts.ShouldContain("flying render · du1-tia1611-render · waiting");
        texts.ShouldContain("Validator's transcript");
        texts.ShouldContain("Worker's transcript");
        texts.ShouldNotContain("Its transcript");
    }

    /// <summary>
    /// A goal that wraps over several lines pushes its Done when down: the label and its text are measured at the width the
    /// text really gets. Laid out as a stack, the text was measured with the label's room too, came out a line short, and
    /// its last line ran over the row under it (PIL189's page, SE1 2026-10-24).
    /// </summary>
    [Theory]
    [InlineData(900f, 1f)]
    [InlineData(1900f, 1f)]
    [InlineData(1900f, 1.25f)]
    [InlineData(1900f, 1.5f)]
    [InlineData(1300f, 1.5f)]
    public void ScrolledToItsEndThePageShowsTheLastLegsReportToItsLastWord(float width, float dpi)
    {
        // PIL294's page at its end cut the last leg's report off at the window's bottom.
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        string Long(string tail) => string.Join(" ", Enumerable.Repeat("words that wrap over several lines", 12)) + " " + tail;
        var legs = Enumerable.Range(1, 4).Select(i => Legs()[1] with
        {
            Name = $"leg{i}", Order = i, Goal = Long("G"), DoneWhen = Long("D"), Report = Long(i == 4 ? "LASTWORD" : "R"),
            Checklist = [.. Enumerable.Range(1, 5).Select(s => new CheckItem { Text = Long("S"), Done = true })],
        }).ToArray();
        var screen = new FlightsScreen([FlightLine.Build([Flight()], [], Now, legs).Single()], "live", Now);
        flights.OpenDetail("TIA1611");
        var window = new Rect<float>(0, 0, width, 700);
        var pixels = new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, dpi);

        Layout.Engine.Arrange(flights.Tree(screen), window, pixels);
        flights.HandleInput(new InputEvent.KeyDown(InputKey.End, InputModifier.None)).ShouldBeTrue();
        var arranged = Layout.Engine.Arrange(flights.Tree(screen), window, pixels);

        var last = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.StartsWith("LASTWORD", StringComparison.Ordinal)).Bounds;
        (last.Y + last.Height).ShouldBeLessThanOrEqualTo(window.Height, "the last word is inside the window at the scroll's end");
    }

    [Fact]
    public void AWrappedGoalPushesItsDoneWhenDownRatherThanRunningOverIt()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Flights;
        var goal = string.Join(" ", Enumerable.Repeat("the goal goes on", 30)) + " LASTWORD";
        var legs = new[] { Legs()[1] with { Goal = goal } };
        var screen = new FlightsScreen([FlightLine.Build([Flight()], [], Now, legs).Single()], "live", Now);
        flights.Tree(screen);
        flights.OpenDetail("TIA1611");

        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<int>(0, 0, 90, 200), CellMeasureContext.PixelAuthored);
        Rect<int> Of(Func<string, bool> text) => arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && text(v)).Bounds;

        var last = Of(v => v.StartsWith("LASTWORD", StringComparison.Ordinal));
        Of(v => v == "Done when").Y.ShouldBeGreaterThanOrEqualTo(last.Y + last.Height, "Done when starts under the goal's last line");
    }

    [Fact]
    public void ThePageLandsInTerminalCellsWithEveryLegsWords()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Flights;
        var screen = new FlightsScreen([Line()], "live", Now);
        flights.Tree(screen);
        flights.OpenDetail("TIA1611");

        var described = CellLayout.Describe(Layout.Engine.Arrange(flights.Tree(screen), new Rect<int>(0, 0, 160, 60), CellMeasureContext.PixelAuthored));

        described.ShouldContain("← Back");
        described.ShouldContain("1. parser");
        described.ShouldContain("2. render");
        described.ShouldContain("1/3 steps");
        described.ShouldContain("go-around 1");
        described.ShouldContain("[x]");
        described.ShouldContain("shader");
        described.ShouldContain("Done when");
        described.ShouldContain("The planets");
    }

    [Fact]
    public void ACaptureOfTheFlightsPageCanOpenAFlightsPage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var png = Path.Combine(folder, "flight.png");
            var screen = new FlightsScreen([Line()], "test", Now);

            var rows = SessionsCapture.Write(new SessionsScreen([], "test", Now), png, 1280, 800, flights: screen, page: PilotPage.Flights, openFlight: "TIA1611");

            File.Exists(png).ShouldBeTrue();
            File.ReadAllText(rows).ShouldContain("   [ ] shader");
            GuiOptions.Parse(["--capture", png, "--page", "flights", "--flight", "TIA1611"], out _).ShouldNotBeNull().Flight.ShouldBe("TIA1611");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFlightTheNodeForgetsClosesItsPage()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Flights;
        flights.Tree(new FlightsScreen([Line()], "live", Now));
        flights.OpenDetail("TIA1611");

        flights.Tree(new FlightsScreen([], "live", Now));

        flights.IsDetailOpen.ShouldBeFalse();
    }

    private static ReviewRow Reviewed(string status, string label, string? url = "https://github.com/x/tianwen/pull/7#pullrequestreview-1") => new()
    {
        State = status == "failure" ? ReviewState.Changes : ReviewState.Passed,
        Reviewer = "r",
        StartedAt = Live.At,
        ReviewedHead = "cafe0000aaaa",
        StatusState = status,
        Label = label,
        ReviewUrl = url,
    };

    // The page's texts and clickable regions: the flight's own page when open, else the table with the selected flight's panel.
    private static (List<string> Texts, List<ClickableRegion> Regions) PageOf(FlightRow flight, bool open, Action<string>? onOpen = null)
    {
        using var vessel = new RgbaImageRenderer(1600, 800);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        flights.OnOpen = onOpen;
        var screen = new FlightsScreen([FlightLine.Build([flight], [], Now, Legs()).Single()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, 1600, 800));
        if (open)
        {
            flights.OpenDetail("TIA1611");
        }
        flights.Render(screen, new RectF32(0, 0, 1600, 800));
        var texts = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, 1600, 800), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value).ToList();
        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        return (texts, regions);
    }

    /// <summary>The page and the selected flight's panel both say the pilot/review status and the pilot: label as the node set them, and link the review comment (#222).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APassedReviewShowsItsStatusItsLabelAndALinkToTheComment(bool page)
    {
        var flight = Flight() with { Review = Reviewed("success", "pilot: passed"), GitHubHead = "cafe0000aaaa", GitHubLabel = "pilot: passed" };
        var opened = new List<string>();

        var (texts, regions) = PageOf(flight, page, opened.Add);

        texts.ShouldContain("pilot/review passed · pilot: passed");
        texts.ShouldNotContain(t => t.Contains("GitHub:", StringComparison.Ordinal));
        // The comment's page opens as the PR's does: through the host's opener.
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "review:TIA1611" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["https://github.com/x/tianwen/pull/7#pullrequestreview-1"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReviewWithChangesShowsItsFailureAsChanges(bool page)
    {
        var (texts, _) = PageOf(Flight() with { Review = Reviewed("failure", "pilot: changes") }, page);

        texts.ShouldContain("pilot/review changes · pilot: changes");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHeadThatMovedSinceTheReviewIsFlaggedInWords(bool page)
    {
        var flight = Flight() with
        {
            Review = Reviewed("success", "pilot: passed"),
            GitHubHead = "beef1111bbbb",
            GitHubDisagrees = "the head moved to beef111 since the review read cafe000",
        };

        var (texts, _) = PageOf(flight, page);

        texts.ShouldContain("⚠ GitHub: the head moved to beef111 since the review read cafe000");
    }

    [Fact]
    public void AMovedHeadIsFlaggedFromTheTwoHeadsWhenTheNodeHasNotSaidWhy()
    {
        var flight = Flight() with { Review = Reviewed("success", "pilot: passed"), GitHubHead = "beef1111bbbb" };

        FlightMarks.From(flight).Warning.ShouldBe("the head moved to beef111 since the review read cafe000");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALabelRemovedByHandIsFlaggedInWordsAndTheNodesLabelStillShows(bool page)
    {
        var flight = Flight() with
        {
            Review = Reviewed("success", "pilot: passed"),
            GitHubHead = "cafe0000aaaa",
            GitHubLabel = null,
            GitHubDisagrees = "pilot: passed was removed on GitHub",
        };

        var (texts, _) = PageOf(flight, page);

        texts.ShouldContain("pilot/review passed · pilot: passed");
        texts.ShouldContain("⚠ GitHub: pilot: passed was removed on GitHub");
    }

    [Fact]
    public void AConflictOrQueueLabelShowsInPlaceOfTheReviewsAndARefusedWriteIsSaid()
    {
        var review = Reviewed("success", "pilot: passed") with { LabelOverride = "pilot: conflicts", MarksWhy = "the label: 403" };

        var marks = FlightMarks.From(Flight() with { Review = review });

        marks.Line.ShouldBe("pilot/review passed · pilot: conflicts");
        marks.Pending.ShouldBe("still to set on GitHub: the label: 403");
    }

    [Fact]
    public void AFlightWithNoMarksSaysNothingOfThem()
    {
        var review = Reviewed("success", "pilot: passed") with { StatusState = null, Label = null, ReviewUrl = null };

        var (texts, regions) = PageOf(Flight() with { Review = review }, true);

        texts.ShouldNotContain(t => t.Contains("pilot/review", StringComparison.Ordinal));
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: "review:TIA1611" }).ShouldBeFalse();
    }

    [Fact]
    public void AFlyingLegWaitingOnAFileSaysWhatItWaitsFor()
    {
        var legs = Legs();
        legs[0] = legs[0] with { Wait = new LegWait { Path = @"C:\x\done.txt", Since = Live.At } };
        var line = FlightLine.Build([Flight()], [], Now, legs).Single();

        FlightDetail.From(line, Now).Legs.Single(l => l.Name == "render").State.ShouldBe(@"waiting for C:\x\done.txt");
    }
}
