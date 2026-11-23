using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Counts every gh call the node makes by kind and caller, per 15-minute window of the node's clock, so the GitHub budget's
/// spenders can be read off (#1293). Lock-free: a <see cref="ConcurrentDictionary{TKey, TValue}"/> per window of cells that
/// only <see cref="Interlocked"/> touches.
/// </summary>
/// <remarks>
/// The kind is the gh subcommand pair (<c>pr view</c>, <c>pr checks</c>, <c>issue view</c>); <c>api graphql</c> is told apart from
/// a REST <c>api</c> call (<c>api rest</c>). How a call is priced:
/// <list type="bullet">
/// <item>A GraphQL query of the node's own that asks <c>rateLimit { cost }</c> reports that cost (<see cref="RecordCost"/>): known points.</item>
/// <item><c>gh pr</c> and <c>gh issue</c> run GraphQL behind gh, so each call counts 1 point, marked estimated, as does a
/// <c>gh api graphql</c> call whose cost nobody reported.</item>
/// <item>REST <c>gh api</c> calls are REST calls (the core budget), counted as calls and under REST, never as points.</item>
/// </list>
/// The caller is passed in by the call site (a member name), never read from a stack trace.
/// </remarks>
public sealed class GhCallCounter(TimeProvider time)
{
    /// <summary>The length of a window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private const int KeptWindows = 8;
    private const int TopKinds = 5;
    private const int TopCallers = 3;

    private sealed class Cell
    {
        public long Calls, Known, Estimated, Rest;
    }

    /// <summary>One kind of call in a window, with its callers (most calls first).</summary>
    public sealed record KindTotals(string Kind, long Calls, long Points, long Rest, (string Caller, long Calls)[] Callers);

    /// <summary>A closed window: its start, totals and kinds, most points first, then most calls.</summary>
    public sealed record Summary(DateTimeOffset Start, long Calls, long KnownPoints, long EstimatedPoints, long RestCalls, KindTotals[] Kinds)
    {
        public long Points => KnownPoints + EstimatedPoints;

        /// <summary>The line of node.log for this window.</summary>
        public string LogLine() => string.Create(CultureInfo.InvariantCulture,
            $"gh calls {Start:yyyy-MM-dd HH:mm}Z (15 min): {Calls} calls, {Points} GraphQL points ({KnownPoints} known, {EstimatedPoints} estimated), {RestCalls} REST; {Top()}");

        /// <summary>The short line of <c>pilot status</c>.</summary>
        public string StatusLine() => string.Create(CultureInfo.InvariantCulture, $"gh, last 15 min: {Calls} calls, ~{Points} points; {Top()}");

        private string Top() => string.Join(", ", Kinds.Take(TopKinds).Select(k =>
            $"{k.Kind} x{k.Calls} ({string.Join(", ", k.Callers.Take(TopCallers).Select(c => c.Calls == k.Calls ? c.Caller : $"{c.Caller} x{c.Calls}"))})"));
    }

    private readonly ConcurrentDictionary<long, ConcurrentDictionary<(string Kind, string Caller), Cell>> _windows = new();
    private long _logged = long.MinValue;

    private static long Index(DateTimeOffset at) => at.UtcTicks / Window.Ticks;

