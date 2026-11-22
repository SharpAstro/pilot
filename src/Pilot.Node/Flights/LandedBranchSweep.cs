using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Deletes the branches of flights that landed before the node did it at landing (PIL1101): every <see cref="Interval"/> it walks the landed
/// and the archived flights whose branch is not yet recorded as deleted, finds each one's merged head (the row's own, else its merged
/// merge entry's, else GitHub's list of merged PRs, read once per repo per sweep), and has <see cref="LandedBranches"/> judge them a few at
/// a time per repo, with a pause between batches and the state's lock never held. A failed push leaves its branches for the next sweep. One
/// Handled line counts the outcome by reason, repeated only when it changes. Off while the delete-landed-branches setting is; the first
/// sweep waits <see cref="StartDelay"/>, so starting never competes with it.
/// </summary>
public sealed class LandedBranchSweep(NodeState state, LandedBranches branches, IGitHub? github, TimeProvider time) : BackgroundService
{
    /// <summary>What the one summary Handled line of a sweep is.</summary>
    public const string SummaryKind = "branch-sweep";

    /// <summary>How many merged PRs one read of GitHub asks for.</summary>
    public const int MergedPrLimit = 1000;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many branches of one repo go in one judgement, which is one push to origin.</summary>
    public int Batch { get; init; } = 10;

    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether the node deletes landed branches at all, read at each sweep.</summary>
    public Func<bool> Enabled { get; init; } = () => true;

    public TextWriter Error { get; set; } = TextWriter.Null;

