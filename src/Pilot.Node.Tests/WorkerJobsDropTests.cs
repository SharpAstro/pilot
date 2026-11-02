using System.Collections.Concurrent;
using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A worker's queued jobs leave the queue when its leg goes around or finishes (#327): each ends Cancelled as "dropped: ..." and
/// Told, nobody is woken for it, a running job is left alone, and the next worker's job moves up to the head of the line.
/// </summary>
[Trait("Category", "Slow")]
public sealed class WorkerJobsDropTests : ReviewTestsBase
{
    private readonly ConcurrentQueue<CommandRequest> _said = new();

    private JobRunner StartJobs()
    {
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                return Task.FromResult(_state.Complete(command, null));
            },
        };
        jobs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        jobs.Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct).GetAwaiter().GetResult();
        _legs.DropJobs = jobs.DropQueuedOf;
        return jobs;
    }

    private void SessionFor(string id, string flight, string leg) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = $"du1-{flight.ToLowerInvariant()}-{leg}", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.Worker, Flight = flight, Leg = leg, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

    private string Queue(JobRunner jobs, string target, string command) =>
        jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = target, Args = [Path.GetTempPath(), "0", command], Who = "test" }).Made.ShouldNotBeNull();

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

    /// <summary>The flight's leg started; its worker has a row, and one job runs on the node's behalf while two wait for the worker.</summary>
    private async Task<(FlightRow Flight, string Worker, string Holder, string First, string Second)> Queued(JobRunner jobs, string issue)
    {
        var flight = await NewFlight(issue, "parser");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single().WorkerJob!;
        SessionFor(worker, flight.Code, "parser");
        var holder = Queue(jobs, Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var first = Queue(jobs, Verbs.SessionTarget(worker), "echo one");
        var second = Queue(jobs, Verbs.SessionTarget(worker), "echo two");
        await Until(second, j => j.Step?.Contains("queued, 1 ahead") == true);
        return (flight, worker, holder, first, second);
    }

    private async Task Release(JobRunner jobs, string holder)
    {
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(holder), Who = "test" });
        await Until(holder, j => j.State == JobState.Cancelled);
        // What was left behind the holder runs out on its own (an echo); wait so no job holds anything of the test's when it ends.
        foreach (var left in _state.Jobs().Where(j => j.State is JobState.Queued or JobState.Running))
        {
            await Until(left.Id, j => j.State is not (JobState.Queued or JobState.Running), seconds: 60);
        }
        await jobs.StopAsync(CancellationToken.None);
    }

    private async Task ShouldBeDropped(string id, string why)
    {
        var row = await Until(id, j => j.State == JobState.Cancelled);
        row.Error.ShouldNotBeNull().ShouldStartWith("dropped:");
        row.Error.ShouldContain(why);
        row.Told.ShouldBeTrue();
    }

    [Fact]
    public async Task AGoAroundDropsTheOldWorkersQueuedJobsWithoutWakingAnyoneAndTheNewWorkersJobIsNextInLine()
    {
        var jobs = StartJobs();
        var (flight, worker, holder, first, second) = await Queued(jobs, "71");

        (await Run(Verbs.GoAround, Verbs.LegTarget(flight.Code, "parser"), "controller", "try again")).Error.ShouldBeNull();

        await ShouldBeDropped(first, "its worker went around");
        await ShouldBeDropped(second, "its worker went around");
        _said.ShouldBeEmpty();
        var replacement = _state.Legs(flight.Code).Single().WorkerJob!;
        replacement.ShouldNotBe(worker);
        SessionFor(replacement, flight.Code, "parser");
        var next = Queue(jobs, Verbs.SessionTarget(replacement), "echo three");
        (await Until(next, j => j.Step?.StartsWith("next in line") == true)).State.ShouldBe(JobState.Queued);
        _state.Jobs().Single(j => j.Id == holder).State.ShouldBe(JobState.Running);

        await Release(jobs, holder);
    }

    [Fact]
    public async Task AReviewerThatGaveItsVerdictHasItsQueuedJobsDroppedWithoutWakingAnyone()
    {
        var jobs = StartJobs();
        var flight = await NewFlight("73", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        var reviewer = F(flight.Code).Review.ShouldNotBeNull().ReviewerJob.ShouldNotBeNull();
        SessionFor(reviewer, flight.Code, "review");
        var holder = Queue(jobs, Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var first = Queue(jobs, Verbs.SessionTarget(reviewer), "echo one");
        var second = Queue(jobs, Verbs.SessionTarget(reviewer), "echo two");
        await Until(second, j => j.Step?.Contains("queued, 1 ahead") == true);

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Ok.ShouldBeTrue();

        await ShouldBeDropped(first, "its reviewer was done");
        await ShouldBeDropped(second, "its reviewer was done");
        _said.ShouldBeEmpty();

        await Release(jobs, holder);
    }

    [Fact]
    public async Task AFinishedLegDropsItsWorkersQueuedJobsWithoutWakingAnyone()
    {
        var jobs = StartJobs();
        var (flight, worker, holder, first, second) = await Queued(jobs, "72");
        File.WriteAllText(Path.Combine(flight.Worktree, $"parser-{Guid.NewGuid():N}.txt"), "work\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "parser work");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "did it")).Error.ShouldBeNull();

        await ShouldBeDropped(first, "its worker ended");
        await ShouldBeDropped(second, "its worker ended");
        _said.ShouldBeEmpty();
        _state.Jobs().Single(j => j.Id == holder).State.ShouldBe(JobState.Running);

        await Release(jobs, holder);
    }
}
