using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>One PR that asks to join a batch: its flight, number and the head merged in.</summary>
public sealed record BatchEntry(string Flight, int Pr, string Head);

/// <summary>
/// What a batch came to: <paramref name="Branch"/> (the batch branch it built), <paramref name="Included"/> the flights merged into
/// it, <paramref name="LeftOut"/> those that conflicted (flight and why), <paramref name="Green"/> whether its CI passed (why not in
/// <paramref name="Why"/>), and <paramref name="Landed"/> whether the base was fast-forwarded to it, which lands every PR at once.
/// <paramref name="NeverStarted"/>: no check and no run appeared on it within <see cref="MergeBatcher.StartBudget"/>.
/// </summary>
public sealed record BatchResult(string Branch, IReadOnlyList<string> Included, IReadOnlyList<(string Flight, string Why)> LeftOut, bool Green, bool Landed, string? Why, bool NeverStarted = false);

/// <summary>
/// A repo's ready merge queue entries as one batch (off unless <c>merge-batch</c> is on): a branch <c>pilot/batch/&lt;repo&gt;-&lt;n&gt;</c>
/// of the base with each PR head merged in (<c>git merge --no-edit</c>), pushed plainly, and a draft PR opened from it to the base: the
/// repos' CI runs on a <c>pull_request</c> (and a push to the base), not on a push of any branch, so the draft PR is what starts it.
/// Its CI is awaited once, read from that PR's checks and the runs of the batch head. On green the base is fast-forwarded to the
/// batch head where the repo allows a direct push (GitHub then shows each PR merged, as merge commits whatever the merge method);
/// where it does not, the caller merges each PR as a single entry already proven green. A PR whose merge conflicts is left out; the
/// draft PR is closed and the batch branch deleted again, local and remote. Never a rebase and never a force push.
/// </summary>
public sealed class MergeBatcher(IGit git, IGitHub github)
{
    private int _next;

    /// <summary>How often the batch's CI is looked at.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the batch's CI may take to show a check or a run at all; none by then, it has not started and the batch falls back.</summary>
    public TimeSpan StartBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the batch's CI may take, once started, before the batch counts as red.</summary>
    public TimeSpan CiBudget { get; set; } = TimeSpan.FromMinutes(45);

    public async Task<BatchResult> RunAsync(string repo, string repoPath, string baseBranch, IReadOnlyList<BatchEntry> entries, CancellationToken cancellationToken)
    {
        var branch = $"pilot/batch/{repo}-{Interlocked.Increment(ref _next)}";
        var folder = Path.Combine(Path.GetTempPath(), $"pilot-batch-{Guid.NewGuid():N}");
        var included = new List<string>();
        var leftOut = new List<(string, string)>();
        var made = false;
        int? pr = null;
        try
        {
            if (await Fail(["fetch", "--quiet", "origin"], repoPath, "fetching origin") is { } fetch)
            {
                return Red(fetch);
            }
            // A batch branch of an earlier node that went away is no use: the remote name is taken afresh (a plain delete, no force).
            await git.RunAsync(["push", "--quiet", "origin", "--delete", branch], repoPath, cancellationToken);
            if (await Fail(["worktree", "add", "-B", branch, folder, $"origin/{baseBranch}"], repoPath, "making the batch worktree") is { } add)
            {
                return Red(add);
            }
            made = true;
            foreach (var entry in entries)
            {
                var merge = await git.RunAsync(["merge", "--no-edit", entry.Head], folder, cancellationToken);
                if (merge.Ok)
                {
                    included.Add(entry.Flight);
                    continue;
                }
                await git.RunAsync(["merge", "--abort"], folder, cancellationToken);
                leftOut.Add((entry.Flight, $"#{entry.Pr} conflicts in the batch ({merge.Why})"));
            }
            if (included.Count < 2)
            {
                return new BatchResult(branch, included, leftOut, false, false, "fewer than two entries merge cleanly into a batch");
            }
            var head = (await git.RunAsync(["rev-parse", "HEAD"], folder, cancellationToken)).Output.Trim();
            if (await Fail(["push", "--quiet", "origin", branch], folder, "pushing the batch") is { } push)
            {
                return new BatchResult(branch, included, leftOut, false, false, push);
            }
            var numbers = string.Join(", ", entries.Where(e => included.Contains(e.Flight)).Select(e => $"#{e.Pr}"));
            pr = await github.OpenDraftAsync(repoPath, branch, baseBranch, $"pilot merge batch: {numbers}",
                $"Opened by pilot's merge queue so CI runs once on {numbers} merged together into {baseBranch}. Not for review or merging: it is closed, and {branch} deleted, when the batch ends.",
                cancellationToken);
            if (await CiAsync(repoPath, pr.Value, head, cancellationToken) is { } red)
            {
                return new BatchResult(branch, included, leftOut, false, false, red.Why, red.NeverStarted);
            }
            // A plain push of the base to the batch head: refused unless it is a fast-forward the repo allows a direct push for.
            var landed = (await git.RunAsync(["push", "--quiet", "origin", $"{head}:refs/heads/{baseBranch}"], folder, cancellationToken)).Ok;
            return new BatchResult(branch, included, leftOut, true, landed, landed ? null : $"{baseBranch} takes no direct push: each PR is merged on its own");
        }
        catch (FlightException ex)
        {
            // gh could not open the draft PR or read its CI: the batch did not pass, and its entries go on one at a time.
            return Red(ex.Message);
        }
        finally
        {
            await Clean(branch, folder, repoPath, made, pr);
        }

        BatchResult Red(string why) => new(branch, included, leftOut, false, false, why);
    }

