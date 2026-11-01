using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A sweep that finds the flight's PR open already under way waits for it instead of asking again (PIL606): two opens at once make one
/// PR and no card, and only a failed open rings, once.
/// </summary>
[Trait("Category", "Slow")]
public sealed class OpenPrJoinTests : ReviewTestsBase
{
    private async Task Reaches(TaskCompletionSource reached) =>
        (await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(20), Ct))).ShouldBe(reached.Task, "the call was not reached");

    // CreateAsync held until the returned source is set, then failing when asked to or passing.
    private (TaskCompletionSource Open, TaskCompletionSource Reached) HoldCreate(bool fail)
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.OnCall = async call =>
        {
            if (call != nameof(IGitHub.CreateAsync))
            {
                return;
            }
            reached.TrySetResult();
            await Task.WhenAny(open.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (fail)
            {
                throw new FlightException("GitHub refused the PR");
            }
        };
        return (open, reached);
    }

    private int Rings() => _state.Cards().Count(c => c.Kind == LegCommander.NextStepKind);

    [Fact]
    public async Task TwoSweepsWhileThePrOpenIsHeldMakeOnePrAndNoCard()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        _legs.Models = new RoleModels(null);
        var (open, reached) = HoldCreate(fail: false);

        var first = _legs.AdvanceAllAsync(Ct);
        await Reaches(reached);
        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);
        open.SetResult();
        await first;

        _github.Created.ShouldHaveSingleItem();
        _state.Flights().Single(f => f.Code == flight.Code).PullRequest.ShouldNotBeNull();
        Rings().ShouldBe(0);
    }

    [Fact]
    public async Task AFailedOpenRingsOnceAcrossSweepsAlsoOneRunWhileItIsHeld()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        _legs.Models = new RoleModels(null);
        var (open, reached) = HoldCreate(fail: true);

        var first = _legs.AdvanceAllAsync(Ct);
        await Reaches(reached);
        await _legs.AdvanceAllAsync(Ct);
        Rings().ShouldBe(0);
        open.SetResult();
        await first;

        // The first miss rings nothing; the sweeps after it ask again and the second miss rings, once.
        for (var i = 0; i < 4; i++)
        {
            await _legs.AdvanceAllAsync(Ct);
        }

        _state.Flights().Single(f => f.Code == flight.Code).PullRequest.ShouldBeNull();
        _state.Cards(openOnly: true).Count(c => c.Kind == LegCommander.NextStepKind).ShouldBe(1);
        Rings().ShouldBe(1);
    }
}
