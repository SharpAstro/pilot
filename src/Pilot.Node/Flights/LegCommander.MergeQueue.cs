using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's merge queue (<see cref="MergeRow"/>): one entry per flight, in order per repo. A merge asked for (by hand, armed at
/// green, autopilot) adds or refreshes the flight's entry; the entry is taken when no other entry of its repo is being updated
/// or merged and none that joined before it still waits (one at a time, in order). It is then brought up to date by plain git,
/// and merges only with a passed review at its head and green checks (<see cref="MergeAsync"/>); an entry whose checks run, or
/// whose review is fresh, waits in <see cref="MergeEntryState.Checks"/> for the node's look (<see cref="LookAtMergesAsync"/>).
/// </summary>
public sealed partial class LegCommander
{
    // The flights whose merge command is under way here, by code: only those hold a repo's turn, so an entry left by a node that
    // went away holds nothing.
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly Lock _turns = new();
    private TaskCompletionSource _turnChanged = NewTurn();

    private static TaskCompletionSource NewTurn() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The flight whose merge <paramref name="command"/> asks for, or null when its target is no flight.</summary>
    private string? MergeCode(CommandRequest command)
    {
        try
        {
            return FlightOf(command.Target).Code;
        }
        catch (FlightException)
        {
            return null;
        }
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_turns)
        {
            old = _turnChanged;
            _turnChanged = NewTurn();
        }
        old.TrySetResult();
    }

    /// <summary>A merge command is done with the flight's turn.</summary>
    private void Leave(string code)
    {
        lock (_turns)
        {
            _active.Remove(code);
        }
        Signal();
    }

    /// <summary>
    /// Adds the flight's entry to its repo's queue, or gives the one it has the new head and method (it keeps its place while it
    /// stands in the queue; a left one starts again at the back). The flight holds its repo's turn from here, until its command ends.
    /// </summary>
    private MergeRow Enqueue(FlightRow flight, string head, string method, string by)
    {
        var existing = state.Merges().FirstOrDefault(m => m.Flight == flight.Code);
        var at = time.GetUtcNow();
        var row = new MergeRow
        {
            Flight = flight.Code, Repo = flight.Repo, Pr = flight.PullRequest, Head = head, Method = method, State = MergeEntryState.Waiting,
            By = by, AddedAt = existing is { Open: true } ? existing.AddedAt : at, ChangedAt = at,
        };
        lock (_turns)
        {
            _active.Add(flight.Code);
        }
        state.PutMerge(row);
        state.Renumber(flight.Repo);
        return row;
    }

    private void SetEntry(string code, MergeEntryState to, string? why, bool awaitsReview = false, string? head = null)
    {
        if (state.Merges().FirstOrDefault(m => m.Flight == code) is { } held)
        {
            state.PutMerge(held with { State = to, Why = why, AwaitsReview = awaitsReview, Head = head ?? held.Head, ChangedAt = time.GetUtcNow() });
            state.Renumber(held.Repo);
        }
    }

    /// <summary>Takes the entry out of its queue with <paramref name="why"/>: a conflict, a failed check, a refusal.</summary>
    public void DropMerge(string code, string why) => state.EndMerge(code, MergeEntryState.Dropped, why);

    /// <summary>
    /// Waits, without the lock, for the entry's turn in its repo: none of its repo's other entries under way is being updated or
    /// merged, and none that joined before it is still waiting. Takes the turn (the entry is then updating) in the same step.
    /// </summary>
    private async Task WaitTurnAsync(string code, string repo, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_turns)
            {
                var mine = state.Merges().FirstOrDefault(m => m.Flight == code);
                var others = state.Merges().Where(m => m.Repo == repo && m.Flight != code && m.Open && _active.Contains(m.Flight)).ToList();
                var running = others.Any(m => m.State is MergeEntryState.Updating or MergeEntryState.Merging);
                var ahead = mine is not null && others.Any(m => m.State == MergeEntryState.Waiting && (m.AddedAt, m.Flight).CompareTo((mine.AddedAt, mine.Flight)) < 0);
                if (!running && !ahead)
                {
                    SetEntry(code, MergeEntryState.Updating, "taken from the queue: brought up to date, then merged");
                    return;
                }
                changed = _turnChanged.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// What the node does of its own with the queue at each look: an entry waiting for a fresh review is merged once the review
    /// has passed at its head (it goes through the queue again), dropped when the review asks for changes, and one whose flight
    /// is gone leaves. Entries of a command under way are that command's.
    /// </summary>
    public async Task LookAtMergesAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in state.Merges().Where(m => m is { Open: true, State: MergeEntryState.Checks, AwaitsReview: true }))
        {
            bool busy;
            lock (_turns)
            {
                busy = _active.Contains(entry.Flight);
            }
            if (busy)
            {
                continue;
            }
            if (state.Flights().FirstOrDefault(f => f.Code == entry.Flight) is not { State: FlightState.Flying } flight)
            {
                DropMerge(entry.Flight, "the flight is no longer flying");
                continue;
            }
            switch (flight.Review)
            {
                case { State: ReviewState.Changes }:
                    DropMerge(entry.Flight, "its review asks for changes");
                    break;
                case { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } reviewed } when flight.PullRequest is not null:
                    await RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [reviewed, entry.Method], Who = "node" }, cancellationToken);
                    break;
            }
        }
    }
}
