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

    [Fact]
    public async Task AWaitingWorkerWhosePendingCallIsKnownRingsWithWhatItWantsAndTheToolInBrackets()
    {
        Worker(SessionState.Waiting, waitingFor: "permission to use Bash");
        _state.PutSession(_state.Sessions().Single() with { Asking = "Bash: git push origin main" });

        await _watch.LookAsync(Ct);

        var card = Open().Single(c => c.Kind == LegWatch.Waiting);
        card.Title.ShouldBe("TIA1/parser's worker wants to run: git push origin main (Bash)");
        card.Detail.ShouldNotBeNull().ShouldStartWith("it wants to run: git push origin main (Bash);");
        card.Summary.ShouldNotBeNull().ShouldStartWith("The worker of TIA1/parser wants to run: git push origin main (Bash).");
        SummaryMatchesChoices(card);
    }

    private void Worker(SessionState state, DateTimeOffset? lastResponse = null, string? apiError = null, DateTimeOffset? resetsAt = null, string? waitingFor = null, long context = 0, string id = "a1b2c3d4") =>
        _state.PutSession(new SessionRow
        {
            Id = id,
            SessionId = id + "-0000-4000-8000-000000000000",
            Name = "du1-tia1-parser",
            Kind = SessionKind.Background,
            Cwd = "/work/tia1",
            Role = SessionRole.Worker,
            Flight = "TIA1",
            Leg = "parser",
            ContextTokens = context,
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
        card.Title.ShouldBe("TIA1/parser's worker was stopped by the API: overloaded");
        card.Summary.ShouldBe("The worker of TIA1/parser was stopped by the API and no reset time is known. Telling it to carry on may get it going again; otherwise a fresh start lets it go.");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Tell the worker to carry on", Verbs.Say), (Verbs.RestartWorker, Verbs.GoAround)]);
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
    public async Task ARerunWhoseCheckJobCarriedOverTheRestartTellsItsWorkerToFinishAgainWhichWaitsOnThatJob()
    {
        Worker(SessionState.Idle);
        Recheck(LegRecheck.CarriedOutcome("dotnet test"));

        await _watch.LookAsync(Ct);

        var said = _said.ShouldHaveSingleItem().Args[0];
        said.ShouldContain("carried over the restart");
        said.ShouldContain("keeps its place");
        said.ShouldContain("`leg finish done` again");
        said.ShouldContain("waits on that same job");
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
        card.Title.ShouldBe("TIA1 is stuck: its worker cannot be told its checks ended");
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
        card.Title.ShouldBe("TIA1 is stuck: its worker stopped working");
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

    private void EndedUntoldJob()
    {
        _state.PutJob(new JobRow
        {
            Id = "j9", Command = "dotnet test", Cwd = "/work/tia1", State = JobState.Succeeded, Owner = "a1b2c3d4", OwnerName = "du1-tia1-parser",
            LogPath = "/tmp/j9.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start, EndedAt = Start.AddMinutes(1), Told = false,
        });
    }

    [Fact]
    public async Task AnIdleWorkerWithAnEndedUntoldJobIsToldItAndNeitherNudgedNorRung()
    {
        // The hook goes through a real JobRunner's tell, as the node wires it (NodeApp.Services.cs).
        var root = Directory.CreateTempSubdirectory("legwatch-tell-").FullName;
        try
        {
            var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(root, "n.sock"), Path.Combine(root, "data")), _time) { Say = Run };
            _watch.TellEndedJobs = jobs.TellUntoldOf;
            Worker(SessionState.Idle);
            EndedUntoldJob();
            _time.Advance(TimeSpan.FromMinutes(10));

            await _watch.LookAsync(Ct);

            _state.Jobs().Single(j => j.Id == "j9").Told.ShouldBeTrue();
            var told = _said.ShouldHaveSingleItem();
            told.Target.ShouldBe(Verbs.SessionTarget("a1b2c3d4"));
            told.Args.ShouldHaveSingleItem().ShouldContain("Your job j9");
            Open().ShouldBeEmpty();
            Handled().ShouldNotContain(c => c.Kind == "worker-nudged");
            Handled().ShouldContain(c => c.Kind == "worker-told-job");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnIdleWorkerWhoseEndedJobCouldNotBeToldFallsThroughToTheNudge()
    {
        Worker(SessionState.Idle);
        EndedUntoldJob();
        _watch.TellEndedJobs = (_, _) => Task.FromResult(false);
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        _said.ShouldHaveSingleItem();
        Handled().ShouldContain(c => c.Kind == "worker-nudged");
    }

    [Fact]
    public async Task AnIdleWorkerWithNoUntoldJobIsStillNudgedWhenTheHookIsSet()
    {
        Worker(SessionState.Idle);
        _watch.TellEndedJobs = (_, _) => throw new InvalidOperationException("must not be called");
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        _said.ShouldHaveSingleItem();
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
        // The golden card (#797): what happened, what to do, and two short verbs; the step count and the nudges are the detail's.
        card.Title.ShouldBe("TIA1 is stuck: its worker stopped working");
        card.Summary.ShouldBe("The worker of TIA1/parser stopped with its leg unfinished. A nudge may get it going again; otherwise a fresh start lets it go.");
        card.Choices.Select(c => c.Label).ShouldBe(["Nudge the worker", Verbs.RestartWorker]);
        card.Detail.ShouldNotBeNull().ShouldContain("1 of 2 steps");
    }

    private void Flight(FlightState state) => _state.PutFlight(new FlightRow
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Title = "Parse it", Branch = "flight/tia1", Worktree = "/r/.claude/worktrees/TIA1",
        Base = "origin/main", State = state, CreatedAt = Start, LandedAt = state == FlightState.Landed ? Start : null,
    });

    // c3651, 2026-11-06: TIA2000 landed and its cards closed, yet its leg review-notes-2 was still flying with an idle worker, so the watch rang again.
    [Fact]
    public async Task AFlyingLegOfALandedFlightRaisesNoCardWhereTheSameLegOfAFlyingFlightRings()
    {
        Worker(SessionState.Idle);
        _time.Advance(TimeSpan.FromMinutes(10));

        Flight(FlightState.Flying);
        foreach (var _ in Enumerable.Range(0, 4))
        {
            await _watch.LookAsync(Ct);
        }
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.Idle);

        // It lands: its cards close with it, and the leg is still flying with the worker idle.
        _state.CloseCardsAbout(Verbs.FlightTarget("TIA1"), "TIA1 landed");
        Flight(FlightState.Landed);
        var said = _said.Count;
        var cards = _state.Cards().Count(c => c.Kind != LegWatch.StraySession);
        foreach (var _ in Enumerable.Range(0, 4))
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            await _watch.LookAsync(Ct);
        }

        // The worker's session still runs, so the stray watch may offer to stop it: that card is about the session, not the leg.
        AboutTheFlight().ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind != LegWatch.StraySession).ShouldBe(cards);
        _said.Count.ShouldBe(said);
    }

    private CardRow[] AboutTheFlight() => Open().Where(c => c.Subject.StartsWith(Verbs.FlightTarget("TIA1"), StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task ACardAboutALegRaisedBeforeItsFlightLandsIsClosedAtTheLandingAndNotRaisedAgain()
    {
        Worker(SessionState.Idle);
        Flight(FlightState.Flying);
        _time.Advance(TimeSpan.FromMinutes(10));
        foreach (var _ in Enumerable.Range(0, 4))
        {
            await _watch.LookAsync(Ct);
        }
        var card = Open().ShouldHaveSingleItem();
        card.Subject.ShouldBe("flight/TIA1/parser");

        _state.CloseCardsAbout(Verbs.FlightTarget("TIA1"), "TIA1 landed");
        Flight(FlightState.Landed);
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        AboutTheFlight().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIdleWorkerWithRoomRingsAndItsDetailSaysItIsIdleWithRoom()
    {
        Worker(SessionState.Idle, context: 20_000);
        _time.Advance(TimeSpan.FromMinutes(10));
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await _watch.LookAsync(Ct);
        }

        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.Ring, LegWatch.Idle));
        card.Detail.ShouldNotBeNull().ShouldContain("; idle with room in its context (10% of its window used) and no job of its own running; ");
    }

    [Fact]
    public async Task AnIdleWorkerWaitingOnAJobOfItsOwnRaisesNoRingAndOneHandledLineSayingSo()
    {
        Worker(SessionState.Idle, context: 190_000);
        _state.PutJob(new JobRow
        {
            Id = "j3", Command = "dotnet test", Cwd = "/work/tia1", State = JobState.Running, Owner = "a1b2c3d4", OwnerName = "du1-tia1-parser",
            LogPath = "/tmp/j3.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start,
        });
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        Open().ShouldBeEmpty();
        _said.ShouldBeEmpty();
        var line = Handled().ShouldHaveSingleItem();
        line.Kind.ShouldBe("worker-waiting-job");
        line.Title.ShouldBe("TIA1's worker is waiting for its job to end");
    }

    [Fact]
    public void AFullContextIdleWorkerIsClassifiedFullAndOneWithRoomAsRoom()
    {
        Worker(SessionState.Idle, context: 190_000);
        var leg = _state.Legs().First();
        _watch.ClassifyIdle(leg, _state.Sessions().First()).ShouldBe(LegWatch.IdleCause.Full);
        Worker(SessionState.Idle, context: 1_000);
        _watch.ClassifyIdle(leg, _state.Sessions().First()).ShouldBe(LegWatch.IdleCause.Room);
    }

    [Fact]
    public async Task ARefusedCompactionSendsTheLegRoundOnTheNextLook()
    {
        Worker(SessionState.Idle, context: 190_000);
        _time.Advance(TimeSpan.FromMinutes(4));
        _refuse = "no such session";
        await _watch.LookAsync(Ct);
        _said.Single().Verb.ShouldBe(Verbs.Compact);
        _refuse = null;

        await _watch.LookAsync(Ct);

        _said.Count.ShouldBe(2);
        _said[1].Verb.ShouldBe(Verbs.GoAround);
        _said[1].Args[0].ShouldContain("context was full");
        _said.Any(c => c.Verb == Verbs.Say).ShouldBeFalse();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task ACompactionThatNeverAnswersIsSpentAfterTheWaitAndTheLegGoesRound()
    {
        Worker(SessionState.Idle, context: 190_000);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);

        _time.Advance(LegWatch.CompactWait);
        await _watch.LookAsync(Ct);

        _said.Count.ShouldBe(2);
        _said[1].Verb.ShouldBe(Verbs.GoAround);
    }

    [Fact]
    public async Task AnIdleWorkerWaitingOnAJobOfItsLegAloneRaisesNoRing()
    {
        Worker(SessionState.Idle, context: 1_000);
        _state.PutJob(new JobRow
        {
            Id = "j4", Command = "dotnet test", Cwd = "/work/tia1", State = JobState.Queued, Owner = "zzzz", OwnerName = "someone-else",
            Flight = "TIA1", Leg = "parser", LogPath = "/tmp/j4.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start,
        });
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        Open().ShouldBeEmpty();
        Handled().ShouldHaveSingleItem().Kind.ShouldBe("worker-waiting-job");
    }

    [Fact]
    public async Task AnIdleWorkerAtNinetyFivePercentIsCompactedAndNotNudged()
    {
        Worker(SessionState.Idle, context: 190_000);
        _time.Advance(TimeSpan.FromMinutes(4));

        await _watch.LookAsync(Ct);

        var compact = _said.ShouldHaveSingleItem();
        (compact.Verb, compact.Target, compact.Who).ShouldBe((Verbs.Compact, "session/a1b2c3d4", "node"));
        compact.Args[0].ShouldContain("job summaries");
        _said.Any(c => c.Verb == Verbs.Say).ShouldBeFalse();
        Open().ShouldBeEmpty();
        Handled().ShouldHaveSingleItem().Kind.ShouldBe("worker-compacted");

        // Idle on and not yet answered: neither nudged nor compacted twice.
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnIdleWorkerStillFullAfterItsCompactionIsReplacedByAGoAroundAndAReplacementThatFillsAgainRings()
    {
        Worker(SessionState.Idle, context: 190_000);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _said.Single().Verb.ShouldBe(Verbs.Compact);

        // It answered the compaction and is idle, still full.
        _time.Advance(TimeSpan.FromMinutes(4));
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow(), context: 192_000);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        _said.Count.ShouldBe(2);
        var around = _said[1];
        (around.Verb, around.Target, around.Who).ShouldBe((Verbs.GoAround, "flight/TIA1/parser", "node"));
        around.Args[0].ShouldContain("context was full");
        around.Args[0].ShouldContain("96%");
        Open().ShouldBeEmpty();
        Handled().Select(c => c.Kind).ShouldContain("worker-replaced");

        // The go-around gave the leg a fresh worker, which fills and stalls too: rung, naming the full context.
        _state.PutLeg(_state.Legs("TIA1").Single() with { WorkerJob = "e5f6a7b8" });
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow(), context: 185_000, id: "e5f6a7b8");
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        _said.Count.ShouldBe(2);
        // (The first worker still shows as a stray in this test, as a real one is stopped by the node; only the idle ring is looked at.)
        var card = Open().Where(c => c.Kind != LegWatch.StraySession).ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.Ring, LegWatch.Idle));
        card.Title.ShouldBe("TIA1 is stuck: its worker's memory is full again");
        card.Title.ShouldNotContain("nudged");
    }

    [Fact]
    public async Task AnIdleWorkerUnderNinetyPercentIsNudgedAsBefore()
    {
        Worker(SessionState.Idle, context: 170_000);
        _time.Advance(TimeSpan.FromMinutes(4));

        await _watch.LookAsync(Ct);

        _said.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Say);
        Handled().ShouldHaveSingleItem().Kind.ShouldBe("worker-nudged");
    }

    [Fact]
    public async Task ANudgeTheNodeCannotDeliverRings()
    {
        Worker(SessionState.Ended);
        _refuse = "the folder is gone";
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Kind.ShouldBe(LegWatch.Idle);
        // The reason is the detail's; the title says what happened.
        card.Detail.ShouldNotBeNull().ShouldEndWith("idle with room in its context (0% of its window used) and no job of its own running; the node tried to nudge it: the folder is gone");
        card.Title.ShouldBe("TIA1 is stuck: its worker stopped and cannot be nudged");
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
        card.Summary.ShouldBe("The worker of TIA1/parser waits on you for an answer. Answering in its own session lets this worker go on with the call; a fresh start stops this one and starts another on the leg from the branch.");
        card.Choices.Select(c => c.Label).ShouldBe([Verbs.RestartWorker]);

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
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ReviewStalled, "flight/TIA1", "The review of TIA1 stopped without a verdict"));
        card.Summary.ShouldBe("The review of TIA1 stopped twice without a verdict. A fresh start is the way on.");
        card.Detail.ShouldNotBeNull().ShouldStartWith("it stopped 12 min ago without its verdict;");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Restart the reviewer", Verbs.StartReview)]);
        _said.Count.ShouldBe(2);
    }

    private void ReviewingFlight(JobState? job, DateTimeOffset? jobEnded = null)
    {
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start,
            Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "du1-tia1-review", ReviewerJob = "a1b2c3d4", StartedAt = Start },
        });
        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        if (job is { } state)
        {
            _state.PutJob(new JobRow
            {
                Id = "rj1", Command = "dotnet test", Cwd = "/r/w", State = state, Owner = "a1b2c3d4", OwnerName = "du1-tia1-review",
                LogPath = "/tmp/rj1.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start, EndedAt = jobEnded,
            });
        }
    }

    [Theory]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Queued)]
    public async Task AReviewerIdleWithAJobOfItsOwnRunningOrQueuedIsNeitherToldNorRestartedNorRung(JobState job)
    {
        ReviewingFlight(job);
        _time.Advance(TimeSpan.FromMinutes(30));

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegWatch.ReviewStalled);
    }

    [Fact]
    public async Task AReviewerWhoseJobEndedRecentlyGetsTheGraceFromThenAndIsNotStalledYet()
    {
        ReviewingFlight(null);
        _time.Advance(TimeSpan.FromMinutes(30));
        _state.PutJob(new JobRow
        {
            Id = "rj1", Command = "dotnet test", Cwd = "/r/w", State = JobState.Succeeded, Owner = "a1b2c3d4", OwnerName = "du1-tia1-review",
            LogPath = "/tmp/rj1.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start, EndedAt = _time.GetUtcNow(),
        });
        _time.Advance(TimeSpan.FromMinutes(1));

        await _watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegWatch.ReviewStalled);
    }

    [Fact]
    public async Task AReviewerIdlePastTheGraceWithNoJobIsToldToGiveItsVerdict()
    {
        ReviewingFlight(null);
        _time.Advance(TimeSpan.FromMinutes(30));

        await _watch.LookAsync(Ct);

        (_said.ShouldHaveSingleItem().Verb, _said[0].Target).ShouldBe((Verbs.Say, "session/a1b2c3d4"));
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
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ValidationStalled, "flight/TIA1/render", "TIA1 cannot start: its validator stopped without an answer"));
        card.Summary.ShouldBe("The check of TIA1/render against the tree never answered, so the leg cannot start. Fly it unchecked, or take the leg off the flight.");
        card.Detail.ShouldNotBeNull().ShouldStartWith("it stopped 4 min ago without its GO or NO-GO;");
        card.Choices.Select(c => (c.Label, c.Verb, c.Args.FirstOrDefault())).ShouldBe([("Start without validation", Verbs.StartLeg, "anyway"), ("Remove the leg", Verbs.RemoveLeg, "its validation stalled")]);
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
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ReviewStalled, "pr/acme/widget#7", "The review of acme/widget#7 stopped without a verdict"));
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

    /// <summary>
    /// A stall ring's summary names the leg in plain words (CardLint) and never says again what a button says (#797): the buttons
    /// do that. It speaks of telling the worker to go on only when the card offers that.
    /// </summary>
    private static void SummaryMatchesChoices(CardRow card)
    {
        var summary = card.Summary.ShouldNotBeNull();
        summary.ShouldContain("TIA1/parser");
        CardLint.FirstLine(summary).ShouldBeEmpty();
        CardLint.ChoiceProse(summary, card.Choices.Select(c => c.Label)).ShouldBeEmpty();
        card.Choices.ShouldContain(c => c.Verb == Verbs.GoAround);
        if (!card.Choices.Any(c => c.Verb == Verbs.Say))
        {
            summary.ShouldNotContain("Resume");
            summary.ShouldNotContain("Nudge");
            summary.ShouldNotContain("carry on");
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
        waiting.Summary!.ShouldContain("lets this worker go on with the call");
        waiting.Summary!.ShouldContain("stops this one and starts another on the leg from the branch");

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
        idle.Choices.Select(c => c.Label).ShouldBe(["Nudge the worker", Verbs.RestartWorker]);
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
