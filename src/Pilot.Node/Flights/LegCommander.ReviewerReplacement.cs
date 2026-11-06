using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A reviewer replaced by a fresh one (pilot #777): at the same head its queued jobs stay for the replacement, whose run of the same
/// command adopts the job (<see cref="JobRunner"/>) and whose end the predecessor's jobs already reach (#601); a moved head drops them.
/// The old reviewer is marked as being replaced until the replacement's row is in, so the tracker does not judge it ended for good
/// and drop the jobs kept for its successor.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>Session ids of reviewers being replaced (marked by StartReviewAsync, cleared when the launch ends); the tracker keeps their jobs meanwhile.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _replacing = new(StringComparer.Ordinal);

    // A launch that fails leaves its mark, so a mark lapses on its own. The tracker looks at an ended row once only, so a row it
    // passed over while marked is not looked at again when the mark lapses: its jobs stay queued (and run, telling nobody but a
    // replacement). The node's own stop never reaches that look anyway (it marks the row stopped, not ended on its own), so the
    // mark matters only for a reviewer that ended by itself within a poll or two of being replaced.
    private static readonly TimeSpan ReplacingFor = TimeSpan.FromMinutes(5);

    /// <summary>What one start's replacement of a reviewer left: the ids it marked, and whose queued jobs it kept (null when it dropped them).</summary>
    private sealed record ReviewerReplacement(IReadOnlyList<string> Marked, string? KeptJobsOf);

    /// <summary>Whether a reviewer is being replaced: its queued jobs wait for the replacement to adopt them.</summary>
    public bool IsBeingReplaced(string id) => _replacing.TryGetValue(id, out var since) && time.GetUtcNow() - since < ReplacingFor;

    /// <summary>
    /// Replaces the flight's reviewer <paramref name="last"/>: keeps its queued jobs when the plane's head is the one it reviewed, drops
    /// them otherwise, marks it as being replaced and stops it.
    /// </summary>
    private async Task<ReviewerReplacement> ReplaceReviewerAsync(FlightRow flight, string last, CancellationToken cancellationToken)
    {
        // The same head keeps the old reviewer's queued jobs: their end goes to the replacement (#601) and its own run of the
        // same command adopts the job, so the build the predecessor queued is not thrown away. A moved head makes them stale.
        var nowHead = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, CancellationToken.None);
        var sameHead = flight.Review?.ReviewedHead is { Length: > 0 } reviewed && nowHead.Ok && nowHead.Output.Trim() == reviewed;
        if (!sameHead)
        {
            DropQueuedJobsOf(last, "its reviewer was replaced");
        }
        // Marked before the stop: the stopped row must not be judged ended for good before the replacement's row exists.
        var marked = new List<string> { last };
        if (state.Sessions().FirstOrDefault(s => s.Id == last || s.SessionId == last) is { } oldRow)
        {
            marked.Add(oldRow.Id);
            marked.Add(oldRow.SessionId);
        }
        foreach (var id in marked)
        {
            _replacing[id] = time.GetUtcNow();
        }
        await StopQuietly(last, cancellationToken);
        return new ReviewerReplacement(marked, sameHead ? last : null);
    }

    /// <summary>
    /// A start held before its reviewer launches: no replacement comes now (a later start finds no reviewer to adopt from), so the jobs
    /// kept for one would run for nobody and go; the marks come off.
    /// </summary>
    private void NoReplacementComes(ReviewerReplacement? replacement)
    {
        if (replacement?.KeptJobsOf is { } kept)
        {
            DropQueuedJobsOf(kept, "its reviewer was replaced");
        }
        Unmark(replacement);
    }

    /// <summary>Takes this start's marks off, and only these: another flight's reviewer may be being replaced at the same time.</summary>
    private void Unmark(ReviewerReplacement? replacement)
    {
        foreach (var id in replacement?.Marked ?? [])
        {
            _replacing.TryRemove(id, out _);
        }
    }
}
