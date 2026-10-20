using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's jobs (<c>pilot run</c>): a long run handed over by a session, which then ends its turn and is woken with the
/// result, so no worker sits polling a test suite (v2's phase 2). A job is the node's own, as tianwen's are: it runs on the
/// node's lifetime, never a request's; it is on the stream like a flight; and only <c>cancel-job</c>, its timeout or the
/// node stopping end it early. Its owner is told how it ended, a real turn for an idle worker, and a wake that fails rings.
/// </summary>
/// <remarks>
/// A job whose node went away while it ran (a restart) cannot be found again: on start the node marks every job still
/// running as failed for that reason, and tells its owner, rather than leave a worker waiting for a result that will never
/// come.
/// </remarks>
public sealed class JobRunner(NodeState state, NodeOptions options, TimeProvider time) : BackgroundService
{
    public const string UntoldKind = "job-untold";

    /// <summary>How long a job may run unless its owner said otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    /// <summary>The longest a job may be given.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(8);

    private const int TailLines = 20;

    /// <summary>Says a job's end to its owner: <see cref="NodeCommands.RunAsync"/> with a <c>say</c>.</summary>
    public Func<CommandRequest, CancellationToken, Task<CommandResult>>? Say { get; set; }

    /// <summary>How often a running job's step (its last line) is put on the stream, so a long run shows it is alive without a delta per line.</summary>
    public TimeSpan StepEvery { get; init; } = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Process> _running = new(StringComparer.Ordinal);
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        _stopping = stopping;
        await RecoverAsync(stopping);
    }

    /// <summary>Marks the jobs a previous node left running as failed, and tells their owners.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var job in state.Jobs().Where(j => j.State == JobState.Running && !_running.ContainsKey(j.Id)))
        {
            var ended = job with { State = JobState.Failed, Error = "the node stopped while it ran, so its end was never seen", EndedAt = time.GetUtcNow() };
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
        var line = string.Join(' ', args.Skip(2)).Trim();
        if (line.Length == 0)
        {
            return state.Complete(command, "run needs the command to run");
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
            State = JobState.Running,
            LogPath = Path.Combine(folder, $"{jobId}.log"),
            Timeout = timeout,
            StartedAt = time.GetUtcNow(),
        };

        Process process;
        try
        {
            process = Start(job);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return state.Complete(command, $"the job did not start: {ex.Message}");
        }
        state.PutJob(job);
        _running[jobId] = process;
        _ = Task.Run(() => WatchAsync(job, process), CancellationToken.None);
        return state.Complete(command, null, made: jobId);
    }

    /// <summary><c>cancel-job job/&lt;id&gt;</c>: stops it; the watch records it cancelled and tells its owner.</summary>
    public CommandResult Cancel(CommandRequest command)
    {
        var id = command.Target.StartsWith(Verbs.JobTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.JobTargetPrefix.Length..] : command.Target;
        if (!_running.TryGetValue(id, out var process))
        {
            return state.Complete(command, state.Jobs().Any(j => j.Id == id) ? $"job {id} is not running" : $"There is no job {id}");
        }
        _cancelled[id] = command.Who;
        Kill(process);
        return state.Complete(command, null);
    }

    private readonly ConcurrentDictionary<string, string> _cancelled = new(StringComparer.Ordinal);

    private static Process Start(JobRow job)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", job.Command } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", job.Command } };
        start.WorkingDirectory = job.Cwd;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return Process.Start(start) ?? throw new InvalidOperationException($"`{job.Command}` did not start");
    }

    private async Task WatchAsync(JobRow job, Process process)
    {
        var tail = new Queue<string>();
        var lastStep = time.GetUtcNow();
        var timedOut = false;
        using (var log = new StreamWriter(job.LogPath, append: false) { AutoFlush = true })
        {
            void Keep(string? line)
            {
                if (line is null)
                {
                    return;
                }
                lock (tail)
                {
                    log.WriteLine(line);
                    tail.Enqueue(line);
                    while (tail.Count > TailLines)
                    {
                        tail.Dequeue();
                    }
                    // The step, put on the stream now and then: a long run shows it is alive, without a delta per line.
                    var now = time.GetUtcNow();
                    if (now - lastStep >= StepEvery && line.Trim().Length > 0)
                    {
                        lastStep = now;
                        state.PutJob(job with { Step = Clip(line) });
                    }
                }
            }
            process.OutputDataReceived += (_, e) => Keep(e.Data);
            process.ErrorDataReceived += (_, e) => Keep(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            limit.CancelAfter(job.Timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !_stopping.IsCancellationRequested;
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // The last lines are read once the process has gone and its pipes are drained.
            process.WaitForExit();
        }

        _running.TryRemove(job.Id, out _);
        string[] last;
        lock (tail)
        {
            last = [.. tail];
        }
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
        else if (_stopping.IsCancellationRequested)
        {
            ended = latest with { State = JobState.Failed, Error = "the node stopped while it ran" };
        }
        else
        {
            ended = process.ExitCode == 0
                ? latest with { State = JobState.Succeeded, ExitCode = 0 }
                : latest with { State = JobState.Failed, ExitCode = process.ExitCode, Error = $"exit {process.ExitCode}" };
        }
        ended = ended with { Step = last.LastOrDefault(l => l.Trim().Length > 0) is { } l ? Clip(l) : null, EndedAt = time.GetUtcNow() };
        state.PutJob(ended);
        await TellAsync(ended, CancellationToken.None, last);
        process.Dispose();
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
        var result = await Say(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(job.Owner), Args = [text], Who = LegWatch.Who }, cancellationToken);
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var process in _running.Values)
        {
            Kill(process);
        }
        await base.StopAsync(cancellationToken);
    }
}
