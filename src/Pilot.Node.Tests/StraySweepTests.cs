using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The stray sweep (pilot #909) over an in-memory node state, a fake process table and a fake clock: no real process is read or ended, and
/// nothing waits.
/// </summary>
public sealed class StraySweepTests
{
    private static readonly DateTimeOffset Began = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
    private const string Plane = @"C:\repos\pilot\.claude\worktrees\pil909";

    private sealed class FakeTable : IProcessTable
    {
        public List<ProcessEntry> Entries { get; } = [];
        public Dictionary<int, DateTimeOffset> Starts { get; } = [];
        public Dictionary<int, TimeSpan> Cpu { get; } = [];
        public Dictionary<int, ProcessDetail> Details { get; } = [];
        public List<int> Killed { get; } = [];

        public void Add(int pid, int parent, string name, DateTimeOffset start, TimeSpan? cpu = null, string? commandLine = null, string? folder = null)
        {
            Entries.Add(new ProcessEntry(pid, parent, name));
            Starts[pid] = start;
            if (cpu is { } used)
            {
                Cpu[pid] = used;
            }
            Details[pid] = new ProcessDetail(commandLine, folder);
        }

        public void Remove(int pid)
        {
            Entries.RemoveAll(e => e.Pid == pid);
            Starts.Remove(pid);
        }

        public IReadOnlyList<ProcessEntry> Snapshot() => [.. Entries];

        public DateTimeOffset? StartOf(int pid) => Starts.TryGetValue(pid, out var start) ? start : null;

        // A kill is recorded; the process stays, as one that would not die does.
        public void Kill(int pid) => Killed.Add(pid);

        public TimeSpan? CpuTimeOf(int pid) => Cpu.TryGetValue(pid, out var used) ? used : null;

        public ProcessDetail? DetailsOf(int pid) => Details.TryGetValue(pid, out var detail) ? detail : null;
    }

    private readonly FakeTable _table = new();
    private readonly ManualTime _time = new(Began.AddHours(3));
    private readonly NodeState _state;
    private readonly StraySweep _sweep;
    private readonly List<(string Id, int Pid)> _live = [];
    private readonly Dictionary<string, List<KnownProcess>> _known = [];

    public StraySweepTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _table.Add(1, 0, "systemd", Began.AddDays(-1));
        _sweep = new StraySweep(_state, _table, _time)
        {
            Windows = true,
            LivePids = () => [.. _live],
            KnownOf = id => _known.TryGetValue(id, out var known) ? known : [],
        };
    }

    private void Job(string id, int wrapper, DateTimeOffset started, TimeSpan timeout, DateTimeOffset? ended = null) => _state.PutJob(new JobRow
    {
        Id = id, Command = "dotnet test", Cwd = Plane, State = ended is null ? JobState.Running : JobState.Succeeded, LogPath = @"C:\data\" + id + ".log",
        Timeout = timeout, StartedAt = started, Pid = wrapper, ProcessStartedAt = started, EndedAt = ended,
    });

    private void Session(string id, string name, SessionState state, string? startReason, DateTimeOffset? ended = null, string cwd = Plane) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = cwd,
        Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Began,
        StartReason = startReason, EndedAt = ended, EndReason = ended is null ? null : SessionEndReasons.Finished,
    });

    // A job j7 that has run three hours of a ten-minute limit, with a test host below its wrapper that has used three hours of CPU.
    private void OverLimitJob()
    {
        Job("j7", 500, Began, TimeSpan.FromMinutes(10));
        _table.Add(500, 1, "pwsh.exe", Began);
        _table.Add(501, 500, "testhost.exe", Began.AddMinutes(1), cpu: TimeSpan.FromHours(3), commandLine: @"testhost.exe --port 5000 C:\repos\pilot\Pilot.Node.Tests.dll");
    }

    private CardRow[] Open(string kind) => [.. _state.Cards(openOnly: true).Where(c => c.Kind == kind)];

    [Fact]
    public void OneCardPerStrayNamesTheProcessItsOwnerAndTheKillCommand()
    {
        OverLimitJob();

        var named = _sweep.SweepOnce();

        // The wrapper and the test host below it are both over the job's limit: a card each, the stray's pid and start its subject.
        named.Select(s => s.Pid).ShouldBe([500, 501]);
        var cards = Open(StraySweep.Kind);
        cards.Length.ShouldBe(2);
        cards.ShouldAllBe(c => c.Bin == CardBin.ForYou);
        var host = cards.Single(c => c.Subject.StartsWith("process:501@", StringComparison.Ordinal));
        host.Title.ShouldContain("testhost.exe");
        host.Title.ShouldContain("501");
        host.Title.ShouldContain("job j7");
        host.Detail.ShouldNotBeNull();
        host.Detail.ShouldContain("Process 501 (testhost.exe) of job j7");
        host.Detail.ShouldContain("running 2 h 59 min");
        host.Detail.ShouldContain("CPU time 3 h 0 min");
        host.Detail.ShouldContain("Command line: testhost.exe --port 5000 C:\\repos\\pilot\\Pilot.Node.Tests.dll");
        host.Detail.ShouldContain("over its time limit of 10 min");
        host.Detail.ShouldContain("Kill command: Stop-Process -Id 501 -Force");
        // The same sweep again is the same cards, not more.
        _sweep.SweepOnce();
        Open(StraySweep.Kind).Length.ShouldBe(2);
        _table.Killed.ShouldBeEmpty();
    }

    [Fact]
    public void ADismissedCardIsNotRaisedAgainForTheSamePidAndStartButAProcessThatReusedThePidIs()
    {
        OverLimitJob();
        _sweep.SweepOnce();
        foreach (var card in Open(StraySweep.Kind))
        {
            _state.CloseCard(card.Id, "dismissed", "gui").ShouldBeTrue();
        }

        _time.Advance(TimeSpan.FromMinutes(5));
        _sweep.SweepOnce();

        Open(StraySweep.Kind).ShouldBeEmpty();

        // The test host ends and another process takes its pid, started later: another cause, so a card again.
        _table.Remove(501);
        _table.Add(501, 500, "testhost.exe", Began.AddHours(2), commandLine: "testhost.exe");
        _sweep.SweepOnce();

        Open(StraySweep.Kind).Select(c => c.Subject).ShouldHaveSingleItem().ShouldStartWith("process:501@");
    }

    [Fact]
    public void TheCardClosesOnceTheProcessIsGoneAndTheMachineRowNamesNoStray()
    {
        _state.PutMachine(new MachineRow { SampledAt = Began, CpuPercent = 5, Cores = 8, MemoryUsed = 1, MemoryTotal = 100 });
        OverLimitJob();
        _sweep.SweepOnce();
        _state.Machine()!.Strays.Select(s => s.Pid).ShouldBe([500, 501]);

        _table.Remove(501);
        _table.Remove(500);
        _sweep.SweepOnce();

        Open(StraySweep.Kind).ShouldBeEmpty();
        _state.Cards().Where(c => c.Kind == StraySweep.Kind).ShouldAllBe(c => c.State == CardState.Closed && c.Outcome == "the process is gone");
        _state.Machine()!.Strays.ShouldBeEmpty();
    }

    [Fact]
    public void AProcessOfAnEndedJobTheNodeLaunchedIsKilledThroughTheTableAndNothingElseIs()
    {
        // 1. j9 ended ten minutes ago; a test host it started while it ran lives on, its wrapper long gone: the node ends it.
        Job("j9", 600, Began, TimeSpan.FromHours(2), ended: _time.GetUtcNow().AddMinutes(-10));
        _table.Add(601, 600, "testhost.exe", Began.AddMinutes(1), commandLine: "testhost.exe");
        _known["j9"] = [new KnownProcess(601, Began.AddMinutes(1))];
        // 2. j7 still runs, over its limit: named, never ended (the job runner stops its own jobs).
        OverLimitJob();
        // 3. A session the node launched ended ten minutes ago; a process only works in its plane, which may be a person's editor: named, never ended.
        Session("w1", "se1-pil909-old", SessionState.Ended, SessionStartReasons.LegLaunch, ended: _time.GetUtcNow().AddMinutes(-10));
        _table.Add(700, 1, "Code.exe", Began.AddHours(1), commandLine: "Code.exe", folder: Plane);
        // 4. A person's own session, and a process in its folder: not a stray at all.
        Session("p1", "se1-sebastian", SessionState.Idle, startReason: null, cwd: @"C:\repos\other");
        _live.Add(("p1", 800));
        _table.Add(800, 1, "claude.exe", Began.AddHours(1), commandLine: "claude.exe");
        _table.Add(801, 800, "dotnet.exe", Began.AddHours(1), commandLine: "dotnet.exe");

        var named = _sweep.SweepOnce();

        _table.Killed.ShouldBe([601]);
        named.Select(s => s.Pid).Order().ShouldBe([500, 501, 700]);
        var ended = _state.Cards().Single(c => c.Kind == StraySweep.KilledKind);
        ended.Bin.ShouldBe(CardBin.Handled);
        ended.Detail.ShouldNotBeNull().ShouldContain("Stop-Process -Id 601 -Force");
        Open(StraySweep.Kind).Select(c => c.Subject.Split('@')[0]).Order().ShouldBe(["process:500", "process:501", "process:700"]);
        Open(StraySweep.Kind).Single(c => c.Subject.StartsWith("process:700@", StringComparison.Ordinal)).Detail.ShouldNotBeNull()
            .ShouldContain("tied to its owner by its folder or command line only");

        // The one the node ended that stays alive is not ended a second time: it is named, and the card says it is still there.
        _sweep.SweepOnce();
        _table.Killed.ShouldBe([601]);
        Open(StraySweep.Kind).Single(c => c.Subject.StartsWith("process:601@", StringComparison.Ordinal)).Detail.ShouldNotBeNull()
            .ShouldContain("tried to end it and it is still here");
    }
}
