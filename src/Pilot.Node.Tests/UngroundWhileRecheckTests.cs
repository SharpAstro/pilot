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

    // The fake launcher holds a job at its sleep until the test finishes it, so the check runs (and the leg stays flying) while the test
    // reads the leg, and the runner's clear of the queued note is seen on a leg that still has its recheck.
    private const string Check = "sleep 1; echo 'checked'; exit 0";

    public UngroundWhileRecheckTests()
    {
        // The flight's issue exists and the tracker is the fake, so the ground labels the issue and the unground takes the label off under
        // the flight's call slot: the path pilot #1020's refusal came from ("hold could not be taken off issue ... is rerunning").
        _github.Issues[1] = "Planets";
        _flights.Trackers = new Trackers([new GitHubTracker(_github)]);
    }

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
            _state.Flights().Single().OnHold.ShouldNotBeNull().Labelled.ShouldBe(true);
            _github.IssueLabels[1].Keys.ShouldContain(PilotMarks.Hold);

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

                // The runner's note on the leg (JobRunner.NoteRecheckQueued): the check is queued, by which job and on what.
                var job = _state.Jobs().Single(j => j.Kind == JobKind.Check);
                await changes.UntilAsync(() => L("parser").Recheck?.Queued?.Contains("on hold") == true, Ceiling, Ct, "the leg noting its queued check");
                L("parser").Recheck.ShouldNotBeNull().Queued.ShouldNotBeNull().ShouldContain(job.Id);
                FlightWaiting.LegState(L("parser")).ShouldStartWith("recheck queued");

                // A second finish meanwhile is refused with the queued words, never "rerunning".
                var again = await legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["done", "parser in"], Who = "tests" }, Ct);
                again.Ok.ShouldBeFalse();
                again.Error.ShouldNotBeNull().ShouldContain("queued, not yet running");
                again.Error.ShouldNotContain("rerunning");

                var released = await legs.RunAsync(new CommandRequest { Verb = Verbs.Unground, Target = "flight/TIA1", Who = "tests" }, Ct);
                released.Ok.ShouldBeTrue(released.Error);
                _github.IssueLabels[1].Keys.ShouldNotContain(PilotMarks.Hold);

                // The release lets the job start, and the runner takes the queued note off the leg before it puts the running row
                // (JobRunner.NoteRecheckQueued(next, null)). The job is held at its sleep, so the leg is still flying with its recheck
                // open: the note must be off, or a refusal would say "queued, not yet running" for a check that runs.
                await changes.UntilAsync(
                    () => _state.Jobs().Single(j => j.Kind == JobKind.Check).State == JobState.Running,
                    Ceiling, Ct, "the check's job started");
                var running = L("parser");
                running.State.ShouldBe(LegState.Flying);
                running.Recheck.ShouldNotBeNull().Queued.ShouldBeNull();
                FlightWaiting.LegState(running).ShouldStartWith("rechecking since");

                launcher.Finish(job.Id);
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
