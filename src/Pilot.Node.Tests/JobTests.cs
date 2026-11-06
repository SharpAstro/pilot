using System.Collections.Concurrent;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JobsCollection
{
    public const string Name = "Jobs";
}

/// <summary>Jobs: a long run handed to the node, its owner woken with how it ended, so no worker polls.</summary>
// PIL537 (merge-queue run 41114507855, windows: the 5 s pipe read was cancelled with the far end still open): a Windows child
// started with redirected stdio inherits every inheritable handle of the testhost, so a process another test class started in
// parallel held the handle test's pipe client end, not the job. These tests run apart from every other class.
public abstract class JobTestsBase : IDisposable
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "p2j-" + Guid.NewGuid().ToString("N")[..8]);
    private protected readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private protected readonly ConcurrentQueue<CommandRequest> _said = new();
    private protected readonly JobRunner _jobs;
    private protected string? _refuse;

    // The machine's free memory as the runner reads it; null (unknown) holds nothing back.
    private protected long? _free;

    protected JobTestsBase()
    {
        Directory.CreateDirectory(_root);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            FreeMemory = () => _free,
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                return Task.FromResult(_state.Complete(command, _refuse));
            },
        };
        // The dispatcher is the runner's hosted loop: started here as the node starts it.
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-parser", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
        _jobs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        // StartAsync returns before ExecuteAsync's recovery has run. Until it has, a Queued or Running row a test puts can be
        // failed as orphaned under it: wait, so every row a test puts is the test's alone.
        _jobs.Recovered.WaitAsync(WaitCeiling).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            // Git's object files are read-only, and a test that made a repo here would fail the delete.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // The same line in pwsh and sh: the node runs no cmd. Quoted, since pwsh's echo writes each word on a line of its own.
    private protected static string Echo(string text, int exit = 0) => $"echo '{text}'; exit {exit}";

    // Not a bare sleep, which the node refuses (SleepOnly): the echo is what makes it a job.
    private protected static string Sleep(int seconds) => $"sleep {seconds}; echo slept";

    /// <summary>A file as it is: the job's wrapper (or the child that writes its pid) may still hold it open for writing a moment after it shows, and a plain File.ReadAllText then fails on Windows.</summary>
    private protected static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>PIL784: the trimmed text of <paramref name="path"/> once it exists and is not empty, waited for under <see cref="WaitCeiling"/>; fails naming the file.</summary>
    private protected async Task<string> TextOf(string path)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < WaitCeiling)
        {
            if (File.Exists(path) && ReadLog(path).Trim() is { Length: > 0 } text)
            {
                return text;
            }
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"{path} did not hold any text within {WaitCeiling}");
    }

    /// <summary>The pid a job's child wrote to <paramref name="path"/>.</summary>
    private protected async Task<int> PidIn(string path) =>
        int.Parse(await TextOf(path), System.Globalization.CultureInfo.InvariantCulture);

    private protected CommandResult Run(string command, string target = "session/a1b2c3d4", long timeoutSeconds = 0, string? cwd = null) =>
        _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = target, Args = [cwd ?? _root, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), command], Who = "se1-tia1-parser" });

    /// <summary>The longest a test waits on a condition: a ceiling a busy machine does not reach, not a time the code is held to.</summary>
    private protected static readonly TimeSpan WaitCeiling = TimeSpan.FromMinutes(2);

    /// <summary>The job once <paramref name="condition"/> holds: the dispatcher acts on its own loop, a moment after the call.</summary>
    private protected async Task<JobRow> Until(string id, Func<JobRow, bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < WaitCeiling)
        {
            if (_state.Jobs().Single(j => j.Id == id) is var job && condition(job))
            {
                return job;
            }
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException($"job {id} never came to the state awaited within {WaitCeiling}");
    }

    private protected async Task<JobRow> Ended(string id)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < WaitCeiling)
        {
            if (_state.Jobs().Single(j => j.Id == id) is { EndedAt: not null } job && (job.Owner is null || job.Told || _refuse is not null))
            {
                return job;
            }
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"job {id} did not end within {WaitCeiling}");
    }

    private protected static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private protected void Cancel(string id) =>
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });

    /// <summary>A job that ended by itself some hours ago, which the node measured.</summary>
    private protected void SeedRun(string id, string command, long median, long peak, int ended) =>
        _state.PutJob(new JobRow
        {
            Id = id, Command = command, Cwd = _root, State = JobState.Succeeded, ExitCode = 0, LogPath = Path.Combine(_root, id + ".log"),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddHours(-ended - 1), EndedAt = DateTimeOffset.UtcNow.AddHours(-ended), Told = true,
            MemoryKey = JobKey.Of(command, _root).ToString(), MemoryMedian = median, MemoryPeak = peak,
        });

    /// <summary>A clock the test moves.</summary>
    private protected sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    // The runner is not started: the test is the dispatcher, so nothing else touches its queue, and the clock and the CPU are the test's.
    private protected (JobRunner Jobs, Clock Clock, Dictionary<string, TimeSpan> Cpu, Func<string, Task> Gone) Idle(Func<double?>? machineCpu = null, Func<double?>? outsideCpu = null)
    {
        var clock = new Clock();
        var cpu = new Dictionary<string, TimeSpan>();
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), clock)
        {
            FreeMemory = () => null,
            IdleWindow = TimeSpan.FromMinutes(15),
            IdleCpu = TimeSpan.FromSeconds(5),
            JobCpu = id => cpu.TryGetValue(id, out var used) ? used : null,
            MachineCpu = machineCpu,
            MachineOutsideCpu = outsideCpu,
        };
        return (jobs, clock, cpu, async id =>
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        });
    }

    private protected string Hand(JobRunner jobs, string command)
    {
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [_root, "0", command], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private protected CardRow[] IdleCards(bool openOnly = true) => [.. _state.Cards(openOnly).Where(c => c.Kind == JobRunner.IdleKind)];

    /// <summary>Calls back, as the state publishes it, when a job's Running row is put.</summary>
    private protected sealed class OnJobRunning(Action<string> running) : INodeEventSink
    {
        public int Capacity => 10_000;

        public bool TryWrite(byte[] message)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("job", out var job) && job.GetProperty("state").GetString() == "running")
            {
                running(job.GetProperty("id").GetString()!);
            }
            return true;
        }

        public void Overflowed()
        {
        }
    }

    /// <summary>
    /// A node on the test's data folder: its own journal, state and runner, as a restart builds them. Stopped, it leaves its
    /// jobs running and its journal on disk for the next one, which is how a node's end looks to its successor.
    /// </summary>
    private protected sealed class Node : IDisposable
    {
        private readonly FileJournal _journal;

        private Node(string root, Func<long?> freeMemory, Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime)
        {
            _journal = new FileJournal(Path.Combine(root, "journal"));
            State = new NodeState(_journal, TimeProvider.System);
            Jobs = new JobRunner(State, new NodeOptions(Path.Combine(root, "n.sock"), Path.Combine(root, "data")), TimeProvider.System)
            {
                FreeMemory = freeMemory,
                Lifetime = lifetime,
                Say = (command, _) =>
                {
                    Said.Enqueue(command);
                    return Task.FromResult(State.Complete(command, null));
                },
            };
        }

        public NodeState State { get; }

        public JobRunner Jobs { get; }

        public ConcurrentQueue<CommandRequest> Said { get; } = new();

        public static async Task<Node> StartAsync(string root, Func<long?>? freeMemory = null, Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
        {
            var node = new Node(root, freeMemory ?? (() => null), lifetime);
            if (node.State.Sessions().Length == 0)
            {
                node.State.PutSession(new SessionRow
                {
                    Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-parser", Kind = SessionKind.Background, Cwd = root,
                    Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
                    SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
                });
            }
            await node.Jobs.StartAsync(CancellationToken.None);
            await node.Jobs.Recovered.WaitAsync(WaitCeiling);
            return node;
        }

        public CommandResult Run(string cwd, string command, long timeoutSeconds = 0) =>
            Jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [cwd, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), command], Who = "se1-tia1-parser" });

        public CommandResult Cancel(string id) =>
            Jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });

        public JobRow Job(string id) => State.Jobs().Single(j => j.Id == id);

        public async Task<JobRow> UntilAsync(string id, Func<JobRow, bool> condition, CancellationToken cancellationToken)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Job(id) is var job && condition(job))
                {
                    return job;
                }
                await Task.Delay(50, cancellationToken);
            }
            throw new TimeoutException($"job {id} never came to the state awaited");
        }

        /// <summary>The job once it has ended and its owner has been told.</summary>
        public Task<JobRow> EndedAsync(string id, CancellationToken cancellationToken) => UntilAsync(id, j => j.EndedAt is not null && j.Told, cancellationToken);

        /// <summary>The node going away: its jobs run on and their rows stay as they are.</summary>
        public async Task StopAsync()
        {
            await Jobs.StopAsync(CancellationToken.None);
            _journal.Dispose();
        }

        public void Dispose() => _journal.Dispose();
    }

    private protected sealed class RestartLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }

    // The pid and every descendant of it now, read the way the node reads them.
    private protected static HashSet<int> TreeOf(int root)
    {
        var entries = OsProcessTable.Instance.Snapshot();
        var tree = new HashSet<int> { root };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var e in entries)
            {
                grew |= tree.Contains(e.ParentPid) && tree.Add(e.Pid);
            }
        }
        return tree;
    }

    private protected static CommandRequest Verb(string verb, string id, string who = "controller") => new() { Verb = verb, Target = Verbs.JobTarget(id), Who = who };

    // CPU time per process of the tree, by pid: a sum over the tree went down when a child exited between two samples (its time left
    // the sum), which read as a paused tree using negative CPU on a busy CI runner (#434). CpuSince compares like with like.
    private protected static Dictionary<int, TimeSpan> Cpu(IEnumerable<int> pids)
    {
        var sample = new Dictionary<int, TimeSpan>();
        foreach (var pid in pids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                sample[pid] = p.TotalProcessorTime;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        return sample;
    }

    // The CPU the tree used since <paramref name="before"/>: over the processes alive in both samples only, each at least zero
    // (a pid reused by a new process reads as less, never as negative time).
    private protected static TimeSpan CpuSince(IEnumerable<int> pids, Dictionary<int, TimeSpan> before) =>
        Cpu(pids).Where(now => before.ContainsKey(now.Key)).Aggregate(TimeSpan.Zero, (sum, now) => sum + TimeSpan.FromTicks(Math.Max(0, (now.Value - before[now.Key]).Ticks)));

    // PIL746: waits until <paramref name="condition"/> holds on a fresh sample, under WaitCeiling, and fails with the last samples
    // it saw. The bounds this replaces (under 100 ms of CPU in 1.5 s, over 100 ms in 1 s) measured the machine's load, not the pause.
    private protected async Task WaitForSample(Func<(bool Holds, string Seen)> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var seen = "no sample taken";
        while (clock.Elapsed < WaitCeiling)
        {
            (var holds, seen) = condition();
            if (holds)
            {
                return;
            }
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"{what} did not hold within {WaitCeiling}; last samples: {seen}");
    }

    // A window of <paramref name="window"/> over the same processes: the CPU they used in it and whether the log length moved.
    private protected async Task<(TimeSpan Cpu, long Before, long After)> SampleWindow(IReadOnlyCollection<int> tree, Func<long> length, TimeSpan window)
    {
        var (cpu, before) = (Cpu(tree), length());
        await Task.Delay(window, Ct);
        return (CpuSince(tree, cpu), before, length());
    }

    // Once paused: waits for two consecutive windows with no CPU advance and no log growth (a straggler started between the node's
    // read of the tree and its suspend, PIL537, shows its start-up CPU once and then settles).
    private protected Task WaitSettled(IReadOnlyCollection<int> tree, Func<long> length) =>
        WaitForSample(() =>
        {
            var (cpu, before) = (Cpu(tree), length());
            Thread.Sleep(300);
            var (used, after) = (CpuSince(tree, cpu), length());
            return (used == TimeSpan.Zero && before == after, $"cpu +{used.TotalMilliseconds:0} ms, log {before} -> {after}");
        }, "the paused tree settling (no CPU, no log growth)");

    private protected static bool Alive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private protected async Task<string> Running(string id)
    {
        var job = await Until(id, j => j.State == JobState.Running);
        // Every log opens with the shell header the job's own script writes before the command runs, so a non-empty log no
        // longer means the busy child is alive: wait for a line after the header, which is the child's first output.
        for (var i = 0; i < 300 && !HasOutputBeyondHeader(job.LogPath); i++)
        {
            await Task.Delay(50, Ct);
        }
        return job.LogPath;
    }

    private static bool HasOutputBeyondHeader(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private protected static long LogLength(string path) => new FileInfo(path).Length;
}

/// <summary>Part 1 of JobTests: the class is split so its tests run side by side, not one after another.</summary>
[Collection(JobsCollection.Name)]
public sealed class JobTests1 : JobTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobRunsOnTheNodeAndItsOwnerIsToldHowItEndedWithItsLastLines()
    {
        var started = Run(Echo("all 12 tests passed"));

        started.Ok.ShouldBeTrue();
        var id = started.Made.ShouldNotBeNull();
        var running = await Until(id, j => j.State != JobState.Queued);
        (running.Id, running.Owner, running.OwnerName, running.Flight, running.Leg).ShouldBe((id, "a1b2c3d4", "se1-tia1-parser", "TIA1", "parser"));

        var job = await Ended(id);
        (job.State, job.ExitCode, job.Step, job.Told).ShouldBe((JobState.Succeeded, 0, "all 12 tests passed", true));
        ReadLog(job.LogPath).ShouldContain("all 12 tests passed");
        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/a1b2c3d4", "node"));
        say.Args[0].ShouldContain($"Your job {id}");
        say.Args[0].ShouldContain("succeeded");
        say.Args[0].ShouldContain("all 12 tests passed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobEndsWhenItsProcessExitsThoughAProcessItStartedStillHoldsItsOutput()
    {
        // SE1's j122: the tests passed in 6 minutes, a process they started kept the job's output open, and the job read as
        // running for four and a half hours with six jobs queued behind it.
        // PIL784: the left-behind process sleeps far past any ceiling and its pid is written to a file, so the job ending while it
        // is still alive proves it ended on its own process's exit, not on the output closing; no wall-clock bound is asserted.
        var pidFile = Path.Combine(_root, "left.pid");
        var leftBehind = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 600' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; echo 'started'"
            : $"sleep 600 & echo $! > '{pidFile}'; echo 'started'";
        int? pid = null;
        try
        {
            var id = Run(leftBehind).Made!;
            pid = await PidIn(pidFile);

            var job = await Ended(id);

            job.State.ShouldBe(JobState.Succeeded);
            IsAlive(pid.Value).ShouldBeTrue();
        }
        finally
        {
            if (pid is { } left && IsAlive(left))
            {
                using var process = System.Diagnostics.Process.GetProcessById(left);
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task AJobInheritsOnlyItsLogAndNulNotAnOtherInheritableHandleTheNodeHolds()
    {
        // PIL365: the node holds inheritable handles of its own (a pipe to a child, say). A job that inherited them would keep the
        // far end open for as long as it ran. Here the test's own client end is that handle: once the test closes its copy,
        // the only other holder would be the job.
        using var server = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        var clientClosed = false;
        try
        {
            var id = Run(Sleep(30)).Made.ShouldNotBeNull();
            var running = await Until(id, j => j.State == JobState.Running && j.Pid is not null);

            server.DisposeLocalCopyOfClientHandle();
            clientClosed = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(WaitCeiling);
            var read = await server.ReadAsync(new byte[1], timeout.Token);

            read.ShouldBe(0);
            IsAlive(running.Pid!.Value).ShouldBeTrue();
        }
        finally
        {
            if (!clientClosed)
            {
                server.DisposeLocalCopyOfClientHandle();
            }
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailingJobSaysItsExitCode()
    {
        var job = await Ended(Run(Echo("3 failed", 3)).Made!);

        (job.State, job.ExitCode, job.Error).ShouldBe((JobState.Failed, 3, "exit 3"));
        _said.Single().Args[0].ShouldContain("failed: exit 3");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFinishedJobLeavesItsRealExitCodeInTheExitFileAndItsRowHoldsItsPidAndStartTime()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var ok = await Ended(Run(Echo("fine")).Made!);
        var bad = await Ended(Run(Echo("not fine", 7)).Made!);

        foreach (var (job, code) in new[] { (ok, 0), (bad, 7) })
        {
            job.ExitPath.ShouldBe(job.LogPath + ".exit");
            File.ReadAllText(job.ExitPath!).Trim().ShouldBe(code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Exists(job.ExitPath + ".tmp").ShouldBeFalse();
            job.ExitCode.ShouldBe(code);
            job.Pid.ShouldNotBeNull().ShouldBeGreaterThan(0);
            job.ProcessStartedAt.ShouldNotBeNull().ShouldBeInRange(before, DateTimeOffset.UtcNow.AddSeconds(5));
        }
        // The script a Windows job ran is deleted once the job has ended, not when it started.
        (ok.ScriptPath is null).ShouldBe(!OperatingSystem.IsWindows());
        if (ok.ScriptPath is { } script)
        {
            File.Exists(script).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ARunningJobsRowHoldsItsPidBeforeItEnds()
    {
        var id = Run(Sleep(30)).Made!;

        var running = await Until(id, j => j.State == JobState.Running);

        running.Pid.ShouldNotBeNull();
        running.ProcessStartedAt.ShouldNotBeNull();
        running.ExitPath.ShouldBe(running.LogPath + ".exit");
        File.Exists(running.ExitPath).ShouldBeFalse();
        if (OperatingSystem.IsWindows())
        {
            File.Exists(running.ScriptPath).ShouldBeTrue();
        }
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
        await Ended(id);
    }

    [Fact]
    public async Task BothStdoutAndStderrReachTheLog()
    {
        var command = OperatingSystem.IsWindows()
            ? "'to stdout é'; [Console]::Error.WriteLine('to stderr'); 'and stdout again'"
            : "echo 'to stdout é'; echo 'to stderr' 1>&2; echo 'and stdout again'";

        var job = await Ended(Run(command).Made!);

        job.State.ShouldBe(JobState.Succeeded);
        // The first line of the log names the shell that ran the job (pilot #697); what follows is the command's own output.
        var header = $"shell: {(OperatingSystem.IsWindows() ? "pwsh" : DetachedShell.UnixShell())}\n";
        ReadLog(job.LogPath).ReplaceLineEndings("\n").ShouldBe(header + "to stdout é\nto stderr\nand stdout again\n");
        job.Step.ShouldBe("and stdout again");
    }

    [Fact]
    public async Task ACancelKillsTheJobsChildProcessesToo()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; $p.WaitForExit()"
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command).Made!;
        await Until(id, j => j.State == JobState.Running);
        var childPid = await PidIn(pidFile);
        IsAlive(childPid).ShouldBeTrue();

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        job.State.ShouldBe(JobState.Cancelled);
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
    }

    [Fact]
    public async Task AForcedCancelEndsAJobWhoseChildOutlivedItsWrapperAndKillsTheChild()
    {
        // The watch looks once an hour, so the row still reads running when only the wrapper has been killed: the stuck state.
        _jobs.FollowEvery = TimeSpan.FromHours(1);
        _jobs.TreeEvery = TimeSpan.FromMilliseconds(100);
        var pidFile = Path.Combine(_root, "child.pid");
        // The child runs in the wrapper's own stdout, which is the job's log: it holds the log open.
        var command = OperatingSystem.IsWindows()
            ? $"pwsh -NoProfile -Command \"Set-Content '{pidFile}' `$PID; Start-Sleep 60\""
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command).Made!;
        var running = await Until(id, j => j.State == JobState.Running);
        var childPid = await PidIn(pidFile);
        IsAlive(childPid).ShouldBeTrue();

        // The tree under the wrapper, as the log read gives it, once the child is in it: it is remembered from here on.
        JobLog Log() => _jobs.Describe(_state.Jobs().Single(j => j.Id == id), new JobLog { Lines = [], Length = 0 });
        for (var i = 0; i < 300 && Log().Tree?.Any(p => p.Pid == childPid) != true; i++)
        {
            await Task.Delay(50, Ct);
        }
        var tree = Log();
        tree.Pid.ShouldBe(running.Pid);
        tree.Tree![0].Pid.ShouldBe(running.Pid!.Value);
        tree.Tree[0].Depth.ShouldBe(0);
        tree.Tree.Single(p => p.Pid == childPid).Depth.ShouldBeGreaterThan(0);
        tree.Stuck.ShouldBeNull();

        // Only the wrapper is killed: the child lives on, holding the log, and the row still says running.
        using (var wrapper = System.Diagnostics.Process.GetProcessById(running.Pid.Value))
        {
            wrapper.Kill();
            wrapper.WaitForExit();
        }
        for (var i = 0; i < 300 && Log().Stuck is null; i++)
        {
            await Task.Delay(50, Ct);
        }
        var stuck = Log();
        stuck.Stuck.ShouldNotBeNull().ShouldStartWith("process exited, output still held by ");
        stuck.Tree!.ShouldContain(p => p.Pid == childPid);
        stuck.Tree!.ShouldNotContain(p => p.Pid == running.Pid);
        _state.Jobs().Single(j => j.Id == id).State.ShouldBe(JobState.Running);
        IsAlive(childPid).ShouldBeTrue();

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Args = [Verbs.ForceArg], Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled (forced) by controller"));
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
        // Its owner was told once, and the tree is not asked again once it has ended.
        var say = _said.ShouldHaveSingleItem();
        say.Args[0].ShouldContain("cancelled (forced) by controller");
        Log().Tree.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AForcedCancelOfAJobWithItsWrapperAliveKillsTheWholeTreeAtOnce()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"pwsh -NoProfile -Command \"Set-Content '{pidFile}' `$PID; Start-Sleep 60\""
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command).Made!;
        await Until(id, j => j.State == JobState.Running);
        var childPid = await PidIn(pidFile);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Args = [Verbs.ForceArg], Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled (forced) by controller"));
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
        _said.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ACancelTakesNoArgumentButForce()
    {
        var id = Run(Sleep(30)).Made!;
        await Until(id, j => j.State == JobState.Running);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Args = ["now"], Who = "controller" })
            .Error.ShouldBe("cancel-job takes only `force`, not now");
        _state.Jobs().Single(j => j.Id == id).State.ShouldBe(JobState.Running);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        (await Ended(id)).Error.ShouldBe("cancelled by controller");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobPastItsTimeoutIsStoppedWithItsWholeTreeAndSaysSo()
    {
        // A child of its own, which a kill of the wrapper alone leaves running: the timeout ends both.
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; $p.WaitForExit()"
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command, timeoutSeconds: 20).Made!;
        var running = await Until(id, j => j.State == JobState.Running);
        var childPid = await PidIn(pidFile);

        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Failed, "stopped after its timeout of 20s"));
        // No wall-clock bound: the state and error above prove the timeout stopped it (the tree would sleep 60 s), and the loop
        // below waits for the tree to be gone.
        for (var i = 0; i < 100 && (IsAlive(childPid) || IsAlive(running.Pid!.Value)); i++)
        {
            await Task.Delay(50, Ct);
        }
        (IsAlive(running.Pid!.Value), IsAlive(childPid)).ShouldBe((false, false));
    }

    [Fact]
    public async Task APwshCommandJobKeepsTheExitCodeOfItsLastNativeCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // Unwrapped, as a check's line is: read twice, the outer pwsh would expand $LASTEXITCODE to nothing and end 0 (TIA1827).
        var job = await Ended(Run("pwsh -NoProfile -Command \"cmd /c exit 3; exit $LASTEXITCODE\"").Made!);

        (job.State, job.ExitCode, job.Error).ShouldBe((JobState.Failed, 3, "exit 3"));
    }

    [Fact]
    public async Task ACancelledJobStopsAndItsOwnerIsTold()
    {
        var id = Run(Sleep(30)).Made!;
        await Until(id, j => j.State == JobState.Running);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller"));
        _said.Single().Args[0].ShouldContain("cancelled by controller");
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Error.ShouldBe($"job {id} is not running");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondJobWaitsItsTurnAndStartsWhenTheFirstEnds()
    {
        var first = Run(Sleep(2)).Made!;
        var second = Run(Echo("second ran")).Made!;

        var waiting = await Until(second, j => j.Step is not null);
        (waiting.State, waiting.Step).ShouldBe((JobState.Queued, "next in line: another job is running; needs 1.0 GB (a guess, it has not run before)"));
        waiting.QueuedAt.ShouldNotBeNull();

        (await Ended(first)).State.ShouldBe(JobState.Succeeded);
        var ran = await Ended(second);
        (ran.State, ran.Step).ShouldBe((JobState.Succeeded, "second ran"));
        ran.StartedAt.ShouldBeGreaterThanOrEqualTo(ran.QueuedAt!.Value);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AJobWaitsForFreeMemoryAndStartsOnceThereIsEnough()
    {
        // Two may run at once, so it is memory that holds the second back, not the slot.
        _jobs.MaxRunning = 2;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        _free = 512L << 20;

        var id = Run(Echo("ran")).Made!;

        // Nothing is known of it: the guess of 1 GB, with no job-memory floor set.
        (await Until(id, j => j.Step is not null)).Step.ShouldBe("next in line: waiting for 1.0 GB of free memory (a guess, it has not run before; 0.5 GB now)");
        _free = 4L << 30;
        _jobs.Pump();
        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
        Cancel(holder);
    }

    [Fact]
    public async Task TheHeadOfTheQueueWithNothingRunningStartsWhateverTheFreeMemory()
    {
        _free = 0;

        var id = Run(Echo("ran")).Made!;

        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondJobIsHeldForTheMedianOfItsKindsPastRunsAndNotForJobMemory()
    {
        // The node ran this kind of command before: three runs, medians of 3, 5 and 4 GB (the estimate is 4), peaks higher.
        SeedRun("j1", Echo("x"), median: 3L << 30, peak: 6L << 30, ended: 3);
        SeedRun("j2", Echo("x"), median: 5L << 30, peak: 9L << 30, ended: 2);
        SeedRun("j3", Echo("x"), median: 4L << 30, peak: 5L << 30, ended: 1);
        // The floor is below the estimate here, so the estimate is what the job is held for.
        _jobs.MinFreeMemory = 1L << 30;
        _jobs.MaxRunning = 2;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        _free = 3L << 30;

        var id = Run(Echo("x")).Made!;

        (await Until(id, j => j.Step is not null)).Step.ShouldBe("next in line: waiting for 4.0 GB of free memory (median of its last 3 runs, peak 9.0 GB; 3.0 GB now)");
        _free = 4L << 30;
        _jobs.Pump();
        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
        Cancel(holder);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task JobMemoryIsAFloorSoAKindMeasuredAboveItWaitsForAllItNeeds()
    {
        // Review of #357: as a cap, a kind measured at 8 GB started beside another with 2 GB free.
        SeedRun("j1", Echo("x"), median: 8L << 30, peak: 9L << 30, ended: 1);
        _jobs.MinFreeMemory = 2L << 30;
        _jobs.MaxRunning = 2;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        _free = 2L << 30;

        var id = Run(Echo("x")).Made!;

        (await Until(id, j => j.Step is not null)).Step.ShouldBe("next in line: waiting for 8.0 GB of free memory (median of its last run, peak 9.0 GB; 2.0 GB now)");
        _jobs.Pump();
        (await Until(id, j => j.Step is not null)).State.ShouldBe(JobState.Queued);
        _free = 8L << 30;
        _jobs.Pump();
        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
        Cancel(holder);
    }
}

/// <summary>Part 2 of JobTests: the class is split so its tests run side by side, not one after another.</summary>
[Collection(JobsCollection.Name)]
public sealed class JobTests2 : JobTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunsSpikeDoesNotRaiseTheEstimateButARunWhoseMedianIsHigherDoesAtOnce()
    {
        // A median of 1 GB with a peak of 7 GB: the spike is shown, never gated on.
        var calls = 0;
        var spiky = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n3.sock"), Path.Combine(_root, "data3")), TimeProvider.System)
        {
            FreeMemory = () => null,
            TreeEvery = TimeSpan.FromMilliseconds(10),
            FollowEvery = TimeSpan.FromMilliseconds(20),
            // One spike among the samples of a run that otherwise holds 1 GB.
            MemorySample = _ => Interlocked.Increment(ref calls) == 4 ? 7L << 30 : 1L << 30,
            Say = (command, _) => Task.FromResult(_state.Complete(command, null)),
        };
        await spiky.StartAsync(Ct);
        await spiky.Recovered.WaitAsync(WaitCeiling, Ct);
        try
        {
            var made = spiky.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.NodeTarget, Args = [_root, "0", "sleep 1; echo slept"], Who = "t" }).Made!;
            var ran = await Until(made, j => j.EndedAt is not null);

            (ran.MemoryMedian, ran.MemoryPeak).ShouldBe((1L << 30, 7L << 30));
            // The spike did not move the estimate: its median did not, and the estimate is made of medians.
            JobMemory.Estimate(_state.Jobs(), JobKey.Parse(ran.MemoryKey)!).Bytes.ShouldBe(1L << 30);
        }
        finally
        {
            await spiky.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ARunWhoseMedianIsAboveTheEstimateRaisesItAtOnceAndAnOldHighRunStaysInTheMedian()
    {
        var key = JobKey.Of(Echo("x"), _root);
        SeedRun("j1", Echo("x"), median: 1L << 30, peak: 1L << 30, ended: 5);
        SeedRun("j2", Echo("x"), median: 1L << 30, peak: 1L << 30, ended: 4);
        SeedRun("j3", Echo("x"), median: 1L << 30, peak: 1L << 30, ended: 3);
        JobMemory.Estimate(_state.Jobs(), key).Bytes.ShouldBe(1L << 30);

        // One run at 6 GB: the median of five is still 1 GB, but the run's median is above it, so it counts at once.
        SeedRun("j4", Echo("x"), median: 6L << 30, peak: 6L << 30, ended: 2);
        JobMemory.Estimate(_state.Jobs(), key).Bytes.ShouldBe(6L << 30);
    }

    [Fact]
    public async Task AQueuedJobBehindAnotherSaysWhatItNeedsAndTheMemoryFreeNow()
    {
        _free = 1L << 30;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        Run(Echo("b"));
        _free = 0;

        var behind = Run(Echo("c")).Made!;

        (await Until(behind, j => j.Step is not null)).Step.ShouldBe("queued, 1 ahead of it; needs 1.0 GB (a guess, it has not run before), 0.0 GB free now");
        Cancel(holder);
    }

    [Fact]
    public async Task AQueuedJobCancelledNeverRunsAndItsOwnerIsTold()
    {
        _free = 0;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        var id = Run(Echo("never")).Made!;
        await Until(id, j => j.Step is not null);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller before its turn"));
        File.Exists(job.LogPath).ShouldBeFalse();
        _said.Single().Args[0].ShouldContain("cancelled by controller before its turn");
        Cancel(holder);
    }

    [Fact]
    public async Task ACancelThatReachesTheDispatcherBeforeItsJobStillCancelsIt()
    {
        // As when a cancel lands between Run putting the row and handing the job over: Withdrawn before HandedOver. A
        // runner that is not started has no dispatcher loop, so the test drives the signals in that order itself.
        var idle = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n2.sock"), Path.Combine(_root, "data2")), TimeProvider.System)
        {
            FreeMemory = () => null,
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                return Task.FromResult(_state.Complete(command, null));
            },
        };
        var job = new JobRow
        {
            Id = "j90", Command = Echo("never"), Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Queued,
            LogPath = Path.Combine(_root, "j90.log"), Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow,
        };
        _state.PutJob(job);

        idle.Dispatch(new JobRunner.Withdrawn("j90", "controller"));
        idle.Dispatch(new JobRunner.HandedOver(job));

        var ended = await Ended("j90");
        (ended.State, ended.Error).ShouldBe((JobState.Cancelled, "cancelled by controller before its turn"));
        File.Exists(job.LogPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ACancelThatSeesARunningJobFindsItsProcess()
    {
        // The moment the Running row is published, a cancel must find the process: the runner adds it first.
        string? answer = null;
        using var watch = _state.Subscribe(null, null, new OnJobRunning(id =>
            answer ??= _jobs.IsRunning(id) ? "found" : "not running"));

        var id = Run(Sleep(30)).Made!;
        await Until(id, j => j.State == JobState.Running);
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
        await Ended(id);

        answer.ShouldBe("found");
    }

    [Theory]
    [InlineData("python -c 'import os,time\nwhile not os.path.exists(\"/tmp/done\"): time.sleep(5)'")]
    [InlineData("while [ ! -f /tmp/done ]; do sleep 5; done")]
    [InlineData("while (-not (Test-Path 'C:\\x\\done')) { Start-Sleep -Seconds 5 }")]
    [InlineData("sleep 600")]
    public void AJobThatOnlySleepsIsRefusedBeforeItTakesAnId(string command)
    {
        var result = Run(command);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("pilot leg wait --for-file");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARealCommandWithASleepAmongOtherWorkIsNotRefused()
    {
        var started = Run("echo 'tests passed'; sleep 1");

        started.Ok.ShouldBeTrue();
        (await Ended(started.Made!)).State.ShouldBe(JobState.Succeeded);
    }
    [Fact]
    [Trait("Category", "Slow")]
    public async Task TwoChecksAndFourJobsAtMaxJobsFourNeverRunMoreThanFourAtOnceAndAllFinish()
    {
        _jobs.MaxRunning = 4;
        var ids = Enumerable.Range(0, 4).Select(_ => Run(Sleep(1)).Made!).ToList();
        var checks = new[] { "one", "two" }.Select(leg => _jobs.RunCheckAsync("TIA1", leg, Sleep(1), _root, TimeSpan.FromMinutes(30), Ct)).ToList();

        var most = 0;
        for (var i = 0; i < 600 && (checks.Any(c => !c.IsCompleted) || ids.Any(id => _state.Jobs().Single(j => j.Id == id).EndedAt is null)); i++)
        {
            most = Math.Max(most, _state.Jobs().Count(j => j.State == JobState.Running));
            await Task.Delay(20, Ct);
        }

        most.ShouldBeInRange(1, 4);
        (await Task.WhenAll(checks)).ShouldAllBe(c => c.Passed);
        var rows = _state.Jobs().Where(j => j.Kind == JobKind.Check).ToList();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(j => j.State == JobState.Succeeded && j.Owner == null && j.Flight == "TIA1");
        foreach (var id in ids)
        {
            (await Ended(id)).State.ShouldBe(JobState.Succeeded);
        }
        // Only the four jobs have an owner to wake: a check says nothing to a session.
        _said.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ACheckQueuedBehindARunningJobIsNotStoppedForTheTimeItWaitedAndCarriesItsExitAndLastLines()
    {
        var (jobs, clock, _, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        var budget = TimeSpan.FromMinutes(30);

        var check = jobs.RunCheckAsync("TIA1", "parser", "echo 'built'; echo 'all green'; exit 0", _root, budget, Ct);
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check);
        row.State.ShouldBe(JobState.Queued);
        jobs.Dispatch(new JobRunner.HandedOver(row));
        (row.Flight, row.Leg, row.Owner, row.Timeout).ShouldBe(("TIA1", "parser", null, budget));

        // It waits longer than its whole budget, then gets the slot: the budget counts from here.
        clock.Advance(TimeSpan.FromMinutes(40));
        await gone(first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        var run = await check.WaitAsync(WaitCeiling, Ct);
        run.Passed.ShouldBeTrue();
        run.Tail.ShouldContain("all green");
        var ended = _state.Jobs().Single(j => j.Id == row.Id);
        (ended.State, ended.ExitCode, ended.StartedAt).ShouldBe((JobState.Succeeded, 0, clock.GetUtcNow()));
        // A check says nothing to a session. (The fixture's own runner shares the state and may tell the cancelled first job's end
        // on its tick, so only what is said about the check counts.)
        _said.Where(c => c.Args.Any(a => a.Contains(row.Id, StringComparison.Ordinal))).ShouldBeEmpty();
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ACheckIsStoppedOnlyOnceItHasRunForItsBudget()
    {
        var (jobs, clock, _, _) = Idle();
        var check = jobs.RunCheckAsync("TIA1", "parser", Sleep(60), _root, TimeSpan.FromMinutes(30), Ct);
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Kind == JobKind.Check)));

        check.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(31));

        var run = await check.WaitAsync(WaitCeiling, Ct);
        (run.ExitCode, run.Tail).ShouldBe((-1, "the check was stopped at 30 m of running: the budget it was given"));
        await jobs.StopAsync(CancellationToken.None);
    }

    private void SeedPasses(string command, params int[] minutes)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        foreach (var m in minutes)
        {
            at = at.AddHours(1);
            _state.PutJob(new JobRow
            {
                Id = _state.NextJobId(), Command = command, Cwd = _root, Kind = JobKind.Check, State = JobState.Succeeded, LogPath = "x.log",
                Timeout = TimeSpan.FromMinutes(30), StartedAt = at.AddMinutes(-m), EndedAt = at, ExitCode = 0,
                MemoryKey = JobKey.Of(command, _root).ToString(),
            });
        }
    }

    [Fact]
    public async Task EarlierSlowPassesOfTheSameCheckRaiseTheNextOnesTimeoutAndAStopNamesTheLimitAndItsBasis()
    {
        var (jobs, clock, _, _) = Idle();
        var command = Sleep(60);
        SeedPasses(command, 20, 35, 30);

        var check = jobs.RunCheckAsync("TIA1", "parser", command, _root, jobs.CheckLimitFor(command, _root), Ct);
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check && j.State == JobState.Queued);
        row.Timeout.ShouldBe(TimeSpan.FromMinutes(52.5));
        jobs.Dispatch(new JobRunner.HandedOver(row));
        clock.Advance(TimeSpan.FromMinutes(54));

        var run = await check.WaitAsync(WaitCeiling, Ct);
        run.StoppedByBudget.ShouldBeTrue();
        var slowest = _state.Jobs().Single(j => j.Kind == JobKind.Check && j.State == JobState.Succeeded && j.EndedAt - j.StartedAt == TimeSpan.FromMinutes(35));
        run.Tail.ShouldBe($"the check was stopped at 53 m of running: 1.5x the slowest of its last 3 passes, {slowest.Id} ({command}), 35 m");
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ACheckQueuedLongerThanItsLimitIsStillGivenItsWholeLimitOnceItRuns()
    {
        var (jobs, clock, _, gone) = Idle();
        var command = Sleep(60);
        SeedPasses(command, 40);
        var first = Hand(jobs, Sleep(61));

        var check = jobs.RunCheckAsync("TIA1", "parser", command, _root, jobs.CheckLimitFor(command, _root), Ct);
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check && j.State == JobState.Queued);
        jobs.Dispatch(new JobRunner.HandedOver(row));
        clock.Advance(TimeSpan.FromMinutes(70));
        await gone(first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        _state.Jobs().Single(j => j.Id == row.Id).State.ShouldBe(JobState.Running);
        check.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(59));
        check.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(2));
        (await check.WaitAsync(WaitCeiling, Ct)).StoppedByBudget.ShouldBeTrue();
        await jobs.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(97, true)]
    [InlineData(30, false)]
    public async Task ACheckStoppedByItsBudgetSaysWhetherTheMachinesCpuWasAbove90PercentForMostOfItsRun(int machine, bool saturated)
    {
        var (jobs, clock, _, _) = Idle(() => machine);
        var check = jobs.RunCheckAsync("TIA1", "parser", Sleep(60), _root, TimeSpan.FromMinutes(30), Ct);
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Kind == JobKind.Check)));
        clock.Advance(TimeSpan.FromMinutes(31));

        var run = await check.WaitAsync(WaitCeiling, Ct);
        (run.StoppedByBudget, run.StoppedSaturated).ShouldBe((true, saturated));
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACheckThatFailsByItsOwnExitIsNotStoppedByItsBudgetWhateverTheMachineDid()
    {
        var (jobs, _, _, _) = Idle(() => 99);
        var check = jobs.RunCheckAsync("TIA1", "parser", "exit 3", _root, TimeSpan.FromMinutes(30), Ct);
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Kind == JobKind.Check)));

        var run = await check.WaitAsync(WaitCeiling, Ct);
        (run.ExitCode, run.StoppedByBudget, run.StoppedSaturated).ShouldBe((3, false, false));
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellingACheckWithdrawsItWhileItWaits()
    {
        var (jobs, _, _, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        using var cancel = new CancellationTokenSource();
        var check = jobs.RunCheckAsync("TIA1", "parser", Echo("never"), _root, TimeSpan.FromMinutes(30), cancel.Token);
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check);
        jobs.Dispatch(new JobRunner.HandedOver(row));

        cancel.Cancel();
        // The withdraw is a signal on the dispatcher, which here is the test.
        jobs.Dispatch(new JobRunner.Withdrawn(row.Id, "the check's caller"));

        await Should.ThrowAsync<OperationCanceledException>(check.WaitAsync(WaitCeiling, Ct));
        _state.Jobs().Single(j => j.Id == row.Id).State.ShouldBe(JobState.Cancelled);
        await gone(first);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANearIdleJobWithAnotherQueuedBehindItRaisesOneCardNamingJobOwnerAndWaiter()
    {
        var (jobs, clock, cpu, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        var second = Hand(jobs, Echo("later"));
        cpu[first] = TimeSpan.FromSeconds(1);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        IdleCards().ShouldBeEmpty();

        clock.Advance(TimeSpan.FromMinutes(16));
        cpu[first] = TimeSpan.FromSeconds(2);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        var card = IdleCards().ShouldHaveSingleItem();
        (card.Kind, card.Bin, card.Subject).ShouldBe((JobRunner.IdleKind, CardBin.ForYou, Verbs.JobTarget(first)));
        card.Title.ShouldContain(first);
        (card.Detail ?? "").ShouldContain("se1-tia1-parser");
        (card.Detail ?? "").ShouldContain(second);

        await gone(first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        IdleCards().ShouldBeEmpty();
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ANearIdleJobWithNothingQueuedBehindItRaisesNoCard()
    {
        var (jobs, clock, cpu, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        cpu[first] = TimeSpan.Zero;
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        clock.Advance(TimeSpan.FromMinutes(16));
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        IdleCards(openOnly: false).ShouldBeEmpty();
        // The runner is not started, so its stop would wait out the real sleep: end the job first.
        await gone(first);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AJobThatKeepsWorkingRaisesNoCardAndTheCardClosesWhenTheQueueEmpties()
    {
        var (jobs, clock, cpu, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        var second = Hand(jobs, Echo("later"));
        cpu[first] = TimeSpan.Zero;
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        // Busy: 20 s of CPU in each ten minutes, so the window keeps starting again.
        for (var i = 1; i <= 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            cpu[first] = TimeSpan.FromSeconds(20 * i);
            jobs.Dispatch(JobRunner.LookAgain.Instance);
        }
        IdleCards(openOnly: false).ShouldBeEmpty();

        // Idle now: the card comes, and goes when the waiting job is withdrawn.
        clock.Advance(TimeSpan.FromMinutes(16));
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        IdleCards().ShouldHaveSingleItem();
        jobs.Dispatch(new JobRunner.Withdrawn(second, "controller"));
        IdleCards().ShouldBeEmpty();
        // The runner is not started, so its stop would wait out the real sleep: end the job first.
        await gone(first);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AJobWhoseCpuReadingFallsWhenAChildEndsAndRisesAgainRaisesNoCard()
    {
        var (jobs, clock, cpu, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        Hand(jobs, Echo("later"));
        double[] sawtooth = [0.8, 2.0, 3.1, 1.2, 2.2, 3.3, 1.4, 2.5, 3.7, 0.0];
        foreach (var seconds in sawtooth.Concat(sawtooth))
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            cpu[first] = TimeSpan.FromSeconds(seconds);
            jobs.Dispatch(JobRunner.LookAgain.Instance);
        }
        IdleCards(openOnly: false).ShouldBeEmpty();
        // The runner is not started, so its stop would wait out the real sleep: end the job first.
        await gone(first);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void TheCpuTallyNeverFallsWhenAProcessEndsAndKeepsWhatAnEndedOneUsed()
    {
        var tally = new ProcessTreeCpu.Tally();
        var started = new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
        tally.Note(10, started, TimeSpan.FromSeconds(1));
        tally.Note(11, started, TimeSpan.FromSeconds(3));
        tally.Total.ShouldBe(TimeSpan.FromSeconds(4));

        // Pid 11 ended and is not noted again: its 3 s still count, and a new child adds to them.
        tally.Note(12, started.AddSeconds(5), TimeSpan.FromSeconds(2));
        tally.Note(10, started, TimeSpan.FromSeconds(1.5));
        tally.Total.ShouldBe(TimeSpan.FromSeconds(6.5));

        // A reused pid with a later start time is another process.
        tally.Note(11, started.AddSeconds(9), TimeSpan.FromSeconds(0.5));
        tally.Total.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARestartAskedForWhileAJobRunsLeavesItRunningForTheNextNodeToFollow()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, "sleep 2; echo 'ended under the successor'").Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        var lifetime = new RestartLifetime();
        var successors = new List<string>();
        var restarter = new NodeRestarter(lifetime, Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeRestarter>.Instance)
        {
            NodePath = "/opt/pilot/pilot-node",
            RestartedBySystemd = _ => false,
            StartSuccessor = path =>
            {
                successors.Add(path);
                return true;
            },
        };
        var commands = new NodeSettingsCommands(new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), first.State, restarter, null!, TimeProvider.System, first.Jobs, null!);

        var result = commands.RestartNode(new CommandRequest { Verb = Verbs.RestartNode, Target = Verbs.NodeTarget, Who = "se1-tia1-parser" });

        // It does not wait for the job: the successor is started and the node stops at once, saying one job carries on.
        result.Way.ShouldBe("the node restarts for se1-tia1-parser; 1 job carries on under the next node");
        successors.ShouldBe(["/opt/pilot/pilot-node"]);
        lifetime.Stopped.ShouldBeTrue();
        first.Job(id).State.ShouldBe(JobState.Running);

        await first.StopAsync();
        first.Said.ShouldBeEmpty();
        IsAlive(running.Pid!.Value).ShouldBeTrue();
        using var second = await Node.StartAsync(_root);
        second.Job(id).State.ShouldBe(JobState.Running);
        second.Jobs.IsRunning(id).ShouldBeTrue();
        var ended = await second.EndedAsync(id, Ct);
        (ended.State, ended.Step).ShouldBe((JobState.Succeeded, "ended under the successor"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobARestartedNodeFindsAgainEndsUnderItWithItsRealExitCodeAndItsOwnerIsToldOnce()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, "sleep 2; echo 'finished under the next node'; exit 5").Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);

        await first.StopAsync();
        // The node going away is not the job's end: nothing was said, and the process still runs.
        first.Said.ShouldBeEmpty();
        IsAlive(running.Pid!.Value).ShouldBeTrue();

        var second = await Node.StartAsync(_root);
        try
        {
            second.Job(id).State.ShouldBe(JobState.Running);
            second.Jobs.IsRunning(id).ShouldBeTrue();

            var job = await second.EndedAsync(id, Ct);

            (job.State, job.ExitCode, job.Error, job.Step).ShouldBe((JobState.Failed, 5, "exit 5", "finished under the next node"));
            var say = second.Said.ShouldHaveSingleItem();
            say.Args[0].ShouldContain($"Your job {id}");
            say.Args[0].ShouldContain("failed: exit 5");
            say.Args[0].ShouldContain("finished under the next node");
            first.Said.ShouldBeEmpty();

            // A third node finds it ended, and tells nobody again.
            await second.StopAsync();
            using var third = await Node.StartAsync(_root);
            third.Said.ShouldBeEmpty();
            third.Job(id).Told.ShouldBeTrue();
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobThatEndedWhileNoNodeWasUpIsEndedFromItsExitFile()
    {
        var first = await Node.StartAsync(_root);
        // The sleep keeps the job running until the first node is stopped: a command that ends at once can be ended by that node itself before the test sees it running.
        var id = first.Run(_root, "sleep 2; echo 'ended in the dark'; exit 4").Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        await first.StopAsync();
        for (var i = 0; i < 300 && !(File.Exists(running.ExitPath) && !IsAlive(running.Pid!.Value)); i++)
        {
            await Task.Delay(50, Ct);
        }
        File.Exists(running.ExitPath).ShouldBeTrue();

        using var second = await Node.StartAsync(_root);
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.ExitCode, job.Error, job.Step).ShouldBe((JobState.Failed, 4, "exit 4", "ended in the dark"));
        job.EndedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(job.StartedAt);
        second.Said.ShouldHaveSingleItem().Args[0].ShouldContain("failed: exit 4");
        // The script a Windows job ran is deleted, the node that ended it was not the one that started it.
        if (job.ScriptPath is { } script)
        {
            File.Exists(script).ShouldBeFalse();
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobThatEndedWhileNoNodeWasUpWithExitCodeZeroSucceeded()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, "sleep 2; " + Echo("all passed")).Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        await first.StopAsync();
        for (var i = 0; i < 300&& !(File.Exists(running.ExitPath) && !IsAlive(running.Pid!.Value)); i++)
        {
            await Task.Delay(50, Ct);
        }

        using var second = await Node.StartAsync(_root);
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.ExitCode, job.Error, job.Step).ShouldBe((JobState.Succeeded, 0, null, "all passed"));
        second.Said.ShouldHaveSingleItem().Args[0].ShouldContain("succeeded");
    }

    [Fact]
    public async Task AJobWhoseProcessIsGoneWithNoExitFileFailsSayingSoAndIsToldOnce()
    {
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Pid = int.MaxValue - 7, ProcessStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await _jobs.RecoverAsync(Ct);
        // Looked at again (as a second start would): it has ended and been told, so nothing more is said.
        await _jobs.RecoverAsync(Ct);

        var job = _state.Jobs().Single();
        (job.State, job.Error, job.Told).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded", true));
        _said.Single().Args[0].ShouldContain("Your job j1");
    }

    [Fact]
    public async Task AJobWhoseRowSaysNoProcessAtAllFailsAsOneGoneWithoutAnExitFile()
    {
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await _jobs.RecoverAsync(Ct);

        (_state.Jobs().Single().State, _state.Jobs().Single().Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"));
    }
}

/// <summary>Part 3 of JobTests: the class is split so its tests run side by side, not one after another.</summary>
[Collection(JobsCollection.Name)]
public sealed class JobTests3 : JobTestsBase
{
    [Fact]
    public async Task APidAnotherProcessHasTakenIsNotTakenForTheJob()
    {
        // The pid is a live process's (this test's own), but it started long after the job did: it is not the job's.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Pid = self.Id, ProcessStartedAt = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero).AddHours(-3),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
        });

        await _jobs.RecoverAsync(Ct);

        var job = _state.Jobs().Single();
        (job.State, job.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"));
        _jobs.IsRunning("j1").ShouldBeFalse();
        // And it was not killed for it.
        IsAlive(self.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task APidAnotherProcessHasTakenDoesNotHideTheExitFileTheJobLeft()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var log = Path.Combine(_root, "j1.log");
        File.WriteAllText(log + ".exit", "0\n");
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = log, ExitPath = log + ".exit", Pid = self.Id, ProcessStartedAt = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero).AddHours(-3),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
        });

        await _jobs.RecoverAsync(Ct);

        (_state.Jobs().Single().State, _state.Jobs().Single().ExitCode).ShouldBe((JobState.Succeeded, 0));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task QueuedJobsSurviveARestartAndRunInTheOrderTheyCame()
    {
        // A job runs while the three wait behind it: with nothing running the head would start at once, whatever the memory.
        var first = await Node.StartAsync(_root, () => 0);
        var blocker = first.Run(_root, Sleep(30)).Made!;
        await first.UntilAsync(blocker, j => j.State == JobState.Running, Ct);
        var one = first.Run(_root, Echo("one ran")).Made!;
        var two = first.Run(_root, Echo("two ran")).Made!;
        var three = first.Run(_root, Echo("three ran")).Made!;
        await first.UntilAsync(three, j => j.Step is not null, Ct);
        await first.StopAsync();

        using var second = await Node.StartAsync(_root, () => 0);
        // Still waiting, nothing failed, and the node said nothing; the job that ran on is followed again.
        foreach (var id in new[] { one, two, three })
        {
            second.Job(id).State.ShouldBe(JobState.Queued);
        }
        second.Job(blocker).State.ShouldBe(JobState.Running);
        await second.UntilAsync(three, j => j.Step is { } step && step.StartsWith("queued, 2 ahead of it", StringComparison.Ordinal), Ct);
        second.Said.ShouldBeEmpty();
        second.Job(one).Step.ShouldNotBeNull().ShouldStartWith("next in line");

        second.Jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(blocker), Who = "controller" }).Ok.ShouldBeTrue();
        var ended = new[] { await second.EndedAsync(one, Ct), await second.EndedAsync(two, Ct), await second.EndedAsync(three, Ct) };

        ended.Select(j => j.State).ShouldAllBe(state => state == JobState.Succeeded);
        ended.Select(j => j.Step).ShouldBe(["one ran", "two ran", "three ran"]);
        ended[0].StartedAt.ShouldBeLessThanOrEqualTo(ended[1].StartedAt);
        ended[1].StartedAt.ShouldBeLessThanOrEqualTo(ended[2].StartedAt);
        second.Said.Count.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACancelKillsAReattachedJobAndItsChildProcessesToo()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; $p.WaitForExit()"
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, command).Made!;
        await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        var childPid = await PidIn(pidFile);
        await first.StopAsync();
        IsAlive(childPid).ShouldBeTrue();

        using var second = await Node.StartAsync(_root);
        second.Jobs.IsRunning(id).ShouldBeTrue();
        second.Cancel(id).Ok.ShouldBeTrue();
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller"));
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
        second.Said.ShouldHaveSingleItem().Args[0].ShouldContain("cancelled by controller");
    }

    [Fact]
    public async Task AReattachedJobsTimeoutCountsFromItsStartNotFromTheRestart()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, Sleep(60), timeoutSeconds: 3).Made!;
        await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        await first.StopAsync();
        await Task.Delay(2000, Ct);

        var restarted = DateTimeOffset.UtcNow;
        using var second = await Node.StartAsync(_root);
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.Error).ShouldBe((JobState.Failed, "stopped after its timeout of 3s"));
        // Counted from the start: it had about 1s left when the second node came up. A fresh 3s from the restart would end it no
        // earlier than restarted + 3s, so ending before that proves the count. Both ends are the job's own row and the test's
        // restart stamp, not how long the test took to get here.
        job.EndedAt!.Value.ShouldBeLessThan(restarted + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task APausedJobStaysFrozenAndPausedAcrossANodeRestartAndResumesAndCancelsByPid()
    {
        var flag = Path.Combine(_root, "go");
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, Busy(flag)).Made!;
        await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        first.Jobs.Pause(Verb(Verbs.PauseJob, id)).Ok.ShouldBeTrue();
        var pid = first.Job(id).Pid!.Value;
        var tree = TreeOf(pid);
        await first.StopAsync();
        // PIL746: settle, then a quiet window of the same processes must show no CPU, instead of fixed delays and a 100 ms bound.
        await WaitSettled(tree, () => 0);
        var cpu = Cpu(tree);
        (await SampleWindow(tree, () => 0, TimeSpan.FromMilliseconds(500))).Cpu.ShouldBe(TimeSpan.Zero);

        using var second = await Node.StartAsync(_root);
        var found = second.Job(id);
        (found.State, found.PausedAt is not null).ShouldBe((JobState.Running, true));
        second.Jobs.Pause(Verb(Verbs.PauseJob, id)).Error.ShouldBe($"job {id} is already paused");
        second.Jobs.Resume(Verb(Verbs.ResumeJob, id)).Ok.ShouldBeTrue();
        (second.Job(id).PausedAt, second.Job(id).PausedFor > TimeSpan.Zero).ShouldBe((null, true));
        await WaitForSample(() =>
        {
            var used = CpuSince(tree, cpu);
            return (used > TimeSpan.Zero, $"cpu +{used.TotalMilliseconds:0} ms");
        }, "the resumed tree using CPU again");

        second.Jobs.Pause(Verb(Verbs.PauseJob, id)).Ok.ShouldBeTrue();
        second.Cancel(id).Ok.ShouldBeTrue();
        (await second.EndedAsync(id, Ct)).State.ShouldBe(JobState.Cancelled);
        for (var i = 0; i < 100 && tree.Any(IsAlive); i++)
        {
            await Task.Delay(50, Ct);
        }
        tree.Where(IsAlive).ShouldBeEmpty();
    }

    [Fact]
    public async Task StoppingTheNodeLeavesItsRunningJobRunningAndItsRowRunning()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, Sleep(60)).Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);

        await first.StopAsync();

        try
        {
            IsAlive(running.Pid!.Value).ShouldBeTrue();
            File.Exists(running.ExitPath).ShouldBeFalse();
            first.Said.ShouldBeEmpty();
            using var second = await Node.StartAsync(_root);
            second.Job(id).State.ShouldBe(JobState.Running);
            second.Cancel(id).Ok.ShouldBeTrue();
            (await second.EndedAsync(id, Ct)).State.ShouldBe(JobState.Cancelled);
        }
        finally
        {
            JobKiller.Kill(running);
        }
    }

    [Fact]
    public async Task AnOwnerThatCannotBeToldRings()
    {
        _refuse = "the folder is gone";

        var job = await Ended(Run(Echo("done")).Made!);
        // The ring follows the refused tell, a moment after the job ends: waited for, as the end is (flaky under load).
        for (var i = 0; i < 100 && _state.Cards(openOnly: true).Length == 0; i++)
        {
            await Task.Delay(50, Ct);
        }

        job.Told.ShouldBeFalse();
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, JobRunner.UntoldKind, Verbs.JobTarget(job.Id)));
        card.Detail.ShouldNotBeNull().ShouldContain("the folder is gone");
    }

    [Fact]
    public void ARunIsRefusedWithWhy()
    {
        Run(Echo("x"), cwd: Path.Combine(_root, "nope")).Error.ShouldNotBeNull().ShouldStartWith("there is no folder");
        Run(Echo("x"), target: "session/ffffffff").Error.ShouldBe("There is no session ffffffff to wake with the result");
        Run(Echo("x"), timeoutSeconds: 9 * 3600).Error.ShouldBe("a job may run 8 hours at most");
        _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "node", Args = [_root, "0"], Who = "t" }).Error.ShouldNotBeNull().ShouldStartWith("run takes");
    }

    [Fact]
    public async Task AWorkerIdleWhileItsJobRunsIsNotNudged()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var state = new NodeState(new InMemoryJournal(), time);
        state.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = "a1b2c3d4", StartedAt = time.GetUtcNow() });
        state.PutSession(_state.Sessions().Single() with { LastResponseAt = time.GetUtcNow() });
        state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", State = JobState.Running, LogPath = "x", Timeout = JobRunner.DefaultTimeout,
            StartedAt = time.GetUtcNow(),
        });
        var said = new List<CommandRequest>();
        var watch = new LegWatch(state, (c, _) => { said.Add(c); return Task.FromResult(state.Complete(c, null)); }, time);

        time.Advance(TimeSpan.FromMinutes(30));
        await watch.LookAsync(Ct);
        said.ShouldBeEmpty();

        state.PutJob(state.Jobs().Single() with { State = JobState.Succeeded, EndedAt = time.GetUtcNow() });
        await watch.LookAsync(Ct);
        said.ShouldHaveSingleItem().Args[0].ShouldContain("is not finished yet");
    }

    [Theory]
    [InlineData("90m", 90 * 60)]
    [InlineData("2h", 7200)]
    [InlineData("45s", 45)]
    public void ATimeoutIsWrittenAsPeopleWriteIt(string text, int seconds)
    {
        PilotCommand.TryDuration(text, out var span).ShouldBeTrue();
        span.ShouldBe(TimeSpan.FromSeconds(seconds));
        PilotCommand.TryDuration("2d", out _).ShouldBeFalse();
    }
    // A busy tree: the job's shell runs a child that burns CPU and prints a counter until the flag file exists, then exits 7.
    private string Busy(string flag)
    {
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(_root, "busy.ps1");
            File.WriteAllText(ps1, "param($flag)\n$i = 0\nwhile (-not (Test-Path $flag)) { $i++; if ($i % 200 -eq 0) { Write-Output $i } }\nexit 7\n");
            return $"pwsh -NoProfile -File '{ps1}' '{flag}'; exit $LASTEXITCODE";
        }
        var sh = Path.Combine(_root, "busy.sh");
        // The loop runs in a background subshell the script waits for: dash runs `sh -c 'sh script'` as the script itself, so
        // without a child the job's tree would be one process on Ubuntu.
        File.WriteAllText(sh, "(i=0\nwhile [ ! -f \"$1\" ]; do i=$((i+1)); if [ $((i % 200)) -eq 0 ]; then echo $i; fi; done) &\nwait $!\nexit 7\n");
        return $"sh '{sh}' '{flag}'";
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APausedJobsTreeStopsWorkingAndWritingAndResumesToItsRealExitCode()
    {
        var flag = Path.Combine(_root, "go");
        var id = Run(Busy(flag)).Made!;
        var log = await Running(id);

        _jobs.Pause(Verb(Verbs.PauseJob, id)).Ok.ShouldBeTrue();
        var paused = _state.Jobs().Single(j => j.Id == id);
        (paused.State, paused.PausedAt is not null).ShouldBe((JobState.Running, true));
        // PIL537 (run 41144008663, windows: CpuSince 750 ms against < 100 ms while paused): half a core ran through the paused
        // window, so a process of the tree was not frozen, one started between the node's read of the tree and its suspend.
        // Pause now returns once a pass of the tree froze nothing new, so the sample is taken at once, on that event, not after
        // a fixed delay that let a straggler's start-up CPU look like work.
        var tree = TreeOf(_jobs.PidOf(id)!.Value);
        // PIL746: wait until the tree settled, then one more window must show no CPU and no log growth at all (a frozen process
        // accrues exactly none); the window's length no longer matters, so load cannot move the verdict.
        await WaitSettled(tree, () => LogLength(log));
        var (cpu, length) = (Cpu(tree), LogLength(log));
        var quiet = await SampleWindow(tree, () => LogLength(log), TimeSpan.FromMilliseconds(500));
        quiet.Cpu.ShouldBe(TimeSpan.Zero);
        (quiet.Before, quiet.After).ShouldBe((length, length));

        _jobs.Resume(Verb(Verbs.ResumeJob, id)).Ok.ShouldBeTrue();
        var resumed = _state.Jobs().Single(j => j.Id == id);
        (resumed.PausedAt, resumed.PausedFor > TimeSpan.Zero).ShouldBe((null, true));
        // Resumed: wait until the same tree has used CPU since the paused sample and the log grew, under the ceiling.
        await WaitForSample(() =>
        {
            var (used, now) = (CpuSince(tree, cpu), LogLength(log));
            return (used > TimeSpan.Zero && now > length, $"cpu +{used.TotalMilliseconds:0} ms, log {length} -> {now}");
        }, "the resumed tree working and writing again");

        File.WriteAllText(flag, "");
        var job = await Ended(id);
        (job.State, job.ExitCode).ShouldBe((JobState.Failed, 7));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePausedSpanIsNotCountedAgainstTheTimeout()
    {
        var flag = Path.Combine(_root, "go");
        // PIL746 (j3539: Pause refused, the job had already run out its 15 s before a loaded machine got to pause it): the time
        // before the pause counts against the timeout, so it is generous (30 s), and the wait for "past the deadline" is measured
        // from the pause itself, so a slow start cannot move it.
        var id = Run(Busy(flag), timeoutSeconds: 30).Made!;
        await Running(id);

        var paused = _jobs.Pause(Verb(Verbs.PauseJob, id));
        paused.Ok.ShouldBeTrue(paused.Error);
        // Paused for longer than the whole timeout: it would have been stopped without the pause.
        var pausedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - pausedAt < TimeSpan.FromSeconds(31))
        {
            await Task.Delay(100, Ct);
        }
        var held = _state.Jobs().Single(j => j.Id == id);
        (held.State, held.EndedAt).ShouldBe((JobState.Running, null));

        _jobs.Resume(Verb(Verbs.ResumeJob, id)).Ok.ShouldBeTrue();
        File.WriteAllText(flag, "");
        var job = await Ended(id);
        (job.State, job.ExitCode, job.Error).ShouldBe((JobState.Failed, 7, "exit 7"));
        job.PausedFor.ShouldBeGreaterThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AResumedJobStillTimesOutOnceItHasRunItsTimeout()
    {
        // PIL784: the time before the pause counts against the timeout, so it is generous (45 s) and a slow start cannot make the
        // Pause refuse because the timeout already ran out.
        var id = Run(Busy(Path.Combine(_root, "never")), timeoutSeconds: 45).Made!;
        await Running(id);
        var paused = _jobs.Pause(Verb(Verbs.PauseJob, id));
        paused.Ok.ShouldBeTrue(paused.Error);
        await Task.Delay(500, Ct);
        _jobs.Resume(Verb(Verbs.ResumeJob, id)).Ok.ShouldBeTrue();

        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Failed, "stopped after its timeout of 45s"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task CancellingAPausedJobLeavesNoProcessOfItsTreeAliveOrFrozen()
    {
        var id = Run(Busy(Path.Combine(_root, "never"))).Made!;
        await Running(id);
        _jobs.Pause(Verb(Verbs.PauseJob, id)).Ok.ShouldBeTrue();
        var tree = TreeOf(_jobs.PidOf(id)!.Value);
        tree.Count.ShouldBeGreaterThan(1);

        _jobs.Cancel(Verb(Verbs.CancelJob, id)).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error, job.PausedAt).ShouldBe((JobState.Cancelled, "cancelled by controller", null));
        for (var i = 0; i < 100 && tree.Any(Alive); i++)
        {
            await Task.Delay(50, Ct);
        }
        tree.Where(Alive).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PausingOrResumingAQueuedOrEndedOrUnpausedJobIsRefusedWithAReason()
    {
        var flag = Path.Combine(_root, "go");
        var first = Run(Busy(flag)).Made!;
        var queued = Run(Echo("later")).Made!;
        await Running(first);
        await Until(queued, j => j.Step is not null);

        _jobs.Pause(Verb(Verbs.PauseJob, queued)).Error.ShouldNotBeNull().ShouldContain("queued, not running");
        _jobs.Resume(Verb(Verbs.ResumeJob, queued)).Error.ShouldNotBeNull().ShouldContain("queued, not running");
        _jobs.Resume(Verb(Verbs.ResumeJob, first)).Error.ShouldBe($"job {first} is not paused");
        _jobs.Pause(Verb(Verbs.PauseJob, first)).Ok.ShouldBeTrue();
        _jobs.Pause(Verb(Verbs.PauseJob, first)).Error.ShouldBe($"job {first} is already paused");
        _jobs.Pause(Verb(Verbs.PauseJob, "j999")).Error.ShouldBe("There is no job j999");

        // A paused job keeps its slot: the queued one waits behind it.
        await Task.Delay(300, Ct);
        _state.Jobs().Single(j => j.Id == queued).State.ShouldBe(JobState.Queued);

        _jobs.Resume(Verb(Verbs.ResumeJob, first)).Ok.ShouldBeTrue();
        File.WriteAllText(flag, "");
        await Ended(first);
        await Ended(queued);
        _jobs.Pause(Verb(Verbs.PauseJob, queued)).Error.ShouldBe($"job {queued} is not running");
        _jobs.Resume(Verb(Verbs.ResumeJob, first)).Error.ShouldBe($"job {first} is not running");
    }

    // Starts a grandchild that sleeps and writes its pid to a marker, then sleeps itself: a check that leaves a process under it.
    private static string Grandchild(string marker) => OperatingSystem.IsWindows()
        ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -PassThru -NoNewWindow; Set-Content -Path '{marker}' -Value $p.Id; Start-Sleep 120"
        : $"sleep 120 & echo $! > '{marker}'; wait";

    private async Task<int> GrandchildPid(string marker, Task run)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(marker) || ReadLog(marker).Trim().Length == 0)
        {
            run.IsCompleted.ShouldBeFalse();
            if (clock.Elapsed > WaitCeiling)
            {
                throw new TimeoutException($"{marker} did not hold any text within {WaitCeiling}");
            }
            await Task.Delay(100, Ct);
        }
        return int.Parse(ReadLog(marker).Trim());
    }

    private async Task GoneWithin(int pid)
    {
        using var gone = new CancellationTokenSource(WaitCeiling);
        while (IsAlive(pid))
        {
            await Task.Delay(100, gone.Token);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task ACancelledCheckJobEndsTheProcessesItStarted()
    {
        var marker = Path.Combine(_root, "grandchild.pid");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = _jobs.RunCheckAsync("TIA1", "parser", Grandchild(marker), _root, TimeSpan.FromMinutes(30), cancel.Token);
        var child = await GrandchildPid(marker, run);

        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(run);

        await GoneWithin(child);
        _state.Jobs().Single(j => j.Kind == JobKind.Check).State.ShouldBe(JobState.Cancelled);
    }

    [Fact(Timeout = 240_000)]
    [Trait("Category", "Slow")]
    public async Task ACheckJobOverItsBudgetEndsTheProcessesItStartedAndSaysSo()
    {
        var marker = Path.Combine(_root, "grandchild.pid");
        // The clock is the test's, so a slow start under load does not eat the budget: it runs out when the test says.
        var (jobs, clock, _, _) = Idle();
        var run = jobs.RunCheckAsync("TIA1", "parser", Grandchild(marker), _root, TimeSpan.FromMinutes(30), Ct);
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Kind == JobKind.Check)));
        var child = await GrandchildPid(marker, run);

        clock.Advance(TimeSpan.FromMinutes(31));
        var result = await run.WaitAsync(WaitCeiling, Ct);

        result.ExitCode.ShouldBe(-1);
        result.Tail.ShouldContain("of running: the budget it was given");
        await GoneWithin(child);
        _state.Jobs().Single(j => j.Kind == JobKind.Check).State.ShouldBe(JobState.Failed);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact(Timeout = 240_000)]
    [Trait("Category", "Slow")]
    public async Task TheNodeStoppingLeavesRunningChecksRunningAndWaitingOnesQueuedAndAnOrdinaryJobRunning()
    {
        var marker = Path.Combine(_root, "grandchild-stop.pid");
        using var node = await Node.StartAsync(_root);
        node.Jobs.MaxRunning = 2;
        var jobId = node.Run(_root, "sleep 90; " + Echo("still going")).Made!;
        var job = await node.UntilAsync(jobId, j => j is { State: JobState.Running, Pid: not null }, Ct);
        var running = node.Jobs.RunCheckAsync("TIA1", "one", Grandchild(marker), _root, TimeSpan.FromMinutes(30), Ct);
        var waiting = node.Jobs.RunCheckAsync("TIA1", "two", Echo("never"), _root, TimeSpan.FromMinutes(30), Ct);
        var child = await GrandchildPid(marker, running);
        var rows = node.State.Jobs().Where(j => j.Kind == JobKind.Check).ToList();
        rows.Count.ShouldBe(2);
        // Picked by leg, not by the order the rows come back in. The grandchild's marker proves the process started, not that the
        // dispatcher has put the row Running with its pid yet, so the stop waits for the row itself.
        var runningId = rows.Single(j => j.Leg == "one").Id;
        var queuedId = rows.Single(j => j.Leg == "two").Id;
        var runningRow = await node.UntilAsync(runningId, j => j is { State: JobState.Running, Pid: not null }, Ct);
        // From here the dispatcher is held still: with no slot at all, nothing the stop frees can start the waiting check, so the
        // rows read below are what the stop left, not what the live dispatcher did after it.
        node.Jobs.MaxRunning = 0;
        node.State.Jobs().Single(j => j.Id == queuedId).State.ShouldBe(JobState.Queued);
        try
        {
            var stop = System.Diagnostics.Stopwatch.StartNew();
            await node.StopAsync();

            // The stop lets go of the running check rather than waiting on it: its watch ends at its next look (a quarter second),
            // where the check itself sleeps for two minutes.
            // Relative to the check's own sleep, not a wall-clock figure: the stop returning well inside it shows the stop did not wait.
            stop.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
            // The waiting check's caller goes with the node, but its row stays queued for the next node (pilot #737); the running one's
            // row stays running with its tree alive, as an ordinary job's does.
            await Should.ThrowAsync<OperationCanceledException>(waiting.WaitAsync(WaitCeiling, Ct));
            var stillRunning = node.State.Jobs().Single(j => j.Id == runningId);
            (stillRunning.State, stillRunning.EndedAt).ShouldBe((JobState.Running, null));
            var stillQueued = node.State.Jobs().Single(j => j.Id == queuedId);
            (stillQueued.State, stillQueued.EndedAt).ShouldBe((JobState.Queued, null));
            IsAlive(child).ShouldBeTrue();
            node.Job(jobId).State.ShouldBe(JobState.Running);
            IsAlive(job.Pid!.Value).ShouldBeTrue();
        }
        finally
        {
            // Left behind on purpose by the node; this test ends them so nothing outlives the run.
            JobKiller.Kill(runningRow);
            JobKiller.Kill(job);
            try
            {
                using var left = System.Diagnostics.Process.GetProcessById(child);
                left.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    [Fact(Timeout = 240_000)]
    [Trait("Category", "Slow")]
    public async Task ARunningCheckOutlivesTheNodeStoppingAndTheNextNodesFinishReattachesToItByIdForItsExitCode()
    {
        var marker = Path.Combine(_root, "grandchild-carry.pid");
        // The check ends when the test says so: it sleeps, then exits 7 once the flag exists.
        var flag = Path.Combine(_root, "carry.flag");
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -PassThru -NoNewWindow; Set-Content -Path '{marker}' -Value $p.Id; while (-not (Test-Path '{flag}')) {{ Start-Sleep -Milliseconds 200 }}; exit 7"
            : $"sleep 120 & echo $! > '{marker}'; while [ ! -e '{flag}' ]; do sleep 0.2; done; exit 7";
        int child = 0;
        JobRow? check = null;
        JobRow? kept = null;
        try
        {
            using (var first = await Node.StartAsync(_root))
            {
                var run = first.Jobs.RunCheckAsync("TIA1", "one", command, _root, TimeSpan.FromMinutes(30), Ct);
                child = await GrandchildPid(marker, run);
                check = await first.UntilAsync(first.State.Jobs().Single(j => j.Kind == JobKind.Check).Id, j => j.State == JobState.Running, Ct);
                await first.StopAsync();
                await Should.ThrowAsync<OperationCanceledException>(run.WaitAsync(WaitCeiling, Ct));
                kept = first.State.Jobs().Single(j => j.Id == check.Id);
            }

            IsAlive(child).ShouldBeTrue();
            IsAlive(check.Pid!.Value).ShouldBeTrue();
            (kept.State, kept.EndedAt).ShouldBe((JobState.Running, null));

            using var second = await Node.StartAsync(_root);
            second.Jobs.IsRunning(check.Id).ShouldBeTrue();
            var again = second.Jobs.RunCheckAsync("TIA1", "one", command, _root, TimeSpan.FromMinutes(30), Ct);
            second.State.Jobs().Where(j => j.Kind == JobKind.Check).ShouldHaveSingleItem().Id.ShouldBe(check.Id);

            File.WriteAllText(flag, "");
            var result = await again.WaitAsync(WaitCeiling, Ct);

            result.ExitCode.ShouldBe(7);
            second.State.Jobs().Where(j => j.Kind == JobKind.Check).ShouldHaveSingleItem().Id.ShouldBe(check.Id);
            await second.StopAsync();
        }
        finally
        {
            if (check is not null)
            {
                JobKiller.Kill(check);
            }
            if (child != 0)
            {
                try
                {
                    using var left = System.Diagnostics.Process.GetProcessById(child);
                    left.Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                }
            }
        }
    }

    private static System.Diagnostics.Process SpawnSleeper()
    {
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("pwsh") { ArgumentList = { "-NoProfile", "-Command", "Start-Sleep 60" } }
            : new System.Diagnostics.ProcessStartInfo("sleep") { ArgumentList = { "60" } };
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return System.Diagnostics.Process.Start(start)!;
    }

    [Fact(Timeout = 240_000)]
    public async Task ADisposedKillOnCloseHoldWithoutTheReleaseStillEndsTheProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "kill-on-close is a Windows job object");
        using var process = SpawnSleeper();
        try
        {
            var hold = KillOnCloseJob.Hold(process).ShouldNotBeNull();
            hold.Dispose();
            await process.WaitForExitAsync(Ct).WaitAsync(WaitCeiling, Ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task AReleasedKillOnCloseHoldLetsTheProcessOutliveItsDisposal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "kill-on-close is a Windows job object");
        using var process = SpawnSleeper();
        try
        {
            var hold = KillOnCloseJob.Hold(process).ShouldNotBeNull();
            hold.Release();
            hold.Dispose();
            await Task.Delay(1500, Ct);
            process.HasExited.ShouldBeFalse();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task RecoveryFollowsACheckRowItFindsRunningLikeAJobEndsOneWithNoProcessKeepsAQueuedOneAndFollowsAnOrdinaryJob()
    {
        string[] program = OperatingSystem.IsWindows() ? ["pwsh", "-NoProfile", "-Command", "Start-Sleep 30"] : ["sleep", "30"];
        System.Diagnostics.Process Spawn()
        {
            var start = new System.Diagnostics.ProcessStartInfo(program[0]) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in program.Skip(1))
            {
                start.ArgumentList.Add(arg);
            }
            return System.Diagnostics.Process.Start(start)!;
        }
        using var checkProcess = Spawn();
        using var jobProcess = Spawn();
        try
        {
            JobRow Row(string id, JobKind kind, JobState state, System.Diagnostics.Process? process) => new()
            {
                Id = id, Command = "dotnet test", Cwd = _root, Kind = kind, Flight = kind == JobKind.Check ? "TIA1" : null, Leg = kind == JobKind.Check ? "parser" : null,
                Owner = kind == JobKind.Check ? null : "a1b2c3d4", OwnerName = kind == JobKind.Check ? null : "se1-tia1-parser", State = state,
                LogPath = Path.Combine(_root, id + ".log"), Pid = process?.Id,
                ProcessStartedAt = process is null ? null : new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow, QueuedAt = DateTimeOffset.UtcNow,
            };
            _state.PutJob(Row("jc1", JobKind.Check, JobState.Running, checkProcess));
            _state.PutJob(Row("jc2", JobKind.Check, JobState.Queued, null));
            _state.PutJob(Row("jc3", JobKind.Check, JobState.Running, null));
            _state.PutJob(Row("jo1", JobKind.Run, JobState.Running, jobProcess));

            // The dispatcher is live and acts on its own loop, so it is held still while the queue is read: with no slot at all it
            // starts nothing it is handed, whatever moment the hand-over reaches it.
            _jobs.MaxRunning = 0;
            await _jobs.RecoverAsync(Ct);

            // A running check whose process is alive is followed again, not ended: Running on its own pid, in a slot.
            IsAlive(checkProcess.Id).ShouldBeTrue();
            var ran = _state.Jobs().Single(j => j.Id == "jc1");
            (ran.State, ran.Pid, ran.EndedAt).ShouldBe((JobState.Running, checkProcess.Id, null));
            _jobs.IsRunning("jc1").ShouldBeTrue();
            // One with no pid cannot be found again, so it is ended.
            var lost = _state.Jobs().Single(j => j.Id == "jc3");
            (lost.State, lost.EndedAt is not null).ShouldBe((JobState.Failed, true));
            _jobs.IsRunning("jc3").ShouldBeFalse();
            // The ordinary job is followed again the same way.
            var other = _state.Jobs().Single(j => j.Id == "jo1");
            (other.State, other.Pid, other.EndedAt).ShouldBe((JobState.Running, jobProcess.Id, null));
            _jobs.IsRunning("jo1").ShouldBeTrue();
            IsAlive(jobProcess.Id).ShouldBeTrue();
            // The queued check carried over: the dispatcher has it (its row says where it waits) and it is still queued, not ended.
            var queued = await Until("jc2", j => j.Step?.StartsWith("next in line", StringComparison.Ordinal) == true);
            (queued.State, queued.EndedAt).ShouldBe((JobState.Queued, null));

            // Given its one slot back, the dispatcher still keeps it waiting: the two recovered jobs hold the slot. The row it rewrites
            // with the new slot count is the dispatch that judged it; a start would leave the row Running instead.
            _jobs.MaxRunning = 1;
            _jobs.Pump();
            var judged = await Until("jc2", j => j.Slots == 1 || j.State != JobState.Queued);
            (judged.State, judged.EndedAt).ShouldBe((JobState.Queued, null));
        }
        finally
        {
            // Held still again, so the queued check does not start in the test's folder once the sleepers below are ended.
            _jobs.MaxRunning = 0;
            try
            {
                jobProcess.Kill(entireProcessTree: true);
                checkProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

}

/// <summary>Kills a job's process from its row, for a test that must not leave one running.</summary>
internal static class JobKiller
{
    public static void Kill(JobRow job)
    {
        if (job is { Pid: { } pid, ProcessStartedAt: { } startedAt })
        {
            new JobProcess(pid, startedAt).Kill();
        }
    }
}

/// <summary>What follows a job's log.</summary>
public sealed class JobFilesTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), "p2f-" + Guid.NewGuid().ToString("N")[..8] + ".log");

    public void Dispose() => File.Delete(_log);

    [Fact]
    public void ALineTheJobHasNotEndedYetWaitsForItsNewline()
    {
        var follower = new LogFollower(_log);
        follower.Read(final: false).ShouldBeEmpty();

        File.WriteAllText(_log, "one\r\ntwo\nthr");
        follower.Read(final: false).ShouldBe(["one", "two"]);

        File.AppendAllText(_log, "ee\nfour");
        follower.Read(final: false).ShouldBe(["three"]);
        follower.Read(final: true).ShouldBe(["four"]);
        follower.Read(final: true).ShouldBeEmpty();
    }

    [Fact]
    public void AMultiByteCharacterSplitBetweenTwoReadsIsWholeInTheLine()
    {
        var follower = new LogFollower(_log);
        var bytes = System.Text.Encoding.UTF8.GetBytes("é\n");
        File.WriteAllBytes(_log, bytes[..1]);
        follower.Read(final: false).ShouldBeEmpty();

        using (var stream = new FileStream(_log, FileMode.Append))
        {
            stream.Write(bytes, 1, bytes.Length - 1);
        }

        follower.Read(final: false).ShouldBe(["é"]);
    }

    [Fact]
    public void AFollowerThatStartsAtTheEndReadsOnlyTheLastWholeLinesAndThenWhatComesAfter()
    {
        var head = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i:0000}\n"));
        File.WriteAllText(_log, head);
        var follower = new LogFollower(_log, fromEnd: true);

        var lines = follower.Read(final: false);

        lines.Count.ShouldBeLessThan(3000);
        lines[^1].ShouldBe("line 2999");
        // Whole lines only: the one the window started in is dropped.
        lines.ShouldAllBe(l => l.StartsWith("line ", StringComparison.Ordinal) && l.Length == 9);
        File.AppendAllText(_log, "after\npart");
        follower.Read(final: false).ShouldBe(["after"]);
        follower.Read(final: true).ShouldBe(["part"]);
    }
}

/// <summary>A job's command line from <c>pilot run</c>'s words, quoted again for the node's shell.</summary>
public sealed class ShellLineTests
{
    [Fact]
    public void AWordHoldingAShellCharacterIsQuotedAgainAndAnOperatorIsLeftOne()
    {
        string[] words = ["dotnet", "test", "-c", "Release", "--filter", "FullyQualifiedName~A|FullyQualifiedName~B", "2>&1"];

        Pilot.Node.Flights.ShellLine.Join(words, windows: true)
            .ShouldBe("dotnet test -c Release --filter 'FullyQualifiedName~A|FullyQualifiedName~B' 2>&1");
        Pilot.Node.Flights.ShellLine.Join(words, windows: false)
            .ShouldBe("dotnet test -c Release --filter 'FullyQualifiedName~A|FullyQualifiedName~B' 2>&1");
        Pilot.Node.Flights.ShellLine.Join(["pwsh", "-File", "C:/temp/a b/run.ps1"], windows: true).ShouldBe("pwsh -File 'C:/temp/a b/run.ps1'");
        Pilot.Node.Flights.ShellLine.Join(["echo", "it's"], windows: true).ShouldBe("echo 'it''s'");
        Pilot.Node.Flights.ShellLine.Join(["echo", "it's"], windows: false).ShouldBe(@"echo 'it'\''s'");
    }

    [Fact]
    public void OneWordIsACommandLineAlready() =>
        Pilot.Node.Flights.ShellLine.Join(["dotnet test --filter \"A|B\" && echo done"], windows: true).ShouldBe("dotnet test --filter \"A|B\" && echo done");
}
