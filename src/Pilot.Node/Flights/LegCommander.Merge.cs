using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// Landing a flight: the merge verb, arming a merge, looking at the PR, the merge cards, and taking a merge back.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The last item of <see cref="FlightRow.MergeWhenGreen"/> when the node armed the merge itself, on autopilot.</summary>
    public const string ArmedByAutopilot = "autopilot";

    /// <summary>
    /// <c>merge &lt;head&gt; [method]</c>: merges the flight's PR, or joins its base's merge queue. While its checks still run,
    /// the merge waits on the flight (<see cref="FlightRow.MergeWhenGreen"/>) and the node takes it once they pass. Whether it
    /// merged, and when it did not, where it stands (<see cref="MergeWays"/>) and the line the card shows for it.
    /// </summary>
    private async Task<(bool Merged, string? Made, string? Way)> MergeAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        // The merge is an entry in its repo's queue (MergeRow), never done on the spot: the node takes the repo's entries in order, one
        // at a time, and the entry says where it stands. Only a flight that may merge at all (every leg done, review passed) gets one.
        var (asked, askedHead, askedMethod) = Mergeable(command);
        var by = command.Who != "node" ? MergeBy.Hand : asked.MergeWhenGreen is null ? MergeBy.Autopilot
            : asked.MergeWhenGreen[^1] == ArmedByAutopilot ? MergeBy.Autopilot : MergeBy.Armed;
        // A flight's entry is its one merge command's: a second ask while one is under way is refused and leaves the entry as it is.
        bool underWay;
        lock (_turns)
        {
            underWay = _active.Contains(asked.Code);
        }
        if (underWay)
        {
            // A command that holds the flight (_active) drives its entry. Waiting for a turn behind another flight, or for checks, can
            // outlast StuckAfter with the entry unmoved and is not stuck: only an entry the command is updating or merging, unmoved for
            // StuckAfter, is (a gh call that never returns). Asked again, the merge answers ok and the entry and card are left as they are.
            if (state.Merges().FirstOrDefault(m => m.Flight == asked.Code) is not { Open: true, State: MergeEntryState.Updating or MergeEntryState.Merging } live
                || time.GetUtcNow() - live.ChangedAt < StuckAfter)
            {
                // The repeat's own head and method are ignored: the first merge's stand. Its Way says "not merged, no news" to callers.
                return (false, AlreadyInProgress, MergeWays.InProgress);
            }
            throw new MergeBlockedException(MergeRefusals.NodeWait, $"{asked.Code} is being merged already: wait for its answer, then ask again");
        }
        // The turn is the command's before its entry is put: a journal that cannot write the entry (a full disk) throws out of Enqueue
        // with the flight already among those under way, and only the command's end gives it back.
        var holder = _held.Value ?? new HeldTurn();
        holder.Code = asked.Code;
        Enqueue(asked, askedHead, askedMethod, by);
        try
        {
            await OffLockAsync(null, () => WaitTurnAsync(asked.Code, asked.Repo, cancellationToken));
            if (Models.MergeBatch)
            {
                await OffLockAsync(null, () => BatchAsync(asked.Code, asked.Repo, cancellationToken));
            }
            if (TakeLanded(asked.Code) is { } batch)
            {
                holder.Answered = true;
                SetEntry(asked.Code, MergeEntryState.Merging, $"merged with {batch}: the flight lands");
                return (true, null, null);
            }
            var result = await MergeEntryAsync(command, cancellationToken);
            holder.Answered = true;
            if (result.Merged)
            {
                SetEntry(asked.Code, MergeEntryState.Merging, "merged: the flight lands");
            }
            else if (result.Way == MergeWays.Armed)
            {
                SetEntry(asked.Code, MergeEntryState.Checks, result.Made);
            }
            else
            {
                SetEntry(asked.Code, MergeEntryState.Merging, result.Made);
            }
            // Armed or queued, the card says so in place of "merging": the caller's card answer does the same, from the same words.
            if (result.Way is MergeWays.Armed or MergeWays.Queued)
            {
                ShowMerge(Verbs.FlightTarget(asked.Code), result.Made, result.Way);
            }
            return result;
        }
        catch (FlightException ex)
        {
            // Not mergeable as it is (conflicting, behind its base) is the update's: the entry stays while the node brings it up to date.
            // A head that moved is dropped by the command's end, which says what comes next (HeadMovedAsync) once git has looked.
            if (!Conflicts(ex.Message) && !Behind(ex.Message) && !HeadMoved(ex.Message))
            {
                DropMerge(asked.Code, ex.Message);
            }
            RestoreMerge(asked.Code);
            throw;
        }
        catch
        {
            // Given up or failed outright: the card offers the Merge it offered before.
            RestoreMerge(asked.Code);
            throw;
        }
    }

    private async Task<(bool Merged, string? Made, string? Way)> MergeEntryAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, head, method) = Mergeable(command);
        // Every ask of GitHub and git is made with the lock let go (PIL338): the flight is this command's meanwhile, and what it
        // found is recorded only if the flight is still as it was decided on.
        using var call = Calling(flight.Code, "being merged");
        PullRequest pr;
        ChecksSummary? checks;
        MergeState? mergeState;
        try
        {
            (pr, checks, mergeState) = await OffLockAsync(flight, async () =>
            {
                var pr = await LookAtPrAsync(flight, head, cancellationToken);
                var checks = await github.ChecksSummaryAsync(flight.RepoPath, pr.Number, cancellationToken);
                // A merge queue takes no PR whose required checks have not passed, and a plain merge would be refused for the same:
                // pressed while they run, the merge waits for them on this head (TIA1818's #1826, DE1 2026-10-25).
                var mergeState = checks?.Status == ChoiceStatus.Failing ? null : await github.MergeStateAsync(flight.RepoPath, pr.Number, cancellationToken);
                return (pr, checks, mergeState);
            });
        }
        catch (AlreadyMergedException)
        {
            // GitHub merged it before this merge asked (the queue, a person, the autopilot's earlier look): the state wanted. The
            // command's end lands the flight as any merge does, and nothing is refused or rung (#799 rule 6, row c17100).
            return (true, null, null);
        }
        // A head re-pinned by the look (moved only by its base or by whitespace or documentation) is the review's head now.
        flight = FlightOf(command.Target);
        // A head that is in the queue now, or whose checks are still pending, is not one the queue dropped for a conflict: only a PR
        // that is neither queued nor waiting on checks is refused as dropped (the mark outlives a requeue by hand or a re-run).
        if (DroppedForConflict(flight.Code, pr.Head) && mergeState?.Kind != MergeKinds.Queued && checks?.Status != ChoiceStatus.Pending)
        {
            // The conflict is the update's to clear (the node merges the base in, the watchtower resolves what conflicts): it passes by itself.
            throw new MergeBlockedException(MergeRefusals.Passing, $"GitHub's merge queue dropped #{pr.Number} at {CommitRefs.Short(pr.Head)} for a conflict: it is queued again once its head moves");
        }
        if (checks?.Status == ChoiceStatus.Failing)
        {
            throw new MergeBlockedException(MergeRefusals.FailedCheck, $"#{pr.Number}'s checks fail: it merges once they pass");
        }
        // What GitHub says about the PR is what the button does: a PR in the queue is not queued twice, one behind its base or
        // with changes requested is refused with that said, as GitHub's own button would not be there (pilot #244).
        switch (mergeState?.Kind)
        {
            case MergeKinds.Queued:
                return (false, QueuedLine(flight.Code, mergeState.Place is { } place ? $"in the merge queue, {place}" : "in the merge queue"), MergeWays.Queued);
            case MergeKinds.Behind:
                throw new MergeBlockedException(MergeRefusals.Passing, BehindText(pr.Number));
            case MergeKinds.ChangesRequested:
                throw new MergeBlockedException(MergeRefusals.ChangesRequested, $"#{pr.Number} has changes requested on GitHub: they are read first, then it merges");
            case MergeKinds.Blocked when mergeState.Reason is { } why && mergeState.FailedName is null:
                // A draft is marked ready for review, not approved; blocked with no failed check named and no draft: what branch
                // protection waits for is an approval.
                throw new MergeBlockedException(mergeState.Draft || pr.Draft ? MergeRefusals.Draft : MergeRefusals.NeedsApproval, $"#{pr.Number} is blocked on GitHub: {why}");
        }
        if (checks?.Status == ChoiceStatus.Pending)
        {
            var pending = Math.Max(checks.Total - checks.Done, 0);
            SetEntry(flight.Code, MergeEntryState.Checks, $"waiting for checks ({pending} pending)", cardNote: $"waiting for checks ({pending} pending)");
            return await ArmAsync(flight, pr, method, checks, null, command.Who == "node", cancellationToken);
        }
        if (mergeState is { AwaitsCheck: true })
        {
            // Every check shown is green, GitHub blocks it for a required check not created yet: arm, and the merge goes through once it passes.
            SetEntry(flight.Code, MergeEntryState.Checks, "waiting for a required check to report", cardNote: "waiting for a required check to report");
            return await ArmAsync(flight, pr, method, checks, AwaitingCheckLine, command.Who == "node", cancellationToken);
        }
        if (flight.MergeWhenGreen is not null)
        {
            Record(flight.Code, f => f with { MergeWhenGreen = null });
        }
        SetEntry(flight.Code, MergeEntryState.Merging, $"merging #{pr.Number} ({method})");
        // The merge itself is the longest ask: nothing of the flight is held over it, and what follows reads the flight afresh.
        var outcome = await LandFlightAsync(flight, pr, method, cancellationToken);
        // Read again: the armed mark was cleared above and the shape step may have re-pinned the review, so an arm below checks the
        // flight as it is now, not the copy read before (a retried merge at green was refused as "changed while asked").
        flight = FlightOf(command.Target);
        var laid =!string.Equals(outcome.Head, pr.Head, StringComparison.OrdinalIgnoreCase);
        switch (outcome.Kind)
        {
            case LandKind.Merged:
                return (true, null, null);
            case LandKind.Queued:
                return (false, QueuedLine(flight.Code), MergeWays.Queued);
            case LandKind.RetryLater:
                // GitHub did not answer: the merge waits on the flight and the node asks again at its next look, with no card.
                return await ArmAsync(flight, pr with { Head = outcome.Head }, method, null, $"GitHub did not answer the merge ({OneLine(outcome.Said ?? "no answer")}); the node asks again", command.Who == "node", cancellationToken);
            case LandKind.Refused when laid && outcome.Answer is not (MergeAnswerKind.Behind or MergeAnswerKind.Conflicts or MergeAnswerKind.HeadMoved):
                // Just laid: its checks have not run on the new head, so the queue refuses it. A Merge card at the laid head stays and the
                // merge waits for the checks there, as for any head whose checks run.
                RaiseReadyAtLaid(flight, outcome.Head, outcome.Said ?? "");
                return await ArmAsync(flight, pr with { Head = outcome.Head }, method, null, "laid on its base; merging when checks pass on the new head", command.Who == "node", cancellationToken);
            default:
                throw RefusalOf(pr.Number, outcome);
        }
    }

    /// <summary>What an armed merge says while every check shown is green and one GitHub requires has yet to be created.</summary>
    public const string AwaitingCheckLine = "merging when checks pass: a required check has yet to report";

    /// <summary>The third element of <see cref="FlightRow.MergeWhenGreen"/> when GitHub's own auto-merge waits for the checks.</summary>
    public const string ArmedByGitHub = "github";

    /// <summary>The third element of <see cref="FlightRow.MergeWhenGreen"/> when the node polls and has nothing to add about it.</summary>
    public const string ArmedByNode = "node";

    /// <summary>The how-to the card gives while the node polls because the repo has auto-merge off.</summary>
    public const string SwitchOnAutoMerge = "Switch on GitHub's auto-merge to let GitHub wait instead: gh repo edit --enable-auto-merge, or the repo's Settings > General > Allow auto-merge. GitHub waits only for required checks, so make them required too (a branch protection rule or a ruleset).";

    /// <summary>
    /// What a merge waiting for its checks shows: how many are done (or <paramref name="progress"/> in their place), and who
    /// waits (<paramref name="how"/>, the third element of <see cref="FlightRow.MergeWhenGreen"/>).
    /// </summary>
    public static string ArmedLine(ChecksSummary? checks, string? how = null, string? progress = null)
    {
        var line = progress ?? (checks is { Total: > 0 } ? $"merging when checks pass: {ArmedProgress.Say(checks.Done, checks.Total)} done" : "merging when checks pass");
        return how switch
        {
            ArmedByGitHub => $"{line}. GitHub merges it, with the node up or not.",
            ArmedByNode => line,
            _ => $"{line}. The node waits for them. {SwitchOnAutoMerge}",
        };
    }

    /// <summary>What a merge that joined its base's merge queue shows, at its place in it when known.</summary>
    public static string QueuedLine(string code, string where = "in the merge queue") =>
        $"{where}: {code} lands once the queue merges it";

    // The merge waits on the flight for its checks, on the head reviewed: GitHub's auto-merge where the repo allows it, else
    // the node's polling (PrChecks). A merge queue base keeps joining the queue once the checks pass, as before.
    private async Task<(bool Merged, string? Made, string? Way)> ArmAsync(FlightRow flight, PullRequest pr, string method, ChecksSummary? checks, string? progress, bool byAutopilot, CancellationToken cancellationToken)
    {
        var how = await OffLockAsync(flight, () => ArmHowAsync(flight, pr, method, cancellationToken));
        // A merge the node asked for on autopilot is marked (last), so a failure of its checks sends the leg round as it would without
        // one. The mark follows who waits (an empty third element when none does).
        Record(flight.Code, f => f with { MergeWhenGreen = (how, byAutopilot) switch
        {
            (null, false) => [pr.Head, method],
            (_, false) => [pr.Head, method, how!],
            _ => [pr.Head, method, how ?? "", ArmedByAutopilot],
        } });
        return (false, ArmedLine(checks, how, progress), MergeWays.Armed);
    }

    // Who waits: GitHub when it takes the arm, the node (no word of it) on a merge queue base or a PR GitHub would not arm, and
    // null, the node with the how-to, where the repo has auto-merge off.
    private async Task<string?> ArmHowAsync(FlightRow flight, PullRequest pr, string method, CancellationToken cancellationToken)
    {
        if (await github.MergeQueueAsync(flight.RepoPath, pr.Number, cancellationToken) is not null)
        {
            return ArmedByNode;
        }
        if (!await github.AutoMergeAllowedAsync(flight.RepoPath, cancellationToken))
        {
            return null;
        }
        try
        {
            await github.ArmAutoMergeAsync(flight.RepoPath, pr.Number, pr.Head, method, cancellationToken);
            return ArmedByGitHub;
        }
        catch (FlightException)
        {
            // Allowed, but GitHub refused: the PR is mergeable already (running checks that are not required leave it so), or has
            // nothing for GitHub to wait on. Nothing was merged or armed: the node waits and merges at green, as it would.
            return ArmedByNode;
        }
    }

    /// <summary>
    /// <c>rerun-checks &lt;head&gt; [method]</c>: the PR's failed runs run again, and the merge waits for them as a merge pressed
    /// while checks run does. What a merge that is not merged returns.
    /// </summary>
    private async Task<(string? Made, string? Way)> RerunChecksAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (flight, head, method) = Mergeable(command);
        using var call = Calling(flight.Code, "re-running its checks");
        var (pr, runs) = await OffLockAsync(flight, async () =>
        {
            var pr = await LookAtPrAsync(flight, head, cancellationToken);
            return (pr, await github.RerunFailedChecksAsync(flight.RepoPath, pr.Number, cancellationToken));
        });
        var (_, made, way) = await ArmAsync(flight, pr, method, null, $"merging when checks pass: {runs} failed {(runs == 1 ? "run" : "runs")} running again", false, cancellationToken);
        return (made, way);
    }

    /// <summary>
    /// What a merge needs of the node's own state before anything is asked of GitHub: the flight flying with every leg done and its
    /// review passed, and the head and method asked for. Throws <see cref="FlightException"/> with why not. Under the lock.
    /// </summary>
    private (FlightRow Flight, string Head, string Method) Mergeable(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        var head = args.Length > 0 ? args[0].Trim() : "";
        var method = args.Length > 1 ? args[1] : Models.MergeMethod;
        if (head.Length < 7)
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified, "merge needs the head commit you reviewed (7 characters at least): the merge is refused if the PR has moved past it");
        }
        if (method is not ("squash" or "rebase" or "merge"))
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified, $"merge takes squash, rebase or merge, not {method}");
        }
        if (flight.State != FlightState.Flying)
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified, $"{flight.Code} is {flight.State.ToString().ToLowerInvariant()}");
        }
        if (state.Legs(flight.Code).Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open)
        {
            throw new MergeBlockedException(MergeRefusals.NodeWait, $"{flight.Code} has legs not done: {string.Join(", ", open)}");
        }

        if (flight.Review is not { State: ReviewState.Passed })
        {
            throw new MergeBlockedException(MergeRefusals.NodeWait, flight.Review is null
                ? $"{flight.Code} has not been reviewed; open its PR with the node (flight pr), which starts the review"
                : $"{flight.Code}'s review is {flight.Review.State.ToString().ToLowerInvariant()}, not passed");
        }
        if (flight.Holding is not null && args.Length > 2 && args[2] == MergeAsIs && IsPerson(flight, command.Who))
        {
            ReleaseHolding(flight, command.Who, command.Why?.Trim() is { Length: > 0 } asIsWhy ? asIsWhy : "merged as it is, the watchtower had not cleared it in time");
            flight = FlightOf(command.Target);
        }
        if (flight.Holding is { } holding)
        {
            throw new MergeBlockedException(MergeRefusals.NodeWait, $"{flight.Code} is in a holding pattern since {holding.Since.ToLocalTime():HH:mm}: {holding.Reason}; the watchtower clears it (`flight clear {flight.Code}`) or pushes its fixes (`flight pr {flight.Code}`); a hold the watchtower does not take (`flight hold {flight.Code}`, or a commit in the plane) is released by the node and merges with its notes");
        }
        // A question of design the review asked of this PR and nobody has answered holds the merge: answer it or dismiss it first.
        if (PrRef.Parse(flight.PullRequestUrl) is { } own
            && state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReviews.DesignKind && c.Subject == Verbs.PrTarget(own.Repo, own.Number)) is { } design)
        {
            throw new MergeBlockedException(MergeRefusals.DesignQuestion, $"{flight.Code}'s PR {own} has an open design question (card {design.Id}): answer it or dismiss it first");
        }
        return (flight, head, method);
    }

    /// <summary>
    /// The flight's PR as GitHub has it, open at the head the controller reviewed with nothing in the plane past it, and not
    /// conflicting. Throws <see cref="FlightException"/> with why not. Asks GitHub and git: never under the lock.
    /// </summary>
    private async Task<PullRequest> LookAtPrAsync(FlightRow flight, string head, CancellationToken cancellationToken)
    {
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken)
            ?? throw new MergeBlockedException(MergeRefusals.Unpushed, $"{flight.Branch} has no PR");
        if (!string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            var said = $"#{pr.Number} is {pr.State.ToLowerInvariant()}, not open";
            // Merged already is what a merge wants (#799 rule 6): the caller lands the flight, it is no refusal to ring.
            throw string.Equals(pr.State, "MERGED", StringComparison.OrdinalIgnoreCase) && (flight.PullRequest is null || flight.PullRequest == pr.Number)
                ? new AlreadyMergedException(said)
                // Closed unmerged: nothing to merge and nothing to press.
                : new MergeBlockedException(MergeRefusals.Closed, said);
        }
        if (!pr.Head.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            pr = await RepinMovedHeadAsync(flight, pr, head, cancellationToken);
        }
        // A commit made in the plane after the PR's last push would be left behind by the merge, and the landing would
        // then take the plane with it (TIA1658's plan read, 2026-10-23, landed by hand afterwards).
        if (Directory.Exists(flight.Worktree)
            && await git.RunAsync(["rev-list", "--count", $"{pr.Head}..HEAD"], flight.Worktree, cancellationToken) is { Ok: true } ahead
            && int.TryParse(ahead.Output.Trim(), out var unpushed) && unpushed > 0)
        {
            throw new MergeBlockedException(MergeRefusals.Unpushed, $"{flight.Code}'s plane holds {unpushed} commit{(unpushed == 1 ? "" : "s")} past #{pr.Number}'s head: open the PR again (flight pr) to push {(unpushed == 1 ? "it" : "them")}, and review what is new before it lands");
        }
        if (string.Equals(pr.Mergeable, "CONFLICTING", StringComparison.OrdinalIgnoreCase))
        {
            throw new MergeBlockedException(MergeRefusals.Conflicting, $"#{pr.Number} conflicts with its base, so GitHub cannot merge it");
        }
        // Behind its base by the plane's own look, not only GitHub's (BEHIND needs a "require up to date" rule): never sent to the
        // queue to find out. The caller brings it up to date and reviews it afresh.
        if (Directory.Exists(flight.Worktree))
        {
            await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
            // The PR's head once fetched; the plane's own head where the remote has not got it (the two are one: nothing is past it).
            var tip = (await git.RunAsync(["cat-file", "-e", $"{pr.Head}^{{commit}}"], flight.Worktree, cancellationToken)).Ok ? pr.Head : "HEAD";
            if (!(await git.RunAsync(["merge-base", "--is-ancestor", flight.Base, tip], flight.Worktree, cancellationToken)).Ok)
            {
                // A base with a merge queue is left to it: the queue builds the PR on top of the latest base (and of the entries
                // ahead of it) itself, so the base merged in first only pushes a new head, whose PR checks run again before the
                // queue is joined, at every landing on the base. Each landing restarted every waiting flight's wait that way
                // (pilot #507 and #509, 2026-10-30). A PR GitHub has queued is left as it is too: a push would drop it from the queue.
                if (await github.MergeQueueAsync(flight.RepoPath, pr.Number, cancellationToken) is not null)
                {
                    return pr;
                }
                throw new MergeBlockedException(MergeRefusals.Passing, BehindText(pr.Number, await HeadWords.BehindAsync(git, flight.Worktree, flight.Base, $"origin/{BaseBranch(flight)}", tip, cancellationToken)));
            }
        }
        return pr;
    }

    private const string HeadMovedMarker = "look again before it lands";

    /// <summary>True for the head-moved refusal of <see cref="RepinMovedHeadAsync"/> (a PR head past the reviewed one, in words).</summary>
    private static bool HeadMoved(string error) => error.EndsWith(HeadMovedMarker, StringComparison.Ordinal) && error.Contains("head is ", StringComparison.Ordinal);

    /// <summary>
    /// A merge refused because the PR's head moved past the reviewed head: when the head has commits of its own past it (not only
    /// the base), the merge row is dropped saying a review of the new head starts and the merge is offered again once it passes, and
    /// that review is started the way the sweep starts an unread-head review. Returns the error the command answers. Outside the lock.
    /// </summary>
    private async Task<string> HeadMovedAsync(CommandRequest command, string error, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        var reviewed = flight.Review?.ReviewedHead;
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        var own = true;
        if (reviewed is { Length: > 0 } && pr is not null && Directory.Exists(flight.Worktree))
        {
            await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
            var count = await git.RunAsync(["rev-list", "--first-parent", "--no-merges", "--count", $"{reviewed}..{pr.Head}"], flight.Worktree, cancellationToken);
            own = !(count.Ok && count.Output.Trim() == "0");
        }
        if (!own || reviewed is not { Length: > 0 } || pr is null)
        {
            DropMerge(flight.Code, error);
            return error;
        }
        // The unread-head review reads the plane: a head pushed from outside it leaves the plane at the reviewed head, and the start
        // would quietly do nothing, so that case says so instead of promising a review.
        var plane = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
        if (plane.Ok && string.Equals(plane.Output.Trim(), reviewed, StringComparison.OrdinalIgnoreCase))
        {
            var outside = $"the head moved to {CommitRefs.Short(pr.Head)} past the reviewed {CommitRefs.Short(reviewed)} from outside the plane, which still holds the reviewed head: no review starts until the plane has the new head";
            DropMerge(flight.Code, outside);
            return $"{error}; {outside}";
        }
        var reason = $"the head moved to {CommitRefs.Short(pr.Head)} past the reviewed {CommitRefs.Short(reviewed)}: a review of it starts, and the merge is offered again once it passes";
        DropMerge(flight.Code, reason);
        var started = await RunAsync(new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(flight.Code), Args = [UnreadHeadArg], Who = "node" }, cancellationToken);
        return started.Ok ? $"{error}; {reason}" : $"{error}; {reason}, but the review did not start: {started.Error ?? "no reason given"}";
    }

    /// <summary>A moved head in words (<see cref="HeadWords"/>, shas after them); the plain form when the plane cannot say.</summary>
    internal async Task<string> MovedHeadWordsAsync(FlightRow flight, string reviewed, string head, CancellationToken cancellationToken)
    {
        if (Directory.Exists(flight.Worktree))
        {
            await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
            return (await HeadWords.DescribeAsync(git, flight.Worktree, flight.Base, $"origin/{BaseBranch(flight)}", reviewed, head, cancellationToken)).Text;
        }
        return $"newer than the review ({CommitRefs.Short(head)} · reviewed {CommitRefs.Short(reviewed)})";
    }

    private static string BehindText(int number, string? words = null) => words is null ? $"#{number} is behind its base" : $"#{number} is behind its base ({words})";

    /// <summary>
    /// The PR's head is not the one the card named. A head moved only by its base, by whitespace or by documentation (.md) changes is
    /// re-pinned: the review's head and marks move to it and the landing goes on with it. A real code change throws, in words
    /// (<see cref="HeadWords"/>), asking for a re-review.
    /// </summary>
    private async Task<PullRequest> RepinMovedHeadAsync(FlightRow flight, PullRequest pr, string head, CancellationToken cancellationToken)
    {
        if (flight.Review is { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } reviewed } && Directory.Exists(flight.Worktree)
            && reviewed.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            await git.RunAsync(["fetch", "--quiet", "origin"], flight.Worktree, cancellationToken);
            var words = await HeadWords.DescribeAsync(git, flight.Worktree, flight.Base, $"origin/{BaseBranch(flight)}", reviewed, pr.Head, cancellationToken);
            if (words.Repinnable && words.Relation != HeadRelation.Unknown)
            {
                Record(flight.Code, f => f with { Review = f.Review is { } held ? held with { ReviewedHead = pr.Head, StatusState = null } : null });
                await _marks.ApplyAsync(flight.Code, cancellationToken);
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code}'s head moved by {flight.Base} or by whitespace or documentation only; its review carries to {CommitRefs.Short(pr.Head)}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
                return pr;
            }
            // The node starts a review of the new head (HeadMovedAsync): it waits on that.
            throw new MergeBlockedException(MergeRefusals.NodeWait, $"#{pr.Number}'s head is {words.Text}: look again before it lands");
        }
        throw new MergeBlockedException(MergeRefusals.NodeWait, $"#{pr.Number}'s head is newer than the one you reviewed ({CommitRefs.Short(pr.Head)} · reviewed {CommitRefs.Short(head)}): look again before it lands");
    }

    private static bool Behind(string error) => error.Contains("is behind its base", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shows a merge that is armed or queued (<paramref name="way"/>, a <see cref="MergeWays"/>) on every open card of the target
    /// offering it: the card says <paramref name="line"/>, its Merge choice armed, until the merge lands, or fails and the card
    /// is turned by <see cref="ShowMergeFailed"/>.
    /// </summary>
    public void ShowMerge(string target, string? line, string way, string? note = null)
    {
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind is PrReady or MergeFailed && c.Subject == target && c.Choices.Any(ch => ch.Verb == Verbs.Merge)))
        {
            var code = target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? target[Verbs.FlightTargetPrefix.Length..] : target;
            var flight = state.Flights().FirstOrDefault(f => f.Code == code);
            var merge = card.Choices.First(ch => ch.Verb == Verbs.Merge);
            _beforeMerging.TryRemove(card.Id, out _);
            // The line says how far the checks are, or where in the queue: the choice carries it too, for a client to put on the button.
            note ??= way == MergeWays.Queued ? System.Text.RegularExpressions.Regex.Match(line ?? "", @"position \d+") is { Success: true } place ? place.Value : null : ArmedProgress.Read(line);
            // A card says what is set on GitHub: armed only where GitHub's auto-merge is, otherwise the node waits and then queues or merges.
            var byGitHub = flight?.MergeWhenGreen?.ElementAtOrDefault(2) == ArmedByGitHub;
            state.UpdateCard(card.Id,
                way == MergeWays.Queued ? $"{code}'s PR #{flight?.PullRequest} is in the merge queue"
                    : byGitHub ? $"{code}'s PR #{flight?.PullRequest} merges when its checks pass" : $"{code}'s PR #{flight?.PullRequest} waits for its checks, then the node queues it",
                line, [merge with { Label = "Merge", Status = way == MergeWays.Queued ? ChoiceStatus.Queued : ChoiceStatus.Armed, Note = note }], card.Link ?? flight?.PullRequestUrl, PrReady,
                way == MergeWays.Queued ? $"The merge of {code} is in GitHub's merge queue. GitHub merges it by itself, with the node up or not."
                    : byGitHub ? $"The merge of {code} is armed. GitHub merges it by itself once its checks pass, with the node up or not."
                    : Models.Autopilot ? $"On autopilot: the node waits for the checks of {code}'s PR, then queues or merges it. Nothing for you to do unless they fail."
                    : $"Nothing is armed on GitHub: the node waits for the checks of {code}'s PR, then queues or merges it, while it is up.",
                bin: MergeCardBin());
        }
    }

    /// <summary>
    /// The bin of a merge card: with the autopilot on the node does the merge itself, so the card is a quiet For-you card until
    /// <paramref name="ownerMustAct"/> (checks failed, queue dropped, GitHub blocks it, the node gave up); with it off, Ring as ever.
    /// </summary>
    public CardBin MergeCardBin(bool ownerMustAct = false) => Models.Autopilot && !ownerMustAct ? CardBin.ForYou : CardBin.Ring;

    /// <summary>
    /// While the flight's merge command is under way, its open pr-ready or merge-failed cards show the Merge choice as
    /// <see cref="ChoiceStatus.Merging"/> with <paramref name="note"/> saying what it waits for. Title and detail stay; the choice
    /// the card offered is kept for <see cref="RestoreMerge"/>. Not a button: a press is answered as in progress.
    /// </summary>
    public void ShowMerging(string code, string note)
    {
        var target = Verbs.FlightTarget(code);
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind is PrReady or MergeFailed && c.Subject == target && c.Choices.Any(ch => ch.Verb == Verbs.Merge)))
        {
            var merge = card.Choices.First(ch => ch.Verb == Verbs.Merge);
            if (merge.Status == ChoiceStatus.Merging)
            {
                if (merge.Note == note)
                {
                    continue;
                }
            }
            else
            {
                _beforeMerging[card.Id] = merge;
            }
            state.UpdateChoices(card.Id, [.. card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = ChoiceStatus.Merging, Note = note } : c)]);
        }
    }

    /// <summary>A merge command that ended refused: every card it marked Merging offers the Merge it offered before.</summary>
    private void RestoreMerge(string code)
    {
        var target = Verbs.FlightTarget(code);
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Subject == target && c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Status == ChoiceStatus.Merging)))
        {
            _beforeMerging.TryRemove(card.Id, out var before);
            state.UpdateChoices(card.Id, [.. card.Choices.Select(c => c.Verb == Verbs.Merge && c.Status == ChoiceStatus.Merging ? before ?? c with { Status = null, Note = null } : c)]);
        }
    }

    /// <summary>
    /// Sets an open PR-ready card's Merge choice, title and detail to what GitHub says of its PR (<paramref name="merge"/>), one
    /// word per state: the same card, in place, each look at it. Checks running and ready keep their button (Merge arms, or
    /// merges); queued, blocked and changes requested are marks, not buttons; conflicts and behind turn the card to a go-around
    /// (<see cref="ShowMergeFailed"/>) that rebases, with no Merge. Nothing for a state the card already shows.
    /// <paramref name="replaceWords"/> turns a card that said what failed (<see cref="MergeFailed"/>) back to a plain Merge: its
    /// words and its go-around and re-run go, as GitHub no longer shows what they were about.
    /// </summary>
    public void ShowMergeState(CardRow card, MergeState merge, string head, bool replaceWords = false)
    {
        var code = card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? card.Subject[Verbs.FlightTargetPrefix.Length..] : card.Subject;
        var flight = state.Flights().FirstOrDefault(f => f.Code == code);
        var number = flight?.PullRequest;
        // A leg still to fly (a review-notes leg making the review's fixes, planned after the card was raised): a Merge now is refused
        // ("has legs not done"). So the card's Merge is a mark, not a button (Blocked, naming the leg), and the card says what the merge
        // waits for, here and on the PR page that takes its button from it (#918; PIL853's #920 said "ready" with a Merge that answered
        // "legs not done: review-notes-2", twice in one evening). The choice stays, with the reviewed head the autopilot and the
        // holding bound read; once the leg lands, the next look turns it back to what GitHub says (below) and the autopilot merges.
        if (flight is not null && state.Legs(code).Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open
            && card.Choices.Any(c => c.Verb == Verbs.Merge))
        {
            var legsLeft = string.Join(", ", open);
            var waitTitle = $"{code}'s PR #{number} merges once {legsLeft} {(open.Count == 1 ? "is" : "are")} done";
            var waitWhy = Models.Autopilot
                ? $"The {legsLeft} {(open.Count == 1 ? "leg makes" : "legs make")} the review's fixes; the node merges the PR once {(open.Count == 1 ? "it lands" : "they land")}."
                : $"The {legsLeft} {(open.Count == 1 ? "leg makes" : "legs make")} the review's fixes; Merge is offered once {(open.Count == 1 ? "it lands" : "they land")}.";
            var waitNote = $"waits for {legsLeft}";
            if (card.Title != waitTitle || card.Choices.Any(c => c.Verb == Verbs.Merge && (c.Status != ChoiceStatus.Blocked || c.Note != waitNote)))
            {
                state.UpdateCard(card.Id, waitTitle, waitWhy,
                    [.. card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = ChoiceStatus.Blocked, Note = waitNote } : c)],
                    flight.PullRequestUrl ?? card.Link, PrReady, waitWhy, card.Bin);
            }
            return;
        }
        // The PR's page, not the card's link: a card that showed a failed check's page turns back to the PR's.
        var link = flight?.PullRequestUrl ?? card.Link;
        // The card's own words (the review, the head) stay while the Merge only changes its colour; a state that wrote its own
        // words over them (queued, blocked) is replaced by a plain line when the PR is mergeable again.
        var showing = card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge)?.Status;
        var hasMerge = card.Choices.Any(c => c.Verb == Verbs.Merge);
        var own = !replaceWords && hasMerge && showing is null or ChoiceStatus.Passing or ChoiceStatus.Pending or ChoiceStatus.Failing or ChoiceStatus.Conflicting;
        var ready = $"{code}'s PR #{number} is ready to merge";
        // A merge that was armed and is not any more says so, and how to arm it again.
        var unarmed = showing == ChoiceStatus.Armed && merge.DroppedFromQueue is null ? "Its merge is not armed any more: GitHub's auto-merge was turned off, or the merge was dropped. Merge arms it again." : null;
        var (status, note, title, detail) = merge.Kind switch
        {
            MergeKinds.Pending when merge.AwaitsCheck => (ChoiceStatus.Pending, null, $"{code}'s PR #{number} waits for a required check", own ? card.Detail : unarmed ?? "A check GitHub requires has yet to report. Merge arms the merge: it goes through once it passes."),
            MergeKinds.Pending => (ChoiceStatus.Pending, merge.Progress, $"{code}'s PR #{number}'s checks are running", own ? card.Detail : unarmed ?? "Its checks run again. Merge arms the merge: it goes through once they pass."),
            MergeKinds.Ready => (ChoiceStatus.Passing, null, ready, own ? card.Detail : unarmed ?? "Nothing on GitHub stands in its way."),
            // Armed on green checks with a review still to come: GitHub waits for that, not for checks.
            MergeKinds.Armed when merge.Reason is { } waits => (ChoiceStatus.Armed, merge.Progress, $"{code}'s PR #{number} merges once it has {waits}", $"GitHub's auto-merge is armed: its checks passed, and GitHub waits for {waits} before it merges. GitHub merges it then, with the node up or not."),
            MergeKinds.Armed => (ChoiceStatus.Armed, merge.Progress, $"{code}'s PR #{number} merges when its checks pass", $"GitHub's auto-merge is armed: merging when checks pass{(merge.Progress is { } progress ? $", {progress} done" : "")}. GitHub merges it, with the node up or not."),
            MergeKinds.Queued => (ChoiceStatus.Queued, merge.Place, $"{code}'s PR #{number} is in the merge queue", QueuedLine(code, merge.Place is { } place ? $"in the merge queue, {place}" : "in the merge queue")),
            MergeKinds.Blocked => (ChoiceStatus.Blocked, merge.FailedName ?? merge.Reason, $"{code}'s PR #{number} is blocked: {merge.Reason}",
                merge.FailedName is { } name ? $"GitHub blocks the merge: {name} failed. Redo the leg to fix it, or re-run the check." : $"GitHub blocks the merge: {merge.Reason}."),
            MergeKinds.ChangesRequested => (ChoiceStatus.ChangesRequested, null, $"{code}'s PR #{number} has changes requested", "A review on GitHub asks for changes. They are read first, then the merge is offered again."),
            _ => ((string?)null, null, null, null),
        };
        if (status is null || title is null)
        {
            return;
        }
        // A dropped queue entry is said on a card that showed it queued, once it is offered again: why GitHub took it out.
        if (!own && merge.DroppedFromQueue is { } dropped && merge.Kind is MergeKinds.Ready or MergeKinds.Pending)
        {
            detail = $"Dropped from the merge queue: {dropped}. Merge queues it again once that is fixed.{(detail is null ? "" : " " + detail)}";
        }
        // A card turned to a go-around (conflicting, behind) has no Merge: it comes back with the state that allows one.
        var choices = hasMerge && !replaceWords
            ? card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Label = "Merge", Status = status, Note = note } : c).ToList()
            : [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = card.Subject, Args = [head, .. card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge)?.Args.Skip(1) ?? []], Status = status, Note = note }];
        // The failed check's page is the link only where a failed check is what blocks it: the others keep the PR's.
        state.UpdateCard(card.Id, title, detail, choices, merge is { Kind: MergeKinds.Blocked, FailedName: not null } ? merge.FailedLink ?? link : link, PrReady, MergeSummary(code, merge),
            MergeCardBin(merge.Kind is MergeKinds.Blocked or MergeKinds.ChangesRequested || !own && merge.DroppedFromQueue is not null));
    }

    /// <summary>What a PR-ready card says of the merge state it shows: its own sentence for each state, never the table's "is ready" for a PR GitHub holds back.</summary>
    internal static string MergeSummary(string code, MergeState merge) => merge.Kind switch
    {
        MergeKinds.Pending when merge.AwaitsCheck => $"The PR of {code} waits for a check GitHub requires. Merge arms it, and it goes through once the check passes.",
        MergeKinds.Pending => $"The checks of the PR of {code} are still running. Merge arms it, and it goes through once they pass.",
        MergeKinds.Armed or MergeKinds.Queued => $"The merge of {code} is set. GitHub merges it by itself, with the node up or not.",
        MergeKinds.Blocked when merge.FailedName is { } name => $"GitHub blocks the merge of {code}: the check {name} failed. Redo the last leg from the flight page or with pilot leg go-around to fix what it found, or re-run the check.",
        MergeKinds.Blocked => $"GitHub blocks the merge of {code}: {merge.Reason}. It waits until that is cleared.",
        MergeKinds.ChangesRequested => $"A review on GitHub asks for changes to {code}. They are read first, then the merge is offered again.",
        _ => $"{code} is reviewed and its PR is ready. Merge lands it; dismissing leaves the PR open.",
    };

    /// <summary>
    /// Turns an armed merge's card, open still, into what failed: <paramref name="title"/> and <paramref name="detail"/> say why, its
    /// link the failed check's page, and it offers a go-around of the flight's last leg, a re-run of the failed checks
    /// (<paramref name="rerun"/>, when checks failed rather than the head moved) and the merge again. False when the flight has no
    /// such card, and nothing is changed. <paramref name="because"/> is why it is unmergeable, as a clause that follows "did not go
    /// through" ("because check build failed", "because the PR conflicts with main"): the summary says it, never only that it failed.
    /// </summary>
    public bool ShowMergeFailed(string code, string head, string method, string title, string detail, string? link, bool rerun, string goAroundWhy, bool mergeAgain = true, string kind = MergeFailed, string? because = null)
    {
        var target = Verbs.FlightTarget(code);
        // A go-around card (no Merge) is the one a conflicting PR already turned; it is turned again for the head it conflicts on.
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind is PrReady or MergeFailed && c.Subject == target && c.Choices.Any(ch => ch.Verb is Verbs.Merge or Verbs.GoAround)) is not { } card)
        {
            return false;
        }
        var choices = new List<CardChoice>();
        if (state.Legs(code).OrderBy(l => l.Order).LastOrDefault() is { } last)
        {
            choices.Add(new CardChoice { Label = Models.RedoLabel(last, rerun ? "left the PR's checks failing" : "left the PR unmergeable"), Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, last.Name), Args = [goAroundWhy] });
        }
        if (rerun)
        {
            choices.Add(new CardChoice { Label = $"Re-run the checks of {code}'s PR", Verb = Verbs.RerunChecks, Target = target, Args = [head, method] });
        }
        // A PR GitHub cannot merge as it is (conflicting, behind) is offered the go-around that merges its base in, and no Merge to refuse.
        if (mergeAgain)
        {
            choices.Add(new CardChoice { Label = $"Merge {code}'s PR again as it is", Verb = Verbs.Merge, Target = target, Args = [head, method] });
        }
        // What each choice is for, in other words: the buttons show their whole labels, so the sentence must not repeat them.
        var offers = new List<string>();
        if (choices.Any(c => c.Verb == Verbs.GoAround) && state.Legs(code).OrderBy(l => l.Order).LastOrDefault() is { } lastLeg)
        {
            offers.Add($"a fresh worker can fix the last leg ({code}/{lastLeg.Name})");
        }
        if (rerun)
        {
            offers.Add("the checks can be run again");
        }
        if (mergeAgain)
        {
            offers.Add("the merge can be tried once more as the PR stands");
        }
        // The panel shows 240 characters of a summary: the reason gets what the rest of the sentence leaves (at most MaxBecause, at
        // least MinBecause), and a longer one is cut here, whole in the detail. A long leg name takes room from it, not from the offers.
        var said = because is { Length: > 0 } ? because : rerun ? "because the PR's checks failed" : null;
        var rest = $"The merge of {code} did not go through.{(offers.Count == 0 ? "" : " Choose: " + JoinOffers(offers) + ".")}";
        var room = Math.Clamp(PanelSummaryChars - rest.Length - 1, MinBecause, MaxBecause);
        var why = said is null ? "" : " " + (said.Length > room ? said[..(room - 3)].TrimEnd() + "..." : said);
        var summary = $"The merge of {code} did not go through{why}.{(offers.Count == 0 ? "" : " Choose: " + JoinOffers(offers) + ".")}";
        state.UpdateCard(card.Id, title, detail, choices, link ?? card.Link, kind, summary, CardBin.Ring);
        return true;
    }

    // The longest reason a merge-failed summary says, and the shortest it is cut to, so the summary with its choices stays inside the panel's 240 characters.
    private const int MaxBecause = 70;
    private const int MinBecause = 12;

    // What the For-you panel shows of a summary (CardsView.SummaryChars, in Pilot.Ui, which the node does not reference).
    private const int PanelSummaryChars = 240;

    // "a", "a or b", "a, b or c".
    private static string JoinOffers(IReadOnlyList<string> offers) =>
        offers.Count == 1 ? offers[0] : string.Join(", ", offers.Take(offers.Count - 1)) + " or " + offers[^1];

    // A merge refused because the PR no longer merges into its base (main moved under it), in the node's own words as RefusalOf
    // writes them; GitHub's texts are sorted by MergeAnswers alone.
    private static bool Conflicts(string error) =>
        error.Contains("conflicts with its base", StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks the Merge choice of every open card about <paramref name="target"/> with <paramref name="status"/>, for every client to show.</summary>
    public void MarkMerge(string target, string? status)
    {
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Target == target)))
        {
            state.UpdateChoices(card.Id, [.. card.Choices.Select(c => c.Verb == Verbs.Merge && c.Target == target ? c with { Status = status } : c)]);
        }
    }

    /// <summary>The card saying a merge was taken back for a new leg, and why.</summary>
    public const string MergeTakenBackKind = "merge-taken-back";

    /// <summary>
    /// A flight whose merge is armed (the node's, or GitHub's auto-merge) or queued in GitHub's merge queue is about to get more
    /// work: the merge is taken back, as it would otherwise land the PR without the new leg. The node's arm is cleared, GitHub's
    /// auto-merge disarmed, the PR taken out of the queue, the passed review turned back (so the merge waits on a fresh one) and
    /// the PR-ready card's Merge closed, and a card says so. Nothing when no merge stands. A disarm or dequeue GitHub refuses
    /// refuses the verb, with that said: a PR is never left armed under a new leg. GitHub is asked with the commander's lock let go, the
    /// flight taken (<see cref="Calling"/>); what was found is recorded under the lock, on the flight as it is then.
    /// </summary>
    private async Task TakeMergeBackAsync(FlightRow flight, string verb, string why, CancellationToken cancellationToken)
    {
        if (flight.PullRequest is not { } number || flight.State == FlightState.Landed)
        {
            return;
        }
        // Every ask of GitHub is made with the lock let go (PIL391, as PIL338's): the flight is this command's meanwhile, and what
        // was found is recorded only if the flight is still as it was decided on, on the flight as it is then.
        using var call = Calling(flight.Code, "taking its merge back");
        var (mergedHead, queued, armedSaid, took) = await OffLockAsync(flight, async () =>
        {
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            // Too late: GitHub merged it already. The new leg's work is not lost: it goes to a new PR from the merged base, and the node rings at once.
            if (pr is { State: "MERGED" } && pr.Number == number)
            {
                return (pr.Head, false, (bool?)false, new List<string>());
            }
            var open = pr is { State: "OPEN" };
            var queued = open && await github.MergeQueueAsync(flight.RepoPath, number, cancellationToken) is { State: "OPEN", Queued: true };
            // Unknown is treated as armed, as EnsureNotArmedAsync does: the disarm is no error when it was not on.
            var armedSaid = open ? await github.AutoMergeArmedAsync(flight.RepoPath, number, cancellationToken) : false;
            var armedOnGitHub = armedSaid != false;
            var took = new List<string>();
            if (flight.MergeWhenGreen is null && !queued && !armedOnGitHub)
            {
                return ((string?)null, queued, armedSaid, took);
            }
            if (armedOnGitHub)
            {
                try
                {
                    await github.DisarmAutoMergeAsync(flight.RepoPath, number, cancellationToken);
                }
                catch (FlightException ex)
                {
                    throw new FlightException($"{verb} is refused: #{number} may be armed with GitHub's auto-merge, which would merge it without the new leg, and it could not be turned off ({ex.Message}): switch it off on the PR page, then try again");
                }
                if (armedSaid == true)
                {
                    took.Add("GitHub's auto-merge turned off");
                }
            }
            if (queued)
            {
                try
                {
                    await github.DequeueAsync(flight.RepoPath, number, cancellationToken);
                }
                catch (FlightException ex)
                {
                    throw new FlightException($"{verb} is refused: #{number} is in the merge queue, which would merge it without the new leg, and it could not be taken out ({ex.Message}): remove it from the queue on the PR page, then try again");
                }
                took.Add("taken out of the merge queue");
            }
            return ((string?)null, queued, armedSaid, took);
        });
        var target = Verbs.FlightTarget(flight.Code);
        if (mergedHead is not null)
        {
            flights.MarkMergedUnderLeg(FlightOf(target), mergedHead, why);
            return;
        }
        // The entry in the node's merge queue goes with it, whatever else stands.
        state.RemoveMerge(flight.Code);
        if (flight.MergeWhenGreen is null && !queued && armedSaid == false)
        {
            return;
        }
        if (flight.MergeWhenGreen is not null)
        {
            Record(flight.Code, f => f with { MergeWhenGreen = null });
            if (took.Count == 0)
            {
                took.Add("no longer waiting for its checks");
            }
        }
        flight = FlightOf(target);
        // The review that passed was of a head without the new work: the merge waits on a fresh one. It lets the lock go itself, and
        // needs nothing of the call held here (a flight's call is a registry entry, not a lock).
        if (flight.Review is { State: ReviewState.Passed })
        {
            await TurnToChangesAsync(flight, why, cancellationToken);
            flight = FlightOf(target);
        }
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{flight.Code}'s merge was taken back: {why}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
        state.RaiseCard(CardBin.Handled, MergeTakenBackKind, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s merge of PR #{number} was taken back",
            $"{(took.Count > 0 ? $"{string.Join(", ", took)}. " : "")}{why}. The merge is offered again once the new leg is done and a fresh review passes.",
            link: flight.PullRequestUrl);
    }
}
