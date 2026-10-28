namespace Pilot.Node.Flights;

/// <summary>One PR that asks to join a batch: its flight, number and the head merged in.</summary>
public sealed record BatchEntry(string Flight, int Pr, string Head);

/// <summary>
/// What a batch came to: <paramref name="Branch"/> (the batch branch it built), <paramref name="Included"/> the flights merged into
/// it, <paramref name="LeftOut"/> those that conflicted (flight and why), <paramref name="Green"/> whether its CI passed (why not in
/// <paramref name="Why"/>), and <paramref name="Landed"/> whether the base was fast-forwarded to it, which lands every PR at once.
/// </summary>
public sealed record BatchResult(string Branch, IReadOnlyList<string> Included, IReadOnlyList<(string Flight, string Why)> LeftOut, bool Green, bool Landed, string? Why);

/// <summary>
/// A repo's ready merge queue entries as one batch (off unless <c>merge-batch</c> is on): a branch <c>pilot/batch/&lt;repo&gt;-&lt;n&gt;</c>
/// of the base with each PR head merged in (<c>git merge --no-edit</c>), pushed plainly, its CI awaited once. On green the base is
/// fast-forwarded to the batch head where the repo allows a direct push (GitHub then shows each PR merged); where it does not, the
/// caller merges each PR as a single entry already proven green. A PR whose merge conflicts is left out; the batch branch is always
/// deleted again, local and remote. Never a rebase and never a force push.
/// </summary>
public sealed class MergeBatcher(IGit git, IGitHub github)
{
    private int _next;

    /// <summary>How often the batch's CI is looked at.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the batch's CI may take, a run that has not appeared included, before the batch counts as red.</summary>
    public TimeSpan CiBudget { get; set; } = TimeSpan.FromMinutes(45);

    public async Task<BatchResult> RunAsync(string repo, string repoPath, string baseBranch, IReadOnlyList<BatchEntry> entries, CancellationToken cancellationToken)
    {
        var branch = $"pilot/batch/{repo}-{Interlocked.Increment(ref _next)}";
        var folder = Path.Combine(Path.GetTempPath(), $"pilot-batch-{Guid.NewGuid():N}");
        var included = new List<string>();
        var leftOut = new List<(string, string)>();
        var made = false;
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
            if (await CiAsync(repoPath, head, cancellationToken) is { } red)
            {
                return new BatchResult(branch, included, leftOut, false, false, red);
            }
            // A plain push of the base to the batch head: refused unless it is a fast-forward the repo allows a direct push for.
            var landed = (await git.RunAsync(["push", "--quiet", "origin", $"{head}:refs/heads/{baseBranch}"], folder, cancellationToken)).Ok;
            return new BatchResult(branch, included, leftOut, true, landed, landed ? null : $"{baseBranch} takes no direct push: each PR is merged on its own");
        }
        finally
        {
            await Clean(branch, folder, repoPath, made);
        }

        BatchResult Red(string why) => new(branch, included, leftOut, false, false, why);
    }

    private async Task<string?> Fail(string[] args, string cwd, string doing)
    {
        var run = await git.RunAsync(args, cwd, CancellationToken.None);
        return run.Ok ? null : $"{doing} failed: {run.Why}";
    }

    // Null when every run of the head passed; else why not. No run before the budget ends is no proof of green.
    private async Task<string?> CiAsync(string repoPath, string head, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var runs = await github.WorkflowRunsAsync(repoPath, head, cancellationToken);
            if (runs.FirstOrDefault(r => r.Status == "completed" && r.Conclusion is not (null or "success" or "skipped" or "neutral")) is { } failed)
            {
                return $"the batch's CI failed: {failed.Name} {failed.Conclusion} ({failed.Url})";
            }
            if (runs.Length > 0 && runs.All(r => r.Status == "completed"))
            {
                return null;
            }
            if (waited >= CiBudget)
            {
                return runs.Length == 0 ? "the batch's CI never started" : "the batch's CI did not finish in time";
            }
            await Task.Delay(PollEvery, cancellationToken);
            waited += PollEvery;
        }
    }

    // The batch branch is gone again whatever came of it: worktree, local branch, remote branch.
    private async Task Clean(string branch, string folder, string repoPath, bool made)
    {
        if (made)
        {
            await git.RunAsync(["worktree", "remove", "--force", folder], repoPath, CancellationToken.None);
        }
        await git.RunAsync(["branch", "-D", branch], repoPath, CancellationToken.None);
        await git.RunAsync(["push", "--quiet", "origin", "--delete", branch], repoPath, CancellationToken.None);
    }
}
