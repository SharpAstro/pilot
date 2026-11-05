using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Node.Lib;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's jobs (<c>pilot run</c>): a long run handed over by a session, which then ends its turn and is woken with the
/// result, so no worker sits polling a test suite (v2's phase 2). A job is the node's own, as tianwen's are: it runs on the
/// node's lifetime, never a request's; it is on the stream like a flight; and only <c>cancel-job</c> or its timeout end it
/// early (the node stopping does not; <c>pause-job</c> freezes it without ending it). Its owner is told how it ended, a real turn for an idle worker, and a wake that fails rings.
/// </summary>
/// <remarks>
/// <para><b>The machine is shared.</b> Several flights hand jobs to one node, and a full test suite can take gigabytes, so
/// jobs do not all start at once: at most <see cref="MaxRunning"/> run together, and a second or later one starts only while
/// the machine has free what the same kind of command used before (<see cref="JobMemory"/>). The head of the queue with
/// nothing running starts whatever the memory, so the queue never waits for memory only its own jobs could free. The rest wait
/// in the order they came, from every flight alike, each saying where it stands and what it needs in its step; their owners
/// end their turns as for any job and are woken when it ends, so a wait costs them nothing.</para>
/// <para><b>What a job needs is measured.</b> The job's process tree is sampled every <see cref="TreeEvery"/>; the run's value is
/// the median of the samples (its peak is only shown), and the estimate for a kind of command (<see cref="JobKey"/>) is the median
/// of its last runs' values, falling back to the same target with other flags, the same verb in the repo, then 1 GB.</para>
/// <para><b>A job outlives the node.</b> Its process is detached and the node follows it by its files, so a restarted node
/// finds a job its predecessor left again: on start it checks each running row's pid and start time (a reused pid is not
/// the job). One still alive is followed again (log, step, exit file) with its timeout counted from its start; one that
/// ended while the node was down is ended from its exit file, and its owner told as for any job; one gone without an exit
/// file fails, saying so, rather than leave a worker waiting for a result that will never come. Waiting jobs stay waiting,
/// in their order.</para>
/// <para><b>Pause.</b> <c>pause-job</c> freezes the job's whole process tree (every process the node reads under it, and on Linux
/// the job's process group too, since a job is its own session) and the row records <see cref="JobRow.PausedAt"/>. The state
/// stays <see cref="JobState.Running"/>, so the job keeps its slot. The row is the truth, not anything in memory: a node
/// that finds a paused job again leaves it frozen and reads it paused, and resume, cancel and a timeout reach it by pid as for
/// any job. Its timeout does not run while it is paused (<see cref="JobRow.PausedFor"/> and the open span are added back), and
/// the idle check skips it. Cancel and a timeout thaw the tree before they kill it, so nothing is left frozen behind.</para>
/// </remarks>
public sealed class JobRunner(NodeState state, NodeOptions options, TimeProvider time) : BackgroundService
{
    public const string UntoldKind = "job-untold";

    /// <summary>The For-you card for a running job that has used next to no CPU while others queue behind it.</summary>
    public const string IdleKind = "job-idle";

    /// <summary>How long a job may run unless its owner said otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    /// <summary>The longest a job may be given.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(8);

    private const int TailLines = 20;

