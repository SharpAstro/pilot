using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A leg records its plane's HEAD when its worker starts, on the first start and on each go-around, so the node can tell whether the worker committed anything (PIL804).</summary>
public sealed class StartHeadTests : LegCommanderTestsBase
{
    [Fact]
    public async Task A_started_leg_records_its_planes_head()
    {
        var flight = await FlightWithTwoLegs();
        L("parser").StartHead.ShouldBeNull();

        await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        var worktree = _state.Flights().Single().Worktree;
        var head = Git(worktree, "rev-parse", "HEAD").Trim();
        head.ShouldNotBeEmpty();
        L("parser").State.ShouldBe(LegState.Flying);
        L("parser").StartHead.ShouldBe(head);
        flight.Code.ShouldBe("TIA1");
    }

    [Fact]
    public async Task A_go_around_records_the_head_the_new_worker_starts_from()
    {
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        var worktree = _state.Flights().Single().Worktree;
        var first = L("parser").StartHead.ShouldNotBeNull();

        // The first worker committed; the go-around's worker starts from the plane as that left it.
        File.WriteAllText(Path.Combine(worktree, "parser.cs"), "class Parser {}\n");
        Git(worktree, "add", ".");
        Commit(worktree, "The parser");
        var after = Git(worktree, "rev-parse", "HEAD").Trim();
        after.ShouldNotBe(first);

        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "ticked", "nothing")).Ok.ShouldBeTrue();

        L("parser").GoArounds.ShouldBe(1);
        L("parser").StartHead.ShouldBe(after);
    }
}
