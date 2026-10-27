using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The machine's sample: CPU and process shares from fixed tick figures, the GPU out of the counters and nvidia-smi, whose processes are the node's, and the row on the stream.</summary>
public sealed class MachineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    // ---- CPU ----

    [Fact]
    public void MachineCpuIsTheNonIdleShareOfTheTicksBetweenTwoReadings()
    {
        MachineSampler.CpuPercent(idleDelta: 75, totalDelta: 100).ShouldBe(25);
        MachineSampler.CpuPercent(idleDelta: 0, totalDelta: 400).ShouldBe(100);
        MachineSampler.CpuPercent(idleDelta: 400, totalDelta: 400).ShouldBe(0);
        // No time passed, or the counter went back: nothing to say rather than a division by zero or a negative share.
        MachineSampler.CpuPercent(0, 0).ShouldBe(0);
        MachineSampler.CpuPercent(0, -5).ShouldBe(0);
    }

    [Fact]
    public void MachineProcStatLinesGiveIdleAndTotalWithIoWaitCountedAsIdle()
    {
        var first = MachineSampler.ParseProcStat("cpu  100 0 50 800 50 0 0 0 0 0").ShouldNotBeNull();
        var second = MachineSampler.ParseProcStat("cpu  160 0 70 860 60 0 10 0 0 0").ShouldNotBeNull();

        first.ShouldBe((850L, 1000L));
        second.ShouldBe((920L, 1160L));
        // 160 ticks passed, 70 of them idle.
        MachineSampler.CpuPercent(second.Idle - first.Idle, second.Total - first.Total).ShouldBe(56.25);

        // A per-core line, a missing one, and a line of another shape are not the aggregate.
        MachineSampler.ParseProcStat("cpu0 1 2 3 4").ShouldBeNull();
        MachineSampler.ParseProcStat(null).ShouldBeNull();
        MachineSampler.ParseProcStat("cpu  1 x 3 4").ShouldBeNull();
    }

    [Fact]
    public void MachineMeminfoGivesTotalAndAvailableInBytes()
    {
        MachineMemory.ParseMeminfo(["MemTotal:       32768000 kB", "MemFree:         1000 kB", "MemAvailable:   16384000 kB", "Buffers: 5 kB"])
            .ShouldBe((32768000L * 1024, 16384000L * 1024));
        MachineMemory.ParseMeminfo(["MemTotal:       32768000 kB"]).ShouldBeNull();
    }

    // ---- Processes ----

    [Fact]
    public void MachineAProcessIsTheShareOfAllCoresItsCpuTimeGrewBy()
    {
        // 2 s of CPU in 5 s on 4 cores: 2 of the 20 core-seconds on offer.
        MachineSampler.ProcessPercent(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), 4).ShouldBe(10);
        // One core's worth, flat out, on a 4-core machine is a quarter of it.
        MachineSampler.ProcessPercent(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), 4).ShouldBe(25);
        // Not seen before and young enough to have started in the interval: what it used counts.
        MachineSampler.ProcessPercent(TimeSpan.FromSeconds(1), null, TimeSpan.FromSeconds(5), 4).ShouldBe(5);
        // Not seen before and having used hours: it was only unreadable until now, so its time is not this interval's.
        MachineSampler.ProcessPercent(TimeSpan.FromHours(3), null, TimeSpan.FromSeconds(5), 4).ShouldBe(0);
        // Its time went back: the pid is another process's now.
        MachineSampler.ProcessPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), 4).ShouldBe(0);
        MachineSampler.ProcessPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.Zero, 4).ShouldBe(0);
    }

    [Fact]
    public void MachineTheTopProcessesAreTheBusiestFirstAndTheBiggerOnATie()
    {
        var processes = new List<MachineSampler.ProcessSample>
        {
            new(1, "idle-big", TimeSpan.FromSeconds(100), 900),
            new(2, "busy", TimeSpan.FromSeconds(110), 100),
            new(3, "busiest", TimeSpan.FromSeconds(120), 100),
            new(4, "idle-small", TimeSpan.FromSeconds(100), 10),
            new(5, "new", TimeSpan.FromSeconds(1), 50),
        };
        var previous = new Dictionary<int, TimeSpan>
        {
            [1] = TimeSpan.FromSeconds(100), [2] = TimeSpan.FromSeconds(100), [3] = TimeSpan.FromSeconds(100), [4] = TimeSpan.FromSeconds(100),
        };

        var top = MachineSampler.Top(processes, previous, TimeSpan.FromSeconds(20), cores: 1, count: 3);

        top.Select(p => p.Name).ShouldBe(["busiest", "busy", "new"]);
        top.Select(p => p.CpuPercent).ShouldBe([100, 50, 5]);
        top[0].ShouldBe(new MachineProcess { Pid = 3, Name = "busiest", CpuPercent = 100, MemoryBytes = 100 });

        // Fewer processes than the top asks for: only those there are, and a tie goes to the bigger working set.
        var few = MachineSampler.Top(processes[..2], previous, TimeSpan.FromSeconds(20), cores: 1, count: 5);
        few.Select(p => p.Pid).ShouldBe([2, 1]);
        MachineSampler.Top(processes.Where(p => p.Pid is 1 or 4).ToList(), previous, TimeSpan.FromSeconds(20), 1, 5).Select(p => p.Pid).ShouldBe([1, 4]);
    }

    // ---- GPU counters ----

    private const string Adapter = "0x00000000_0x0000D1B2";

    private static string Engine(int pid, string type, string luid = Adapter, int eng = 0) => $"pid_{pid}_luid_{luid}_phys_0_eng_{eng}_engtype_{type}";

    private static string Memory(string luid = Adapter) => $"luid_{luid}_phys_0";

    [Fact]
    public void MachineGpuEngineInstancesAreSummedPerAdapterAndTheBusiestEngineTypeIsTheShare()
    {
        var gpu = MachineSampler.ParseGpu(
            [(Engine(1234, "3D"), 20), (Engine(5678, "3D"), 15.5), (Engine(1234, "Copy", eng: 1), 4), (Engine(42, "VideoDecode", eng: 2), 30)],
            [(Memory(), 2L << 30)],
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [Adapter.ToLowerInvariant()] = 8L << 30 });

        // The 3D engines sum to 35.5, more than the decoder's 30 and the copy engine's 4.
        gpu.ShouldBe(new MachineGpu { Percent = 35.5, MemoryUsed = 2L << 30, MemoryTotal = 8L << 30 });
    }

    [Fact]
    public void MachineGpuTheAdapterWithTheMostMemoryIsTheOneReportedAndASumIsCapped()
    {
        const string integrated = "0x00000000_0x00001111";
        var gpu = MachineSampler.ParseGpu(
            [(Engine(1, "3D", integrated), 70), (Engine(2, "3D", integrated), 60), (Engine(3, "3D"), 8)],
            [(Memory(integrated), 1L << 28), (Memory(), 3L << 30), (Memory(), 1L << 30)],
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [integrated] = 1L << 29, [Adapter] = 12L << 30 });

        gpu.ShouldBe(new MachineGpu { Percent = 8, MemoryUsed = 4L << 30, MemoryTotal = 12L << 30 });
        MachineSampler.ParseGpu([(Engine(1, "3D", integrated), 70), (Engine(2, "3D", integrated), 60)], [], new Dictionary<string, long>())!.Percent.ShouldBe(100);
    }

    [Fact]
    public void MachineGpuWithNoTotalOrNoCountersSaysWhatItKnows()
    {
        // The adapter's total is DXGI's to say: without it the used memory still shows.
        MachineSampler.ParseGpu([], [(Memory(), 512L << 20)], new Dictionary<string, long>())
            .ShouldBe(new MachineGpu { Percent = 0, MemoryUsed = 512L << 20, MemoryTotal = null });
        MachineSampler.ParseGpu([], [], new Dictionary<string, long>()).ShouldBeNull();
        // A name that is not an adapter's is no adapter.
        MachineSampler.ParseGpu([("_Total", 50)], [], new Dictionary<string, long>()).ShouldBeNull();
    }

    [Fact]
    public void MachineCounterInstanceNamesGiveTheirAdapterAndEngineType()
    {
        MachineSampler.LuidOf("pid_1234_luid_0x00000000_0x0000D1B2_phys_0_eng_0_engtype_3D").ShouldBe("0x00000000_0x0000d1b2");
        MachineSampler.LuidOf("luid_0x00000000_0x0000D1B2_phys_0").ShouldBe("0x00000000_0x0000d1b2");
        MachineSampler.LuidOf("_Total").ShouldBeNull();
        MachineSampler.EngineTypeOf("pid_1234_luid_0x00000000_0x0000D1B2_phys_0_eng_3_engtype_VideoEncode").ShouldBe("VideoEncode");
        MachineSampler.EngineTypeOf("luid_0x00000000_0x0000D1B2_phys_0").ShouldBe("");
    }

    // ---- nvidia-smi ----

    [Fact]
    public void MachineNvidiaSmiOneGpuIsItsUtilisationAndMemoryInMebibytes()
    {
        MachineSampler.ParseNvidiaSmi("34, 1024, 8192\n").ShouldBe(new MachineGpu { Percent = 34, MemoryUsed = 1024L << 20, MemoryTotal = 8192L << 20 });
    }

    [Fact]
    public void MachineNvidiaSmiTwoGpusReportTheOneWithMostMemory()
    {
        MachineSampler.ParseNvidiaSmi("5, 300, 4096\r\n90, 20000, 24576\r\n").ShouldBe(new MachineGpu { Percent = 90, MemoryUsed = 20000L << 20, MemoryTotal = 24576L << 20 });
    }

    [Fact]
    public void MachineNvidiaSmiNotAvailableFiguresAreNullAndAnEmptyAnswerIsNoGpu()
    {
        MachineSampler.ParseNvidiaSmi("12, [N/A], [N/A]").ShouldBe(new MachineGpu { Percent = 12, MemoryUsed = null, MemoryTotal = null });
        // No utilisation, no reading.
        MachineSampler.ParseNvidiaSmi("[N/A], 100, 200").ShouldBeNull();
        MachineSampler.ParseNvidiaSmi("").ShouldBeNull();
        MachineSampler.ParseNvidiaSmi(null).ShouldBeNull();
        MachineSampler.ParseNvidiaSmi("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.").ShouldBeNull();
    }

    // ---- Owners ----

    [Fact]
    public void MachineOwnProcessesAreMarkedWithTheirSessionJobOrWatchtower()
    {
        // 100 is a session's claude, 200 a job's shell, with the test run (210) its child and the compiler (211) its grandchild; 300 a watchtower; 900 someone else's.
        var parents = new Dictionary<int, int> { [100] = 1, [200] = 1, [210] = 200, [211] = 210, [300] = 1, [900] = 1, [901] = 900 };
        var roots = new Dictionary<int, string> { [100] = "se1-pil232-machine-sampler", [200] = "j7", [300] = MachineSampler.WatchtowerOwner };

        MachineSampler.OwnerOf(100, parents, roots).ShouldBe("se1-pil232-machine-sampler");
        MachineSampler.OwnerOf(210, parents, roots).ShouldBe("j7");
        MachineSampler.OwnerOf(211, parents, roots).ShouldBe("j7");
        MachineSampler.OwnerOf(300, parents, roots).ShouldBe("watchtower");
        MachineSampler.OwnerOf(900, parents, roots).ShouldBeNull();
        MachineSampler.OwnerOf(901, parents, roots).ShouldBeNull();
        MachineSampler.OwnerOf(5555, parents, roots).ShouldBeNull();
    }

    [Fact]
    public void MachineAParentChainThatLoopsMarksNothingAndNeverHangs()
    {
        // Reused ids can point two processes at each other.
        var parents = new Dictionary<int, int> { [10] = 11, [11] = 10, [12] = 12 };

        MachineSampler.OwnerOf(10, parents, new Dictionary<int, string> { [99] = "j1" }).ShouldBeNull();
        MachineSampler.OwnerOf(12, parents, new Dictionary<int, string> { [99] = "j1" }).ShouldBeNull();
    }

    // ---- What changed ----

    private static MachineRow Row(double cpu = 20, long used = 8L << 30, MachineGpu? gpu = null, DateTimeOffset? at = null, params MachineProcess[] processes) => new()
    {
        SampledAt = at ?? Now,
        CpuPercent = cpu,
        Cores = 8,
        MemoryUsed = used,
        MemoryTotal = 32L << 30,
        Gpu = gpu,
        Processes = processes,
    };

    private static MachineProcess Proc(int pid, double cpu, string name = "p", string? owner = null, long memory = 500L << 20) =>
        new() { Pid = pid, Name = name, CpuPercent = cpu, MemoryBytes = memory, Owner = owner };

    [Fact]
    public void MachineJitterIsNotAChangeButARealMoveIs()
    {
        var held = Row(20, 8L << 30, new MachineGpu { Percent = 10, MemoryUsed = 1L << 30, MemoryTotal = 8L << 30 }, Now, Proc(1, 12), Proc(2, 0.3, "idle"));

        // The CPU a point off, a little memory more, the GPU a point off, the idle process gone, the busy one a point off.
        var same = Row(21, (8L << 30) + (50L << 20), new MachineGpu { Percent = 11, MemoryUsed = 1L << 30, MemoryTotal = 8L << 30 }, Now.AddSeconds(5), Proc(1, 13));
        MachineSampler.Differs(held, same).ShouldBeFalse();

        MachineSampler.Differs(held, Row(40, 8L << 30, held.Gpu, Now.AddSeconds(5), Proc(1, 12))).ShouldBeTrue();
        MachineSampler.Differs(held, Row(20, 10L << 30, held.Gpu, Now.AddSeconds(5), Proc(1, 12))).ShouldBeTrue();
        MachineSampler.Differs(held, Row(20, 8L << 30, null, Now.AddSeconds(5), Proc(1, 12))).ShouldBeTrue();
        MachineSampler.Differs(held, Row(20, 8L << 30, held.Gpu! with { Percent = 50 }, Now.AddSeconds(5), Proc(1, 12))).ShouldBeTrue();
        // A busy process came, one went, one took over, or one is now the node's.
        MachineSampler.Differs(held, Row(20, 8L << 30, held.Gpu, Now.AddSeconds(5), Proc(1, 12), Proc(3, 5))).ShouldBeTrue();
        MachineSampler.Differs(held, Row(20, 8L << 30, held.Gpu, Now.AddSeconds(5))).ShouldBeTrue();
        MachineSampler.Differs(held, Row(20, 8L << 30, held.Gpu, Now.AddSeconds(5), Proc(4, 12))).ShouldBeTrue();
        MachineSampler.Differs(held, Row(20, 8L << 30, held.Gpu, Now.AddSeconds(5), Proc(1, 12, owner: "j1"))).ShouldBeTrue();
        // Its working set grew by a gigabyte.
        MachineSampler.Differs(held, Row(20, 8L << 30, held.Gpu, Now.AddSeconds(5), Proc(1, 12, memory: 1500L << 20))).ShouldBeTrue();
    }

    [Fact]
    public void MachineTheSameSampleIsPutAgainOnceItIsAHeartbeatOld()
    {
        var held = Row(20, at: Now);

        MachineSampler.Differs(held, Row(20, at: Now.AddSeconds(59))).ShouldBeFalse();
        MachineSampler.Differs(held, Row(20, at: Now.AddSeconds(60))).ShouldBeTrue();
    }

    // ---- On the node ----

    [Fact]
    public void MachineTheNodePutsAChangedRowAndKeepsAnUnchangedOneOff()
    {
        var time = new ManualTime(Now);
        var state = new NodeState(new InMemoryJournal(), time);
        state.Machine().ShouldBeNull();

        state.PutMachine(Row(20, at: Now));
        var seq = state.Seq;
        state.PutMachine(Row(21, at: Now.AddSeconds(5)));
        state.Seq.ShouldBe(seq);
        state.PutMachine(Row(60, at: Now.AddSeconds(10)));

        state.Seq.ShouldBe(seq + 1);
        state.Machine().ShouldNotBeNull().CpuPercent.ShouldBe(60);
        state.Snapshot().Machine.ShouldBe(state.Machine());
    }

    [Fact]
    public async Task MachineARowArrivesOnTheEventStreamAndInTheSnapshot()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutMachine(Row(35, 12L << 30, new MachineGpu { Percent = 7 }, Now, Proc(4242, 30, "dotnet", "j1")));

        await using var events = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        var mirror = new NodeMirror();
        // A client arriving after the row is told it in the snapshot...
        var snapshot = (await ReadAsync(events)).ShouldBeOfType<SnapshotEvent>();
        mirror.Apply(snapshot);
        snapshot.Machine.ShouldNotBeNull().CpuPercent.ShouldBe(35);
        Json(mirror.Machine).ShouldBe(Json(node.State.Machine()));

        // ...and one already there, in a delta.
        node.State.PutMachine(Row(80, 12L << 30, null, Now.AddSeconds(5), Proc(4242, 75, "dotnet", "j1")));
        var delta = (await ReadAsync(events)).ShouldBeOfType<MachineChangedEvent>();
        mirror.Apply(delta);

        delta.Seq.ShouldBe(snapshot.Seq + 1);
        delta.Machine.Processes.ShouldHaveSingleItem().Owner.ShouldBe("j1");
        Json(mirror.Machine).ShouldBe(Json(node.State.Machine()));
        mirror.Machine!.CpuPercent.ShouldBe(80);
        mirror.Machine.Gpu.ShouldBeNull();
        mirror.Machine.Line().ShouldBe("CPU 80% (8 cores) · MEM 12.0/32.0 GB");
    }

    [Fact]
    public void MachineAnOlderNodesSnapshotWithoutOneReadsAsNull()
    {
        var state = new NodeState(new InMemoryJournal(), new ManualTime(Now));
        state.PutMachine(Row(20, at: Now));
        var json = JsonSerializer.SerializeToNode<NodeEvent>(state.Snapshot(), PilotJsonContext.Default.NodeEvent)!.AsObject();
        json.ContainsKey("machine").ShouldBeTrue();
        json.Remove("machine");

        var snapshot = json.Deserialize(PilotJsonContext.Default.NodeEvent).ShouldBeOfType<SnapshotEvent>();

        snapshot.Machine.ShouldBeNull();
        var mirror = new NodeMirror();
        mirror.Apply(snapshot);
        mirror.Machine.ShouldBeNull();
    }

    [Fact]
    public void MachineTheRowSurvivesTheWire()
    {
        var sent = new MachineChangedEvent
        {
            Seq = 9,
            At = Now,
            Machine = Row(42, 12L << 30, new MachineGpu { Percent = 7, MemoryUsed = 1L << 30 }, Now, Proc(4242, 30, "dotnet", "watchtower")),
        };

        var read = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(sent, PilotJsonContext.Default.NodeEvent), PilotJsonContext.Default.NodeEvent);

        var machine = read.ShouldBeOfType<MachineChangedEvent>();
        machine.Seq.ShouldBe(9);
        Json(machine.Machine).ShouldBe(Json(sent.Machine));
        machine.Machine.Processes.ShouldHaveSingleItem().ShouldBe(sent.Machine.Processes[0]);
        machine.Machine.Gpu.ShouldBe(sent.Machine.Gpu);
    }

    // ---- This machine ----

    [Fact]
    public void MachineTheSamplerReadsThisMachine()
    {
        var sampler = new MachineSampler(new NodeState(new InMemoryJournal(), new ManualTime(Now)), TimeProvider.System);
        sampler.Baseline();
        // Some work to see, and the time for the tick counters to move.
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 300)
        {
        }

        var row = sampler.Sample().ShouldNotBeNull();

        row.Cores.ShouldBeGreaterThan(0);
        row.MemoryTotal.ShouldBeGreaterThan(0);
        row.MemoryUsed.ShouldBeInRange(1, row.MemoryTotal);
        row.CpuPercent.ShouldBeInRange(0, 100);
        row.Processes.Length.ShouldBeInRange(1, MachineSampler.TopCount);
        row.Processes.ShouldAllBe(p => p.CpuPercent >= 0 && p.CpuPercent <= 100 && p.MemoryBytes >= 0 && p.Name.Length > 0);
        row.Processes.Select(p => p.CpuPercent).ShouldBe(row.Processes.Select(p => p.CpuPercent).OrderDescending());
        row.Gpu?.Percent.ShouldBeInRange(0, 100);
        sampler.Dispose();
    }

    private static string Json(MachineRow? row) => JsonSerializer.Serialize(row, PilotJsonContext.Default.MachineRow);

    private static async Task<NodeEvent> ReadAsync(NodeEventStream events)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        return await events.ReadAsync(budget.Token) ?? throw new InvalidOperationException("The node closed the stream");
    }
}
