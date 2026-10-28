using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A job's pid, its live process tree and the stuck fact, read through a fake process table: no process is started or killed.</summary>
public sealed class JobTreeTests
{
    private static readonly DateTimeOffset Began = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A process table a test edits: who exists, whose child each is, when each started.</summary>
    private sealed class FakeTable : IProcessTable
    {
        public List<ProcessEntry> Entries { get; } = [];
        public Dictionary<int, DateTimeOffset> Starts { get; } = [];
        public List<int> Killed { get; } = [];

        public void Add(int pid, int parent, string name, DateTimeOffset? start = null)
        {
            Entries.Add(new ProcessEntry(pid, parent, name));
            Starts[pid] = start ?? Began;
        }

        public void Remove(int pid)
        {
            Entries.RemoveAll(e => e.Pid == pid);
            Starts.Remove(pid);
        }

        /// <summary>The parent of a process that outlived its parent is the init process.</summary>
        public void Orphan(int pid)
        {
            var at = Entries.FindIndex(e => e.Pid == pid);
            Entries[at] = Entries[at] with { ParentPid = 1 };
        }

        public IReadOnlyList<ProcessEntry> Snapshot() => [.. Entries];

        public DateTimeOffset? StartOf(int pid) => Starts.TryGetValue(pid, out var start) ? start : null;

        public void Kill(int pid)
        {
            Killed.Add(pid);
            Remove(pid);
        }
    }

    private const int Wrapper = 100;

    private readonly FakeTable _table = new();
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly JobRunner _jobs;

