using System.Diagnostics;

namespace Pilot.Node.Tests;

/// <summary>
/// One reading of the test process. <paramref name="At"/> is time since the sampler's epoch (a stopwatch, so it never
/// goes backwards); <paramref name="PoolLag"/> is how long the sampler's latest pool probe waited for a pool thread
/// (still waiting, if it had not run yet); the rest are the runtime's own counters.
/// </summary>
/// <param name="Cpu">Total CPU time the process has used so far (all its threads).</param>
/// <param name="Contention">Monitor lock waits so far (a thread that had to wait for a lock another held).</param>
/// <param name="Completed">Pool work items finished so far.</param>
/// <param name="TickLate">How late the sampler's own thread (not a pool thread) woke for this tick; zero for a mark taken by a test.</param>
/// <param name="Starts">Child processes begun so far, as the tests' process-start hook saw them (<see cref="ProcessStall.NoteStart"/>).</param>
public readonly record struct StallSample(TimeSpan At, TimeSpan PoolLag, int Threads, long Queue, TimeSpan GcPause,
    TimeSpan Cpu = default, long Contention = 0, long Completed = 0, TimeSpan TickLate = default, long Starts = 0);

/// <summary>What the runtime reports about itself: the pool's thread count and queue length, total GC pause time, the process's CPU time, monitor lock waits, pool items finished and child processes begun, all so far.</summary>
public readonly record struct StallRuntime(int Threads, long Queue, TimeSpan GcPause,
    TimeSpan Cpu = default, long Contention = 0, long Completed = 0, long Starts = 0);

/// <summary>
/// A test's window on the sampler's clock. <paramref name="End"/> is null while the test still runs. A test running at
/// the same time as another is how a process-wide stall shows: it hits every test in flight.
/// </summary>
public sealed record TestWindow(string Name, TimeSpan Start, TimeSpan? End, bool TaggedSlow);

/// <summary>Another test that overlapped a window for at least <see cref="ProcessStall.AlsoSlowFloor"/>.</summary>
public sealed record AlsoSlow(string Name, TimeSpan Elapsed, bool Running, bool TaggedSlow);

/// <summary>What the process was doing over one test's window.</summary>
/// <param name="Window">The test's own time.</param>
/// <param name="MaxPoolLag">The longest wait any pool probe had over the window (a probe that began before the window counts whole).</param>
/// <param name="PoolStall">How much of the window the pool had a probe waiting for more than <see cref="ProcessStall.LagThreshold"/>.</param>
/// <param name="MinThreads">Fewest pool threads over the window.</param>
/// <param name="MaxThreads">Most pool threads over the window.</param>
/// <param name="MaxQueue">Longest pool queue over the window.</param>
/// <param name="GcPause">GC pause time that fell inside the window.</param>
/// <param name="AlsoSlow">Other tests that ran alongside for a while, longest first.</param>
/// <param name="StallCpu">CPU time the whole process used while the pool lagged (the stalled spans only).</param>
/// <param name="StallContention">Monitor lock waits while the pool lagged.</param>
/// <param name="StallCompleted">Pool work items finished while the pool lagged.</param>
/// <param name="LateTicks">How late the sampler's own thread woke, summed over the ticks of the window that were late by at least <see cref="ProcessStall.LagThreshold"/>.</param>
/// <param name="MaxTickLate">The latest the sampler's own thread woke over the window.</param>
/// <param name="StallStarts">Child processes begun while the pool lagged. A start is a synchronous call that holds its thread (and, on Windows, the runtime's one create-process lock) until the process exists.</param>
public sealed record StallReadings(
    TimeSpan Window, TimeSpan MaxPoolLag, TimeSpan PoolStall, int MinThreads, int MaxThreads, long MaxQueue,
    TimeSpan GcPause, IReadOnlyList<AlsoSlow> AlsoSlow,
    TimeSpan StallCpu = default, long StallContention = 0, long StallCompleted = 0,
    TimeSpan LateTicks = default, TimeSpan MaxTickLate = default, long StallStarts = 0)
{
    /// <summary>The time the process lost: the larger of the pool stall and the GC pause (a GC pause also stalls the pool, so they are not added).</summary>
    public TimeSpan Lost => PoolStall > GcPause ? PoolStall : GcPause;

    /// <summary>
    /// What took the time: the GC when its pause covers at least half of the pool stall (a stop-the-world pause stalls the
    /// pool too, so the lag is its consequence); otherwise the whole process starved of CPU when the sampler's own thread,
    /// which no pool block can hold up, woke late for at least half of it; otherwise pool starvation.
    /// </summary>
    public StallCause Cause => Lost == TimeSpan.Zero ? StallCause.None
        : GcPause * 2 >= PoolStall ? StallCause.Gc
        : LateTicks * 2 >= PoolStall ? StallCause.ProcessStarved
        : StallCause.PoolStarvation;
}

