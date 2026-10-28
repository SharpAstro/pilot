using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The leg watch: no flying leg's worker stalls without the node resuming it, nudging it, or ringing for it.</summary>
public sealed class LegWatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly List<CommandRequest> _said = [];
    private readonly LegWatch _watch;

    /// <summary>Why the next say is refused; null to deliver it.</summary>
    private string? _refuse;

    public LegWatchTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _watch = new LegWatch(_state, Run, _time) { Pilot = "pilot --node-socket \"/tmp/n.sock\"" };
        _state.PutLeg(new LegRow
        {
            Flight = "TIA1",
            Name = "parser",
            Order = 1,
            Goal = "Parse the ephemeris",
            DoneWhen = "The parser tests pass",
            Checklist = [new CheckItem { Text = "Write it", Done = true }, new CheckItem { Text = "Test it" }],
            State = LegState.Flying,
            Worker = "du1-tia1-parser",
            WorkerJob = "a1b2c3d4",
            StartedAt = Start,
        });
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, _refuse));
    }

    private void Worker(SessionState state, DateTimeOffset? lastResponse = null, string? apiError = null, DateTimeOffset? resetsAt = null, string? waitingFor = null) =>
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4",
            SessionId = "a1b2c3d4-0000-4000-8000-000000000000",
            Name = "du1-tia1-parser",
            Kind = SessionKind.Background,
            Cwd = "/work/tia1",
            Role = SessionRole.Worker,
            Flight = "TIA1",
            Leg = "parser",
            ContextTokens = 0,
            ContextWindow = 200_000,
            State = state,
            WaitingFor = waitingFor,
            SpendWeighted = 0,
            ApiError = apiError,
            ApiErrorResetsAt = resetsAt,
            LastResponseAt = lastResponse ?? Start,
            StartedAt = Start,
        });

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private CardRow[] Handled() => [.. _state.Cards().Where(c => c.Bin == CardBin.Handled)];

    [Fact]
    public async Task AWorkerTheUsageLimitStoppedIsResumedOnlyOnceItsLimitHasReset()
    {
        Worker(SessionState.Idle, apiError: "rate_limit", resetsAt: Start.AddHours(2));

        await _watch.LookAsync(Ct);
        _said.ShouldBeEmpty();
        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.ForYou, LegWatch.Stopped, "flight/TIA1/parser"));
        card.Choices.Select(c => c.Label).ShouldBe(["Resume now", "Redo leg parser"]);

        _time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3));
        await _watch.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/a1b2c3d4", "node"));
        say.Args[0].ShouldContain("limit that stopped you has reset");
        Open().ShouldBeEmpty();
        Handled().ShouldHaveSingleItem().Kind.ShouldBe("worker-resumed");

        // Still stopped on the same reset a look later: the resume did not take, so it rings rather than resuming again.
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        (Open().ShouldHaveSingleItem().Bin, Open()[0].Kind).ShouldBe((CardBin.Ring, LegWatch.Stopped));
    }

    [Fact]
    public async Task AStopWithNoResetTimeRingsAtOnce()
    {
        Worker(SessionState.Idle, apiError: "overloaded");

        await _watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.Ring, LegWatch.Stopped));
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Resume", Verbs.Say), ("Redo leg parser", Verbs.GoAround)]);
    }

    [Fact]
    public async Task AWorkerThatAnswersEachNudgeAndIdlesAgainIsRungAsAnsweringNotAsDeaf()
    {
        // TIA1241's worker waited on jobs it had handed to another node: it answered both nudges, and the ring said "to no
        // avail" under the last go-around's reason as its report (SE1, 2026-10-25).
        Worker(SessionState.Idle);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            _time.Advance(TimeSpan.FromMinutes(4));
            await _watch.LookAsync(Ct);
            _time.Advance(TimeSpan.FromSeconds(30));
            Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        }
        _said.Count.ShouldBe(2);

        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldContain("answers its nudges but stays idle");
        card.Detail.ShouldNotBeNull().ShouldContain("it answered each");
        card.Detail.ShouldNotContain("to no avail");
    }

    [Fact]
    public async Task AWorkerThatAnsweredOnlyItsFirstNudgeIsSaidToHaveAnsweredOneOfThem()
    {
        Worker(SessionState.Idle);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        // It answers the first nudge.
        _time.Advance(TimeSpan.FromSeconds(30));
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        // Not the second.
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        Open().ShouldHaveSingleItem().Detail.ShouldNotBeNull().ShouldContain("it answered 1 of them");
    }

    [Fact]
    public async Task AnIdleWorkerIsLeftItsGraceThenNudgedTwiceThenRungFor()
    {
        Worker(SessionState.Idle);

        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(3));
        await _watch.LookAsync(Ct);
        var nudge = _said.ShouldHaveSingleItem().Args[0];
        nudge.ShouldContain("1 of 2 steps are not ticked yet");
        nudge.ShouldContain("pilot --node-socket \"/tmp/n.sock\" leg finish done");
        // The same limit the brief gives: failed only for what stops the Done-when.
        nudge.ShouldContain("if something stops the Done-when");

        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        Handled().Length.ShouldBe(2);
        Open().ShouldBeEmpty();

        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.Ring, LegWatch.Idle));
        card.Choices.Select(c => c.Label).ShouldBe(["Nudge", "Redo leg parser"]);
    }

    [Fact]
    public async Task ANudgeTheNodeCannotDeliverRings()
    {
        Worker(SessionState.Ended);
        _refuse = "the folder is gone";
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Detail).ShouldBe((LegWatch.Idle, "the folder is gone"));
    }

    [Fact]
    public async Task AWaitingWorkerRingsWithItsQuestionAndTheCardClosesWhenItWorksAgain()
    {
        Worker(SessionState.Waiting, waitingFor: "permission to run rm -rf build");

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Detail).ShouldBe((LegWatch.Waiting, "permission to run rm -rf build"));
        card.Choices.Select(c => c.Label).ShouldBe(["Redo leg parser"]);

        Worker(SessionState.Working);
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();
        _state.Cards().Single(c => c.Id == card.Id).Outcome.ShouldBe("working again");
    }

    [Fact]
    public async Task AFailedWorkerAndOneThatNeverShowedUpRing()
    {
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.Missing);

        Worker(SessionState.Failed);
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.Failed);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReviewerThatStopsWithoutItsVerdictIsToldThenReviewedAfreshAndOnlyThenRings()
    {
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start,
            Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "du1-tia1-review", ReviewerJob = "a1b2c3d4", StartedAt = Start },
        });
        Worker(SessionState.Working);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        // Told first, in its own session: nothing for the person yet (PIL274's stall only rang, and nobody pressed it).
        (_said.ShouldHaveSingleItem().Verb, _said[0].Target).ShouldBe((Verbs.Say, "session/a1b2c3d4"));
        Open().ShouldNotContain(c => c.Bin != CardBin.Handled);

        // Still idle a grace later: the node starts a fresh review itself.
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        (_said[^1].Verb, _said[^1].Target).ShouldBe((Verbs.StartReview, "flight/TIA1"));
        Open().ShouldNotContain(c => c.Bin != CardBin.Handled);

        // Stalled again after that: now it rings, with a fresh review as the way on. (The session row stands in for the fresh
        // reviewer the node's start-review put on the flight; the fake run does not swap ReviewerJob, the ladder is the same.)
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        var card = Open().Where(c => c.Bin == CardBin.Ring).ShouldHaveSingleItem();
        (card.Kind, card.Subject, card.Detail).ShouldBe((LegWatch.ReviewStalled, "flight/TIA1", "it stopped 12 min ago without its verdict"));
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.StartReview);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ALaterReviewRoundOfTheSameFlightIsToldAgainBeforeItIsRestartedOrRung()
    {
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        FlightRow Flight(ReviewRow? review) => new()
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start, Review = review,
        };
        ReviewRow Reviewing() => new() { State = ReviewState.Reviewing, Reviewer = "du1-tia1-review", ReviewerJob = "a1b2c3d4", StartedAt = _time.GetUtcNow() };

        _state.PutFlight(Flight(Reviewing()));
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        (_said[0].Verb, _said[1].Verb).ShouldBe((Verbs.Say, Verbs.StartReview));

        // A verdict lands (changes), fixes follow, and the next round starts: its first stall is told, not rung.
        _state.PutFlight(Flight(new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", ReviewerJob = "a1b2c3d4", StartedAt = Start }));
        await _watch.LookAsync(Ct);
        _state.PutFlight(Flight(Reviewing()));
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        _said.Count.ShouldBe(3);
        (_said[2].Verb, _said[2].Target).ShouldBe((Verbs.Say, "session/a1b2c3d4"));
        Open().Where(c => c.Bin == CardBin.Ring).ShouldBeEmpty();
    }

    [Fact]
    public async Task AValidatorThatStopsWithoutItsAnswerRingsWithGoAnywayAndRemove()
    {
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutLeg(new LegRow
        {
            Flight = "TIA1", Name = "render", Order = 2, Goal = "Render it", DoneWhen = "It renders", State = LegState.Planned,
            Validation = LegValidation.Pending, Validator = "du1-tia1-render-validator", ValidatorJob = "a1b2c3d4",
        });
        _state.RaiseCard(CardBin.Handled, LegCommander.ValidatingKind, "flight/TIA1/render", "Validating TIA1/render");
        Worker(SessionState.Working);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Subject, card.Detail).ShouldBe((LegWatch.ValidationStalled, "flight/TIA1/render", "it stopped 4 min ago without its GO or NO-GO"));
        card.Choices.Select(c => (c.Verb, c.Args.FirstOrDefault())).ShouldBe([(Verbs.StartLeg, "anyway"), (Verbs.RemoveLeg, "its validation stalled")]);
    }

    [Fact]
    public async Task AnyPrsReviewerIsWatchedAsAFlightsIs()
    {
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "4f2c9a01d6e8",
            ClonePath = "/r", Worktree = "/r/.claude/worktrees/review-widget-7", Reviewer = "du1-review-widget-7", ReviewerJob = "a1b2c3d4",
            State = PrReviewState.Reviewing, StartedAt = Start,
        });
        Worker(SessionState.Working);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        // Told, then reviewed afresh by the node, then rung: as a flight's reviewer is.
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        (_said[^1].Verb, _said[^1].Target).ShouldBe((Verbs.ReviewPr, "pr/acme/widget#7"));
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        var card = Open().Where(c => c.Bin == CardBin.Ring).ShouldHaveSingleItem();
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ReviewStalled, "pr/acme/widget#7", "acme/widget#7's review stalled"));
        var again = card.Choices.ShouldHaveSingleItem();
        (again.Verb, again.Target).ShouldBe((Verbs.ReviewPr, "pr/acme/widget#7"));
    }

    [Fact]
    public async Task LegsNotFlyingAreNotWatched()
    {
        var leg = _state.Legs().Single();
        _state.PutLeg(leg with { State = LegState.Done });
        Worker(SessionState.Idle);
        _time.Advance(TimeSpan.FromHours(1));

        await _watch.LookAsync(Ct);

        (_said.Count, Open().Length).ShouldBe((0, 0));
    }

    private void Unplanned(string code = "PIL9") => _state.PutFlight(new FlightRow
    {
        Code = code, Repo = "pilot", RepoPath = "/r", Issue = 9, Branch = "flight/pil9", Worktree = "/r/pil9", Base = "origin/main",
        State = FlightState.Flying, CreatedAt = Start,
    });

    private void Planner(SessionState state, DateTimeOffset lastResponse) =>
        _state.PutSession(new SessionRow
        {
            Id = "e5f6a7b8",
            SessionId = "e5f6a7b8-0000-4000-8000-000000000000",
            Name = "du1-pil9-plan",
            Kind = SessionKind.Background,
            Cwd = "/r/pil9",
            Role = SessionRole.Planner,
            Flight = "PIL9",
            ContextTokens = 0,
            ContextWindow = 200_000,
            State = state,
            SpendWeighted = 0,
            LastResponseAt = lastResponse,
            StartedAt = Start,
        });

    [Fact]
    public async Task AFlightWithNoLegsAndNoPlannerGetsOneAfterTheGraceAndRingsWhenThatFails()
    {
        // PIL221 sat flying with no legs and no planner for days, and nothing said so (SE1, 2026-10-28).
        Worker(SessionState.Working);
        Unplanned();
        await _watch.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);
        var plan = _said.ShouldHaveSingleItem();
        (plan.Verb, plan.Target, plan.Who).ShouldBe((Verbs.PlanFlight, "flight/PIL9", "node"));
        Handled().ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.PlanStalled);

        // The fresh planner never shows: a look within the grace does nothing, the next rings with Plan again.
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        var ring = Open().ShouldHaveSingleItem();
        (ring.Bin, ring.Kind, ring.Subject).ShouldBe((CardBin.Ring, LegWatch.PlanStalled, "flight/PIL9"));
        ring.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Plan PIL9 again", Verbs.PlanFlight)]);

        // Legs planned: the ring closes.
        _state.PutLeg(_state.Legs().Single() with { Flight = "PIL9", Name = "first" });
        await _watch.LookAsync(Ct);
        Open().Where(c => c.Kind == LegWatch.PlanStalled).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIdlePlannerWithNoLegsIsToldToHandThemInThenRungNeverReplaced()
    {
        Worker(SessionState.Working);
        Unplanned();
        Planner(SessionState.Idle, Start);
        _time.Advance(TimeSpan.FromMinutes(4));

        await _watch.LookAsync(Ct);
        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target).ShouldBe((Verbs.Say, "session/e5f6a7b8"));
        say.Args[0].ShouldContain("planned command");

        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        var ring = Open().ShouldHaveSingleItem();
        (ring.Bin, ring.Kind).ShouldBe((CardBin.Ring, LegWatch.PlanStalled));
        ring.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Say);
    }

    [Fact]
    public async Task AFlightWithAnotherCardOpenAboutItIsLeftToThatCard()
    {
        Worker(SessionState.Working);
        Unplanned();
        _state.RaiseCard(CardBin.ForYou, "overlap", "flight/PIL9", "PIL9 looks like part of PIL8", null);
        _time.Advance(TimeSpan.FromMinutes(30));

        await _watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Open().ShouldHaveSingleItem().Kind.ShouldBe("overlap");
    }

    /// <summary>A stall ring's summary names the redo as the card words it, and Resume or Nudge only when the card offers one.</summary>
    private static void SummaryMatchesChoices(CardRow card)
    {
        var summary = card.Summary.ShouldNotBeNull();
        summary.ShouldContain("TIA1/parser");
        summary.ShouldContain(card.Choices.Single(c => c.Verb == Verbs.GoAround).Label);
        var say = card.Choices.FirstOrDefault(c => c.Verb == Verbs.Say)?.Label;
        if (say is null)
        {
            summary.ShouldNotContain("Resume");
            summary.ShouldNotContain("Nudge");
        }
        else
        {
            summary.ShouldContain(say);
            foreach (var other in new[] { "Resume now", "Resume", "Nudge" }.Where(l => l != say && !say.StartsWith(l, StringComparison.Ordinal)))
            {
                summary.ShouldNotContain(other);
            }
        }
    }

    [Fact]
    public async Task ANoResetStopRingsASummaryThatMatchesItsChoices()
    {
        // A usage-limit stop with no reset time, and the same stop once the resume did not take.
        Worker(SessionState.Idle, apiError: "overloaded");
        await _watch.LookAsync(Ct);
        var stopped = Open().ShouldHaveSingleItem();
        SummaryMatchesChoices(stopped);
        stopped.Summary!.ShouldNotContain("nothing to do");
    }

    [Fact]
    public async Task AUsageLimitStopAndItsFailedResumeRingSummariesMatchTheirChoices()
    {
        Worker(SessionState.Idle, apiError: "rate_limit", resetsAt: Start.AddHours(2));
        await _watch.LookAsync(Ct);
        var limited = Open().Single(c => c.Kind == LegWatch.Stopped);
        SummaryMatchesChoices(limited);
        limited.Summary!.ShouldContain("resumes it at");

        _time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3));
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);
        var again = Open().Single(c => c.Kind == LegWatch.Stopped);
        SummaryMatchesChoices(again);
        again.Summary!.ShouldContain("still stopped");
    }

    [Fact]
    public async Task AWaitingFailedMissingAndIdleWorkersRingsSummariesMatchTheirChoices()
    {
        // No worker at all: the redo alone.
        _time.Advance(TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
        var missing = Open().ShouldHaveSingleItem();
        missing.Kind.ShouldBe(LegWatch.Missing);
        missing.Choices.ShouldHaveSingleItem();
        SummaryMatchesChoices(missing);

        // A waiting worker is answered in its own session: the redo alone.
        Worker(SessionState.Waiting, waitingFor: "permission to run rm -rf build");
        await _watch.LookAsync(Ct);
        var waiting = Open().Single(c => c.Kind == LegWatch.Waiting);
        waiting.Choices.ShouldHaveSingleItem();
        SummaryMatchesChoices(waiting);
        waiting.Summary!.ShouldContain("waits on you");

        Worker(SessionState.Failed);
        await _watch.LookAsync(Ct);
        SummaryMatchesChoices(Open().Single(c => c.Kind == LegWatch.Failed));

        // An idle worker, nudged twice, then rung for: Nudge is offered and named.
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        foreach (var _ in Enumerable.Range(0, 3))
        {
            _time.Advance(TimeSpan.FromMinutes(4));
            await _watch.LookAsync(Ct);
        }
        var idle = Open().Single(c => c.Kind == LegWatch.Idle);
        idle.Choices.Select(c => c.Label).ShouldBe(["Nudge", "Redo leg parser"]);
        SummaryMatchesChoices(idle);
    }
}