    private async Task<string?> Fail(string[] args, string cwd, string doing)
    {
        var run = await git.RunAsync(args, cwd, CancellationToken.None);
        return run.Ok ? null : $"{doing} failed: {run.Why}";
    }

    // Null when the draft PR's checks passed and every run of the head completed; else why not. A check passing while a run of the
    // head still goes is not yet green (a gate job is made only once the builds end). Nothing at all within StartBudget is a batch
    // whose CI never started: it falls back at once rather than hold the repo's turn for the whole CiBudget.
    private async Task<(string Why, bool NeverStarted)?> CiAsync(string repoPath, int pr, string head, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var runs = await github.WorkflowRunsAsync(repoPath, head, cancellationToken);
            var checks = await github.ChecksSummaryAsync(repoPath, pr, cancellationToken);
            if (runs.FirstOrDefault(r => r.Status == "completed" && r.Conclusion is not (null or "success" or "skipped" or "neutral")) is { } failed)
            {
                return ($"the batch's CI failed: {failed.Name} {failed.Conclusion} ({failed.Url})", false);
            }
            if (checks is { Status: ChoiceStatus.Failing } red)
            {
                return ($"the batch's CI failed: {red.FailedName ?? "a check"} on draft PR #{pr}{(red.FailedLink is { } link ? $" ({link})" : "")}", false);
            }
            var runsDone = runs.All(r => r.Status == "completed");
            if (checks is { Status: ChoiceStatus.Passing } && runsDone)
            {
                return null;
            }
            if (checks is null && runs.Length > 0 && runsDone)
            {
                return ($"the batch's CI ran no check on draft PR #{pr} (every run skipped)", false);
            }
            if (checks is null && runs.Length == 0 && waited >= StartBudget)
            {
                return ($"the batch's CI never started: no check or run on draft PR #{pr} after {StartBudget.TotalMinutes:0.#} min", true);
            }
            if (waited >= CiBudget)
            {
                return ("the batch's CI did not finish in time", false);
            }
            await Task.Delay(PollEvery, cancellationToken);
            waited += PollEvery;
        }
    }

    // The batch is gone again whatever came of it: its draft PR closed, worktree, local branch, remote branch.
    private async Task Clean(string branch, string folder, string repoPath, bool made, int? pr)
    {
        if (pr is { } number)
        {
            try
            {
                await github.ClosePullRequestAsync(repoPath, number, $"pilot's merge batch {branch} has ended; its PRs go on in the merge queue.", CancellationToken.None);
            }
            catch (FlightException)
            {
                // Deleting its branch below closes it on GitHub all the same.
            }
        }
        if (made)
        {
            await git.RunAsync(["worktree", "remove", "--force", folder], repoPath, CancellationToken.None);
        }
        await git.RunAsync(["branch", "-D", branch], repoPath, CancellationToken.None);
        await git.RunAsync(["push", "--quiet", "origin", "--delete", branch], repoPath, CancellationToken.None);
    }
}
