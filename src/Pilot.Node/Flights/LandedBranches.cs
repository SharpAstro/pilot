using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>One landed flight to judge: its row, the head its PR merged at, and whether a session of it is still live.</summary>
public sealed record LandedCandidate(FlightRow Flight, string MergedHead, bool LiveSession);

/// <summary>How a branch fared: <see cref="Deleted"/>, <see cref="Kept"/> (with a reason group and why), <see cref="RetryLater"/>, or <see cref="Failed"/> (origin refused the delete).</summary>
public enum LandedBranchResult
{
    Deleted,
    Kept,
    RetryLater,

    /// <summary>Origin refused the delete of this branch (group push-failed); <c>Why</c> carries git's own text.</summary>
    Failed,
}

/// <summary>The reason groups a sweep counts kept branches by.</summary>
public static class LandedBranchGroups
{
    public const string NotFlightBranch = "not-flight-branch";
    public const string LocalAhead = "local-ahead";
    public const string OriginAhead = "origin-ahead";
    public const string CheckedOut = "checked-out";
    public const string LiveSession = "live-session";
    public const string NoMergedHead = "no-merged-head";
    public const string PushFailed = "push-failed";
    public const string ReadFailed = "read-failed";
    public const string LocalDeleteFailed = "local-delete-failed";
    public const string PlaneFolder = "plane-folder";
    public const string FoldedPending = "folded-pending";
    public const string Moved = "moved";
}

/// <summary>The outcome for one flight; <see cref="Commits"/> lists what a branch holds past the merged head (<c>git log --oneline</c> lines).</summary>
public sealed record LandedBranchOutcome(string Code, string Branch, LandedBranchResult Result, string? Group = null, string? Why = null, string[]? Commits = null);

/// <summary>
/// Judges whether a landed flight's branch is safe to delete and deletes the safe ones of one repo (PIL1101): locally with one
/// <c>git branch -D a b c</c> and on origin with one <c>git push --porcelain origin :refs/heads/a ...</c>. Safe means the row's own branch, local and
/// origin tips both equal to the merged head (a missing branch counts as gone), checked out nowhere, and no live session of the flight.
/// Each ref's own answer is read: a refused one comes back as <see cref="LandedBranchResult.Failed"/> with git's text, and is never retried here.
/// </summary>
public sealed class LandedBranches(IGit git)
{
    public async Task<IReadOnlyList<LandedBranchOutcome>> SweepAsync(string repoPath, IReadOnlyList<LandedCandidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }
        var outcomes = new Dictionary<string, LandedBranchOutcome>();
        var judged = new List<(LandedCandidate Candidate, string Branch, string? Local, string? Origin)>();

        var local = await ReadRefsAsync(["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads/flight/"], "refs/heads/", repoPath, cancellationToken);
        var origin = await ReadRefsAsync(["ls-remote", "--heads", "origin", "refs/heads/flight/*"], "refs/heads/", repoPath, cancellationToken);
        var checkedOut = await ReadCheckedOutAsync(repoPath, cancellationToken);
        if (local is null || origin is null || checkedOut is null)
        {
            // A failed read is not "no branch": delete nothing, record nothing, and try again on the next sweep.
            var what = local is null ? "the local branches" : origin is null ? "origin's branches" : "the worktree list";
            return candidates.Select(c => new LandedBranchOutcome(c.Flight.Code, c.Flight.Branch, LandedBranchResult.RetryLater, LandedBranchGroups.ReadFailed, $"could not read {what}")).ToList();
        }

