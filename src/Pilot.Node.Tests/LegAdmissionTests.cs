using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Admission wired into the starts the node makes: the Opus cap, the one-flight-per-repo mode and the priority label, each showing
/// as a queued reason on the row (never a ring or a stall) and starting by itself on the sweep once a slot frees.
/// </summary>
public sealed class LegAdmissionTests : LegCommanderTestsBase
{
    private void Settings(int cap, bool oneFlightPerRepo = false)
    {
        var settings = NodeSettings.With(new NodeSettings(), "opus-cap", [cap.ToString()]).Settings!;
        settings = NodeSettings.With(settings, "worker-model", ["opus"]).Settings!;
        settings = NodeSettings.With(settings, "one-flight-per-repo", [oneFlightPerRepo ? "on" : "off"]).Settings!;
        _legs.Models = new RoleModels(settings);
    }

    private void OtherOpus(string name, SessionState state = SessionState.Working, string? model = null) =>
        _state.PutSession(new SessionRow
        {
            Id = name[^8..], SessionId = name[^8..] + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/elsewhere",
            Role = SessionRole.Worker, Model = model, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

    private async Task<FlightRow> Flight(int issue, string leg)
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue.ToString(), "Work"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var code = $"TIA{issue}";
        (await Leg(Verbs.PlanLeg, $"flight/{code}", $"name={leg}", "goal=Do it", "done-when=It is done")).Ok.ShouldBeTrue();
        return _state.Flights().Single(f => f.Code == code);
    }

    private LegRow Leg1(string code, string name) => _state.Legs(code).Single(l => l.Name == name);

    private void NoRings() => _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring).ShouldBeEmpty();

    [Fact]
    public async Task AtTheOpusCapALegStartWaitsWithItsWhyOnTheLegAndStartsByItselfOnTheSweepOnceTheSlotFrees()
    {
        Settings(cap: 1);
        await Flight(1, "one");
        OtherOpus("du1-other-worker");

        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA1", "one"))).Ok.ShouldBeTrue();

        _workers.Started.ShouldBeEmpty();
        var queued = Leg1("TIA1", "one");
        queued.State.ShouldBe(LegState.Planned);
        queued.Queued.ShouldNotBeNull().ShouldStartWith("waiting for an Opus slot: 1 of 1 flying: du1-other-worker");
        FlightWaiting.LegState(queued).ShouldStartWith("queued: waiting for an Opus slot");
        NoRings();

        // Still full: the sweep asks again and changes nothing.
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.ShouldBeEmpty();
        NoRings();

        // The other session ends: the sweep starts the leg, with no one asking again.
        OtherOpus("du1-other-worker", SessionState.Ended);
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        var flying = Leg1("TIA1", "one");
        flying.State.ShouldBe(LegState.Flying);
        flying.Queued.ShouldBeNull();
    }

    [Fact]
    public async Task ASessionCountsAsOpusByTheModelItIsStartedWithSoALegOnSonnetStartsAtTheCap()
    {
        Settings(cap: 1);
        await Flight(1, "one");
        OtherOpus("du1-other-worker");
        _state.PutLeg(Leg1("TIA1", "one") with { Model = "sonnet" });

        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA1", "one"))).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem().Model.ShouldBe("sonnet");
        Leg1("TIA1", "one").Queued.ShouldBeNull();
    }

    [Fact]
    public async Task APlannerAtTheOpusCapIsQueuedOnItsFlightAndStartsOnTheSweepAndIsNotAStall()
    {
        Settings(cap: 1);
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Work"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        OtherOpus("du1-other-worker");

        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();

        _workers.Started.ShouldBeEmpty();
        _state.Flights().Single().Queued.ShouldNotBeNull().ShouldStartWith("waiting for an Opus slot");
        NoRings();

        OtherOpus("du1-other-worker", SessionState.Ended);
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-plan");
        _state.Flights().Single().Queued.ShouldBeNull();
    }

    [Fact]
    public async Task WithOneFlightPerRepoASecondFlightOfTheSameRepoWaitsUntilTheFirstsLegEndsAndOnlyOneStartsAtATime()
    {
        Settings(cap: 0, oneFlightPerRepo: true);
        await Flight(1, "one");
        await Flight(2, "two");

        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA1", "one"))).Ok.ShouldBeTrue();
        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA2", "two"))).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        Leg1("TIA2", "two").Queued.ShouldNotBeNull().ShouldContain("waiting for the repo tianwen: TIA1 holds it");
        NoRings();

        // The first leg ends (its worker is gone and the leg done): the sweep starts the second flight's leg.
        _state.PutLeg(Leg1("TIA1", "one") with { State = LegState.Done, FinishedAt = DateTimeOffset.UtcNow });
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia2-two");
        Leg1("TIA2", "two").Queued.ShouldBeNull();
    }

    [Fact]
    public async Task APriorityFlightStartsAtTheCapAndTheRepoHoldAndTakingTheLabelOffPutsItBackUnderTheRules()
    {
        Settings(cap: 1, oneFlightPerRepo: true);
        await Flight(1, "one");
        OtherOpus("du1-other-worker");
        _state.UpdateFlight("TIA1", f => f with { GitHubLabel = PilotMarks.Priority });

        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA1", "one"))).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-one");
        Leg1("TIA1", "one").Queued.ShouldBeNull();

        // Another flight with no label waits, for the cap and for the repo the priority flight holds.
        await Flight(2, "two");
        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA2", "two"))).Ok.ShouldBeTrue();
        _workers.Started.Count.ShouldBe(1);
        Leg1("TIA2", "two").Queued.ShouldNotBeNull();

        // Marked priority on its issue (read at the start): it goes at once.
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), TimeProvider.System, _workers, _github);
        var legs = new LegCommander(_state, _workers, _checks, _github, git, new Planes(git), flights,
            new NodeOptions(Path.Combine(_root, "node2.sock"), Path.Combine(_root, "data2"), MachinePrefix: "DU1"), TimeProvider.System) { Models = _legs.Models };
        _github.IssueLabels[2] = [PilotMarks.Priority];
        (await legs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget("TIA2", "two"), Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia2-two");
        Leg1("TIA2", "two").Queued.ShouldBeNull();
    }

    [Fact]
    public async Task AFlightAdmittedToBreakACycleRaisesOneCardNamingBothFlights()
    {
        Settings(cap: 0, oneFlightPerRepo: true);
        await Flight(1, "one");
        await Flight(2, "two");
        // TIA1 flies and waits for TIA2 to land; TIA2 waits for the repo TIA1 holds: neither can go until TIA2 gives way.
        _state.PutSession(new SessionRow
        {
            Id = "aaaa0001", SessionId = "aaaa0001-0000-4000-8000-000000000000", Name = "du1-tia1-one", Kind = SessionKind.Background, Cwd = "/r", Role = SessionRole.Worker,
            Flight = "TIA1", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
        _state.UpdateFlight("TIA1", f => f with { Connections = ["TIA2"] });

        (await Leg(Verbs.StartLeg, Verbs.LegTarget("TIA2", "two"))).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia2-two");
        var card = _state.Cards().Where(c => c.Kind == AdmissionGate.CycleKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldContain("TIA1 waits for TIA2 to land");
        card.Title.ShouldContain("TIA2 for the repo");
        card.Title.ShouldContain("TIA2 starts to break the wait");
    }
}
