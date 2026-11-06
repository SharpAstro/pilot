using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Replacing a reviewer does not throw away a job its replacement could use (pilot #777): at the same reviewed head the old
/// reviewer's queued jobs stay queued, and a run of the same command in the same folder by the replacement adopts the
/// predecessor's job instead of queueing another.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ReviewerJobAdoptionTests : ReviewTestsBase
{
    private JobRunner StartJobs()
    {
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            Say = (command, _) => Task.FromResult(_state.Complete(command, null)),
        };
        jobs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        jobs.Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct).GetAwaiter().GetResult();
        _legs.DropJobs = jobs.DropQueuedOf;
        return jobs;
    }

    private SessionRow Session(string id, string name, DateTimeOffset started, SessionState sessionState = SessionState.Idle, string? flight = null, SessionRole role = SessionRole.Reviewer)
    {
        var row = new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = _root, Role = role,
            Flight = flight, ContextTokens = 0, ContextWindow = 200_000, State = sessionState, SpendWeighted = 0, StartedAt = started,
        };
        _state.PutSession(row);
        return row;
    }

    private string Queue(JobRunner jobs, string target, string command, string? cwd = null) =>
        jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = target, Args = [cwd ?? Path.GetTempPath(), "0", command], Who = "test" }).Made.ShouldNotBeNull();

    private static CommandRequest RunAs(SessionRow session, string cwd, string command) =>
        new() { Verb = Verbs.Run, Target = Verbs.SessionTarget(session.Id), Args = [cwd, "0", command], Who = "test" };

    private async Task<JobRow> Until(string id, Func<JobRow, bool> condition, int seconds = 4)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (_state.Jobs().Single(j => j.Id == id) is var job && condition(job))
            {
                return job;
            }
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(seconds), $"job {id} did not reach the state");
            await Task.Delay(20, Ct);
        }
    }

    private async Task Release(JobRunner jobs, string holder)
    {
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(holder), Who = "test" });
        await Until(holder, j => j.State is not (JobState.Queued or JobState.Running), seconds: 60);
        foreach (var left in _state.Jobs().Where(j => j.State is JobState.Queued or JobState.Running))
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(left.Id), Who = "test" });
            await Until(left.Id, j => j.State is not (JobState.Queued or JobState.Running), seconds: 60);
        }
        await jobs.StopAsync(CancellationToken.None);
    }

    /// <summary>A flight whose reviewer owns a job queued behind a holder.</summary>
    private async Task<(JobRunner Jobs, string Holder, string Queued)> ReviewerWithQueuedJob(string issue, string code)
    {
        var jobs = StartJobs();
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        var reviewer = F(flight.Code).Review!.ReviewerJob!;
        Session(reviewer, Reviewer(flight.Code), DateTimeOffset.UtcNow.AddMinutes(-5), flight: flight.Code);
        var holder = Queue(jobs, Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var queued = Queue(jobs, Verbs.SessionTarget(reviewer), "echo build");
        await Until(queued, j => j.State == JobState.Queued && j.Step is not null);
        return (jobs, holder, queued);
    }

    [Fact]
    public async Task ReplacingAReviewerAtTheSameReviewedHeadLeavesItsQueuedJobQueued()
    {
        var (jobs, holder, queued) = await ReviewerWithQueuedJob("771", "TIA1");
        var code = _state.Flights().Single().Code;

        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);

        _state.Jobs().Single(j => j.Id == queued).State.ShouldBe(JobState.Queued);
        await Release(jobs, holder);
    }

    [Fact]
    public async Task ReplacingAReviewerAtAnotherHeadDropsItsQueuedJobAsBefore()
    {
        var (jobs, holder, queued) = await ReviewerWithQueuedJob("772", "TIA2");
        var flight = _state.Flights().Single();
        File.WriteAllText(Path.Combine(flight.Worktree, $"more-{Guid.NewGuid():N}.txt"), "more\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "more work");

        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        var row = await Until(queued, j => j.State == JobState.Cancelled);
        row.Error.ShouldNotBeNull().ShouldContain("its reviewer was replaced");
        await Release(jobs, holder);
    }

    private async Task<(JobRunner Jobs, string Holder, string Queued, SessionRow? Replacement)> Predecessor(string command, string cwd, bool replaced = true)
    {
        var jobs = StartJobs();
        var before = Session("old00001", "du1-x-review", DateTimeOffset.UtcNow.AddMinutes(-10), SessionState.Ended);
        var holder = Queue(jobs, Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var queued = Queue(jobs, Verbs.SessionTarget(before.Id), command, cwd);
        await Until(queued, j => j.State == JobState.Queued && j.Step is not null);
        var after = replaced ? Session("new00001", "du1-x-review", DateTimeOffset.UtcNow) : null;
        return (jobs, holder, queued, after);
    }

    [Fact]
    public async Task ARunFromTheReplacementAdoptsThePredecessorsQueuedJobAndQueuesNoNewRow()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, queued, after) = await Predecessor("echo build", cwd);
        var replacement = after.ShouldNotBeNull();
        var rows = _state.Jobs().Length;

        var result = jobs.Run(RunAs(replacement, cwd, "echo build"));

        result.Made.ShouldBe(queued);
        _state.Jobs().Length.ShouldBe(rows);
        // The dispatcher's own copy takes the new owner, so the passes that rewrite the waiting row keep it.
        await Until(queued, j => j.Owner == replacement.Id);
        jobs.Pump();
        jobs.Pump();
        await Task.Delay(200, Ct);
        _state.Jobs().Single(j => j.Id == queued).ShouldSatisfyAllConditions(
            j => j.State.ShouldBe(JobState.Queued), j => j.Step.ShouldNotBeNull(), j => j.Owner.ShouldBe(replacement.Id), j => j.OwnerName.ShouldBe(replacement.Name));

        // The start puts the row from that copy too: the ended row, built from the running one, still names the replacement.
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(holder), Who = "test" });
        var ended = await Until(queued, j => j.State is not (JobState.Queued or JobState.Running), seconds: 30);
        ended.ShouldSatisfyAllConditions(j => j.State.ShouldBe(JobState.Succeeded), j => j.Owner.ShouldBe(replacement.Id), j => j.OwnerName.ShouldBe(replacement.Name));
        await Release(jobs, holder);
    }

    [Fact]
    public async Task AnAdoptionThatReachesTheDispatcherBeforeItsHandOverIsAppliedWhenTheJobArrives()
    {
        // A runner that is not started has no dispatcher loop, so the test drives the signals in that order itself (as JobTests does for a cancel).
        var idle = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n2.sock"), Path.Combine(_root, "data2")), TimeProvider.System)
        {
            FreeMemory = () => null,
            Say = (command, _) => Task.FromResult(_state.Complete(command, null)),
        };
        var late = new JobRow
        {
            Id = "late1", Command = "echo late", Cwd = _root, Owner = "old", OwnerName = "old-name", State = JobState.Queued, Kind = JobKind.Run,
            LogPath = Path.Combine(_root, "late1.log"), Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow,
        };
        _state.PutJob(late);

        idle.Dispatch(new JobRunner.Adopted("late1", "new", "new-name", null, null));
        idle.Dispatch(new JobRunner.HandedOver(late));

        var ended = await Until("late1", j => j.State is not (JobState.Queued or JobState.Running), seconds: 30);
        ended.ShouldSatisfyAllConditions(j => j.Owner.ShouldBe("new"), j => j.OwnerName.ShouldBe("new-name"));
    }

    [Fact]
    public async Task ARunFromTheReplacementAdoptsThePredecessorsRunningJob()
    {
        var jobs = StartJobs();
        var before = Session("old00002", "du1-y-review", DateTimeOffset.UtcNow.AddMinutes(-10), SessionState.Ended);
        var running = Queue(jobs, Verbs.SessionTarget(before.Id), "sleep 60; echo slept");
        await Until(running, j => j.State == JobState.Running);
        var after = Session("new00002", "du1-y-review", DateTimeOffset.UtcNow);

        var result = jobs.Run(RunAs(after, Path.GetTempPath(), "sleep 60; echo slept"));

        result.Made.ShouldBe(running);
        _state.Jobs().Single(j => j.Id == running).Owner.ShouldBe(after.Id);
        jobs.Pump();
        await Task.Delay(200, Ct);
        _state.Jobs().Single(j => j.Id == running).ShouldSatisfyAllConditions(j => j.State.ShouldBe(JobState.Running), j => j.Owner.ShouldBe(after.Id));
        await Release(jobs, running);
    }

    [Fact]
    public async Task AReplacementWorkerDoesNotAdoptARunningJobStartedBeforeItsEdits()
    {
        var jobs = StartJobs();
        var before = Session("old00003", "du1-w-leg", DateTimeOffset.UtcNow.AddMinutes(-10), SessionState.Ended, role: SessionRole.Worker);
        var running = Queue(jobs, Verbs.SessionTarget(before.Id), "sleep 60; echo slept");
        await Until(running, j => j.State == JobState.Running);
        var after = Session("new00003", "du1-w-leg", DateTimeOffset.UtcNow, role: SessionRole.Worker);

        var result = jobs.Run(RunAs(after, Path.GetTempPath(), "sleep 60; echo slept"));

        result.Made.ShouldNotBe(running);
        _state.Jobs().Single(j => j.Id == running).Owner.ShouldBe(before.Id);
        await Release(jobs, running);
    }

    [Fact]
    public async Task ADifferentCommandOrFolderQueuesANewJob()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, queued, after) = await Predecessor("echo build", cwd);
        var replacement = after.ShouldNotBeNull();
        var other = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        var command = jobs.Run(RunAs(replacement, cwd, "echo tests"));
        var folder = jobs.Run(RunAs(replacement, other, "echo build"));

        command.Made.ShouldNotBe(queued);
        folder.Made.ShouldNotBe(queued);
        _state.Jobs().Single(j => j.Id == queued).Owner.ShouldBe("old00001");
        await Release(jobs, holder);
    }

    [Fact]
    public async Task ASessionOfAnotherNameAdoptsNothing()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, queued, _) = await Predecessor("echo build", cwd);
        var stranger = Session("odd00001", "du1-z-review", DateTimeOffset.UtcNow);

        jobs.Run(RunAs(stranger, cwd, "echo build")).Made.ShouldNotBe(queued);

        _state.Jobs().Single(j => j.Id == queued).Owner.ShouldBe("old00001");
        await Release(jobs, holder);
    }

    [Fact]
    public async Task ANamesakeOfAnotherRoleAdoptsNothing()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, queued, _) = await Predecessor("echo build", cwd, replaced: false);
        // Same name and newer, but a worker: SessionMessenger.ReplacedBy wants the same role too.
        var namesake = Session("odd00002", "du1-x-review", DateTimeOffset.UtcNow, role: SessionRole.Worker);

        jobs.Run(RunAs(namesake, cwd, "echo build")).Made.ShouldNotBe(queued);

        _state.Jobs().Single(j => j.Id == queued).Owner.ShouldBe("old00001");
        await Release(jobs, holder);
    }

    [Fact]
    public async Task APredecessorWithNoReplacementKeepsItsJob()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, queued, _) = await Predecessor("echo build", cwd, replaced: false);
        // Same name and role, but started before the predecessor: no replacement of it.
        var earlier = Session("odd00003", "du1-x-review", DateTimeOffset.UtcNow.AddMinutes(-20), SessionState.Ended);

        jobs.Run(RunAs(earlier, cwd, "echo build")).Made.ShouldNotBe(queued);

        _state.Jobs().Single(j => j.Id == queued).Owner.ShouldBe("old00001");
        await Release(jobs, holder);
    }
}
