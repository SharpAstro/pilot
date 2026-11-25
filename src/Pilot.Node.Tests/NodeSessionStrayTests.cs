using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node session's own claude process is never left spinning (se1-fix-tianwen-2480, SE1, 2026-11-23 to 11-25: an idle PR fixer's
/// claude.exe, renamed claude.exe.old.* by a Claude Code self-update and hanging below Claude Code's launcher, not the node, span a core for
/// 33 h while its row read ended). The sweep finds it by the session id and name its command line starts, and by its own CPU while the session
/// is idle, and ends it; a person's session is never touched. In-memory state, a fake process table and a fake clock: nothing real is read,
/// ended or waited on.
/// </summary>
public sealed class NodeSessionStrayTests
{
    private static readonly DateTimeOffset Began = new(2026, 11, 23, 10, 56, 0, TimeSpan.Zero);
    private const string Plane = @"C:\repos\tianwen\.claude\worktrees\fix-tianwen-2480";
    private const string Name = "se1-fix-tianwen-2480";
    private const string SessionId = "aa79e66e-428e-45f7-bbbf-d72cf91bf1f0";
    private const string Forked = "de01c934-3e93-45a3-a507-09842a33e9e5";

    private sealed class FakeTable : IProcessTable
    {
        public List<ProcessEntry> Entries { get; } = [];
        public Dictionary<int, DateTimeOffset> Starts { get; } = [];
        public Dictionary<int, TimeSpan> Cpu { get; } = [];
        public Dictionary<int, ProcessDetail> Details { get; } = [];
        public List<int> Killed { get; } = [];

        public void Add(int pid, int parent, string name, DateTimeOffset start, string? commandLine = null, TimeSpan? cpu = null)
        {
            Entries.Add(new ProcessEntry(pid, parent, name));
            Starts[pid] = start;
            Details[pid] = new ProcessDetail(commandLine, null);
            if (cpu is { } used)
            {
                Cpu[pid] = used;
            }
        }

        public IReadOnlyList<ProcessEntry> Snapshot() => [.. Entries];

        public DateTimeOffset? StartOf(int pid) => Starts.TryGetValue(pid, out var start) ? start : null;

        public void Kill(int pid) => Killed.Add(pid);

        public TimeSpan? CpuTimeOf(int pid) => Cpu.TryGetValue(pid, out var used) ? used : null;

        public ProcessDetail? DetailsOf(int pid) => Details.TryGetValue(pid, out var detail) ? detail : null;
    }

    private readonly FakeTable _table = new();
    private readonly ManualTime _time = new(Began.AddHours(33));
    private readonly NodeState _state;
    private readonly StraySweep _sweep;
    private readonly List<(string Id, int Pid)> _live = [];

