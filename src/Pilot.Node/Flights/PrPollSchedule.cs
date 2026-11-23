using Pilot.Contracts;
using System.Collections.Concurrent;

namespace Pilot.Node.Flights;

/// <summary>
/// Poll by state, not by clock (pilot #1293, Want 2): each flight PR (keyed by repo and branch) has its own next-read time, and a look
/// reads a repo's open PRs only when one of its flights' PRs is due; a PR not due is answered from the last read. In memory only: a
/// restarted node reads everything at its first look. What the node does to a PR itself (a push, a merge queued or armed, a close) marks
/// it due at once (<see cref="MarkDue"/>), so nothing the node did waits out a long interval to be seen.
/// </summary>
internal sealed class PrPollSchedule(TimeProvider time)
{
    /// <summary>Right after the node pushed the branch, queued or armed the merge, or the head changed: the PR's state is moving fast and a person is waiting on it.</summary>
    public static readonly TimeSpan JustChanged = TimeSpan.FromSeconds(30);

    /// <summary>The least wait while CI runs: a run is never finished sooner than this is worth asking, and it keeps a repo to one read per 2 minutes.</summary>
    public static readonly TimeSpan MinCiWait = TimeSpan.FromMinutes(2);

    /// <summary>How long this repo's CI usually runs when nothing says otherwise: a PR whose checks started a moment ago is not asked before about this much of it has passed.</summary>
    public static readonly TimeSpan UsualCiRun = TimeSpan.FromMinutes(8);

    /// <summary>The wait once CI has run longer than <see cref="UsualCiRun"/>: it ends any minute, but not one to poll every 2 minutes.</summary>
    public static readonly TimeSpan CiOverrunWait = TimeSpan.FromMinutes(5);

    /// <summary>A merge sitting in GitHub's queue: it leaves at its own pace, so a minute (after the first 30 s) sees it merged or dropped.</summary>
    public static readonly TimeSpan QueuedWait = TimeSpan.FromMinutes(1);

    /// <summary>A red, conflicting, draft or unknown PR waits on the node's own fix (which marks it due when it pushes) or on a person: the least wait.</summary>
    public static readonly TimeSpan StuckWait = MinCiWait;

    private sealed record Entry(DateTimeOffset NextRead, string Head, DateTimeOffset? PendingSince, int PassingReads);

    private readonly ConcurrentDictionary<(string Repo, string Branch), Entry> _prs = new();

    // The last batch read of each repo: what a PR not due is answered from.
    private readonly ConcurrentDictionary<string, OpenPullRequest[]> _last = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last batch read of the repo; null before the first one.</summary>
    public OpenPullRequest[]? Last(string repoPath) => _last.TryGetValue(repoPath, out var last) ? last : null;

    /// <summary>True when the repo has never been read, or one of <paramref name="branches"/> has no next-read time yet or its time has come.</summary>
    public bool Due(string repoPath, IEnumerable<string> branches)
    {
        if (!_last.ContainsKey(repoPath))
        {
            return true;
        }
        var now = time.GetUtcNow();
        return branches.Any(b => !_prs.TryGetValue((repoPath, b), out var entry) || entry.NextRead <= now);
    }

    /// <summary>The node did something to the PR of <paramref name="branch"/> (pushed it, queued or armed its merge, closed it): it is read again within <paramref name="within"/> at the latest.</summary>
    public void MarkDue(string repoPath, string branch, TimeSpan? within = null)
    {
        var at = time.GetUtcNow() + (within ?? JustChanged);
        _prs.AddOrUpdate((repoPath, branch), _ => new Entry(at, "", null, 0), (_, e) => e.NextRead <= at ? e : e with { NextRead = at });
    }

    /// <summary>A write to the repo that may change any PR (a commit status): every PR it knows is read again within 30 s.</summary>
    public void MarkRepoDue(string repoPath)
    {
        foreach (var key in _prs.Keys.Where(k => string.Equals(k.Repo, repoPath, StringComparison.OrdinalIgnoreCase)))
        {
            MarkDue(key.Repo, key.Branch);
        }
    }

    /// <summary>The node wrote to PR <paramref name="number"/>: the same as <see cref="MarkDue"/>, for a writer that knows the number and not the branch (every PR of a repo not known by number is marked).</summary>
    public void MarkDueNumber(string repoPath, int number)
    {
        if (Last(repoPath)?.FirstOrDefault(p => p.Pull.Number == number) is { } known)
        {
            MarkDue(repoPath, known.HeadBranch);
            return;
        }
        MarkRepoDue(repoPath);
    }

    /// <summary>A batch read of the repo: it is the new last read, and each of <paramref name="branches"/> gets its next-read time from what the batch says of its PR.</summary>
    public void Read(string repoPath, IEnumerable<string> branches, OpenPullRequest[] batch)
    {
        _last[repoPath] = batch;
        var now = time.GetUtcNow();
        foreach (var branch in branches)
        {
            var pr = batch.FirstOrDefault(p => p.HeadBranch == branch);
            _prs.AddOrUpdate((repoPath, branch), _ => Next(null, pr, now), (_, before) => Next(before, pr, now));
        }
    }

    private static Entry Next(Entry? before, OpenPullRequest? pr, DateTimeOffset now)
    {
        if (pr is null)
        {
            return new Entry(now + StuckWait, "", null, 0);
        }
        var head = pr.Pull.Head;
        var changed = before is { Head.Length: > 0 } && before.Head != head;
        if (changed)
        {
            return new Entry(now + JustChanged, head, null, 0);
        }
        var queued = pr.Queue is { Queued: true } || pr.Merge?.Kind == MergeKinds.Queued;
        if (queued)
        {
            return new Entry(now + QueuedWait, head, null, 0);
        }
        switch (pr.Checks?.Status)
        {
            case ChoiceStatus.Pending:
                var since = before?.PendingSince ?? now;
                var remaining = UsualCiRun - (now - since);
                var wait = remaining > TimeSpan.Zero ? (remaining < MinCiWait ? MinCiWait : remaining) : CiOverrunWait;
                return new Entry(now + wait, head, since, 0);
            case ChoiceStatus.Passing:
                var reads = (before?.PassingReads ?? 0) + 1;
                return new Entry(now + PrChecks.PassingBackoff[Math.Min(reads, PrChecks.PassingBackoff.Length) - 1], head, null, reads);
            default:
                return new Entry(now + StuckWait, head, null, 0);
        }
    }
}
