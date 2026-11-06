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

    private SessionRow Session(string id, string name, DateTimeOffset started, SessionState sessionState = SessionState.Idle, string? flight = null)
    {
        var row = new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = _root, Role = SessionRole.Reviewer,
            Flight = flight, ContextTokens = 0, ContextWindow = 200_000, State = sessionState, SpendWeighted = 0, StartedAt = started,
        };
        _state.PutSession(row);
        return row;
    }

    private string Queue(JobRunner jobs, string target, string command, string? cwd = null) =>
        jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = target, Args = [cwd ?? Path.GetTempPath(), "0", command], Who = "test" }).Made.ShouldNotBeNull();

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
        await Until(holder, j => j.State == JobState.Cancelled);
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

    private async Task<(JobRunner Jobs, string Holder, string Old, string Queued, SessionRow Old1, SessionRow New)> Predecessor(string command, string cwd)
    {
        var jobs = StartJobs();
        var before = Session("old00001", "du1-x-review", DateTimeOffset.UtcNow.AddMinutes(-10), SessionState.Ended);
        var holder = Queue(jobs, Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var queued = Queue(jobs, Verbs.SessionTarget(before.Id), command, cwd);
        await Until(queued, j => j.State == JobState.Queued && j.Step is not null);
        var after = Session("new00001", "du1-x-review", DateTimeOffset.UtcNow);
        return (jobs, holder, before.Id, queued, before, after);
    }

    [Fact]
    public async Task ARunFromTheReplacementAdoptsThePredecessorsQueuedJobAndQueuesNoNewRow()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, _, queued, _, replacement) = await Predecessor("echo build", cwd);
        var rows = _state.Jobs().Length;

        var result = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.SessionTarget(replacement.Id), Args = [cwd, "0", "echo build"], Who = "test" });

        result.Made.ShouldBe(queued);
        _state.Jobs().Length.ShouldBe(rows);
        _state.Jobs().Single(j => j.Id == queued).ShouldSatisfyAllConditions(j => j.Owner.ShouldBe(replacement.Id), j => j.OwnerName.ShouldBe(replacement.Name));
        await Release(jobs, holder);
    }

    [Fact]
    public async Task ARunFromTheReplacementAdoptsThePredecessorsRunningJob()
    {
        var jobs = StartJobs();
        var before = Session("old00002", "du1-y-review", DateTimeOffset.UtcNow.AddMinutes(-10), SessionState.Ended);
        var running = Queue(jobs, Verbs.SessionTarget(before.Id), "sleep 60; echo slept");
        await Until(running, j => j.State == JobState.Running);
        var after = Session("new00002", "du1-y-review", DateTimeOffset.UtcNow);

        var result = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.SessionTarget(after.Id), Args = [Path.GetTempPath(), "0", "sleep 60; echo slept"], Who = "test" });

        result.Made.ShouldBe(running);
        _state.Jobs().Single(j => j.Id == running).Owner.ShouldBe(after.Id);
        await Release(jobs, running);
    }

    [Fact]
    public async Task ADifferentCommandOrFolderQueuesANewJob()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, _, queued, _, replacement) = await Predecessor("echo build", cwd);
        var other = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        var command = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.SessionTarget(replacement.Id), Args = [cwd, "0", "echo tests"], Who = "test" });
        var folder = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.SessionTarget(replacement.Id), Args = [other, "0", "echo build"], Who = "test" });

        command.Made.ShouldNotBe(queued);
        folder.Made.ShouldNotBe(queued);
        _state.Jobs().Single(j => j.Id == queued).Owner.ShouldBe("old00001");
        await Release(jobs, holder);
    }

    [Fact]
    public async Task APredecessorThatWasNotReplacedKeepsItsJobAndANamesakeOfAnotherRoleAdoptsNothing()
    {
        var cwd = Path.GetTempPath();
        var (jobs, holder, _, queued, _, replacement) = await Predecessor("echo build", cwd);
        // Another session under another name is no replacement of the old reviewer.
        var stranger = Session("odd00001", "du1-z-review", DateTimeOffset.UtcNow);

        var result = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.SessionTarget(stranger.Id), Args = [cwd, "0", "echo build"], Who = "test" });

        result.Made.ShouldNotBe(queued);
        _state.Jobs().Single(j => j.Id == queued).Owner.ShouldBe("old00001");
        replacement.Id.ShouldNotBe(stranger.Id);
        await Release(jobs, holder);
    }
}
