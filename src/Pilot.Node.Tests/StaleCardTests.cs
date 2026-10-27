using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Each summary run also checks the open cards against what they were about (#221): the call proposes the ids of those it thinks are
/// resolved, in a section that is never shown, and the node dismisses only those whose fact it reads itself (a PR merged or closed,
/// checks green again, a conflict gone, a review given, a flight landed), with a Handled line saying why. The model is faked.
/// </summary>
public sealed class StaleCardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private const string Head = "64d4eb3a1c0f9e8d7b6a5c4d3e2f1a0b9c8d7e6f";
    private const string OldHead = "4f2c9a01d6e8b3c7a5f0e2d4c6b8a0f1e3d5c7b9";
    private const string PrUrl = "https://github.com/SharpAstro/tianwen/pull/344";

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeGitHub _github = new();
    private readonly FakeSummaryModel _model = new();
    private readonly Summaries _summaries;

    public StaleCardTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _summaries = new Summaries(_state, _model, _time) { GitHub = _github };
        // The PR the node holds for PIL342, as GitHub has it unless a test says otherwise.
        _github.Pr = new PullRequest(344, "OPEN", Head, PrUrl, "MERGEABLE");
        _state.PutFlight(Flight("PIL342") with { PullRequest = 344, PullRequestUrl = PrUrl, GitHubHead = OldHead });
        // Another flight in play, so the fleet always has something to summarise.
        _state.PutFlight(Flight("DIR7", issue: 7));
    }

    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, int issue = 342) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = issue, Title = "Plate-solve the guide frame", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/r/.claude/worktrees/{code}", Base = "origin/main", State = state, CreatedAt = Start - TimeSpan.FromHours(3),
    };

    private CardRow Raise(string kind, string subject = "flight/PIL342", CardBin bin = CardBin.Ring, IReadOnlyList<CardChoice>? choices = null) =>
        _state.RaiseCard(bin, kind, subject, $"A {kind} card", "Its detail.", choices);

    private async Task<StaleVerdict> Check(CardRow card, string? about = null) =>
        (await new StaleCards(_state, _github).DismissAsync([card.Id], about, Ct)).ShouldHaveSingleItem();

    private CardRow Now(CardRow card) => _state.Cards().Single(c => c.Id == card.Id);

    private CardRow Handled() => _state.Cards().Single(c => c.Kind == StaleCards.HandledKind);

    private void Dismissed(CardRow card, StaleVerdict verdict, string fact)
    {
        verdict.Dismissed.ShouldBeTrue(verdict.Note);
        verdict.Note.ShouldContain(fact);
        var closed = Now(card);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldBe($"dismissed: {verdict.Note}");
        closed.ClosedBy.ShouldBe("node");
        var line = Handled();
        line.Bin.ShouldBe(CardBin.Handled);
        line.State.ShouldBe(CardState.Closed);
        line.Subject.ShouldBe(card.Subject);
        line.Title.ShouldStartWith("PIL342's ");
        line.Title.ShouldContain(fact);
        line.Detail.ShouldContain(card.Id);
    }

    private void Left(CardRow card, StaleVerdict verdict, string note)
    {
        verdict.Dismissed.ShouldBeFalse();
        verdict.Note.ShouldContain(note);
        Now(card).State.ShouldBe(CardState.Open);
        _state.Cards().ShouldNotContain(c => c.Kind == StaleCards.HandledKind);
    }

    // The proposal: parsed strictly, and never part of what is shown.

    [Fact]
    public void TheResolvedSectionIsTakenOutOfTheAnswerAndItsIdsKept()
    {
        var (prose, ids) = StaleCardProposal.Split("## Needs you\n- c3 waits for a merge.\n\n## Where it is\nParser is flying.\n\n## Resolved\n- c1\n- c2\nc1\n");

        ids.ShouldBe(["c1", "c2"]);
        prose.ShouldBe("## Needs you\n- c3 waits for a merge.\n\n## Where it is\nParser is flying.");
        prose.ShouldNotContain("Resolved");
    }

    [Fact]
    public void AResolvedSectionInTheMiddleLeavesTheSectionsAfterIt()
    {
        var (prose, ids) = StaleCardProposal.Split("## Needs you\nNothing.\r\n\r\n## Resolved\r\nc7\r\n\r\n## Where it is\r\nAll quiet.");

        ids.ShouldBe(["c7"]);
        prose.ShouldBe("## Needs you\nNothing.\n\n## Where it is\nAll quiet.");
    }

    [Theory]
    [InlineData("None")]
    [InlineData("none.")]
    [InlineData("")]
    public void ANoneOrEmptySectionProposesNothingAndIsStillTakenOut(string said)
    {
        var (prose, ids) = StaleCardProposal.Split($"## Needs you\nNothing.\n\n## Where it is\nQuiet.\n\n## Resolved\n{said}");

        ids.ShouldBeEmpty();
        prose.ShouldNotContain("Resolved");
        prose.ShouldEndWith("Quiet.");
    }

    [Theory]
    [InlineData("c1\nthe PR that merged")]
    [InlineData("c1\nNone")]
    [InlineData("c1, c2")]
    [InlineData("`c1`")]
    [InlineData("flight/PIL342")]
    [InlineData("C1")]
    [InlineData("c")]
    public void ASectionThatHoldsAnythingButIdsProposesNothing(string said)
    {
        var (prose, ids) = StaleCardProposal.Split($"## Needs you\nNothing.\n\n## Resolved\n{said}");

        ids.ShouldBeEmpty();
        prose.ShouldBe("## Needs you\nNothing.");
    }

    [Fact]
    public void TwoSectionsOrTooManyIdsProposeNothing()
    {
        StaleCardProposal.Split("## Resolved\nc1\n\n## Where it is\nx\n\n## Resolved\nc2").Ids.ShouldBeEmpty();
        StaleCardProposal.Split("## Resolved\n" + string.Join('\n', Enumerable.Range(1, StaleCardProposal.MaxProposed + 1).Select(i => $"c{i}"))).Ids.ShouldBeEmpty();
        StaleCardProposal.Split("## Resolved\n" + string.Join('\n', Enumerable.Range(1, StaleCardProposal.MaxProposed).Select(i => $"c{i}"))).Ids.Count.ShouldBe(StaleCardProposal.MaxProposed);
    }

    [Fact]
    public void AnAnswerWithNoSectionProposesNothing()
    {
        var (prose, ids) = StaleCardProposal.Split(FakeSummaryModel.Fine.Text);

        ids.ShouldBeEmpty();
        prose.ShouldBe(FakeSummaryModel.Fine.Text);
    }

    [Fact]
    public void ThePromptAsksForTheSectionOverTheFleetAndAFlightButNotOverSeveralNodes()
    {
        var asks = $"{SummaryPrompt.Resolved}\n- One line per open card";

        SummaryPrompt.Fleet(2, "records").ShouldContain(asks);
        SummaryPrompt.Flight("PIL342", "records").ShouldContain(asks);
        SummaryPrompt.Nodes(2, "records").ShouldNotContain(SummaryPrompt.Resolved);
        // Ahead of the records, whose own text can never be mistaken for it.
        SummaryPrompt.Fleet(2, "records").IndexOf(SummaryPrompt.Resolved, StringComparison.Ordinal).ShouldBeLessThan(SummaryPrompt.Fleet(2, "records").IndexOf("Records:", StringComparison.Ordinal));
    }

    // Through a summary run.

    [Fact]
    public async Task ASummaryRunDismissesTheProposedCardWhoseFactHoldsAndNeverShowsTheIds()
    {
        var card = Raise(LegCommander.PrReady);
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = Head };
        _model.Answer = new SummaryAnswer($"## Needs you\nNothing.\n\n## Where it is\nPIL342 merged.\n\n{SummaryPrompt.Resolved}\n- {card.Id}\n", 100, 0.001);

        var row = (await _summaries.MakeAsync(SummaryRow.FleetScope, Ct)).Row.ShouldNotBeNull();

        row.Markdown.ShouldBe("## Needs you\nNothing.\n\n## Where it is\nPIL342 merged.");
        _state.Summaries().ShouldHaveSingleItem().Markdown.ShouldNotContain(card.Id);
        Now(card).State.ShouldBe(CardState.Closed);
        Handled().Title.ShouldBe("PIL342's #344 PR card: #344 merged on GitHub as 64d4eb3a");
        // The prompt it was asked carries the card, so it can name its id.
        _model.Prompts.ShouldHaveSingleItem().ShouldContain($"{card.Id} (ring, pr-ready)");
    }

    [Fact]
    public async Task ASummaryRunLeavesACardItsCheckContradicts()
    {
        var card = Raise(LegCommander.PrReady);
        _model.Answer = new SummaryAnswer($"## Needs you\nc1 is stale.\n\n## Where it is\nPIL342 waits.\n\n{SummaryPrompt.Resolved}\n{card.Id}", 100, null);

        var row = (await _summaries.MakeAsync(SummaryRow.FleetScope, Ct)).Row.ShouldNotBeNull();

        row.Markdown.ShouldNotContain(SummaryPrompt.Resolved);
        Now(card).State.ShouldBe(CardState.Open);
        _state.Cards().ShouldNotContain(c => c.Kind == StaleCards.HandledKind);
    }

    [Fact]
    public async Task ASummaryRunWithoutGitHubReadsNoCauseAndDismissesNothing()
    {
        var card = Raise(LegCommander.PrReady);
        _github.Pr = _github.Pr! with { State = "MERGED" };
        _model.Answer = new SummaryAnswer($"## Needs you\nNothing.\n\n## Where it is\nQuiet.\n\n{SummaryPrompt.Resolved}\n{card.Id}", 100, null);
        var bare = new Summaries(_state, _model, _time);

        (await bare.MakeAsync(SummaryRow.FleetScope, Ct)).Row.ShouldNotBeNull().Markdown.ShouldNotContain(card.Id);

        Now(card).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task ASummaryOverOneFlightOnlyDismissesCardsAboutThatFlight()
    {
        var own = Raise(LegCommander.PrReady);
        var other = Raise(LegCommander.PrReady, "flight/DIR7");
        _state.PutFlight(_state.Flights().Single(f => f.Code == "DIR7") with { PullRequest = 344 });
        _github.Pr = _github.Pr! with { State = "MERGED" };
        _model.Answer = new SummaryAnswer($"## Needs you\nNothing.\n\n## Where it is\nQuiet.\n\n{SummaryPrompt.Resolved}\n{own.Id}\n{other.Id}", 100, null);

        await _summaries.MakeAsync("PIL342", Ct);

        Now(own).State.ShouldBe(CardState.Closed);
        Now(other).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task AnAnswerWithABadSectionStillMakesTheSummaryAndDismissesNothing()
    {
        var card = Raise(LegCommander.PrReady);
        _github.Pr = _github.Pr! with { State = "MERGED" };
        _model.Answer = new SummaryAnswer($"## Needs you\nNothing.\n\n## Where it is\nQuiet.\n\n{SummaryPrompt.Resolved}\n{card.Id} because the PR merged", 100, null);

        (await _summaries.MakeAsync(SummaryRow.FleetScope, Ct)).Row.ShouldNotBeNull().Markdown.ShouldNotContain("because");

        Now(card).State.ShouldBe(CardState.Open);
    }

    // A PR card whose PR merged or closed.

    [Theory]
    [InlineData(LegCommander.PrReady)]
    [InlineData(LegCommander.MergeFailed)]
    [InlineData(PrChecks.QueueDropped)]
    [InlineData(PrChecks.MergeDropped)]
    [InlineData("pr-conflicts")]
    [InlineData("pr-checks-failed")]
    public async Task APrCardIsDismissedOnceItsPrMerged(string kind)
    {
        var card = Raise(kind);
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = Head };

        var verdict = await Check(card);

        Dismissed(card, verdict, "#344 merged on GitHub as 64d4eb3a");
        Handled().Link.ShouldBe(PrUrl);
    }

    [Fact]
    public async Task APrCardIsDismissedOnceItsPrClosedWithoutAMerge()
    {
        var card = Raise(LegCommander.PrReady);
        _github.Pr = _github.Pr! with { State = "CLOSED" };

        Dismissed(card, await Check(card), "#344 closed on GitHub without a merge");
    }

    [Fact]
    public async Task APrCardWhosePrIsStillOpenIsLeftAlone()
    {
        var card = Raise(LegCommander.PrReady);

        Left(card, await Check(card), "#344 is still open");
    }

    // A failed-check card whose PR is green.

    [Fact]
    public async Task AFailedCheckCardIsDismissedOnceItsPrIsGreenAtANewerHead()
    {
        var card = Raise("pr-checks-failed", choices: [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/PIL342", Args = [OldHead] }]);
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 7, 7);

        Dismissed(card, await Check(card), "its checks pass at a newer head (64d4eb3a, was 4f2c9a01)");
    }

    [Fact]
    public async Task AFailedCheckCardIsDismissedOnceTheSameHeadsChecksWereRerunGreen()
    {
        var card = Raise("pr-checks-failed");
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 7, 7);

        Dismissed(card, await Check(card), "its checks pass at 64d4eb3a");
    }

    [Fact]
    public async Task AMergeFailedCardOfAFailedCheckIsDismissedOnceItsChecksPass()
    {
        var failed = Raise(LegCommander.MergeFailed, choices: [new CardChoice { Label = "Re-run", Verb = Verbs.RerunChecks, Target = "flight/PIL342" }]);
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 7, 7);

        Dismissed(failed, await Check(failed), "its checks pass at 64d4eb3a");
    }

    [Fact]
    public async Task AMergeFailedCardOfAnythingElseHasNoCheckToReadEvenWithTheChecksGreen()
    {
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 7, 7);
        var refused = Raise(LegCommander.MergeFailed, "flight/DIR7");
        _state.PutFlight(_state.Flights().Single(f => f.Code == "DIR7") with { PullRequest = 344 });
        Left(refused, await Check(refused), "#344 is still open");
    }

    [Theory]
    [InlineData(ChoiceStatus.Failing, "failing (build)")]
    [InlineData(ChoiceStatus.Pending, "pending")]
    public async Task AFailedCheckCardWhosePrIsNotGreenIsLeftAlone(string status, string said)
    {
        var card = Raise("pr-checks-failed");
        _github.Summary = new ChecksSummary(status, 3, 7, status == ChoiceStatus.Failing ? "build" : null);

        Left(card, await Check(card), $"#344's checks are {said}");
    }

    [Fact]
    public async Task AFailedCheckCardWhoseChecksGitHubDoesNotSayIsLeftAlone()
    {
        var card = Raise("pr-checks-failed");

        Left(card, await Check(card), "does not say where #344's checks stand");
    }

    // A conflict card whose branch is clean.

    [Fact]
    public async Task AConflictCardIsDismissedOnceTheBranchMergesCleanly()
    {
        var card = Raise("pr-conflicts");

        var verdict = await Check(card);

        Dismissed(card, verdict, "the branch merges cleanly at 64d4eb3a");
        Handled().Title.ShouldBe("PIL342's #344 conflict: the branch merges cleanly at 64d4eb3a");
    }

    [Theory]
    [InlineData("CONFLICTING", "#344 still conflicts with its base")]
    [InlineData("UNKNOWN", "GitHub has not said whether #344 merges cleanly")]
    [InlineData("", "GitHub has not said whether #344 merges cleanly")]
    public async Task AConflictCardWhoseBranchStillConflictsOrIsNotReadIsLeftAlone(string mergeable, string said)
    {
        var card = Raise("pr-conflicts");
        _github.Pr = _github.Pr! with { Mergeable = mergeable };

        Left(card, await Check(card), said);
    }

    [Theory]
    [InlineData(MergeKinds.Conflicts)]
    [InlineData(MergeKinds.Behind)]
    public async Task AConflictCardWhoseMergeStateGitHubStillShowsAsConflictingOrBehindIsLeftAlone(string kind)
    {
        var card = Raise("pr-conflicts");
        _github.Merge = new MergeState(kind);

        Left(card, await Check(card), $"GitHub still shows #344 as {kind}");
    }

    // A stalled-review card whose review gave its verdict.

    [Theory]
    [InlineData(ReviewState.Passed, "its review passed 2026-10-28 09:05 UTC")]
    [InlineData(ReviewState.Changes, "its review asks for changes 2026-10-28 09:05 UTC")]
    public async Task AStalledReviewCardIsDismissedOnceItsReviewGaveItsVerdict(ReviewState verdict, string fact)
    {
        var card = Raise(LegWatch.ReviewStalled);
        _state.PutFlight(_state.Flights().Single(f => f.Code == "PIL342") with
        {
            Review = new ReviewRow { State = verdict, Reviewer = "se1-pil342-review", StartedAt = Start - TimeSpan.FromMinutes(30), FinishedAt = Start + TimeSpan.FromMinutes(5) },
        });

        var result = await Check(card);

        Dismissed(card, result, fact);
        Handled().Title.ShouldStartWith("PIL342's #344 review stall: ");
    }

    [Fact]
    public async Task AStalledReviewCardWhoseReviewStillRunsOrGaveItsVerdictBeforeTheCardIsLeftAlone()
    {
        var card = Raise(LegWatch.ReviewStalled);
        var flight = _state.Flights().Single(f => f.Code == "PIL342");
        _state.PutFlight(flight with { Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "r", StartedAt = Start } });
        Left(card, await Check(card), "has not given a verdict since the card");

        // An earlier review's verdict says nothing of the one that stalled afterwards.
        _state.PutFlight(flight with { Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "r", StartedAt = Start - TimeSpan.FromHours(2), FinishedAt = Start - TimeSpan.FromHours(1) } });
        Left(card, await Check(card), "has not given a verdict since the card");

        _state.PutFlight(flight with { Review = null });
        Left(card, await Check(card), "holds no review of its flight");
    }

    [Fact]
    public async Task AStalledReviewCardOfAnyPrIsDismissedOnceItsReviewGaveItsVerdict()
    {
        var subject = Verbs.PrTarget("acme/widget", 7);
        var card = Raise(LegWatch.ReviewStalled, subject);
        PrReviewRow Review(PrReviewState state, DateTimeOffset? finished, string? verdict) => new()
        {
            Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = OldHead, ClonePath = "/r",
            Worktree = "/r/.claude/worktrees/review-widget-7", Reviewer = "se1-review-widget-7", State = state, Verdict = verdict, StartedAt = Start - TimeSpan.FromMinutes(30), FinishedAt = finished,
        };

        Left(card, await Check(card), "holds no such review");
        _state.PutPrReview(Review(PrReviewState.Reviewing, null, null));
        Left(card, await Check(card), "has not given a verdict since the card");

        _state.PutPrReview(Review(PrReviewState.Reviewed, Start + TimeSpan.FromMinutes(3), "approve"));
        var verdict = await Check(card);

        verdict.Dismissed.ShouldBeTrue(verdict.Note);
        verdict.Note.ShouldBe("its review gave its verdict (approve) 2026-10-28 09:03 UTC");
        Now(card).State.ShouldBe(CardState.Closed);
        Handled().Title.ShouldBe("acme/widget#7's review stall: its review gave its verdict (approve) 2026-10-28 09:03 UTC");
    }

    // A flight's card for a flight that has landed.

    [Theory]
    [InlineData(LegCommander.PrReady, "flight/PIL342")]
    [InlineData("leg-failed", "flight/PIL342/parser")]
    [InlineData(LegWatch.Stopped, "flight/PIL342/parser")]
    [InlineData(LegWatch.Idle, "flight/PIL342/parser")]
    [InlineData(LegWatch.OverBudget, "flight/PIL342/parser")]
    [InlineData(LegWatch.ValidationStalled, "flight/PIL342/parser")]
    [InlineData(LegWatch.ReviewStalled, "flight/PIL342")]
    [InlineData(LegCommander.NextStepKind, "flight/PIL342")]
    public async Task AFlightsCardIsDismissedOnceItsFlightHasLanded(string kind, string subject)
    {
        var card = Raise(kind, subject);
        _state.PutFlight(_state.Flights().Single(f => f.Code == "PIL342") with { State = FlightState.Landed, LandedAt = Start - TimeSpan.FromMinutes(7) });
        // Landing does not need GitHub to say anything: the node holds that fact.
        _github.PrFails = "offline";

        var verdict = await Check(card);

        Dismissed(card, verdict, "PIL342 landed 2026-10-28 08:53 UTC");
        Handled().Title.ShouldStartWith("PIL342's #344 ");
        Handled().Link.ShouldBe(PrUrl);
    }

    [Fact]
    public async Task AFlightsCardForAFlightStillInPlayIsLeftAlone()
    {
        var card = Raise("leg-failed", "flight/PIL342/parser");

        Left(card, await Check(card), "its flight has not landed");
    }

    // Never dismissed: what needs the person, and what has no check.

    [Theory]
    [InlineData(LegCommander.ReviewChanges, "flight/PIL342")]
    [InlineData(CardKinds.AssistantAsk, "flight/PIL342")]
    [InlineData(LegCommander.OverlapKind, "flight/PIL342")]
    [InlineData(LegCommander.NoGoKind, "flight/PIL342/parser")]
    [InlineData(StartGate.WeekKind, "node")]
    [InlineData(Pilot.Node.Sessions.UsageReader.HighKind, "node")]
    [InlineData(Digest.Kind, "node")]
    public async Task ACardThatNeedsThePersonsDecisionIsNeverDismissedEvenWithItsFlightLanded(string kind, string subject)
    {
        var card = Raise(kind, subject);
        _state.PutFlight(_state.Flights().Single(f => f.Code == "PIL342") with { State = FlightState.Landed, LandedAt = Start });
        _github.Pr = _github.Pr! with { State = "MERGED" };

        Left(card, await Check(card), "it needs the person's decision");
    }

    [Theory]
    [InlineData("landed", "flight/PIL342")]
    [InlineData("plane-left", "flight/PIL342")]
    [InlineData("store", "snapshot")]
    [InlineData("session-waiting", "session/a1b2c3d4")]
    [InlineData("something-new", "flight/PIL342")]
    [InlineData(PrReviews.ReadyKind, "pr/acme/widget#7")]
    public async Task ACardOfAKindWithNoCheckIsLeftAloneEvenWhenItsFlightLandedAndItsPrMerged(string kind, string subject)
    {
        var card = Raise(kind, subject);
        _state.PutFlight(_state.Flights().Single(f => f.Code == "PIL342") with { State = FlightState.Landed, LandedAt = Start });
        _github.Pr = _github.Pr! with { State = "MERGED" };

        Left(card, await Check(card), "no check reads this kind of card");
    }

    // Never dismissed: a cause that cannot be read.

    [Fact]
    public async Task ACardWhosePrGitHubCannotBeReadForIsLeftAlone()
    {
        var card = Raise(LegCommander.PrReady);
        _github.PrFails = "gh could not reach GitHub";

        Left(card, await Check(card), "GitHub could not be read: gh could not reach GitHub");
    }

    [Fact]
    public async Task ACardWhosePrGitHubShowsNothingOrAnotherOneOfIsLeftAlone()
    {
        var card = Raise(LegCommander.PrReady);
        _github.Pr = null;
        Left(card, await Check(card), "GitHub shows no PR #344 on its branch");

        _github.Pr = new PullRequest(999, "MERGED", Head, "https://github.com/SharpAstro/tianwen/pull/999");
        Left(card, await Check(card), "GitHub shows no PR #344 on its branch");
    }

    [Fact]
    public async Task ACardWhoseFlightHasNoPrOrIsNotHeldIsLeftAlone()
    {
        var card = Raise(LegCommander.PrReady, "flight/DIR7");
        Left(card, await Check(card), "its flight has no PR the node could read");

        var gone = Raise(LegCommander.PrReady, "flight/NOPE1");
        Left(gone, await Check(gone), "the node holds no flight NOPE1");
    }

    // Never dismissed: a proposal the node's check contradicts, or that names no open card.

    [Fact]
    public async Task AProposalNamingNoCardOrAClosedCardOrAnotherFlightsCardChangesNothing()
    {
        var closed = Raise(LegCommander.PrReady);
        _state.CloseCard(closed.Id, "dismissed", "ada");
        _github.Pr = _github.Pr! with { State = "MERGED" };

        var verdicts = await new StaleCards(_state, _github).DismissAsync(["c99", closed.Id], null, Ct);

        verdicts.Select(v => (v.Card, v.Dismissed, v.Note)).ShouldBe([("c99", false, "there is no such card"), (closed.Id, false, "it is not open")]);
        _state.Cards().ShouldNotContain(c => c.Kind == StaleCards.HandledKind);

        var open = Raise(LegCommander.PrReady);
        (await Check(open, about: "flight/DIR7")).Note.ShouldBe("it is not about what was summarised");
        Now(open).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task OneProposalIsCheckedAloneTheOthersStandOrFallByTheirOwnFact()
    {
        var merged = Raise(LegCommander.PrReady);
        var open = Raise(LegWatch.ReviewStalled);
        var ask = Raise(CardKinds.AssistantAsk);
        _github.Pr = _github.Pr! with { State = "MERGED" };

        var verdicts = await new StaleCards(_state, _github).DismissAsync([merged.Id, open.Id, ask.Id], null, Ct);

        verdicts.Select(v => v.Dismissed).ShouldBe([true, false, false]);
        Now(merged).State.ShouldBe(CardState.Closed);
        Now(open).State.ShouldBe(CardState.Open);
        Now(ask).State.ShouldBe(CardState.Open);
        _state.Cards().Count(c => c.Kind == StaleCards.HandledKind).ShouldBe(1);
    }
}