        foreach (var c in candidates)
        {
            var branch = c.Flight.Branch;
            var code = c.Flight.Code;
            LandedBranchOutcome Keep(string group, string why, string[]? commits = null) => new(code, branch, LandedBranchResult.Kept, group, why, commits);

            if (!IsFlightBranch(c.Flight))
            {
                outcomes[code] = Keep(LandedBranchGroups.NotFlightBranch, $"{branch} is not the flight's own branch flight/{code.ToLowerInvariant()}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(c.MergedHead))
            {
                outcomes[code] = Keep(LandedBranchGroups.NoMergedHead, "the head its PR merged at is not known");
                continue;
            }
            if (c.LiveSession)
            {
                outcomes[code] = Keep(LandedBranchGroups.LiveSession, "a session of the flight is still live");
                continue;
            }
            if (checkedOut.Contains(branch))
            {
                outcomes[code] = Keep(LandedBranchGroups.CheckedOut, $"{branch} is checked out in a worktree");
                continue;
            }
            local.TryGetValue(branch, out var localTip);
            origin.TryGetValue(branch, out var originTip);
            if (localTip is not null && localTip != c.MergedHead)
            {
                outcomes[code] = Keep(LandedBranchGroups.LocalAhead, $"local {branch} is at {Short(localTip)}, not the merged head {Short(c.MergedHead)}", await CommitsPastAsync(repoPath, c.MergedHead, localTip, cancellationToken));
                continue;
            }
            if (originTip is not null && originTip != c.MergedHead)
            {
                outcomes[code] = Keep(LandedBranchGroups.OriginAhead, $"origin/{branch} is at {Short(originTip)}, not the merged head {Short(c.MergedHead)}", await CommitsPastAsync(repoPath, c.MergedHead, originTip, cancellationToken));
                continue;
            }
            judged.Add((c, branch, localTip, originTip));
        }

        if (judged.Count > 0)
        {
            var onOrigin = judged.Where(j => j.Origin is not null).Select(j => j.Branch).ToList();
            var originFailure = new Dictionary<string, string>(StringComparer.Ordinal);
            if (onOrigin.Count > 0)
            {
                var push = await git.RunAsync(["push", "--porcelain", "origin", .. onOrigin.Select(b => $":refs/heads/{b}")], repoPath, cancellationToken);
                foreach (var (b, why) in ReadPushAnswers(push, onOrigin))
                {
                    originFailure[b] = why;
                }
            }
            // Only a branch whose origin delete succeeded, or that was never on origin, goes locally.
            var onDisk = judged.Where(j => j.Local is not null && !originFailure.ContainsKey(j.Branch)).Select(j => j.Branch).ToList();
            string? localFailure = null;
            if (onDisk.Count > 0)
            {
                var del = await git.RunAsync(["branch", "-D", .. onDisk], repoPath, cancellationToken);
                if (!del.Ok)
                {
                    localFailure = $"could not delete locally: {del.Why}";
                }
            }
            foreach (var j in judged)
            {
                var code = j.Candidate.Flight.Code;
                outcomes[code] = originFailure.TryGetValue(j.Branch, out var refused)
                    ? new(code, j.Branch, LandedBranchResult.Failed, LandedBranchGroups.PushFailed, $"could not delete on origin: {refused}")
                    : localFailure is not null && j.Local is not null
                        ? new(code, j.Branch, LandedBranchResult.RetryLater, LandedBranchGroups.LocalDeleteFailed, localFailure)
                        : new(code, j.Branch, LandedBranchResult.Deleted);
            }
        }

        return candidates.Select(c => outcomes[c.Flight.Code]).ToList();
    }

    /// <summary>A folded flight's candidate: its branch tips must be contained in the lander's merged head or in main; else an outcome that keeps it.</summary>
    public async Task<(LandedCandidate? Candidate, LandedBranchOutcome? Outcome)> FoldedCandidateAsync(FlightRow other, string? landerHead, bool liveSession, CancellationToken cancellationToken)
    {
        var repo = other.RepoPath;
        var local = await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/heads/{other.Branch}"], repo, cancellationToken);
        var remote = await git.RunAsync(["ls-remote", "--heads", "origin", $"refs/heads/{other.Branch}"], repo, cancellationToken);
        if (!remote.Ok)
        {
            return (null, new LandedBranchOutcome(other.Code, other.Branch, LandedBranchResult.RetryLater, LandedBranchGroups.ReadFailed, $"could not read origin's {other.Branch}: {remote.Why}"));
        }
        var localTip = local.Ok ? local.Output.Trim() : null;
        var originTip = remote.Output.Split(['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var tips = new[] { localTip, originTip }.OfType<string>().Where(t => t.Length > 0).Distinct().ToList();
        if (tips.Count == 0)
        {
            // Nothing of it is left anywhere: its branch is gone already.
            return (null, new LandedBranchOutcome(other.Code, other.Branch, LandedBranchResult.Deleted));
        }
        var mainRef = other.Base.Length > 0 ? other.Base : "origin/main";
        var ahead = new List<string>();
        foreach (var tip in tips)
        {
            var contained = landerHead is not null && (await git.RunAsync(["merge-base", "--is-ancestor", tip, landerHead], repo, cancellationToken)).Ok
                || (await git.RunAsync(["merge-base", "--is-ancestor", tip, mainRef], repo, cancellationToken)).Ok;
            if (!contained)
            {
                var log = await git.RunAsync(["log", "--oneline", tip, "--not", mainRef, .. (landerHead is null ? [] : new[] { landerHead })], repo, cancellationToken);
                ahead.AddRange(log.Ok ? log.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : []);
            }
        }
        if (ahead.Count > 0)
        {
            var where = localTip is not null && originTip is null ? LandedBranchGroups.LocalAhead : LandedBranchGroups.OriginAhead;
            return (null, new LandedBranchOutcome(other.Code, other.Branch, LandedBranchResult.Kept, where,
                $"{other.Branch} holds work in neither the merged head of {other.FoldedInto} nor {mainRef}", [.. ahead.Distinct()]));
        }
        return (new LandedCandidate(other, localTip ?? originTip!, liveSession), null);
    }

    private static bool IsFlightBranch(FlightRow flight) =>
        flight.Branch == $"flight/{flight.Code.ToLowerInvariant()}" && flight.Branch.IndexOfAny(['*', '?', '[', ' ']) < 0;

    /// <summary>
    /// Reads a <c>git push --porcelain</c> of delete refspecs per ref: <c>-</c> is deleted, <c>!</c> is refused with its reason, and a
    /// ref answered "remote ref does not exist" is gone already. Returns the refused branches with git's whole stderr (trimmed, the
    /// "To" line dropped, capped); when the output names no ref at all, git failed before talking to origin and every branch carries it.
    /// </summary>
    private static Dictionary<string, string> ReadPushAnswers(GitRun push, IReadOnlyList<string> branches)
    {
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (push.Ok)
        {
            return failures;
        }
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in push.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].Length != 1 || !"-!*=+ ".Contains(parts[0][0]))
            {
                continue;
            }
            var spec = parts[1];
            var dst = spec[(spec.IndexOf(':') + 1)..];
            if (!dst.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                continue;
            }
            var branch = dst["refs/heads/".Length..];
            answered.Add(branch);
            if (parts[0] == "!" && !line.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase))
            {
                rejected[branch] = line;
            }
        }
        var stderr = TrimStderr(push.Error);
        foreach (var b in branches)
        {
            // A ref git calls "unable to delete '<ref>': remote ref does not exist" is gone already, not refused.
            if (push.Error.Contains($"unable to delete '{b}': remote ref does not exist", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (rejected.ContainsKey(b) || answered.Count == 0)
            {
                failures[b] = stderr.Length > 0 ? stderr : rejected.GetValueOrDefault(b) ?? push.Why;
            }
        }
        return failures;
    }

    private static string TrimStderr(string error)
    {
        var kept = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("To ", StringComparison.Ordinal));
        var text = string.Join("; ", kept);
        return text.Length > 1000 ? text[..1000] + "..." : text;
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>Reads <c>&lt;sha&gt; &lt;ref&gt;</c> lines (for-each-ref's format) or <c>&lt;sha&gt;\t&lt;ref&gt;</c> (ls-remote's) into branch name to tip.</summary>
    private async Task<Dictionary<string, string>?> ReadRefsAsync(IReadOnlyList<string> args, string prefix, string repoPath, CancellationToken cancellationToken)
    {
        var run = await git.RunAsync(args, repoPath, cancellationToken);
        if (!run.Ok)
        {
            return null;
        }
        var map = new Dictionary<string, string>();
        foreach (var line in run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].StartsWith(prefix, StringComparison.Ordinal))
            {
                map[parts[1][prefix.Length..]] = parts[0];
            }
        }
        return map;
    }

    private async Task<HashSet<string>?> ReadCheckedOutAsync(string repoPath, CancellationToken cancellationToken)
    {
        var run = await git.RunAsync(["worktree", "list", "--porcelain"], repoPath, cancellationToken);
        if (!run.Ok)
        {
            return null;
        }
        var set = new HashSet<string>();
        foreach (var line in run.Output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                set.Add(line["branch refs/heads/".Length..]);
            }
        }
        return set;
    }

    /// <summary>The commits of <paramref name="tip"/> past <paramref name="head"/>, one line each; empty when git cannot say (an origin tip never fetched).</summary>
    private async Task<string[]> CommitsPastAsync(string repoPath, string head, string tip, CancellationToken cancellationToken)
    {
        var run = await git.RunAsync(["log", "--oneline", $"{head}..{tip}"], repoPath, cancellationToken);
        return run.Ok
            ? run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }
}
