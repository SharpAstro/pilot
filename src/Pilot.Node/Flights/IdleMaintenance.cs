using System.Globalization;
using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a maintenance step would remove: each item with its size and reason, and how many items each keep rule kept. <see cref="Bytes"/>
/// and <see cref="Items"/> are the totals of the list.
/// </summary>
public sealed record MaintenanceMeasure(IReadOnlyList<MaintenanceItem> List, IReadOnlyList<MaintenanceKept> Kept)
{
    /// <summary>A measure of the given items, with no keep rules counted.</summary>
    public MaintenanceMeasure(IReadOnlyList<MaintenanceItem> list) : this(list, [])
    {
    }

    /// <summary>A measure of <paramref name="items"/> anonymous items holding <paramref name="bytes"/> between them; for a step that lists nothing by name.</summary>
    public MaintenanceMeasure(long bytes, int items) : this([.. Enumerable.Range(0, items).Select(i => new MaintenanceItem($"item {i + 1}", i == 0 ? bytes : 0, ""))], [])
    {
    }

    /// <summary>The bytes the items hold.</summary>
    public long Bytes => List.Sum(i => i.Bytes);

    /// <summary>How many items.</summary>
    public int Items => List.Count;

    /// <summary>Counts of keep rules by name, in the order first seen, as the report carries them.</summary>
    public static MaintenanceKept[] Count(IEnumerable<string> rules) => [.. rules.GroupBy(r => r).Select(g => new MaintenanceKept(g.Key, g.Count()))];
}

/// <summary>What a maintenance step removed, and what it could not touch (a note for the disk alert), or null when it touched everything it meant to.</summary>
public sealed record MaintenanceResult(long Bytes, string? Untouched = null);

/// <summary>
/// One step of the idle-time maintenance sweep (PIL1102): <c>packages</c>, <c>git</c>, <c>lfs</c>, <c>jobs</c>. A step that starts a child
/// process starts it through <see cref="MaintenanceProcess"/>, so it runs at the job priority inside a kill-on-close job object and
/// its tree ends when the token is cancelled (work arrived).
/// </summary>
public interface IMaintenanceStep
{
    /// <summary>The name the report gives the step.</summary>
    string Name { get; }

    /// <summary>What <see cref="RunAsync"/> would remove, removing nothing (the dry run).</summary>
    Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken);

    /// <summary>Removes what it can prove unused; says the bytes freed and what it could not touch.</summary>
    Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>One step's line of a report.</summary>
/// <param name="Name">The step's name.</param>
/// <param name="Bytes">Bytes freed, or, for a dry run, bytes it would free.</param>
/// <param name="Untouched">What it could not touch, or null.</param>
/// <param name="Cancelled">Whether work arriving ended it before it was done.</param>
/// <param name="Items">For a dry run, how many items it would remove.</param>
/// <param name="Measured">For a dry run, the items and kept counts behind <paramref name="Bytes"/>.</param>
public sealed record MaintenanceStepReport(string Name, long Bytes, string? Untouched, bool Cancelled = false, int Items = 0, MaintenanceMeasure? Measured = null);

/// <summary>One run of the sweep: when, what each step did, and the free space before and after on the repos' drive.</summary>
public sealed record MaintenanceRun(DateTimeOffset At, bool DryRun, IReadOnlyList<MaintenanceStepReport> Steps, string? Drive, long? FreeBefore, long? FreeAfter, bool Cancelled, string? Stopped = null)
{
    /// <summary>The bytes freed (or, for a dry run, that would be) across the steps.</summary>
    public long Bytes => Steps.Sum(s => s.Bytes);

    /// <summary>What the steps could not touch, joined for the disk alert; null when nothing.</summary>
    public string? Untouched
    {
        get
        {
            var notes = Steps.Where(s => s.Untouched is { Length: > 0 }).Select(s => $"{s.Name}: {s.Untouched}").ToArray();
            return notes.Length > 0 ? string.Join("; ", notes) : null;
        }
    }