    /// <summary>The gh subcommand pair of <paramref name="args"/>; <c>api graphql</c> and <c>api rest</c> for gh api.</summary>
    public static string KindOf(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "gh";
        }
        if (args[0] == "api")
        {
            return args.Count > 1 && args[1] == "graphql" ? "api graphql" : "api rest";
        }
        return args.Count > 1 && !args[1].StartsWith('-') ? $"{args[0]} {args[1]}" : args[0];
    }

    private Cell CellOf(string kind, string caller)
    {
        var window = Index(time.GetUtcNow());
        var cells = _windows.GetOrAdd(window, static _ => new());
        foreach (var old in _windows.Keys.Where(k => k <= window - KeptWindows))
        {
            _windows.TryRemove(old, out _);
        }
        return cells.GetOrAdd((kind, caller), static _ => new Cell());
    }

    /// <summary>Counts one gh call whose arguments are <paramref name="args"/>, made by <paramref name="caller"/>.</summary>
    public void Record(IReadOnlyList<string> args, string? caller) => Record(KindOf(args), caller);

    public void Record(string kind, string? caller)
    {
        var cell = CellOf(kind, caller ?? "unknown");
        Interlocked.Increment(ref cell.Calls);
        if (kind == "api rest")
        {
            Interlocked.Increment(ref cell.Rest);
        }
        else if (kind == "api graphql" || kind.StartsWith("pr ", StringComparison.Ordinal) || kind.StartsWith("issue ", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref cell.Estimated);
        }
    }

    /// <summary>A call reported its real cost (<c>rateLimit { cost }</c>): the known points replace the 1 point estimated when it was counted.</summary>
    public void RecordCost(string kind, string caller, int cost)
    {
        var cell = CellOf(kind, caller);
        Interlocked.Add(ref cell.Known, cost);
        if (Interlocked.Decrement(ref cell.Estimated) < 0)
        {
            Interlocked.Increment(ref cell.Estimated);
        }
    }

    /// <summary>The totals of the window starting at <paramref name="start"/>; null when nothing was counted in it.</summary>
    public Summary? Of(DateTimeOffset start)
    {
        if (!_windows.TryGetValue(Index(start), out var cells))
        {
            return null;
        }
        var rows = cells.Select(c => (c.Key.Kind, c.Key.Caller, Calls: Interlocked.Read(ref c.Value.Calls), Known: Interlocked.Read(ref c.Value.Known), Est: Interlocked.Read(ref c.Value.Estimated), Rest: Interlocked.Read(ref c.Value.Rest))).ToArray();
        if (rows.Length == 0)
        {
            return null;
        }
        var kinds = rows.GroupBy(r => r.Kind).Select(g => new KindTotals(g.Key, g.Sum(r => r.Calls), g.Sum(r => r.Known + r.Est), g.Sum(r => r.Rest),
                [.. g.OrderByDescending(r => r.Calls).ThenBy(r => r.Caller, StringComparer.Ordinal).Select(r => (r.Caller, r.Calls))]))
            .OrderByDescending(k => k.Points).ThenByDescending(k => k.Calls).ThenBy(k => k.Kind, StringComparer.Ordinal).ToArray();
        return new Summary(start, rows.Sum(r => r.Calls), rows.Sum(r => r.Known), rows.Sum(r => r.Est), rows.Sum(r => r.Rest), kinds);
    }

    private DateTimeOffset LastClosedStart() => new((Index(time.GetUtcNow()) - 1) * Window.Ticks, TimeSpan.Zero);

    /// <summary>The last closed window, as <c>pilot status</c> shows it; null when it counted nothing.</summary>
    public Summary? Last() => Of(LastClosedStart());

    /// <summary>
    /// Writes one line for the last closed window when it has not been written yet and counted something; true when it wrote.
    /// Called every window by <see cref="GhCallLog"/>.
    /// </summary>
    public bool Roll(Action<string> write)
    {
        var closed = Index(time.GetUtcNow()) - 1;
        long seen;
        do
        {
            seen = Interlocked.Read(ref _logged);
            if (seen >= closed)
            {
                return false;
            }
        }
        while (Interlocked.CompareExchange(ref _logged, closed, seen) != seen);
        if (Last() is not { } summary)
        {
            return false;
        }
        write(summary.LogLine());
        return true;
    }
}

/// <summary>Writes the gh call counter's line to node.log once per 15-minute window.</summary>
public sealed class GhCallLog(GhCallCounter counter, RollingLog log, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var next = new DateTimeOffset((time.GetUtcNow().UtcTicks / GhCallCounter.Window.Ticks + 1) * GhCallCounter.Window.Ticks, TimeSpan.Zero);
            try
            {
                await Task.Delay(next - time.GetUtcNow() + TimeSpan.FromSeconds(1), time, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            counter.Roll(line => log.Write(line));
        }
    }
}
