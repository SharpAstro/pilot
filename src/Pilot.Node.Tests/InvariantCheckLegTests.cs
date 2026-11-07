using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node widens a leg's filtered check at plan, so the stored check is the one that runs, and again at finish for a leg planned before.</summary>
public sealed class InvariantCheckLegTests : LegCommanderTestsBase
{
    private const string Widened = "dotnet test Tianwen.slnx --filter '(FullyQualifiedName~Foo)|Category=Invariant'";

    private async Task Create() =>
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APlannedFilteredCheckIsStoredWidenedAndRerunSoOnFinish()
    {
        await Create();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse", "done-when=It parses", "check=dotnet test Tianwen.slnx --filter FullyQualifiedName~Foo")).Ok.ShouldBeTrue();
        L("parser").Check.ShouldBe(Widened);

        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "ok")).Ok.ShouldBeTrue();
        _checks.Runs.Single().Command.ShouldBe(Widened);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALegPlannedBeforeGetsTheInvariantTestsOnTheRerun()
    {
        await Create();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse", "done-when=It parses", "check=dotnet test Tianwen.slnx --filter FullyQualifiedName~Foo")).Ok.ShouldBeTrue();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        _state.PutLeg(L("parser") with { Check = "dotnet test Tianwen.slnx --filter FullyQualifiedName~Foo" });

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "ok")).Ok.ShouldBeTrue();
        _checks.Runs.Single().Command.ShouldBe(Widened);
        L("parser").Check.ShouldBe(Widened);
    }
}