    /// <summary>The items and kept counts of each step, largest item first, for the client to print; the one-line way stays <see cref="Line"/>.</summary>
    public MaintenanceReport Report() => new(DryRun,
        [.. Steps.Select(s => new MaintenanceStepList(
            s.Name, s.Bytes,
            [.. (s.Measured?.List ?? []).OrderByDescending(i => i.Bytes).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)],
            [.. s.Measured?.Kept ?? []],
            s.Untouched))]);

    /// <summary>The report line: <c>maintenance: freed 9.2 GB (packages 8.1 GB, git 0.3 GB, lfs 0.2 GB, jobs 0.6 GB); C: 7.0 -> 16.2 GB free</c>.</summary>
    public string Line()
    {
        var verb = DryRun ? "maintenance (dry run): would free" : Cancelled ? $"maintenance (stopped: {Stopped ?? "work arrived"}): freed" : "maintenance: freed";
        var steps = string.Join(", ", Steps.Select(s => $"{s.Name} {IdleMaintenance.Gb(s.Bytes)}{(s.Cancelled ? " (stopped)" : "")}"));
        var text = $"{verb} {IdleMaintenance.Gb(Bytes)}{(steps.Length > 0 ? $" ({steps})" : "")}";
        if (!DryRun && FreeBefore is { } before && FreeAfter is { } after)
        {
            text += $"; {Drive} {IdleMaintenance.Gb(before, unit: false)} -> {IdleMaintenance.Gb(after)} free";
        }
        else if (FreeBefore is { } now)
        {
            text += $"; {Drive} {IdleMaintenance.Gb(now)} free";
        }
        return text;
    }
}

/// <summary>
/// The idle-time maintenance sweep (PIL1102, pilot #1102): once the node has been idle for <see cref="IdleFor"/> (no leg flying, no job
/// running or queued, no PR review round live) it runs its <see cref="IMaintenanceStep"/>s one by one, at most once per <see cref="Every"/>
/// (null is off). Work arriving cancels the running step within <see cref="WatchTick"/>. One Handled card says what each step freed and the
/// free space before and after. <c>pilot node maintenance [--dry-run]</c> runs it now (<see cref="RunNowAsync"/>). Not the reboot mode,
/// which is the <c>maintenance</c> setting.
/// </summary>
public sealed class IdleMaintenance(NodeState state, TimeProvider time, IEnumerable<IMaintenanceStep> steps) : BackgroundService
{
    /// <summary>What the Handled card is.</summary>
    public const string Kind = "maintenance-run";

    /// <summary>What the first-run For-you card is (PIL1114): the first sweep on a node waits for a person's look at a dry run.</summary>
    public const string FirstRunKind = "maintenance-first-run";

    /// <summary>The label of the first-run choice that saves maintenance-every as the default.</summary>
    public const string RunLabel = "Run on schedule";

    /// <summary>The label of the first-run choice that saves maintenance-every as off.</summary>
    public const string KeepOffLabel = "Keep off";

    /// <summary>How many of the largest items per step the first-run card lists.</summary>
    public const int FirstRunTop = 5;

    private readonly IMaintenanceStep[] _steps = [.. steps];
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly object _gate = new();
    private DateTimeOffset? _idleSince;
    private DateTimeOffset? _lastRunAt;
    private MaintenanceRun? _last;