public enum StallCause
{
    None,
    PoolStarvation,
    Gc,
    ProcessStarved,
}

/// <summary>
/// Measures what a test process does while a test runs, to tell a slow test from a process-wide stall (#801: three
/// unrelated tests each took 15 s on CI at once, because the whole process stood still for about 14 s, and the one
/// that crossed the limit was blamed).
/// <para>
/// A <see cref="StallSampler"/> runs on its own thread, which is not a pool thread, so a starved pool cannot stop it.
/// About every <see cref="Interval"/> it queues a no-op work item to the pool and records how late that item ran (the
/// pool lag), with the pool's thread count, its queue length and the runtime's total GC pause time. Nothing here starts
/// a process or burns CPU: a few runtime property reads and one queued item per tick.
/// </para>
/// </summary>
public static class ProcessStall
{
    /// <summary>How often the sampler ticks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>A probe that waited longer than this counts the pool as lagging. A healthy pool runs it within a millisecond or two; a busy CI runner within tens.</summary>
    public static readonly TimeSpan LagThreshold = TimeSpan.FromMilliseconds(250);

    /// <summary>Another test counts as slow alongside a test when it overlapped the test's window for at least this long.</summary>
    public static readonly TimeSpan AlsoSlowFloor = TimeSpan.FromSeconds(1);

    /// <summary>Samples kept: ten minutes at one per <see cref="Interval"/>. A test over its limit is nowhere near that old.</summary>
    public const int Capacity = 6000;

    private static readonly long Epoch = Stopwatch.GetTimestamp();

    private static readonly Lazy<StallSampler> SharedSampler = new(StartShared);

    /// <summary>The sampler for this test assembly, started on first use and never stopped.</summary>
    public static StallSampler Shared => SharedSampler.Value;

    /// <summary>Time since the sampler's epoch, on the same clock as every <see cref="StallSample.At"/>.</summary>
    public static TimeSpan Now() => Stopwatch.GetElapsedTime(Epoch);

    private static long _starts;

    /// <summary>Counts one child process begun; the tests' process-start hook calls it before each start.</summary>
    public static void NoteStart() => Interlocked.Increment(ref _starts);

    /// <summary>The runtime's counters right now.</summary>
    public static StallRuntime ReadRuntime() => new(ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, GC.GetTotalPauseDuration(),
        Environment.CpuUsage.TotalTime, Monitor.LockContentionCount, ThreadPool.CompletedWorkItemCount, Interlocked.Read(ref _starts));

    /// <summary>Queues <paramref name="work"/> on the global pool queue, as a test's own tasks would be.</summary>
    public static void QueueToPool(Action work) => ThreadPool.UnsafeQueueUserWorkItem(static a => a(), work, preferLocal: false);

    private static StallSampler StartShared()
    {
        var sampler = new StallSampler(Now, QueueToPool, ReadRuntime, Capacity);
        var thread = new Thread(() =>
        {
            while (true)
            {
                sampler.Tick();
                Thread.Sleep(Interval);
            }
        })
        {
            // Not a pool thread: it has to keep ticking while the pool is starved. Background, so it never holds the run open.
            IsBackground = true,
            Name = "pilot-test-stall-sampler",
        };
        thread.Start();
        return sampler;
    }

