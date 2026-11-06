using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>An Idle session holds an Opus slot only while it has work pending (#569).</summary>
/// <remarks>Slow: every test starts legs of real flights in worktrees (5.5 to 6.4 s under load).</remarks>
[Trait("Category", "Slow")]
public sealed class IdleAdmissionTests : LegCommanderTestsBase
{
    private void Settings(int cap, string onePerRepo = "off")
    {
        var settings = NodeSettings.With(new NodeSettings(), "opus-cap", [cap.ToString()]).Settings!;
        settings = NodeSettings.With(settings, "worker-range", ["opus"]).Settings!;
        settings = NodeSettings.With(settings, "one-flight-per-repo", [onePerRepo]).Settings!;
        _legs.Models = new RoleModels(settings);
    }

    // A session that has taken its first turn, unless <paramref name="turned"/> is false (just started: no context, no response yet).
    private void Put(string name, SessionState state, bool turned = true, string? flight = null) =>
        _state.PutSession(new SessionRow
        {
            Flight = flight,
            Id = name[^8..], SessionId = name[^8..] + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/elsewhere",
            Role = SessionRole.Worker, ContextTokens = turned ? 5_000 : 0, LastResponseAt = turned ? DateTimeOffset.UtcNow : null, ContextWindow = 200_000, State = state,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

    private async Task QueueLeg(string number = "1", string code = "TIA1")
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [number, "Work"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, $"flight/{code}", "name=one", "goal=Do it", "done-when=It is done")).Ok.ShouldBeTrue();
        (await Leg(Verbs.StartLeg, Verbs.LegTarget(code, "one"))).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AnIdleSessionThatTookNoFirstTurnYetStillHoldsTheSlotItWasAdmittedFor()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle, turned: false);

        await QueueLeg();

        _workers.Started.ShouldBeEmpty();
        _legs.Slots.PublishState();
        _state.Admission().ShouldNotBeNull().Counted.ShouldHaveSingleItem().ShouldBe("du1-idle-aaaaaaa1 (idle, no first turn yet)");
    }

    [Fact]
    public async Task AFlyingLegWhoseWorkerIsIdleWithNothingPendingStillHoldsItsRepo()
    {
        Settings(cap: 3, onePerRepo: "on");
        await QueueLeg();
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        Put("du1-tia1-one", SessionState.Idle, flight: "TIA1");
        _state.Legs("TIA1").Single().State.ShouldBe(LegState.Flying);

        await QueueLeg("2", "TIA2");

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        _state.Legs("TIA2").Single().Queued.ShouldNotBeNull();
    }

    [Fact]
    public async Task TwoIdleSessionsWithNothingPendingDoNotHoldSlotsSoAQueuedLegStartsAtACapOfThree()
    {
        Settings(cap: 3);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        Put("du1-idle-aaaaaaa2", SessionState.Idle);
        Put("du1-busy-aaaaaaa3", SessionState.Working);

        await QueueLeg();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
    }

    [Fact]
    public async Task AnIdleSessionOwningARunningJobHoldsNoSlotAndIsPublishedAsWaiting()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        _state.PutJob(new JobRow
        {
            Id = "j12", Command = "dotnet test", Cwd = "/elsewhere", Owner = "aaaaaaa1", State = JobState.Running, LogPath = "/tmp/j12.log",
            Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow,
        });

        await QueueLeg();

        _workers.Started.ShouldHaveSingleItem($"queued: {_state.Legs("TIA1").Single().Queued}").Name.ShouldBe("du1-tia1-one");
        _legs.Slots.PublishState();
        _state.Admission().ShouldNotBeNull().WaitingOnPerson.ShouldHaveSingleItem().ShouldBe("du1-idle-aaaaaaa1 (idle, job j12 running)");
    }

    [Fact]
    public async Task AnIdleSessionWithAJobResultNotYetToldStillHoldsItsSlot()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        _state.PutJob(new JobRow
        {
            Id = "j13", Command = "dotnet test", Cwd = "/elsewhere", Owner = "aaaaaaa1", State = JobState.Succeeded, LogPath = "/tmp/j13.log",
            Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow, Told = false,
        });

        await QueueLeg();

        _workers.Started.ShouldBeEmpty();
        _state.Legs("TIA1").Single().Queued.ShouldNotBeNull().ShouldContain("du1-idle-aaaaaaa1 (idle, job j13 result to tell)");
    }

    [Fact]
    public async Task AnIdleSessionWithAnOpenCardItAskedHoldsNoSlotAndIsPublishedAsWaiting()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        var card = _state.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, "flight/OTHER", "asks", askedBy: "du1-idle-aaaaaaa1");

        await QueueLeg();

        _workers.Started.ShouldHaveSingleItem($"queued: {_state.Legs("TIA1").Single().Queued}").Name.ShouldBe("du1-tia1-one");
        _legs.Slots.PublishState();
        var admission = _state.Admission().ShouldNotBeNull();
        admission.WaitingOnPerson.ShouldHaveSingleItem().ShouldBe($"du1-idle-aaaaaaa1 (idle, card {card.Id} open)");
        admission.Counted.ShouldNotContain(c => c.StartsWith("du1-idle-aaaaaaa1"));
    }

    [Fact]
    public async Task AnIdlePlannerWhoseFlightHasItsPlannedCardOpenHoldsNoSlot()
    {
        Settings(cap: 1);
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["2", "Other"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        Put("du1-tia2-planner-aaaaaaa1", SessionState.Idle, flight: "TIA2");
        _state.PutSession(_state.Sessions().Single(s => s.Name == "du1-tia2-planner-aaaaaaa1") with { Role = SessionRole.Planner });
        _state.RaiseCard(CardBin.ForYou, LegCommander.PlannedKind, Verbs.FlightTarget("TIA2"), "TIA2: 1 leg planned");

        await QueueLeg();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        _legs.Slots.PublishState();
        _state.Admission().ShouldNotBeNull().WaitingOnPerson.ShouldHaveSingleItem().ShouldContain("du1-tia2-planner-aaaaaaa1");
    }

    private void PutJob(string id, JobState state, bool told = false) =>
        _state.PutJob(new JobRow
        {
            Id = id, Command = "dotnet test", Cwd = "/elsewhere", Owner = "aaaaaaa1", State = state, LogPath = $"/tmp/{id}.log",
            Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow, Told = told,
        });

    [Fact]
    public void ANodeWhoseOnlySessionIsAWaitingOneStillPublishesItsAdmissionSoTheNotCountedLineAppears()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        PutJob("j12", JobState.Running);
        _state.Admission().ShouldBeNull();

        _legs.Slots.PublishState();

        // Nothing is counted, no flight has priority and no peer was left out: the waiting list is all the row has to say.
        var admission = _state.Admission().ShouldNotBeNull();
        admission.OpusCount.ShouldBe(0);
        admission.Counted.ShouldBeEmpty();
        admission.WaitingOnPerson.ShouldHaveSingleItem().ShouldBe("du1-idle-aaaaaaa1 (idle, job j12 running)");
    }

    [Fact]
    public void ANodeWithNothingCountedAndNobodyWaitingStillPublishesNoAdmission()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);

        _legs.Slots.PublishState();

        _state.Admission().ShouldBeNull();
    }

    [Fact]
    public void AChangeInWhoWaitsIsPublishedWhileTheCountStaysPut()
    {
        Settings(cap: 3);
        Put("du1-busy-aaaaaaa3", SessionState.Working);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        PutJob("j12", JobState.Running);
        _legs.Slots.PublishState();
        var first = _state.Admission().ShouldNotBeNull();
        first.OpusCount.ShouldBe(1);
        first.WaitingOnPerson.ShouldHaveSingleItem().ShouldBe("du1-idle-aaaaaaa1 (idle, job j12 running)");

        // The job ends and is told, and the session asks a card instead: one still waits, for something else, and the count did not move.
        PutJob("j12", JobState.Succeeded, told: true);
        var card = _state.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, "flight/OTHER", "asks", askedBy: "du1-idle-aaaaaaa1");
        _legs.Slots.PublishState();
        var second = _state.Admission().ShouldNotBeNull();
        second.OpusCount.ShouldBe(1);
        second.Counted.ShouldBe(first.Counted);
        second.WaitingOnPerson.ShouldHaveSingleItem().ShouldBe($"du1-idle-aaaaaaa1 (idle, card {card.Id} open)");

        // The card is answered: nobody waits now, and the line goes with it.
        _state.CloseCard(card.Id, "answered", "tests").ShouldBeTrue();
        _legs.Slots.PublishState();
        var third = _state.Admission().ShouldNotBeNull();
        third.OpusCount.ShouldBe(1);
        third.WaitingOnPerson.ShouldBeEmpty();
    }

    [Fact]
    public void AnIdleSonnetSessionWaitingOnACardIsNotListedAsNotCountedSinceItNeverCounted()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        _state.PutSession(_state.Sessions().Single(s => s.Name == "du1-idle-aaaaaaa1") with { Model = "claude-sonnet-5-5" });
        Put("du1-idle-aaaaaaa2", SessionState.Idle);
        _state.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, "flight/OTHER", "asks", askedBy: "du1-idle-aaaaaaa1");
        // A card of the same kind about the same subject is the one already open, so the second asks about another subject.
        var opusCard = _state.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, "flight/ELSE", "asks too", askedBy: "du1-idle-aaaaaaa2");
        opusCard.AskedBy.ShouldBe("du1-idle-aaaaaaa2");

        _legs.Slots.PublishState();

        _state.Admission().ShouldNotBeNull().WaitingOnPerson.ShouldHaveSingleItem().ShouldBe($"du1-idle-aaaaaaa2 (idle, card {opusCard.Id} open)");
    }
}