    /// <summary>How often the idle state is looked at while waiting to start.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a running step is checked for work that arrived: well inside the second a cancel is owed.</summary>
    public TimeSpan WatchTick { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the node must have been idle before a run starts; read at each look, so a saved setting is in force at once.</summary>
    public Func<TimeSpan> IdleFor { get; init; } = () => NodeSettings.DefaultMaintenanceIdle;

    /// <summary>How long between runs, or null for off; read at each look.</summary>
    public Func<TimeSpan?> Every { get; init; } = () => NodeSettings.DefaultMaintenanceEvery;

    /// <summary>Whether a person has set maintenance-every (the saved value is not null); until then the first sweep waits for the first-run card (PIL1114).</summary>
    public Func<bool> EverySet { get; init; } = () => true;

    /// <summary>A path on the drive the report counts free space of (a repo's); null reads none and the report leaves the drive out.</summary>
    public Func<string?> DrivePath { get; init; } = () => null;

    /// <summary>The free space seam: free and total bytes and the drive's name of a path.</summary>
    public Func<string, (long Free, long Total, string Name)?> FreeSpace { get; init; } = DiskUsage.FreeSpace;

    /// <summary>The last run, as the disk alert reports it (when, per-step bytes, what stayed); null before the first. Kept in memory.</summary>
    public MaintenanceRun? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>The steps' names, in the order they run.</summary>
    public IReadOnlyList<string> StepNames => [.. _steps.Select(s => s.Name)];

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, time, stopping);
                await TickAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // One bad run is no reason to stop: the next look tries again.
            }
        }
    }

    /// <summary>
    /// One look: notes when the node became idle and, once it has been for <see cref="IdleFor"/> and the last run is older than
    /// <see cref="Every"/>, runs the sweep. Returns the run, or null when none started.
    /// </summary>
    public async Task<MaintenanceRun?> TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        DateTimeOffset since;
        lock (_gate)
        {
            if (WhyBusy() is not null)
            {
                _idleSince = null;
                return null;
            }
            _idleSince ??= now;
            since = _idleSince.Value;
        }
        var every = Every();
        if (_steps.Length == 0 || now - since < IdleFor())
        {
            return null;
        }
        if (!EverySet())
        {
            // Nothing real runs until a person has answered the first-run card (or set maintenance-every by hand).
            await RaiseFirstRunAsync(cancellationToken);
            return null;
        }
        if (every is null)
        {
            return null;
        }
        lock (_gate)
        {
            if (_lastRunAt is { } last && now - last < every)
            {
                return null;
            }
        }
        return await RunAsync(dryRun: false, cancellationToken);
    }

    /// <summary>
    /// The first sweep on a node waits for a look (PIL1114): one dry run, and one For-you card with its totals and the largest items per step.
    /// The open card is looked up by kind, so a later tick or a restarted service raises no second.
    /// </summary>
    private async Task RaiseFirstRunAsync(CancellationToken cancellationToken)
    {
        if (state.Cards(openOnly: true).Any(c => c.Kind == FirstRunKind))
        {
            return;
        }
        var run = await RunAsync(dryRun: true, cancellationToken);
        if (run is null || state.Cards(openOnly: true).Any(c => c.Kind == FirstRunKind))
        {
            return;
        }
        var report = run.Report();
        var detail = new System.Text.StringBuilder();
        foreach (var step in report.Steps)
        {
            detail.Append(CultureInfo.InvariantCulture, $"{step.Name}: {Gb(step.Bytes)} in {step.Items.Length} item(s)").Append('\n');
            foreach (var item in step.Items.Take(FirstRunTop))
            {
                detail.Append(CultureInfo.InvariantCulture, $"  {item.Name}: {Scaled(item.Bytes)}{(item.Reason.Length > 0 ? $" ({item.Reason})" : "")}").Append('\n');
            }
            if (step.Items.Length > FirstRunTop)
            {
                detail.Append(CultureInfo.InvariantCulture, $"  and {step.Items.Length - FirstRunTop} more").Append('\n');
            }
        }
        var every = NodeSettings.ArchiveDurationText(NodeSettings.DefaultMaintenanceEvery);
        detail.Append(CultureInfo.InvariantCulture, $"Nothing was removed. Run on schedule saves maintenance-every as {every}; Keep off saves it as off.");
        var items = report.Steps.Sum(s => s.Items.Length);
        state.RaiseCard(CardBin.ForYou, FirstRunKind, Verbs.NodeTarget,
            $"Run the first idle disk sweep? It would free {Gb(report.Bytes)} in {items} item(s)",
            detail.ToString(),
            [
                new CardChoice { Label = RunLabel, Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.MaintenanceEveryKey, every] },
                new CardChoice { Label = KeepOffLabel, Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.MaintenanceEveryKey, "off"] },
            ]);
    }

    /// <summary>The sweep now, ignoring the idle wait and the period; a real run is refused while a leg flies or a job runs, a dry run is not; the run, or why it did not start.</summary>
    public async Task<(MaintenanceRun? Run, string? Refused)> RunNowAsync(bool dryRun, CancellationToken cancellationToken)
    {
        // A dry run removes nothing, so work does not refuse it (PIL1114); a real run keeps the rule.
        if (!dryRun && WhyBusy() is { } busy)
        {
            return (null, $"not now: {busy}");
        }
        if (_steps.Length == 0)
        {
            return (null, "this node has no maintenance steps");
        }
        var run = await RunAsync(dryRun, cancellationToken);
        return run is null ? (null, "not now: a maintenance run is already going") : (run, null);
    }

    /// <summary>Why the node is not idle, or null when it is: a leg flying, a job running or queued, a PR review round live.</summary>
    public string? WhyBusy()
    {
        if (state.Legs().FirstOrDefault(l => l.State == LegState.Flying) is { } leg)
        {
            return $"a leg is flying ({leg.Flight}/{leg.Name})";
        }
        if (state.Jobs().FirstOrDefault(j => j.State is JobState.Queued or JobState.Running) is { } job)
        {
            return $"job {job.Id} is {(job.State == JobState.Queued ? "queued" : "running")}";
        }
        if (state.PrReviews().FirstOrDefault(r => r.State == PrReviewState.Reviewing || r is { FixRound: not null, FixerJob: not null, State: not PrReviewState.Closed }) is { } review)
        {
            return $"a review round is live on {review.Repo}#{review.Number}";
        }
        return null;
    }

    /// <summary>What arrived, worded for a run that stopped for it ('job j6410 started'), or null when the node is idle.</summary>
    private string? WhyStopped()
    {
        if (state.Legs().FirstOrDefault(l => l.State == LegState.Flying) is { } leg)
        {
            return $"a leg took off ({leg.Flight}/{leg.Name})";
        }
        if (state.Jobs().FirstOrDefault(j => j.State is JobState.Queued or JobState.Running) is { } job)
        {
            return $"job {job.Id} started";
        }
        if (state.PrReviews().FirstOrDefault(r => r.State == PrReviewState.Reviewing || r is { FixRound: not null, FixerJob: not null, State: not PrReviewState.Closed }) is { } review)
        {
            return $"a review round started on {review.Repo}#{review.Number}";
        }
        return null;
    }

    /// <summary>
    /// Runs the steps one by one. While it runs a timer looks for work arriving every <see cref="WatchTick"/> and cancels the step
    /// that is running (its process tree ends); the rest do not start. Raises the Handled card, remembers the run. Null when a run is
    /// already going.
    /// </summary>
    public async Task<MaintenanceRun?> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (!await _running.WaitAsync(0, cancellationToken))
        {
            return null;
        }
        try
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var drive = DrivePath() is { Length: > 0 } path ? FreeSpace(path) : null;
            var at = time.GetUtcNow();
            var reports = new List<MaintenanceStepReport>();
            string? stopped = null;
            ITimer? watch = null;
            // A dry run removes nothing and runs to the end whatever arrives (PIL1114): no watch.
            watch = dryRun ? null : time.CreateTimer(_ =>
            {
                try
                {
                    if (WhyStopped() is { } why)
                    {
                        stopped = why;
                        work.Cancel();
                        return;
                    }
                    watch?.Change(WatchTick, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // The run is over.
                }
            }, null, WatchTick, Timeout.InfiniteTimeSpan);
            var cancelled = false;
            try
            {
                foreach (var step in _steps)
                {
                    if (work.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    try
                    {
                        if (dryRun)
                        {
                            var measure = await step.MeasureAsync(work.Token);
                            reports.Add(new MaintenanceStepReport(step.Name, measure.Bytes, null, Items: measure.Items, Measured: measure));
                        }
                        else
                        {
                            var result = await step.RunAsync(work.Token);
                            reports.Add(new MaintenanceStepReport(step.Name, result.Bytes, result.Untouched));
                        }
                    }
                    catch (OperationCanceledException) when (work.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        reports.Add(new MaintenanceStepReport(step.Name, 0, $"stopped: {stopped ?? "work arrived"}", Cancelled: true));
                        cancelled = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        reports.Add(new MaintenanceStepReport(step.Name, 0, $"failed: {ex.Message}"));
                    }
                }
            }
            finally
            {
                watch?.Dispose();
            }
            var after = !dryRun && DrivePath() is { Length: > 0 } again ? FreeSpace(again) : null;
            var run = new MaintenanceRun(at, dryRun, reports, drive?.Name, drive?.Free, after?.Free ?? drive?.Free, cancelled, stopped);
            if (!dryRun)
            {
                lock (_gate)
                {
                    _last = run;
                    // A run that work cut short does not use up the period: the next idle window runs again.
                    if (!cancelled)
                    {
                        _lastRunAt = at;
                    }
                }
                state.RaiseCard(CardBin.Handled, Kind, Verbs.NodeTarget, run.Line(), run.Untouched is { } untouched ? $"Could not touch: {untouched}." : null);
            }
            return run;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Bytes as B, KB, MB or GB, so a small item does not read as 0.0 GB.</summary>
    public static string Scaled(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B" : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>Bytes as gigabytes with one decimal (<c>9.2 GB</c>), or just the number (<c>7.0</c>) when <paramref name="unit"/> is false.</summary>
    public static string Gb(long bytes, bool unit = true) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + (unit ? " GB" : "");
}
