using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

namespace Pilot.Node.Flights;

/// <summary>
/// The plain sentence every card carries (<see cref="CardRow.Summary"/>): what happened and what the choices do, kept apart from
/// the raw detail. A raiser that wrote one passes it; any other card gets one here from its kind and subject, and a kind this
/// table does not know gets a generic line built from the title and the choices. Text that looks like a log is never one.
/// </summary>
public static partial class CardSummaries
{
    // Well above the longest sentence a raiser writes (the pr-conflicts one, about 200 with a long code), so none is thrown away;
    // text taken from a detail is far longer than a sentence and still fails.
    private const int MaxLength = 600;

    /// <summary>Whether <paramref name="text"/> can stand as a summary: short, at most two lines, and nothing like a log.</summary>
    public static bool Usable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return false;
        }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length is > 0 and <= 2 && !LooksLikeLog().IsMatch(text);
    }

    /// <summary>The summary to keep: the raiser's when it is usable and not just the detail, else the table's.</summary>
    public static string Resolve(string? given, string? detail, string kind, string subject, string title, IReadOnlyList<CardChoice> choices) =>
        Usable(given) && !string.Equals(given!.Trim(), detail?.Trim(), StringComparison.Ordinal) ? given!.Trim() : For(kind, subject, title, choices);

    /// <summary>The sentence for a card of <paramref name="kind"/> about <paramref name="subject"/>.</summary>
    public static string For(string kind, string subject, string title, IReadOnlyList<CardChoice> choices)
    {
        var what = Name(subject);
        return kind switch
        {
            LegCommander.ReviewChanges => $"The review of {what} found things to fix. Fix the review's findings has a worker fix them on the leg's work as it stands, nothing thrown away; Land it with the review's findings unfixed takes it as it is.",
            LegCommander.PrReady => $"{what} is reviewed and its PR is ready. Merge lands it; dismissing leaves the PR open.",
            LegCommander.MergeFailed => $"The merge of {what} did not go through. Start a fresh worker on its last leg sends one to fix it; Merge its PR again as it is tries once more.",
            "leg-failed" => $"The worker of {what} stopped. Send it round again starts a fresh worker told why; Accept it as it stands keeps the failure in its report and the flight goes on.",
            LegCommander.PushBlockedKind => $"{what} is done and its check passes, but its branch was rebased off the pushed one. Push the rebased branch with lease overwrites origin's only if it has not moved; Merge origin's branch into the plane keeps its commits and pushes plain.",
            "pr-checks-failed" => $"The PR of {what} fails its checks and the node could not send the last leg back to fix them. The first choice starts a fresh worker on that leg, told which checks failed and to push a fix; or fix them by hand, then dismiss this.",
            "pr-conflicts" => $"The PR of {what} conflicts with its base and the node could not merge the base in by itself. The watchtower resolves it in the plane (merge, check, plain push); Resolve the conflict has a worker on the last leg do it, for real work only.",
            LegWatch.Stopped => $"The worker of {what} was stopped by the API and the node cannot resume it by itself. Tell it to carry on, or start a fresh worker on the leg.",
            LegWatch.LimitWait => $"The worker of {what} hit the usage limit. The node resumes it by itself after the window resets; nothing to do.",
            LegWatch.Idle => $"The worker of {what} has been idle with its leg unfinished and the node's nudges did not help. Nudge it to finish or say why not, or start a fresh worker on the leg.",
            LegWatch.Waiting => $"The worker of {what} waits on you for an answer. Answer it in its own session, or start a fresh worker on the leg.",
            LegWatch.Failed or LegWatch.Missing => $"The worker of {what} is gone. Tell it to carry on if it is still there, or start a fresh worker on the leg.",
            LegWatch.OverBudget => $"The worker of {what} is past its budget but keeps working. Let it spend more, or start a fresh worker on the leg.",
            LegWatch.ReviewStalled => $"The reviewer of {what} stalled twice. Start a fresh reviewer, or dismiss this once it moves.",
            LegWatch.ValidationStalled => $"The validator of {what} stalled. Start the leg without its validation, or drop the leg.",
            LegCommander.NoGoKind => $"The validator of {what} reports the tree is not as its brief assumes. Start the leg as the tree is, drop it, or plan the flight again.",
            LegCommander.WaitLateKind => $"{what} waits on a file that has not come in time. Look at what it waits for, or start a fresh worker on the leg.",
            LegCommander.NextStepKind => $"The node tried {what}'s next step and was refused. Do the step now to ask it again.",
            LegCommander.StartFailedKind => $"A session of {what} did not start and the node does not retry it by itself. Start it again from the card.",
            LegCommander.OverlapKind => $"{what} overlaps another flight's files. Fold it in, connect it, or plan both in parallel.",
            StopGate.Kind => $"A session of {what} stopped with files not committed after being told once. Tell it again to commit or drop them.",
            LegCommander.PlannedKind => $"{what} has a plan of legs. Start it to fly them, or change the plan first.",
            "landed" => $"{what} landed: its PR is merged and its legs are done. Nothing to do; dismiss it when read.",
            "not-landed" => $"The PR of {what} merged on GitHub, but landing the flight was refused (the title says why). The branch is in the base; land the flight by hand, then dismiss this.",
            "worker-nudged" => $"The worker of {what} went idle with its leg unfinished and the node nudged it. Nothing to do unless it goes idle again.",
            "worker-resumed" => $"The worker of {what} was resumed after its usage limit reset. Nothing to do.",
            LegWatch.PlanStalled => $"The planner of {what} stalled before it handed in legs. The card's choice tells it to finish, or plans the flight again.",
            LegCommander.HoldKind => $"{what} is held until a package a sibling repo is publishing reaches nuget.org. The node starts it again by itself; nothing to do.",
            LegCommander.MoveFailedKind => $"The move of {what} to another node failed and it stays flying here. Retry move runs the move again.",
            LegCommander.MovedAwayKind => $"{what} was handed over to another node, which flies it from here. Nothing to do.",
            LegCommander.LegRemovedKind => $"A leg of {what} was removed before it flew. Nothing to do; the legs after it keep their order.",
            LegCommander.FlownElsewhereKind => $"{what} is already flown on another node, so this node did not start it. Stop it there first, or leave it where it is.",
            LegCommander.ValidatingKind => $"A validator was started to check {what} against the tree before its worker starts. Nothing to do; the node starts the leg on its answer.",
            LegCommander.SiblingBehindKind => $"{what} compiles against a sibling clone that is behind origin and the node could not pull it. Pull it by hand, or let the leg run as it is.",
            LegCommander.SiblingPulledKind => $"The node pulled a sibling clone that was behind origin before it started {what}. Nothing to do.",
            LegCommander.SelfFinishedKind => $"The node finished {what} itself: its work was pushed and its check passed. Nothing to do.",
            LegCommander.PushDivergedKind => $"{what} was not pushed because the remote branch holds commits the plane lacks. Merge origin's branch into the plane by hand, then push again.",
            LegCommander.MergeTakenBackKind => $"The merge of {what}'s PR was taken back because a new leg was planned. The merge is offered again once the new leg is done and a fresh review passes; nothing to do.",
            "planner-started" => $"A planner was started for {what}. Nothing to do; its plan comes as a card.",
            PrMarks.RefusedKind => "GitHub refused a status, label or ready mark on a PR; the review itself stands. The node sets what is missing at its next look, so nothing to do unless it keeps refusing.",
            "node-label-refused" => "GitHub refused the node's label on a flight's PR or issue. Add it by hand if the flight should show which node flies it.",
            "plane-left" => "A flight's plane folder could not be deleted because a process holds it. The node tries again at its next start; delete it by hand if it stays.",
            "issue-left-open" => "A landed flight's issue is still open on GitHub. Close it by hand; its work is in the merged PR.",
            FlightCommander.FoldedKind => $"{what} was folded into another flight, whose planner now plans its points. Nothing to do.",
            FlightCommander.RoutedKind => "A new flight was made on another node because this one is in maintenance. Nothing to do.",
            FlightCommander.DisconnectedKind => "A flight was disconnected from another flight it had been connected to. Nothing to do.",
            FlightCommander.ReceivedKind => $"{what} was handed over to this node, which made its plane. Nothing to do.",
            FlightCommander.MergedUnderLegKind => $"GitHub merged the PR of {what} before the new leg's work could join it. The new leg's work goes to a new PR once the legs are done; nothing to do.",
            "checks-rerun" => $"Failed checks of {what}'s PR were run again once before its leg is sent round. Nothing to do unless they fail again.",
            PrChecks.ClosedLanded => $"{what}'s PR was closed without a merge and its plane held nothing of its own, so the flight landed. Nothing to do.",
            PrChecks.ClosedWithWork => $"{what}'s PR was closed without a merge but its plane holds work of its own. Reopen the PR, or drop the work and land the flight.",
            PrChecks.QueueDropped => $"GitHub took the PR of {what} out of the merge queue. The card's choice queues it again once the cause is dealt with.",
            PrChecks.MergeDropped => $"The merge of {what} did not go through. Merge it again once the cause is dealt with.",
            PrChecks.AutopilotMergeRefused => $"The autopilot's merge of {what}'s PR was refused. Merge once more after dealing with what the title says, or merge by hand.",
            CardKinds.AutopilotRan => "An assistant ran a command on the autopilot, which is on. Nothing to do; the card is the record.",
            CardKinds.PersonsWord => "The watchtower answered or dismissed a card at the person's word in its chat. Nothing to do; the card is the record.",
            Watchtower.RaisedKind => "A card raised for the person by the watchtower or a session. Read its detail and dismiss it once dealt with.",
            StaleCards.HandledKind => "The node read the fact itself and closed a card the summary proposed as resolved. Nothing to do.",
            PrRecheck.HandledKind => "The node rechecked a PR whose label asked for it. Nothing to do; the title says what it found.",
            PrRecheck.FailedKind => "The recheck of a PR could not run, and its label stays. Take the label off and put it back to try again.",
            PrRecheck.ConflictKind => "A PR conflicts with its base and it is not pilot's branch to update. Bring it up to date with its base and push; the node looks again.",
            PrReviews.ReadyKind => "pilot reviewed a PR and has its verdict. Read it in the detail; the choices on the card act on it.",
            PrReviews.StartedKind => "A reviewer was started on a PR. Nothing to do; its verdict comes as a card.",
            PrReviews.LeftKind => "A review's worktree could not be removed yet because a process holds it. The node removes it once it is let go.",
            PrReviews.RestartedKind => "A review whose reviewer was lost in a node restart was started afresh. Nothing to do.",
            PrReviews.LostKind => "A review was lost in a node restart and starting it again failed. Start the review afresh from the card.",
            PrReviews.PostedKind => "pilot posted its review on the PR. Nothing to do.",
            PrReviews.QueuedKind => "A PR reviewed clean with green checks was queued for merging. Nothing to do; the queue merges it.",
            PrReviews.MergeKind => "A PR is reviewed and green but the node does not merge it by itself. Merge it when you are ready.",
            PrReviews.QueueRefusedKind => "GitHub refused to queue a reviewed PR. Merge it once more after dealing with what the title says.",
            PrReviews.DesignKind => "The review of a PR asks a question of design, which is the author's to decide. No fixer started.",
            PrReviews.FixStartedKind => "A fixer was started on a PR's branch to make the changes its review asked for. Nothing to do.",
            PrReviews.FixOfferKind => "A review asked for changes on someone else's PR. Fix it starts a session on the PR's branch that makes them.",
            PrReviews.FixFailedKind => "The fixer for a PR could not start, or its push could not be reviewed. The card's choice starts it again.",
            PrReviews.FixCappedKind => "A PR still asks for changes after three rounds of fixes, so no fourth fixer starts. What is left is yours to decide.",
            LegCommander.LaidKind => $"{what}'s branch merged cleanly with its base but would not rebase onto it, so the node laid it on the base as one commit. Its review carries over; nothing to do.",
            LegCommander.LayFailedKind => $"The node could not lay {what} on its base as one commit, and the merge queue needs that. The detail says why; fix it by hand, then dismiss this.",
            LegCommander.DropUnexplainedKind => $"The merge queue dropped the PR of {what} or would not take its laid head, and the node found no cause. It is not queued again at this head; push a changed head or look at the PR page.",
            PrReviews.NoFixerKind => "A review asks for changes or puts a question, and the node starts no fixer on that PR. The detail says why; what is left is yours to decide.",
            PrReviews.WaitingKind => "A review or fixer of a PR waits for a free slot under the node's admission rules. The node starts it by itself; nothing to do unless the start did not go.",
            PrReviews.PushedUnreviewedKind => "A fixer pushed to a PR but the review of that push could not start. The push stands; Start the review of the fixer's push runs it.",
            AdmissionGate.CycleKind => "Two flights each waited on the other under the admission rules, so the node admitted one to break the wait. Nothing to do.",
            LegCommander.SpendAlertKind => "A leg has spent a large share of the 7-day window before its next round. The round still starts; stop the flight by hand if it should not.",
            LegCommander.ReviewNotesKind => "The review of a flight passed with notes that do not block. The watchtower makes the small ones in the plane; no leg goes round.",
            LegCommander.ReviewLoopKind => "The review of a flight has asked for changes in three rounds. Pass with notes ends the loop; Allow one more round lets a fourth start.",
            "review-kept" => "A flight's base was merged into its branch and its own diff is unchanged, so its passed review stands and no reviewer started. Nothing to do.",
            LegWatch.StraySession => "A live session the node did not start and that matches no role, leg, flight or PR. Stop it if it is a stray; a role's current session never rings here.",
            PrReviews.FixEndedKind => "A fixer ended without pushing. Fix it starts another fixer on the same findings.",
            PrReviews.FixLeftKind => "A fixer's worktree is still held by a process. The node tries again.",
            Compactor.Kind => "The node compacted a session's context, or could not and tries again. Nothing to do.",
            Digest.Kind => "What landed, what was handled and what is parked since the last digest. Read it and dismiss it.",
            NodeUpdater.Kind => "A new pilot is out, or an update was installed or failed. The card's choice installs it; otherwise nothing to do.",
            NodeLan.AskedKind => "A node on the LAN asks to fly flights here. Allow lets it run any command here until revoked; Refuse turns it away.",
            "restart" => "The node restarted at someone's word. Nothing to do.",
            NodeState.StoreCardKind => "The node's store could not write its snapshot. Every change is still journaled, so a restart loses nothing; fix the folder or the disk.",
            UsageReader.HighKind => "The 5-hour usage window is nearly spent. Workers stop when it runs out and the node resumes them after the reset; nothing to do.",
            StartGate.WeekKind => "The 7-day usage window is largely spent. Pause flights by hand if the week should last; nothing is stopped by itself.",
            RemovalQueueStart.Kind => "Folders of landed flights or closed reviews are still held open by Windows. The node removes them once they are let go; close whatever holds them.",
            JobRunner.IdleKind => "A job holds the machine's slot but uses almost no CPU. Cancel it if it is only waiting; its owner can wait with pilot leg wait.",
            JobRunner.UntoldKind => "A job ended but its owner's session refused to be told. Tell the owner yourself.",
            LockWait.QueuedKind => "A command that waited for a lock has run or was refused. Nothing to do; the title says which.",
            PackageWatch.LateKind => "A package a leg waits for is late on nuget.org. The card's choice says what to do about it.",
            PilotMd.TooLongKind => "A repo's PILOT.md is over the line limit and was not quoted into briefs. Shorten it to its rules.",
            Watchtower.Down => "The watchtower could not be started. The card says why and what to do; the node tries again by itself.",
            Watchtower.Stopped => "The watchtower was stopped by the API or hit the usage limit. The node resumes it after the reset, or the card offers a fresh one.",
            "watchtower-resumed" => "The watchtower resumed after its usage limit reset. Nothing to do.",
            "watchtower-started" => "The node started a fresh watchtower. Nothing to do.",
            "watchtower-duplicate" => "A second live watchtower was stopped so that only one runs. Nothing to do.",
            "watchtower-parked" => "The watchtower was stopped because there was nothing to watch. The next card resumes it or starts a fresh one; nothing to do.",
            Watchtower.Waits => "The watchtower waits to start until the usage window resets. The node starts it then.",
            CardKinds.WatchtowerChurning => "The watchtower keeps ending soon after it starts. The card says how often; it closes itself when it calms down.",
            CardKinds.IdeaConflict => "Your edit of an idea contradicts what GitHub holds now. Keep the merge, yours or GitHub's; nothing is written until you choose.",
            CardKinds.IdeaRepo => "A new idea fits more than one repo equally. Choose the repo to file it in; nothing is filed until you do.",
            CardKinds.SessionWaiting =>"A session waits on you for an answer. Open it and reply; it closes by itself once the session moves on.",
            CardKinds.AssistantAsk => "An assistant asks to run a command only you may run. Allow runs it as you; Deny refuses it.",
            _ => Fallback(title, choices),
        };
    }

    private static string Fallback(string title, IReadOnlyList<CardChoice> choices)
    {
        var line = title.Trim().TrimEnd('.', ':', ';');
        var labels = choices.Select(c => c.Label).Where(l => l.Length > 0).ToArray();
        var choose = labels.Length == 0 ? "Dismiss it once read" : $"Choose {string.Join(" or ", labels)}, or dismiss it";
        return $"{line}. {choose}.";
    }

    /// <summary>The thing a subject names, as a person says it: <c>PIL355/card-summary</c> for <c>flight/PIL355/card-summary</c>.</summary>
    private static string Name(string subject)
    {
        var parts = subject.Split('/');
        return parts.Length >= 2 && parts[0] == "flight" ? string.Join('/', parts.Skip(1)) : subject;
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}|\b\d{1,2}:\d{2}:\d{2}\b|^\s+at\s+\S+|\b[A-Za-z]:\\|(?:/[\w.-]+){3,}|\\[\w.-]+\\[\w.-]+", RegexOptions.Multiline)]
    private static partial Regex LooksLikeLog();
}
