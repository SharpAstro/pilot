using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight grounded while its leg's finish reruns the check: the check's job waits on the hold (JobRunner.FlightHold), and the rerun
/// holds the flight's calls slot meanwhile. The unground used to be refused by that slot, so the hold and the check waited on each other
/// (pilot #1020). Now the release goes through, and the queued check starts and finishes the leg.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class UngroundWhileRecheckTests : LegCommanderTestsBase
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(25);

    private const string Check = "echo 'checked'; exit 0";

    [Fact(Timeout = 120_000)]
    public async Task AFlightUngroundedWhileItsCheckIsQueuedBehindTheHoldIsReleasedAndTheCheckThenFinishesTheLeg()
    {
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");
        _state.PutLeg(L("parser") with { Check = Check });

        var launcher = new FakeJobLauncher(TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        var jobs = new JobRunner(_state, options, TimeProvider.System)
        {
            Launcher = launcher,
            FreeMemory = () => null,
            Say = (command, _) => Task.FromResult(_state.Complete(command, null)),
        };
        await jobs.StartAsync(CancellationToken.None);
        try
        {
            await jobs.Recovered.WaitAsync(Ceiling, Ct);
            _flights.PumpJobs = jobs.Pump;
            var legs = new LegCommander(_state, _workers, new JobCheckRunner(jobs), _github, _git, new Planes(_git), _flights, options, TimeProvider.System);

            (await legs.RunAsync(new CommandRequest { Verb = Verbs.Ground, Target = "flight/TIA1", Args = ["waiting", "for", "the", "owner"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            _state.Flights().Single().OnHold.ShouldNotBeNull();

            var finishing = legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["done", "parser in"], Who = "tests" }, Ct);
            var (changes, subscription) = StateChanges.Watch(_state);
            using (subscription)
            {
                await changes.UntilAsync(
                    () => _state.Jobs().Any(j => j is { Kind: JobKind.Check, State: JobState.Queued }),
                    Ceiling, Ct, "the check's job queued behind the hold");

                await changes.UntilAsync(
                    () => _state.Jobs().Single(j => j.Kind == JobKind.Check).Step?.Contains("on hold") == true,
                    Ceiling, Ct, "the check's job naming the hold");

                var released = await legs.RunAsync(new CommandRequest { Verb = Verbs.Unground, Target = "flight/TIA1", Who = "tests" }, Ct);
                released.Ok.ShouldBeTrue(released.Error);

                var finished = await finishing.WaitAsync(Ceiling, Ct);
                finished.Ok.ShouldBeTrue(finished.Error);
                await changes.UntilAsync(() => L("parser").State == LegState.Done, Ceiling, Ct, "the leg done");
            }

            var flight = _state.Flights().Single();
            flight.OnHold.ShouldBeNull();
            flight.State.ShouldBe(FlightState.Flying);
            _state.Jobs().Single(j => j.Kind == JobKind.Check).State.ShouldBe(JobState.Succeeded);
            launcher.Starts.ShouldContain(s => s.JobId == _state.Jobs().Single(j => j.Kind == JobKind.Check).Id);
        }
        finally
        {
            await jobs.StopAsync(CancellationToken.None);
        }
    }
}
