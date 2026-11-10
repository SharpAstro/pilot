using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node stopping while a finish reruns a leg's check leaves the leg's recheck open for the next node to word, and does not record
/// the cancellation as the check having thrown ("could not be run (A task was canceled.)", pilot #819). Any other exception still does.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class RecheckStopTests : LegCommanderTestsBase
{
    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose() => _stopping.Dispose();
    }

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(25);

    // Not a bare sleep, which the node refuses: the echo is what makes it a job. The same line in pwsh and sh.
    private const string LongCheck = "sleep 90; echo slept";

    private async Task StartLegAsync()
    {
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");
    }

    // The check's job runs on an in-memory launcher (#933): its process is in this table, not on the machine.
    private readonly FakeJobLauncher _launcher = new(TimeProvider.System);

    // Ends the job the stopped node left, so nothing outlives the test.
    private void Kill(JobRow job) => _launcher.JobOrNull(job.Id)?.Die();

    // A real node's runner and a commander on it; the finish is cancelled by the node stopping, as the command route cancels it.
    // Returns the check job as the stopped node left it.
    private async Task<JobRow> FinishInterruptedByTheNodeStoppingAsync()
    {
        await StartLegAsync();
        _state.PutLeg(L("parser") with { Check = LongCheck });
        using var lifetime = new Lifetime();
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            Launcher = _launcher,
            FreeMemory = () => null,
            Lifetime = lifetime,
            Say = (command, _) => Task.FromResult(_state.Complete(command, null)),
        };
        await jobs.StartAsync(CancellationToken.None);
        await jobs.Recovered.WaitAsync(Ceiling, Ct);
        var legs = new LegCommander(_state, _workers, new JobCheckRunner(jobs), _github, new GitCli(), new Planes(new GitCli()), _flights,
            new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1"), TimeProvider.System);

        var finishing = legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["done", "parser in"], Who = "tests" }, lifetime.ApplicationStopping);
        JobRow? running = null;
        // The check's row comes Running with its pid: waited for as the state changes, not looked at again after a pause.
        var (changes, subscription) = StateChanges.Watch(_state);
        using (subscription)
        {
            await changes.UntilAsync(
                () => (running = _state.Jobs().FirstOrDefault(j => j is { Kind: JobKind.Check, State: JobState.Running, Pid: not null, ProcessStartedAt: not null })) is not null,
                Ceiling, Ct, "the check's job running");
        }
        running.ShouldNotBeNull();
        try
        {
            L("parser").Recheck.ShouldNotBeNull().Outcome.ShouldBeNull();

            lifetime.StopApplication();
            await Should.ThrowAsync<OperationCanceledException>(finishing);
            await jobs.StopAsync(CancellationToken.None);
        }
        catch
        {
            Kill(running);
            throw;
        }
        return _state.Jobs().Single(j => j.Id == running.Id);
    }

    [Fact(Timeout = 120_000)]
    public async Task AFinishWhoseCheckRerunIsWaitingOnAJobWhenTheNodeStopsLeavesTheRecheckOpenAndTheJobRunning()
    {
        var job = await FinishInterruptedByTheNodeStoppingAsync();
        try
        {
            (job.State, job.EndedAt).ShouldBe((JobState.Running, null));
            var leg = L("parser");
            leg.State.ShouldBe(LegState.Flying);
            var recheck = leg.Recheck.ShouldNotBeNull();
            recheck.Outcome.ShouldBeNull();
            recheck.Check.ShouldBe(LongCheck);
        }
        finally
        {
            Kill(job);
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThatStateLoadedIntoAFreshNodeStateMarksTheRerunForTheNodeToResumeWhateverItsJobWasAndNeverGivesTheThrewOutcome(bool queued)
    {
        var job = await FinishInterruptedByTheNodeStoppingAsync();
        try
        {
            if (queued)
            {
                // The same check as the stopped node would have left it had the slot been taken: still waiting its turn.
                _state.PutJob(job with { State = JobState.Queued, Pid = null, ProcessStartedAt = null });
            }

            var next = new NodeState(_journal, TimeProvider.System);

            // The finish was asked by "tests" with its report, so the next node finishes it itself (CarriedFinishTests): no outcome that
            // asks the worker to finish again, and never the cancellation recorded as the check having thrown.
            var recheck = next.Legs("TIA1").Single(l => l.Name == "parser").Recheck.ShouldNotBeNull();
            (recheck.Resume, recheck.Outcome, recheck.By, recheck.Report).ShouldBe((true, null, "tests", "parser in"));
        }
        finally
        {
            Kill(job);
        }
    }

    // The finish of a leg whose check throws past the reply deadline, so a record of it is kept on the leg.
    private async Task<Exception> FinishWhoseCheckThrowsPastTheDeadlineAsync(Exception thrown, bool nodeStopping)
    {
        await StartLegAsync();
        _legs.ReplyDeadline = () => TimeSpan.FromMilliseconds(30);
        _checks.Stopping = nodeStopping;
        _checks.OnRun = async () =>
        {
            await Task.Delay(150, Ct);
            throw thrown;
        };
        return await Should.ThrowAsync<Exception>(Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parser in"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ARunnerThatThrowsAnythingButACancellationPastTheReplyDeadlineStillRecordsTheThrewOutcome()
    {
        await FinishWhoseCheckThrowsPastTheDeadlineAsync(new InvalidOperationException("no shell"), nodeStopping: true);

        L("parser").Recheck.ShouldNotBeNull().Outcome.ShouldBe(LegRecheck.ThrewOutcome(L("parser").Recheck!.Check, "no shell"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ACancellationWhileTheNodeIsNotStoppingStillRecordsTheThrewOutcome()
    {
        await FinishWhoseCheckThrowsPastTheDeadlineAsync(new TaskCanceledException(), nodeStopping: false);

        L("parser").Recheck.ShouldNotBeNull().Outcome.ShouldNotBeNull().ShouldContain("could not be run");
    }

    [Fact(Timeout = 60_000)]
    public async Task ACancellationWhileTheNodeIsStoppingLeavesTheRecheckOpenWithItsSinceAndCheckKept()
    {
        await FinishWhoseCheckThrowsPastTheDeadlineAsync(new TaskCanceledException(), nodeStopping: true);

        var recheck = L("parser").Recheck.ShouldNotBeNull();
        recheck.Outcome.ShouldBeNull();
        recheck.Check.ShouldNotBeNullOrEmpty();
    }
}
