using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What a machine sample costs (#794): every process's CPU time, memory and parent in ONE read of the OS's table, a handle
/// opened only on the few the row names, a sample every 15 s while no client shows the machine and every 5 s while one
/// does, and the GPU's counters read at most every 30 s.
/// </summary>
public sealed class MachineSamplerCostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A table of many processes that counts how often it is read whole.</summary>
    private sealed class CountingTable(int count) : ISystemProcesses
    {
        private int _reads;
        private long _tick;

        public int Reads => Volatile.Read(ref _reads);

        public bool Read(List<SystemProcess> into)
        {
            Interlocked.Increment(ref _reads);
            var tick = Interlocked.Increment(ref _tick);
            into.Clear();
            into.Add(new SystemProcess(0, 0, "Idle", TimeSpan.FromHours(tick), 0));
            for (var pid = 1; pid <= count; pid++)
            {
                // The higher the pid, the busier: each read adds pid milliseconds of CPU.
                into.Add(new SystemProcess(pid, pid == 1 ? 0 : 1, $"p{pid}.exe", TimeSpan.FromMilliseconds(pid * tick), pid * 1024L));
            }
            return true;
        }
    }

    /// <summary>What the sampler logged: a sample that failed says so here.</summary>
    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<MachineSampler>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue($"{formatter(state, exception)} {exception}");
    }

    private static MachineSampler NewSampler(ISystemProcesses table, TimeProvider? time = null, PageViews? views = null,
        Func<int, (string?, string?)>? details = null, Func<MachineGpu?>? gpu = null, Func<IReadOnlyList<(string, int)>>? owners = null) =>
        new(new NodeState(new InMemoryJournal(), new ManualTime(Now)), time ?? new ManualTime(Now))
        {
            Processes = table,
            Views = views,
            Details = details ?? (_ => (null, null)),
            Gpu = gpu ?? (() => null),
            Owners = owners ?? (() => []),
        };

    [Fact]
    public void OneSampleReadsEveryProcessesCpuAndMemoryInOneReadAndOpensOnlyTheTopFew()
    {
        var table = new CountingTable(1000);
        var opened = new List<int>();
        using var sampler = NewSampler(table, details: pid =>
        {
            opened.Add(pid);
            return ($"cmd {pid}", null);
        });

        sampler.Baseline();
        table.Reads.ShouldBe(1);
        var row = sampler.Sample().ShouldNotBeNull();

        // One read of the whole table for the sample, a thousand processes in it, and a handle only on the five named.
        table.Reads.ShouldBe(2);
        opened.Count.ShouldBe(MachineSampler.TopCount);
        row.Processes.Select(p => p.Pid).ShouldBe([1000, 999, 998, 997, 996]);
        row.Processes.ShouldAllBe(p => p.Name == $"p{p.Pid}" && p.CommandLine == $"cmd {p.Pid}" && p.MemoryBytes == p.Pid * 1024L);
        opened.ShouldBe(row.Processes.Select(p => p.Pid));

        // A second sample is one more read, never one per process.
        sampler.Sample();
        table.Reads.ShouldBe(3);
    }

    [Fact]
    public void WhoseAProcessIsComesFromTheParentsOfTheSameRead()
    {
        // Every process's parent is 1, so a root at 1 owns them all: no second walk of the table is needed to say so.
        var table = new CountingTable(50);
        using var sampler = NewSampler(table, owners: () => [("job-7", 1)]);

        sampler.Baseline();
        var row = sampler.Sample().ShouldNotBeNull();

        row.Processes.ShouldAllBe(p => p.Owner == "job-7");
        row.OwnedCpuPercent.ShouldNotBeNull().ShouldBeGreaterThan(0);
        table.Reads.ShouldBe(2);
    }

    [Fact]
    public void TheIntervalIsFifteenSecondsWithNoWatcherAndFiveWhileAClientShowsHome()
    {
        var views = new PageViews();
        using var sampler = NewSampler(new CountingTable(1), views: views);
        var gui = new object();
        var tui = new object();

        sampler.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(15));
        sampler.Watched.ShouldBeFalse();

        // Another page shows no machine.
        views.Say(gui, ClientPages.PullRequests);
        sampler.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(15));

        views.Say(gui, ClientPages.Home);
        sampler.Watched.ShouldBeTrue();
        sampler.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(5));
        views.Say(tui, ClientPages.Home);
        views.Forget(gui);
        sampler.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(5));

        views.Forget(tui);
        sampler.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(15));

        // A sampler told of no pages is never watched.
        using var alone = NewSampler(new CountingTable(1));
        alone.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void TheGpuIsReadAtMostEveryThirtySeconds()
    {
        var time = new ManualTime(Now);
        var reads = 0;
        using var sampler = NewSampler(new CountingTable(3), time, gpu: () => new MachineGpu { Percent = ++reads });

        sampler.Baseline();
        reads.ShouldBe(0);
        sampler.Sample().ShouldNotBeNull().Gpu.ShouldNotBeNull().Percent.ShouldBe(1);
        reads.ShouldBe(1);

        // Two samples at the watched pace, and one at the idle pace: the last reading is held.
        time.Advance(TimeSpan.FromSeconds(5));
        sampler.Sample().ShouldNotBeNull().Gpu.ShouldNotBeNull().Percent.ShouldBe(1);
        time.Advance(TimeSpan.FromSeconds(15));
        sampler.Sample().ShouldNotBeNull().Gpu.ShouldNotBeNull().Percent.ShouldBe(1);
        reads.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(10));
        sampler.Sample().ShouldNotBeNull().Gpu.ShouldNotBeNull().Percent.ShouldBe(2);
        reads.ShouldBe(2);
    }

    [Fact]
    public async Task AClientOpeningHomeWakesTheIdleWait()
    {
        var views = new PageViews();
        var table = new CountingTable(3);
        var logged = new ListLogger();
        using var sampler = new MachineSampler(new NodeState(new InMemoryJournal(), TimeProvider.System), TimeProvider.System)
        {
            Processes = table,
            Views = views,
            Details = _ => (null, null),
            Gpu = () => null,
            FirstAfter = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(20),
            IdleInterval = TimeSpan.FromHours(1),
            Logger = logged,
        };
        await sampler.StartAsync(Ct);
        try
        {
            // The baseline and the first sample, then the idle wait of an hour.
            (await WaitFor(() => table.Reads >= 2)).ShouldBeTrue(string.Join('\n', logged.Lines));
            await Task.Delay(100, Ct);
            var idle = table.Reads;
            idle.ShouldBe(2);

            views.Say(new object(), ClientPages.Home);

            (await WaitFor(() => table.Reads >= idle + 2)).ShouldBeTrue($"reads {table.Reads}: {string.Join('\n', logged.Lines)}");
        }
        finally
        {
            await sampler.StopAsync(Ct);
        }
        logged.Lines.ShouldBeEmpty();

        static async Task<bool> WaitFor(Func<bool> condition)
        {
            for (var waited = 0; waited < 4000; waited += 10)
            {
                if (condition())
                {
                    return true;
                }
                await Task.Delay(10, Ct);
            }
            return condition();
        }
    }

    // ---- The real table ----

    [Fact]
    public void TheRealTableReadsThisProcessWithItsCpuMemoryAndParentInOneSystemCall()
    {
        using var table = new SystemProcesses();
        var read = new List<SystemProcess>();

        // The first read may grow the buffer once; after it, a read is one call.
        table.Read(read).ShouldBeTrue();
        var before = table.Calls;
        table.Read(read).ShouldBeTrue();
        if (OperatingSystem.IsWindows())
        {
            (table.Calls - before).ShouldBe(1);
        }

        read.Count.ShouldBeGreaterThan(1);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var me = read.Single(p => p.Pid == Environment.ProcessId);
        me.Cpu.ShouldBeGreaterThan(TimeSpan.Zero);
        me.Cpu.ShouldBeLessThanOrEqualTo(self.TotalProcessorTime + TimeSpan.FromSeconds(1));
        me.Memory.ShouldBeGreaterThan(0);
        read.ShouldContain(p => p.Pid == me.ParentPid);
        if (OperatingSystem.IsWindows())
        {
            // Linux's comm is cut at 15 characters, where .NET may read the program's file instead.
            MachineSampler.NameOf(me.Image).ShouldBe(self.ProcessName);
        }
    }

    [Fact]
    public void TheProcessTableReadsOneProcesssMemoryAndTheWholeTable()
    {
        OsProcessTable.Instance.MemoryOf(Environment.ProcessId).ShouldNotBeNull().ShouldBeGreaterThan(0);
        OsProcessTable.Instance.MemoryOf(int.MaxValue).ShouldBeNull();
        OsProcessTable.Instance.Snapshot().ShouldContain(p => p.Pid == Environment.ProcessId);
    }

    [Fact]
    public void AProcStatLineGivesTheParentCpuAndMemory()
    {
        // utime 250 and stime 50 ticks at 100 a second; rss 300 pages. The comm holds a space and a parenthesis.
        const string stat = "4242 (my (odd) proc) S 17 4242 4242 0 -1 4194560 100 0 0 0 250 50 0 0 20 0 1 0 1000 10000000 300 18446744073709551615";
        var p = SystemProcesses.ParseStat(4242, stat, 4096).ShouldNotBeNull();

        p.ParentPid.ShouldBe(17);
        p.Image.ShouldBe("my (odd) proc");
        p.Cpu.ShouldBe(TimeSpan.FromSeconds(3));
        p.Memory.ShouldBe(300 * 4096L);
        SystemProcesses.ParseStat(1, "1 (x) S 0", 4096).ShouldBeNull();
    }
}