    /// <summary>How often a running job's log and exit file are looked at.</summary>
    public TimeSpan FollowEvery { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Where a job's exit code is written when it ends: its log's path plus <c>.exit</c>.</summary>
    public static string ExitPathOf(string logPath) => logPath + ".exit";

    /// <summary>Says a job's end to its owner: <see cref="NodeCommands.RunAsync"/> with a <c>say</c>.</summary>
    public Func<CommandRequest, CancellationToken, Task<CommandResult>>? Say { get; set; }

    /// <summary>The session ledger, to tell a session that ended normally from one that was replaced; null where none is kept (#601).</summary>
    public Sessions.SessionLedger? Ledger { get; set; }

    /// <summary>How often a running job's step (its last line) is put on the stream, so a long run shows it is alive without a delta per line.</summary>
    public TimeSpan StepEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many jobs run at once; the rest wait their turn. Set live by <c>set node max-jobs</c>, then <see cref="Pump"/>.</summary>
    public int MaxRunning { get; set; } = 1;

    /// <summary>The priority every job's process tree runs at, so builds and tests yield the CPU to other work (#629); Normal keeps the node's own.</summary>
    public JobPriority Priority { get; set; } = JobPriority.BelowNormal;

    /// <summary>
    /// The least free memory a second job waits for: what it needs is its kind's measured estimate (<see cref="JobMemory"/>), or this
    /// when that is less. A floor, never a cap: as a cap, a kind measured at 8 GB started beside another with 2 GB free, the overload
    /// the gate is for (review of #357). Set live by <c>set node job-memory</c>; 0 leaves the estimate alone.
    /// </summary>
    public long MinFreeMemory { get; set; }

    /// <summary>The memory, in bytes, the processes of a job's tree hold now; null when it cannot be read. Null reads each from <see cref="ProcessTable"/>; a test puts its own.</summary>
    public Func<JobProcessInfo[], long?>? MemorySample { get; init; }

    /// <summary>The machine's free memory in bytes, or null when it cannot be read (then memory holds nothing back).</summary>
    public Func<long?> FreeMemory { get; init; } = MachineMemory.Available;

    /// <summary>How often the queue looks again, for memory that has come free.</summary>
    public TimeSpan PumpEvery { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a running job may use next to no CPU, with others queued behind it, before a card says so.</summary>
    public TimeSpan IdleWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The CPU time over <see cref="IdleWindow"/> below which a job counts as near-idle.</summary>
    public TimeSpan IdleCpu { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The CPU time a running job's process tree has used in all, or null when it cannot be read.</summary>
    public Func<string, TimeSpan?>? JobCpu { get; init; }

    /// <summary>The machine's CPU use now in percent, or null when it is not sampled: a check's saturation is counted from it. A test puts its own.</summary>
    public Func<double?>? MachineCpu { get; init; }

    /// <summary>The machine CPU percent a benchmark (<see cref="JobKind.Bench"/>) needs at most to start: above it the machine is not quiet.</summary>
    public double BenchQuietCpu { get; set; } = 15;

    /// <summary>The share of the looks at a check that found the machine's CPU above <see cref="SaturatedAbove"/> percent, over which the machine counts as saturated.</summary>
    public const double SaturatedShare = 0.5;

    /// <summary>The machine CPU percent above which a look counts as saturated.</summary>
    public const double SaturatedAbove = 90;

    /// <summary>The OS's process table, as the node reads a job's tree and kills it: a test puts a fake one.</summary>
    public IProcessTable ProcessTable { get; init; } = OsProcessTable.Instance;

    /// <summary>How often a running job's process tree is looked at, so a process it starts is remembered and is found again if it outlives the wrapper.</summary>
    public TimeSpan TreeEvery { get; set; } = TimeSpan.FromSeconds(2);

    // The running jobs' processes, by pid and start time: the node follows a job by its files, and holds no pipe of it.
    private readonly ConcurrentDictionary<string, JobProcess> _running = new(StringComparer.Ordinal);
    // Where each running job's idle window began and the CPU it had used then. The dispatcher's alone.
    private readonly Dictionary<string, (DateTimeOffset At, TimeSpan Cpu, TimeSpan Last)> _cpuMarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessTreeCpu.Tally> _cpuTallies = new(StringComparer.Ordinal);
    // What each running job's tree has held; made once, from the process table the runner was given.
    private JobTree? _tree;
    private JobTree Tree => LazyInitializer.EnsureInitialized(ref _tree, () => new JobTree(ProcessTable));
    // Wakes a watch that is between two looks, so a cancel is seen at once and not a look later.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _nudges = new(StringComparer.Ordinal);
    // Every job this node handed over, so a recovery knows which ones a previous node left behind.
    private readonly ConcurrentDictionary<string, byte> _mine = new(StringComparer.Ordinal);
    // The watches of the running jobs, so the node's stop waits for each to let go of its job without touching its row.
    private readonly ConcurrentDictionary<string, Task> _watches = new(StringComparer.Ordinal);
    // Rows whose end this node run has already tried to tell (or is telling): the sweep tries each once, so a refused tell rings once.
    private readonly ConcurrentDictionary<string, byte> _tried = new(StringComparer.Ordinal);
    // Rows whose tell threw and rang: a retry that throws again does not ring again.
    private readonly ConcurrentDictionary<string, byte> _rang = new(StringComparer.Ordinal);
    // What the dispatcher acts on, one at a time: a job handed over, a cancel of a waiting one, and "look again" (a job
    // ended, the timer for memory). No lock anywhere: everything that decides who runs happens on the dispatcher alone.
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    // The waiting jobs, in order. The dispatcher's alone: nothing else reads or writes it.
    private readonly List<JobRow> _queue = [];
    // Cancels that reached the dispatcher before their job did (Run puts the row, then hands the job over, and a cancel
    // can land between the two): the job is cancelled before its turn when it arrives. The dispatcher's alone.
    private readonly Dictionary<string, (string Who, string? Dropped)> _withdrawnEarly = new(StringComparer.Ordinal);
    // Read-modify-write of a running job's row (pause, resume, the step): one at a time, so none loses another's change.
    private readonly object _rowLock = new();
    // The checks a caller awaits (RunCheckAsync), by job id: completed when the job ends however it ends.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CheckRun>> _checkWaiters = new(StringComparer.Ordinal);
    // The benchmarks a caller awaits (QueueBench), by job id: completed with the ended row when the job ends however it ends.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JobRow>> _benchWaiters = new(StringComparer.Ordinal);
    // The running checks' kill-on-close job objects (Windows): a node that crashes ends them, as a check never outlives its node.
    private readonly ConcurrentDictionary<string, KillOnCloseJob> _holds = new(StringComparer.Ordinal);
    // The share of looks at a running check at which the machine's CPU was above 90%, kept until its row ends.
    private readonly ConcurrentDictionary<string, double> _saturation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _checkBasis = new(StringComparer.Ordinal);
    private const string TimeoutPrefix = "stopped after its timeout";
    private CancellationToken _stopping;
    // Completes once the startup recovery has run. ExecuteAsync runs in the background, so without it a caller sharing the
    // state cannot tell whether a Queued row it puts will be seen by that recovery and failed as orphaned.
    private readonly TaskCompletionSource _recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the startup recovery has run: from then on, only rows already present were judged orphaned.</summary>
    internal Task Recovered => _recovered.Task;

    internal abstract record Signal;

    internal sealed record HandedOver(JobRow Job) : Signal;

    internal sealed record Withdrawn(string Id, string Who, string? Dropped = null) : Signal;

    internal sealed record LookAgain : Signal
    {
        public static readonly LookAgain Instance = new();
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        _stopping = stopping;
        try
        {
            await RecoverAsync(stopping);
        }
        finally
        {
            _recovered.TrySetResult();
        }
        _ = TickAsync(stopping);
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(stopping))
            {
                Dispatch(signal);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>Asks the dispatcher to look again every <see cref="PumpEvery"/>, for memory that has come free.</summary>
    private async Task TickAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(PumpEvery, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                Pump();
                try
                {
                    await TellUntoldAsync(null, stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The next tick looks again.
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Tells every ended, owned, untold row that is not a check and that no live watch of this node holds, once per row per
    /// node run (a refused or failing tell has rung once and is not tried again). <paramref name="owners"/> limits it to
    /// those owners' jobs. Returns whether any row was told.
    /// </summary>
    internal async Task<bool> TellUntoldAsync(IReadOnlyCollection<string>? owners, CancellationToken cancellationToken)
    {
        if (Say is null)
        {
            return false;
        }
        var told = false;
        foreach (var row in state.Jobs().Where(j => j is { EndedAt: not null, Owner: not null, Told: false } && j.Kind != JobKind.Check).ToList())
        {
            if (owners is not null && !owners.Contains(row.Owner!) || _watches.ContainsKey(row.Id) || !_tried.TryAdd(row.Id, 0))
            {
                continue;
            }
            await TellAsync(row, cancellationToken);
            told |= state.Jobs().FirstOrDefault(j => j.Id == row.Id) is { Told: true };
        }
        return told;
    }

    /// <summary>One owner set's untold ended jobs, told now (for an idle check); whether any was told.</summary>
    internal Task<bool> TellUntoldOf(IReadOnlyCollection<string> owners, CancellationToken cancellationToken = default) =>
        TellUntoldAsync(owners, cancellationToken);

    /// <summary>
    /// Takes up the jobs a previous node left: a running one still alive is followed again, one that ended while no node was
    /// up is ended from its exit file (failed when it left none), and a waiting one goes back on the queue in its order.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var left = state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued && !_mine.ContainsKey(j.Id)).ToList();
        // A check is not followed: the node that awaited it is gone, so nothing waits for its result. Its tree is ended and its row says so.
        foreach (var check in left.Where(j => j.Kind == JobKind.Check).ToList())
        {
            left.Remove(check);
            _mine[check.Id] = 0;
            EndOrphanedCheck(check);
        }
        // Back on the queue through the dispatcher, which owns it, in the order they were handed over.
        foreach (var job in left.Where(j => j.State == JobState.Queued).OrderBy(j => j.QueuedAt ?? j.StartedAt).ThenBy(j => j.Id, StringComparer.Ordinal))
        {
            _mine[job.Id] = 0;
            _signals.Writer.TryWrite(new HandedOver(job));
        }
        foreach (var job in left.Where(j => j.State == JobState.Running))
        {
            _mine[job.Id] = 0;
            await RecoverRunningAsync(job, cancellationToken);
        }
    }

    private void EndOrphanedCheck(JobRow check)
    {
        if (check is { State: JobState.Running, Pid: { } pid, ProcessStartedAt: { } startedAt })
        {
            var process = new JobProcess(pid, startedAt);
            // Only a live process of that pid and start time is touched: its named job object first, then the wrapper's tree.
            if (process.IsAlive())
            {
                DetachedShell.TerminateJob(check.Id, check.LogPath);
                process.Kill();
            }
        }
        DetachedShell.DeleteScripts(check.ScriptPath);
        state.PutJob(Unpaused(check, time.GetUtcNow()) with
        {
            State = JobState.Failed,
            Error = "the node restarted and nothing waits for this check any more, so it was ended",
            EndedAt = time.GetUtcNow(),
        });
    }

    private async Task RecoverRunningAsync(JobRow job, CancellationToken cancellationToken)
    {
        var exitPath = job.ExitPath ?? ExitPathOf(job.LogPath);
        // A pid alone proves nothing: a process that took it since has another start time.
        var process = job is { Pid: { } pid, ProcessStartedAt: { } startedAt } ? new JobProcess(pid, startedAt) : null;
        if (process is not null && process.IsAlive())
        {
            _running[job.Id] = process;
            _watches[job.Id] = Task.Run(() => WatchAsync(job, process, fromEnd: true), CancellationToken.None);
            return;
        }

        // Gone: how it ended is what its wrapper wrote.
        var follower = new LogFollower(job.LogPath, fromEnd: true);
        string[] last = [.. follower.Read(final: true)];
        job = Unpaused(job, EndOf(job, exitPath));
        var ended = ReadExit(exitPath) switch
        {
            0 => job with { State = JobState.Succeeded, ExitCode = 0 },
            { } code => job with { State = JobState.Failed, ExitCode = code, Error = $"exit {code}" },
            null => job with { State = JobState.Failed, Error = "ended while the node was down, no exit recorded" },
        };
        ended = ended with
        {
            Step = StepOf(last, job),
            EndedAt = EndOf(job, exitPath),
        };
        _tried[job.Id] = 0;
        state.PutJob(ended);
        DetachedShell.DeleteScripts(job.ScriptPath);
        await TellAsync(ended, cancellationToken, last);
    }

    /// <summary>When a job that ended while the node was down ended: when its exit file was written, else now.</summary>
    private DateTimeOffset EndOf(JobRow job, string exitPath)
    {
        try
        {
            if (File.Exists(exitPath) && new DateTimeOffset(File.GetLastWriteTimeUtc(exitPath), TimeSpan.Zero) is var written && written >= job.StartedAt)
            {
                return written;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not readable: now will do.
        }
        return time.GetUtcNow();
    }

    /// <summary><c>run session/&lt;id&gt;|node &lt;cwd&gt; &lt;timeout seconds&gt; &lt;command…&gt;</c>: starts the job; its id is the result's <c>Made</c>.</summary>
    public CommandResult Run(CommandRequest command)
    {
        var args = command.Args ?? [];
        // A leading --alone asks for the node to itself (a benchmark); the rest is as for any run.
        var alone = args.Length > 0 && args[0] == Verbs.AloneArg;
        if (alone)
        {
            args = [.. args.Skip(1)];
        }
        if (args.Length < 3)
        {
            return state.Complete(command, "run takes the folder, the timeout in seconds (0 for the default) and the command");
        }
        var cwd = args[0];
        if (!Directory.Exists(cwd))
        {
            return state.Complete(command, $"there is no folder {cwd} on this node's machine");
        }
        if (!long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return state.Complete(command, $"the timeout is in whole seconds, not {args[1]}");
        }
        var timeout = seconds == 0 ? DefaultTimeout : TimeSpan.FromSeconds(seconds);
        if (timeout > MaxTimeout)
        {
            return state.Complete(command, $"a job may run {MaxTimeout.TotalHours:0} hours at most");
        }
        var line = ShellLine.Join([.. args.Skip(2)], OperatingSystem.IsWindows());
        if (line.Length == 0)
        {
            return state.Complete(command, "run needs the command to run");
        }
        if (SleepOnly.Refusal(line) is { } refusal)
        {
            return state.Complete(command, refusal);
        }
        // A bash or sh line runs Git Bash on Windows, never WSL's launcher; with none found it is refused here, running nothing.
        if (GitBash.Refusal(line) is { } noGitBash)
        {
            return state.Complete(command, noGitBash);
        }

        SessionRow? owner = null;
        if (command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal))
        {
            var id = command.Target[Verbs.SessionTargetPrefix.Length..];
            owner = state.Sessions().FirstOrDefault(s => s.Id == id);
            if (owner is null)
            {
                return state.Complete(command, $"There is no session {id} to wake with the result");
            }
        }
        else if (command.Target != Verbs.NodeTarget)
        {
            return state.Complete(command, $"run takes a session to wake ({Verbs.SessionTargetPrefix}<id>) or the node, not {command.Target}");
        }

        var jobId = state.NextJobId();
        var folder = Path.Combine(options.DataFolder, "jobs");
        Directory.CreateDirectory(folder);
        var logPath = Path.Combine(folder, $"{jobId}.log");
        var job = new JobRow
        {
            Id = jobId,
            Command = line,
            Cwd = cwd,
            Owner = owner?.Id,
            OwnerName = owner?.Name,
            Flight = owner?.Flight,
            Leg = owner?.Leg,
            Kind = alone ? JobKind.Bench : JobKind.Run,
            State = JobState.Queued,
            LogPath = logPath,
            ExitPath = ExitPathOf(logPath),
            Timeout = timeout,
            StartedAt = time.GetUtcNow(),
            MemoryKey = JobKey.Of(line, cwd).ToString(),
        };
        job = job with { QueuedAt = job.StartedAt };

        // Handed to the dispatcher, which starts it at once when nothing waits before it and the machine has room.
        _mine[jobId] = 0;
        state.PutJob(job);
        _signals.Writer.TryWrite(new HandedOver(job));
        return state.Complete(command, null, made: jobId);
    }

    /// <summary>
    /// Runs a leg's check as a job of kind <see cref="JobKind.Check"/>: it waits its turn in the same queue as any job (so
    /// <see cref="MaxRunning"/> and the memory gate hold it), has no owner and says nothing to a session, and its
    /// <paramref name="budget"/> counts from when it starts running, not from when it was queued. The result is its exit code
    /// and last lines; one stopped by its budget, cancelled or never started is -1 with why. Cancelling
    /// <paramref name="cancellationToken"/> withdraws a waiting check or kills a running one's tree.
    /// </summary>
    public Task<CheckRun> RunCheckAsync(string flight, string leg, string command, string cwd, TimeSpan budget, CancellationToken cancellationToken) =>
        RunCheckAsync(flight, leg, command, cwd, new CheckLimit(budget, "the budget it was given", 0, null), cancellationToken);

    /// <summary>The limit a check of <paramref name="command"/> in <paramref name="cwd"/> gets, from this node's job rows.</summary>
    public CheckLimit CheckLimitFor(string command, string cwd) => CheckLimit.For(state.Jobs(), JobKey.Of(command, cwd));

    /// <summary>As above, with the limit and where it came from, so a stop can say both.</summary>
    public async Task<CheckRun> RunCheckAsync(string flight, string leg, string command, string cwd, CheckLimit limit, CancellationToken cancellationToken)
    {
        var budget = limit.Limit;
        var jobId = state.NextJobId();
        _checkBasis[jobId] = limit.Basis;
        var folder = Path.Combine(options.DataFolder, "jobs");
        Directory.CreateDirectory(folder);
        var logPath = Path.Combine(folder, $"{jobId}.log");
        var now = time.GetUtcNow();
        var job = new JobRow
        {
            Id = jobId,
            Command = command,
            Cwd = cwd,
            Kind = JobKind.Check,
            Flight = flight,
            Leg = leg,
            State = JobState.Queued,
            LogPath = logPath,
            ExitPath = ExitPathOf(logPath),
            Timeout = budget,
            StartedAt = now,
            QueuedAt = now,
            MemoryKey = JobKey.Of(command, cwd).ToString(),
        };
        var done = new TaskCompletionSource<CheckRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        _checkWaiters[jobId] = done;
        _mine[jobId] = 0;
        state.PutJob(job);
        _signals.Writer.TryWrite(new HandedOver(job));
        using (cancellationToken.Register(() => CancelJob(jobId, "the check's caller", force: true)))
        {
            var run = await done.Task;
            // A caller that gave up is told so, not handed a result it did not ask for.
            cancellationToken.ThrowIfCancellationRequested();
            return run;
        }
    }

    /// <summary>
    /// Queues a benchmark as a job of kind <see cref="JobKind.Bench"/> (the node to itself) and returns its id and a task that
    /// completes with the ended row when the job ends however it ends. <paramref name="owner"/>, when given, is woken as for any job.
    /// </summary>
    public (string Id, Task<JobRow> Ended) QueueBench(string command, string cwd, TimeSpan timeout, SessionRow? owner)
    {
        var jobId = state.NextJobId();
        var folder = Path.Combine(options.DataFolder, "jobs");
        Directory.CreateDirectory(folder);
        var logPath = Path.Combine(folder, $"{jobId}.log");
        var now = time.GetUtcNow();
        var job = new JobRow
        {
            Id = jobId,
            Command = command,
            Cwd = cwd,
            Owner = owner?.Id,
            OwnerName = owner?.Name,
            Flight = owner?.Flight,
            Leg = owner?.Leg,
            Kind = JobKind.Bench,
            State = JobState.Queued,
            LogPath = logPath,
            ExitPath = ExitPathOf(logPath),
            Timeout = timeout,
            StartedAt = now,
            QueuedAt = now,
            MemoryKey = JobKey.Of(command, cwd).ToString(),
        };
        var done = new TaskCompletionSource<JobRow>(TaskCreationOptions.RunContinuationsAsynchronously);
        _benchWaiters[jobId] = done;
        _mine[jobId] = 0;
        state.PutJob(job);
        _signals.Writer.TryWrite(new HandedOver(job));
        return (jobId, done.Task);
    }

    private void Finish(JobRow ended, string[]? last)
    {
        if (_benchWaiters.TryRemove(ended.Id, out var bench))
        {
            bench.TrySetResult(ended);
            return;
        }
        if (!_checkWaiters.TryRemove(ended.Id, out var waiter))
        {
            return;
        }
        var tail = last is { Length: > 0 } ? string.Join('\n', last.TakeLast(30)) : "";
        _saturation.TryRemove(ended.Id, out var share);
        _checkBasis.TryRemove(ended.Id, out var basis);
        waiter.TrySetResult(ended.State switch
        {
            JobState.Succeeded => new CheckRun(0, tail),
            _ when ended.Error?.StartsWith(TimeoutPrefix, StringComparison.Ordinal) == true =>
                new CheckRun(-1, $"the check was stopped at {CheckLimit.Describe(ended.Timeout)} of running: {basis ?? "its limit"}") { StoppedByBudget = true, Saturation = share },
            _ when ended.ExitCode is { } code => new CheckRun(code, tail),
            _ => new CheckRun(-1, string.IsNullOrEmpty(tail) ? ended.Error ?? "the check did not run" : $"{ended.Error}\n{tail}"),
        });
    }

    /// <summary>Whether the job's process is held as running: what a cancel looks for.</summary>
    internal bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>The running jobs' root processes, as (job id, pid): what the machine sampler marks a job's tree by.</summary>
    internal List<(string Id, int Pid)> RunningPids() => [.. _running.Select(pair => (pair.Key, pair.Value.Pid))];

    /// <summary>Asks the dispatcher to look again: a slot or memory may have come free.</summary>
    public void Pump() => _signals.Writer.TryWrite(LookAgain.Instance);

    /// <summary>
    /// One signal, on the dispatcher: then every waiting job the machine has room for starts, in order. Internal so a test
    /// can drive the signals in an order the channel only rarely gives.
    /// </summary>
    internal void Dispatch(Signal signal)
    {
        switch (signal)
        {
            case HandedOver handed when _withdrawnEarly.Remove(handed.Job.Id, out var by):
                CancelBeforeItsTurn(handed.Job, by.Who, by.Dropped);
                break;
            case HandedOver { Job.Kind: JobKind.Bench } handed:
                // A benchmark goes ahead of the other jobs, so the drain it waits for is not endless; benchmarks keep their own order.
                _queue.Insert(_queue.FindLastIndex(j => j.Kind == JobKind.Bench) + 1, handed.Job);
                break;
            case HandedOver handed:
                _queue.Add(handed.Job);
                break;
            case Withdrawn withdrawn when _queue.FindIndex(j => j.Id == withdrawn.Id) is var at and >= 0:
                var waiting = _queue[at];
                _queue.RemoveAt(at);
                CancelBeforeItsTurn(waiting, withdrawn.Who, withdrawn.Dropped);
                break;
            case Withdrawn { Dropped: not null } dropRace when _running.ContainsKey(dropRace.Id):
                // A drop is for queued jobs only: this one started first, and runs on.
                break;
            case Withdrawn withdrawn when _running.TryGetValue(withdrawn.Id, out var process):
                // It started before the cancel reached the dispatcher: stop it as any running job.
                _cancelled[withdrawn.Id] = withdrawn.Who;
                Kill(withdrawn.Id, process);
                break;
            case Withdrawn withdrawn when state.Jobs().FirstOrDefault(j => j.Id == withdrawn.Id) is { State: JobState.Queued }:
                // Its hand-over is still behind this in the channel: cancel it when it comes.
                _withdrawnEarly[withdrawn.Id] = (withdrawn.Who, withdrawn.Dropped);
                break;
        }

        ReorderQueue();

        StartWhatCan();

        // Each waiting job says where it stands.
        var flights = state.Flights();
        var admission = state.Admission();
        var now = time.GetUtcNow();
        // Benchmarks go by their own rule, so only the other jobs ahead count towards a priority reason.
        // Each wait is worked out once per dispatch and shared by the steps and BehindHeld.
        var whys = _queue.Select(j => WhyWait(j)).ToList();
        var ranks = _queue.Select(j => j.Kind == JobKind.Bench ? (int?)null : JobOrder.RankOf(j, flights, admission, now)).ToList();
        for (var i = 0; i < _queue.Count; i++)
        {
            var job = _queue[i];
            var why = i > 0 && ranks[i] is { } rank
                ? JobOrder.WhyHere(job, rank, JobOrder.IsLifted(job, JobOrder.BaseRankOf(job, flights, admission), now), ranks.Take(i).OfType<int>())
                : null;
            var place = why is null ? "" : $" ({why})";
            _queue[i] = job with
            {
                Step = i == 0 ? $"next in line: {whys[0]}" : BenchHold(job) is { } held ? $"queued, {i} ahead of it{place}; {held}" : BehindHeld(i, whys) is { } behind ? $"queued, {i} ahead of it{place}; {behind}" : BuildHold(job) is { } building ? $"queued, {i} ahead of it{place}; {building}" : BehindOwn(i) is { } own ? $"queued, {i} ahead of it{place}; {own}" : $"queued, {i} ahead of it{place}; {Needs(job)}",
                Slots = MaxRunning,
                WaitsOnSibling = SiblingHold(job) is not null,
            };
            state.PutJob(_queue[i]);
        }

        CheckIdle();
    }

    /// <summary>
    /// Starts every waiting job the machine has room for. From the head while a job may start, as ever; when the job at the front
    /// waits only on a sibling-clone hold, the walk goes on down the queue and starts the first later jobs that can start now
    /// and share no clone with a held job passed over, so a job that builds nothing is not stuck behind a held head, and no
    /// job jumps a held one it shares clones with. The walk stops at the first job that waits for anything but a sibling hold
    /// (memory, a slot, a benchmark): a head waiting for memory must not be starved by smaller jobs slipping past it forever.
    /// A start fills a slot and changes memory, so the walk begins again after each one. A job started out of turn can still be
    /// running when the holder releases, so the head may then wait for a slot; once it no longer waits on a sibling hold it blocks the walk.
    /// </summary>
    private void StartWhatCan()
    {
        while (_queue.Count > 0)
        {
            var passed = new List<JobRow>();
            var passedClones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var passedOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var at = -1;
            for (var i = 0; i < _queue.Count; i++)
            {
                var job = _queue[i];
                var why = WhyWait(job);
                var shares = passed.Count > 0 && SiblingsFor(job).Any(passedClones.Contains);
                var orderHeld = OrderKey(job) is { } key && passedOrders.Contains(key);
                if (why is null && !shares && !orderHeld)
                {
                    at = i;
                    break;
                }
                // Held by a running build, or queued behind a held job it shares clones with: passed over, its clones stay claimed.
                var siblingOnly = why is null || ((SiblingHold(job) is not null || BuildHold(job) is not null) && BenchHold(job) is null && job.Kind != JobKind.Bench && WhyWait(job, ignoreSibling: true, ignoreBuild: true) is null);
                if (!siblingOnly)
                {
                    break;
                }
                passed.Add(job);
                passedClones.UnionWith(SiblingsFor(job));
                if (OrderKey(job) is { } passedKey)
                {
                    passedOrders.Add(passedKey);
                }
            }
            if (at < 0)
            {
                return;
            }
            var next = _queue[at];
            _queue.RemoveAt(at);
            var step = passed.Count == 0
                ? null
                : $"started ahead of {passed.Count} held job{(passed.Count == 1 ? "" : "s")} ({string.Join(", ", passed.Select(j => j.Id))}) because it shares no sibling clone with them";
            if (Start(next with { State = JobState.Running, Step = step, StartedAt = time.GetUtcNow() }) is { } failed)
            {
                var ended = next with { State = JobState.Failed, Error = $"it did not start: {failed}", EndedAt = time.GetUtcNow() };
                _tried[next.Id] = 0;
                state.PutJob(ended);
                Finish(ended, null);
                _ = TellAsync(ended, _stopping);
            }
        }
    }

    /// <summary>Whose queue order a job keeps: its owner (a check job, which has none, its flight and leg) and the checkout of its Cwd. Null when it has neither.</summary>
    private static string? OrderKey(JobRow job)
    {
        var who = job.Owner ?? (job.Flight is null ? null : $"{job.Flight}/{job.Leg}");
        return who is null ? null : $"{who}|{SiblingSet.CheckoutOf(job.Cwd)}";
    }

    /// <summary>The earlier queued job of the same owner in the same checkout that this one must start after. Null when none.</summary>
    private string? BehindOwn(int at)
    {
        var job = _queue[at];
        if (OrderKey(job) is not { } key)
        {
            return null;
        }
        for (var i = 0; i < at; i++)
        {
            if (string.Equals(OrderKey(_queue[i]), key, StringComparison.OrdinalIgnoreCase))
            {
                return $"waits behind job {_queue[i].Id}, queued earlier by this session in this plane";
            }
        }
        return null;
    }

    /// <summary>The queued job ahead of this one that is held and shares a sibling clone with it, so this one waits behind it. Null when none.</summary>
    private string? BehindHeld(int at, List<string?> whys)
    {
        var job = _queue[at];
        if (SiblingsFor(job) is not { Count: > 0 } mine)
        {
            return null;
        }
        for (var i = 0; i < at; i++)
        {
            var ahead = _queue[i];
            var shared = SiblingsFor(ahead).Where(c => mine.Contains(c, StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
            if (shared.Count > 0 && whys[i] is not null)
            {
                return $"waits behind job {ahead.Id}, which shares its clone{(shared.Count == 1 ? "" : "s")} {string.Join(", ", shared)}";
            }
        }
        return null;
    }

    /// <summary>
    /// Puts the waiting jobs in start order on every dispatch, so a priority set while a job waits, or the wait bound passing,
    /// takes effect: benchmarks stay ahead in their own order, the rest by <see cref="JobOrder"/> (rank, queued at, id).
    /// </summary>
    private void ReorderQueue()
    {
        if (_queue.Count < 2)
        {
            return;
        }
        var benches = _queue.Where(j => j.Kind == JobKind.Bench).ToList();
        var others = JobOrder.Sort(_queue.Where(j => j.Kind != JobKind.Bench), state.Flights(), state.Admission(), time.GetUtcNow());
        _queue.Clear();
        _queue.AddRange(benches);
        _queue.AddRange(others);
    }

    /// <summary>
    /// A running job that has used next to no CPU for <see cref="IdleWindow"/> while others queue behind it raises one
    /// For-you card, closed when the job ends or the queue empties. The heuristic in <see cref="SleepOnly"/> misses some
    /// sleeps (a script that polls), and this is what is left to catch them.
    /// </summary>
    private void CheckIdle()
    {
        var now = time.GetUtcNow();
        foreach (var gone in _cpuMarks.Keys.Where(id => !_running.ContainsKey(id)).ToList())
        {
            _cpuMarks.Remove(gone);
            _cpuTallies.Remove(gone);
            state.CloseCardsAbout(Verbs.JobTarget(gone), "the job ended", IdleKind);
        }
        var paused = state.Jobs().Where(j => j.PausedAt is not null).Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _running.Keys)
        {
            if (paused.Contains(id))
            {
                // Frozen is not idle: its window starts again when it is resumed.
                _cpuMarks.Remove(id);
                continue;
            }
            var subject = Verbs.JobTarget(id);
            var read = JobCpu ?? (jobId =>
            {
                if (!_running.TryGetValue(jobId, out var p))
                {
                    return null;
                }
                if (!_cpuTallies.TryGetValue(jobId, out var tally))
                {
                    _cpuTallies[jobId] = tally = new ProcessTreeCpu.Tally();
                }
                return ProcessTreeCpu.Of(p.Pid, tally);
            });
            if (read(id) is not { } cpu)
            {
                continue;
            }
            // A reading that fell means a process ended: that is work too, so the window starts again.
            if (!_cpuMarks.TryGetValue(id, out var mark) || cpu < mark.Last || cpu - mark.Cpu >= IdleCpu)
            {
                // It has worked since the window began: the window starts again.
                _cpuMarks[id] = (now, cpu, cpu);
                state.CloseCardsAbout(subject, "the job used CPU again", IdleKind);
            }
            else if (Remember(id, mark, cpu) && _queue.Count == 0)
            {
                state.CloseCardsAbout(subject, "nothing waits behind it now", IdleKind);
            }
            else if (now - mark.At >= IdleWindow && state.Jobs().FirstOrDefault(j => j.Id == id) is { } job)
            {
                var waiting = string.Join(", ", _queue.Select(q => $"{q.Id} ({q.OwnerName ?? q.Owner ?? "the node"})"));
                state.RaiseCard(CardBin.ForYou, IdleKind, subject, $"job {id} holds the slot but has used next to no CPU for {Took(now - mark.At)}",
                    $"`{job.Command}` is run by {job.OwnerName ?? job.Owner ?? "the node"}; {_queue.Count} wait behind it: {waiting}. It may only be waiting on something; cancel it with `pilot job cancel {id}` if so, and the owner can wait with `pilot leg wait`.");
            }
        }
    }

    /// <summary>Notes the latest reading beside the window's mark, so a later one that is lower shows a process ended. Always true.</summary>
    private bool Remember(string id, (DateTimeOffset At, TimeSpan Cpu, TimeSpan Last) mark, TimeSpan cpu)
    {
        _cpuMarks[id] = mark with { Last = cpu };
        return true;
    }

    private void CancelBeforeItsTurn(JobRow job, string who, string? dropped = null)
    {
        // A dropped job (its worker went around or ended) leaves the queue told: nobody is woken for it (#327).
        var cancelled = dropped is null
            ? job with { State = JobState.Cancelled, Error = $"cancelled by {who} before its turn", Step = null, EndedAt = time.GetUtcNow() }
            : job with { State = JobState.Cancelled, Error = $"dropped: {dropped}", Step = null, EndedAt = time.GetUtcNow(), Told = true };
        _tried[job.Id] = 0;
        state.PutJob(cancelled);
        _siblings.TryRemove(job.Id, out _);
        Finish(cancelled, null);
        if (dropped is null)
        {
            _ = TellAsync(cancelled, _stopping);
        }
    }

    /// <summary>
    /// Why a job may not start now, or null when it may. With nothing running it always may (when a slot is allowed at all):
    /// the only memory it could wait for is what its own queue's jobs hold, and a head that waits for memory nothing will
    /// free is a queue that never moves. Otherwise it starts when the machine has free what its kind of command needs.
    /// </summary>
    private string? WhyWait(JobRow job, bool ignoreSibling = false, bool ignoreBuild = false)
    {
        if (job.Kind == JobKind.Bench)
        {
            if (!_running.IsEmpty)
            {
                return _running.Count == 1 ? "a benchmark runs alone: another job is running" : $"a benchmark runs alone: {_running.Count} jobs are running";
            }
            return (MachineCpu is { } read ? read() : state.Machine()?.CpuPercent) is { } cpu && cpu > BenchQuietCpu
                ? $"a benchmark waits for a quiet CPU ({cpu:0}% now, {BenchQuietCpu:0}% at most)"
                : null;
        }
        if (BenchHold(job) is { } holds)
        {
            return holds;
        }
        if (!ignoreSibling && SiblingHold(job) is { } shared)
        {
            return shared;
        }
        if (!ignoreBuild && BuildHold(job) is { } building)
        {
            return building;
        }
        if (_running.Count >= MaxRunning)
        {
            var slots = MaxRunning == 1 ? "another job is running" : $"{_running.Count} jobs are running, {MaxRunning} at a time";
            return $"{slots}; {Needs(job)}";
        }
        if (_running.IsEmpty)
        {
            return null;
        }
        var estimate = EstimateOf(job);
        var need = Math.Max(estimate.Bytes, MinFreeMemory);
        return FreeMemory() is { } free && free < need
            ? $"waiting for {MemoryEstimate.Gigabytes(need)} of free memory ({Basis(estimate, need)}; {MemoryEstimate.Gigabytes(free)} now)"
            : null;
    }

    /// <summary>Whether a job in a plane builds into the plane's own artifacts (<see cref="NodeSettings.JobPlaneOutputKey"/>); off unless set, live.</summary>
    public bool PlaneOutput
    {
        get => _planeOutput;
        set
        {
            if (_planeOutput == value)
            {
                return;
            }
            _planeOutput = value;
            // A queued job's set was worked out under the old value; a running job started with it and keeps it. Forget the
            // queued ones' sets, or a plane job cached as [] with the setting on would build into the sibling obj unlocked.
            foreach (var id in _siblings.Keys.Where(id => !_running.ContainsKey(id)).ToList())
            {
                _siblings.TryRemove(id, out _);
            }
        }
    }

    private bool _planeOutput;

    /// <summary>The clones a job's build compiles; settable so a test can give fixed sets. Answered once per job, kept until it ends.</summary>
    public Func<JobRow, IReadOnlyCollection<string>> SiblingsOf { get; set; } = job => SiblingSet.Of(job.Command, job.Cwd);

    private readonly ConcurrentDictionary<string, IReadOnlyCollection<string>> _siblings = new(StringComparer.Ordinal);

    private IReadOnlyCollection<string> SiblingsFor(JobRow job)
    {
        if (_siblings.TryGetValue(job.Id, out var known))
        {
            return known;
        }
        IReadOnlyCollection<string> set;
        try
        {
            // A plane job that builds into the plane's own artifacts shares nothing to hold (job-plane-output).
            set = PlaneOutput && Flights.PlaneOutput.PlaneOf(job.Cwd) is not null ? [] : SiblingsOf(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            set = [];
        }
        return _siblings[job.Id] = set;
    }

    /// <summary>Why a job may not start beside the running ones: its build shares a sibling clone with one of them. Null when it shares none.</summary>
    private string? SiblingHold(JobRow job)
    {
        if (_running.IsEmpty || SiblingsFor(job) is not { Count: > 0 } mine)
        {
            return null;
        }
        foreach (var id in _running.Keys.Order(StringComparer.Ordinal))
        {
            // A job past its build has let its clones go (PIL724).
            if (_released.ContainsKey(id))
            {
                continue;
            }
            var holder = state.Jobs().FirstOrDefault(j => j.Id == id);
            var theirs = SiblingsFor(holder ?? job with { Id = id });
            var shared = mine.Where(m => theirs.Contains(m, StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
            if (shared.Count > 0)
            {
                var owner = holder is { } row ? row.OwnerName ?? row.Owner ?? "the node" : "the node";
                return $"waiting for job {id}'s build of the sibling clone{(shared.Count == 1 ? "" : "s")} {string.Join(", ", shared)} ({owner}){RemainingOf(holder)}";
            }
        }
        return null;
    }

    /// <summary>
    /// Why a job that only consumes a build (every dotnet test or run in it has <c>--no-build</c>) may not start: a job that builds
    /// is running in its checkout, or is queued ahead of it there, by any owner. Null for any other job or when no build is pending.
    /// </summary>
    private string? BuildHold(JobRow job)
    {
        if (job.Kind == JobKind.Bench || !SiblingSet.OnlyConsumesBuild(job.Command, job.Cwd))
        {
            return null;
        }
        var root = SiblingSet.CheckoutOf(job.Cwd);
        bool BuildsHere(JobRow j) => j.Id != job.Id
            && string.Equals(SiblingSet.CheckoutOf(j.Cwd), root, StringComparison.OrdinalIgnoreCase)
            && SiblingSet.BuildsAny(j.Command, j.Cwd);
        foreach (var id in _running.Keys.Order(StringComparer.Ordinal))
        {
            // A job past its build has let go of it.
            if (!_released.ContainsKey(id) && state.Jobs().FirstOrDefault(j => j.Id == id) is { } row && BuildsHere(row))
            {
                return $"waits for {id}, the build of this plane";
            }
        }
        foreach (var ahead in _queue)
        {
            if (ahead.Id == job.Id)
            {
                break;
            }
            if (BuildsHere(ahead))
            {
                return $"waits for {ahead.Id}, the build of this plane";
            }
        }
        return null;
    }

    // Jobs that have let go of their sibling clones, once and for good: their tree moved past the last building segment.
    private readonly ConcurrentDictionary<string, byte> _released = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a job's process tree shows it past its last building segment. Settable so a test can say so without real processes.
    /// The default reads the command lines of the tree's processes below the wrapper.
    /// </summary>
    public Func<JobRow, JobTree.Reading, bool> PastBuild { get; set; } = (job, reading) =>
        SiblingSet.PastBuild(job.Command, job.Cwd, reading.Tree.Where(p => p.Depth > 0).Select(p => ProcessDetails.Read(p.Pid).CommandLine));

    /// <summary>Lets go of a job's clones once its tree is past its build, and wakes the queue. Returns true when it did.</summary>
    private void ReleaseIfPastBuild(JobRow job, JobTree.Reading reading)
    {
        if (_released.ContainsKey(job.Id) || SiblingsFor(job).Count == 0)
        {
            return;
        }
        bool past;
        try
        {
            past = PastBuild(job, reading);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (!past || !_released.TryAdd(job.Id, 0))
        {
            return;
        }
        lock (_rowLock)
        {
            if (state.Jobs().FirstOrDefault(j => j.Id == job.Id) is { PausedAt: null, State: JobState.Running } current)
            {
                state.PutJob(current with { Step = "build done, sibling clones released" });
            }
        }
        Pump();
    }

    /// <summary>The step's tag for a running job: it holds the sibling clones, or has released them; empty for a job with no clones.</summary>
    private string HoldTag(JobRow job) =>
        SiblingsFor(job).Count == 0 ? "" : _released.ContainsKey(job.Id) ? "[clones released] " : "[holds clones] ";

    /// <summary>What a holder's build has left, from how long the same kind of job took before; empty when there is no history.</summary>
    private string RemainingOf(JobRow? holder)
    {
        if (holder?.MemoryKey is not { } key)
        {
            return "";
        }
        var took = state.Jobs()
            .Where(j => j.State == JobState.Succeeded && j.MemoryKey == key && j.EndedAt is not null)
            .OrderByDescending(j => j.EndedAt)
            .Take(5)
            .Select(j => (j.EndedAt!.Value - j.StartedAt - j.PausedFor).Ticks)
            .ToList();
        if (took.Count == 0)
        {
            return "";
        }
        var left = TimeSpan.FromTicks(JobMemory.Median(took)) - (time.GetUtcNow() - holder.StartedAt);
        return left > TimeSpan.Zero ? $", about {Took(left)} left by past runs" : ", past runs say it is nearly done";
    }

    /// <summary>Why a job other than a benchmark may not start: a benchmark runs, or heads the queue waiting for quiet. Null when none does.</summary>
    private string? BenchHold(JobRow job)
    {
        if (job.Kind == JobKind.Bench)
        {
            return null;
        }
        var bench = state.Jobs().FirstOrDefault(j => j.Kind == JobKind.Bench && j.State == JobState.Running && _running.ContainsKey(j.Id))
            ?? (_queue.Count > 0 && _queue[0].Kind == JobKind.Bench ? _queue[0] : null);
        return bench is null ? null : $"a benchmark ({bench.Id}) holds the node";
    }

    private MemoryEstimate EstimateOf(JobRow job) => JobMemory.Estimate(state.Jobs(), JobKey.Parse(job.MemoryKey) ?? JobKey.Of(job.Command, job.Cwd));

    /// <summary>What the estimate rests on; when job-memory raises it, that too.</summary>
    private static string Basis(MemoryEstimate estimate, long need) =>
        need > estimate.Bytes ? $"{estimate.Describe()}, raised to the job-memory floor" : estimate.Describe();

    /// <summary>What a waiting job needs, as its step says it: the estimate with its median and peak, and the memory free now.</summary>
    private string Needs(JobRow job)
    {
        var estimate = EstimateOf(job);
        var need = Math.Max(estimate.Bytes, MinFreeMemory);
        var now = FreeMemory() is { } free ? $", {MemoryEstimate.Gigabytes(free)} free now" : "";
        return $"needs {MemoryEstimate.Gigabytes(need)} ({Basis(estimate, need)}){now}";
    }

    /// <summary>
    /// Starts a job detached from the node (<see cref="DetachedShell"/>: on Windows in a named job object of its own, inheriting
    /// only its log and NUL); null when it started, else why not.
    /// </summary>
    private string? Start(JobRow job)
    {
        DetachedShell? shell = null;
        JobProcess process;
        var exitPath = job.ExitPath ?? ExitPathOf(job.LogPath);
        try
        {
            // An exit file of an earlier run of this id would end the job at once: there is none.
            File.Delete(exitPath);
            shell = DetachedShell.For(job.Id, job.Command, job.Cwd, job.LogPath, exitPath, Priority, Flights.PlaneOutput.Variables(PlaneOutput, job.Cwd));
            var started = shell.Start();
            process = new JobProcess(started.Id, StartOf(started), started);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            shell?.Dispose();
            return ex.Message;
        }
        // A check never outlives the node, a crash included: its wrapper (and so whatever it starts) is held by a kill-on-close job.
        if (job.Kind == JobKind.Check && process.Child is { } wrapper && KillOnCloseJob.Hold(wrapper, Priority) is { } hold)
        {
            _holds[job.Id] = hold;
        }
        // What a later node needs to find it again, on the row before it is put. The scripts stay until the job has ended.
        job = job with { ExitPath = exitPath, Pid = process.Pid, ProcessStartedAt = process.StartedAt, ScriptPath = shell.Script, Shell = shell.Shell };
        // In _running before the Running row is put: a cancel that sees the row must find the process.
        _running[job.Id] = process;
        state.PutJob(job);
        _watches[job.Id] = Task.Run(() => WatchAsync(job, process, fromEnd: false), CancellationToken.None);
        return null;
    }

    /// <summary>Whether <paramref name="line"/> is the shell header the job's log opens with (<see cref="ShellScript.LogHeader"/>), which is no progress.</summary>
    private static bool IsShellHeader(string line, JobRow job) =>
        job.Shell is { } shell && line.TrimEnd() == ShellScript.LogHeader(shell);

    /// <summary>A job's step from its last lines: the last non-empty one, but never the shell header, so a job that wrote nothing has no step rather than its shell.</summary>
    private static string? StepOf(string[] lines, JobRow job) =>
        lines.LastOrDefault(l => l.Trim().Length > 0 && !IsShellHeader(l, job)) is { } l ? Clip(l) : null;

    /// <summary>When <paramref name="process"/> started, in UTC; now when it cannot be read (it ended already, and the watch finds it so).</summary>
    private DateTimeOffset StartOf(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return time.GetUtcNow();
        }
    }

    /// <summary>
    /// Ends a job's whole tree: on Windows by its named job object first, which holds a descendant whose parent already exited
    /// and is found by name by a node that did not start the job; then the wrapper's tree by parent pids, which is all Linux has.
    /// </summary>
    private void Kill(string id, JobProcess process)
    {
        lock (_rowLock)
        {
            if (state.Jobs().FirstOrDefault(j => j.Id == id) is { } row)
            {
                if (row.PausedAt is not null)
                {
                    // Thawed first, by pid through the same table: nothing is left frozen, and a frozen tree is never killed unseen.
                    SignalTree(id, process, stop: false);
                }
                DetachedShell.TerminateJob(id, row.LogPath);
            }
        }
        process.Kill();
    }

    /// <summary>Freezes or thaws the job's tree: every process read under it, and on Linux its process group; returns how many it reached.</summary>
    private int SignalTree(string id, JobProcess process, bool stop)
    {
        var reached = 0;
        // A freeze reads the tree again until a pass finds nothing it has not frozen: a process a running one started between
        // the read and its suspend (PIL537) would otherwise keep running through the pause. Each pid is suspended once, since
        // suspends nest on Windows and one resume must undo it. A thaw is one pass: nothing runs to start another.
        var seen = new HashSet<int>();
        for (var pass = 0; pass < (stop ? 8 : 1); pass++)
        {
            var fresh = false;
            foreach (var info in Tree.Read(id, process).Tree)
            {
                if (!seen.Add(info.Pid))
                {
                    continue;
                }
                fresh = true;
                if (stop ? ProcessTable.Suspend(info.Pid) : ProcessTable.Resume(info.Pid))
                {
                    reached++;
                }
            }
            if (!fresh)
            {
                break;
            }
        }
        // setsid made the wrapper its own group leader: its group is the job's, children that left the tree by parent pid included.
        if (ProcessTable.SignalGroup(process.Pid, stop))
        {
            reached++;
        }
        return reached;
    }

    /// <summary>The pid of a running job's wrapper, or null.</summary>
    internal int? PidOf(string id) => _running.TryGetValue(id, out var p) ? p.Pid : null;

    /// <summary><c>pause-job job/&lt;id&gt;</c>: freezes a running job's whole process tree; it keeps its slot and its timeout stops counting.</summary>
    public CommandResult Pause(CommandRequest command) => Freeze(command, pause: true);

    /// <summary><c>resume-job job/&lt;id&gt;</c>: thaws a paused job; the span it was paused is added to <see cref="JobRow.PausedFor"/>.</summary>
    public CommandResult Resume(CommandRequest command) => Freeze(command, pause: false);

    private CommandResult Freeze(CommandRequest command, bool pause)
    {
        var id = command.Target.StartsWith(Verbs.JobTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.JobTargetPrefix.Length..] : command.Target;
        lock (_rowLock)
        {
            var row = state.Jobs().FirstOrDefault(j => j.Id == id);
            if (row is null)
            {
                return state.Complete(command, $"There is no job {id}");
            }
            if (!_running.TryGetValue(id, out var process) || row.State != JobState.Running || _cancelled.ContainsKey(id))
            {
                return state.Complete(command, row.State == JobState.Queued ? $"job {id} is queued, not running: only a running job can be {(pause ? "paused" : "resumed")}" : $"job {id} is not running");
            }
            if (pause == (row.PausedAt is not null))
            {
                return state.Complete(command, pause ? $"job {id} is already paused" : $"job {id} is not paused");
            }
            var now = time.GetUtcNow();
            if (SignalTree(id, process, stop: pause) == 0)
            {
                return state.Complete(command, pause ? $"job {id}'s process could not be paused" : $"job {id}'s process could not be resumed");
            }
            state.PutJob(pause
                ? row with { PausedAt = now, Step = "paused" }
                : row with { PausedAt = null, PausedFor = row.PausedFor + (now - row.PausedAt!.Value), Step = "resumed" });
            return state.Complete(command, null);
        }
    }

    /// <summary>The row with the span it was paused folded into <see cref="JobRow.PausedFor"/> and no longer paused, as of <paramref name="at"/>.</summary>
    private static JobRow Unpaused(JobRow row, DateTimeOffset at) =>
        row.PausedAt is { } since ? row with { PausedAt = null, PausedFor = row.PausedFor + (at - since) } : row;

    /// <summary>How long a job has left of its timeout: the deadline is pushed out by every paused span, the open one included, so a paused job never runs out.</summary>
    private TimeSpan TimeLeft(JobRow job)
    {
        var now = time.GetUtcNow();
        var row = state.Jobs().FirstOrDefault(j => j.Id == job.Id) ?? job;
        var paused = row.PausedFor + (row.PausedAt is { } since ? now - since : TimeSpan.Zero);
        return job.StartedAt + job.Timeout + paused - now;
    }

    /// <summary>
    /// <c>cancel-job job/&lt;id&gt; [force]</c>: stops it, or takes it out of the queue; its owner is told, as of any job's end.
    /// With <c>force</c> the wrapper and every process the node has seen under it are killed at once, so one that outlived the
    /// wrapper and holds the job's output dies too, and the job ends <c>cancelled (forced) by &lt;who&gt;</c>.
    /// </summary>
    public CommandResult Cancel(CommandRequest command)
    {
        var id = command.Target.StartsWith(Verbs.JobTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.JobTargetPrefix.Length..] : command.Target;
        var args = command.Args ?? [];
        if (args.Any(a => !string.Equals(a, Verbs.ForceArg, StringComparison.OrdinalIgnoreCase)))
        {
            return state.Complete(command, $"cancel-job takes only `{Verbs.ForceArg}`, not {string.Join(' ', args)}");
        }
        return state.Complete(command, CancelJob(id, command.Who, args.Length > 0));
    }

    /// <summary>
    /// Cancels every queued or running job of <paramref name="flight"/> (its check, a worker's run) and every one owned by one of
    /// <paramref name="owners"/> (session row ids): a landed or merged flight leaves nothing of its own on the queue. Returns how many.
    /// </summary>
    public int CancelFor(string flight, IReadOnlyCollection<string> owners, string who)
    {
        var count = 0;
        foreach (var job in state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued
            && (string.Equals(j.Flight, flight, StringComparison.OrdinalIgnoreCase) || (j.Owner is not null && owners.Contains(j.Owner, StringComparer.Ordinal)))))
        {
            if (CancelJob(job.Id, who, force: false) is null)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Cancels every queued or running job owned by one of <paramref name="owners"/> (session row ids); returns how many.</summary>
    public int CancelOwnedBy(IReadOnlyCollection<string> owners, string who)
    {
        var count = 0;
        foreach (var job in state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued && j.Owner is not null && owners.Contains(j.Owner, StringComparer.Ordinal)))
        {
            if (CancelJob(job.Id, who, force: false) is null)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Takes the queued (not running) jobs owned by any of <paramref name="owners"/> (a session's row id, session id or earlier ids)
    /// off the queue, each ending Cancelled as <c>dropped: why</c> and told, so nobody is woken. A running job is left alone: its
    /// end goes to the replacement (#601). Returns how many were withdrawn.
    /// </summary>
    public int DropQueuedOf(IReadOnlyCollection<string> owners, string why)
    {
        var count = 0;
        foreach (var job in state.Jobs().Where(j => j.State == JobState.Queued && j.Owner is not null && owners.Contains(j.Owner, StringComparer.Ordinal)).ToList())
        {
            _signals.Writer.TryWrite(new Withdrawn(job.Id, "the node", why));
            count++;
        }
        return count;
    }

    /// <summary>Stops a running job or takes a queued one out; null when it did, else why not.</summary>
    private string? CancelJob(string id, string who, bool force)
    {
        if (_running.TryGetValue(id, out var process))
        {
            _cancelled[id] = who;
            if (force)
            {
                _forced[id] = 0;
                Kill(id, process);
                Tree.KillAll(id, process);
            }
            else
            {
                Kill(id, process);
            }
            Nudge(id);
            return null;
        }
        if (state.Jobs().FirstOrDefault(j => j.Id == id) is { State: JobState.Queued })
        {
            // The dispatcher owns the queue: it takes the job out, or stops it if it has started since.
            _signals.Writer.TryWrite(new Withdrawn(id, who));
            return null;
        }
        return state.Jobs().Any(j => j.Id == id) ? $"job {id} is not running" : $"There is no job {id}";
    }

    private void Nudge(string id)
    {
        if (_nudges.TryGetValue(id, out var nudge))
        {
            nudge.TrySetResult();
        }
    }

    /// <summary>
    /// What the job log read adds to the log's end: the wrapper's pid, the process tree under it as the OS has it (and what the
    /// node saw earlier that lives still), and, when the row reads running while the wrapper has exited and a process it
    /// started lives on, the fact that the job is held by it. A job that is not running has only its pid.
    /// </summary>
    public JobLog Describe(JobRow job, JobLog log)
    {
        JobProcess? process = null;
        if (job.State == JobState.Running)
        {
            process = _running.TryGetValue(job.Id, out var held) ? held : job is { Pid: { } pid, ProcessStartedAt: { } startedAt } ? new JobProcess(pid, startedAt) : null;
        }
        if (process is null)
        {
            return log with { Pid = job.Pid };
        }
        var reading = Tree.Read(job.Id, process);
        var outlived = reading.Held.ToList();
        // The ones nearest the wrapper hold it, whatever they started in turn.
        var stuck = outlived.Count > 0
            ? $"process exited, output still held by {string.Join(", ", outlived.Where(h => h.Depth == outlived.Min(x => x.Depth)).Select(h => h.Pid))}"
            : null;
        return log with { Pid = process.Pid, Tree = reading.Tree, Stuck = stuck };
    }

    private readonly ConcurrentDictionary<string, string> _cancelled = new(StringComparer.Ordinal);

    // The jobs whose cancel was forced, so their row says so.
    private readonly ConcurrentDictionary<string, byte> _forced = new(StringComparer.Ordinal);

    /// <summary>
    /// Follows a job by its files, not by pipes it holds: the log is read for new lines (they are the step, put on the stream
    /// now and then, and the last lines its owner is told), until the exit file appears or the process is gone. The exit code
    /// is the one in the exit file; the process is killed by pid, with its tree, on a cancel or when its timeout runs out.
    /// The node stopping only ends the watch: the job runs on and its row stays running, for the next node to follow again
    /// (<paramref name="fromEnd"/>: a job found again, whose log is followed from its end).
    /// </summary>
    private async Task WatchAsync(JobRow job, JobProcess process, bool fromEnd)
    {
        var exitPath = job.ExitPath ?? ExitPathOf(job.LogPath);
        var tail = new CircularBuffer<string>(TailLines);
        var follower = new LogFollower(job.LogPath, fromEnd);
        var lastStep = time.GetUtcNow();
        var timedOut = false;
        int? exitCode = null;
        // What the job's process tree held at each look at it: the run's median is its value in the estimate, its peak only shown.
        var memory = new List<long>();
        // The looks at the machine's CPU while this job ran, and how many found it above 90%.
        int looks = 0, hot = 0;

        void Follow(bool final)
        {
            foreach (var line in follower.Read(final))
            {
                tail.Add(line);
                // The step, put on the stream now and then: a long run shows it is alive, without a delta per line.
                var now = time.GetUtcNow();
                if (now - lastStep >= StepEvery && line.Trim().Length > 0 && !IsShellHeader(line, job))
                {
                    lastStep = now;
                    lock (_rowLock)
                    {
                        // The row as it is now (a pause may have marked it), and a paused job's step stays "paused".
                        if (state.Jobs().FirstOrDefault(j => j.Id == job.Id) is { PausedAt: null, State: JobState.Running } current)
                        {
                            state.PutJob(current with { Step = HoldTag(job) + Clip(line) });
                        }
                    }
                }
            }
        }

        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping))
        {
            // Counted from its start, as a node that found it again does: a restart does not give a job its time anew. Looked at
            // at every turn of the loop, as the row says how long it was paused, which moves the deadline.
            var nudge = _nudges[job.Id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextTree = time.GetUtcNow();
            try
            {
                while (true)
                {
                    // The processes under it are looked at now and then, so one that outlives the wrapper is still known.
                    if (time.GetUtcNow() >= nextTree)
                    {
                        nextTree = time.GetUtcNow() + TreeEvery;
                        var reading = Tree.Read(job.Id, process);
                        ReleaseIfPastBuild(job, reading);
                        // Nothing readable is not a sample of zero.
                        if (HeldBy(reading.Tree) is > 0 and var held)
                        {
                            memory.Add(held);
                        }
                        if (job.Kind == JobKind.Check && (MachineCpu is { } cpuNow ? cpuNow() : state.Machine()?.CpuPercent) is { } cpu)
                        {
                            looks++;
                            hot += cpu > SaturatedAbove ? 1 : 0;
                        }
                    }
                    // Sampled before the exit file is looked at: a process that was gone by now has left its exit file, if it was going to.
                    var alive = process.IsAlive();
                    Follow(final: false);
                    if (ReadExit(exitPath) is { } code)
                    {
                        exitCode = code;
                        break;
                    }
                    if (!alive)
                    {
                        break;
                    }
                    var left = TimeLeft(job);
                    if (left <= TimeSpan.Zero)
                    {
                        timedOut = true;
                        Kill(job.Id, process);
                        if (job.Kind == JobKind.Check)
                        {
                            Tree.KillAll(job.Id, process);
                        }
                        await GoneAsync(process);
                        exitCode = ReadExit(exitPath);
                        break;
                    }
                    // Between two looks, or sooner when a cancel says so, or the timeout is due.
                    using var pause = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                    var tick = Task.Delay(left < FollowEvery ? left : FollowEvery, time, pause.Token);
                    if (await Task.WhenAny(tick, nudge.Task) == tick)
                    {
                        await tick;
                    }
                    else
                    {
                        pause.Cancel();
                        nudge = _nudges[job.Id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                if (job.Kind == JobKind.Check)
                {
                    // A check is given up with the node: nobody will await it, and its tests must not hold the plane's files.
                    EndCheckForStop(job, process);
                    return;
                }
                // The node is stopping: the job is not, and its row stays as it is for the next node.
                process.Child?.Dispose();
                return;
            }
        }
        // The exit file is written by the wrapper just before it exits: the job is not ended while the wrapper still lives, or it
        // holds its cwd and log after the row says ended (a temp folder could not be deleted on Windows, #721).
        await GoneAsync(process);
        // What the job wrote last, after the process has gone, and the whole of a last line it left without a newline.
        Follow(final: true);
        // Ended: the scripts it ran are of no use now.
        DetachedShell.DeleteScripts(job.ScriptPath);
        ReleaseHold(job.Id);

        _running.TryRemove(job.Id, out _);
        _siblings.TryRemove(job.Id, out _);
        _released.TryRemove(job.Id, out _);
        _watches.TryRemove(job.Id, out _);
        _nudges.TryRemove(job.Id, out _);
        Tree.Forget(job.Id);
        // Its slot is free: the next in line may start.
        Pump();
        string[] last = [.. tail.Snapshot];
        // Ended while frozen (a cancel or timeout thaws it to kill it): the span counts, and it is no longer paused.
        var latest = Unpaused(state.Jobs().FirstOrDefault(j => j.Id == job.Id) ?? job, time.GetUtcNow());
        JobRow ended;
        if (_cancelled.TryRemove(job.Id, out var by))
        {
            ended = latest with { State = JobState.Cancelled, Error = _forced.TryRemove(job.Id, out _) ? $"cancelled (forced) by {by}" : $"cancelled by {by}" };
        }
        else if (timedOut)
        {
            ended = latest with { State = JobState.Failed, Error = $"{TimeoutPrefix} of {Took(job.Timeout)}" };
        }
        else if (exitCode is { } code)
        {
            ended = code == 0
                ? latest with { State = JobState.Succeeded, ExitCode = 0 }
                : latest with { State = JobState.Failed, ExitCode = code, Error = $"exit {code}" };
        }
        else
        {
            ended = latest with { State = JobState.Failed, Error = "its process ended without leaving an exit code" };
        }
        ended = ended with { Step = StepOf(last, job), EndedAt = time.GetUtcNow() };
        if (memory.Count > 0)
        {
            ended = ended with { MemoryMedian = JobMemory.Median(memory), MemoryPeak = memory.Max() };
        }
        // This watch tells it, so the sweep leaves it alone (marked before the row shows it ended).
        _tried[job.Id] = 0;
        state.PutJob(ended);
        if (looks > 0)
        {
            _saturation[job.Id] = (double)hot / looks;
        }
        try
        {
            Finish(ended, last);
            // Looked at again now this run is in the history: the look its slot gave may have been before it, with the older estimate.
            Pump();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The job's owner is told all the same.
        }
        await TellAsync(ended, CancellationToken.None, last);
        process.Child?.Dispose();
    }

    private void ReleaseHold(string id)
    {
        if (_holds.TryRemove(id, out var hold))
        {
            hold.Dispose();
        }
    }

    /// <summary>The node is stopping: a running check's whole tree is killed and its row ended, saying why.</summary>
    private void EndCheckForStop(JobRow job, JobProcess process)
    {
        Kill(job.Id, process);
        Tree.KillAll(job.Id, process);
        DetachedShell.DeleteScripts(job.ScriptPath);
        ReleaseHold(job.Id);
        _running.TryRemove(job.Id, out _);
        _watches.TryRemove(job.Id, out _);
        _nudges.TryRemove(job.Id, out _);
        Tree.Forget(job.Id);
        var latest = Unpaused(state.Jobs().FirstOrDefault(j => j.Id == job.Id) ?? job, time.GetUtcNow());
        var ended = latest with { State = JobState.Failed, Error = "the node stopped, so the check was ended", EndedAt = time.GetUtcNow() };
        state.PutJob(ended);
        Finish(ended, null);
        process.Child?.Dispose();
    }

    /// <summary>The memory the processes of a job's tree hold in all, in bytes; null when none of it could be read.</summary>
    private long? HeldBy(JobProcessInfo[] tree)
    {
        if (MemorySample is { } sample)
        {
            return sample(tree);
        }
        long? total = null;
        foreach (var process in tree)
        {
            if (ProcessTable.MemoryOf(process.Pid) is { } bytes)
            {
                total = (total ?? 0) + bytes;
            }
        }
        return total;
    }

    /// <summary>The exit code its wrapper left in <paramref name="path"/>; null while there is none (or it is not a number yet).</summary>
    private static int? ReadExit(string path)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Waits for a killed process to be gone, so its files can be read to the end and its script deleted.</summary>
    private static async Task GoneAsync(JobProcess process)
    {
        for (var i = 0; i < 200 && process.IsAlive(); i++)
        {
            await Task.Delay(50, CancellationToken.None);
        }
    }

    /// <summary>Says how the job ended to its owner, a real turn for an idle worker; a wake that fails rings, so the result is never lost in silence.</summary>
    internal async Task TellAsync(JobRow job, CancellationToken cancellationToken, string[]? last = null)
    {
        // Once only: a row already told (by this node, or one before it) is never told again.
        if (job.Owner is null || Say is null || job.Told || state.Jobs().FirstOrDefault(j => j.Id == job.Id) is { Told: true })
        {
            return;
        }
        var took = (job.EndedAt ?? time.GetUtcNow()) - job.StartedAt;
        var how = job.State switch
        {
            JobState.Succeeded => "succeeded",
            JobState.Cancelled => job.Error ?? "was cancelled",
            _ => $"failed: {job.Error}",
        };
        var lines = last is { Length: > 0 } ? "\nIts last lines:\n" + string.Join('\n', last.TakeLast(12)) : "";
        var text = $"Your job {job.Id} (`{job.Command}`, in {job.Cwd}) {how}, after {Took(took)}.{lines}\nThe whole output is in {job.LogPath}. Carry on from here.";
        var sessions = state.Sessions();
        var ownerRow = OwnerRow(job.Owner, sessions);
        var target = WakeTarget(job.Owner, job.OwnerName);
        if (ownerRow is not null)
        {
            // A session replaced by a later one of its role (a go-around, a restart) hands its jobs on: the end goes to the
            // current session, saying whose job it was (#601).
            var candidates = ownerRow.Role == SessionRole.Worker
                ? sessions.Where(s => s.Flight == ownerRow.Flight && s.Leg == ownerRow.Leg).ToArray()
                : sessions;
            if (Sessions.SessionMessenger.ReplacedBy(ownerRow, candidates) is { } replacement)
            {
                target = replacement.Id;
                text = $"Your predecessor {ownerRow.Name} ({ownerRow.Id}), the session you replaced, started this job; its result is passed on to you.\n{text}";
            }
            else if (EndedNormally(ownerRow) || EndedForGood?.Invoke(ownerRow.Id) == true)
            {
                // The owner ended (finished, crashed or stopped) and nothing took its place: nobody is waiting on this job.
                state.PutJob(job with { Told = true });
                return;
            }
        }
        else if (Ledger?.Find([job.Owner]) is { EndedAt: not null })
        {
            // No row for the owner, and the ledger says its session ended: nobody is waiting on this job.
            state.PutJob(job with { Told = true });
            return;
        }
        bool ok;
        string? error;
        var threw = false;
        try
        {
            var said = await Say(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(target), Args = [text], Who = LegWatch.Who }, cancellationToken);
            (ok, error) = (said.Ok, said.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The node is stopping: the row stays untold for the next node's sweep.
            return;
        }
        catch (Exception ex)
        {
            // A say that throws is a say that failed: it rings like a refused one (once), rather than faulting the caller
            // silently, and unlike a refusal it is tried again by the next sweep, as the fault may have been a passing one.
            _tried.TryRemove(job.Id, out _);
            threw = true;
            (ok, error) = (false, $"{ex.GetType().Name}: {ex.Message}");
            if (!_rang.TryAdd(job.Id, 0))
            {
                return;
            }
        }
        if (ok)
        {
            state.PutJob(job with { Told = true });
            // A card an earlier try rang ("could not be told") is out of date now it has been told.
            state.CloseCardsAbout(Verbs.JobTarget(job.Id), "told on a later try", UntoldKind);
        }
        else if (ownerRow is null && !threw)
        {
            // The node has no row for the owner and its session refused the say (it is gone): nobody is waiting on this job.
            // Marked told, quietly, so a session long gone is not rung for again on every node start.
            state.PutJob(job with { Told = true });
        }
        else
        {
            state.RaiseCard(CardBin.Ring, UntoldKind, Verbs.JobTarget(job.Id), $"{job.Id} ended but {job.OwnerName ?? job.Owner} could not be told: its session refused the say ({LegCommander.OneLine(error ?? "no reason given")})",
                $"{how}; the say was refused: {error}");
        }
    }

    private static string Took(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{Math.Max(0, (int)span.TotalSeconds)}s";

    private static string Clip(string line) => line.Trim() is { Length: > 200 } long_ ? long_[..200] + "…" : line.Trim();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The jobs are left running, and their rows Running: the next node finds them again. Each watch lets go of its job
        // (the stopping token ends it) and is waited for, so none acts on a row after the node is gone.
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(_watches.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        // Checks still waiting are dropped: their callers go with the node.
        foreach (var waiting in state.Jobs().Where(j => j.Kind == JobKind.Check && j.State == JobState.Queued && _mine.ContainsKey(j.Id)).ToList())
        {
            var ended = waiting with { State = JobState.Failed, Error = "the node stopped before the check started", Step = null, EndedAt = time.GetUtcNow() };
            state.PutJob(ended);
            Finish(ended, null);
        }
    }

    /// <summary>
    /// The row to wake with a job's end: its owner's, or, once that has ended, the newest live session of the owner's name.
    /// A worker Claude Code continued as a copy is a row of its own, and the first row's session has stopped, so waking it
    /// would leave the copy, which holds the later conversation, waiting on a job it never hears of (TIA1774's j3).
    /// </summary>
    private SessionRow? OwnerRow(string owner, SessionRow[] sessions) =>
        sessions.FirstOrDefault(s => s.Id == owner)
        ?? sessions.Where(s => s.SessionId == owner || s.EarlierSessionIds?.Contains(owner) == true).OrderByDescending(s => s.StartedAt).FirstOrDefault();

    /// <summary>
    /// Whether the tracker saw a session end on its own, for good (two polls, nothing replaced it), so no one waits on its jobs.
    /// A row that is merely not live (one the node restored, a blip) is not one, and its jobs still try the session. Set by the
    /// node; null in a bare runner (#327).
    /// </summary>
    public Func<string, bool>? EndedForGood { get; set; }

    private bool EndedNormally(SessionRow row) =>
        WindDown.Why(state, Ledger, row) is not null
        || Ledger?.Find([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []]) is { EndReason: SessionEndReasons.Finished };

    internal string WakeTarget(string owner, string? ownerName)
    {
        // By id only, never a namesake (#542): the owner's own row, or the row its id moved to (a continuation lists it among its
        // earlier ids), else the owner as recorded.
        var sessions = state.Sessions();
        if (sessions.FirstOrDefault(s => s.Id == owner) is not null)
        {
            return owner;
        }
        return sessions.Where(s => s.SessionId == owner || s.EarlierSessionIds?.Contains(owner) == true)
            .OrderByDescending(s => s.StartedAt).FirstOrDefault()?.Id ?? owner;
    }

}
