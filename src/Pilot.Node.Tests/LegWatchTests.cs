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
        // The node resumes it itself, so nobody is asked: a Handled line, no open card.
        Open().ShouldBeEmpty();
        var wait = Handled().ShouldHaveSingleItem();
        (wait.Kind, wait.Subject).ShouldBe((LegWatch.LimitWait, "flight/TIA1/parser"));

        _time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3));
        await _watch.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/a1b2c3d4", "node"));
        say.Args[0].ShouldContain("limit that stopped you has reset");
        Open().ShouldBeEmpty();
        Handled().Select(c => c.Kind).Order().ShouldBe([LegWatch.LimitWait, "worker-resumed"]);

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
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Tell du1-tia1-parser to carry on with parser", Verbs.Say), ("Start a fresh worker on parser (the last one was stopped by the API)", Verbs.GoAround)]);
    }

    private void Recheck(string? outcome) =>
        _state.PutLeg(_state.Legs("TIA1").Single() with { Recheck = new LegRecheck { Check = "dotnet test", Since = Start, Outcome = outcome } });

    [Fact]
    public async Task AnIdleWorkerWhoseFinishIsRerunningItsCheckIsNeitherNudgedNorRung()
    {
        // Four workers went idle on a finish the node answered queued, were nudged twice and rung (pilot #487, 2026-10-30).
        Worker(SessionState.Idle);
        Recheck(null);

        foreach (var _ in Enumerable.Range(0, 5))
        {
            _time.Advance(TimeSpan.FromMinutes(4));
            await _watch.LookAsync(Ct);
        }

        _said.ShouldBeEmpty();
        Open().ShouldBeEmpty();
        Handled().ShouldBeEmpty();
        _state.Legs("TIA1").Single().Recheck.ShouldNotBeNull().Outcome.ShouldBeNull();
    }

    [Fact]
    public async Task AFailedRerunsOutcomeIsSaidToItsWorkerOnceAndClearedWithNoNudgeAfterIt()
    {
        Worker(SessionState.Idle);
        var outcome = LegRecheck.FailedOutcome("dotnet test", 1, "Failed: 2, Passed: 40");
        Recheck(outcome);

        await _watch.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/a1b2c3d4", "node"));
        say.Args[0].ShouldBe(outcome);
        say.Args[0].ShouldContain("(1)");
        say.Args[0].ShouldContain("Failed: 2, Passed: 40");
        say.Args[0].ShouldContain("again");
        _state.Legs("TIA1").Single().Recheck.ShouldBeNull();
        Open().ShouldBeEmpty();

        // Said once: a later look says nothing more of it, and the worker, woken by it, is given its grace before a nudge.
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARerunThatEndedByTheLegChangingOrTheNodeRestartingTellsItsWorkerToFinishAgain(bool restarted)
    {
        Worker(SessionState.Idle);
        Recheck(restarted ? LegRecheck.RestartedOutcome("dotnet test") : LegRecheck.ChangedOutcome("dotnet test"));

        await _watch.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        say.Args[0].ShouldContain(restarted ? "node restarted" : "leg changed");
        say.Args[0].ShouldContain("`leg finish done` again");
        _state.Legs("TIA1").Single().Recheck.ShouldBeNull();
    }

    [Fact]
    public async Task ARerunOutcomeThatCannotBeSaidRingsIdleAndIsStillClearedSoItIsNotSaidTwice()
    {
        Worker(SessionState.Idle);
        Recheck(LegRecheck.ChangedOutcome("dotnet test"));
        _refuse = "no such session";

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.Ring, LegWatch.Idle));
        card.Title.ShouldContain("check rerun ended but its worker could not be told");
        _state.Legs("TIA1").Single().Recheck.ShouldBeNull();
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
        card.Title.ShouldContain("though it answers its nudges");
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

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Running)]
    public async Task AWorkerWaitingOnItsLegsCheckJobIsNeitherNudgedNorRungFor(JobState jobState)
    {
        // A check job has the flight and leg and no owner, so the owner match missed it: the worker was nudged and rung for while its check ran.
        Worker(SessionState.Idle);
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = "/work/tia1", Kind = JobKind.Check, Flight = "TIA1", Leg = "parser", State = jobState,
            LogPath = "/tmp/j1.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start,
        });

        foreach (var _ in Enumerable.Range(0, 4))
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await _watch.LookAsync(Ct);
        }

        _said.ShouldBeEmpty();
        Open().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(JobState.Queued, true)]
    [InlineData(JobState.Running, true)]
    [InlineData(JobState.Queued, false)]
    public async Task AWorkerWithAJobOfItsOwnQueuedOrRunningIsNeitherNudgedNorRungFor(JobState jobState, bool byRowId)
    {
        Worker(SessionState.Idle);
        var worker = _state.Sessions().First();
        _state.PutJob(new JobRow
        {
            Id = "j2", Command = "dotnet build", Cwd = "/work/tia1", State = jobState, Owner = byRowId ? worker.Id : "c0000000", OwnerName = worker.Name,
            LogPath = "/tmp/j2.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start,
        });

        foreach (var _ in Enumerable.Range(0, 4))
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await _watch.LookAsync(Ct);
        }

        _said.ShouldBeEmpty();
        Open().ShouldBeEmpty();
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
        card.Choices.Select(c => c.Label).ShouldBe(["Nudge du1-tia1-parser to finish parser or say why not", "Start a fresh worker on parser (the last one went idle with the leg unfinished)"]);
    }

    [Fact]
    public async Task ANudgeTheNodeCannotDeliverRings()
    {
        Worker(SessionState.Ended);
        _refuse = "the folder is gone";
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Detail).ShouldBe((LegWatch.Idle, "the node tried to nudge it: the folder is gone"));
        card.Title.ShouldEndWith("could not be nudged: the folder is gone");
    }

    [Fact]
    public async Task AWaitingWorkerRingsWithItsQuestionAndTheCardClosesWhenItWorksAgain()
    {
        Worker(SessionState.Waiting, waitingFor: "permission to run rm -rf build");

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Kind.ShouldBe(LegWatch.Waiting);
        card.Title.ShouldBe("TIA1/parser's worker waits on you for permission to run rm -rf build");
        card.Detail.ShouldNotBeNull().ShouldStartWith("permission to run rm -rf build;");
        card.Choices.Select(c => c.Label).ShouldBe(["Start a fresh worker on parser (the last one waited on you)"]);

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

        // The first no-show is a restart (a start that did not take), the second rings.
        _time.Advance(TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
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
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ReviewStalled, "flight/TIA1", "TIA1's reviewer stalled: it stopped 12 min ago without its verdict"));
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
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ValidationStalled, "flight/TIA1/render", "TIA1/render's validator stalled: it stopped 4 min ago without its GO or NO-GO"));
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
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ReviewStalled, "pr/acme/widget#7", "acme/widget#7's reviewer stalled: it stopped 12 min ago without its verdict"));
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

    private void Planner(SessionState state, DateTimeOffset lastResponse, bool gone = false) =>
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
            Gone = gone,
            StartedAt = Start,
        });

    private readonly List<string> _stopped = [];
    private readonly List<string> _restarted = [];
    private (string Job, DateTimeOffset At)? _started;

    private void HookPlanners()
    {
        _watch.StartedPlanner = _ => _started;
        _watch.StopPlanner = (code, _) => { _stopped.Add(code); _started = null; return Task.CompletedTask; };
        _watch.RestartPlanner = (code, _) =>
        {
            _restarted.Add(code);
            _started = ("fresh" + _restarted.Count, _time.GetUtcNow());
            _state.RemoveSession("e5f6a7b8");
            return Task.CompletedTask;
        };
    }

    private void SilentPlanner() => _state.PutSession(new SessionRow
    {
        Id = "e5f6a7b8", SessionId = "e5f6a7b8-0000-4000-8000-000000000000", Name = "du1-pil9-plan", Kind = SessionKind.Background,
        Cwd = "/r/pil9", Role = SessionRole.Planner, Flight = "PIL9", ContextTokens = 0, ContextWindow = 200_000,
        State = SessionState.Working, SpendWeighted = 0, LastResponseAt = null, StartedAt = _time.GetUtcNow(),
    });

    [Fact]
    public async Task APlannerThatNeverTakesATurnIsStoppedAndStartedOnceMoreThenRungWhenTheSecondAlsoShowsNothing()
    {
        HookPlanners();
        Worker(SessionState.Working);
        Unplanned();
        SilentPlanner();
        _started = ("e5f6a7b8", Start);
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _restarted.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _restarted.ShouldBe(["PIL9"]);
        var handled = Handled().ShouldHaveSingleItem();
        (handled.Kind, handled.Title).ShouldBe((LegWatch.PlanStalled, "PIL9's planner (job e5f6a7b8) took no first turn and was started again"));

        // The second one never appears in the listing at all: same ladder, then the ring.
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _stopped.ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _stopped.ShouldBe(["PIL9"]);
        _restarted.Count.ShouldBe(1);
        var ring = Open().ShouldHaveSingleItem();
        (ring.Bin, ring.Title).ShouldBe((CardBin.Ring, "PIL9's planner never took a turn, twice"));
        ring.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Plan PIL9 again", Verbs.PlanFlight)]);

        // A further look does not start a third.
        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);
        _restarted.Count.ShouldBe(1);
        Open().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task APlannerThatTakesItsFirstTurnWithinTheGraceIsLeftAlone()
    {
        HookPlanners();
        Worker(SessionState.Working);
        Unplanned();
        SilentPlanner();
        _time.Advance(TimeSpan.FromMinutes(2));
        _state.PutSession(_state.Sessions().Single(s => s.Id == "e5f6a7b8") with { ContextTokens = 12_000, LastResponseAt = _time.GetUtcNow() });
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);

        (_restarted.Count, _stopped.Count, _said.Count, Open().Length).ShouldBe((0, 0, 0, 0));
    }

    [Fact]
    public async Task AStartedPlannerThatNeverAppearsInTheListingFollowsTheSameLadder()
    {
        HookPlanners();
        Worker(SessionState.Working);
        Unplanned();
        _started = ("job1", Start);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _restarted.ShouldBe(["PIL9"]);
        Handled().ShouldHaveSingleItem().Title.ShouldContain("job1");
    }

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
    public async Task APlannerThatWaitsForAdmissionIsWaitingNeverStalledAndRingsNothing()
    {
        Worker(SessionState.Working);
        Unplanned();
        _state.UpdateFlight("PIL9", f => f with { Queued = "waiting for an Opus slot: 1 of 1 flying: du1-other" });

        _time.Advance(TimeSpan.FromHours(3));
        await _watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Open().Where(c => c.Kind == LegWatch.PlanStalled).ShouldBeEmpty();
        Handled().Where(c => c.Kind == LegWatch.PlanStalled).ShouldBeEmpty();
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
    public async Task AnIdlePlannerWithNoProcessWasStoppedFromOutsideAndIsReplacedNotToldToFinish()
    {
        // PIL530: claude stop leaves the row idle with no process; it holds no role, so the next sweep starts a fresh planner.
        Worker(SessionState.Working);
        Unplanned();
        Planner(SessionState.Idle, Start, gone: true);
        _time.Advance(TimeSpan.FromMinutes(4));

        await _watch.LookAsync(Ct);

        var plan = _said.ShouldHaveSingleItem();
        (plan.Verb, plan.Target).ShouldBe((Verbs.PlanFlight, "flight/PIL9"));
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
            foreach (var other in new[] { "Nudge" }.Where(l => l != say && !say.StartsWith(l, StringComparison.Ordinal)))
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
        Open().ShouldBeEmpty();
        Handled().Single(c => c.Kind == LegWatch.LimitWait).Summary!.ShouldContain("resumes it by itself");

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
        // No worker at all: the redo alone (after its one restart).
        _time.Advance(TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
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
        idle.Choices.Select(c => c.Label).ShouldBe(["Nudge du1-tia1-parser to finish parser or say why not", "Start a fresh worker on parser (the last one went idle with the leg unfinished)"]);
        SummaryMatchesChoices(idle);
    }

    // ---- the first-turn ladder for workers, validators and reviewers (PIL530) ----

    private readonly List<string> _restartedWorkers = [];
    private int _fresh;

    private void HookWorkers()
    {
        _watch.StopSession = (job, _) => { _stopped.Add(job); return Task.CompletedTask; };
        _watch.RestartWorker = (flight, leg, _) =>
        {
            _restartedWorkers.Add($"{flight}/{leg}");
            var row = _state.Legs(flight).Single(l => l.Name == leg);
            _stopped.Add(row.WorkerJob!);
            _state.RemoveSession(row.WorkerJob!);
            _state.PutLeg(row with { WorkerJob = "fresh" + ++_fresh, StartedAt = _time.GetUtcNow() });
            return Task.CompletedTask;
        };
    }

    private void Silent(string id, SessionState state = SessionState.Working, SessionRole role = SessionRole.Worker) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "du1-tia1-parser", Kind = SessionKind.Background, Cwd = "/work/tia1",
        Role = role, Flight = "TIA1", Leg = role == SessionRole.Worker ? "parser" : null, ContextTokens = 0, ContextWindow = 200_000, State = state,
        SpendWeighted = 0, LastResponseAt = null, StartedAt = _time.GetUtcNow(),
    });

    [Fact]
    public async Task AWorkerThatNeverTakesATurnIsStoppedAndStartedOnceThenRungWhenTheSecondShowsNothing()
    {
        HookWorkers();
        Silent("a1b2c3d4");
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _restartedWorkers.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _restartedWorkers.ShouldBe(["TIA1/parser"]);
        _stopped.ShouldBe(["a1b2c3d4"]);
        var handled = Handled().ShouldHaveSingleItem();
        (handled.Kind, handled.Title).ShouldBe((LegWatch.NeverTurned, "TIA1/parser's worker (job a1b2c3d4) took no first turn and was started again"));
        // Not counted as a go-around of the work, and never nudged.
        _state.Legs("TIA1").Single().GoArounds.ShouldBe(0);
        _said.ShouldBeEmpty();

        // The fresh one is listed and says nothing either.
        Silent("fresh1");
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _stopped.ShouldBe(["a1b2c3d4", "fresh1"]);
        _restartedWorkers.Count.ShouldBe(1);
        var ring = Open().ShouldHaveSingleItem();
        (ring.Bin, ring.Kind, ring.Title).ShouldBe((CardBin.Ring, LegWatch.NeverTurned, "TIA1/parser's worker never took a turn after two starts"));
        ring.Choices.Select(c => c.Verb).ShouldBe([Verbs.GoAround]);
        _said.ShouldBeEmpty();

        // No third start, no repeat.
        _time.Advance(TimeSpan.FromMinutes(20));
        await _watch.LookAsync(Ct);
        (_restartedWorkers.Count, Open().Length, _said.Count).ShouldBe((1, 1, 0));
    }

    [Fact]
    public async Task AWorkerThatNeverShowsInTheListingIsRetriedOnceBeforeTheMissingRing()
    {
        HookWorkers();
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _restartedWorkers.ShouldBe(["TIA1/parser"]);
        Open().ShouldBeEmpty();
        Handled().ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.NeverTurned);

        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _restartedWorkers.Count.ShouldBe(1);
        var ring = Open().ShouldHaveSingleItem();
        (ring.Kind, ring.Title).ShouldBe((LegWatch.Missing, "TIA1/parser's worker du1-tia1-parser never showed up, 4 min after it started"));
        ring.Detail!.ShouldContain("job fresh1");
    }

    [Fact]
    public async Task AWorkerThatTakesATurnWithinTheGraceIsLeftAloneAndTheIdleLadderIsAsBefore()
    {
        HookWorkers();
        Silent("a1b2c3d4");
        _time.Advance(TimeSpan.FromMinutes(2));
        _state.PutSession(_state.Sessions().Single() with { ContextTokens = 9_000, LastResponseAt = _time.GetUtcNow() });
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        (_restartedWorkers.Count, _stopped.Count, _said.Count, Open().Length).ShouldBe((0, 0, 0, 0));

        // It goes idle after that: nudged, as before.
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _said.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Say);
        _restartedWorkers.ShouldBeEmpty();
    }

    private void Reviewing(string job) => _state.PutFlight(new FlightRow
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "origin/main",
        State = FlightState.Flying, CreatedAt = Start,
        Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "du1-tia1-review", ReviewerJob = job, StartedAt = _time.GetUtcNow() },
    });

    [Fact]
    public async Task AReviewerThatNeverTakesATurnIsRestartedOnceThenRung()
    {
        HookWorkers();
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        Reviewing("rev1");
        Silent("rev1", role: SessionRole.Reviewer);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        // Started afresh through the existing review path, never told to finish a verdict it has not begun.
        (_said.ShouldHaveSingleItem().Verb, _said[0].Target).ShouldBe((Verbs.StartReview, "flight/TIA1"));
        Handled().ShouldHaveSingleItem().Title.ShouldBe("TIA1's reviewer (job rev1) took no first turn and was started again");
        Open().ShouldNotContain(c => c.Bin != CardBin.Handled);

        // The fresh reviewer is in place and shows nothing either.
        _state.RemoveSession("rev1");
        Reviewing("rev2");
        Silent("rev2", role: SessionRole.Reviewer);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        _stopped.ShouldBe(["rev2"]);
        var ring = Open().Single(c => c.Bin == CardBin.Ring);
        (ring.Kind, ring.Title).ShouldBe((LegWatch.ReviewStalled, "TIA1's reviewer never took a turn after two starts"));
        ring.Choices.Select(c => c.Verb).ShouldBe([Verbs.StartReview]);
    }

    [Fact]
    public async Task AReviewerThatTakesATurnWithinTheGraceIsUntouched()
    {
        HookWorkers();
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        Reviewing("rev1");
        Silent("rev1", role: SessionRole.Reviewer);
        _time.Advance(TimeSpan.FromMinutes(2));
        _state.PutSession(_state.Sessions().Single() with { ContextTokens = 5_000, LastResponseAt = _time.GetUtcNow() });
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        (_said.Count, _stopped.Count, Open().Length, Handled().Length).ShouldBe((0, 0, 0, 0));
    }
}