    private string? _lastSummary;

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
                    await Error.WriteLineAsync($"landed branch sweep failed: {ex.Message}");
                }
                await Task.Delay(Interval, time, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private sealed record Entry(FlightRow Flight, ArchivedFlight? Whole);

    /// <summary>One sweep; returns how many branches it deleted.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled())
        {
            return 0;
        }
        var entries = Candidates();
        var deleted = 0;
        var failed = 0;
        var retry = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(LandedBranchOutcome o)
        {
            switch (o.Result)
            {
                case LandedBranchResult.Deleted:
                    deleted++;
                    break;
                case LandedBranchResult.Failed:
                    failed++;
                    break;
                case LandedBranchResult.RetryLater:
                    retry[o.Group ?? "retry"] = retry.GetValueOrDefault(o.Group ?? "retry") + 1;
                    break;
                default:
                    kept[o.Group ?? "kept"] = kept.GetValueOrDefault(o.Group ?? "kept") + 1;
                    break;
            }
        }

        foreach (var repo in entries.GroupBy(e => e.Flight.RepoPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var refused = new List<LandedBranchOutcome>();
            try
            {
                await SweepRepoAsync(repo.Key, [.. repo], Count, refused, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One repo that cannot be judged (a vanished folder, a git that will not start) never stops the ones after it.
                await Error.WriteLineAsync($"landed branch sweep: {repo.Key} not swept: {ex.Message}");
                kept["an error"] = kept.GetValueOrDefault("an error") + repo.Count();
            }
            if (refused.Count > 0)
            {
                RaisePushFailedCard(state, Verbs.RepoTarget(repo.Key), refused);
            }
        }

        var keptTotal = kept.Values.Sum();
        var retryTotal = retry.Values.Sum();
        if (deleted + retryTotal + keptTotal + failed > 0)
        {
            var parts = new List<string> { $"deleted {deleted}" };
            static string Groups(Dictionary<string, int> counts) =>
                string.Join(", ", counts.OrderByDescending(g => g.Value).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Value} {g.Key}"));
            if (retryTotal > 0)
            {
                parts.Add($"{retryTotal} tried again next sweep: {Groups(retry)}");
            }
            if (failed > 0)
            {
                parts.Add($"{failed} push-failed, left until answered");
            }
            var groups = Groups(kept);
            var title = $"branches: {string.Join(", ", parts)}, kept {keptTotal}{(keptTotal > 0 ? ": " + groups : "")}";
            if (deleted > 0 || _lastSummary != title)
            {
                state.RaiseCard(CardBin.Handled, SummaryKind, "branches", title);
            }
            _lastSummary = title;
        }
        return deleted;
    }

    /// <summary>The landed flights, live and archived, whose branch is not recorded deleted; an archived one is read from its file for its repo.</summary>
    private List<Entry> Candidates()
    {
        var list = new List<Entry>();
        foreach (var f in state.Flights().Where(f => f.State == FlightState.Landed && f.BranchDeletedAt is null && f.BranchDeleteFailed is null && f.Branch.Length > 0))
        {
            list.Add(new Entry(f, null));
        }
        if (state.Archive is { } archive)
        {
            foreach (var row in state.ArchivedFlightRows().Where(r => r.BranchDeletedAt is null))
            {
                if (archive.Read(row.Code) is { } whole && whole.Flight.BranchDeletedAt is null && whole.Flight.BranchDeleteFailed is null && whole.Flight.Branch.Length > 0)
                {
                    list.Add(new Entry(whole.Flight, whole));
                }
            }
        }
        return list;
    }

    private async Task SweepRepoAsync(string repoPath, List<Entry> entries, Action<LandedBranchOutcome> count, List<LandedBranchOutcome> refused, CancellationToken cancellationToken)
    {
        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        MergedPullRequest[]? merged = null;
        var mergedRead = false;
        var candidates = new List<(Entry Entry, LandedCandidate Candidate)>();
        var sessions = state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed)).Select(s => s.Flight).ToHashSet(StringComparer.Ordinal);

        void Settle(Entry entry, LandedBranchOutcome outcome)
        {
            count(outcome);
            if (outcome.Result == LandedBranchResult.Failed)
            {
                refused.Add(outcome);
            }
            Record(entry, outcome, heads.GetValueOrDefault(entry.Flight.Code));
        }

        foreach (var entry in entries)
        {
            var flight = entry.Flight;
            LandedBranchOutcome Keep(string group, string why) => new(flight.Code, flight.Branch, LandedBranchResult.Kept, group, why);
            if (!Directory.Exists(repoPath))
            {
                Settle(entry, Keep("repo-gone", $"the repo folder {repoPath} is gone"));
                continue;
            }
            if (flight.MovedTo is { } to)
            {
                Settle(entry, Keep(LandedBranchGroups.Moved, $"{flight.Code} went to {to.To}, whose landing handles origin"));
                continue;
            }
            if (flight.Worktree.Length > 0 && Directory.Exists(flight.Worktree))
            {
                Settle(entry, Keep(LandedBranchGroups.PlaneFolder, "its plane folder is still on disk"));
                continue;
            }
            var live = sessions.Contains(flight.Code);
            if (flight.FoldedInto is { } into)
            {
                var lander = FindFlight(into);
                if (lander is null || lander.State != FlightState.Landed)
                {
                    Settle(entry, Keep(LandedBranchGroups.FoldedPending, $"{into}, which it was folded into, has not landed"));
                    continue;
                }
                var (folded, outcome) = await branches.FoldedCandidateAsync(flight, lander.LandedHead, live, cancellationToken);
                if (folded is null)
                {
                    Settle(entry, outcome!);
                    continue;
                }
                heads[flight.Code] = folded.MergedHead;
                candidates.Add((entry, folded));
                continue;
            }
            var head = flight.LandedHead ?? MergedEntryHead(flight.Code);
            var number = flight.PullRequest ?? flight.MergedPullRequest;
            var answered = false;
            if (head is null && number is { } n && github is not null && flight.Repo.Contains('/'))
            {
                // By number first: the list is capped, so a flight older than its last MergedPrLimit PRs is only found this way.
                try
                {
                    var pr = await github.PullRequestOfAsync(flight.Repo, n, cancellationToken);
                    answered = true;
                    // gh pr view is not asked for mergedAt, so merged-ness is the state, as the landing reads it.
                    if (string.Equals(pr.State, "MERGED", StringComparison.OrdinalIgnoreCase) && pr.HeadRef == flight.Branch && pr.Head.Length > 0)
                    {
                        head = pr.Head;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // GitHub could not say for this PR; the list below may.
                }
            }
            if (head is null && !answered)
            {
                if (!mergedRead)
                {
                    mergedRead = true;
                    merged = github is null ? null : await github.MergedPullRequestsAsync(repoPath, MergedPrLimit, cancellationToken);
                }
                head = merged?.Where(p => p.HeadRef == flight.Branch && (number is null || p.Number == number)).OrderByDescending(p => p.Number).FirstOrDefault()?.HeadOid;
            }
            if (head is null)
            {
                Settle(entry, Keep(LandedBranchGroups.NoMergedHead, "GitHub does not say its PR merged at a known head"));
                continue;
            }
            heads[flight.Code] = head;
            candidates.Add((entry, new LandedCandidate(flight, head, live)));
        }

        for (var i = 0; i < candidates.Count; i += Math.Max(1, Batch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = candidates.Skip(i).Take(Math.Max(1, Batch)).ToList();
            var outcomes = await branches.SweepAsync(repoPath, [.. batch.Select(b => b.Candidate)], cancellationToken);
            foreach (var (entry, _) in batch)
            {
                Settle(entry, outcomes.First(o => o.Code == entry.Flight.Code));
            }
            if (i + Math.Max(1, Batch) < candidates.Count)
            {
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
    }

    /// <summary>
    /// The one For-you card of a repo's refused deletes (PIL1265): every branch with git's text, and the two answers. Retry clears the
    /// record so the next sweep pushes once more; leaving them (Mark it read) keeps the sweeps and landings from pushing for them.
    /// </summary>
    public static void RaisePushFailedCard(NodeState state, string subject, IReadOnlyList<LandedBranchOutcome> refused)
    {
        var codes = refused.Select(o => o.Code).ToArray();
        var lines = string.Join("; ", refused.Select(o => $"{o.Code} ({o.Branch}): {o.Why}"));
        var many = refused.Count == 1 ? "its branch" : $"{refused.Count} branches";
        state.RaiseCard(CardBin.ForYou, FlightCommander.BranchKeptKind, subject,
            refused.Count == 1 ? $"{codes[0]} landed, but origin refused deleting its branch {refused[0].Branch}" : $"{refused.Count} landed branches: origin refused deleting them",
            $"Origin refused the delete of {many}, pushed once and now left until you answer: {lines}. Try deleting again lets the next sweep push once more; Leave them keeps the node from pushing for them again.",
            [
                new CardChoice { Label = "Try deleting again", Verb = Verbs.BranchRetry, Target = Verbs.FlightTarget(codes[0]), Args = [.. codes.Skip(1)] },
                new CardChoice { Label = "Leave them", Verb = Verbs.Dismiss, Target = "" },
            ]);
    }

    private FlightRow? FindFlight(string code) =>
        state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? (state.ArchivedRow(code) is { } row ? state.Archive?.Read(row.Code)?.Flight : null);

    private string? MergedEntryHead(string code) =>
        state.Merges().FirstOrDefault(m => m.Flight == code && m.State == MergeEntryState.Merged) is { Head.Length: > 0 } entry ? entry.Head : null;

    /// <summary>Writes the outcome on the live row, or into the archived flight's file (only when something changed there).</summary>
    private void Record(Entry entry, LandedBranchOutcome outcome, string? head)
    {
        if (outcome.Result is LandedBranchResult.RetryLater)
        {
            return;
        }
        var now = time.GetUtcNow();
        FlightRow Apply(FlightRow f) => outcome.Result switch
        {
            LandedBranchResult.Deleted => f with { BranchDeletedAt = now, BranchKept = null, BranchDeleteFailed = null, BranchDeleteFailedAt = null, LandedHead = f.LandedHead ?? head },
            LandedBranchResult.Failed => f with { BranchKept = outcome.Why, BranchDeleteFailed = outcome.Why, BranchDeleteFailedAt = now, LandedHead = f.LandedHead ?? head },
            _ => f with { BranchKept = outcome.Why, LandedHead = f.LandedHead ?? head },
        };

        if (entry.Whole is not { } whole)
        {
            // A kept branch told the same reason again changes nothing, so a sweep every 10 minutes writes no event for it.
            if (state.Flights().FirstOrDefault(f => f.Code == entry.Flight.Code) is { } current && Apply(current) != current)
            {
                state.UpdateFlight(entry.Flight.Code, Apply);
            }
            return;
        }
        var flight = Apply(whole.Flight);
        if (flight == whole.Flight || state.ArchivedRow(whole.Row.Code) is null)
        {
            return;
        }
        state.Archive!.Save(whole with { Flight = flight, Row = whole.Row with { BranchDeletedAt = flight.BranchDeletedAt } });
    }
}
