using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What <see cref="LegCommander.ProbeRebaseAsync"/> found: whether the branch rebases onto its base, and why not.</summary>
/// <param name="Clean">True when every commit of the branch replays onto the base.</param>
/// <param name="Files">The files left unmerged where the rebase stopped; empty when clean or when the probe could not run.</param>
/// <param name="Error">Why the probe could not run (a worktree that would not add, a base that is not there); null when it ran.</param>
public sealed record RebaseProbe(bool Clean, IReadOnlyList<string> Files, string? Error = null);

public sealed partial class LegCommander
{
    /// <summary>
    /// Whether the branch of the plane at <paramref name="plane"/> rebases onto <c>origin/&lt;baseBranch&gt;</c>, as a merge queue that
    /// rebases would do. A PR can merge cleanly (merge-tree is clean) and still not rebase: a keep-ours merge in its history, with later
    /// commits touching the same lines, stops a replay one commit at a time. The test runs in a detached throwaway worktree in a
    /// temp folder at the plane's HEAD, which is aborted and removed whatever happens; the plane and its branch are never touched.
    /// </summary>
    /// <summary>The kind of card that says a PR could not be laid on its base.</summary>
    public const string LayFailedKind = "lay-failed";

    /// <summary>The kind of card that says a PR was laid on its base as one commit.</summary>
    public const string LaidKind = "laid-on-base";

