using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A session that ends without the node stopping it (finished or crashed, as the tracker sees it) has its queued jobs dropped
/// ("dropped: its worker ended", nobody woken), and a running job of an ended session nothing replaced wakes nobody (#327).
/// </summary>
// The jobs run on FakeJobLauncher and every wait is on the state's change feed: no shell, no poll, no wall-clock pace.
public sealed class EndedSessionJobsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly string _root = Directory.CreateTempSubdirectory("ended-jobs-").FullName;
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly ConcurrentQueue<CommandRequest> _said = new();
    private readonly SessionTracker _tracker;
    private readonly JobRunner _jobs;
    private readonly StateChanges _changes;
    private readonly IDisposable _feed;

    // A ceiling a busy machine does not reach, never a pace: every wait below is on the state's next change.
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(2);

    public EndedSessionJobsTests()
    {
        (_changes, _feed) = StateChanges.Watch(_state);
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            Launcher = new FakeJobLauncher(TimeProvider.System),
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                return Task.FromResult(_state.Complete(command, null));
            },
        };
        _tracker.DropJobs = (owners, why) => _jobs.DropQueuedOf(owners, why);
        _jobs.EndedForGood = _tracker.IsEndedForGood;
        _jobs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _jobs.Recovered.WaitAsync(Ceiling, Ct).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _feed.Dispose();
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _profile.Dispose();
        try
        {
            TestDirs.DeleteTree(_root);
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

    private async Task<JobRow> Until(string id, Func<JobRow, bool> condition)
    {
        JobRow? job = null;
        await _changes.UntilAsync(() => condition(job = _state.Jobs().Single(j => j.Id == id)), Ceiling, Ct, $"job {id} did not reach the state");
        return job!;
    }

    private async Task Release(string holder)
    {
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(holder), Who = "test" });
        await Until(holder, j => j.State == JobState.Cancelled);
        foreach (var left in _state.Jobs().Where(j => j.State is JobState.Queued or JobState.Running))
        {
            await Until(left.Id, j => j.State is not (JobState.Queued or JobState.Running));
        }
    }

    private async Task QueuedBehindARunningJob(string endedListing)
    {
        var worker = AgentRows.SessionA[..8];
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242), Live(AgentRows.SessionB, "worker-b", 4343));
        var holder = Queue(Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var first = Queue(Verbs.SessionTarget(worker), "echo one");
        var second = Queue(Verbs.SessionTarget(worker), "echo two");
        await Until(second, j => j.Step?.Contains("queued, 1 ahead") == true);

        // One listing that omits it is a blip; the end counts once the next poll agrees.
        await Poll(endedListing, Live(AgentRows.SessionB, "worker-b", 4343));
        _state.Jobs().Single(j => j.Id == first).State.ShouldBe(JobState.Queued);
        await Poll(endedListing, Live(AgentRows.SessionB, "worker-b", 4343));
        _state.Sessions().Single(s => s.Id == worker).State.ShouldBeOneOf(SessionState.Ended, SessionState.Failed);

        foreach (var id in new[] { first, second })
        {
            var row = await Until(id, j => j.State == JobState.Cancelled);
            row.Error.ShouldNotBeNull().ShouldStartWith("dropped:");
            row.Error.ShouldContain("its worker ended");
            row.Told.ShouldBeTrue();
        }
        _said.ShouldBeEmpty();
        var live = Queue(Verbs.SessionTarget(AgentRows.SessionB[..8]), "echo three");
        (await Until(live, j => j.Step?.StartsWith("next in line") == true)).State.ShouldBe(JobState.Queued);

        await Release(holder);
    }

    [Fact]
    public Task AFinishedSessionsQueuedJobsAreDroppedWithoutWakingAnyone() =>
        QueuedBehindARunningJob(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"));

    [Fact]
    public Task AFailedSessionsQueuedJobsAreDroppedWithoutWakingAnyone() =>
        QueuedBehindARunningJob(AgentRows.Background(AgentRows.SessionA, "worker-a", "failed"));

    [Fact]
    public async Task AnEndedSessionsQueuedJobsStayWhenALiveCopyOfItIsListed()
    {
        var worker = AgentRows.SessionA[..8];
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242));
        var holder = Queue(Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var queued = Queue(Verbs.SessionTarget(worker), "echo one");
        await Until(queued, j => j.Step?.StartsWith("next in line") == true);

        // The old row ends, and a newer live row of the same name is listed after it in the same poll.
        await Poll(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"),
            AgentRows.Background(AgentRows.SessionB, "worker-a", "working", pid: 4343, status: "busy", startedAt: 1792000099000));
        await Poll(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"),
            AgentRows.Background(AgentRows.SessionB, "worker-a", "working", pid: 4343, status: "busy", startedAt: 1792000099000));
        _state.Sessions().Single(s => s.Id == worker).State.ShouldBeOneOf(SessionState.Ended, SessionState.Failed);

        // Nothing to wait out: the tracker drops (or keeps) the jobs inside the poll that ends the session, so the row is already final.
        _state.Jobs().Single(j => j.Id == queued).State.ShouldBe(JobState.Queued);

        await Release(holder);
    }

    [Fact]
    public async Task AnEndedSessionTheCommanderIsReplacingKeepsItsQueuedJobsUntilItsReplacementExists()
    {
        var worker = AgentRows.SessionA[..8];
        var replacing = true;
        _tracker.IsBeingReplaced = id => replacing && id == worker;
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242));
        var holder = Queue(Verbs.NodeTarget, "sleep 60; echo slept");
        await Until(holder, j => j.State == JobState.Running);
        var queued = Queue(Verbs.SessionTarget(worker), "echo one");
        await Until(queued, j => j.Step?.StartsWith("next in line") == true);

        // Ended and nothing listed in its place yet: the replacement is still starting, so its jobs stay.
        await Poll(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"));
        await Poll(AgentRows.Background(AgentRows.SessionA, "worker-a", "done"));
        // Nothing to wait out: the drop decision is made inside the poll, so the row is already final.
        _state.Jobs().Single(j => j.Id == queued).State.ShouldBe(JobState.Queued);
        _tracker.IsEndedForGood(worker).ShouldBeFalse();

        replacing = false;
        await Release(holder);
    }

    [Fact]
    public async Task ARunningJobOfAnEndedUnreplacedSessionWakesNobodyWhenItEnds()
    {
        var worker = AgentRows.SessionA[..8];
        await Poll(Live(AgentRows.SessionA, "worker-a", 4242));
        var running = Queue(Verbs.SessionTarget(worker), "sleep 60; echo slept");
        await Until(running, j => j.State == JobState.Running);

        await Poll();
        await Poll();
        _state.Sessions().Single(s => s.Id == worker).State.ShouldBeOneOf(SessionState.Ended, SessionState.Failed);
        _state.Jobs().Single(j => j.Id == running).State.ShouldBe(JobState.Running);

        await Release(running);
        (await Until(running, j => j.Told)).Told.ShouldBeTrue();
        _said.ShouldBeEmpty();
    }
}
