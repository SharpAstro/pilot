using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The sweep of ended Claude Code jobs (pilot #891): what it stops and removes, and what it never touches.</summary>
public sealed class ClaudeJobSweepTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Old = TimeSpan.FromDays(2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2js-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state;
    private readonly ManualTime _time = new(Start);
    private readonly SessionLedger _ledger;
    private readonly FakeJobs _jobs;
    private readonly FakeClaudeAgents _agents = new();
    private readonly StringWriter _log = new();
    private readonly List<(string Id, int Pid)> _processes = [];
    private SessionRow? _watchtower;

    public ClaudeJobSweepTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _ledger = new SessionLedger(time: _time);
        Directory.CreateDirectory(Jobs);
        _jobs = new FakeJobs(new ClaudeCliJobs(Jobs));
    }

    private string Jobs => Path.Combine(_root, "jobs");

    public void Dispose() => TestDirs.DeleteTree(_root);

    private ClaudeJobSweep Sweep() => new(_state, _ledger, _jobs, _agents, "se1", _time)
    {
        CurrentWatchtower = () => _watchtower,
        Error = _log,
    };

    private static string Full(string id) => id + "-0000-4000-8000-000000000000";

    // A job folder as Claude Code writes it: only the fields the sweep reads.
    private string Job(string id, string state, string name, TimeSpan updatedAgo, string? worktree = null)
    {
        var folder = Path.Combine(Jobs, id);
        Directory.CreateDirectory(folder);
        var at = (Start - updatedAgo).ToString("O");
        File.WriteAllText(Path.Combine(folder, "state.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["state"] = state, ["name"] = name, ["sessionId"] = Full(id), ["worktreePath"] = worktree, ["createdAt"] = at, ["updatedAt"] = at,
        }));
        return folder;
    }

    // A process the listing shows for the job.
    private void Process(string id, string name)
    {
        _processes.Add((id, 100 + _processes.Count));
        _agents.Json = "[" + string.Join(",", _processes.Select(p =>
            $$"""{"id":"{{p.Id}}","sessionId":"{{Full(p.Id)}}","pid":{{p.Pid}},"cwd":"/x","kind":"background","name":"{{name}}","state":"working","status":"idle"}""")) + "]";
    }

    private void Row(string id, string name, SessionState state, string[]? earlier = null) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = Full(id), EarlierSessionIds = earlier, Name = name, Kind = SessionKind.Background, Cwd = "/x",
        Role = SessionRoles.Resolve(name).Role, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Start - Old,
    });

    private void Ledgered(string id, string name, string? endReason = null)
    {
        _ledger.Started(id, name, SessionRoles.Resolve(name), SessionStartReasons.LegLaunch, null);
        if (endReason is not null)
        {
            _ledger.Stopped([id], endReason, "node");
        }
    }

    [Fact]
    public async Task An_ended_ledger_job_is_stopped_then_removed()
    {
        Job("aaaa1111", "done", "se1-pil891-job-sweep", TimeSpan.FromMinutes(5));
        Ledgered("aaaa1111", "se1-pil891-job-sweep", SessionEndReasons.Landed);
        Process("aaaa1111", "se1-pil891-job-sweep");

        var result = await Sweep().SweepAsync();

        _jobs.Calls.ShouldBe(["stop aaaa1111", "rm aaaa1111"]);
        result.Removed.ShouldBe(1);
        result.Stopped.ShouldBe(1);
        _log.ToString().ShouldContain("aaaa1111 se1-pil891-job-sweep");
    }

    [Fact]
    public async Task An_ended_row_job_with_no_process_is_removed_without_a_stop()
    {
        Job("bbbb2222", "stopped", "se1-pil891-nudge", TimeSpan.FromMinutes(5));
        Row("bbbb2222", "se1-pil891-nudge", SessionState.Ended);

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBe(["rm bbbb2222"]);
    }

    [Fact]
    public async Task A_ledger_job_is_known_by_the_first_eight_characters_of_a_later_session_id()
    {
        // The folder is named by the id the job started under; the ledger keeps only the later session's full id.
        Job("cccc3333", "done", "renamed by claude", TimeSpan.FromMinutes(5));
        _ledger.Started("zzzz9999", "se1-pil891-x", SessionRoles.Resolve("se1-pil891-x"), SessionStartReasons.LegLaunch, null);
        _ledger.Observe(new SessionRow
        {
            Id = "zzzz9999", SessionId = Full("cccc3333"), Name = "x", Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Worker,
            ContextTokens = 0, ContextWindow = 1, State = SessionState.Ended, SpendWeighted = 0, StartedAt = Start,
        }, Start);

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBe(["rm cccc3333"]);
    }

    [Theory]
    [InlineData("se1-aaa123-leg")]
    [InlineData("se1-watchtower")]
    public async Task A_blocked_pilot_role_job_over_a_day_old_with_no_process_is_removed(string name)
    {
        Job("dddd4444", "blocked", name, Old);

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBe(["rm dddd4444"]);
    }

    [Fact]
    public async Task A_blocked_job_under_a_day_old_is_left()
    {
        Job("eeee5555", "blocked", "se1-aaa123-leg", TimeSpan.FromHours(23));

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_blocked_job_with_a_process_is_left()
    {
        Job("ffff6666", "blocked", "se1-aaa123-leg", Old);
        Process("ffff6666", "se1-aaa123-leg");

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_of_a_live_row_is_left_under_its_id_and_its_earlier_ids()
    {
        Job("11110000", "blocked", "se1-aaa123-leg", Old);
        Job("22220000", "blocked", "se1-bbb456-leg", Old);
        Row("11110000", "se1-aaa123-leg", SessionState.Working);
        // Resumed since: the live row is a later id and remembers the folder's.
        Row("33330000", "se1-bbb456-leg", SessionState.Idle, earlier: [Full("22220000")]);
        Ledgered("11110000", "se1-aaa123-leg", SessionEndReasons.Finished);

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_current_watchtowers_job_is_left()
    {
        Job("44440000", "blocked", "se1-watchtower", Old);
        Row("44440000", "se1-watchtower", SessionState.Ended);
        Ledgered("44440000", "se1-watchtower", SessionEndReasons.Finished);
        _watchtower = _state.Sessions().Single();

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replaced_watchtower_is_removed()
    {
        Job("55550000", "blocked", "se1-watchtower", Old);
        Row("55550000", "se1-watchtower", SessionState.Ended);
        Ledgered("55550000", "se1-watchtower", SessionEndReasons.Watchtower);
        Row("66660000", "se1-watchtower", SessionState.Idle);
        _watchtower = _state.Sessions().Single(s => s.Id == "66660000");

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBe(["rm 55550000"]);
    }

    [Theory]
    [InlineData("se1-fix-sdlvulkan-renderer-153")]
    [InlineData("de1-aaa123-leg")]
    [InlineData("a person's own session")]
    public async Task A_job_pilot_did_not_start_is_left(string name)
    {
        // Old, blocked, and its row ended: still not pilot's, so not touched.
        Job("77770000", "blocked", name, Old);
        Row("77770000", name, SessionState.Ended);

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_job_whose_worktree_still_exists_is_stopped_and_not_removed_until_it_is_gone()
    {
        var plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(plane);
        Job("88880000", "done", "se1-aaa123-leg", TimeSpan.FromMinutes(5), worktree: plane);
        Ledgered("88880000", "se1-aaa123-leg", SessionEndReasons.Landed);
        Process("88880000", "se1-aaa123-leg");

        var first = await Sweep().SweepAsync();

        _jobs.Calls.ShouldBe(["stop 88880000"]);
        first.Kept.ShouldBe(1);

        Directory.Delete(plane);
        _processes.Clear();
        _agents.Json = "[]";
        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBe(["stop 88880000", "rm 88880000"]);
    }

    [Fact]
    public void Rm_is_given_the_job_and_nothing_else()
    {
        ClaudeCliJobs.RemoveArguments("abc12345").ShouldBe(["rm", "abc12345"]);
        ClaudeCliJobs.StopArguments("abc12345").ShouldBe(["stop", "abc12345"]);
        _jobs.Calls.ShouldNotContain(c => c.Contains("--"));
    }

    [Fact]
    public async Task One_failing_call_does_not_stop_the_sweep()
    {
        Job("99990000", "done", "se1-aaa123-one", TimeSpan.FromMinutes(5));
        Job("99991111", "done", "se1-aaa123-two", TimeSpan.FromMinutes(5));
        Job("99992222", "done", "se1-aaa123-three", TimeSpan.FromMinutes(5));
        Ledgered("99990000", "se1-aaa123-one", SessionEndReasons.Finished);
        Ledgered("99991111", "se1-aaa123-two", SessionEndReasons.Finished);
        Ledgered("99992222", "se1-aaa123-three", SessionEndReasons.Finished);
        Process("99990000", "se1-aaa123-one");
        _jobs.Failing.Add("stop 99990000");
        _jobs.Failing.Add("rm 99991111");

        var result = await Sweep().SweepAsync();

        _jobs.Calls.ShouldContain("rm 99992222");
        _jobs.Calls.ShouldNotContain("rm 99990000");
        result.Failed.ShouldBe(2);
        result.Removed.ShouldBe(1);
        _log.ToString().ShouldContain("failed");
    }

    [Fact]
    public async Task A_listing_that_fails_sweeps_nothing()
    {
        Job("aaaa0000", "blocked", "se1-aaa123-leg", Old);
        _agents.Failure = new InvalidOperationException("claude agents exited 1");

        await Sweep().SweepAsync();

        _jobs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_service_sweeps_once_the_tracker_has_listed_and_not_before()
    {
        Job("bbbb0000", "blocked", "se1-aaa123-leg", Old);
        var ready = new TaskCompletionSource();
        var sweep = new ClaudeJobSweep(_state, _ledger, _jobs, _agents, "se1", _time) { Ready = () => ready.Task, Error = _log };

        await sweep.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        _jobs.Calls.ShouldBeEmpty();

        ready.SetResult();
        var until = DateTime.UtcNow.AddSeconds(3);
        while (_jobs.Calls.Count == 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }
        await sweep.StopAsync(CancellationToken.None);

        _jobs.Calls.ShouldBe(["rm bbbb0000"]);
    }

    // Reads the temp folder as the real implementation does, and records the calls instead of making them.
    private sealed class FakeJobs(ClaudeCliJobs folders) : IClaudeJobs
    {
        private readonly List<string> _calls = [];

        public HashSet<string> Failing { get; } = [];

        public List<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public IReadOnlyList<ClaudeJobFolder> Folders() => folders.Folders();

        public Task<ClaudeJobCall> StopAsync(string job, CancellationToken cancellationToken) => Record($"stop {job}");

        public Task<ClaudeJobCall> RemoveAsync(string job, CancellationToken cancellationToken) => Record($"rm {job}");

        private Task<ClaudeJobCall> Record(string call)
        {
            lock (_calls)
            {
                _calls.Add(call);
            }
            return Task.FromResult(Failing.Contains(call) ? new ClaudeJobCall(false, "exit 1") : new ClaudeJobCall(true, ""));
        }
    }
}
