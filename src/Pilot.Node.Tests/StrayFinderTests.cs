using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The stray classifier over a fake process table and a fake clock: no real process is read and nothing waits.</summary>
public sealed class StrayFinderTests
{
    private static readonly DateTimeOffset Began = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
    private const string Plane = @"C:\repos\pilot\.claude\worktrees\pil909";
    private const string Scratch = @"C:\data\jobs\j42";

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeTable : IProcessTable
    {
        public List<ProcessEntry> Entries { get; } = [];
        public Dictionary<int, DateTimeOffset> Starts { get; } = [];
        public Dictionary<int, TimeSpan> Cpu { get; } = [];
        public Dictionary<int, ProcessDetail> Details { get; } = [];

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

        public IReadOnlyList<ProcessEntry> Snapshot() => [.. Entries];

        public DateTimeOffset? StartOf(int pid) => Starts.TryGetValue(pid, out var start) ? start : null;

        public void Kill(int pid) => throw new InvalidOperationException("the finder only decides, it never kills");

        public TimeSpan? CpuTimeOf(int pid) => Cpu.TryGetValue(pid, out var used) ? used : null;

        public ProcessDetail? DetailsOf(int pid) => Details.TryGetValue(pid, out var detail) ? detail : null;
    }

    private readonly FakeTable _table = new();
    private readonly FakeClock _clock = new(Began.AddHours(1));

    public StrayFinderTests()
    {
        _table.Add(1, 0, "systemd", Began.AddDays(-1));
        _table.Add(900, 1, "explorer.exe", Began.AddDays(-1));
    }

    private StrayFinder Finder(bool windows = true) => new(_table, _clock, windows);

    private StrayReading Find(IReadOnlyList<StrayJob>? jobs = null, IReadOnlyList<StraySession>? sessions = null, StrayHistory? history = null, bool windows = true) =>
        Finder(windows).Find(jobs ?? [], sessions ?? [], history ?? StrayHistory.Empty);

    [Fact]
    public void A_jobs_leftover_child_after_the_job_ended_is_a_stray()
    {
        // The wrapper ended at 08:30; dotnet (seen while it lived) is still here, re-parented to init, at 09:00.
        var job = new StrayJob("j42", 100, Began, TimeSpan.FromMinutes(30), Scratch, EndedAt: Began.AddMinutes(30), Known: [new KnownProcess(102, Began.AddSeconds(2))]);
        _table.Add(102, 1, "dotnet.exe", Began.AddSeconds(2), TimeSpan.FromMinutes(7), "dotnet test X.csproj", Scratch);

        var stray = Find([job]).Strays.ShouldHaveSingleItem();

        stray.Pid.ShouldBe(102);
        stray.Name.ShouldBe("dotnet.exe");
        stray.CommandLine.ShouldBe("dotnet test X.csproj");
        stray.Age.ShouldBe(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(2));
        stray.Cpu.ShouldBe(TimeSpan.FromMinutes(7));
        stray.Owner.ShouldBe("job j42");
        stray.Reason.ShouldContain("after job j42 ended");
        stray.KillCommand.ShouldBe("Stop-Process -Id 102 -Force");
        stray.NodeLaunched.ShouldBeTrue();
    }

