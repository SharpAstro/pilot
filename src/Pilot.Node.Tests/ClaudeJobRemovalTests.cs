using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The removal of a Claude Code job at the moment the node ends a session it started (pilot #891): a stopped worker's and watchtower's job
/// goes, one whose plane still exists stays for the sweep, a failed rm costs the stop nothing, and the ended row and its ledger reason
/// outlive the job. In memory: a fake <see cref="IClaudeJobs"/>, a fake delivery and stopper, a temp profile.
/// </summary>
public sealed class ClaudeJobRemovalTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Job = AgentRows.SessionA[..8];
    private const string Name = "se1-pil891-remove-on-end";

    private readonly TempProfile _profile = new();
    private readonly FakeClaudeAgents _agents = new();
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly SessionLedger _ledger = new(time: TimeProvider.System);
    private readonly StartOptionsBook _book = new();
    private readonly InMemoryJobs _jobs = new();
    private readonly StringWriter _log = new();
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;
    private readonly SessionMessenger _messenger;

    public ClaudeJobRemovalTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance) { Launched = _ledger };
        _delivery = new FakeSessionDelivery(_agents);
        _messenger = new SessionMessenger(_state, _tracker, _delivery, new StringWriter(), _book, _ledger) { Removal = new ClaudeJobRemoval(_jobs, _log) };
    }

    public void Dispose() => _profile.Dispose();

    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static CommandRequest End(string who, string why) =>
        new() { Verb = Verbs.EndSession, Target = Verbs.SessionTarget(Job), Args = [why], Who = who };

    // A session the node started: in the ledger, listed with a process, its job folder on disk (here: in the fake).
    private async Task RunningAsync(string? worktree = null)
    {
        _ledger.Started(Job, Name, SessionRoles.Resolve(Name, background: true), SessionStartReasons.LegLaunch, "claude-sonnet-5-5");
        _jobs.Add(new ClaudeJobFolder(Job, "working", Name, AgentRows.SessionA, worktree, null, null));
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, Name, "working", pid: 1, status: "busy", cwd: Cwd));
        await _tracker.PollAsync(Ct);
    }

    [Fact]
    public async Task A_stopped_worker_job_is_removed_and_its_reason_is_in_the_ledger()
    {
        _ledger.Started(Job, Name, SessionRoles.Resolve(Name, background: true), SessionStartReasons.LegLaunch, null);
        _jobs.Add(new ClaudeJobFolder(Job, "working", Name, AgentRows.SessionA, Path.Combine(_profile.Folder, "plane-gone"), null, null));
        var stopped = new List<string>();
        var launcher = new ClaudeWorkerLauncher(new ClaudeTrust(Path.Combine(_profile.Folder, "claude.json")), ledger: _ledger, removal: new ClaudeJobRemoval(_jobs, _log))
        {
            Stopper = (job, _) =>
            {
                stopped.Add(job);
                return Task.CompletedTask;
            },
        };

        await launcher.StopAsync(Job, SessionEndReasons.Finished, "node", Ct);

        stopped.ShouldBe([Job]);
        _jobs.Calls.ShouldBe([$"rm {Job}"]);
        _jobs.Folders().ShouldBeEmpty();
        _ledger.Find([Job]).ShouldNotBeNull().EndReason.ShouldBe(SessionEndReasons.Finished);
        _log.ToString().ShouldContain($"job removal: {Job} {Name}");
    }

    [Fact]
    public async Task A_stopped_watchtower_job_is_removed()
    {
        await RunningAsync();

        var result = await _messenger.EndAsync(End("de1-watchtower", "replaced by a fresh one"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _delivery.Calls.ShouldBe([$"stop {AgentRows.SessionA}"]);
        _jobs.Calls.ShouldBe([$"rm {Job}"]);
        _log.ToString().ShouldContain("removed");
    }

    [Fact]
    public async Task A_job_whose_plane_exists_is_stopped_and_not_removed()
    {
        // The temp folder is a folder that exists, as a live plane is.
        await RunningAsync(worktree: Path.GetTempPath());

        var result = await _messenger.EndAsync(End("cli", "not wanted"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _delivery.Calls.ShouldBe([$"stop {AgentRows.SessionA}"]);
        _jobs.Calls.ShouldBeEmpty();
        _jobs.Folders().ShouldHaveSingleItem();
        _log.ToString().ShouldContain("kept while its worktree");
    }

    [Fact]
    public async Task A_failed_rm_leaves_the_stop_confirmed_and_the_ledger_reason_intact()
    {
        await RunningAsync();
        _jobs.Failing.Add($"rm {Job}");

        var result = await _messenger.EndAsync(End("cli", "not wanted"), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        _state.Sessions().Single().State.ShouldBe(SessionState.Ended);
        _ledger.Find([Job]).ShouldNotBeNull().EndReason.ShouldBe($"{SessionEndReasons.Person}: not wanted");
        _jobs.Folders().ShouldHaveSingleItem();
        _log.ToString().ShouldContain("remove failed, left for the sweep");
    }

    [Fact]
    public async Task A_removal_that_throws_never_reaches_the_caller()
    {
        _ledger.Started(Job, Name, SessionRoles.Resolve(Name, background: true), SessionStartReasons.LegLaunch, null);
        _jobs.Add(new ClaudeJobFolder(Job, "working", Name, AgentRows.SessionA, null, null, null));
        _jobs.Throwing = true;
        var launcher = new ClaudeWorkerLauncher(new ClaudeTrust(Path.Combine(_profile.Folder, "claude.json")), ledger: _ledger, removal: new ClaudeJobRemoval(_jobs, _log))
        {
            Stopper = (_, _) => Task.CompletedTask,
        };

        await launcher.StopAsync(Job, SessionEndReasons.Finished, "node", Ct);

        _ledger.Find([Job]).ShouldNotBeNull().EndReason.ShouldBe(SessionEndReasons.Finished);
        _log.ToString().ShouldContain("left for the sweep");
    }

    [Fact]
    public async Task An_ended_row_that_drops_out_of_the_agents_listing_stays_ended_with_its_ledger_reason()
    {
        await RunningAsync();
        (await _messenger.EndAsync(End("cli", "not wanted"), Ct)).Ok.ShouldBeTrue();
        _jobs.Folders().ShouldBeEmpty();

        // claude rm took the job, so claude agents --all no longer lists the session.
        _agents.Json = "[]";
        await _tracker.PollAsync(Ct);

        var row = _state.Sessions().ShouldHaveSingleItem();
        row.State.ShouldBe(SessionState.Ended);
        row.EndedBecause.ShouldNotBeNull().ShouldContain("not wanted");
        _ledger.Find([Job]).ShouldNotBeNull().EndReason.ShouldBe($"{SessionEndReasons.Person}: not wanted");
    }

    [Fact]
    public async Task A_resume_of_a_removed_session_still_starts_from_its_transcript_with_the_options_the_node_recorded()
    {
        await RunningAsync();
        var start = new WorkerStart(Name, "/r/pilot", "pil891", "b", "sonnet", "medium", McpConfig: "/d/mcp.json");
        _book.Record(new SessionStartOptions(Name, Job, [.. ClaudeWorkerLauncher.SessionArguments(start)]));
        (await _messenger.EndAsync(End("cli", "not wanted"), Ct)).Ok.ShouldBeTrue();
        _jobs.Folders().ShouldBeEmpty();
        _agents.Json = "[]";
        await _tracker.PollAsync(Ct);
        var row = _state.Sessions().ShouldHaveSingleItem();

        var said = await _messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = ["go on"], Who = "tests" }, Ct);

        said.Ok.ShouldBeTrue(said.Error);
        var request = _delivery.Resumes.ShouldHaveSingleItem();
        request.SessionId.ShouldBe(AgentRows.SessionA);
        request.OptionsOnFile.ShouldBeFalse();
        ClaudeCliDelivery.ResumeArguments(request).ShouldBe(["--bg", "--resume", AgentRows.SessionA, .. ClaudeWorkerLauncher.SessionArguments(start)]);
    }

    /// <summary>The job folders as a map, and the calls made on them; a removed job leaves it, as <c>claude rm</c> takes the folder.</summary>
    private sealed class InMemoryJobs : IClaudeJobs
    {
        private readonly object _gate = new();
        private readonly List<ClaudeJobFolder> _folders = [];
        private readonly List<string> _calls = [];

        public HashSet<string> Failing { get; } = [];

        public bool Throwing { get; set; }

        public List<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public void Add(ClaudeJobFolder folder)
        {
            lock (_gate)
            {
                _folders.Add(folder);
            }
        }

        public IReadOnlyList<ClaudeJobFolder> Folders()
        {
            lock (_gate)
            {
                return [.. _folders];
            }
        }

        public Task<ClaudeJobCall> StopAsync(string job, CancellationToken cancellationToken) => Task.FromResult(Record($"stop {job}"));

        public Task<ClaudeJobCall> RemoveAsync(string job, CancellationToken cancellationToken)
        {
            if (Throwing)
            {
                throw new InvalidOperationException("claude rm blew up");
            }
            var call = Record($"rm {job}");
            if (call.Ok)
            {
                lock (_gate)
                {
                    _folders.RemoveAll(f => f.Id == job);
                }
            }
            return Task.FromResult(call);
        }

        private ClaudeJobCall Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
            return Failing.Contains(call) ? new ClaudeJobCall(false, "exit 1") : new ClaudeJobCall(true, "");
        }
    }
}
