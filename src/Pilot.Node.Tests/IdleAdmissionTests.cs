using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>An Idle session holds an Opus slot only while it has work pending (#569).</summary>
public sealed class IdleAdmissionTests : LegCommanderTestsBase
{
    private void Settings(int cap)
    {
        var settings = NodeSettings.With(new NodeSettings(), "opus-cap", [cap.ToString()]).Settings!;
        settings = NodeSettings.With(settings, "worker-model", ["opus"]).Settings!;
        settings = NodeSettings.With(settings, "one-flight-per-repo", ["off"]).Settings!;
        _legs.Models = new RoleModels(settings);
    }

    private void Put(string name, SessionState state) =>
        _state.PutSession(new SessionRow
        {
            Id = name[^8..], SessionId = name[^8..] + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/elsewhere",
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

    private async Task QueueLeg()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Work"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=one", "goal=Do it", "done-when=It is done")).Ok.ShouldBeTrue();
        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA1", "one"))).Ok.ShouldBeTrue();
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
    public async Task AnIdleSessionOwningARunningJobHoldsItsSlotAndTheWhyNamesItAsIdle()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        _state.PutJob(new JobRow
        {
            Id = "j12", Command = "dotnet test", Cwd = "/elsewhere", Owner = "aaaaaaa1", State = JobState.Running, LogPath = "/tmp/j12.log",
            Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow,
        });

        await QueueLeg();

        _workers.Started.ShouldBeEmpty();
        _state.Legs("TIA1").Single().Queued.ShouldNotBeNull().ShouldContain("du1-idle-aaaaaaa1 (idle, job j12 running)");
    }

    [Fact]
    public async Task AnIdleSessionWithAnOpenCardItAskedHoldsItsSlotAndPublishedCountedSaysIdle()
    {
        Settings(cap: 1);
        Put("du1-idle-aaaaaaa1", SessionState.Idle);
        var card = _state.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, "flight/OTHER", "asks", askedBy: "du1-idle-aaaaaaa1");

        await QueueLeg();

        _workers.Started.ShouldBeEmpty();
        _legs.Slots.PublishState();
        _state.Admission().ShouldNotBeNull().Counted.ShouldHaveSingleItem().ShouldBe($"du1-idle-aaaaaaa1 (idle, card {card.Id} open)");
    }
}