    /// <summary>
    /// Summarises the window from <paramref name="start"/> to <paramref name="end"/> (readings taken when the test began
    /// and ended) out of the sampler's <paramref name="samples"/>. Pure: the same inputs give the same readings.
    /// </summary>
    /// <param name="others">Windows of other tests, the test itself left out; those overlapping this one for at least <see cref="AlsoSlowFloor"/> are named.</param>
    public static StallReadings Summarise(IReadOnlyList<StallSample> samples, StallSample start, StallSample end,
        IReadOnlyList<TestWindow> others)
    {
        var window = end.At > start.At ? end.At - start.At : TimeSpan.Zero;

        // The samples that fall inside the window, and the two marks. The start mark's lag belongs to the time before the
        // window, so it only counts for the counters, not for the lag.
        var inside = samples.Where(s => s.At >= start.At && s.At <= end.At).Append(end).ToList();
        var counters = inside.Append(start).ToList();

        var spans = new List<(TimeSpan From, TimeSpan To)>();
        foreach (var s in inside.Where(s => s.PoolLag >= LagThreshold))
        {
            var from = s.At - s.PoolLag;
            spans.Add((from > start.At ? from : start.At, s.At < end.At ? s.At : end.At));
        }

        var gc = end.GcPause - start.GcPause;

        // What the whole process did while the pool lagged: counters read at the two ends of each stalled span.
        var merged = Merge(spans);
        var ordered = counters.OrderBy(s => s.At).ToList();
        TimeSpan stallCpu = default;
        long stallContention = 0, stallCompleted = 0, stallStarts = 0;
        foreach (var (from, to) in merged)
        {
            var a = CountersAt(ordered, from);
            var b = CountersAt(ordered, to);
            stallCpu += Positive(b.Cpu - a.Cpu);
            stallContention += Math.Max(0, b.Contention - a.Contention);
            stallCompleted += Math.Max(0, b.Completed - a.Completed);
            stallStarts += Math.Max(0, b.Starts - a.Starts);
        }

        // The sampler's own thread is no pool thread: when it woke late too, nothing the pool did explains it.
        var late = inside.Where(s => s.TickLate >= LagThreshold).ToList();

        return new StallReadings(
            window,
            inside.Max(s => s.PoolLag),
            Length(merged),
            counters.Min(s => s.Threads),
            counters.Max(s => s.Threads),
            counters.Max(s => s.Queue),
            gc > TimeSpan.Zero ? gc : TimeSpan.Zero,
            AlsoSlowIn(others, start.At, end.At),
            stallCpu, stallContention, stallCompleted,
            late.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.TickLate),
            inside.Max(s => s.TickLate),
            stallStarts);
    }

    internal static TimeSpan Positive(TimeSpan time) => time > TimeSpan.Zero ? time : TimeSpan.Zero;

    /// <summary>The counters as last read at or before <paramref name="at"/> (the first reading when none is that old).</summary>
    private static StallSample CountersAt(List<StallSample> ordered, TimeSpan at)
    {
        var found = ordered[0];
        foreach (var sample in ordered)
        {
            if (sample.At > at)
            {
                break;
            }

            found = sample;
        }

        return found;
    }

    private static List<AlsoSlow> AlsoSlowIn(IReadOnlyList<TestWindow> others, TimeSpan from, TimeSpan to)
    {
        var found = new List<AlsoSlow>();
        foreach (var other in others)
        {
            var otherEnd = other.End ?? to;
            var overlap = (otherEnd < to ? otherEnd : to) - (other.Start > from ? other.Start : from);
            if (overlap >= AlsoSlowFloor)
            {
                found.Add(new AlsoSlow(other.Name, otherEnd - other.Start, other.End is null, other.TaggedSlow));
            }
        }

        found.Sort((a, b) => a.Elapsed != b.Elapsed ? b.Elapsed.CompareTo(a.Elapsed) : string.CompareOrdinal(a.Name, b.Name));
        return found;
    }

    /// <summary>The spans with overlaps joined, in order: consecutive ticks of one stalled probe all cover the same time.</summary>
    private static List<(TimeSpan From, TimeSpan To)> Merge(List<(TimeSpan From, TimeSpan To)> spans)
    {
        spans.Sort((a, b) => a.From.CompareTo(b.From));
        var merged = new List<(TimeSpan From, TimeSpan To)>();
        foreach (var span in spans)
        {
            if (span.To <= span.From)
            {
                continue;
            }

            if (merged.Count > 0 && span.From <= merged[^1].To)
            {
                var last = merged[^1];
                merged[^1] = (last.From, span.To > last.To ? span.To : last.To);
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    private static TimeSpan Length(List<(TimeSpan From, TimeSpan To)> merged)
        => merged.Aggregate(TimeSpan.Zero, (total, span) => total + (span.To - span.From));
}

/// <summary>
/// Takes readings of the process on a clock and a pool queue it is given, so the lag measure can be driven without a real
/// stall. <see cref="Tick"/> is what the sampler thread calls every <see cref="ProcessStall.Interval"/>; everything else
/// may be called from any thread.
/// </summary>
public sealed class StallSampler
{
    private readonly Func<TimeSpan> _clock;
    private readonly Action<Action> _queue;
    private readonly Func<StallRuntime> _read;
    private readonly StallSample[] _ring;
    private readonly object _gate = new();
    private int _next;
    private int _count;
    private Probe? _probe;
    private TimeSpan? _lastTick;

    public StallSampler(Func<TimeSpan> clock, Action<Action> queueToPool, Func<StallRuntime> readRuntime, int capacity)
    {
        _clock = clock;
        _queue = queueToPool;
        _read = readRuntime;
        _ring = new StallSample[capacity];
    }

    /// <summary>
    /// One tick: records the pool lag (the finished probe's wait, or how long the unfinished one has waited so far) with the
    /// runtime's counters, then queues a new probe if the last one finished. One probe is out at a time, so the pool is
    /// never asked for more than one no-op.
    /// </summary>
    public void Tick()
    {
        var now = _clock();
        lock (_gate)
        {
            var at = now;
            var lag = TimeSpan.Zero;
            if (_probe is { } probe)
            {
                if (probe.TryGetRanAt(out var ranAt))
                {
                    // Dated when it ran, not when the tick noticed, so the stalled span ends where the stall did.
                    at = ranAt;
                    lag = ranAt - probe.Queued;
                    _probe = null;
                }
                else
                {
                    lag = now - probe.Queued;
                }
            }

            // The sampler sleeps one interval between ticks; whatever it overslept is time this thread, which no pool block can
            // hold up, was kept from running.
            var tickLate = _lastTick is { } last ? ProcessStall.Positive(now - last - ProcessStall.Interval) : TimeSpan.Zero;
            _lastTick = now;

            var runtime = _read();
            Add(new StallSample(at, lag, runtime.Threads, runtime.Queue, runtime.GcPause, runtime.Cpu, runtime.Contention, runtime.Completed, tickLate, runtime.Starts));

            if (_probe is null)
            {
                var next = new Probe(now);
                _probe = next;
                _queue(() => next.Ran(_clock()));
            }
        }
    }

    /// <summary>A reading right now. Its lag is the unfinished probe's wait so far; a probe that finished since the last tick is left for that tick to record.</summary>
    public StallSample Take()
    {
        var now = _clock();
        var lag = TimeSpan.Zero;
        lock (_gate)
        {
            if (_probe is { } probe && !probe.TryGetRanAt(out _))
            {
                lag = now - probe.Queued;
            }
        }

        var runtime = _read();
        return new StallSample(now, lag, runtime.Threads, runtime.Queue, runtime.GcPause, runtime.Cpu, runtime.Contention, runtime.Completed, Starts: runtime.Starts);
    }

    /// <summary>The samples kept, oldest first.</summary>
    public StallSample[] Samples()
    {
        lock (_gate)
        {
            var copy = new StallSample[_count];
            var first = _count < _ring.Length ? 0 : _next;
            for (var i = 0; i < _count; i++)
            {
                copy[i] = _ring[(first + i) % _ring.Length];
            }

            return copy;
        }
    }

    private void Add(StallSample sample)
    {
        _ring[_next] = sample;
        _next = (_next + 1) % _ring.Length;
        _count = Math.Min(_count + 1, _ring.Length);
    }

    private sealed class Probe(TimeSpan queued)
    {
        private long _ranAtTicks = -1;

        public TimeSpan Queued { get; } = queued;

        public void Ran(TimeSpan at) => Volatile.Write(ref _ranAtTicks, at.Ticks);

        public bool TryGetRanAt(out TimeSpan at)
        {
            var ticks = Volatile.Read(ref _ranAtTicks);
            at = TimeSpan.FromTicks(ticks);
            return ticks >= 0;
        }
    }
}
