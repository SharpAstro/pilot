using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>The merge queue's entries, oldest first (the order of every repo's queue).</summary>
    public MergeRow[] Merges()
    {
        lock (_gate)
        {
            return [.. _merges.Values.OrderBy(m => m.AddedAt).ThenBy(m => m.Flight, StringComparer.Ordinal)];
        }
    }

    /// <summary>Puts a merge entry: added when new, changed when it differs, nothing when it is the same.</summary>
    public void PutMerge(MergeRow merge)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(merge.Flight, out var held))
            {
                if (held != merge)
                {
                    Publish(new MergeChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Merge = merge });
                }
            }
            else
            {
                Publish(new MergeAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Merge = merge });
            }
        }
    }

    /// <summary>Ends a flight's open merge entry (merged, or dropped with <paramref name="why"/>), and renumbers its repo's queue; nothing when it has none open.</summary>
    public void EndMerge(string flight, MergeEntryState to, string? why)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(flight, out var held) && held.Open)
            {
                PutMerge(held with { State = to, Why = why, Place = null, ChangedAt = _time.GetUtcNow() });
                Renumber(held.Repo);
            }
        }
    }

    /// <summary>
    /// Ends a flight's merge entry as merged whatever state it stands in, one already dropped included: its PR merged, and a look or a
    /// refused merge that dropped it just before is not the last word. Nothing when it has none, or it is merged already.
    /// </summary>
    public void EndMergeAsMerged(string flight, string? why)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(flight, out var held) && held.State != MergeEntryState.Merged)
            {
                PutMerge(held with { State = MergeEntryState.Merged, Why = why, Place = null, AwaitsReview = false, ChangedAt = _time.GetUtcNow() });
                Renumber(held.Repo);
            }
        }
    }

    /// <summary>Sets every open entry of <paramref name="repo"/>'s place: 1 for the one that joined first.</summary>
    public void Renumber(string repo)
    {
        lock (_gate)
        {
            var place = 0;
            foreach (var entry in _merges.Values.Where(m => m.Repo == repo && m.Open).OrderBy(m => m.AddedAt).ThenBy(m => m.Flight, StringComparer.Ordinal).ToList())
            {
                place++;
                PutMerge(entry with { Place = place });
            }
        }
    }

    /// <summary>Takes a flight's merge entry out of the queue; nothing happens when there is none.</summary>
    public void RemoveMerge(string flight)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(flight, out var held))
            {
                Publish(new MergeRemovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Flight = flight });
                Renumber(held.Repo);
            }
        }
    }
}