    /// <summary>
    /// Before a PR joins a merge queue that rebases: probes whether its branch rebases onto its base, and where it does not (though
    /// its merge with the base is clean) lays it on the base as one commit. Returns the head to queue: the laid commit, or
    /// <paramref name="head"/> when nothing was needed. Asks git and GitHub, so never under the lock. A merge that is not clean, or
    /// a tree that is not the merge's, pushes nothing, rings once with the reason and throws.
    /// </summary>
    private async Task<string> LayIfNotRebaseableAsync(FlightRow flight, string head, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(flight.Worktree)
            || !string.Equals(await OffLockAsync(null, () => github.MergeQueueMethodAsync(flight.RepoPath, BaseBranch(flight), cancellationToken)), "REBASE", StringComparison.OrdinalIgnoreCase))
        {
            return head;
        }
        var probe = await OffLockAsync(null, () => ProbeRebaseAsync(flight.Worktree, BaseBranch(flight), cancellationToken));
        if (probe.Clean || probe.Error is not null)
        {
            // A probe that could not run says nothing about the rebase: the queue is joined as before.
            return head;
        }
        try
        {
            var (laid, old) = await OffLockAsync(null, () => LayOnBaseAsync(flight, cancellationToken));
            // The passed review read the old head; the branch's tree is the merge's, so the pass goes with it (status and label set again there).
            if (flight.Review is { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } read } && string.Equals(read, old, StringComparison.OrdinalIgnoreCase))
            {
                Record(flight.Code, f => f with { Review = f.Review is { } held ? held with { ReviewedHead = laid, StatusState = null } : null });
                await OffLockAsync(null, () => _marks.ApplyAsync(flight.Code, cancellationToken));
            }
            state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code} laid on {flight.Base}; its review carries to {Short(laid)}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
            state.RaiseCard(CardBin.Handled, LaidKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Code} laid on {flight.Base} as one commit",
                $"Its merge with {flight.Base} was clean but its branch did not rebase onto it (stopped at {string.Join(", ", probe.Files)}), which the merge queue needs. Laid as {Short(laid)}, one commit with the clean merge's tree; the old head is kept as refs/pilot/laid/{flight.Code}. Its review carries to the new head, and it joins the queue there.");
            return laid;
        }
        catch (FlightException ex)
        {
            state.RaiseCard(CardBin.Ring, LayFailedKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Code} could not be laid on {flight.Base}", ex.Message);
            throw new FlightException($"{flight.Code} does not rebase onto {flight.Base} and could not be laid on it: {ex.Message}");
        }
    }

    /// <summary>
    /// Lays the plane's branch on <c>origin/&lt;base&gt;</c> as one commit with the tree of the clean merge of the two, keeps the old head
    /// as <c>refs/pilot/laid/&lt;code&gt;</c>, moves the plane's branch to the commit and pushes it. <b>This is the one push of the node
    /// that replaces a pushed branch</b> (<c>--force-with-lease=&lt;branch&gt;:&lt;old head&gt;</c>, naming the head read, so a branch
    /// pushed from elsewhere meanwhile is never overwritten); <see cref="PushAsync"/> keeps its plain-push rule. The plane must be clean.
    /// Asks git only, so it runs with the lock let go. Returns the new head and the old one.
    /// </summary>
    private async Task<(string Laid, string Old)> LayOnBaseAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var plane = flight.Worktree;
        var baseRef = $"origin/{BaseBranch(flight)}";
        if ((await git.RunAsync(["status", "--porcelain"], plane, cancellationToken)).Output.Trim().Length > 0)
        {
            throw new FlightException("its plane holds changes not committed");
        }
        var old = (await git.RunAsync(["rev-parse", "HEAD"], plane, cancellationToken)).Output.Trim();
        var merged = await git.RunAsync(["merge-tree", "--write-tree", baseRef, "HEAD"], plane, cancellationToken);
        if (!merged.Ok)
        {
            throw new FlightException($"its merge with {baseRef} is not clean ({merged.Why})");
        }
        var tree = merged.Output.Trim().Split('\n')[0].Trim();
        var message = $"{PrTitle(flight, "")}\n\nLaid on {baseRef} as one commit ({flight.Code}): the branch merges cleanly but does not rebase.";
        var made = await git.RunAsync(["commit-tree", tree, "-p", baseRef, "-m", message], plane, cancellationToken);
        if (!made.Ok || made.Output.Trim() is not { Length: > 0 } laid)
        {
            throw new FlightException($"the commit could not be made ({made.Why})");
        }
        var got = (await git.RunAsync(["rev-parse", $"{laid}^{{tree}}"], plane, cancellationToken)).Output.Trim();
        if (!string.Equals(got, tree, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"the laid commit's tree {got} is not the merge's {tree}");
        }
        var kept = await git.RunAsync(["update-ref", $"refs/pilot/laid/{flight.Code}", old], plane, cancellationToken);
        if (!kept.Ok)
        {
            throw new FlightException($"the old head could not be kept ({kept.Why})");
        }
        var moved = await git.RunAsync(["reset", "--hard", "--quiet", laid], plane, cancellationToken);
        if (!moved.Ok)
        {
            throw new FlightException($"the plane's branch could not be moved ({moved.Why})");
        }
        var push = await git.RunAsync(["push", $"--force-with-lease={flight.Branch}:{old}", "origin", flight.Branch], plane, cancellationToken);
        if (!push.Ok)
        {
            await git.RunAsync(["reset", "--hard", "--quiet", old], plane, CancellationToken.None);
            throw new FlightException($"git could not push {flight.Branch} ({push.Why}); the plane is back at {Short(old)}");
        }
        return (laid, old);
    }

    public async Task<RebaseProbe> ProbeRebaseAsync(string plane, string baseBranch, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-rebase-probe-" + Guid.NewGuid().ToString("N")[..12]);
        var added = false;
        try
        {
            var add = await git.RunAsync(["worktree", "add", "--detach", folder, "HEAD"], plane, cancellationToken);
            if (!add.Ok)
            {
                return new RebaseProbe(false, [], $"the rebase test worktree could not be added ({add.Why})");
            }
            added = true;
            var rebase = await git.RunAsync(["rebase", $"origin/{baseBranch}"], folder, cancellationToken);
            if (rebase.Ok)
            {
                return new RebaseProbe(true, []);
            }
            var unmerged = await git.RunAsync(["diff", "--name-only", "--diff-filter=U"], folder, cancellationToken);
            var files = unmerged.Ok
                ? unmerged.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            await git.RunAsync(["rebase", "--abort"], folder, cancellationToken);
            return files.Length == 0
                ? new RebaseProbe(false, [], $"the rebase onto origin/{baseBranch} stopped ({rebase.Why})")
                : new RebaseProbe(false, files);
        }
        finally
        {
            if (added)
            {
                await git.RunAsync(["rebase", "--abort"], folder, CancellationToken.None);
                await git.RunAsync(["worktree", "remove", "--force", folder], plane, CancellationToken.None);
            }
            await git.RunAsync(["worktree", "prune"], plane, CancellationToken.None);
            if (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held: git has forgotten it already, and the temp folder is swept by the OS.
                }
            }
        }
    }
}
