using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Whether a worker, planner, fixer or reviewer may start: the Opus cap, one flight per repo with its deadlock walk, priority.</summary>
public sealed class AdmissionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    private static FleetSession Opus(string label, string node = "DE1") => new(node, label, "opus");

    private static AdmissionInput Cap(int cap, params FleetSession[] sessions) => new(sessions, [], cap, false);

    private static AdmissionInput Repo(params FleetFlight[] flights) => new([], flights, 3, true);

    private static FleetFlight Flight(string code, string repo, FlightPhase phase, string node = "DE1", int age = 0, params FlightWait[] waits) =>
        new(node, code, repo, phase) { Waits = waits, Since = T0.AddMinutes(age) };

    // A held flight whose plane is up, so it holds its repo while it waits.
    private static FleetFlight Holding(string code, string repo, string node = "DE1", params FlightWait[] waits) =>
        new(node, code, repo, FlightPhase.Held) { Holds = true, Waits = waits };

    private static AdmissionRequest Start(string flight, string? model = "opus", string node = "DE1") => new(AdmissionRole.Worker, model, node, flight);

    [Fact]
    public void ACapThatIsFullHoldsAnOpusStartAndNamesTheSessionsInTheAir()
    {
        var input = Cap(3, Opus("PIL418/x"), Opus("TIA1871/y"), Opus("review of #424"));
        Admission.Decide(input, new AdmissionRequest(AdmissionRole.Worker, "opus")).ShouldBe(
            new AdmissionDecision.Wait("waiting for an Opus slot: 3 of 3 flying: PIL418/x, TIA1871/y, review of #424"));
    }

    [Fact]
    public void AFreeSlotAdmitsAndASonnetStartNeverWaitsForOne()
    {
        Admission.Decide(Cap(3, Opus("a"), Opus("b")), new AdmissionRequest(AdmissionRole.Planner, "opus")).ShouldBeOfType<AdmissionDecision.Admit>();
        Admission.Decide(Cap(2, Opus("a"), Opus("b")), new AdmissionRequest(AdmissionRole.Worker, "sonnet")).ShouldBeOfType<AdmissionDecision.Admit>();
    }

    [Fact]
    public void ACapOfZeroIsNoCap()
    {
        var many = Cap(0, [.. Enumerable.Range(0, 20).Select(i => Opus("s" + i))]);
        Admission.Decide(many, new AdmissionRequest(AdmissionRole.Reviewer, "opus")).ShouldBeOfType<AdmissionDecision.Admit>();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("none", true)]
    [InlineData("opus", true)]
    [InlineData("claude-opus-5-5[1m]", true)]
    [InlineData("sonnet", false)]
    [InlineData("haiku", false)]
    public void ARoleLeftToClaudeCodesDefaultCountsAsOpus(string? model, bool opus)
    {
        Admission.IsOpus(model).ShouldBe(opus);
        var input = Cap(1, new FleetSession("DE1", "PIL1/a", model));
        var decision = Admission.Decide(input, new AdmissionRequest(AdmissionRole.Worker, "opus"));
        (decision is AdmissionDecision.Wait).ShouldBe(opus);
    }

    [Fact]
    public void AMetaSessionIsNotCounted()
    {
        var input = Cap(1, new FleetSession("DE1", "DE1-watchtower", "opus", Meta: true), new FleetSession("DE1", "assistant", null, Meta: true));
        Admission.Decide(input, new AdmissionRequest(AdmissionRole.Worker, "opus")).ShouldBeOfType<AdmissionDecision.Admit>();
    }

    [Theory]
    [InlineData(FlightPhase.Flying, true)]
    [InlineData(FlightPhase.InReview, true)]
    [InlineData(FlightPhase.Fixing, true)]
    [InlineData(FlightPhase.CiWait, false)]
    [InlineData(FlightPhase.MergeQueue, false)]
    [InlineData(FlightPhase.Landed, false)]
    public void ARepoIsHeldByALegOrAPrInReviewOrFixingButNotByOneWaitingOnCiOrTheQueue(FlightPhase phase, bool holds)
    {
        var input = Repo(Flight("TIA1", "tianwen", phase), Flight("TIA2", "tianwen", FlightPhase.Ready));
        var decision = Admission.Decide(input, Start("TIA2"));
        if (holds)
        {
            decision.ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldContain("TIA1");
        }
        else
        {
            decision.ShouldBeOfType<AdmissionDecision.Admit>();
        }
    }

    [Fact]
    public void TheModeOffHoldsNothingAndAnotherRepoIsFree()
    {
        var flights = new[] { Flight("TIA1", "tianwen", FlightPhase.Flying), Flight("TIA2", "tianwen", FlightPhase.Ready), Flight("PIL1", "pilot", FlightPhase.Ready) };
        Admission.Decide(new AdmissionInput([], flights, 3, false), Start("TIA2")).ShouldBeOfType<AdmissionDecision.Admit>();
        Admission.Decide(new AdmissionInput([], flights, 3, true), Start("PIL1")).ShouldBeOfType<AdmissionDecision.Admit>();
    }

    [Fact]
    public void AFlightThatHoldsItsRepoGoesOnWithItsNextSession()
    {
        var input = Repo(Flight("TIA1", "tianwen", FlightPhase.InReview), Flight("TIA2", "tianwen", FlightPhase.Ready));
        Admission.Decide(input, new AdmissionRequest(AdmissionRole.Fixer, "opus", "DE1", "TIA1")).ShouldBeOfType<AdmissionDecision.Admit>();
    }

    [Fact]
    public void FlightsOthersDependOnGoFirstAndAHeldFlightsDependencyIsNeverBehindIt()
    {
        // TIA2 is older, but the held TIA3 (same repo) connects from TIA4, so TIA4 goes first.
        var input = Repo(
            Flight("TIA2", "tianwen", FlightPhase.Ready, age: 0),
            Flight("TIA4", "tianwen", FlightPhase.Ready, age: 5),
            Flight("TIA3", "tianwen", FlightPhase.Held, age: 1, waits: new FlightWait("DE1", "TIA4", WaitKind.Connection)));
        Admission.Decide(input, Start("TIA4")).ShouldBeOfType<AdmissionDecision.Admit>();
        Admission.Decide(input, Start("TIA2")).ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldContain("TIA4 goes first");
    }

    [Fact]
    public void WithoutDependentsTheOlderFlightGoesFirst()
    {
        var input = Repo(Flight("TIA2", "tianwen", FlightPhase.Ready, age: 0), Flight("TIA4", "tianwen", FlightPhase.Ready, age: 5));
        Admission.Decide(input, Start("TIA2")).ShouldBeOfType<AdmissionDecision.Admit>();
        Admission.Decide(input, Start("TIA4")).ShouldBeOfType<AdmissionDecision.Wait>();
    }

    [Fact]
    public void ATwoFlightCycleIsBrokenByTheFlightThatWaitsForTheRepo()
    {
        // PIL12 holds the repo and waits for PIL15's package; PIL15 waits for the repo.
        var input = Repo(
            Holding("PIL12", "pilot", waits: new FlightWait("DE1", "PIL15", WaitKind.Package)),
            Flight("PIL15", "pilot", FlightPhase.Ready));
        var decision = Admission.Decide(input, Start("PIL15")).ShouldBeOfType<AdmissionDecision.AdmitToBreakCycle>();
        decision.Card.ShouldStartWith("two PIL flights in the air: PIL12 waits for PIL15's package and PIL15 for the repo");
    }

    [Fact]
    public void AThreeFlightCycleAcrossTwoReposIsBroken()
    {
        // TIA1 wants the repo; TIA2 holds it and waits for DIR3; DIR3 holds dir.lib and waits for TIA1 to land.
        var input = Repo(
            Flight("TIA1", "tianwen", FlightPhase.Ready),
            Holding("TIA2", "tianwen", waits: new FlightWait("DE1", "DIR3", WaitKind.Connection)),
            Holding("DIR3", "DIR.Lib", waits: new FlightWait("DE1", "TIA1", WaitKind.Connection)));
        var card = Admission.Decide(input, Start("TIA1")).ShouldBeOfType<AdmissionDecision.AdmitToBreakCycle>().Card;
        card.ShouldStartWith("three flights in the air");
        card.ShouldContain("TIA1 for the repo");
    }

    [Fact]
    public void ACycleAcrossNodesIsBroken()
    {
        var input = Repo(
            Holding("PIL12", "pilot", waits: new FlightWait("SE1", "PIL15", WaitKind.PeerConnection)),
            Flight("PIL15", "pilot", FlightPhase.Ready, node: "SE1"));
        Admission.Decide(input, Start("PIL15", node: "SE1")).ShouldBeOfType<AdmissionDecision.AdmitToBreakCycle>().Card.ShouldContain("PIL15 on SE1 to land");
    }

    [Fact]
    public void APackageWaitAcrossReposBreaksNothingUntilItClosesACycle()
    {
        // A chain: DIR2 waits for DIR3, which holds dir.lib; TIA1 waits for DIR2's package. No cycle, so DIR2 waits.
        var chain = Repo(
            Holding("TIA1", "tianwen", waits: new FlightWait("DE1", "DIR2", WaitKind.Package)),
            Flight("DIR2", "DIR.Lib", FlightPhase.Ready),
            Flight("DIR3", "DIR.Lib", FlightPhase.Flying));
        Admission.Decide(chain, Start("DIR2")).ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldContain("DIR3");
        // Close it: DIR3 now waits for TIA1.
        var cyclic = Repo(
            Holding("TIA1", "tianwen", waits: new FlightWait("DE1", "DIR2", WaitKind.Package)),
            Holding("DIR3", "DIR.Lib", waits: new FlightWait("DE1", "TIA1", WaitKind.Connection)),
            Flight("DIR2", "DIR.Lib", FlightPhase.Ready));
        Admission.Decide(cyclic, Start("DIR2")).ShouldBeOfType<AdmissionDecision.AdmitToBreakCycle>().Card.ShouldContain("DIR2's package");
    }

    [Fact]
    public void OnlyOneOfTwoWaitingFlightsOnACycleBreaksIt()
    {
        var input = Repo(
            Holding("PIL12", "pilot", waits: new FlightWait("DE1", "PIL15", WaitKind.Package)),
            Flight("PIL15", "pilot", FlightPhase.Ready, age: 0),
            Flight("PIL16", "pilot", FlightPhase.Ready, age: 1));
        Admission.Decide(input, Start("PIL15")).ShouldBeOfType<AdmissionDecision.AdmitToBreakCycle>();
        Admission.Decide(input, Start("PIL16")).ShouldBeOfType<AdmissionDecision.Wait>();
    }

    [Fact]
    public void APriorityFlightIsExemptFromBothRulesYetCountsInTheOpusFigure()
    {
        var holder = Flight("TIA1", "tianwen", FlightPhase.Flying);
        var priority = Flight("TIA2", "tianwen", FlightPhase.Ready) with { Priority = true };
        var full = new AdmissionInput([Opus("a"), Opus("b"), Opus("c")], [holder, priority], 3, true);
        Admission.Decide(full, Start("TIA2")).ShouldBeOfType<AdmissionDecision.Admit>();
        // Its own session is in the figure, so another flight's start sees the slot it takes.
        var counted = new AdmissionInput([Opus("a"), Opus("b"), Opus("TIA2/x")], [Flight("PIL1", "pilot", FlightPhase.Ready)], 3, true);
        Admission.Decide(counted, Start("PIL1")).ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldContain("TIA2/x");
    }

    [Fact]
    public void AFullCapStillHoldsTheFlightThatBreaksACycle()
    {
        var input = new AdmissionInput([Opus("a")], [
            Holding("PIL12", "pilot", waits: new FlightWait("DE1", "PIL15", WaitKind.Package)),
            Flight("PIL15", "pilot", FlightPhase.Ready)], 1, true);
        Admission.Decide(input, Start("PIL15")).ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldStartWith("waiting for an Opus slot");
    }
}
