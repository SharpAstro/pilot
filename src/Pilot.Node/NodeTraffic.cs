using System.Diagnostics;
using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

/// <summary>
/// Counts the node's own network use, so its share can be told from the machine's (<see cref="MachineRow"/>'s net gauge):
/// totals since start and per UTC clock hour, and once an hour one line in node.log (<see cref="TrafficLog"/>).
/// </summary>
/// <remarks>
/// What is counted, and how exact:
/// <list type="bullet">
/// <item>The node's own HTTP (NuGet feed, Shortcut tracker), by <see cref="CountingHandler"/>: the request line and headers
/// as written plus the body length, and the response status line, headers and body. A response that was compressed on the
/// wire is counted decompressed, so the figure is an upper bound there.</item>
/// <item>The git and gh children (<see cref="Start"/>), whose traffic no portable per-process counter gives. On Windows
/// each child is started into an anonymous job object (so its own children, git-remote-https and git-lfs, are in it) and
/// the job's <c>OtherTransferCount</c> (JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION) is read once the child ended:
/// socket I/O is counted there as "other" transfer, but so is any other non-read, non-write I/O, so it is an <b>upper
/// estimate</b>, in and out together. A grandchild that left before the child was put in the job is missed. On Linux
/// nothing per process is readable, so the bytes are unknown (null) and only the runs are counted.</item>
/// </list>
/// Wherever the figures are shown the child part is labelled an estimate.
/// </remarks>
public sealed class NodeTraffic
{
    /// <summary>The node's one counter: the static git and gh helpers cannot take it from the container, so they add here.</summary>
    public static NodeTraffic Shared { get; } = new();

    /// <summary>One hour's counts, as a snapshot.</summary>
    public sealed record Totals(long HttpIn, long HttpOut, long ChildBytes, bool ChildKnown, int GitRuns, int GhRuns);

    private sealed class Bucket
    {
        public long HttpIn, HttpOut, ChildBytes;
        public bool ChildKnown;
        public int GitRuns, GhRuns;
    }

    private readonly Lock _gate = new();
    private readonly SortedDictionary<DateTimeOffset, Bucket> _hours = [];
    private DateTimeOffset? _logged;
    private long _httpIn, _httpOut, _childBytes;
    private int _gitRuns, _ghRuns;

    /// <summary>The clock the buckets are cut by; a test sets a fake one.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public long HttpInSinceStart => Interlocked.Read(ref _httpIn);

    public long HttpOutSinceStart => Interlocked.Read(ref _httpOut);

    public long ChildBytesSinceStart => Interlocked.Read(ref _childBytes);

    public int GitRunsSinceStart => Volatile.Read(ref _gitRuns);

    public int GhRunsSinceStart => Volatile.Read(ref _ghRuns);

    /// <summary>Whether a child's bytes can be read on this platform (Windows job objects); elsewhere they are unknown.</summary>
    public static bool CanMeasureChildren => OperatingSystem.IsWindows();

