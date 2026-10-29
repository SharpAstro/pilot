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

    // The turn a running command took (the flight it enqueued), set by MergeAsync and given back when the command ends; a command
    // refused before it enqueued holds none, so it never gives back another command's.
    private sealed class HeldTurn
    {
        public string? Code { get; set; }
    }

    private readonly AsyncLocal<HeldTurn?> _held = new();

    // Merge batches (the merge-batch setting): the batch branch's builder; the flights a green batch landed with the base's direct
    // push, by code (their commands take it as their merge); and the flights a batch was tried for, never batched again (a red or
    // conflicting batch falls back to them one at a time).
    private MergeBatcher? _batcher;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _landedInBatch = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _batchTried = new(StringComparer.Ordinal);

    // Repos whose last batch's CI never started, until when they are not batched again: each new merge command would otherwise build
    // a batch there and hold the repo's turn for the start budget, only to fall back again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _batchCold = new(StringComparer.Ordinal);

    /// <summary>How long a repo is not batched after a batch whose CI never started.</summary>
    public static readonly TimeSpan BatchColdFor = TimeSpan.FromHours(1);

    /// <summary>Builds and checks batch branches; a test sets how often it looks at the batch's CI.</summary>
    public MergeBatcher Batcher
    {
        get => _batcher ??= new MergeBatcher(git, github);
        set => _batcher = value;
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
        _batchTried.TryRemove(code, out _);
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
                // A batch that landed this entry with the base's direct push has merged it: nothing is left to take a turn for.
                if (_landedInBatch.ContainsKey(code))
                {
                    return;
                }
                var mine = state.Merges().FirstOrDefault(m => m.Flight == code);
                var others = state.Merges().Where(m => m.Repo == repo && m.Flight != code && m.Open && _active.Contains(m.Flight)).ToList();
                // A batch waiting on its CI holds the repo's turn as a merge does: its entries are Batched until it ends.
                var running = others.Any(m => m.State is MergeEntryState.Updating or MergeEntryState.Merging or MergeEntryState.Batched);
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
    /// With <c>merge-batch</c> on, an entry that has just taken its repo's turn looks for fellows: the repo's other waiting entries
    /// that are ready (a passed review at their head, green checks) and not batched before. With two or more ready (this one among
    /// them) the batch branch is built, pushed and its CI awaited once (<see cref="MergeBatcher"/>). Landed with the base's direct
    /// push, each entry is merged (<see cref="TakeLanded"/>); else every entry goes back to waiting and is merged one at a time,
    /// as it would be with batching off, the entries of a green batch as already proven green.
    /// </summary>
    private async Task BatchAsync(string code, string repo, CancellationToken cancellationToken)
    {
        if (_batchCold.TryGetValue(repo, out var cold) && time.GetUtcNow() < cold)
        {
            return;
        }
        var ready = new List<(MergeRow Entry, FlightRow Flight)>();
        foreach (var entry in state.Merges().Where(m => m.Repo == repo && m.Open && m.Pr is not null && (m.Flight == code || m.State == MergeEntryState.Waiting))
            .OrderBy(m => m.AddedAt).ThenBy(m => m.Flight, StringComparer.Ordinal).ToList())
        {
            bool taken;
            lock (_turns)
            {
                taken = entry.Flight != code && !_active.Contains(entry.Flight);
            }
            if (taken || _batchTried.ContainsKey(entry.Flight)
                || state.Flights().FirstOrDefault(f => f.Code == entry.Flight) is not { State: FlightState.Flying, Review: { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } reviewed } } flight
                || !(entry.Head.StartsWith(reviewed, StringComparison.OrdinalIgnoreCase) || reviewed.StartsWith(entry.Head, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if ((await github.ChecksSummaryAsync(flight.RepoPath, entry.Pr!.Value, cancellationToken))?.Status != ChoiceStatus.Passing)
            {
                continue;
            }
            ready.Add((entry, flight));
        }
        if (ready.Count < 2 || ready.All(r => r.Entry.Flight != code))
        {
            return;
        }
        foreach (var (entry, _) in ready)
        {
            _batchTried[entry.Flight] = true;
        }
        var leader = ready.First(r => r.Entry.Flight == code).Flight;
        BatchResult result;
        try
        {
            var members = ready.Select(r => new BatchEntry(r.Entry.Flight, r.Entry.Pr!.Value, r.Entry.Head)).ToList();
            foreach (var member in members)
            {
                SetEntry(member.Flight, MergeEntryState.Batched, $"in a batch of {members.Count} PRs: its CI runs once for them");
            }
            result = await Batcher.RunAsync(repo, leader.RepoPath, leader.Base, members, cancellationToken);
        }
        catch (FlightException ex)
        {
            result = new BatchResult("pilot/batch", [], [], false, false, ex.Message);
        }
        if (result.NeverStarted)
        {
            _batchCold[repo] = time.GetUtcNow() + BatchColdFor;
        }
        foreach (var (entry, _) in ready)
        {
            var left = result.LeftOut.FirstOrDefault(l => l.Flight == entry.Flight).Why;
            if (result.Landed && result.Included.Contains(entry.Flight))
            {
                _landedInBatch[entry.Flight] = result.Branch;
                SetEntry(entry.Flight, MergeEntryState.Batched, $"landed with {result.Branch}: its CI passed once for the batch");
            }
            else
            {
                var back = entry.Flight == code ? MergeEntryState.Updating : MergeEntryState.Waiting;
                SetEntry(entry.Flight, back, left is not null ? $"left out of {result.Branch}: {left}; merged one at a time"
                    : result.Green ? $"{result.Branch} was green: merged on its own, already proven green ({result.Why})"
                    : $"{result.Branch} did not pass ({result.Why}): merged one at a time{(result.NeverStarted ? $"; {repo} is not batched again for {BatchColdFor.TotalMinutes:0} min" : "")}");
            }
        }
        Signal();
    }

    /// <summary>The batch a green batch landed the flight with; the entry's command takes it as the flight's merge. Null when none did.</summary>
    private string? TakeLanded(string code) => _landedInBatch.TryRemove(code, out var batch) ? batch : null;

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
                    // GitHub still shows the head before the update: the entry waits for it to show the head the review carried to.
                    if (await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken) is not { } shown || !shown.Head.StartsWith(reviewed, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    await RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [reviewed, entry.Method], Who = "node" }, cancellationToken);
                    break;
            }
        }
    }
}
