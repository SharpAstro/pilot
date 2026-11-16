using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Archives landed flights on its own (PIL1083): every <see cref="Interval"/> each flight landed longer ago than the
/// <see cref="NodeSettings.ArchiveLandedAfterKey"/> setting is archived when <see cref="FlightArchiver.WhyNotAsync"/> says nothing holds it,
/// a few at a time with the state's lock released between flights and a pause between batches, so a store with hundreds of landed flights
/// never holds startup or the stream. A flight held back gets one Handled line naming why, repeated only when the reason changes. After a sweep
/// archived something the connection release runs, so a flight held on one is let go.
/// </summary>
public sealed class FlightArchiveSweep(NodeState state, FlightArchiver archiver, FlightCommander flights, TimeProvider time) : BackgroundService
{
    /// <summary>What the Handled line is.</summary>
    public const string HeldKind = "archive-held";

    /// <summary>How often the node sweeps.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long after the start the first sweep waits, so starting never competes with it.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many flights one batch archives before the sweep pauses.</summary>
    public int Batch { get; init; } = 10;

    /// <summary>The pause between batches.</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a flight stays landed before it is archived, read at each sweep; null is off.</summary>
    public Func<TimeSpan?> After { get; init; } = () => NodeSettings.DefaultArchiveLandedAfter;

    /// <summary>Told after a batch archived flights, so the node's row (its archived count) follows at once.</summary>
    public Action? Archived { get; init; }

    public TextWriter Error { get; set; } = TextWriter.Null;

    // What each held-back flight was last told, so a sweep repeats a line only when its reason changes.
    private readonly Dictionary<string, string> _told = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(StartDelay, time, stopping);
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stopping);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Error.WriteLineAsync($"flight archive sweep failed: {ex.Message}");
                }
                await Task.Delay(Interval, time, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>One sweep; returns how many flights it archived.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        // A landed row stored with a merge-queue note (older nodes left it) is healed first, so it is not refused for ever.
        foreach (var stale in state.Flights().Where(f => f.State == FlightState.Landed && f.MergeQueue is not null).Select(f => f.Code).ToList())
        {
            state.UpdateFlight(stale, f => f with { MergeQueue = null });
        }
        if (After() is not { } after)
        {
            return 0;
        }
        var cutoff = time.GetUtcNow() - after;
        var due = state.Flights()
            .Where(f => f.State == FlightState.Landed && AgeFrom(f) <= cutoff)
            .OrderBy(AgeFrom)
            .Select(f => f.Code)
            .ToList();
        // A flight that left without being archived (reopened, removed) is no longer one the sweep holds back, so its told line goes.
        var landed = state.Flights().Where(f => f.State == FlightState.Landed).Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _told.Keys.Where(k => !landed.Contains(k)).ToList())
        {
            _told.Remove(gone);
        }
        var archived = 0;
        var inBatch = 0;
        foreach (var code in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Looked up again: it may have moved or been archived since the list was made.
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Landed } flight)
            {
                continue;
            }
            try
            {
                if (await archiver.WhyNotAsync(flight, cancellationToken) is { } why)
                {
                    Say(flight, why);
                    continue;
                }
                state.ArchiveFlight(code);
                _told.Remove(code);
                archived++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One flight that cannot be judged or archived (a vanished repo folder, a store fault) never stops the ones after it.
                await Error.WriteLineAsync($"flight archive sweep: {code} not archived: {ex.Message}");
                continue;
            }
            if (++inBatch >= Batch)
            {
                inBatch = 0;
                Archived?.Invoke();
                if (Pause > TimeSpan.Zero)
                {
                    await Task.Delay(Pause, time, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        if (archived > 0)
        {
            Archived?.Invoke();
            await flights.ReleaseLandedConnectionsAsync(cancellationToken);
        }
        return archived;
    }

    // A brought-back flight is as old as its restoring, so the sweep does not take it away again at the next tick.
    private static DateTimeOffset AgeFrom(FlightRow f) =>
        f.RestoredAt is { } restored && restored > (f.LandedAt ?? DateTimeOffset.MinValue) ? restored : f.LandedAt ?? DateTimeOffset.MinValue;

    private void Say(FlightRow flight, string why)
    {
        var title = $"{flight.Code} is not archived yet: {why}";
        if (_told.TryGetValue(flight.Code, out var last) ? last == title
            : state.Cards().Any(c => c.Kind == HeldKind && c.Subject == Verbs.FlightTarget(flight.Code) && c.Title == title))
        {
            _told[flight.Code] = title;
            return;
        }
        _told[flight.Code] = title;
        state.RaiseCard(CardBin.Handled, HeldKind, Verbs.FlightTarget(flight.Code), title);
    }
}
