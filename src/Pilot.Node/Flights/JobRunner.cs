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

    /// <summary>How often a running job's step (its last line) is put on the stream, so a long run shows it is alive without a delta per line.</summary>
    public TimeSpan StepEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many jobs run at once; the rest wait their turn. Set live by <c>set node max-jobs</c>, then <see cref="Pump"/>.</summary>
    public int MaxRunning { get; set; } = 1;

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
    // What the dispatcher acts on, one at a time: a job handed over, a cancel of a waiting one, and "look again" (a job
    // ended, the timer for memory). No lock anywhere: everything that decides who runs happens on the dispatcher alone.
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    // The waiting jobs, in order. The dispatcher's alone: nothing else reads or writes it.
    private readonly List<JobRow> _queue = [];
    // Cancels that reached the dispatcher before their job did (Run puts the row, then hands the job over, and a cancel
    // can land between the two): the job is cancelled before its turn when it arrives. The dispatcher's alone.
    private readonly Dictionary<string, string> _withdrawnEarly = new(StringComparer.Ordinal);
    // Read-modify-write of a running job's row (pause, resume, the step): one at a time, so none loses another's change.
    private readonly object _rowLock = new();
    // The checks a caller awaits (RunCheckAsync), by job id: completed when the job ends however it ends.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CheckRun>> _checkWaiters = new(StringComparer.Ordinal);
    // The running checks' kill-on-close job objects (Windows): a node that crashes ends them, as a check never outlives its node.
    private readonly ConcurrentDictionary<string, KillOnCloseJob> _holds = new(StringComparer.Ordinal);
    // The share of looks at a running check at which the machine's CPU was above 90%, kept until its row ends.
    private readonly ConcurrentDictionary<string, double> _saturation = new(StringComparer.Ordinal);
    private const string TimeoutPrefix = "stopped after its timeout";
    private CancellationToken _stopping;
    // Completes once the startup recovery has run. ExecuteAsync runs in the background, so without it a caller sharing the
    // state cannot tell whether a Queued row it puts will be seen by that recovery and failed as orphaned.
    private readonly TaskCompletionSource _recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the startup recovery has run: from then on, only rows already present were judged orphaned.</summary>
    internal Task Recovered => _recovered.Task;

    internal abstract record Signal;

    internal sealed record HandedOver(JobRow Job) : Signal;

    internal sealed record Withdrawn(string Id, string Who) : Signal;

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
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

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
            Step = last.LastOrDefault(l => l.Trim().Length > 0) is { } l ? Clip(l) : null,
            EndedAt = EndOf(job, exitPath),
        };
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
    public async Task<CheckRun> RunCheckAsync(string flight, string leg, string command, string cwd, TimeSpan budget, CancellationToken cancellationToken)
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

    private void Finish(JobRow ended, string[]? last)
    {
        if (!_checkWaiters.TryRemove(ended.Id, out var waiter))
        {
            return;
        }
        var tail = last is { Length: > 0 } ? string.Join('\n', last.TakeLast(30)) : "";
        _saturation.TryRemove(ended.Id, out var share);
        waiter.TrySetResult(ended.State switch
        {
            JobState.Succeeded => new CheckRun(0, tail),
            _ when ended.Error?.StartsWith(TimeoutPrefix, StringComparison.Ordinal) == true =>
                new CheckRun(-1, $"the check ran past {ended.Timeout.TotalMinutes:0} minutes of running and was stopped") { StoppedByBudget = true, Saturation = share },
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
                CancelBeforeItsTurn(handed.Job, by);
                break;
            case HandedOver handed:
                _queue.Add(handed.Job);
                break;
            case Withdrawn withdrawn when _queue.FindIndex(j => j.Id == withdrawn.Id) is var at and >= 0:
                var waiting = _queue[at];
                _queue.RemoveAt(at);
                CancelBeforeItsTurn(waiting, withdrawn.Who);
                break;
            case Withdrawn withdrawn when _running.TryGetValue(withdrawn.Id, out var process):
                // It started before the cancel reached the dispatcher: stop it as any running job.
                _cancelled[withdrawn.Id] = withdrawn.Who;
                Kill(withdrawn.Id, process);
                break;
            case Withdrawn withdrawn when state.Jobs().FirstOrDefault(j => j.Id == withdrawn.Id) is { State: JobState.Queued }:
                // Its hand-over is still behind this in the channel: cancel it when it comes.
                _withdrawnEarly[withdrawn.Id] = withdrawn.Who;
                break;
        }

        while (_queue.Count > 0 && WhyWait(_queue[0]) is null)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            if (Start(next with { State = JobState.Running, Step = null, StartedAt = time.GetUtcNow() }) is { } failed)
            {
                var ended = next with { State = JobState.Failed, Error = $"it did not start: {failed}", EndedAt = time.GetUtcNow() };
                state.PutJob(ended);
                Finish(ended, null);
                _ = TellAsync(ended, _stopping);
            }
        }

        // Each waiting job says where it stands.
        for (var i = 0; i < _queue.Count; i++)
        {
            _queue[i] = _queue[i] with { Step = i == 0 ? $"next in line: {WhyWait(_queue[0])}" : $"queued, {i} ahead of it; {Needs(_queue[i])}" };
            state.PutJob(_queue[i]);
        }

        CheckIdle();
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

    private void CancelBeforeItsTurn(JobRow job, string who)
    {
        var cancelled = job with { State = JobState.Cancelled, Error = $"cancelled by {who} before its turn", Step = null, EndedAt = time.GetUtcNow() };
        state.PutJob(cancelled);
        Finish(cancelled, null);
        _ = TellAsync(cancelled, _stopping);
    }

    /// <summary>
    /// Why a job may not start now, or null when it may. With nothing running it always may (when a slot is allowed at all):
    /// the only memory it could wait for is what its own queue's jobs hold, and a head that waits for memory nothing will
    /// free is a queue that never moves. Otherwise it starts when the machine has free what its kind of command needs.
    /// </summary>
    private string? WhyWait(JobRow job)
    {
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
            shell = DetachedShell.For(job.Id, job.Command, job.Cwd, job.LogPath, exitPath);
            var started = shell.Start();
            process = new JobProcess(started.Id, StartOf(started), started);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            shell?.Dispose();
            return ex.Message;
        }
        // A check never outlives the node, a crash included: its wrapper (and so whatever it starts) is held by a kill-on-close job.
        if (job.Kind == JobKind.Check && process.Child is { } wrapper && KillOnCloseJob.Hold(wrapper) is { } hold)
        {
            _holds[job.Id] = hold;
        }
        // What a later node needs to find it again, on the row before it is put. The scripts stay until the job has ended.
        job = job with { ExitPath = exitPath, Pid = process.Pid, ProcessStartedAt = process.StartedAt, ScriptPath = shell.Script };
        // In _running before the Running row is put: a cancel that sees the row must find the process.
        _running[job.Id] = process;
        state.PutJob(job);
        _watches[job.Id] = Task.Run(() => WatchAsync(job, process, fromEnd: false), CancellationToken.None);
        return null;
    }

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
        foreach (var info in Tree.Read(id, process).Tree)
        {
            if (stop ? ProcessTable.Suspend(info.Pid) : ProcessTable.Resume(info.Pid))
            {
                reached++;
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
                if (now - lastStep >= StepEvery && line.Trim().Length > 0)
                {
                    lastStep = now;
                    lock (_rowLock)
                    {
                        // The row as it is now (a pause may have marked it), and a paused job's step stays "paused".
                        if (state.Jobs().FirstOrDefault(j => j.Id == job.Id) is { PausedAt: null, State: JobState.Running } current)
                        {
                            state.PutJob(current with { Step = Clip(line) });
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
        // What the job wrote last, after the process has gone, and the whole of a last line it left without a newline.
        Follow(final: true);
        // Ended: the scripts it ran are of no use now.
        DetachedShell.DeleteScripts(job.ScriptPath);
        ReleaseHold(job.Id);

        _running.TryRemove(job.Id, out _);
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
        ended = ended with { Step = last.LastOrDefault(l => l.Trim().Length > 0) is { } l ? Clip(l) : null, EndedAt = time.GetUtcNow() };
        if (memory.Count > 0)
        {
            ended = ended with { MemoryMedian = JobMemory.Median(memory), MemoryPeak = memory.Max() };
        }
        state.PutJob(ended);
        if (looks > 0)
        {
            _saturation[job.Id] = (double)hot / looks;
        }
        Finish(ended, last);
        // Looked at again now this run is in the history: the look its slot gave may have been before it, with the older estimate.
        Pump();
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
    private async Task TellAsync(JobRow job, CancellationToken cancellationToken, string[]? last = null)
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
        var result = await Say(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(WakeTarget(job.Owner, job.OwnerName)), Args = [text], Who = LegWatch.Who }, cancellationToken);
        if (result.Ok)
        {
            state.PutJob(job with { Told = true });
        }
        else
        {
            state.RaiseCard(CardBin.Ring, UntoldKind, Verbs.JobTarget(job.Id), $"{job.Id} ended but {job.OwnerName ?? job.Owner} could not be told",
                $"{how}; the say was refused: {result.Error}");
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
