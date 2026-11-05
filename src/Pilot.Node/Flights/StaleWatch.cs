using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The background watch over staleness (#736 points 2, 5, 6). Each tick it reads <see cref="Staleness"/>: at half a bound it takes
/// the node's own action where one exists (re-tells an ended job its owner never heard of, nudges an idle worker, re-rings an
/// unanswered card) and records it as a handled card; past a bound it raises the flight's priority one step, once per bound,
/// through the tracker path <c>flight-priority</c> uses, and records the raise on the flight; and it attaches the stale summary to
/// the flight, refreshed only when its cause changes.
/// </summary>
public sealed class StaleWatch(NodeState state, TimeProvider time) : BackgroundService
{
    public const string ActedKind = "stale-acted";
    public const string ReminderKind = "stale-card-reminder";

    /// <summary>How often it reads.</summary>
    public TimeSpan Every { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The bounds past which work is stale; the defaults unless the node wires its settings.</summary>
    public Func<StaleBounds> Bounds { get; set; } = () => new StaleBounds();

    /// <summary>The trackers a raise is written through. Without one a raise says it could not be made.</summary>
    public Func<Trackers?> Trackers { get; set; } = () => null;

    /// <summary>Tells the owners given their ended, untold jobs (JobRunner.TellUntoldOf); whether any was told.</summary>
    public Func<IReadOnlyCollection<string>, CancellationToken, Task<bool>>? TellEndedJobs { get; set; }

    /// <summary>Says <c>text</c> to a session by its id, through the node's own messaging path; whether it was delivered.</summary>
    public Func<string, string, CancellationToken, Task<bool>>? Say { get; set; }

    /// <summary>Where a failure of one tick goes.</summary>
    public TextWriter? Error { get; init; }

    private readonly ConcurrentDictionary<string, byte> _acted = new(StringComparer.Ordinal);

    /// <summary>The key a raise is kept under for the kind of bound passed.</summary>
    public static string BoundKey(StaleKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The next step up: unset, medium and low go to high, high to urgent; null at urgent.</summary>
    public static IssuePriority? Raised(IssuePriority? level) => level switch
    {
        IssuePriority.Urgent => null,
        IssuePriority.High => IssuePriority.Urgent,
        _ => IssuePriority.High,
    };

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, time, stopping);
                await TickAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Error?.WriteLine($"stale watch: {ex.Message}");
            }
        }
    }

    /// <summary>One reading and what follows from it.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var reading = Staleness.Read(state.Flights(), state.Legs(), state.Cards(), state.Jobs(), state.PullRequests(), now, Bounds(), time.LocalTimeZone);

        foreach (var item in reading.Nearing)
        {
            await ActAsync(item, cancellationToken);
        }
        foreach (var flight in state.Flights().Where(f => f is { State: not FlightState.Landed, FoldedInto: null, MovedTo: null }))
        {
            var mine = reading.Stale.Where(i => i.Flight == flight.Code).ToList();
            if (mine.Count > 0)
            {
                await RaiseAsync(flight, mine, now, cancellationToken);
            }
            Attach(flight.Code, mine.FirstOrDefault(), now);
        }
    }

    // Half a bound: the node's own action where one exists, once per item and per kind of action.
    private async Task ActAsync(StaleItem item, CancellationToken cancellationToken)
    {
        var subject = Verbs.FlightTarget(item.Flight ?? item.Subject);
        if (item.Kind == StaleKind.Card)
        {
            if (state.Cards(openOnly: true).FirstOrDefault(c => c.Id == item.Subject) is { } card && _acted.TryAdd($"card:{card.Id}", 0))
            {
                state.RaiseCard(CardBin.ForYou, ReminderKind, Verbs.CardTarget(card.Id), $"Still ringing after {item.AgeText}: {card.Title}", $"Card {card.Id} has had no answer. {item.Action}.", card.Choices);
                state.RaiseCard(CardBin.Handled, ActedKind, subject, $"{item.Subject} has rung {item.AgeText} unanswered; the node rang it again");
            }
            return;
        }
        if (item.Flight is not { } code)
        {
            return;
        }
        var workers = state.Sessions().Where(s => s.Role == SessionRole.Worker && s.Flight == code && s.State is not (SessionState.Ended or SessionState.Failed)).ToList();
        if (workers.Count == 0)
        {
            return;
        }
        if (TellEndedJobs is not null && _acted.TryAdd($"tell:{item.Kind}:{item.Subject}", 0)
            && await TellEndedJobs([.. workers.Select(w => w.Id)], cancellationToken))
        {
            // A worker just told of a job has something to do: no nudge on top of it.
            _acted.TryAdd($"nudge:{item.Kind}:{item.Subject}", 0);
            state.RaiseCard(CardBin.Handled, ActedKind, subject, $"{item.Subject} is {item.AgeText} old, past half its bound; the node told its worker of a job that had ended");
            return;
        }
        if (Say is not null && workers.FirstOrDefault(w => w.State == SessionState.Idle) is { } idle && _acted.TryAdd($"nudge:{item.Kind}:{item.Subject}", 0))
        {
            var text = $"Stale check: {item.Subject} has been open {item.AgeText}, past half its bound of {item.Bound}. It waits on {item.WaitsOn}. If your leg can move, move it; otherwise say what holds it.";
            if (await Say(idle.Id, text, cancellationToken))
            {
                state.RaiseCard(CardBin.Handled, ActedKind, subject, $"{item.Subject} is {item.AgeText} old, past half its bound; the node nudged its idle worker");
            }
        }
    }

    // Past a bound: one step up in priority, once per kind of bound, one raise per flight and tick.
    private async Task RaiseAsync(FlightRow flight, List<StaleItem> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var done = flight.StaleRaises ?? [];
        var item = items.FirstOrDefault(i => !done.Any(r => r.Bound == BoundKey(i.Kind)));
        if (item is null)
        {
            return;
        }
        var tracker = Trackers()?.Of(flight.Tracker);
        string note;
        IssuePriority? level = null;
        try
        {
            if (tracker is null)
            {
                note = $"could not be raised: {flight.Code} has no tracker to set a priority on";
            }
            else if (await tracker.PriorityAsync(flight.RepoPath, flight.Issue, cancellationToken) is not { HasField: true } read)
            {
                note = $"could not be raised: {flight.Code}'s repo has no Priority field";
            }
            else if (Raised(read.Level) is not { } next)
            {
                // Already urgent: nothing to raise, and no record.
                return;
            }
            else
            {
                await tracker.SetPriorityAsync(flight.RepoPath, flight.Issue, next, cancellationToken);
                level = next;
                note = $"raised to {IssuePriorities.Name(next).ToLowerInvariant()}: {item.Cause.Split(',')[0]}";
            }
        }
        catch (FlightException ex)
        {
            note = $"could not be raised: {LegCommander.OneLine(ex.Message)}";
        }
        var raise = new FlightStaleRaise { Bound = BoundKey(item.Kind), Level = level, At = now, Note = note };
        state.UpdateFlight(flight.Code, f => f with
        {
            StaleRaises = [.. f.StaleRaises ?? [], raise],
            IssuePriority = level ?? f.IssuePriority,
            HasPriorityField = level is not null || f.HasPriorityField,
        });
        state.RaiseCard(CardBin.Handled, ActedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code}: {note}");
    }

    // The summary on the flight is replaced only when what it waits on, or the kind of staleness, changed.
    private void Attach(string code, StaleItem? item, DateTimeOffset now)
    {
        var cause = item is null ? null : $"{item.Kind}: {item.WaitsOn}";
        state.UpdateFlight(code, f => f.StaleCause == cause ? f : f with { StaleCause = cause, StaleSummary = item?.Summary, StaleSince = item is null ? null : now });
    }
}