    [Fact]
    public void A_protected_process_known_with_no_start_is_never_a_jobs_leftover()
    {
        // wininit and WmiPrvSE say no start time; recorded as a job's (through a reused parent id) with none, any process with the pid
        // matched, and SE1 listed 151 "strays" still running after job j2356 ended (2026-11-09).
        var job = new StrayJob("j2356", 100, Began, null, Scratch, EndedAt: Began.AddMinutes(10), Known: [new KnownProcess(1616, null), new KnownProcess(364, null)]);
        _table.Entries.Add(new ProcessEntry(1616, 1, "wininit.exe"));
        _table.Entries.Add(new ProcessEntry(364, 1, "WmiPrvSE.exe"));

        Find([job]).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void A_job_that_ended_a_moment_ago_still_has_its_grace()
    {
        var job = new StrayJob("j42", 100, Began, null, Scratch, EndedAt: _clock.Now - TimeSpan.FromSeconds(30), Known: [new KnownProcess(102, Began.AddSeconds(2))]);
        _table.Add(102, 1, "dotnet.exe", Began.AddSeconds(2));

        Find([job]).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void A_sessions_mcp_server_that_outlived_the_session_is_a_stray()
    {
        // The session's claude (pid 300) ended and is gone; its MCP server (301) still names it as its parent, which the table no longer holds.
        var session = new StraySession("se1-pil909-stray-finder", NodeLaunched: true, Pid: 300, Start: Began, Folder: Plane, EndedAt: Began.AddMinutes(20));
        _table.Add(301, 300, "node.exe", Began.AddSeconds(5), TimeSpan.FromSeconds(3), "node mcp-server.js");

        var stray = Find(sessions: [session], windows: false).Strays.ShouldHaveSingleItem();

        stray.Pid.ShouldBe(301);
        stray.Owner.ShouldBe("session se1-pil909-stray-finder");
        stray.Reason.ShouldContain("its parent was the owner's process");
        stray.KillCommand.ShouldBe("kill -9 301");
    }

    [Fact]
    public void An_orphan_is_attributed_to_a_plane_by_its_working_folder()
    {
        var session = new StraySession("se1-pil909-stray-finder", true, null, null, Plane, EndedAt: Began.AddMinutes(20));
        _table.Add(400, 1, "pwsh.exe", Began.AddMinutes(5), commandLine: "pwsh -NoProfile", folder: Plane + @"\src");

        var stray = Find(sessions: [session]).Strays.ShouldHaveSingleItem();

        stray.Pid.ShouldBe(400);
        stray.Reason.ShouldContain($"it works in {Plane}");
    }

    [Fact]
    public void An_orphan_is_attributed_to_a_job_by_its_command_line()
    {
        var job = new StrayJob("j42", 100, Began, null, Scratch, EndedAt: Began.AddMinutes(10));
        _table.Add(410, 1, "testhost.exe", Began.AddMinutes(1), commandLine: $"testhost.exe --diag {Scratch}\\log.txt", folder: @"C:\Windows");

        var stray = Find([job]).Strays.ShouldHaveSingleItem();

        stray.Pid.ShouldBe(410);
        stray.Reason.ShouldContain($"its command line names {Scratch}");
    }

    [Fact]
    public void A_folder_that_only_starts_like_the_plane_is_not_the_plane()
    {
        var session = new StraySession("se1-pil909-stray-finder", true, null, null, Plane, EndedAt: Began.AddMinutes(20));
        _table.Add(420, 1, "pwsh.exe", Began.AddMinutes(5), folder: Plane + "2");

        Find(sessions: [session]).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void An_orphan_with_a_live_owner_is_left_alone()
    {
        var session = new StraySession("se1-pil909-stray-finder", true, 300, Began, Plane);
        _table.Add(300, 900, "claude.exe", Began, folder: Plane);
        _table.Add(400, 1, "pwsh.exe", Began.AddMinutes(5), folder: Plane);

        Find(sessions: [session]).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void A_long_cpu_process_under_an_idle_session_is_a_stray()
    {
        var session = new StraySession("se1-pil909-stray-finder", true, 300, Began, Plane);
        _table.Add(300, 900, "claude.exe", Began);
        _table.Add(301, 300, "dotnet.exe", Began.AddMinutes(1), TimeSpan.FromMinutes(40), "dotnet build", Plane);
        // Five minutes ago it had used 37 min of CPU, now 40: 3 min of CPU in 5 min is 0.6 cores, over the 0.5 an idle session's child may use.
        var history = new StrayHistory(
            new Dictionary<int, CpuSample> { [301] = new CpuSample(Began.AddMinutes(1), TimeSpan.FromMinutes(37), _clock.Now - TimeSpan.FromMinutes(5)) },
            new Dictionary<string, DateTimeOffset> { ["session se1-pil909-stray-finder"] = _clock.Now - TimeSpan.FromMinutes(25) });

        var stray = Find(sessions: [session], history: history).Strays.ShouldHaveSingleItem();

        stray.Pid.ShouldBe(301);
        stray.Owner.ShouldBe("session se1-pil909-stray-finder");
        stray.Reason.ShouldContain("idle for 25 min");
    }

    [Fact]
    public void The_same_cpu_under_a_busy_session_is_not_a_stray()
    {
        var session = new StraySession("se1-pil909-stray-finder", true, 300, Began, Plane);
        _table.Add(300, 900, "claude.exe", Began);
        _table.Add(301, 300, "dotnet.exe", Began.AddMinutes(1), TimeSpan.FromMinutes(40));
        var history = new StrayHistory(
            new Dictionary<int, CpuSample> { [301] = new CpuSample(Began.AddMinutes(1), TimeSpan.FromMinutes(37), _clock.Now - TimeSpan.FromMinutes(5)) },
            new Dictionary<string, DateTimeOffset> { ["session se1-pil909-stray-finder"] = _clock.Now - TimeSpan.FromSeconds(20) });

        Find(sessions: [session], history: history).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void An_interactive_sessions_processes_are_never_flagged()
    {
        // A person's session (not node-launched) whose child spins and whose orphan sits in its folder; the node's own plane is inside it.
        var mine = new StraySession("my-session", NodeLaunched: false, Pid: 500, Start: Began, Folder: @"C:\repos\pilot");
        var node = new StraySession("se1-pil909-stray-finder", true, null, null, Plane, EndedAt: Began.AddMinutes(20));
        _table.Add(500, 900, "claude.exe", Began);
        _table.Add(501, 500, "dotnet.exe", Began.AddMinutes(1), TimeSpan.FromHours(3), "dotnet build", @"C:\repos\pilot");
        _table.Add(502, 1, "pwsh.exe", Began.AddMinutes(2), folder: @"C:\repos\pilot\src");
        var history = new StrayHistory(
            new Dictionary<int, CpuSample> { [501] = new CpuSample(Began.AddMinutes(1), TimeSpan.FromHours(2), _clock.Now - TimeSpan.FromMinutes(5)) },
            new Dictionary<string, DateTimeOffset> { ["session my-session"] = _clock.Now - TimeSpan.FromHours(2) });

        Find(sessions: [mine, node], history: history).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void A_process_over_its_jobs_time_limit_is_a_stray()
    {
        // The job started an hour ago with a 30 minute limit and is still running with its child.
        var job = new StrayJob("j42", 100, Began, TimeSpan.FromMinutes(30), Scratch);
        _table.Add(100, 900, "pwsh.exe", Began);
        _table.Add(101, 100, "dotnet.exe", Began.AddSeconds(3), TimeSpan.FromMinutes(50), "dotnet test", Scratch);

        var strays = Find([job]).Strays;

        strays.Select(s => s.Pid).ShouldBe([100, 101]);
        strays[1].Reason.ShouldBe("job j42 has run 1 h 0 min, over its time limit of 30 min");
        strays[1].Owner.ShouldBe("job j42");
    }

    [Fact]
    public void A_job_inside_its_time_limit_is_not_a_stray()
    {
        var job = new StrayJob("j42", 100, Began, TimeSpan.FromHours(2), Scratch);
        _table.Add(100, 900, "pwsh.exe", Began);
        _table.Add(101, 100, "dotnet.exe", Began.AddSeconds(3));

        Find([job]).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void A_reused_pid_is_not_mistaken_for_the_old_process()
    {
        // 102 was a job's child that ended long ago; the pid now belongs to an unrelated process that started 40 minutes in.
        var job = new StrayJob("j42", 100, Began, null, Scratch, EndedAt: Began.AddMinutes(10), Known: [new KnownProcess(102, Began.AddSeconds(2))]);
        _table.Add(102, 900, "chrome.exe", Began.AddMinutes(40), folder: @"C:\Users\me");

        Find([job]).Strays.ShouldBeEmpty();
    }

    [Fact]
    public void The_reading_carries_the_cpu_of_every_process_looked_at_for_the_next_sweep()
    {
        var session = new StraySession("se1-pil909-stray-finder", true, 300, Began, Plane);
        _table.Add(300, 900, "claude.exe", Began, TimeSpan.FromSeconds(30));
        _table.Add(301, 300, "node.exe", Began.AddMinutes(1), TimeSpan.FromSeconds(5));

        var reading = Find(sessions: [session]);

        reading.Cpu[301].ShouldBe(new CpuSample(Began.AddMinutes(1), TimeSpan.FromSeconds(5), _clock.Now));
        reading.Cpu.ShouldContainKey(300);
    }

    [Fact]
    public void The_kill_command_matches_the_os()
    {
        Finder(windows: true).KillCommandFor(7).ShouldBe("Stop-Process -Id 7 -Force");
        Finder(windows: false).KillCommandFor(7).ShouldBe("kill -9 7");
    }
}