    public JobTreeTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(root, "n.sock"), Path.Combine(root, "data")), TimeProvider.System) { ProcessTable = _table };
        _table.Add(1, 0, "systemd");
        _table.Add(200, 1, "explorer.exe");
        // pwsh (the wrapper) runs pwsh, which runs dotnet test, which runs testhost.
        _table.Add(Wrapper, 1, "pwsh.exe", Began);
        _table.Add(101, Wrapper, "pwsh.exe", Began.AddSeconds(1));
        _table.Add(102, 101, "dotnet.exe", Began.AddSeconds(2));
        _table.Add(103, 102, "testhost.exe", Began.AddSeconds(3));
    }

    private static JobRow Running(JobState state = JobState.Running) => new()
    {
        Id = "j1", Command = "dotnet test", Cwd = ".", State = state, LogPath = "j1.log", Timeout = TimeSpan.FromHours(1),
        StartedAt = Began, Pid = Wrapper, ProcessStartedAt = Began,
    };

    private JobLog Describe(JobRow job) => _jobs.Describe(job, new JobLog { Lines = ["Restore complete"], Length = 17 });

    [Fact]
    public void ARunningJobsLogCarriesItsPidAndItsChildTreeWithDepth()
    {
        var log = Describe(Running());

        log.Pid.ShouldBe(Wrapper);
        log.Tree.ShouldBe([new JobProcessInfo("pwsh", 100, 0), new JobProcessInfo("pwsh", 101, 1), new JobProcessInfo("dotnet", 102, 2), new JobProcessInfo("testhost", 103, 3)]);
        log.Stuck.ShouldBeNull();
        log.Lines.ShouldBe(["Restore complete"]);
    }

    [Fact]
    public void AProcessUnrelatedToTheWrapperIsNotInTheTree()
    {
        _table.Add(201, 200, "chrome.exe", Began.AddSeconds(5));

        Describe(Running()).Tree!.Select(p => p.Pid).ShouldBe([100, 101, 102, 103]);
    }

    [Fact]
    public void AGoneWrapperWithALiveChildIsStuckAndTheChildIsStillFound()
    {
        // Seen while the wrapper lived: the node's look at the tree.
        Describe(Running());
        // The wrapper exits; the processes it started live on, their parent now the init process.
        _table.Remove(Wrapper);
        _table.Orphan(101);

        var log = Describe(Running());

        log.Pid.ShouldBe(Wrapper);
        log.Tree.ShouldBe([new JobProcessInfo("pwsh", 101, 1), new JobProcessInfo("dotnet", 102, 2), new JobProcessInfo("testhost", 103, 3)]);
        log.Stuck.ShouldBe("process exited, output still held by 101");
    }

    [Fact]
    public void AChildTheNodeNeverSawBelowAGoneWrapperIsNotFound()
    {
        _table.Remove(Wrapper);
        _table.Orphan(101);

        var log = Describe(Running());

        log.Tree.ShouldBeEmpty();
        log.Stuck.ShouldBeNull();
    }

    [Fact]
    public void AnEndedProcessLeavesTheTreeAndTheStuckFactOnceTheLastChildIsGone()
    {
        Describe(Running());
        _table.Remove(Wrapper);
        _table.Orphan(101);
        Describe(Running()).Stuck.ShouldNotBeNull();

        _table.Remove(103);
        Describe(Running()).Tree!.Select(p => p.Pid).ShouldBe([101, 102]);
        _table.Remove(102);
        _table.Remove(101);

        var log = Describe(Running());

        log.Tree.ShouldBeEmpty();
        log.Stuck.ShouldBeNull();
    }

    [Fact]
    public void ANewChildOfAChildThatOutlivedTheWrapperIsFound()
    {
        Describe(Running());
        _table.Remove(Wrapper);
        _table.Orphan(101);
        _table.Add(104, 103, "git.exe", Began.AddSeconds(10));

        Describe(Running()).Tree!.Select(p => (p.Pid, p.Depth)).ShouldBe([(101, 1), (102, 2), (103, 3), (104, 4)]);
    }

    [Fact]
    public void AReusedPidIsNotTheJobsProcess()
    {
        Describe(Running());
        // The wrapper's pid and its first child's now belong to other processes, started long after.
        _table.Remove(Wrapper);
        _table.Remove(101);
        _table.Add(Wrapper, 1, "notepad.exe", Began.AddHours(5));
        _table.Add(101, 1, "calc.exe", Began.AddHours(5));

        var log = Describe(Running());

        log.Tree!.Select(p => p.Pid).ShouldBe([102, 103]);
        log.Stuck.ShouldBe("process exited, output still held by 102");
    }

    [Fact]
    public void AJobThatIsNotRunningHasItsPidAndNoTree()
    {
        var log = Describe(Running(JobState.Succeeded));

        log.Pid.ShouldBe(Wrapper);
        log.Tree.ShouldBeNull();
        log.Stuck.ShouldBeNull();
    }

    [Fact]
    public void AJobStillWaitingHasNeitherPidNorTree()
    {
        var log = Describe(Running(JobState.Queued) with { Pid = null, ProcessStartedAt = null });

        (log.Pid, log.Tree, log.Stuck).ShouldBe((null, null, null));
    }

    [Fact]
    public void ATableThatCannotBeReadForgetsNothing()
    {
        Describe(Running());
        _table.Remove(Wrapper);
        _table.Orphan(101);
        Describe(Running());
        var all = _table.Entries.ToList();
        _table.Entries.Clear();
        Describe(Running()).Tree.ShouldBeEmpty();
        _table.Entries.AddRange(all);

        Describe(Running()).Tree!.Select(p => p.Pid).ShouldBe([101, 102, 103]);
    }

    [Fact]
    public void TheJobLogCarriesTheTreeOverTheWire()
    {
        var log = new JobLog { Lines = [], Length = 0, Pid = 5, Tree = [new JobProcessInfo("pwsh", 5, 0), new JobProcessInfo("dotnet", 6, 1)], Stuck = "process exited, output still held by 6" };

        var back = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(log, PilotJsonContext.Default.JobLog), PilotJsonContext.Default.JobLog)!;

        (back.Pid, back.Stuck).ShouldBe((5, "process exited, output still held by 6"));
        back.Tree.ShouldBe(log.Tree);
    }
}
