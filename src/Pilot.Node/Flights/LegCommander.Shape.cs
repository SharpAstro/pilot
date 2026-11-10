using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class LegCommander
{
    /// <summary>The kind of card that says a branch was rebased onto its base before it merged.</summary>
    public const string ShapedKind = "rebased-on-base";

    /// <summary>
    /// The one lease-push rule of the node: replaces a pushed branch (<c>--force-with-lease=&lt;branch&gt;:&lt;old head&gt;</c>, naming the
    /// head read, so a branch pushed from elsewhere meanwhile is never overwritten). When the push fails the plane is put back at
    /// <paramref name="old"/> and a <see cref="FlightException"/> says so.
    /// </summary>
    private async Task LeasePushAsync(string plane, string branch, string old, CancellationToken cancellationToken)
    {
        var push = await git.RunAsync(["push", $"--force-with-lease={branch}:{old}", "origin", branch], plane, cancellationToken);
        if (!push.Ok)
        {
            await git.RunAsync(["reset", "--hard", "--quiet", old], plane, CancellationToken.None);
            throw new FlightException($"git could not push {branch} ({push.Why}); the plane is back at {CommitRefs.Short(old)}");
        }
    }

    /// <summary>
    /// A passed review read <paramref name="old"/>; the branch is now at <paramref name="shaped"/>. The pass goes with it where that is
    /// the head reviewed, or where the flight's files at the reviewed head are the new head's exactly (status and label set again).
    /// </summary>
    private async Task<bool> CarryReviewAsync(FlightRow flight, string old, string shaped, CancellationToken cancellationToken)
    {
        if (flight.Review is { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } read }
            && (string.Equals(read, old, StringComparison.OrdinalIgnoreCase)
                || await OffLockAsync(null, () => SameFlightFilesAsync(flight.Worktree, $"origin/{BaseBranch(flight)}", read, shaped, cancellationToken))))
        {
            Record(flight.Code, f => f with { Review = f.Review is { } held ? held with { ReviewedHead = shaped, StatusState = null } : null });
            await OffLockAsync(null, () => _marks.ApplyAsync(flight.Code, cancellationToken));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Before any merge: makes the branch fit how its base merges, so what lands is linear. Where the method is rebase (the queue's
    /// <c>merge_method</c>, else <paramref name="method"/>, the direct merge's) and the branch holds merge commits or does not replay,
    /// the plane's branch is rebased onto <c>origin/&lt;base&gt;</c> and pushed with the lease rule; where that rebase conflicts but the
    /// merge with the base is clean it is laid as one commit (<see cref="LayOnBaseAsync"/>). A squash or merge method is never reshaped.
    /// Returns the head to merge. Asks git and GitHub, so never under the lock. A failure puts the plane back, rings once and throws.
    /// </summary>
    private async Task<string> ShapeForMergeAsync(FlightRow flight, string head, string method, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(flight.Worktree))
        {
            return head;
        }
        var wanted = await OffLockAsync(null, () => github.MergeQueueMethodAsync(flight.RepoPath, BaseBranch(flight), cancellationToken)) ?? method;
        if (!string.Equals(wanted, "rebase", StringComparison.OrdinalIgnoreCase))
        {
            return head;
        }
        var plane = flight.Worktree;
        var baseRef = $"origin/{BaseBranch(flight)}";
        var merges = await OffLockAsync(null, () => git.RunAsync(["rev-list", "--merges", "--count", $"{baseRef}..HEAD"], plane, cancellationToken));
        var holdsMerges = merges.Ok && int.TryParse(merges.Output.Trim(), out var count) && count > 0;
        if (!holdsMerges)
        {
            var probe = await OffLockAsync(null, () => ProbeRebaseAsync(plane, BaseBranch(flight), cancellationToken));
            if (probe.Clean || probe.Error is not null)
            {
                // A probe that could not run says nothing about the rebase: the merge goes as before.
                return head;
            }
        }
        string shaped;
        string old;
        IReadOnlyList<string>? laidFiles;
        bool carried;
        try
        {
            (shaped, old, laidFiles) = await OffLockAsync(null, () => RebaseOrLayAsync(flight, cancellationToken));
            if (string.Equals(shaped, old, StringComparison.OrdinalIgnoreCase))
            {
                return head;
            }
            carried = await CarryReviewAsync(flight, old, shaped, cancellationToken);
        }
        catch (FlightException ex)
        {
            state.RaiseCard(CardBin.ForYou, LayFailedKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Code} could not be laid on {flight.Base}", ex.Message,
                [new CardChoice { Label = "Try merging again", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [head, method] }]);
            throw new FlightException($"{flight.Code} does not rebase onto {flight.Base} and could not be laid on it: {ex.Message}");
        }
        _laidAt[$"{flight.Code} {shaped}"] = DateTimeOffset.UtcNow;
        var how = laidFiles is null ? $"rebased onto {flight.Base}" : $"laid on {flight.Base} as one commit";
        if (!carried)
        {
            // Pushed, but the flight's files there are not the ones reviewed: nobody has read this head, so it does not merge. The look
            // before the next merge relates it to the review in words and asks for the review it needs.
            var reviewed = flight.Review?.ReviewedHead is { Length: > 0 } r ? $" (reviewed {CommitRefs.Short(r)})" : "";
            var title = $"{flight.Code} {how}, its review not carried";
            var detail = $"{flight.Base} merges by rebase, so the branch was {how} and pushed as {CommitRefs.Short(shaped)}; the old head is kept as refs/pilot/laid/{flight.Code}. The flight's files there differ from the head reviewed{reviewed}, so the review does not carry and it does not merge until that head is reviewed.";
            if (laidFiles is null)
            {
                state.RaiseCard(CardBin.Handled, ShapedKind, Verbs.FlightTarget(flight.Code), title, detail);
            }
            else
            {
                state.RaiseCard(CardBin.Handled, LaidKind, Verbs.FlightTarget(flight.Code), title, detail);
            }
            throw new FlightException($"{flight.Code} was {how} as {CommitRefs.Short(shaped)}, but its files differ from the head reviewed{reviewed}: its review did not carry, look again before it lands");
        }
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code} shaped for {flight.Base}; its review carries to {CommitRefs.Short(shaped)}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
        if (laidFiles is null)
        {
            state.RaiseCard(CardBin.Handled, ShapedKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Code} rebased onto {flight.Base}",
                $"{flight.Base} merges by rebase and the branch held a merge or did not replay as it was, so it was rebased onto {flight.Base} and pushed as {CommitRefs.Short(shaped)}; the old head is kept as refs/pilot/laid/{flight.Code}. Its review carries to the new head.");
        }
        else
        {
            state.RaiseCard(CardBin.Handled, LaidKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Code} laid on {flight.Base} as one commit",
                $"Its merge with {flight.Base} was clean but its branch did not rebase onto it (stopped at {string.Join(", ", laidFiles)}), which {flight.Base}'s rebase merge needs. Laid as {CommitRefs.Short(shaped)}, one commit with the clean merge's tree; the old head is kept as refs/pilot/laid/{flight.Code}. Its review carries to the new head, and it merges there.");
        }
        return shaped;
    }

    /// <summary>
    /// Rebases the plane's branch onto its base and lease-pushes it; a rebase that conflicts is aborted and the branch laid as one commit
    /// instead (the files it stopped at come back). The plane is put back at the old head on any failure.
    /// </summary>
    private async Task<(string Shaped, string Old, IReadOnlyList<string>? LaidFiles)> RebaseOrLayAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var plane = flight.Worktree;
        var baseRef = $"origin/{BaseBranch(flight)}";
        if ((await git.RunAsync(["status", "--porcelain"], plane, cancellationToken)).Output.Trim().Length > 0)
        {
            throw new FlightException("its plane holds changes not committed");
        }
        var old = (await git.RunAsync(["rev-parse", "HEAD"], plane, cancellationToken)).Output.Trim();
        var rebase = await git.RunAsync(["rebase", baseRef], plane, cancellationToken);
        if (!rebase.Ok)
        {
            var unmerged = await git.RunAsync(["diff", "--name-only", "--diff-filter=U"], plane, cancellationToken);
            var files = unmerged.Ok ? unmerged.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            await git.RunAsync(["rebase", "--abort"], plane, CancellationToken.None);
            await git.RunAsync(["reset", "--hard", "--quiet", old], plane, CancellationToken.None);
            var (laid, _) = await LayOnBaseAsync(flight, cancellationToken);
            return (laid, old, files.Length > 0 ? files : ["(see the rebase)"]);
        }
        var shaped = (await git.RunAsync(["rev-parse", "HEAD"], plane, cancellationToken)).Output.Trim();
        if (string.Equals(shaped, old, StringComparison.OrdinalIgnoreCase))
        {
            return (old, old, null);
        }
        var kept = await git.RunAsync(["update-ref", $"refs/pilot/laid/{flight.Code}", old], plane, cancellationToken);
        if (!kept.Ok)
        {
            await git.RunAsync(["reset", "--hard", "--quiet", old], plane, CancellationToken.None);
            throw new FlightException($"the old head could not be kept ({kept.Why})");
        }
        await LeasePushAsync(plane, flight.Branch, old, cancellationToken);
        return (shaped, old, null);
    }
}
