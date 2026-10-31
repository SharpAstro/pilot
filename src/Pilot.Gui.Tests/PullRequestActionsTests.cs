using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A PR row's right-hand actions: only those that fit its state (Merge on a flight's ready PR, Review on one pilot has not reviewed,
/// Go around on a flight's conflicting one, Open on every row), in slots that keep the row's height and the columns where they are;
/// each press sends the node what the flight's card's button sends; a refusal is said in the node's words and the row is changed
/// by nothing but the node's next answer.
/// </summary>
public sealed class PullRequestActionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private const string Head = "3021bcecd64a831400e54f191a11274ab26597a6";

    private static PullRequestRow Pr(string repo, int number, PullRequestKind kind, string label, string? flight = null, string? review = null, string title = "A change") => new()
    {
        Repo = repo, Number = number, Title = title, Url = $"https://github.com/{repo}/pull/{number}", Author = "sebgod", Kind = kind, Label = label,
        Flight = flight, Review = review, ReviewUrl = review is null ? null : $"https://github.com/{repo}/pull/{number}#pullrequestreview-1",
        UpdatedAt = Now - TimeSpan.FromHours(3), CreatedAt = Now - TimeSpan.FromDays(2), Head = flight is null ? "feature/x" : $"flight/{flight.ToLowerInvariant()}", Base = "main",
    };

    private static CardRow MergeCard(string flight, string id = "c1", string? status = null) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "pr-ready", Subject = Verbs.FlightTarget(flight), Title = $"{flight}'s PR is ready to merge", At = Now, State = CardState.Open,
        Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight), Args = [Head], Status = status }],
    };

    private static CardRow AroundCard(string flight, string id = "c2") => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "review-changes", Subject = Verbs.FlightTarget(flight), Title = $"{flight}'s PR conflicts with main", At = Now, State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = "Go around parser", Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight, "parser"), Args = ["resolve the conflicts"] },
            new CardChoice { Label = "Land the PR with the review's findings unfixed", Verb = Verbs.Review, Target = Verbs.FlightTarget(flight), Args = ["pass", "ready anyway"] },
        ],
    };

    private static PullRequestKind[] AllKinds => [.. Enum.GetValues<PullRequestKind>()];

    private static PullRequestActionKind[] Kinds(PullRequestRow pr, params CardRow[] cards) => [.. PullRequestActions.Offered(pr, cards).Select(a => a.Kind)];

    [Fact]
    public void MergeIsOfferedOnAFlightsReadyPRAndOnOneWhoseChecksRunButOnNothingElse()
    {
        var cards = new[] { MergeCard("PIL252") };
        Kinds(Pr("SharpAstro/pilot", 252, PullRequestKind.Ready, "Ready", "PIL252", PullRequestReview.Passed), cards).ShouldBe([PullRequestActionKind.Merge, PullRequestActionKind.Open]);

        // Checks running: the card's Merge arms it, and says so.
        var running = PullRequestActions.Offered(Pr("SharpAstro/pilot", 252, PullRequestKind.ChecksRunning, "Checks running 4/7", "PIL252", PullRequestReview.Passed), [MergeCard("PIL252", status: ChoiceStatus.Pending)]);
        running.Select(a => a.Kind).ShouldBe([PullRequestActionKind.Merge, PullRequestActionKind.Open]);
        running[0].Status.ShouldBe(ChoiceStatus.Pending);
        running[0].Tooltip.ShouldContain("arms the merge");

        // Waiting for a required check is the card's pending too: Merge arms it.
        var awaiting = PullRequestActions.Offered(Pr("SharpAstro/pilot", 252, PullRequestKind.AwaitingCheck, "Waiting for a required check", "PIL252", PullRequestReview.Passed), [MergeCard("PIL252", status: ChoiceStatus.Pending)]);
        awaiting.Select(a => a.Kind).ShouldBe([PullRequestActionKind.Merge, PullRequestActionKind.Open]);
        awaiting[0].Status.ShouldBe(ChoiceStatus.Pending);

        // Not on any other state of a flight's PR, card or no card.
        foreach (var kind in AllKinds.Where(k => k is not (PullRequestKind.Ready or PullRequestKind.ChecksRunning or PullRequestKind.AwaitingCheck)))
        {
            Kinds(Pr("SharpAstro/pilot", 252, kind, kind.ToString(), "PIL252", PullRequestReview.Passed), cards).ShouldNotContain(PullRequestActionKind.Merge, kind.ToString());
        }

        // Not on a PR that is no flight's, nor a flight's whose card the node has not raised (it is not reviewed yet), nor another flight's card.
        Kinds(Pr("SharpAstro/pilot", 7, PullRequestKind.Ready, "Ready", review: PullRequestReview.Passed), cards).ShouldBe([PullRequestActionKind.Open]);
        Kinds(Pr("SharpAstro/pilot", 252, PullRequestKind.Ready, "Ready", "PIL252", PullRequestReview.Passed)).ShouldBe([PullRequestActionKind.Open]);
        Kinds(Pr("SharpAstro/pilot", 253, PullRequestKind.Ready, "Ready", "PIL253", PullRequestReview.Passed), cards).ShouldBe([PullRequestActionKind.Open]);
        // A closed card is no button.
        Kinds(Pr("SharpAstro/pilot", 252, PullRequestKind.Ready, "Ready", "PIL252", PullRequestReview.Passed), MergeCard("PIL252") with { State = CardState.Closed }).ShouldBe([PullRequestActionKind.Open]);
    }

    [Fact]
    public void GoAroundIsOfferedOnAFlightsConflictingPRWhoseCardAsksForItAndOnNothingElse()
    {
        var cards = new[] { AroundCard("TIA1799") };
        var conflicts = Pr("SharpAstro/tianwen", 1799, PullRequestKind.Conflicts, "Conflicts", "TIA1799", PullRequestReview.Changes);
        var offered = PullRequestActions.Offered(conflicts, cards);
        offered.Select(a => a.Kind).ShouldBe([PullRequestActionKind.GoAround, PullRequestActionKind.Open]);
        // The first choice of the card whose verb is go-around, as its own button numbers it.
        (offered[0].Card?.Id, offered[0].Choice).ShouldBe(("c2", 1));

        foreach (var kind in AllKinds.Where(k => k != PullRequestKind.Conflicts))
        {
            Kinds(conflicts with { Kind = kind, Label = kind.ToString() }, cards).ShouldNotContain(PullRequestActionKind.GoAround, kind.ToString());
        }
        // Not a flight's: no card to press, and no Go around (its review says changes, so a plain PR gets Address review instead).
        Kinds(conflicts with { Flight = null }, cards).ShouldNotContain(PullRequestActionKind.GoAround);
        Kinds(conflicts).ShouldBe([PullRequestActionKind.Open]);
    }

    // #536, 2026-10-31: Review on PIL525's draft PR was pressed and nothing showed: it sent `pilot review pr`, which the node refuses
    // on a flight's PR. A draft flight PR says it is in flight; one marked ready offers Review, and that sends the flight's own review.
    [Fact]
    public void AFlightsDraftSaysInFlightAndItsReadyPrsReviewIsTheFlightsOwn()
    {
        var flying = Pr("SharpAstro/pilot", 536, PullRequestKind.ChecksRunning, "Checks running", flight: "PIL525") with { Draft = true };
        Kinds(flying).ShouldNotContain(PullRequestActionKind.Review);
        PullRequestActions.FlightReviewSaid(flying)!.Value.Label.ShouldBe("In flight");
        PullRequestActions.FlightReviewSaid(flying with { Review = PullRequestReview.Passed }).ShouldBeNull();
        PullRequestActions.FlightReviewSaid(flying with { Flight = null }).ShouldBeNull();

        var ready = flying with { Draft = false };
        PullRequestActions.FlightReviewSaid(ready).ShouldBeNull();
        Kinds(ready).ShouldContain(PullRequestActionKind.Review);
        var sent = DashboardCommands.ReviewPrRequest(ready, "gui");
        sent.Verb.ShouldBe(Verbs.StartReview);
        sent.Target.ShouldBe(Verbs.FlightTarget("PIL525"));
        DashboardCommands.ReviewPrRequest(ready with { Flight = null }, "gui").Verb.ShouldBe(Verbs.ReviewPr);
    }

    [Fact]
    public void ReviewIsOfferedWherePilotsReviewIsNoneAndNowhereElseAndOpenIsOnEveryRow()
    {
        foreach (var kind in AllKinds)
        {
            var unreviewed = Pr("SharpAstro/pilot", 9, kind, kind.ToString());
            Kinds(unreviewed).ShouldBe([PullRequestActionKind.Review, PullRequestActionKind.Open], kind.ToString());
            foreach (var review in new[] { PullRequestReview.Passed, PullRequestReview.Reviewing })
            {
                Kinds(unreviewed with { Review = review }).ShouldBe([PullRequestActionKind.Open], $"{kind} {review}");
            }
            // Changes: not Review again, but the findings fixed.
            Kinds(unreviewed with { Review = PullRequestReview.Changes }).ShouldBe([PullRequestActionKind.AddressReview, PullRequestActionKind.Open], kind.ToString());
        }
    }

    private static CardRow FindingsCard(string flight, string id = "c4", string words = "Fix the review's findings", string kind = "review-changes") => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = kind, Subject = Verbs.FlightTarget(flight), Title = $"{flight}'s review asks for changes", At = Now, State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = "Land the PR with the review's findings unfixed", Verb = Verbs.Review, Target = Verbs.FlightTarget(flight), Args = ["pass", "ready anyway"] },
            new CardChoice { Label = words, Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight, "parser"), Args = ["the review asked for changes: x"] },
        ],
    };

    [Fact]
    public void AddressReviewOnAFlightsPRPressesItsReviewChangesCardsGoAroundNotAConflictCards()
    {
        var pr = Pr("SharpAstro/tianwen", 1799, PullRequestKind.ChecksFailed, "Checks failed", "TIA1799", PullRequestReview.Changes);
        var offered = PullRequestActions.Offered(pr, [AroundCard("TIA1799"), FindingsCard("TIA1799")]);
        offered.Select(a => a.Kind).ShouldBe([PullRequestActionKind.AddressReview, PullRequestActionKind.Open]);
        (offered[0].Card?.Id, offered[0].Choice).ShouldBe(("c4", 2));
        offered[0].Tooltip.ShouldBe("Send TIA1799's last leg round on the review's findings, as its card's Fix the review's findings does");

        // A card that only resolves a conflict, or no card at all: no button, and never fix-pr for a flight's PR.
        Kinds(pr, AroundCard("TIA1799")).ShouldBe([PullRequestActionKind.Open]);
        Kinds(pr).ShouldBe([PullRequestActionKind.Open]);
        Kinds(pr, FindingsCard("TIA1799") with { State = CardState.Closed }).ShouldBe([PullRequestActionKind.Open]);
        Kinds(pr with { Review = PullRequestReview.Passed }, FindingsCard("TIA1799")).ShouldBe([PullRequestActionKind.Open]);
    }

    [Fact]
    public async Task AddressReviewOnAFlightsPRAnswersTheCardAndOnAPlainPRSendsFixPr()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var answered = new List<string>();
        page.OnAnswer = (card, number) => answered.Add($"{card.Id}:{number}");
        var sent = new List<string>();
        page.OnFixReview = (pr, _) =>
        {
            var request = DashboardCommands.FixPrRequest(pr, "gui");
            sent.Add($"{request.Verb} {request.Target} {request.Who}");
            return Task.CompletedTask;
        };
        var flight = Pr("SharpAstro/pilot", 460, PullRequestKind.Behind, "Behind", "PIL460", PullRequestReview.Changes);
        var plain = Pr("SharpAstro/DIR.Lib", 301, PullRequestKind.Behind, "Behind", review: PullRequestReview.Changes);
        _ = page.Tree(Screen(Row() with { PullRequests = [flight, plain] }, [FindingsCard("PIL460")]));

        page.Press(flight, page.ActionsOf(flight).Single(a => a.Kind == PullRequestActionKind.AddressReview));
        answered.ShouldBe(["c4:2"]);
        sent.ShouldBeEmpty();

        var action = page.ActionsOf(plain).Single(a => a.Kind == PullRequestActionKind.AddressReview);
        action.Card.ShouldBeNull();
        action.Tooltip.ShouldBe("Start a fixer on SharpAstro/DIR.Lib#301's branch with the review's findings (pilot review fix)");
        await page.FixReviewAsync(plain);
        sent.ShouldBe(["fix-pr pr/SharpAstro/DIR.Lib#301 gui"]);
        Verbs.FixPr.ShouldBe("fix-pr");
    }

    [Fact]
    public async Task AddressReviewSaysStartingThenFixingUntilAReadShowsAnotherReviewWordAndARefusalIsSaidInTheNodesWords()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var answer = new TaskCompletionSource();
        page.OnFixReview = (_, _) => answer.Task;
        var plain = Pr("SharpAstro/DIR.Lib", 301, PullRequestKind.Behind, "Behind", review: PullRequestReview.Changes);
        var screen = Screen(Row() with { PullRequests = [plain] });
        static string[] Words(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
            [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];
        Words(Arrange(renderer, page, screen)).ShouldContain("Address review");

        var press = page.FixReviewAsync(plain);
        var arranged = Arrange(renderer, page, screen);
        Words(arranged).ShouldContain("Starting fix…");
        ButtonOrNull(arranged, Id(plain, "address-review")).ShouldBeNull();

        answer.SetResult();
        await press;
        arranged = Arrange(renderer, page, screen);
        Words(arranged).ShouldContain("fixing");
        Words(arranged).ShouldNotContain("Starting fix…");
        ButtonOrNull(arranged, Id(plain, "address-review")).ShouldBeNull();

        // A read that shows another review word takes over, and the row is no longer waiting on the fixer.
        var read = Screen(screen.Row! with { PullRequests = [plain with { Review = PullRequestReview.Reviewing }] });
        Words(Arrange(renderer, page, read)).ShouldNotContain("fixing");

        // The node's refusal, in its words.
        page.OnFixReview = (_, _) => throw new InvalidOperationException("a flight owns SharpAstro/DIR.Lib#301");
        var again = Screen(Row() with { PullRequests = [plain] });
        await page.FixReviewAsync(plain);
        page.FixRefused.ShouldBe("review fix SharpAstro/DIR.Lib#301 refused: a flight owns SharpAstro/DIR.Lib#301");
        page.Refusal().ShouldBe(page.FixRefused);
        Words(Arrange(renderer, page, again)).ShouldContain(t => t.Contains("a flight owns", StringComparison.Ordinal));

        // Without a read, a taken press lapses after StartedFor and the button is back.
        page.OnFixReview = (_, _) => Task.CompletedTask;
        await page.FixReviewAsync(plain);
        page.FixRefused.ShouldBeNull();
        page.StartedFor = TimeSpan.Zero;
        ButtonOrNull(Arrange(renderer, page, again), Id(plain, "address-review")).ShouldNotBeNull();
    }

    [Fact]
    public void AHostWithNoReviewCommandHidesReviewAndTheOthersStay()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var page = chrome.PullRequests;
        var pr = Pr("SharpAstro/pilot", 9, PullRequestKind.Behind, "Behind");
        page.ActionsOf(pr).Select(a => a.Kind).ShouldBe([PullRequestActionKind.Open]);
        page.OnReview = (_, _) => Task.CompletedTask;
        page.ActionsOf(pr).Select(a => a.Kind).ShouldBe([PullRequestActionKind.Review, PullRequestActionKind.Open]);
    }

    // A window on the page with the rows below: a ready flight PR (Merge), a conflicting one (Go around), a plain PR no one has reviewed (Review), one reviewed (Open only).
    private static PullRequestsRow Row() => new()
    {
        Orgs = ["SharpAstro"],
        ReadAt = Now - TimeSpan.FromSeconds(40),
        Viewer = "sebgod",
        PullRequests =
        [
            Pr("SharpAstro/pilot", 252, PullRequestKind.Ready, "Ready", "PIL252", PullRequestReview.Passed, "Pull requests page: every open PR across the orgs"),
            Pr("SharpAstro/pilot", 261, PullRequestKind.ChecksRunning, "Checks running 4/7", "PIL261", PullRequestReview.Passed, "Checks while the next leg flies"),
            Pr("SharpAstro/pilot", 258, PullRequestKind.InMergeQueue, "In merge queue #2", review: PullRequestReview.Passed, title: "Bump the lexer"),
            Pr("SharpAstro/tianwen", 1799, PullRequestKind.Conflicts, "Conflicts", "TIA1799", PullRequestReview.Changes, "Plate solving: a fallback when the catalogue is missing"),
            Pr("SharpAstro/tianwen", 1820, PullRequestKind.ChecksFailed, "Checks failed: build-and-test (ubuntu-latest, net10.0)", title: "Rework the whole of the plate solver's catalogue loader so that a very long title still gets its ellipsis"),
            Pr("SharpAstro/DIR.Lib", 301, PullRequestKind.Behind, "Behind", title: "A text input that scrolls"),
            Pr("sebgod/notes", 12, PullRequestKind.Draft, "Draft", title: "Draft: notes on the dome"),
        ],
    };

    private static CardRow[] Cards => [MergeCard("PIL252", "c1", ChoiceStatus.Passing), MergeCard("PIL261", "c3", ChoiceStatus.Pending), AroundCard("TIA1799")];

    private static PullRequestsScreen Screen(PullRequestsRow? row = null, IReadOnlyList<CardRow>? cards = null) =>
        new(row ?? Row(), "live from the node on node.sock", Now) { OpenCards = cards ?? Cards };

    private static (PilotChrome<RgbaImage> Chrome, PullRequestsView<RgbaImage> Page) Window(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.PullRequests.OnRefresh = _ => Task.CompletedTask;
        chrome.PullRequests.OnReview = (_, _) => Task.CompletedTask;
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        return (chrome, chrome.PullRequests);
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(RgbaImageRenderer renderer, PullRequestsView<RgbaImage> page, PullRequestsScreen screen, float width = 1280f, float height = 900f) =>
        Layout.Engine.Arrange(page.Tree(screen, width), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

    private static Rect<float>? ButtonOrNull(ImmutableArray<Layout.ArrangedNode<float>> arranged, string action) =>
        arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action).Select(a => (Rect<float>?)a.Bounds).SingleOrDefault();

    private static Rect<float> Button(ImmutableArray<Layout.ArrangedNode<float>> arranged, string action) => ButtonOrNull(arranged, action).ShouldNotBeNull(action);

    private static Rect<float> RowOf(ImmutableArray<Layout.ArrangedNode<float>> arranged, PullRequestsView<RgbaImage> page, PullRequestRow pr) =>
        arranged.First(a => a.Node.Hit is HitResult.ListItemHit h && h.Index == page.Rows.ToList().FindIndex(r => r.Url == pr.Url)).Bounds;

    private static void Click(ImmutableArray<Layout.ArrangedNode<float>> arranged, string action) =>
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action).Node.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

    private static string Id(PullRequestRow pr, string action) => $"pr:{pr.Url}:{action}";

    [Theory]
    [InlineData(1280f)]
    [InlineData(2000f)]
    public void EachRowShowsTheButtonsItsStateOffersInFixedSlotsSoRowsAndColumnsHoldWithAndWithoutThem(float width)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 1100);
        var (_, page) = Window(renderer);
        var screen = Screen();
        var arranged = Arrange(renderer, page, screen, width, 1100f);
        var prs = screen.Row!.PullRequests;
        PullRequestRow P(int number) => prs.Single(p => p.Number == number);

        // Which buttons each row has, by state.
        string[] Has(PullRequestRow pr) => [.. new[] { "merge", "go-around", "start-review", "open" }.Where(a => ButtonOrNull(arranged, Id(pr, a)) is not null)];
        Has(P(252)).ShouldBe(["merge", "open"]);
        Has(P(261)).ShouldBe(["merge", "open"]);
        Has(P(1799)).ShouldBe(["go-around", "open"]);
        Has(P(258)).ShouldBe(["open"]);
        Has(P(1820)).ShouldBe(["start-review", "open"]);
        Has(P(301)).ShouldBe(["start-review", "open"]);
        Has(P(12)).ShouldBe(["start-review", "open"]);

        // Every row is one height, with the buttons and without.
        var rows = arranged.Where(a => a.Node.Hit is HitResult.ListItemHit).Select(a => a.Bounds).ToList();
        rows.Count.ShouldBe(prs.Length);
        rows.Select(r => r.Height).Distinct().ShouldBe([PullRequestsView<RgbaImage>.RowHeight]);

        // One slot, one x and one width, on every row that has it: the card's button, Review and Open.
        (float X, float Width)[] Slot(params string[] actions) =>
            [.. prs.SelectMany(pr => actions.Select(a => ButtonOrNull(arranged, Id(pr, a)))).OfType<Rect<float>>().Select(b => (b.X, b.Width)).Distinct()];
        Slot("merge", "go-around").Length.ShouldBe(1, "the card's slot");
        Slot("start-review").Length.ShouldBe(1, "the review slot");
        Slot("open").Length.ShouldBe(1, "the open slot");

        // Every button is a chip tall, in the row's middle, inside the row and inside the actions column; Open ends at the row's right edge less its padding.
        foreach (var pr in prs)
        {
            var row = RowOf(arranged, page, pr);
            foreach (var action in new[] { "merge", "go-around", "start-review", "open" })
            {
                if (ButtonOrNull(arranged, Id(pr, action)) is not { } button)
                {
                    continue;
                }
                button.Height.ShouldBe(PullRequestsView<RgbaImage>.ChipHeight, $"{pr.Number} {action}");
                (button.Y + button.Height / 2f).ShouldBe(row.Y + row.Height / 2f, 1.5f, $"{pr.Number} {action}");
                button.X.ShouldBeGreaterThanOrEqualTo(row.X + row.Width - PullRequestsView<RgbaImage>.ActionsWidth - ListTable.EndPad - 1f);
                (button.X + button.Width).ShouldBeLessThanOrEqualTo(row.X + row.Width + 0.5f);
            }
            // The title stops before the actions column, so a long one is cut there, never under the buttons.
            var title = Button(arranged, $"pr:{pr.Url}");
            (title.X + title.Width).ShouldBeLessThanOrEqualTo(Button(arranged, Id(pr, "open")).X);
        }

        // The slots do not overlap one another.
        foreach (var pr in prs)
        {
            var placed = new[] { "merge", "go-around", "start-review", "open" }.Select(a => ButtonOrNull(arranged, Id(pr, a))).OfType<Rect<float>>().OrderBy(b => b.X).ToList();
            for (var i = 1; i < placed.Count; i++)
            {
                (placed[i - 1].X + placed[i - 1].Width).ShouldBeLessThan(placed[i].X);
            }
        }

        // The columns before the actions are where they are without any button: the same page with no cards and no review command.
        page.OnReview = null;
        var bare = Arrange(renderer, page, Screen(cards: []), width, 1100f);
        foreach (var pr in prs)
        {
            Button(bare, $"pr:{pr.Url}").ShouldBe(Button(arranged, $"pr:{pr.Url}"));
            Button(bare, Id(pr, "open")).ShouldBe(Button(arranged, Id(pr, "open")));
            RowOf(bare, page, pr).ShouldBe(RowOf(arranged, page, pr));
        }
    }

    [Fact]
    public void TheButtonsWordsAreOnesTheirTooltipsExplainAndNothingIsCut()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var screen = Screen();
        var arranged = Arrange(renderer, page, screen);
        string Tooltip(PullRequestRow pr, string action) => arranged.Single(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == Id(pr, action)).Node.Tooltip.ShouldNotBeNull();
        Tooltip(screen.Row!.PullRequests[0], "merge").ShouldContain("SharpAstro/pilot#252");
        Tooltip(screen.Row.PullRequests[1], "merge").ShouldContain("checks are still running");
        Tooltip(screen.Row.PullRequests[3], "go-around").ShouldContain("TIA1799");
        Tooltip(screen.Row.PullRequests[4], "start-review").ShouldContain("pilot review pr");
        Tooltip(screen.Row.PullRequests[4], "open").ShouldContain("SharpAstro/tianwen#1820");

        // Each word fits its slot at the size it is drawn at, so none is cut.
        foreach (var (word, slot) in new[] { ("Merge", 80f), ("Go around", 80f), ("Review", 64f), ("Open ↗", 64f) })
        {
            renderer.MeasureText(word, SessionsCapture.FontPath, 12f).Width.ShouldBeLessThan(slot - 8f, word);
        }
    }

    [Fact]
    public void AMergeAndAGoAroundPressSendTheNodeWhatTheFlightsCardsOwnButtonSends()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Window(renderer);
        var answered = new List<(string Id, int Number)>();
        chrome.Cards.OnAnswer = (card, number) => answered.Add((card.Id, number));
        var screen = Screen();
        var cards = CardsScreen.From(screen.OpenCards);

        // The card's own buttons, in the panel: what they say to the node is what a click on them sends.
        var panel = Layout.Engine.Arrange(chrome.Cards.Build(cards), new Rect<float>(0, 0, CardsView<RgbaImage>.Width, 1400f), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        Click(panel, "card:c1:1");
        Click(panel, "card:c3:1");
        Click(panel, "card:c2:1");
        var fromCards = answered.ToList();
        fromCards.ShouldBe([("c1", 1), ("c3", 1), ("c2", 1)]);
        answered.Clear();

        // The rows' buttons: through the same hook, so the same card and the same choice.
        var arranged = Arrange(renderer, page, screen);
        var prs = screen.Row!.PullRequests;
        Click(arranged, Id(prs[0], "merge"));
        Click(arranged, Id(prs[1], "merge"));
        Click(arranged, Id(prs[3], "go-around"));
        answered.ShouldBe(fromCards);

        // And what the host sends for that is one builder's: the card's target with the choice's number, from the host.
        var request = DashboardCommands.AnswerRequest(cards.Open.First(c => c.Id == "c3"), 1, "gui");
        (request.Verb, request.Target, request.Who).ShouldBe((Verbs.Answer, "card/c3", "gui"));
        request.Args.ShouldBe(["1"]);
    }

    [Fact]
    public void AReviewPressSendsWhatPilotReviewPrSendsAndAnOpenPressOpensThePR()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var reviewed = new List<string>();
        page.OnReview = (pr, _) =>
        {
            var request = DashboardCommands.ReviewPrRequest(pr, "gui");
            reviewed.Add($"{request.Verb} {request.Target} [{string.Join(',', request.Args ?? [])}] {request.Who}");
            return Task.CompletedTask;
        };
        var opened = new List<string>();
        page.OnOpen = opened.Add;
        var screen = Screen();
        var arranged = Arrange(renderer, page, screen);
        var prs = screen.Row!.PullRequests;

        Click(arranged, Id(prs[4], "start-review"));
        reviewed.ShouldBe(["review-pr pr/SharpAstro/tianwen#1820 [] gui"]);
        Verbs.ReviewPr.ShouldBe("review-pr");
        // The CLI's `pilot review pr SharpAstro/tianwen#1820` builds the target with the same Verbs.PrTarget.
        Verbs.PrTarget("SharpAstro/tianwen", 1820).ShouldBe("pr/SharpAstro/tianwen#1820");

        Click(arranged, Id(prs[4], "open"));
        Click(arranged, Id(prs[0], "open"));
        opened.ShouldBe([prs[4].Url, prs[0].Url]);
    }

    [Fact]
    public async Task AReviewPressSaysStartingAtOnceThenReviewingUntilTheNextReadShowsIt()
    {
        // The owner (2026-10-29): "clicking the review button had no visual feedback. all buttons should always have a feedback".
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var answer = new TaskCompletionSource();
        page.OnReview = (_, _) => answer.Task;
        var screen = Screen();
        var pr = screen.Row!.PullRequests[4];
        static string[] Words(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
            [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];
        Arrange(renderer, page, screen);

        var press = page.ReviewAsync(pr);
        // Pressed: the button says so and is no button to press again.
        var arranged = Arrange(renderer, page, screen);
        Words(arranged).ShouldContain("Starting review…");
        ButtonOrNull(arranged, Id(pr, "start-review")).ShouldBeNull();

        answer.SetResult();
        await press;
        // Taken: the row says it is being reviewed at once, and offers no second Review.
        arranged = Arrange(renderer, page, screen);
        Words(arranged).ShouldNotContain("Starting review…");
        Words(arranged).ShouldContain("reviewing");
        ButtonOrNull(arranged, Id(pr, "start-review")).ShouldBeNull();

        // The next read that shows the review takes over from there.
        var read = Screen(screen.Row with { PullRequests = [.. screen.Row.PullRequests.Select(p => p.Url == pr.Url ? p with { Review = PullRequestReview.Passed } : p)] });
        Words(Arrange(renderer, page, read)).ShouldContain("passed");
    }

    [Fact]
    public async Task AReviewTheNodeTookButNothingShowsLapsesSoTheRowIsNotStuckOnReviewing()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        page.OnReview = (_, _) => Task.CompletedTask;
        var changed = 0;
        page.Changed = () => changed++;
        var screen = Screen();
        var pr = screen.Row!.PullRequests[4];
        static string[] Words(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
            [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];

        await page.ReviewAsync(pr);
        // The press said "Starting…" and then the taking: two redraws asked.
        changed.ShouldBe(2);
        var arranged = Arrange(renderer, page, screen);
        Words(arranged).ShouldContain("reviewing");

        // No read ever shows the review (it never ran): after its time the row is as before and the Review button is back.
        page.StartedFor = TimeSpan.Zero;
        arranged = Arrange(renderer, page, screen);
        Words(arranged).ShouldNotContain("reviewing");
        ButtonOrNull(arranged, Id(pr, "start-review")).ShouldNotBeNull();
    }

    [Fact]
    public async Task AReviewTheNodeRefusesIsSaidInItsWordsAndNoPressChangesTheRowsState()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        page.OnReview = (_, _) => throw new InvalidOperationException("SharpAstro/tianwen#1820 is merged, not open");
        var screen = Screen();
        _ = page.Tree(screen);
        var rowsBefore = page.Rows.ToList();
        var prs = screen.Row!.PullRequests;

        await page.ReviewAsync(prs[4]);
        page.ReviewRefused.ShouldBe("review SharpAstro/tianwen#1820 refused: SharpAstro/tianwen#1820 is merged, not open");
        var arranged = Arrange(renderer, page, screen);
        var said = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value).ToList();
        said.ShouldContain(t => t.Contains("is merged, not open", StringComparison.Ordinal));

        // The row says what it said: the node's next answer, not the click, is what changes it.
        page.Rows.ShouldBe(rowsBefore);
        page.ActionsOf(prs[4]).Select(a => a.Kind).ShouldBe([PullRequestActionKind.Review, PullRequestActionKind.Open]);
        page.ActionsOf(prs[4]).Select(a => a.Kind).ShouldContain(PullRequestActionKind.Review);

        // The next press clears it once it starts; a Merge or Go around clears it too.
        page.OnReview = (_, _) => Task.CompletedTask;
        await page.ReviewAsync(prs[4]);
        page.ReviewRefused.ShouldBeNull();
    }

    [Fact]
    public void ACardRefusalIsSaidOnThePageInTheNodesWordsToo()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Window(renderer);
        chrome.Cards.NoteRefused("c1", "Merge was refused: head moved past what was reviewed");
        var screen = Screen();
        page.Refusal().ShouldBeNull("not before a frame has told the page its cards");
        var arranged = Arrange(renderer, page, screen);
        page.Refusal().ShouldNotBeNull().ShouldContain("head moved past what was reviewed");
        arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>()
            .Select(t => t.Value).ShouldContain(t => t.Contains("head moved past what was reviewed", StringComparison.Ordinal));

        chrome.Cards.NoteRefused("c1", null);
        page.Refusal().ShouldBeNull();
    }

    private static string[] Texts(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];

    // The hover of the button saying exactly this word.
    private static string? TooltipOf(ImmutableArray<Layout.ArrangedNode<float>> arranged, string word) =>
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == word).Select(a => a.Node.Tooltip).FirstOrDefault();

    [Fact]
    public async Task TheReviewButtonSaysStartingWhileTheNodeIsAskedThenReviewingOnceItTookItAndIsBackWhenNothingShowsItAfterAWhile()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var answer = new TaskCompletionSource();
        page.OnReview = (_, _) => answer.Task;
        var screen = Screen();
        var pr = screen.Row!.PullRequests[4];
        var arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Review");
        Texts(arranged).ShouldNotContain("Starting review…");

        // The press alone says nothing the node has not: the words come with the unanswered send.
        var press = page.ReviewAsync(pr);
        arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Starting review…");
        TooltipOf(arranged, "Starting review…").ShouldBe("Asking the node to review SharpAstro/tianwen#1820");
        ButtonOrNull(arranged, Id(pr, "start-review")).ShouldBeNull();
        await page.ReviewAsync(pr);

        answer.SetResult();
        await press;
        arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Reviewing…");
        Texts(arranged).ShouldNotContain("Starting review…");
        TooltipOf(arranged, "Reviewing…").ShouldNotBeNull().ShouldContain("The node took the review of SharpAstro/tianwen#1820");
        ButtonOrNull(arranged, Id(pr, "start-review")).ShouldBeNull();

        page.StartedFor = TimeSpan.Zero;
        arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldNotContain("Reviewing…");
        ButtonOrNull(arranged, Id(pr, "start-review")).ShouldNotBeNull();
    }

    [Fact]
    public void ARowWhoseReviewReadsReviewingSaysReviewingInTheSlotAndOffersNothingToPress()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var screen = Screen(Row() with { PullRequests = [Pr("SharpAstro/DIR.Lib", 301, PullRequestKind.Behind, "Behind", review: PullRequestReview.Reviewing)] });
        var pr = screen.Row!.PullRequests[0];
        var arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Reviewing…");
        TooltipOf(arranged, "Reviewing…").ShouldBe("Pilot is reviewing SharpAstro/DIR.Lib#301");
        ButtonOrNull(arranged, Id(pr, "start-review")).ShouldBeNull();
        ButtonOrNull(arranged, Id(pr, "address-review")).ShouldBeNull();
        ButtonOrNull(arranged, Id(pr, "open")).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReviewTheNodeRefusesOrThatDoesNotGetThroughLeavesFailedWithTheWordsAndAPressRetries(bool refused)
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var why = refused ? "SharpAstro/tianwen#1820 is merged, not open" : "the node's socket is not there";
        page.OnReview = (_, _) => Task.FromException(refused ? new InvalidOperationException(why) : new IOException(why));
        var screen = Screen();
        var pr = screen.Row!.PullRequests[4];

        await page.ReviewAsync(pr);
        var arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Failed");
        TooltipOf(arranged, "Failed").ShouldBe($"The node did not start the review of SharpAstro/tianwen#1820: {why}. Press to try again");
        page.Refusal().ShouldBe($"review SharpAstro/tianwen#1820 refused: {why}");
        Texts(arranged).ShouldContain(t => t.Contains(why, StringComparison.Ordinal));

        // Failed is a button still: pressed, it asks again, and the node's yes says Reviewing… with the bar cleared.
        var again = new TaskCompletionSource();
        page.OnReview = (_, _) => again.Task;
        Click(arranged, Id(pr, "start-review"));
        arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Starting review…");
        Texts(arranged).ShouldNotContain("Failed");
        page.Refusal().ShouldBeNull();
        again.SetResult();
        await Task.Yield();
        for (var i = 0; i < 100 && !Texts(Arrange(renderer, page, screen)).Contains("Reviewing…"); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Texts(Arrange(renderer, page, screen)).ShouldContain("Reviewing…");
    }

    [Fact]
    public async Task TheAddressReviewButtonSaysStartingFixThenAddressingAndFailedWithTheNodesWordsUntilPressedAgain()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Window(renderer);
        var answer = new TaskCompletionSource();
        page.OnFixReview = (_, _) => answer.Task;
        var plain = Pr("SharpAstro/DIR.Lib", 301, PullRequestKind.Behind, "Behind", review: PullRequestReview.Changes);
        var screen = Screen(Row() with { PullRequests = [plain] });
        Texts(Arrange(renderer, page, screen)).ShouldContain("Address review");

        var press = page.FixReviewAsync(plain);
        var arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Starting fix…");
        TooltipOf(arranged, "Starting fix…").ShouldBe("Asking the node to fix SharpAstro/DIR.Lib#301's review findings");
        ButtonOrNull(arranged, Id(plain, "address-review")).ShouldBeNull();

        answer.SetResult();
        await press;
        arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Addressing…");
        TooltipOf(arranged, "Addressing…").ShouldNotBeNull().ShouldContain("The node took the fixer on SharpAstro/DIR.Lib#301's review findings");
        ButtonOrNull(arranged, Id(plain, "address-review")).ShouldBeNull();

        // A read that shows another review word ends it; the node's refusal later is Failed, in its words, in the bar too.
        Texts(Arrange(renderer, page, Screen(Row() with { PullRequests = [plain with { Review = PullRequestReview.Passed }] }))).ShouldNotContain("Addressing…");
        page.OnFixReview = (_, _) => Task.FromException(new InvalidOperationException("a flight owns SharpAstro/DIR.Lib#301"));
        await page.FixReviewAsync(plain);
        arranged = Arrange(renderer, page, screen);
        Texts(arranged).ShouldContain("Failed");
        TooltipOf(arranged, "Failed").ShouldBe("The node did not start a fixer on SharpAstro/DIR.Lib#301: a flight owns SharpAstro/DIR.Lib#301. Press to try again");
        page.Refusal().ShouldBe("review fix SharpAstro/DIR.Lib#301 refused: a flight owns SharpAstro/DIR.Lib#301");

        // Pressed again, the send is retried.
        var retried = 0;
        page.OnFixReview = (_, _) => { retried++; return Task.CompletedTask; };
        Click(arranged, Id(plain, "address-review"));
        for (var i = 0; i < 100 && !Texts(Arrange(renderer, page, screen)).Contains("Addressing…"); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        retried.ShouldBe(1);
        Texts(Arrange(renderer, page, screen)).ShouldContain("Addressing…");
        page.Refusal().ShouldBeNull();
    }

    [Fact]
    public void ThePressedAndFailedWordsMeetTheContrastFloorOnTheButtonInBothPalettes()
    {
        static double Lum(RGBAColor32 c)
        {
            static double Linear(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
        }
        static double Contrast(RGBAColor32 a, RGBAColor32 b) => (Math.Max(Lum(a), Lum(b)) + 0.05) / (Math.Min(Lum(a), Lum(b)) + 0.05);
        // The night palette is pure red on black by design: its floor is the 3:1 every button label there already has.
        foreach (var (palette, floor) in new[] { (Palette.Standard, 4.5), (Palette.Night, 3.0) })
        {
            Contrast(palette.Bright, palette.Button).ShouldBeGreaterThanOrEqualTo(floor);
            Contrast(palette.Held, palette.Button).ShouldBeGreaterThanOrEqualTo(floor);
        }
    }

    // Nine plain PRs, one in each state of the two buttons: Review, Starting review…, Reviewing… (taken), Reviewing… (read), Failed,
    // and Address review, Starting fix…, Addressing…, Failed.
    private static async Task<(PullRequestsView<RgbaImage> Page, PullRequestsScreen Screen)> InEachState(PilotChrome<RgbaImage> chrome)
    {
        var page = chrome.PullRequests;
        var never = new TaskCompletionSource();
        page.OnReview = (pr, _) => pr.Number switch
        {
            2 => never.Task,
            3 => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("SharpAstro/tianwen#5 is merged, not open: nothing to review there, and a very long reason that the bar above the list must cut with its ellipsis and say whole on hover")),
        };
        page.OnFixReview = (pr, _) => pr.Number switch
        {
            6 => never.Task,
            7 => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("a flight owns this PR")),
        };
        var prs = new[]
        {
            Pr("SharpAstro/tianwen", 1, PullRequestKind.Behind, "Behind", title: "Review, not yet pressed"),
            Pr("SharpAstro/tianwen", 2, PullRequestKind.Behind, "Behind", title: "Starting review"),
            Pr("SharpAstro/tianwen", 3, PullRequestKind.Behind, "Behind", title: "Reviewing, the node took it"),
            Pr("SharpAstro/tianwen", 4, PullRequestKind.Behind, "Behind", review: PullRequestReview.Reviewing, title: "Reviewing, the row's read says so"),
            Pr("SharpAstro/tianwen", 5, PullRequestKind.Behind, "Behind", title: "Failed, the node refused"),
            Pr("SharpAstro/tianwen", 6, PullRequestKind.Behind, "Behind", review: PullRequestReview.Changes, title: "Starting fix"),
            Pr("SharpAstro/tianwen", 7, PullRequestKind.Behind, "Behind", review: PullRequestReview.Changes, title: "Addressing"),
            Pr("SharpAstro/tianwen", 8, PullRequestKind.Behind, "Behind", review: PullRequestReview.Changes, title: "Address review failed"),
            Pr("SharpAstro/tianwen", 9, PullRequestKind.Behind, "Behind", review: PullRequestReview.Changes, title: "Address review, not yet pressed"),
        };
        _ = page.ReviewAsync(prs[1]);
        await page.ReviewAsync(prs[2]);
        await page.ReviewAsync(prs[4]);
        _ = page.FixReviewAsync(prs[5]);
        await page.FixReviewAsync(prs[6]);
        await page.FixReviewAsync(prs[7]);
        return (page, Screen(Row() with { PullRequests = prs }));
    }

    [Theory]
    [InlineData(1280, false)]
    [InlineData(2000, false)]
    [InlineData(1280, true)]
    [InlineData(2000, true)]
    public async Task ARenderedPageWithARowInEachStateShowsEveryWordWholeAndNothingOverlaps(int width, bool night)
    {
        // PILOT_TEST_CAPTURE_DIR keeps the frames as PNGs, for looking at; without it the frames are only painted.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        const int height = 600;
        using var renderer = new RgbaImageRenderer((uint)width, height);
        var (chrome, _) = Window(renderer);
        chrome.Show(PilotPage.PullRequests);
        chrome.Night.On = night;
        var (page, screen) = await InEachState(chrome);

        var arranged = Arrange(renderer, page, screen, width, height);
        var texts = Texts(arranged);
        foreach (var word in new[] { "Review", "Starting review…", "Reviewing…", "Failed", "Address review", "Starting fix…", "Addressing…" })
        {
            texts.ShouldContain(word);
        }
        texts.Count(t => t == "Reviewing…").ShouldBe(2);
        texts.Count(t => t == "Failed").ShouldBe(2);
        TextOverlap.Faults(arranged).Where(f => f.Bounds.Y > 70f).Select(f => f.Line).ShouldBeEmpty($"at {width} wide, night {night}");

        // No word of a slot is cut: each is as wide as the slot allows.
        foreach (var a in arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value is "Starting review…" or "Reviewing…" or "Failed" or "Starting fix…" or "Addressing…" or "Review" or "Address review" && a.Bounds.Height == PullRequestsView<RgbaImage>.ChipHeight))
        {
            var text = (Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content;
            var (measured, _) = renderer.MeasureText(text.Value, SessionsCapture.FontPath, text.FontSize);
            measured.ShouldBeLessThanOrEqualTo(a.Bounds.Width - 8f, $"'{text.Value}' in a slot {a.Bounds.Width} wide");
            a.Node.Tooltip.ShouldNotBeNullOrEmpty($"'{text.Value}'");
        }

        var sessions = new SessionsScreen([], "live from the node on node.sock", Now);
        chrome.Render(sessions, null, CardsScreen.From(screen.OpenCards), null, null, null, screen, new RectF32(0, 0, width, height));
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"pull-request-button-states-{width}-{(night ? "night" : "day")}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }

    [Fact]
    public void ACaptureRendersRowsInEachStateAtBothWidthsInBothPalettes()
    {
        // PILOT_TEST_CAPTURE_DIR keeps the frames as PNGs, for looking at; without it the frames are only painted.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        foreach (var (width, height) in new[] { (1280, 720), (2000, 720) })
        {
            foreach (var night in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (chrome, page) = Window(renderer);
                chrome.Show(PilotPage.PullRequests);
                chrome.Night.On = night;
                var sessions = new SessionsScreen([], "live from the node on node.sock", Now);
                var screen = Screen();
                chrome.Render(sessions, null, CardsScreen.From(screen.OpenCards), null, null, null, screen, new RectF32(0, 0, width, height));
                if (dir is { Length: > 0 })
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"pull-request-actions-{width}-{(night ? "night" : "day")}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
                }
            }
        }
    }
}
