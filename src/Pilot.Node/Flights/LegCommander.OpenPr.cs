using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// Opening a flight's PR: the push, the fetch and merge of the base, and the PR title and body.
/// </summary>
public sealed partial class LegCommander
{
    private async Task OpenPrAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        // Another step's calls out for the flight (a push, a check rerun, the node's own sweep) only hold this back: it waits for them, as the
        // node's own rebase does, instead of refusing as busy, and reads the flight afresh (pilot #871: a loaded run refused it).
        await WaitForFlightFreeAsync(flight.Code, OpeningPr, cancellationToken);
        flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        var legs = state.Legs(flight.Code);
        if (legs.Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open)
        {
            throw new FlightException($"{flight.Code} has legs not done: {string.Join(", ", open)}");
        }

        var title = string.Join(' ', command.Args ?? []).Trim();
        // The base repaired, the branch pushed and the PR looked up and made are git and GitHub: asked with the lock let go (PIL338), and
        // recorded once the flight is found as it was. Each is safe to say again, as a PR made and not recorded is found by its branch.
        using var call = Calling(flight.Code, OpeningPr, coalesce: true);
        (FlightRow repaired, PullRequest pr, bool mergedBase) opened;
        try
        {
        opened = await OffLockAsync(flight, async () =>
        {
            // A base that is not a branch on the remote is repaired first: the merge it takes must be what is pushed.
            // The remote's commits are taken first, so the repair's merge is made on the whole branch.
            await TakeRemoteAsync(flight, cancellationToken);
            var repaired = await RepairBaseAsync(flight, cancellationToken);
            var mergedBase = false;
            if (repaired.MergedHead is { } mergedHead)
            {
                // The earlier PR merged without these legs (#342): the base as it now stands is merged into the plane, so the new PR holds
                // only what the plane has past it, whether the merge kept the commits' ids or squashed them. Never a rebase of the branch.
                if (await planes.UncommittedAsync(repaired.Worktree, cancellationToken) is { } dirty)
                {
                    throw new FlightException($"{repaired.Code}'s plane holds {dirty}: commit {(dirty.StartsWith("1 ", StringComparison.Ordinal) ? "it" : "them")}, then open the PR, which merges {repaired.Base} into the plane");
                }
                // A repeat after a push that threw (MergedHead is cleared only by the later Record) finds the base already in HEAD: no second merge.
                var taken = await git.RunAsync(["merge-base", "--is-ancestor", repaired.Base, "HEAD"], repaired.Worktree, cancellationToken);
                if (!taken.Ok)
                {
                    await git.RunAsync(["fetch", "--quiet", "origin"], repaired.Worktree, cancellationToken);
                    var onto = await git.RunAsync(["merge", "--no-edit", repaired.Base], repaired.Worktree, cancellationToken);
                    if (!onto.Ok)
                    {
                        await git.RunAsync(["merge", "--abort"], repaired.Worktree, cancellationToken);
                        throw new FlightException($"merging {repaired.Base} into {repaired.Code}'s plane conflicts ({onto.Why}): merge it by hand (git merge {repaired.Base}, the earlier PR merged at {CommitRefs.Short(mergedHead)}), then open the PR");
                    }
                }
                repaired = repaired with { MergedHead = null };
                mergedBase = true;
            }
            // Pushed every time, not only for a new PR: a go-around's commits must reach the PR before it is reviewed again.
            await PushAsync(repaired, cancellationToken);
            var pr = await github.PullRequestAsync(repaired.RepoPath, repaired.Branch, cancellationToken);
            if (pr is null || !string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                pr = await github.CreateAsync(repaired.RepoPath, repaired.Branch, BaseBranch(repaired), PrTitle(repaired, title), PrBody(repaired, legs), cancellationToken);
                // The node label rides the PR as an issue's label (a PR is one on GitHub), not through SetPilotLabelAsync, which
                // would swap out the PR's state labels. A refusal is noted, not fatal to the PR.
                try
                {
                    await github.AddIssueLabelAsync(repaired.RepoPath, pr.Number, PilotMarks.NodeLabel(options.Machine), cancellationToken);
                }
                catch (FlightException ex)
                {
                    state.RaiseCard(CardBin.Handled, "node-label-refused", Verbs.FlightTarget(repaired.Code), $"{repaired.Code}'s PR has no {PilotMarks.NodeLabel(options.Machine)} label", ex.Message);
                }
            }
            return (repaired, pr, mergedBase);
        });
        }
        catch (FlightException ex) when (IsConflict(ex.Message))
        {
            // A base that conflicts is a worker's to resolve, whoever asked to open the PR (the node after the last leg, a card, the CLI):
            // a worker flying a leg is told to merge the base, else the last leg goes round by itself with the conflict told, once for each
            // head of the base (a failed worker is followed by a stronger one), and nothing rings until the last rung fails. The open's own
            // call on the flight is let go first, as the go-around takes the flight itself.
            call.Dispose();
            await ResolveConflictAsync(flight.Code, ex.Message, cancellationToken, lockHeld: true);
            // Noted for the node's sweep, which does not try the same open again until the plane or the base moves.
            _conflictOpenTried[flight.Code] = await ConflictHeadsAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken);
            throw;
        }
        var (repaired, pr, mergedBase) = opened;
        // A push that moved the head of a holding flight ends the hold: the fresh review and checks gate the merge as for any push.
        var beforeOpen = FlightOf(Verbs.FlightTarget(flight.Code));
        Record(flight.Code, f => f with { Base = repaired.Base, PullRequest = pr.Number, PullRequestUrl = pr.Url, MergedHead = mergedBase ? null : f.MergedHead });
        EndHoldPastReview(beforeOpen, pr.Head, "flight pr");
        // The PR opened, so a conflict that held its opening is resolved.
        _conflictOpenTried.TryRemove(flight.Code, out _);
        if (mergedBase)
        {
            state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{repaired.Base} merged into {flight.Code}'s plane; a new PR opens", FlightCommander.MergedUnderLegKind);
            // The earlier PR merged without these commits: the new PR carries them, and a line links it.
            state.RaiseCard(CardBin.Handled, FollowUpPrKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Code}'s commits missed {(beforeOpen.MergedPullRequest is { } missed ? $"#{missed}'s" : "the earlier PR's")} merge: PR #{pr.Number} carries them",
                $"The earlier PR merged without them, so the node merged {repaired.Base} into the plane, pushed plain and opened PR #{pr.Number}, which is reviewed as any PR. The flight lands when it merges.",
                link: pr.Url);
        }

        // The PR open, a fresh reviewer reads it before the merge is offered. One that cannot start costs the flight
        // its review, not its merge: the card says so and the controller decides.
        try
        {
            await StartReviewAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken, quiet: true);
        }
        catch (FlightException ex)
        {
            ReviewNotStarted(flight.Code, $"the reviewer did not start: {ex.Message}");
        }
    }

    /// <summary>
    /// GitHub's auto-merge, armed on the flight's open PR, merges whatever head the PR has once its checks pass (its
    /// <c>--match-head-commit</c> is only checked when it is armed): a push must never reach a PR while it is armed. Turned off
    /// first when it is on; when it cannot be, or GitHub would not say whether it is, the push is refused with that said.
    /// </summary>
    private async Task EnsureNotArmedAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        if (pr is not { State: "OPEN" })
        {
            return;
        }
        // A PR in the merge queue locks its branch (GH006) and merges whatever head it had when it was queued: out first.
        await EnsureNotQueuedAsync(flight, pr.Number, cancellationToken);
        // Unknown is treated as armed: the disarm is no error when it was not on.
        if (await github.AutoMergeArmedAsync(flight.RepoPath, pr.Number, cancellationToken) == false)
        {
            return;
        }
        try
        {
            await github.DisarmAutoMergeAsync(flight.RepoPath, pr.Number, cancellationToken);
        }
        catch (FlightException ex)
        {
            throw new FlightException($"{flight.Branch} is not pushed: #{pr.Number} may be armed with GitHub's auto-merge, which would merge the new head unreviewed, and it could not be turned off ({ex.Message}): switch it off on the PR page, then try again");
        }
    }

    /// <summary>The card a push refused because the remote branch holds commits the plane lacks and merging them conflicts.</summary>
    public const string PushDivergedKind = "push-diverged";

    /// <summary>
    /// Fetches the flight's branch and takes in what the remote holds that the plane lacks: a fast-forward, or a merge. A merge that
    /// conflicts is undone, a Ring card names both heads and what to do, and the push is refused. Nothing is pushed here. A branch
    /// the remote does not have yet, or a fetch that fails, leaves the plane as it is: git itself then refuses a push that would drop commits.
    /// </summary>
    private async Task TakeRemoteAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var tracking = $"refs/remotes/origin/{flight.Branch}";
        await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{flight.Branch}:{tracking}"], flight.Worktree, cancellationToken);
        var remote = await git.RunAsync(["rev-parse", "--verify", "--quiet", tracking], flight.Worktree, cancellationToken);
        if (!remote.Ok || (await git.RunAsync(["merge-base", "--is-ancestor", tracking, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            return;
        }
        var merge = await git.RunAsync(["merge", "--no-edit", tracking], flight.Worktree, cancellationToken);
        if (merge.Ok)
        {
            return;
        }
        await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
        var plane = (await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken)).Output.Trim();
        var theirs = remote.Output.Trim();
        var why = $"{flight.Branch} is not pushed: origin/{flight.Branch} ({CommitRefs.Short(theirs)}) holds commits the plane ({CommitRefs.Short(plane)}) lacks, and merging them conflicts";
        state.RaiseCard(CardBin.ForYou, PushDivergedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} was not pushed: the remote branch holds commits it lacks",
            $"{why} ({merge.Why}). Nothing was overwritten. Merge origin/{flight.Branch} into the plane by hand ({flight.Worktree}), resolve the conflicts and commit, then push again.",
            [new CardChoice { Label = "Push the branch again", Verb = Verbs.OpenPr, Target = Verbs.FlightTarget(flight.Code) }]);
        throw new FlightException($"{why}: merge origin/{flight.Branch} ({theirs}) into the plane's head ({plane}) by hand, then push again");
    }

    /// <summary>
    /// Fetches, takes in what the remote holds that the plane lacks (<see cref="TakeRemoteAsync"/>), then merges the base in
    /// (<c>git merge --no-edit origin/&lt;base&gt;</c>) when the branch lacks it; a conflict is aborted (<c>git merge --abort</c>) and refused.
    /// Never a rebase: a pushed branch is only ever added to.
    /// </summary>
    private async Task FetchAndMergeBaseAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
        await TakeRemoteAsync(flight, cancellationToken);
        if ((await git.RunAsync(["merge-base", "--is-ancestor", flight.Base, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            return;
        }
        var merge = await git.RunAsync(["merge", "--no-edit", flight.Base], flight.Worktree, cancellationToken);
        if (!merge.Ok)
        {
            await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
            throw new FlightException($"{flight.Branch} is not pushed: merging {flight.Base} into it conflicts ({merge.Why}); {UpdateInstruction}");
        }
    }

    /// <summary>
    /// Pushes the flight's branch plain (<c>git push -u origin &lt;branch&gt;</c>), never with --force or --force-with-lease: the PIL294 work
    /// was lost to a force push on 2026-10-27. A head that still lacks its base, or whose pushed history is not an ancestor of it, is
    /// refused, and git itself refuses a push that is no fast-forward.
    /// </summary>
    /// <param name="early">A failed finish's push of the plane's commits: the base is not required, the disarm and the never-overwrite check stay.</param>
    /// <param name="brought">The plane was fetched and has its base merged in already (a leg's finish did it before its check ran): not done again.</param>
    internal async Task PushAsync(FlightRow flight, CancellationToken cancellationToken, bool brought = false, bool early = false)
    {
        await EnsureNotArmedAsync(flight, cancellationToken);
        // A push after the leg is recorded done sends what the check ran on: a second update here could fail with the leg already done.
        if (!brought)
        {
            await FetchAndMergeBaseAsync(flight, cancellationToken);
        }
        // The node's push refuses a branch still lacking its base, however it came to be so. An early push (#752) does not:
        // origin/<base> moves on any fetch, so a branch behind it is the usual state, and the work should reach the PR all the same.
        if (!early && !brought && !(await git.RunAsync(["merge-base", "--is-ancestor", flight.Base, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            throw new FlightException($"{flight.Branch} is not pushed: it is still behind {flight.Base}, which is not an ancestor of its head");
        }
        var tracking = $"refs/remotes/origin/{flight.Branch}";
        if ((await git.RunAsync(["rev-parse", "--verify", "--quiet", tracking], flight.Worktree, cancellationToken)).Ok
            && !(await git.RunAsync(["merge-base", "--is-ancestor", tracking, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            throw new FlightException($"{flight.Branch} is not pushed: origin/{flight.Branch} holds commits its head lacks, and the node never overwrites a pushed branch");
        }
        var push = await git.RunAsync(["push", "-u", "origin", flight.Branch], flight.Worktree, cancellationToken);
        // Git's whole text is read: GitHub's GH006 line is not always the first of its stderr.
        if (!push.Ok && PushRefusals.IsProtectedBranch($"{push.Error}\n{push.Output}"))
        {
            // GH006 (PIL836): the PR was queued behind the node's back, or merged meanwhile; the PR is read and the push is made once more or handed on.
            push = await RetryProtectedPushAsync(flight, cancellationToken);
            // Still locked after the retry: a refusal the node cannot answer, said in its way (the sweep reads that, never the text,
            // whose GH006 line may sit anywhere in git's output) with GitHub's own line, wherever git printed it.
            if (!push.Ok && PushRefusals.ProtectedLine($"{push.Error}\n{push.Output}") is { } locked)
            {
                throw new PushRefusedException(RefusalWays.ProtectedBranch, $"git could not push {flight.Branch}: {locked}");
            }
        }
        if (!push.Ok)
        {
            throw new FlightException($"git could not push {flight.Branch}: {push.Why}");
        }
    }

    /// <summary>
    /// A flight whose base is not a branch on the remote (cut while the clone lacked <c>origin/HEAD</c>, from whatever was
    /// checked out; PIL124 and PIL126 from a feature branch since merged and deleted): its base becomes the remote's
    /// default, and its clean plane has it merged in, which holds the commits that branch already landed. A merge that
    /// conflicts is aborted and said, never left half done. Asks git and GitHub, so never under the lock, and the repaired row is the
    /// caller's to record.
    /// </summary>
    private async Task<FlightRow> RepairBaseAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.Base.StartsWith("origin/", StringComparison.Ordinal)
            && (await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/remotes/{flight.Base}"], flight.Worktree, cancellationToken)).Ok)
        {
            return flight;
        }
        var remoteBase = await planes.RemoteBaseAsync(flight.RepoPath, cancellationToken);
        if (!remoteBase.StartsWith("origin/", StringComparison.Ordinal) || remoteBase == flight.Base)
        {
            return flight;
        }
        if ((await git.RunAsync(["status", "--porcelain"], flight.Worktree, cancellationToken)).Output.Trim().Length > 0)
        {
            throw new FlightException($"{flight.Code}'s base {flight.Base} is not a branch on the remote, and its plane holds changes not committed: commit them, then open the PR, which merges {remoteBase} into it");
        }
        if (!(await git.RunAsync(["merge-base", "--is-ancestor", remoteBase, "HEAD"], flight.Worktree, cancellationToken)).Ok)
        {
            var merge = await git.RunAsync(["merge", "--no-edit", remoteBase], flight.Worktree, cancellationToken);
            if (!merge.Ok)
            {
                await git.RunAsync(["merge", "--abort"], flight.Worktree, cancellationToken);
                throw new FlightException($"{flight.Code}'s base {flight.Base} is not a branch on the remote, and merging {remoteBase} into its plane conflicts ({merge.Why}). Merge it by hand, then open the PR");
            }
        }
        // The caller records the new base, under the lock: this runs with it let go.
        return flight with { Base = remoteBase };
    }

    /// <summary>The branch a PR goes into: its base without the remote (<c>origin/main</c> is <c>main</c>).</summary>
    private static string BaseBranch(FlightRow flight) =>
        flight.Base.StartsWith("origin/", StringComparison.Ordinal) ? flight.Base["origin/".Length..] : flight.Base;

    /// <summary>The PR's title: the code and the given or flight's title; a Shortcut story's id is in it too, as the integration reads it there.</summary>
    internal static string PrTitle(FlightRow flight, string given)
    {
        var title = $"{flight.Code}: {(given.Length > 0 ? given : flight.Title ?? flight.Item.Noun)}";
        return flight.Tracker == TrackerKind.Shortcut && !title.Contains(flight.Item.Display, StringComparison.OrdinalIgnoreCase) ? $"{title} [{flight.Item.Display}]" : title;
    }

    internal static string PrBody(FlightRow flight, IReadOnlyList<LegRow> legs)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine($"Flight {flight.Code}{(flight.Title is { Length: > 0 } t ? $": {t}" : "")}.");
        if (legs.Count > 0)
        {
            body.AppendLine();
            foreach (var leg in legs)
            {
                body.AppendLine($"- **{leg.Name}**: {leg.Goal}{(leg.Report is { Length: > 0 } r ? $" — {r.Split('\n')[0]}" : "")}");
            }
        }
        body.AppendLine();
        // Shortcut links a PR to its story by the sc-<id> in it and closes the story by its own workflow, so no Closes here.
        var shortcut = flight.Tracker == TrackerKind.Shortcut;
        body.AppendLine(shortcut ? $"Shortcut story {flight.Item.Display}." : $"Closes {flight.Item.Display}.");
        foreach (var folded in flight.FoldedIn)
        {
            body.AppendLine(shortcut
                ? $"Shortcut story sc-{folded.Number} (folded in from {folded.From})."
                : $"Closes #{folded.Number} (folded in from {folded.From}).");
        }
        return body.ToString();
    }
}