    public static DateTimeOffset HourOf(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Adds bytes the node's own HTTP received and sent.</summary>
    public void AddHttp(long received, long sent)
    {
        Interlocked.Add(ref _httpIn, received);
        Interlocked.Add(ref _httpOut, sent);
        lock (_gate)
        {
            var bucket = Current();
            bucket.HttpIn += received;
            bucket.HttpOut += sent;
        }
    }

    /// <summary>Adds one finished git or gh run and the bytes its tree moved (an estimate); null where the platform cannot say.</summary>
    public void AddChild(string tool, long? bytes)
    {
        if (bytes is { } moved)
        {
            Interlocked.Add(ref _childBytes, moved);
        }
        lock (_gate)
        {
            var bucket = Current();
            if (tool == "gh")
            {
                bucket.GhRuns++;
                Interlocked.Increment(ref _ghRuns);
            }
            else
            {
                bucket.GitRuns++;
                Interlocked.Increment(ref _gitRuns);
            }
            if (bytes is { } b)
            {
                bucket.ChildBytes += b;
                bucket.ChildKnown = true;
            }
        }
    }

    private Bucket Current()
    {
        var hour = HourOf(Time.GetUtcNow());
        if (!_hours.TryGetValue(hour, out var bucket))
        {
            _hours[hour] = bucket = new Bucket();
            while (_hours.Count > 48)
            {
                _hours.Remove(_hours.Keys.First());
            }
        }
        return bucket;
    }

    /// <summary>The totals of the clock hour starting at <paramref name="hour"/>; zeros when nothing was counted in it.</summary>
    public Totals Hour(DateTimeOffset hour)
    {
        lock (_gate)
        {
            return _hours.TryGetValue(hour, out var b) ? new Totals(b.HttpIn, b.HttpOut, b.ChildBytes, b.ChildKnown, b.GitRuns, b.GhRuns) : new Totals(0, 0, 0, false, 0, 0);
        }
    }

    /// <summary>The last full clock hour as the node's summary row carries it; its child bytes are null where this platform cannot say.</summary>
    public NodeTrafficRow LastHourRow()
    {
        var hour = HourOf(Time.GetUtcNow()).AddHours(-1);
        var t = Hour(hour);
        return new NodeTrafficRow
        {
            Hour = hour,
            HttpIn = t.HttpIn,
            HttpOut = t.HttpOut,
            ChildBytes = CanMeasureChildren ? t.ChildBytes : null,
            GitRuns = t.GitRuns,
            GhRuns = t.GhRuns,
        };
    }

    /// <summary>
    /// Writes the line of the last full clock hour when it has not been written yet; true when it wrote. Called by the hourly
    /// service after each hour boundary.
    /// </summary>
    public bool Roll(Action<string> write)
    {
        var hour = HourOf(Time.GetUtcNow()).AddHours(-1);
        lock (_gate)
        {
            if (_logged is { } logged && logged >= hour)
            {
                return false;
            }
            _logged = hour;
        }
        var t = Hour(hour);
        var child = CanMeasureChildren ? $"{t.ChildBytes} bytes (estimate, upper bound)" : "unknown";
        write(string.Create(CultureInfo.InvariantCulture,
            $"traffic {hour:yyyy-MM-dd HH}:00Z: http in {t.HttpIn} bytes, out {t.HttpOut} bytes; git and gh children {child}; git runs {t.GitRuns}, gh runs {t.GhRuns}"));
        return true;
    }

    /// <summary>A started git or gh child, measured while it runs and added when disposed (after the process ended).</summary>
    public sealed class Child : IDisposable
    {
        private readonly NodeTraffic _traffic;
        private readonly string _tool;
        private nint _job;

        internal Child(NodeTraffic traffic, string tool, Process process)
        {
            _traffic = traffic;
            _tool = tool;
            if (CanMeasureChildren)
            {
                _job = DetachedShell.CreateAccountingJob(process);
            }
        }

        public void Dispose()
        {
            long? bytes = CanMeasureChildren ? 0 : null;
            if (_job != 0)
            {
                bytes = DetachedShell.JobOtherTransfer(_job);
                DetachedShell.CloseJob(_job);
                _job = 0;
            }
            _traffic.AddChild(_tool, bytes);
        }
    }

    /// <summary>Starts <paramref name="start"/> as a counted child of kind <c>git</c> or <c>gh</c>; dispose the counter once the process has ended.</summary>
    public (Process Process, Child Counted) Start(string tool, ProcessStartInfo start)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start");
        return (process, new Child(this, tool, process));
    }
}

/// <summary>Counts what a client sends and receives through the handler it wraps into <see cref="NodeTraffic"/>.</summary>
public sealed class CountingHandler(NodeTraffic traffic) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long sent = HeaderBytes($"{request.Method} {request.RequestUri?.PathAndQuery} HTTP/{request.Version}", request.Headers, request.Content?.Headers);
        if (request.Content is { } body)
        {
            sent += body.Headers.ContentLength ?? (await body.ReadAsByteArrayAsync(cancellationToken)).Length;
        }
        long received = 0;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Content.Headers.ContentLength is null)
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
            }
            received = HeaderBytes($"HTTP/{response.Version} {(int)response.StatusCode} {response.ReasonPhrase}", response.Headers, response.Content.Headers)
                + (response.Content.Headers.ContentLength ?? 0);
            return response;
        }
        finally
        {
            traffic.AddHttp(received, sent);
        }
    }

    private static long HeaderBytes(string firstLine, params System.Net.Http.Headers.HttpHeaders?[] headers)
    {
        long total = firstLine.Length + 4;
        foreach (var set in headers)
        {
            if (set is null)
            {
                continue;
            }
            foreach (var header in set)
            {
                total += header.Key.Length + 4 + header.Value.Sum(v => v.Length);
            }
        }
        return total;
    }
}

/// <summary>Writes <see cref="NodeTraffic"/>'s last full hour to node.log once after each clock hour.</summary>
public sealed class TrafficLog(NodeTraffic traffic, RollingLog log, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var next = NodeTraffic.HourOf(time.GetUtcNow()).AddHours(1);
            try
            {
                await Task.Delay(next - time.GetUtcNow() + TimeSpan.FromSeconds(1), time, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            traffic.Roll(line => log.Write(line));
        }
    }
}
