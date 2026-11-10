using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node stopping (an update, a restart) keeps a QUEUED leg check job: same id, same place in the queue, and the next node's
/// finish of that leg waits on that job instead of queueing a fresh one at the back (pilot #737).
/// </summary>
[Collection(JobsCollection.Name)]
[Trait("Category", "Slow")]
public sealed class CheckCarryOverTests : JobTestsBase
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

    private const string Flight = "TIA1";
    private const string LegName = "parser";

    // A first node with an ordinary job running (it holds the only slot) and a check queued behind it, whose caller is cancelled by the
    // shutdown; then the node is stopped. Returns what the next node starts from.
    private async Task<(JobRow Busy, JobRow Check, JobRow After)> FirstNodeStoppedAsync(string checkCommand)
    {
        using var lifetime = new Lifetime();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct, lifetime.ApplicationStopping);
        using var first = await Node.StartAsync(_root, lifetime: lifetime);
        var busyId = first.Run(_root, Sleep(90)).Made!;
        var busy = await first.UntilAsync(busyId, j => j.State == JobState.Running, Ct);
        var run = first.Jobs.RunCheckAsync(Flight, LegName, checkCommand, _root, TimeSpan.FromMinutes(30), caller.Token);
        var check = first.State.Jobs().Single(j => j.Kind == JobKind.Check);
        var afterId = first.Run(_root, Echo("after")).Made!;
        await first.UntilAsync(afterId, j => j.Step is { } step && step.Contains("ahead"), Ct);

        // The shutdown: the host's stopping fires, which cancels the caller, and then the runner stops.
        lifetime.StopApplication();
        await Should.ThrowAsync<OperationCanceledException>(run);
        await first.StopAsync();

        var kept = first.State.Jobs().Single(j => j.Id == check.Id);
        (kept.State, kept.EndedAt).ShouldBe((JobState.Queued, null));
        return (busy, check, first.Job(afterId));
    }

    private static void KillBusy(JobRow busy)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(busy.Pid!.Value);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AQueuedCheckSurvivesTheNodeStoppingWithTheSameIdAndPlaceAheadOfALaterJob()
    {
        var (busy, check, after) = await FirstNodeStoppedAsync(Echo("carried"));
        try
        {
            using var second = await Node.StartAsync(_root);

            var rows = second.State.Jobs();
            var carried = rows.Single(j => j.Id == check.Id);
            (carried.State, carried.QueuedAt, carried.EndedAt).ShouldBe((JobState.Queued, check.QueuedAt, null));
            var later = await second.UntilAsync(after.Id, j => j.Step is { } step && step.Contains("1 ahead"), Ct);
            later.State.ShouldBe(JobState.Queued);
            (await second.UntilAsync(check.Id, j => j.Step is { } step && step.StartsWith("next in line", StringComparison.Ordinal), Ct)).Id.ShouldBe(check.Id);
            await second.StopAsync();
        }
        finally
        {
            KillBusy(busy);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ASecondRunOfTheSameCheckWaitsOnTheCarriedJobAndMakesNoNewRow()
    {
        var (busy, check, _) = await FirstNodeStoppedAsync(Echo("carried"));
        try
        {
            using var second = await Node.StartAsync(_root);

            var run = second.Jobs.RunCheckAsync(Flight, LegName, check.Command, _root, TimeSpan.FromMinutes(30), Ct);
            second.State.Jobs().Count(j => j.Kind == JobKind.Check).ShouldBe(1);

            // The slot frees: the carried check runs, and the finish that attached gets its result.
            KillBusy(busy);
            var result = await run.WaitAsync(TimeSpan.FromSeconds(25), Ct);

            result.ExitCode.ShouldBe(0);
            result.Tail.ShouldContain("carried");
            second.State.Jobs().Where(j => j.Kind == JobKind.Check).ShouldHaveSingleItem().Id.ShouldBe(check.Id);
            await second.StopAsync();
        }
        finally
        {
            KillBusy(busy);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ACheckTheCarriedRunEndedBeforeTheFinishCameBackIsAdoptedNotRunAgain()
    {
        var (busy, check, _) = await FirstNodeStoppedAsync(Echo("done early"));
        try
        {
            using var second = await Node.StartAsync(_root);
            // Gated: the finish comes back exactly between the carried check's ended row and its kept result. It must wait for the
            // result (taking it) rather than see an ended row with nothing kept and queue the check again.
            using var ended = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            second.Jobs.AfterEndedRow = row =>
            {
                if (row.Id != check.Id)
                {
                    return;
                }
                ended.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            KillBusy(busy);
            await Task.Run(() => ended.Wait(TimeSpan.FromSeconds(40)), Ct);
            ended.IsSet.ShouldBeTrue();
            second.State.Jobs().Single(j => j.Id == check.Id).EndedAt.ShouldNotBeNull();
            var attach = Task.Run(() => second.Jobs.RunCheckAsync(Flight, LegName, check.Command, _root, TimeSpan.FromMinutes(30), Ct), Ct);
            await Task.Delay(300, Ct);
            attach.IsCompleted.ShouldBeFalse();
            release.Set();

            var result = await attach.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            result.ExitCode.ShouldBe(0);
            second.State.Jobs().Where(j => j.Kind == JobKind.Check).ShouldHaveSingleItem().Id.ShouldBe(check.Id);
            await second.StopAsync();
        }
        finally
        {
            KillBusy(busy);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ACheckACrashEndedWithNoExitRecordedIsNotTheVerdictAndTheNextFinishRunsItAfresh()
    {
        // What a crash leaves: the check's row still Running, its process gone (the kill-on-close hold ended the tree, wrapper
        // included), and no exit file.
        var command = Echo("fresh");
        _state.PutJob(new JobRow
        {
            Id = "jc1", Command = command, Cwd = _root, Kind = JobKind.Check, Flight = Flight, Leg = LegName, State = JobState.Running,
            LogPath = Path.Combine(_root, "jc1.log"), Pid = int.MaxValue - 7, ProcessStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Timeout = TimeSpan.FromMinutes(30), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5), QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await _jobs.RecoverAsync(Ct);
        var crashed = _state.Jobs().Single(j => j.Id == "jc1");
        (crashed.State, crashed.EndedAt is not null).ShouldBe((JobState.Failed, true));

        var result = await _jobs.RunCheckAsync(Flight, LegName, command, _root, TimeSpan.FromMinutes(30), Ct).WaitAsync(TimeSpan.FromSeconds(120), Ct); // budget: a saturated machine starts git, pwsh and the launch late; only a failing test waits it out (#743)

        (result.ExitCode, result.Tail.Contains("fresh", StringComparison.Ordinal)).ShouldBe((0, true));
        var checks = _state.Jobs().Where(j => j.Kind == JobKind.Check).ToList();
        checks.Count.ShouldBe(2);
        checks.Single(j => j.Id != "jc1").State.ShouldBe(JobState.Succeeded);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACallerGivingUpWhileTheNodeIsNotStoppingStillCancelsItsQueuedCheck()
    {
        using var lifetime = new Lifetime();
        using var node = await Node.StartAsync(_root, lifetime: lifetime);
        var busyId = node.Run(_root, Sleep(90)).Made!;
        var busy = await node.UntilAsync(busyId, j => j.State == JobState.Running, Ct);
        try
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var run = node.Jobs.RunCheckAsync(Flight, LegName, Echo("never"), _root, TimeSpan.FromMinutes(30), caller.Token);
            var check = node.State.Jobs().Single(j => j.Kind == JobKind.Check);

            await caller.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(run);

            (await node.UntilAsync(check.Id, j => j.EndedAt is not null, Ct)).State.ShouldBe(JobState.Cancelled);
            await node.StopAsync();
        }
        finally
        {
            KillBusy(busy);
        }
    }
}
