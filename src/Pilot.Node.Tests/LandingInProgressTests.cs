using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight whose landing is already under way is not rung for as "merged on GitHub but did not land" (pilot #1055): the second
/// caller is refused with <see cref="AlreadyLandingException"/> and leaves it to the landing in progress. A landing refused for
/// another reason still rings.
/// </summary>
public sealed class LandingInProgressTests : ReviewTestsBase
{
    private sealed class Gate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public async Task PassAsync()
        {
            _reached.TrySetResult();
            await Task.WhenAny(_open.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        public void Open() => _open.TrySetResult();
    }

    private sealed class GatedGit(IGit inner, Gate gate) : IGit
    {
        public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            if (args is ["worktree", "remove", ..])
            {
                await gate.PassAsync();
            }
            return await inner.RunAsync(args, cwd, cancellationToken);
        }
    }

    private async Task Within(Task task, string what)
    {
        (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(120), Ct))).ShouldBe(task, what); // budget: a saturated machine starts git late; only a failing test waits it out
        await task;
    }

    [Fact]
    public async Task AMergeSeenWhileTheFlightIsBeingLandedRaisesNoNotLandedRingAndTheFlightLands_PIL1055()
    {
        var (flight, _) = await Passed("1055");
        var gate = new Gate();
        var flights = new FlightCommander(_state, new Planes(new GatedGit(_git, gate)), TimeProvider.System, _workers, _github);
        var land = flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct);
        await Within(gate.Reached, "the plane's removal was out");
        _github.Pr = _github.Pr! with { State = "MERGED" };

        await new PrChecks(_state, _github, TimeProvider.System, flights, _legs).LookAsync(Ct);

        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.NotLandedKind);

        gate.Open();
        (await land).Ok.ShouldBeTrue();
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.NotLandedKind);
    }

    private async Task<(FlightCommander Flights, Task<CommandResult> Land, Gate Gate, FlightRow Flight)> LandingUnderWay(string issue)
    {
        var (flight, _) = await Passed(issue);
        var gate = new Gate();
        var flights = new FlightCommander(_state, new Planes(new GatedGit(_git, gate)), TimeProvider.System, _workers, _github);
        var land = flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct);
        await Within(gate.Reached, "the plane's removal was out");
        return (flights, land, gate, flight);
    }

    [Fact]
    public async Task AMergeSeenWithTheReadyCardDismissedWhileTheFlightIsBeingLandedRaisesNoNotLandedRing_PIL1055()
    {
        var (flights, land, gate, flight) = await LandingUnderWay("1057");
        // No open PrReady card: the look reaches LandMergedWithoutCardAsync, the first of the two catches.
        foreach (var card in Open().Where(c => c.Kind == LegCommander.PrReady))
        {
            _state.CloseCard(card.Id, "dismissed: known", "gui").ShouldBeTrue();
        }
        _github.Pr = _github.Pr! with { State = "MERGED" };

        await new PrChecks(_state, _github, TimeProvider.System, flights, _legs).LookAsync(Ct);

        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.NotLandedKind);

        gate.Open();
        (await land).Ok.ShouldBeTrue();
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.NotLandedKind);
    }

    [Fact]
    public async Task AMergeCommandWhoseLandingMeetsALandingUnderWayEndsWithNoErrorAndNoDidNotLandText_PIL1055()
    {
        var (flights, land, gate, flight) = await LandingUnderWay("1058");
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        var legs = new LegCommander(_state, _workers, new FakeChecks(), _github, _git, new Planes(_git), flights, options, TimeProvider.System);

        var result = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [_github.Pr!.Head[..8]], Who = "controller" }, Ct);

        result.Error.ShouldBeNull();
        result.Ok.ShouldBeTrue();
        // The merge itself was made: the landing it reached is the one under way (the review of #1060).
        _github.Merged.ShouldHaveSingleItem();

        gate.Open();
        (await land).Ok.ShouldBeTrue();
        F(flight.Code).State.ShouldBe(FlightState.Landed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeWhoseLandingIsRefusedForAnotherReasonStillRingsNotLanded_PIL1055()
    {
        RealGit(); // the refusal is a file the plane holds uncommitted: git's own status says so
        var (flight, _) = await Passed("1056");
        File.WriteAllText(Path.Combine(flight.Worktree, "half.txt"), "not done\n");
        _github.Pr = _github.Pr! with { State = "MERGED" };

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Flying);
        Open().Single(c => c.Kind == PrChecks.NotLandedKind).Bin.ShouldBe(CardBin.Ring);
    }
}