    public NodeSessionStrayTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _table.Add(1, 0, "System", Began.AddDays(-1));
        // Claude Code's own launcher: alive, and nothing of the node's.
        _table.Add(29784, 1, "claude.exe", Began.AddDays(-1));
        _sweep = new StraySweep(_state, _table, _time) { Windows = true, LivePids = () => [.. _live] };
    }

    // How the node launches a PR fixer's compaction: the new session id, the one it forks, and the session's name.
    private static string NodeCommandLine(string name = Name) =>
        $@"C:\Users\me\.local\bin\claude.exe --session-id {SessionId} --fork-session --resume {Forked} -n {name} --permission-mode auto --model sonnet -- ""/compact Keep your task""";

    private void PutSession(SessionState state, string? startReason = SessionStartReasons.PrFix, DateTimeOffset? ended = null, DateTimeOffset? answered = null) => _state.PutSession(new SessionRow
    {
        Id = SessionId[..8], SessionId = SessionId, Name = Name, Kind = SessionKind.Background, Cwd = Plane, Role = SessionRole.Fixer,
        ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Began, StartReason = startReason,
        EndedAt = ended, LastResponseAt = answered,
    });

    [Fact]
    public void A_node_sessions_claude_that_outlived_its_row_is_ended_with_its_children()
    {
        // The row turned ended a moment before the process it launched was even listed; the process lives on below Claude Code's launcher.
        PutSession(SessionState.Ended, ended: Began);
        _table.Add(34212, 29784, "claude.exe.old.1795472384929.35552", Began.AddSeconds(7), NodeCommandLine(), TimeSpan.FromHours(17));
        _table.Add(20388, 34212, "pilot.exe~old-3", Began.AddSeconds(10), "pilot.exe mcp");

        _sweep.SweepOnce().ShouldBeEmpty();

        _table.Killed.ShouldBe([20388, 34212], ignoreOrder: true);
        var handled = _state.Cards().Where(c => c.Kind == StraySweep.KilledKind).ToList();
        handled.Count.ShouldBe(2);
        handled.ShouldAllBe(c => (c.Detail ?? "").Contains("its row has ended, and this is the session's own process by its session id and name"));
    }

    [Fact]
    public void The_finder_ties_the_process_by_its_session_id_not_its_parent()
    {
        var session = new StraySession(Name, NodeLaunched: true, Pid: null, Start: null, Folder: null, EndedAt: Began, SessionIds: [SessionId]);
        _table.Add(34212, 29784, "claude.exe.old.1", Began.AddSeconds(7), NodeCommandLine());

        var stray = new StrayFinder(_table, _time, windows: true).Find([], [session], StrayHistory.Empty).Strays.ShouldHaveSingleItem();

        stray.Pid.ShouldBe(34212);
        stray.BySession.ShouldBeTrue();
        stray.OwnerEnded.ShouldBeTrue();
        stray.Reason.ShouldContain($"its command line runs session {SessionId}");
    }

    [Fact]
    public void A_person_resuming_a_node_session_by_hand_is_never_taken_for_it()
    {
        // Same session id, but no node name: a person's own `claude --resume`.
        PutSession(SessionState.Ended, ended: Began);
        _table.Add(500, 29784, "claude.exe", Began.AddSeconds(30), $"claude.exe --resume {SessionId}");
        _table.Add(501, 29784, "claude.exe", Began.AddSeconds(30), $"claude.exe --resume {SessionId} -n my-own-look");

        _sweep.SweepOnce().ShouldBeEmpty();

        _table.Killed.ShouldBeEmpty();
    }

    [Fact]
    public void A_later_resume_of_an_ended_session_is_a_new_process_and_left_alone()
    {
        // The row ended long before this process started: a resume the node or a person made since, not the ended session's leftover.
        PutSession(SessionState.Ended, ended: Began);
        _table.Add(700, 29784, "claude.exe", Began.AddHours(2), NodeCommandLine());

        _sweep.SweepOnce().ShouldBeEmpty();

        _table.Killed.ShouldBeEmpty();
    }

    [Fact]
    public void A_live_node_sessions_claude_that_spins_while_idle_is_ended()
    {
        // Listed as idle, last answered an hour ago, its own process using a whole core between two sweeps.
        PutSession(SessionState.Idle, answered: _time.GetUtcNow() - TimeSpan.FromHours(1));
        _table.Add(34212, 29784, "claude.exe", Began.AddSeconds(7), NodeCommandLine(), TimeSpan.FromHours(17));
        _live.Add((SessionId[..8], 34212));

        _sweep.SweepOnce();
        _table.Killed.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(5));
        _table.Cpu[34212] += TimeSpan.FromMinutes(5);
        _sweep.SweepOnce();

        _table.Killed.ShouldBe([34212]);
        _state.Cards().ShouldContain(c => c.Kind == StraySweep.KilledKind && (c.Detail ?? "").Contains("computes while the session is idle"));
    }

    [Fact]
    public void A_live_node_sessions_claude_at_rest_is_left_alone()
    {
        PutSession(SessionState.Idle, answered: _time.GetUtcNow() - TimeSpan.FromHours(1));
        _table.Add(34212, 29784, "claude.exe", Began.AddSeconds(7), NodeCommandLine(), TimeSpan.FromMinutes(2));
        _live.Add((SessionId[..8], 34212));

        _sweep.SweepOnce();
        _time.Advance(TimeSpan.FromMinutes(5));
        _table.Cpu[34212] += TimeSpan.FromSeconds(1);

        _sweep.SweepOnce().ShouldBeEmpty();
        _table.Killed.ShouldBeEmpty();
    }

    [Fact]
    public void A_working_sessions_claude_may_use_the_cpu()
    {
        PutSession(SessionState.Working);
        _table.Add(34212, 29784, "claude.exe", Began.AddSeconds(7), NodeCommandLine(), TimeSpan.FromHours(1));
        _live.Add((SessionId[..8], 34212));

        _sweep.SweepOnce();
        _time.Advance(TimeSpan.FromMinutes(5));
        _table.Cpu[34212] += TimeSpan.FromMinutes(5);

        _sweep.SweepOnce().ShouldBeEmpty();
        _table.Killed.ShouldBeEmpty();
    }

    [Fact]
    public void A_persons_spinning_session_is_never_ended_nor_named()
    {
        // No start reason: the node did not launch it.
        PutSession(SessionState.Idle, startReason: null, answered: _time.GetUtcNow() - TimeSpan.FromHours(1));
        _table.Add(34212, 29784, "claude.exe", Began.AddSeconds(7), "claude.exe", TimeSpan.FromHours(1));
        _live.Add((SessionId[..8], 34212));

        _sweep.SweepOnce();
        _time.Advance(TimeSpan.FromMinutes(5));
        _table.Cpu[34212] += TimeSpan.FromMinutes(5);

        _sweep.SweepOnce().ShouldBeEmpty();
        _table.Killed.ShouldBeEmpty();
    }
}
