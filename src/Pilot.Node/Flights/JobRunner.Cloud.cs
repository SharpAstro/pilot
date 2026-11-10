using System.Collections.Concurrent;
using System.Globalization;
using Node.Lib;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a cloud VM needs of a bench job besides its row (#823): the refs (null: the commit the job's folder is at), the repo that holds them, the
/// fetch step and the command as words. Kept in memory from the hand-over to the end, so a node restart drops it and a bench that waited through
/// one runs on this machine.
/// </summary>
public sealed record CloudBenchPlan
{
    /// <summary>The repo whose object database holds the refs, where the git bundle is made; null for a job of <c>run --alone</c>, whose folder is its checkout.</summary>
    public string? RepoPath { get; init; }

    /// <summary>The refs, in the order they run; null for a job of <c>run --alone</c>: the folder's HEAD, when the folder is a clean checkout.</summary>
    public IReadOnlyList<CloudBenchRef>? Refs { get; init; }

    /// <summary>The repo's fetch step, run on the VM in each ref's clone; null for none.</summary>
    public string? Fetch { get; init; }

    /// <summary>The benchmark command's words, run (by <c>bash</c>) in each ref's clone on the VM.</summary>
    public required IReadOnlyList<string> Command { get; init; }
}

/// <summary>
/// A benchmark on a spot VM (#823): a bench job that is not <c>--local</c>, with the cloud settings saved and the month's cap not spent, runs on
/// one VM through <see cref="ICloudBench"/> instead of waiting for a quiet CPU on this machine. Such a job is Running at once and holds no process
/// of this node, so it takes no job slot, waits for nothing and holds nothing: <see cref="BenchHold"/> and <see cref="BenchYields"/> read only the
/// jobs in <c>_running</c> and the queue, and it is in neither. Its output goes into the job's log as it comes, the VM it ran on goes on its row
/// when it ends, a spot eviction runs it again once, and a cancel or the node's stop deletes the VM before the row ends.
/// </summary>
public sealed partial class JobRunner
{
    /// <summary>What <see cref="BenchVm.LocalBecause"/> says of a bench kept on this machine with <c>--local</c>.</summary>
    public const string AskedLocalBecause = "asked for with --local";

    /// <summary>How many times a spot VM may run a bench in all: one eviction runs it again, a second ends it.</summary>
    public const int CloudAttempts = 2;

    /// <summary>The cloud runner; without one every bench runs on this machine, as before cloud benches.</summary>
    public ICloudBench? CloudBench { get; init; }

    /// <summary>Where a bench may run in Azure, read when the bench is handed over (the keys are in force at once): the saved settings file unless a test puts its own.</summary>
    public Func<NodeSettings> BenchSettings { get; init; } = () => NodeSettings.Read(options.SettingsFile);


    // What each handed-over bench needs to go to a VM, by job id.
    private readonly ConcurrentDictionary<string, CloudBenchPlan> _plans = new(StringComparer.Ordinal);

    // The running cloud benches: their cancel, by job id. They are not in _running, which is the node's own processes.
    private readonly ConcurrentDictionary<string, CloudRun> _cloud = new(StringComparer.Ordinal);

    // Why a queued bench stays on this machine when its step should say so (the cap is spent, nothing a VM needs was kept), by job id.
    private readonly ConcurrentDictionary<string, string> _localWhy = new(StringComparer.Ordinal);

    // Benches that came back from a cloud start to run on this machine, with why, by job id.
    private readonly ConcurrentDictionary<string, string> _forceLocal = new(StringComparer.Ordinal);

    /// <summary>The folder a cloud bench's key, bundle and script live in while its VM does, and its result files are left in: under the node's jobs folder.</summary>
    public static string CloudTmpOf(string dataFolder, string jobId) => Path.Combine(dataFolder, "jobs", jobId + ".tmp");

    private sealed class CloudRun(CancellationTokenSource cancel) : IDisposable
    {
        public CancellationToken Token => cancel.Token;

        public void Cancel()
        {
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended first.
            }
        }

        public void Dispose() => cancel.Dispose();
    }

    /// <summary>Where a bench goes: the cloud (with its settings), or this machine with why, and whether the bench's step says why.</summary>
    private sealed record Route(BenchCloud? Cloud, string? Why, bool SayWhy);

    private Route RouteOf(JobRow job)
    {
        if (_forceLocal.TryRemove(job.Id, out var instead))
        {
            return new Route(null, instead, SayWhy: true);
        }
        if (job.Local)
        {
            return new Route(null, AskedLocalBecause, SayWhy: false);
        }
        var benchSettings = BenchSettings();
        var (cloud, whyNot) = benchSettings.BenchCloudOrWhy();
        if (cloud is null)
        {
            // Cloud benches are not set up at all: a bench is as it was before them, with nothing new on its row or card. A cloud set up
            // by half (one of the subscription and resource group, or a service principal short of a part) says why it is off.
            var halfSet = benchSettings.BenchSubscription is { Length: > 0 } || benchSettings.BenchResourceGroup is { Length: > 0 };
            return halfSet ? new Route(null, whyNot, SayWhy: true) : new Route(null, null, SayWhy: false);
        }
        if (CloudBench is null)
        {
            return new Route(null, "this node has no cloud runner, so a bench runs on this machine", SayWhy: true);
        }
        if (!_plans.ContainsKey(job.Id))
        {
            return new Route(null, "the node restarted since the bench was queued, which lost what a VM needs of it, so it runs on this machine", SayWhy: true);
        }
        var now = time.GetUtcNow();
        var spent = MonthSpend(cloud, now);
        if (spent >= cloud.MonthlyCap)
        {
            return new Route(null, $"the cloud benches of {now.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} have cost ${Money(spent)} of the ${Money(cloud.MonthlyCap)} cap ({NodeSettings.BenchMonthlyCapKey}), so it runs on this machine", SayWhy: true);
        }
        return new Route(cloud, null, SayWhy: false);
    }

    private static string Money(double dollars) => dollars.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>
    /// What the cloud benches of the calendar month (UTC, as Azure bills) have cost, from the job rows: each ended bench's cost, and for one still
    /// running the hours it has run so far at its price (the settings' most when the price is not known yet), so benches started together cannot
    /// all pass under a cap that one of them would spend.
    /// </summary>
    private double MonthSpend(BenchCloud cloud, DateTimeOffset now) =>
        BenchSpend.Month(state.Jobs(), now, cloud.SpotPricePerHour, cloud.MonthlyCap).Cost;

    /// <summary>A queued bench's step, with why it stays on this machine after it when that is news (the cap, no plan for a VM).</summary>
    private string WithLocalNote(JobRow job, string step) =>
        _localWhy.TryGetValue(job.Id, out var why) ? $"{step}; it runs on this machine: {why}" : step;

    // The dispatcher's hand-over of a bench: to a VM at once, or into the queue for this machine with the reason on its row.
    private void HandBench(JobRow job)
    {
        var route = RouteOf(job);
        if (route.Cloud is { } cloud)
        {
            StartCloud(job, cloud);
            return;
        }
        if (route.SayWhy && route.Why is not null)
        {
            _localWhy[job.Id] = route.Why;
        }
        // A benchmark goes ahead of the other jobs, so the drain it waits for is not endless; benchmarks keep their own order.
        _queue.Insert(_queue.FindLastIndex(j => j.Kind == JobKind.Bench) + 1, route.Why is null ? job : job with { BenchVm = new BenchVm { LocalBecause = route.Why } });
    }

    private void StartCloud(JobRow queued, BenchCloud cloud)
    {
        var run = new CloudRun(CancellationTokenSource.CreateLinkedTokenSource(_stopping));
        var started = queued with
        {
            State = JobState.Running,
            Step = $"starting a spot VM ({cloud.VmSize}, {cloud.Region})",
            StartedAt = time.GetUtcNow(),
            BenchVm = new BenchVm { Size = cloud.VmSize, Image = cloud.Image, Region = cloud.Region },
        };
        _cloud[started.Id] = run;
        state.PutJob(started);
        _watches[started.Id] = Task.Run(() => RunCloudAsync(started, cloud, run), CancellationToken.None);
    }

    /// <summary>Stops a running cloud bench by its id: its VM is deleted before its row ends. False when it is not one.</summary>
    private bool CancelCloud(string id)
    {
        if (!_cloud.TryGetValue(id, out var run))
        {
            return false;
        }
        run.Cancel();
        return true;
    }

    // The job's whole life on a VM: plan, run (twice at most), end. Never throws: the row always ends, and its owner is told.
    private async Task RunCloudAsync(JobRow job, BenchCloud cloud, CloudRun run)
    {
        CloudBenchResult? result = null;
        string? crashed = null;
        var tail = new CircularBuffer<string>(TailLines);
        using var timeout = new CancellationTokenSource(job.Timeout, time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(run.Token, timeout.Token);
        CloudLogWriter? log = null;
        try
        {
            var (plan, why) = await PlanOfAsync(job, limit.Token);
            if (plan is null)
            {
                // Nothing of the VM exists yet, and the log is untouched: the job goes back to be run on this machine, saying why.
                BackToThisMachine(job, run, why!);
                return;
            }
            var writer = log = new CloudLogWriter(job.LogPath);
            var gate = new Lock();
            var lastStep = DateTimeOffset.MinValue;
            writer.Line($"=== cloud bench {job.Id} on a spot VM: {cloud.VmSize}, {cloud.Image}, {cloud.Region}, at most ${Money(cloud.SpotPricePerHour)} an hour; the VM is deleted when the bench ends ===");
            void OnLine(string line)
            {
                string? step = null;
                lock (gate)
                {
                    writer.Line(line);
                    tail.Add(line);
                    var now = time.GetUtcNow();
                    if (line.StartsWith(CloudBenches.EvictedPrefix, StringComparison.Ordinal))
                    {
                        lastStep = now;
                        step = "evicted by Azure: the bench is re-queued once, on a new VM";
                    }
                    else if (line.Trim().Length > 0 && now - lastStep >= StepEvery)
                    {
                        lastStep = now;
                        step = Clip(line);
                    }
                }
                if (step is not null)
                {
                    SetCloudStep(job.Id, step);
                }
            }
            var request = new CloudBenchRequest
            {
                JobId = job.Id,
                Cloud = cloud,
                RepoPath = plan.RepoPath!,
                Refs = plan.Refs!,
                Fetch = plan.Fetch,
                Command = plan.Command,
                TmpFolder = CloudTmpOf(options.DataFolder, job.Id),
                Log = OnLine,
            };
            result = await CloudBench!.RunWithRetryAsync(request, CloudAttempts, limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            // Ended before the runner could answer (while the plan was read): the cause is told below.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            crashed = $"the cloud bench could not run: {ex.Message.Split('\n')[0].Trim()}";
        }
        string[] last = [.. tail.Snapshot];
        EndCloud(job, cloud, run, result, crashed, last, log);
        log?.Dispose();
        await TellAsync(state.Jobs().FirstOrDefault(j => j.Id == job.Id) ?? job, _stopping, last);
    }

    private void SetCloudStep(string id, string step)
    {
        lock (_rowLock)
        {
            if (state.Jobs().FirstOrDefault(j => j.Id == id) is { State: JobState.Running } current)
            {
                state.PutJob(current with { Step = step });
            }
        }
    }

    // The row of a cloud bench that ended: how, with the VM it ran on. Told to its owner by the caller, once it is on the stream.
    private void EndCloud(JobRow job, BenchCloud cloud, CloudRun run, CloudBenchResult? result, string? crashed, string[] last, CloudLogWriter? log)
    {
        var now = time.GetUtcNow();
        var outcome = JobState.Failed;
        int? exit = null;
        string? error;
        _cancelled.TryRemove(job.Id, out var by);
        _forced.TryRemove(job.Id, out _);
        var stopped = NodeStopping;
        if (crashed is not null)
        {
            error = crashed;
        }
        else if (result is null || result.Outcome == CloudBenchOutcome.Cancelled)
        {
            // Cancelled by a person, the node stopping, or the timeout: told apart by what asked.
            if (by is not null)
            {
                (outcome, error) = (JobState.Cancelled, $"cancelled by {by}");
            }
            else if (stopped)
            {
                error = "the node stopped while the bench ran on its VM, so the VM was deleted and the bench has to be run again";
            }
            else
            {
                error = $"{TimeoutPrefix} of {Took(job.Timeout)}";
            }
        }
        else if (result.Outcome == CloudBenchOutcome.Succeeded)
        {
            (outcome, exit, error) = (JobState.Succeeded, 0, null);
        }
        else if (result.Outcome == CloudBenchOutcome.Evicted)
        {
            error = $"the spot VM was evicted by Azure twice (the bench was re-queued once and evicted again), so it was not run a third time: {result.Error ?? "Azure took the VM back"}";
        }
        else
        {
            exit = result.ExitCode;
            error = result.Started
                ? result.Error ?? (exit is { } code ? $"exit {code}" : "the bench failed on its VM")
                : $"the benchmark never started: the cloud bench stopped at {result.Stage.ToString().ToLowerInvariant()}: {result.Error ?? "no reason given"}";
        }
        if (result?.Leaked is { Count: > 0 } leaked)
        {
            var left = $"{leaked.Count} Azure resource{(leaked.Count == 1 ? "" : "s")} could not be deleted and cost money until a person deletes {(leaked.Count == 1 ? "it" : "them")}: {string.Join(", ", leaked)}";
            log?.Line($"=== {left} ===");
            error = error is null ? left : $"{error}; {left}";
        }
        _cloud.TryRemove(job.Id, out _);
        _watches.TryRemove(job.Id, out _);
        run.Dispose();
        _tried[job.Id] = 0;
        lock (_rowLock)
        {
            var latest = state.Jobs().FirstOrDefault(j => j.Id == job.Id) ?? job;
            var ended = latest with
            {
                State = outcome,
                ExitCode = exit,
                Error = error,
                Step = StepOf(last, job),
                EndedAt = now,
                BenchVm = result?.Vm ?? Billed(latest.BenchVm, job.StartedAt, cloud, now),
            };
            state.PutJob(ended);
            Finish(ended, last);
        }
        Pump();
    }

    // A run that ended with no result from the runner (it threw, or was cut off) still cost what its VM ran: the minutes since its start at its
    // price (the most the settings allow when none is known yet), as an upper bound, so the row and the month's spend are not short of it.
    private static BenchVm? Billed(BenchVm? vm, DateTimeOffset? started, BenchCloud? cloud, DateTimeOffset now)
    {
        if (vm is not { Size: not null, Cost: null } || started is not { } from)
        {
            return vm;
        }
        var minutes = Math.Max(0, (now - from).TotalMinutes);
        var price = vm.PricePerHour ?? cloud?.SpotPricePerHour ?? NodeSettings.DefaultBenchSpotPrice;
        return vm with { Minutes = minutes, Cost = minutes / 60 * price, PricePerHour = price, PriceIsUpperBound = true };
    }

    // A bench that cannot go to a VM (its folder is no clean checkout) is handed over again as one for this machine, which waits and holds as ever.
    private void BackToThisMachine(JobRow job, CloudRun run, string why)
    {
        _forceLocal[job.Id] = why;
        _cloud.TryRemove(job.Id, out _);
        _watches.TryRemove(job.Id, out _);
        run.Dispose();
        var back = (state.Jobs().FirstOrDefault(j => j.Id == job.Id) ?? job) with { State = JobState.Queued, Step = null, BenchVm = null, StartedAt = job.QueuedAt ?? job.StartedAt };
        state.PutJob(back);
        _signals.Writer.TryWrite(new HandedOver(back));
    }

    /// <summary>
    /// What a VM needs of the job: the plan it was handed over with, with the refs read from its folder when it has none (a <c>run --alone</c>
    /// job): the folder must be the root of a git checkout with nothing uncommitted, since the VM gets commits only. A null plan says why not.
    /// </summary>
    private async Task<(CloudBenchPlan? Plan, string? Why)> PlanOfAsync(JobRow job, CancellationToken cancellationToken)
    {
        if (!_plans.TryGetValue(job.Id, out var plan))
        {
            return (null, "the node restarted since the bench was queued, which lost what a VM needs of it, so it runs on this machine");
        }
        if (plan.Refs is not null && plan.RepoPath is not null)
        {
            return (plan, null);
        }
        var prefix = await Git.RunAsync(["rev-parse", "--show-prefix"], job.Cwd, cancellationToken);
        if (!prefix.Ok)
        {
            return (null, $"{job.Cwd} is not a git checkout, which a VM needs (it gets commits), so the bench runs on this machine");
        }
        if (prefix.Output.Trim().Length > 0)
        {
            return (null, $"{job.Cwd} is a folder inside its checkout, and a VM runs the command at the checkout's root, so the bench runs on this machine");
        }
        var status = await Git.RunAsync(["status", "--porcelain"], job.Cwd, cancellationToken);
        if (!status.Ok || status.Output.Trim().Length > 0)
        {
            return (null, $"{job.Cwd} has changes that are not committed, which a VM would not see, so the bench runs on this machine: commit them to run it on a VM");
        }
        var head = await Git.RunAsync(["rev-parse", "HEAD"], job.Cwd, cancellationToken);
        return head.Ok && head.Output.Trim() is { Length: > 0 } sha
            ? (plan with { RepoPath = job.Cwd, Refs = [new CloudBenchRef("ref", "HEAD", sha)] }, null)
            : (null, $"git could not read the commit {job.Cwd} is at, so the bench runs on this machine");
    }

    // A bench row a node that stopped or crashed left Running on a VM: its watch is gone with that node, but the names of what it made follow from the
    // job id and the subscription and resource group are in the settings, so this node deletes them (the same delete by name and sweep by prefix) and says what happened.
    private async Task<JobRow> EndLostCloudBenchAsync(JobRow job, CancellationToken cancellationToken)
    {
        var settings = BenchSettings();
        var prefix = CloudBenchNames.For(job.Id).Prefix;
        var (cloud, whyNot) = settings.BenchCloudOrWhy();
        var where = settings.BenchResourceGroup is { } group ? $" in the resource group {group}" : "";
        var vm = job.BenchVm;
        string error;
        if (cloud is null || CloudBench is null)
        {
            error = $"the node stopped while the bench ran on its VM, without deleting it, and the cloud settings that would let this node delete it are not there ({whyNot ?? "this node has no cloud runner"}): Azure resources named {prefix}*{where} may still be running and cost money until a person deletes them";
        }
        else
        {
            var lines = new List<string>();
            CloudCleanupResult cleanup;
            try
            {
                cleanup = await CloudBench.CleanUpAsync(new CloudCleanupRequest
                {
                    JobId = job.Id,
                    Cloud = cloud,
                    TmpFolder = CloudTmpOf(options.DataFolder, job.Id),
                    Log = line => { lock (lines) { lines.Add(line); } },
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cleanup = new CloudCleanupResult { Error = ex.Message.Split('\n')[0].Trim() };
            }
            AppendLog(job.LogPath, [$"=== the node that ran this bench stopped; the node that took the row up deletes its Azure resources named {prefix}* ===", .. lines]);
            // The VM may have run until now: its minutes and cost, from its start at its price (the settings' most when it is not known), as an upper bound.
            if (job.StartedAt is { } started && vm is not null)
            {
                var minutes = Math.Max(0, (time.GetUtcNow() - started).TotalMinutes);
                var price = vm.PricePerHour ?? cloud.SpotPricePerHour;
                vm = vm with { Minutes = minutes, Cost = minutes / 60 * price, PricePerHour = price, PriceIsUpperBound = true };
            }
            error = cleanup switch
            {
                { Clean: true } => $"the node stopped while the bench ran on its VM; the node that took the row up deleted the Azure resources named {prefix}*{where}{(cleanup.Swept.Count > 0 ? $" ({cleanup.Swept.Count} found by the sweep)" : "")}, so the bench has to be run again",
                { Error: { } why } => $"the node stopped while the bench ran on its VM, and this node could not delete what it made ({why}): Azure resources named {prefix}*{where} may still be running and cost money until a person deletes them",
                _ => $"the node stopped while the bench ran on its VM; this node could not delete {cleanup.Leaked.Count} of its Azure resources and they cost money until a person deletes them: {string.Join(", ", cleanup.Leaked)}",
            };
        }
        var ended = job with
        {
            State = JobState.Failed,
            Error = error,
            EndedAt = time.GetUtcNow(),
            BenchVm = vm,
        };
        _tried[job.Id] = 0;
        state.PutJob(ended);
        Finish(ended, null);
        return ended;
    }

    private static void AppendLog(string path, IEnumerable<string> lines)
    {
        try
        {
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The row says it all; a log that cannot be written is not the cleanup's failure.
        }
    }

    /// <summary>The job's log, written line by line from several threads while it is read by others.</summary>
    private sealed class CloudLogWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Lock _gate = new();

        public CloudLogWriter(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        }

        public void Line(string line)
        {
            lock (_gate)
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // A log that cannot be written is not the bench's failure: its row and the result still tell.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer.Dispose();
            }
        }
    }
}
