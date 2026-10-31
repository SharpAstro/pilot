using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's heartbeat over the background sessions it will wake again: the watchtower while there is something to
/// watch, and any session waiting on a job of its own (a worker that handed a suite over with <c>pilot run</c>). Claude
/// Code's prompt cache lives an hour; a session woken after that reads its whole context again at full price, which is
/// what made v1's long-lived sessions dear. So one idle for <see cref="IdleFor"/> with more than <see cref="Above"/> of
/// context is compacted (<c>compact session/&lt;id&gt;</c>, Claude Code's <c>/compact</c>) while its cache is still
/// warm, and wakes on a summary; one past <see cref="WindowShare"/> of its window is compacted as soon as it is idle.
/// </summary>
/// <remarks>
/// A session is compacted once per stretch of idleness: not again until it has answered something since. The compaction
/// is its next turn, never words said into one (<see cref="Sessions.SessionMessenger"/> refuses a session mid-turn), and
/// an interactive session is never touched: someone is at it. Each compaction leaves a Handled line, done or refused.
/// </remarks>
public sealed class Compactor(NodeState state, Watchtower watchtower, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The ledger of launched sessions, which says which were stopped on a land or merge; null in a test with none.</summary>
    public Sessions.SessionLedger? Ledger { get; set; }

    /// <summary>What its Handled lines are.</summary>
    public const string Kind = "session-compacted";

    /// <summary>The limits a compaction's resume waits on; a test's compactor takes the defaults.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>What holds a start while the 5-hour window is spent: the node's one gate, shared with the leg sweep and the watchtower.</summary>
    public StartGate Gate { get; set; } = new(state, time);

    /// <summary>How often it looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a session sits idle before it is compacted: inside the prompt cache's hour, so the compaction reads it warm.</summary>
    public TimeSpan IdleFor { get; init; } = TimeSpan.FromMinutes(50);

    /// <summary>
    /// How much context makes a compaction worth its turn: below it, a cold read costs little more than the summary. A fresh
    /// session starts near 50k (Claude Code's own prompt and tools), which no compaction takes away.
    /// </summary>
    public long Above { get; init; } = 100_000;

    /// <summary>The share of its window past which an idle session is compacted at once, however long it has been idle.</summary>
    public double WindowShare { get; init; } = 0.7;

    /// <summary>How long after a refused compaction the next one is tried.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>What the watchtower's summary keeps.</summary>
    public const string WatchtowerFocus = "Keep what you are watching and waiting on: the flights, legs, jobs and cards you know of, what you did about "
        + "each, and what you told the controller. `pilot status` and `pilot cards` say where things stand now.";

    /// <summary>What a session waiting on its job keeps.</summary>
    public const string JobFocus = "Keep your task: what it is, what is done and left, and the job you handed to the node, which wakes you with how "
        + "it ended. Wait for that; do not poll it.";

    // By row: the response it had when it was last compacted, and when a refused one may be tried again.
    private readonly Dictionary<string, DateTimeOffset?> _compactedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAt = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await LookAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // As with the leg watch: a heartbeat that died on one bad look would leave every session to go cold.
                await Error.WriteLineAsync($"compactor: one look failed, looking again: {ex.Message}");
            }
        }
    }

    /// <summary>The background sessions the node will wake again, idle now, with what their summary keeps.</summary>
    public IEnumerable<(SessionRow Row, string Focus)> Kept()
    {
        var waiting = state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued && j.Owner is not null).Select(j => j.Owner!).ToHashSet(StringComparer.Ordinal);
        var watching = watchtower.Watching() is not null;
        var current = watchtower.Current()?.Id;
        foreach (var row in state.Sessions().Where(r => r is { Kind: SessionKind.Background, State: SessionState.Idle }))
        {
            if (row.Name == watchtower.Name && row.Id != current)
            {
                // A replaced watchtower is never compacted or resumed: the role's current session is the only one the node wakes (#542).
                continue;
            }
            if (WindDown.Why(state, Ledger, row) is not null)
            {
                // A session of a landed flight, or one stopped on land or merge, is never compacted: it is to be gone.
                continue;
            }
            if (waiting.Contains(row.Id))
            {
                yield return (row, row.Name == watchtower.Name ? WatchtowerFocus : JobFocus);
            }
            else if (row.Name == watchtower.Name && watching)
            {
                yield return (row, WatchtowerFocus);
            }
        }
    }

    /// <summary>One look: compacts each kept session that has sat idle long enough with enough context, or grown past its share.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var (row, focus) in Kept().ToList())
        {
            var last = row.LastResponseAt ?? row.StartedAt;
            if (_compactedAt.TryGetValue(row.Id, out var compacted) && compacted == row.LastResponseAt
                || _retryAt.TryGetValue(row.Id, out var retry) && now < retry)
            {
                continue;
            }
            var full = row.ContextWindow > 0 && row.ContextTokens > WindowShare * row.ContextWindow;
            var cooling = now - last >= IdleFor && row.ContextTokens > Above;
            if (!full && !cooling)
            {
                continue;
            }
            await CompactAsync(row, focus, full
                ? string.Create(CultureInfo.InvariantCulture, $"its context reached {100.0 * row.ContextTokens / row.ContextWindow:0}% of its window")
                : $"idle {Minutes(now - last)} with {Tokens(row.ContextTokens)} of context, before its prompt cache went cold", cancellationToken);
        }
    }

    private async Task CompactAsync(SessionRow row, string focus, string why, CancellationToken cancellationToken)
    {
        var subject = row.Flight is { } flight ? row.Leg is { } leg ? Verbs.LegTarget(flight, leg) : Verbs.FlightTarget(flight)
            : row.Name == watchtower.Name ? Verbs.WatchtowerTarget : Verbs.NodeTarget;
        // A compaction resumes a session for a turn of its own, so a spent 5-hour window holds it as it holds any start: not marked
        // compacted, so the next look takes it once the window has reset. The flight's repo may have runway, as for its legs.
        var limit = row.Flight is { } code && state.Flights().FirstOrDefault(f => f.Code == code) is { } owner ? Models.StartLimitFor(owner.Repo) : Models.StartLimit;
        if (Gate.Hold($"{Verbs.Compact} {row.Id}", limit, Models.WeekLimit, Kind, subject, $"Compaction of {row.Name} waits ({why})"))
        {
            return;
        }
        var result = await run(new CommandRequest { Verb = Verbs.Compact, Target = Verbs.SessionTarget(row.Id), Args = [focus], Who = LegWatch.Who }, cancellationToken);
        if (result.Ok)
        {
            _compactedAt[row.Id] = row.LastResponseAt;
            _retryAt.Remove(row.Id);
            state.RaiseCard(CardBin.Handled, Kind, subject, $"Compacted {row.Name}: {why}");
        }
        else
        {
            _retryAt[row.Id] = time.GetUtcNow() + RetryAfter;
            state.RaiseCard(CardBin.Handled, Kind, subject, $"Could not compact {row.Name} ({why}): {result.Error}; tried again in {RetryAfter.TotalMinutes:0} minutes");
        }
    }

    private static string Minutes(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalMinutes} min";

    private static string Tokens(long tokens) => tokens >= 1_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{tokens / 1_000_000.0:0.#}M")
        : string.Create(CultureInfo.InvariantCulture, $"{tokens / 1000}k");
}
