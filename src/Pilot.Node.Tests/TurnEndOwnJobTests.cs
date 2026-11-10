using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A --bg session Claude Code ended after its turn finished, while it owns a job still queued or running, is not ended in the node's
/// eyes: its jobs stay, and the job's end resumes it with the outcome (pilot #893: PIL846's reviewer ended its turn to wait for its
/// own build job, had the job dropped and was rung as stalled three times). A crash still drops its jobs (#327).
/// </summary>
[Trait("Category", "Slow")]
public sealed class TurnEndOwnJobTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly string _root = Directory.CreateTempSubdirectory("turn-end-jobs-").FullName;
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly ConcurrentQueue<CommandRequest> _said = new();
    private readonly SessionTracker _tracker;
    private readonly JobRunner _jobs;

    public TurnEndOwnJobTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance)
        {
            // Claude Code's record of a job whose turn is over, which is how a finished turn is told from a crash.
            JobRecord = _ => new ClaudeJobRecord("done", null),
        };
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                return Task.FromResult(_state.Complete(command, null));
            },
        };
        _tracker.DropJobs = (owners, why) => _jobs.DropQueuedOf(owners, why);
        _jobs.EndedForGood = _tracker.IsEndedForGood;
        _jobs.EndedAfterTurn = _tracker.EndedAfterFinishedTurn;
        _jobs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _jobs.Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _profile.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Live(string sessionId, string name, int pid) => AgentRows.Background(sessionId, name, "working", pid: pid, status: "busy");

    private async Task Poll(params string[] rows)
    {
        foreach (var id in new[] { AgentRows.SessionA, AgentRows.SessionB })
        {
            if (!File.Exists(_profile.TranscriptPath(id)))
            {
                _profile.Append(_profile.TranscriptPath(id), Lines.Assistant("m1", input: 1));
            }
        }
        _agents.Json = AgentRows.List(rows);
        await _tracker.PollAsync(Ct);
    }

    private string Queue(string target, string command) =>
        _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = target, Args = [Path.GetTempPath(), "0", command], Who = "test" }).Made.ShouldNotBeNull();

    private async Task<JobRow> Until(string id, Func<JobRow, bool> condition, int seconds = 10)
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

    private async Task Release(string holder)
    {
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(holder), Who = "test" });
        await Until(holder, j => j.State == JobState.Cancelled);
    }

    /// <summary>Two polls, since one listing that omits a session is a blip and an end counts once the next poll agrees.</summary>
    private async Task EndedTwice(string ended, params string[] others)
    {
        await Poll([ended, .. others]);
        await Poll([ended, .. others]);
    }

    [Fact]
    public async Task AFinishedTurnKeepsItsQueuedJobsAndTheirEndResumesIt()
    {
        var worker = AgentRows.SessionA[..8];
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242));
        var holder = Queue(Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var first = Queue(Verbs.SessionTarget(worker), "echo one");
        var second = Queue(Verbs.SessionTarget(worker), "echo two");
        await Until(second, j => j.Step?.Contains("queued, 1 ahead") == true);

        await EndedTwice(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"));
        _state.Sessions().Single(s => s.Id == worker).State.ShouldBe(SessionState.Ended);
        _tracker.EndedAfterFinishedTurn(worker).ShouldBeTrue();

        // Neither is dropped ("its worker ended"), and the session is not ended for good while it owns them.
        await Task.Delay(200, Ct);
        _state.Jobs().Single(j => j.Id == first).State.ShouldBe(JobState.Queued);
        _state.Jobs().Single(j => j.Id == second).State.ShouldBe(JobState.Queued);
        _tracker.IsEndedForGood(worker).ShouldBeFalse();
        _said.ShouldBeEmpty();

        await Release(holder);
        var told = await Until(first, j => j.Told);
        told.State.ShouldBe(JobState.Succeeded);
        await Until(second, j => j.Told);
        var said = _said.ToArray();
        said.Length.ShouldBe(2);
        said.ShouldAllBe(c => c.Target == Verbs.SessionTarget(worker));
        said.ShouldContain(c => c.Args[0].Contains("echo one") && c.Args[0].Contains("succeeded"));
        said.ShouldContain(c => c.Args[0].Contains("echo two") && c.Args[0].Contains("succeeded"));
    }

    [Fact]
    public async Task AFinishedTurnKeepsItsRunningJobAndItsEndResumesIt()
    {
        var worker = AgentRows.SessionA[..8];
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242));
        var running = Queue(Verbs.SessionTarget(worker), "sleep 2; echo built");
        await Until(running, j => j.State == JobState.Running);

        await EndedTwice(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"));
        _state.Sessions().Single(s => s.Id == worker).State.ShouldBe(SessionState.Ended);
        _tracker.IsEndedForGood(worker).ShouldBeFalse();
        _state.Jobs().Single(j => j.Id == running).State.ShouldBe(JobState.Running);
        _said.ShouldBeEmpty();

        var ended = await Until(running, j => j.Told);
        ended.State.ShouldBe(JobState.Succeeded);
        var said = _said.Single();
        said.Target.ShouldBe(Verbs.SessionTarget(worker));
        said.Args[0].ShouldContain("Your job " + running);
        said.Args[0].ShouldContain("succeeded");
    }

    [Fact]
    public async Task ACrashStillDropsItsQueuedJobsAndWakesNobody()
    {
        var worker = AgentRows.SessionA[..8];
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242));
        var holder = Queue(Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var queued = Queue(Verbs.SessionTarget(worker), "echo one");
        await Until(queued, j => j.Step?.StartsWith("next in line") == true);

        await EndedTwice(AgentRows.Background(AgentRows.SessionA, "worker-a", "failed"));
        _state.Sessions().Single(s => s.Id == worker).State.ShouldBe(SessionState.Failed);
        _tracker.EndedAfterFinishedTurn(worker).ShouldBeFalse();

        var row = await Until(queued, j => j.State == JobState.Cancelled);
        row.Error.ShouldNotBeNull().ShouldContain("its worker ended");
        _tracker.IsEndedForGood(worker).ShouldBeTrue();
        _said.ShouldBeEmpty();

        await Release(holder);
    }

    [Fact]
    public async Task AFinishedTurnWithNoJobOpenIsEndedForGood()
    {
        var worker = AgentRows.SessionA[..8];
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242));

        await EndedTwice(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"));
        _tracker.IsEndedForGood(worker).ShouldBeTrue();
    }
}
