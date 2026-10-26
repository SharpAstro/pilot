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
/// node's lifetime, never a request's; it is on the stream like a flight; and only <c>cancel-job</c>, its timeout or the
/// node stopping end it early. Its owner is told how it ended, a real turn for an idle worker, and a wake that fails rings.
/// </summary>
/// <remarks>
/// <para><b>The machine is shared.</b> Several flights hand jobs to one node, and a full test suite can take gigabytes, so
/// jobs do not all start at once: at most <see cref="MaxRunning"/> run together, and one starts only while the machine
/// has <see cref="MinFreeMemory"/> free. The rest wait in the order they came, from every flight alike, each saying where
/// it stands in its step; their owners end their turns as for any job and are woken when it ends, so a wait costs them
/// nothing.</para>
/// <para>A job whose node went away while it ran or waited (a restart) cannot be found again: on start the node marks
/// every such job as failed for that reason, and tells its owner, rather than leave a worker waiting for a result that
/// will never come.</para>
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

    /// <summary>Says a job's end to its owner: <see cref="NodeCommands.RunAsync"/> with a <c>say</c>.</summary>
    public Func<CommandRequest, CancellationToken, Task<CommandResult>>? Say { get; set; }

    /// <summary>How often a running job's step (its last line) is put on the stream, so a long run shows it is alive without a delta per line.</summary>
    public TimeSpan StepEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for a job's exit file once its process has exited. The end is the process's, never its output's: a
    /// process the job started (a test's node, a git, a pwsh) that outlives it keeps the log open, and waiting on that
    /// kept SE1's j122 "running" for four and a half hours after its tests had passed (2026-10-26).
    /// </summary>
    public TimeSpan DrainGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How many jobs run at once; the rest wait their turn. Set live by <c>set node max-jobs</c>, then <see cref="Pump"/>.</summary>
    public int MaxRunning { get; set; } = 1;

    /// <summary>The free memory a job needs before it starts: a suite started on a machine already short of it takes the machine down with it. Set live by <c>set node job-memory</c>.</summary>
    public long MinFreeMemory { get; set; } = 2L << 30;

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

    private readonly ConcurrentDictionary<string, Process> _running = new(StringComparer.Ordinal);
    // Where each running job's idle window began and the CPU it had used then. The dispatcher's alone.
    private readonly Dictionary<string, (DateTimeOffset At, TimeSpan Cpu, TimeSpan Last)> _cpuMarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessTreeCpu.Tally> _cpuTallies = new(StringComparer.Ordinal);
    // Every job this node handed over, so a recovery knows which ones a previous node left behind.
    private readonly ConcurrentDictionary<string, byte> _mine = new(StringComparer.Ordinal);
    // What the dispatcher acts on, one at a time: a job handed over, a cancel of a waiting one, and "look again" (a job
    // ended, the timer for memory). No lock anywhere: everything that decides who runs happens on the dispatcher alone.
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    // The waiting jobs, in order. The dispatcher's alone: nothing else reads or writes it.
    private readonly List<JobRow> _queue = [];
    // Cancels that reached the dispatcher before their job did (Run puts the row, then hands the job over, and a cancel
    // can land between the two): the job is cancelled before its turn when it arrives. The dispatcher's alone.
    private readonly Dictionary<string, string> _withdrawnEarly = new(StringComparer.Ordinal);
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

    /// <summary>Marks the jobs a previous node left running or waiting as failed, and tells their owners.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var job in state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued && !_mine.ContainsKey(j.Id)))
        {
            var ended = job with
            {
                State = JobState.Failed,
                Error = job.State == JobState.Queued ? "the node stopped before its turn came" : "the node stopped while it ran, so its end was never seen",
                EndedAt = time.GetUtcNow(),
            };
            state.PutJob(ended);
            await TellAsync(ended, cancellationToken);
        }
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
            LogPath = Path.Combine(folder, $"{jobId}.log"),
            Timeout = timeout,
            StartedAt = time.GetUtcNow(),
        };
        job = job with { QueuedAt = job.StartedAt };

        // Handed to the dispatcher, which starts it at once when nothing waits before it and the machine has room.
        _mine[jobId] = 0;
        state.PutJob(job);
        _signals.Writer.TryWrite(new HandedOver(job));
        return state.Complete(command, null, made: jobId);
    }

    /// <summary>Whether the job's process is held as running: what a cancel looks for.</summary>
    internal bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>The running jobs' root processes, as (job id, pid): what the machine sampler marks a job's tree by.</summary>
    internal List<(string Id, int Pid)> RunningPids()
    {
        var result = new List<(string, int)>();
        foreach (var (id, process) in _running)
        {
            try
            {
                result.Add((id, process.Id));
            }
            catch (InvalidOperationException)
            {
                // Ended and released since it was listed.
            }
        }
        return result;
    }

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
                Kill(process);
                break;
            case Withdrawn withdrawn when state.Jobs().FirstOrDefault(j => j.Id == withdrawn.Id) is { State: JobState.Queued }:
                // Its hand-over is still behind this in the channel: cancel it when it comes.
                _withdrawnEarly[withdrawn.Id] = withdrawn.Who;
                break;
        }

        while (_queue.Count > 0 && WhyWait() is null)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            if (Start(next with { State = JobState.Running, Step = null, StartedAt = time.GetUtcNow() }) is { } failed)
            {
                var ended = next with { State = JobState.Failed, Error = $"it did not start: {failed}", EndedAt = time.GetUtcNow() };
                state.PutJob(ended);
                _ = TellAsync(ended, _stopping);
            }
        }

        // Each waiting job says where it stands.
        var why = _queue.Count > 0 ? WhyWait() : null;
        for (var i = 0; i < _queue.Count; i++)
        {
            _queue[i] = _queue[i] with { Step = i == 0 ? $"next in line: {why}" : $"queued, {i} ahead of it" };
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
        foreach (var id in _running.Keys)
        {
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
                return ProcessTreeCpu.Of(p, tally);
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
        _ = TellAsync(cancelled, _stopping);
    }

    /// <summary>Why a job may not start now, or null when it may.</summary>
    private string? WhyWait()
    {
        if (_running.Count >= MaxRunning)
        {
            return MaxRunning == 1 ? "another job is running" : $"{_running.Count} jobs are running, {MaxRunning} at a time";
        }
        return FreeMemory() is { } free && free < MinFreeMemory
            ? $"waiting for {Gigabytes(MinFreeMemory)} of free memory ({Gigabytes(free)} now)"
            : null;
    }

    /// <summary>Starts a job, detached from the node; null when it started, else why not.</summary>
    private string? Start(JobRow job)
    {
        DetachedShell? shell = null;
        Process process;
        var exitPath = job.LogPath + ".exit";
        try
        {
            File.Delete(exitPath);
            shell = DetachedShell.For(job.Command, job.Cwd, job.LogPath, exitPath);
            process = shell.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            shell?.Dispose();
            return ex.Message;
        }
        DateTimeOffset? began = null;
        try
        {
            began = process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already: the watch finds it ended.
        }
        var running = job with { Pid = process.Id, ProcessStartedAt = began, ExitPath = exitPath };
        // In _running before the Running row is put: a cancel that sees the row must find the process.
        _running[job.Id] = process;
        state.PutJob(running);
        _ = Task.Run(() => WatchAsync(running, process, shell), CancellationToken.None);
        return null;
    }

    private static string Gigabytes(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

    /// <summary><c>cancel-job job/&lt;id&gt;</c>: stops it, or takes it out of the queue; its owner is told, as of any job's end.</summary>
    public CommandResult Cancel(CommandRequest command)
    {
        var id = command.Target.StartsWith(Verbs.JobTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.JobTargetPrefix.Length..] : command.Target;
        if (_running.TryGetValue(id, out var process))
        {
            _cancelled[id] = command.Who;
            Kill(process);
            return state.Complete(command, null);
        }
        if (state.Jobs().FirstOrDefault(j => j.Id == id) is { State: JobState.Queued })
        {
            // The dispatcher owns the queue: it takes the job out, or stops it if it has started since.
            _signals.Writer.TryWrite(new Withdrawn(id, command.Who));
            return state.Complete(command, null);
        }
        return state.Complete(command, state.Jobs().Any(j => j.Id == id) ? $"job {id} is not running" : $"There is no job {id}");
    }

    private readonly ConcurrentDictionary<string, string> _cancelled = new(StringComparer.Ordinal);

    /// <summary>How often a running job's log is read and its process looked at.</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(100);

    private async Task WatchAsync(JobRow job, Process process, DetachedShell shell)
    {
        // The job writes its own log; the node follows the file for the last lines and the step. Nothing is held open
        // between them, so the node ending leaves the job running and its end to be read from the exit file.
        var tail = new CircularBuffer<string>(TailLines);
        using var follower = new LogFollower(job.LogPath);
        var lastStep = time.GetUtcNow();
        void Take(IEnumerable<string?> lines)
        {
            foreach (var line in lines)
            {
                if (line is null)
                {
                    continue;
                }
                tail.Add(line);
                // The step, put on the stream now and then: a long run shows it is alive, without a delta per line.
                var now = time.GetUtcNow();
                if (now - lastStep >= StepEvery && line.Trim().Length > 0)
                {
                    lastStep = now;
                    state.PutJob(job with { Step = Clip(line) });
                }
            }
        }

        var timedOut = false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        limit.CancelAfter(job.Timeout);
        while (true)
        {
            Take(follower.ReadNew());
            if (HasExited(process))
            {
                break;
            }
            try
            {
                await Task.Delay(PollEvery, limit.Token);
            }
            catch (OperationCanceledException)
            {
                if (_stopping.IsCancellationRequested)
                {
                    // The node is stopping: the job is not its to end. It runs on and writes its exit file.
                    process.Dispose();
                    return;
                }
                timedOut = true;
                Kill(process);
                for (var i = 0; i < 100 && !HasExited(process); i++)
                {
                    await Task.Delay(PollEvery, CancellationToken.None);
                }
                break;
            }
        }
        Take(follower.ReadNew());
        Take([follower.Flush()]);

        _running.TryRemove(job.Id, out _);
        // Its slot is free: the next in line may start.
        Pump();
        string[] last = [.. tail.Snapshot];
        var latest = state.Jobs().FirstOrDefault(j => j.Id == job.Id) ?? job;
        JobRow ended;
        if (_cancelled.TryRemove(job.Id, out var by))
        {
            ended = latest with { State = JobState.Cancelled, Error = $"cancelled by {by}" };
        }
        else if (timedOut)
        {
            ended = latest with { State = JobState.Failed, Error = $"stopped after its timeout of {Took(job.Timeout)}" };
        }
        else if (await ReadExitAsync(job.ExitPath!) is not { } code)
        {
            ended = latest with { State = JobState.Failed, Error = "the job's process ended and recorded no exit code" };
        }
        else
        {
            ended = code == 0
                ? latest with { State = JobState.Succeeded, ExitCode = 0 }
                : latest with { State = JobState.Failed, ExitCode = code, Error = $"exit {code}" };
        }
        ended = ended with { Step = last.LastOrDefault(l => l.Trim().Length > 0) is { } l ? Clip(l) : null, EndedAt = time.GetUtcNow() };
        state.PutJob(ended);
        shell.Dispose();
        await TellAsync(ended, CancellationToken.None, last);
        process.Dispose();
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// The exit code the job's wrapper wrote, or null when it wrote none. The file is written before the wrapper's process
    /// ends, so it is there when the process is gone; <see cref="DrainGrace"/> only allows for a file system slow to show it.
    /// </summary>
    private async Task<int?> ReadExitAsync(string exitPath)
    {
        var waited = Stopwatch.StartNew();
        do
        {
            try
            {
                if (int.TryParse((await File.ReadAllTextAsync(exitPath)).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    return code;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not there yet.
            }
            await Task.Delay(PollEvery, CancellationToken.None);
        }
        while (waited.Elapsed < DrainGrace);
        return null;
    }

    /// <summary>Says how the job ended to its owner, a real turn for an idle worker; a wake that fails rings, so the result is never lost in silence.</summary>
    private async Task TellAsync(JobRow job, CancellationToken cancellationToken, string[]? last = null)
    {
        if (job.Owner is null || Say is null)
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

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already.
        }
    }

    private static string Took(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{Math.Max(0, (int)span.TotalSeconds)}s";

    private static string Clip(string line) => line.Trim() is { Length: > 200 } long_ ? long_[..200] + "…" : line.Trim();

    // StopAsync is the base one: stopping the node does not stop its jobs. They are detached, and the row of each carries the
    // pid and exit file a later node follows them by.

    /// <summary>
    /// The row to wake with a job's end: its owner's, or, once that has ended, the newest live session of the owner's name.
    /// A worker Claude Code continued as a copy is a row of its own, and the first row's session has stopped, so waking it
    /// would leave the copy, which holds the later conversation, waiting on a job it never hears of (TIA1774's j3).
    /// </summary>
    internal string WakeTarget(string owner, string? ownerName)
    {
        var sessions = state.Sessions();
        if (sessions.FirstOrDefault(s => s.Id == owner) is { State: not SessionState.Ended } || ownerName is not { Length: > 0 } name)
        {
            return owner;
        }
        return sessions.Where(s => s.State != SessionState.Ended && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.StartedAt).FirstOrDefault()?.Id ?? owner;
    }

}
