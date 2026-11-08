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
    // Well above the longest sentence a raiser writes (about 200 with a long code), so none is thrown away;
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
            LegCommander.MergeFailed => $"The merge of {what} did not go through. Choose: a fresh worker can fix its last leg, the checks can be run again if they failed, or the merge can be tried once more as the PR stands.",
            "leg-failed" => $"The worker of {what} stopped. Send it round starts a fresh worker, told the whole report, that fixes it and finishes the leg; Accept as it is keeps the failure in the leg's report and the flight goes on.",
            LegCommander.PushBlockedKind => $"{what} is done and its check passes, but its branch was rebased off the pushed one. Push the rebased branch with lease overwrites origin's only if it has not moved; Merge origin's branch into the plane keeps its commits and pushes plain.",
            "pr-checks-failed" => $"The PR of {what} fails its checks and the node could not send the last leg back to fix them. The first choice starts a fresh worker on that leg, told which checks failed and to push a fix; or fix them by hand, then dismiss this.",
            LegWatch.Stopped => $"The worker of {what} was stopped by the API and the node cannot resume it by itself. Tell it to carry on, or start a fresh worker on the leg.",
            LegWatch.LimitWait => $"The worker of {what} hit the usage limit. The node resumes it by itself after the window resets; nothing to do.",
            LegWatch.Idle => $"The worker of {what} has been idle with its leg unfinished and the node's nudges did not help. Nudge it to finish or say why not, or start a fresh worker on the leg.",
            LegWatch.Waiting => $"The worker of {what} waits on you for an answer. Answering in its own session lets this worker go on with the call; starting a fresh worker stops this one and starts another on the leg from the branch.",
            LegWatch.NeverTurned => $"The worker of {what} took no first turn after two starts. Tell it to carry on if it is there, or start a fresh worker on the leg.",
            LegWatch.Failed or LegWatch.Missing => $"The worker of {what} is gone. Tell it to carry on if it is still there, or start a fresh worker on the leg.",
            LegWatch.OverBudget => $"The worker of {what} is past its budget but keeps working. Let it spend more, or start a fresh worker on the leg.",
            LegWatch.LegStale => $"{what} has gone past two go-arounds or six hours flying. The detail lists its check runs and whether each failure was the flight's. Nothing to do unless the cause is yours to fix.",
            LegWatch.ReviewStalled => $"The reviewer of {what} stalled twice. Start a fresh reviewer, or dismiss this once it moves.",
            LegWatch.ValidationStalled => $"The validator of {what} stalled. Start the leg without its validation, or remove the leg.",
            LegCommander.NoGoKind => $"The validator of {what} reports the tree is not as its brief assumes. Start the leg as the tree is, drop it, or plan the flight again.",
            LegCommander.WaitLateKind => $"{what} waits on a file that has not come in time. Look at what it waits for, or start a fresh worker on the leg.",
            LegCommander.NextStepKind => $"The node tried {what}'s next step and was refused. Do the step now to ask it again.",
            LegCommander.StartFailedKind => $"A session of {what} did not start and the node does not retry it by itself. Start it again from the card.",
            LegCommander.OverlapKind => $"{what} overlaps another flight's files. Fold it in, connect it, or plan both in parallel.",
            LegCommander.OverlapParallelKind => $"{what} shares files with an open PR no flight owns, with a flight that held a card on it too, or with another flight in a small one-way overlap, so the node planned in parallel by itself. Nothing to do; the second to land resolves the conflicts.",
            LegCommander.OverlapConnectedKind => $"{what} shares files with a flight in the air that its plan did not foresee, and that flight is moving, so the node connected it to the other itself: its next leg waits until the other lands. Nothing to do; disconnect it with --anyway to fly both in parallel instead.",
            LegCommander.OverlapLandedKind => $"{what} overlapped a flight that has landed since, so there is nothing to wait for. The node closed the overlap card itself and {what} goes on. Nothing to do.",
            StopGate.Kind => $"A session of {what} was told once to commit its files and stopped again with them still there. It can be told once more; the files stay in the plane until they are committed.",
            LegCommander.PlannedKind => choices.Count == 0
                ? $"{what} has a plan of legs, but its flight is held and cannot start yet. The detail says what it waits for; Start is offered here once that lifts."
                : $"{what} has a plan of legs. Start it to fly them, or change the plan first.",
            "landed" => $"{what} landed: its PR is merged and its legs are done. Nothing to do; dismiss it when read.",
            LegWatch.TickerSentRound => $"The ticking pass of {what} stopped with steps unticked, so the node sent the leg round once with a fresh worker told which steps remain. Nothing to do.",
            "worker-told-job" => $"The worker of {what} was idle because a job of its own had ended untold, and the node told it. Nothing to do.",
            "not-landed" => $"The PR of {what} merged on GitHub, but landing the flight was refused. The branch is in the base; the detail says what stood in the way, and the flight lands once that is dealt with.",
            "worker-nudged" => $"The worker of {what} went idle with its leg unfinished and the node nudged it. Nothing to do unless it goes idle again.",
            "worker-waiting-job" => $"The worker of {what} is idle because it waits on a job of its own, which wakes it. Nothing to do.",
            "worker-compacted" => $"The worker of {what} went idle with its context nearly full, so the node compacted it instead of nudging it. Nothing to do.",
            "worker-replaced" => $"The worker of {what} was still nearly full after its compaction, so the node sent the leg round with a fresh worker. Nothing to do.",
            "worker-resumed" => $"The worker of {what} was resumed after its usage limit reset. Nothing to do.",
            LegWatch.PlanStalled => $"The planner of {what} stalled before it handed in legs. The card's choice tells it to finish, or plans the flight again.",
            LegCommander.HoldKind => $"{what} is held until a package a sibling repo is publishing reaches nuget.org. The node starts it again by itself; nothing to do.",
            LegCommander.MoveFailedKind => $"The move of {what} to another node failed and it stays flying here. Retry move runs the move again.",
            LegCommander.MovedAwayKind => $"{what} was handed over to another node, which flies it from here. Nothing to do.",
            LegCommander.LegRemovedKind => $"A leg of {what} was removed before it flew. Nothing to do; the legs after it keep their order.",
            LegCommander.FlownElsewhereKind => $"{what} is already flown on another node, so this node did not start it. Stop it there first, or leave it where it is.",
            LegCommander.ValidatingKind => $"A validator was started to check {what} against the tree before its worker starts. Nothing to do; the node starts the leg on its answer.",
            LegCommander.TickerStartedKind => $"The worker of {what} ended with commits made and steps unticked, so the node started a ticker to tick what the commits cover and finish the leg. Nothing to do; it rings only if the ticker stops without finishing.",
            LegCommander.SiblingBehindKind => $"{what} compiles against a sibling clone that is behind origin and the node could not pull it. Pull it by hand, or let the leg run as it is.",
            LegCommander.SiblingPulledKind => $"The node pulled a sibling clone that was behind origin before it started {what}. Nothing to do.",
            LegCommander.SelfFinishedKind => $"The node finished {what} itself: its work was pushed and its check passed. Nothing to do.",
            LegCommander.RetiredKind => $"{what} was stopped on the word of the controller or the owner, so it ends retired, not failed. The flight goes on with its next leg; nothing to do.",
            LegCommander.PushDivergedKind => $"{what} was not pushed because the remote branch holds commits the plane lacks. Merge origin's branch into the plane by hand, then push again.",
            LegCommander.MergeTakenBackKind => $"The merge of {what}'s PR was taken back because a new leg was planned. The merge is offered again once the new leg is done and a fresh review passes; nothing to do.",
            "planner-ended" => $"The planner of {what} was ended once its first leg flew, so it no longer holds an Opus slot. Nothing to do; a later plan starts a fresh one.",
            "planner-started" => $"A planner was started for {what}. Nothing to do; its plan comes as a card.",
            PrMarks.RefusedKind => "GitHub refused a status, label or ready mark on a PR; the review itself stands. The node sets what is missing at its next look, so nothing to do unless it keeps refusing.",
            DesignDecisionMirror.FailedKind => "A design decision was recorded but GitHub refused its comment on the PR or the flight's issue. The decision stays in the black box (pilot decisions --kind design); post it by hand if it should be there.",
            "repo-setup" => "What repo setup made and found in a repo: the Priority field and the labels the node reads. Nothing to do; a refusal has its own card.",
            "node-label-refused" => "GitHub refused the node's label on a flight's PR or issue. Add it by hand if the flight should show which node flies it.",
            DiskSweep.Kind => "The node's disk sweep deleted job scratch (of landed flights' ended sessions, or older than the scratch-days setting) and old probe outputs, and says how much by unique bytes. Nothing to do.",
            DiskSweep.WorktreeRemovedKind => "The node's disk sweep removed a clean worktree under .claude/worktrees whose PR had merged or closed; the branch is kept. Nothing to do.",
            DiskSweep.WorktreeDirtyKind => "A worktree under .claude/worktrees has a merged or closed PR but holds changes not committed, so the disk sweep left it. Commit or discard the changes and the next sweep removes it.",
            DiskSweep.DiskLowKind => "Free space on the repos' drive is below the disk-alert-gb setting. The card names the biggest reclaimable items by unique bytes; free some space or raise the setting, and the card closes by itself once space is back.",
            "plane-left" => "A flight's plane folder could not be deleted because a process holds it. The node tries again at its next start; delete it by hand if it stays.",
            "issue-left-open" => "A landed flight's issue is still open on GitHub. Close it by hand; its work is in the merged PR.",
            FlightCommander.FoldedKind => $"{what} was folded into another flight, whose planner now plans its points. Nothing to do.",
            FlightCommander.RoutedKind => "A new flight was made on another node because this one is in maintenance. Nothing to do.",
            FlightCommander.ConnectionReleasedKind => "A flight that waited for another flight no longer does, because that flight landed. Nothing to do.",
            FlightCommander.DisconnectedKind => "A flight was disconnected from another flight it had been connected to. Nothing to do.",
            FlightCommander.ReceivedKind => $"{what} was handed over to this node, which made its plane. Nothing to do.",
            LegCommander.FollowUpPrKind => $"The commits of {what} missed the merge of its earlier PR, so a new PR carries them. Nothing to do; it is reviewed as any PR and the flight lands when it merges.",
            FlightCommander.MergedUnderLegKind => $"GitHub merged the PR of {what} before the new leg's work could join it. The new leg's work goes to a new PR once the legs are done; nothing to do.",
            FailureInvestigations.CardKind => "A test the PR does not touch failed, which is a bug with a cause to find. It is filed as an issue of priority high and a flight investigates it; later occurrences are added to that issue. Nothing to do unless you want to change its priority.",
            FailureAlerts.AlertKind => "A failure in a test its PRs do not touch came again, so the tower is on alert: the flight that investigates it looks for the cause, and every flight the failure hit is held, with no go-around, re-queue or rerun, until that flight lands. Nothing to do unless the investigation needs help.",
            FailureAlerts.RetriedKind => "A failure in a test the PR does not touch was retried once (the PR queued again, or its failed jobs run again) so it is not blocked, while its investigation looks for the cause. Nothing to do; if it fails the same way again the tower goes on alert.",
            FailureAlerts.ReleasedKind => $"The investigation of a failure landed, so {what} is released from its hold and what it was held from is done again: queued, its checks run, or its leg check run. Nothing to do.",
            "checks-rerun" =>$"Failed checks of {what}'s PR were run again once before its leg is sent round. Nothing to do unless they fail again.",
            PrChecks.ClosedLanded => $"{what}'s PR was closed without a merge and its plane held nothing of its own, so the flight landed. Nothing to do.",
            PrChecks.ClosedWithWork => $"{what}'s PR was closed without a merge but its plane holds work of its own. Reopen the PR, or open a new one for that work, then land the flight.",
            PrChecks.QueueDropped => $"GitHub took the PR of {what} out of the merge queue. The card's choice queues it again once the cause is dealt with.",
            PrChecks.MergeDropped => $"The merge of {what} did not go through. Merge it again once the cause is dealt with.",
            PrChecks.AutopilotMergeRefused => $"The node tried to merge {what}'s PR and the refusal is yours to clear (or GitHub did not answer in three tries, a minute and four minutes apart), for the reason in the title. The card says what the node tried and the one thing to do; a merge again only helps once that is done.",
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
            PrReviews.DesignKind => "The review of a PR asks a question of design, which is the author's to decide. No fixer started; an option or Answer in your own words posts the answer on the PR and starts it.",
            PrReviews.DesignSettledKind => "A review's design question stopped ringing because its PR merged or closed, a follow-up flight landed, or it was answered (an answer starts the fixer or sends the flight round, and the notice says which). Nothing to do.",
            PrReviews.FixStartedKind => "A fixer was started on a PR's branch to make the changes its review asked for. Nothing to do.",
            PrReviews.FixOfferKind => "A review asked for changes on someone else's PR. Fix it starts a session on the PR's branch that makes them.",
            PrReviews.FixFailedKind => "The fixer for a PR could not start, or its push could not be reviewed. The card's choice starts it again.",
            PrReviews.FixCappedKind => "A PR still asks for changes after three rounds of fixes, so no fourth fixer starts. What is left is yours to decide.",
            LegCommander.LaidKind => $"{what}'s branch merged cleanly with its base but would not rebase onto it, so the node laid it on the base as one commit. Its review carries over; nothing to do.",
            LegCommander.ShapedKind => $"{what}'s branch held a merge or did not replay as it was, and its base merges by rebase, so the node rebased it onto the base and pushed it. Its review carries over; nothing to do.",
            LegCommander.LayFailedKind => $"The node could not lay {what} on its base as one commit, and the merge queue needs that. The detail says why; fix it by hand, then dismiss this.",
            LegCommander.DropUnexplainedKind => $"The merge queue dropped the PR of {what} or would not take its laid head, and the node found no cause. It is not queued again at this head; push a changed head or look at the PR page.",
            PrReviews.NoFixerKind => "A review asks for changes or puts a question, and the node starts no fixer on that PR. The detail says why; what is left is yours to decide.",
            PrReviews.WaitingKind => "A review or fixer of a PR waits for a free slot under the node's admission rules. The node starts it by itself; nothing to do unless the start did not go.",
            PrReviews.PushedUnreviewedKind => "A fixer pushed to a PR but the review of that push could not start. The push stands; Start the review of the fixer's push runs it.",
            AdmissionGate.CycleKind => "Two flights each waited on the other under the admission rules, so the node admitted one to break the wait. Nothing to do.",
            LegCommander.SpendAlertKind => "A leg has spent a large share of the 7-day window before its next round. The round still starts; stop the flight by hand if it should not.",
            LegCommander.ConflictDecisionKind => $"The workers sent to merge {what}'s PR with its base, on Sonnet, then Opus, then Opus at xhigh, all failed, and the last found that the two sides change the same behaviour in opposite ways. The detail names the files, both behaviours and what keeping each costs; each choice sends the leg round told which side to keep.",
            LegCommander.GoAroundCapKind => "A leg has gone around as many times as the cap allows, whatever the reasons, and the node and the watchtower send it round no more. Go around sends one more; Accept takes the leg as it stands.",
            LegCommander.ReviewNotesKind => $"The review of {what} passed with notes that do not block the merge. The merge waits while a review-notes leg makes them (its push ends the hold and a fresh review gates the merge), or the watchtower works on them in the plane or clears the flight; if none takes them in a few minutes the flight merges with them. Nothing for you to do.",
            LegCommander.HumanVerificationKind => $"The review of {what} noted checks only a person can make, and the node filed each as an issue in the repo's human-verification milestone. Check each by hand and close its issue; dismiss this once read.",
            LegCommander.HumanVerificationRefusedKind => $"GitHub refused some of {what}'s human checks, so they are not filed; the verdict stands. The detail names them; file them by hand.",
            LegCommander.HoldingClearedKind => "A flight held in a holding pattern was cleared to land, by the watchtower or the person, or released by the node because the watchtower did not take the hold: its merge is no longer held, and autopilot merges it as for any passed review.",
            LegCommander.HoldReleasedKind => "A flight held in a holding pattern had no watchtower running to make its review's not blocking notes, so the node wrote them on the PR as one comment and released the hold: autopilot merges it as for any passed review. Nothing to do.",
            LegCommander.PrReadyRestoredKind => "A flight's review had passed but no merge card was open (the node restarted between the verdict and the card), so the node raised its PR-ready card again. Nothing to do.",
            LegCommander.HoldingTimeoutKind => $"{what} has circled in a holding pattern for 30 minutes after the watchtower took it, without clearing it, or the watchtower is not live and the node could do nothing. Merge as it is lands it at the reviewed head; Wait keeps it holding and asks again later.",
            LegCommander.ReviewLoopKind => "The review of a flight has asked for changes in three rounds. Pass with notes ends the loop; Allow one more round lets a fourth start.",
            "review-kept" => "A flight's base was merged into its branch and its own diff is unchanged, so its passed review stands and no reviewer started. Nothing to do.",
            LegWatch.StraySession => "A live session that should not be running: one past its purpose (its leg is done or was redone, or its flight landed), or one the node did not start and no role claims. Stop it ends the session; the card closes by itself when the session ends. A role's current session never rings here.",
            PrReviews.FixEndedKind => "A fixer ended without pushing. Fix it starts another fixer on the same findings.",
            PrReviews.FixFollowUpKind => "A PR merged while its fixer worked, so the fixer's commits went to a new branch and a follow-up PR, which is reviewed as any PR. Nothing to do.",
            PrReviews.FixFollowUpFailedKind => "A PR merged while its fixer worked and the fixer's commits could not be pushed or opened as a follow-up PR. The detail names the commit and the local branch that holds it; push it by hand.",
            PrReviews.FixLeftKind =>"A fixer's worktree is still held by a process. The node tries again.",
            Compactor.Kind => "The node compacted a session's context, or could not and tries again. Nothing to do.",
            Digest.Kind => "What landed, what was handled and what is parked since the last digest. Read it and dismiss it.",
            StaleWatch.ActedKind => $"The node acted on {what} at half a stale bound (told its worker of an ended job, nudged it, or raised its priority). Nothing to do.",
            NodeUpdater.Kind => "A new pilot is out, or an update was installed or failed. The card's choice installs it; otherwise nothing to do.",
            NodeLan.AskedKind => "A node on the LAN asks to fly flights here. Allow lets it run any command here until revoked; Refuse turns it away.",
            "restart" => "The node restarted at someone's word. Nothing to do.",
            NodeState.StoreCardKind => "The node's store could not write its snapshot. Every change is still journaled, so a restart loses nothing; fix the folder or the disk.",
            Pilot.Node.Sessions.SessionMessenger.StopUnconfirmedKind => "A session was told to stop and its process may still run. The card's choice ends it again; the title says which session.",
            UsageReader.HighKind => "The 5-hour usage window is nearly spent. Workers stop when it runs out and the node resumes them after the reset; nothing to do.",
            StartGate.WeekKind => "The 7-day usage window is largely spent. Pause flights by hand if the week should last; nothing is stopped by itself.",
            RemovalQueueStart.Kind => "Folders of landed flights or closed reviews are still held open by Windows. The node removes them once they are let go; close whatever holds them.",
            JobRunner.IdleKind => "A job holds the machine's slot but uses almost no CPU. Cancel it if it is only waiting; its owner can wait with pilot leg wait.",
            BenchRun.CardKind => "A benchmark ended and its tables are on the card, per ref. Read them, then dismiss it; nothing is waiting on you.",
            JobRunner.BenchBusyKind => "A queued benchmark has waited over half an hour for a quiet CPU. The card names what holds it; cancel the job or let it keep waiting.",
            JobRunner.UntoldKind => "A job ended but its owner's session refused to be told. Tell the owner yourself.",
            JobRunner.UnansweredKind => "A job's owner was told twice how it ended and sat idle both times. Look at its session; it may have lost the message.",
            LockWait.QueuedKind => "A command that waited for a lock has run or was refused. Nothing to do; the title says which.",
            PackageWatch.LateKind => "A package a leg waits for is late on nuget.org. The card's choice says what to do about it.",
            PilotMd.TooLongKind => "A repo's PILOT.md is over the line limit and was not quoted into briefs. Shorten it to its rules.",
            Watchtower.Down => "The watchtower could not be started. The card says why and what to do; the node tries again by itself.",
            Watchtower.Stopped => "The watchtower was stopped by the API or hit the usage limit. The node resumes it after the reset, or the card offers a fresh one.",
            "watchtower-resumed" => "The watchtower resumed after its usage limit reset. Nothing to do.",
            "watchtower-started" => "The node started a fresh watchtower. Nothing to do.",
            "watchtower-duplicate" => "A second live watchtower was stopped so that only one runs. Nothing to do.",
            "watchtower-parked" => "The watchtower was stopped because no flight was in the air and no UI was connected. A flight or a UI connecting resumes it or starts a fresh one; nothing to do.",
            Watchtower.Waits => "The watchtower waits to start until the usage window resets. The node starts it then.",
            CardKinds.WatchtowerChurning => "The watchtower keeps ending soon after it starts. The card says how often; it closes itself when it calms down.",
            CardKinds.IdeaConflict => "Your edit of an idea contradicts what GitHub holds now. Keep the merge, yours or GitHub's; nothing is written until you choose.",
            CardKinds.IdeaRepo => "A new idea fits more than one repo equally. Choose the repo to file it in; nothing is filed until you do.",
            CardKinds.SessionWaiting =>"A session waits on you for an answer. Open it and reply; it closes by itself once the session moves on.",
            Watchtower.AskKind => "The watchtower waits on you for leave to run a tool, or for the answer to a question. The buttons answer it, as the chat's panel does; it closes once answered.",
            CardKinds.AssistantAsk => "An assistant asks to run a command only you may run. Allow runs it as you; Deny refuses it.",
            _ => Fallback(title, choices),
        };
    }

    /// <summary>
    /// The sentence of a landing refused after its PR merged: the refusal's own reason (<paramref name="why"/>, the plane's folder
    /// said as "its plane", cut to fit the panel's 240 characters), not "the title says why". The choice is Land it, not repeated here.
    /// </summary>
    public static string NotLanded(string code, int number, string why, string worktree)
    {
        // The summary already says the PR merged, so the refusal's own "TIA1 merged at 1a2b3c4d, but" opening is left out.
        var reason = LegCommander.OneLine(MergedOpening().Replace(why.Replace(worktree, "its plane", StringComparison.OrdinalIgnoreCase), ""));
        return $"The PR #{number} of {code} merged on GitHub, but the flight did not land: {reason}. Its branch is in the base; the flight lands once that is dealt with.";
    }

    /// <summary>
    /// The sentence of a merged flight whose own commits missed the merge: which one (the first by subject), and that a PR with them
    /// lands the flight. Never main's commits or merges of main, which are not the flight's work.
    /// </summary>
    public static string LeftOut(string code, int number, IReadOnlyList<string> commits)
    {
        var first = commits.Count > 0 ? commits[0] : "";
        var more = commits.Count > 1 ? $" and {commits.Count - 1} more" : "";
        var text = $"PR #{number} merged without {code}'s last work: {first}{more}. A PR with {(commits.Count == 1 ? "it" : "them")} brings it in, and the flight lands when that merges.";
        return text.Length <= 240 ? text : text[..237] + "...";
    }

    /// <summary>
    /// The sentence of a review that passed with notes that do not block: what they are (how many, and the first in its own words)
    /// and that nothing is asked of the person. No word of how the node sends a leg round.
    /// </summary>
    public static string ReviewNotes(string code, IReadOnlyList<string> notes, string? fixerLeg = null)
    {
        var first = notes.Select(n => n.Trim().TrimStart('-', '*', ' ')).FirstOrDefault(n => n.Length > 0) ?? "";
        first = first.ReplaceLineEndings(" ");
        first = first.Length > 40 ? first[..37].TrimEnd() + "..." : first;
        var example = first.Length > 0 ? $", for example: {first}" : "";
        var head = $"The review of {code} passed with {notes.Count} small {(notes.Count == 1 ? "note" : "notes")} that {(notes.Count == 1 ? "does" : "do")} not block the merge{example}.";
        return fixerLeg is not null
            ? $"{head} The fixer leg {fixerLeg} took them; nothing for you to do."
            : $"{head} The watchtower hands them to a fixer leg or clears the flight; else it merges with them. Nothing for you to do.";
    }

    /// <summary>The sentence of a review-notes card whose hold the watchtower did not take: the flight merges with the notes, which stay for a follow-up.</summary>
    public static string ReviewNotesLeft(string code) =>
        $"The review of {code} passed with notes that do not block the merge. The watchtower did not take them in time, so the flight merges at the reviewed head; the notes stay here for a follow-up.";

    /// <summary>The sentence of the card that names the issues filed for a flight's human checks: what they are and that the controller checks each by hand and closes it.</summary>
    public static string HumanVerification(string code, int count) =>
        $"The review of {code} noted {count} {(count == 1 ? "check" : "checks")} only a person can make, filed as {(count == 1 ? "an issue" : "issues")} in the repo's human-verification milestone. Check each by hand and close its issue; dismiss this once read.";

    /// <summary>The sentence of the card for human checks GitHub refused to take: the verdict stands and the rest is by hand.</summary>
    public static string HumanVerificationRefused(string code, int count) =>
        $"GitHub refused {count} of {code}'s human {(count == 1 ? "check" : "checks")}, so {(count == 1 ? "it is" : "they are")} not filed; the review's verdict stands. The detail names {(count == 1 ? "it" : "them")}; file {(count == 1 ? "it" : "them")} by hand.";

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

    [GeneratedRegex(@"^\S+ merged at [0-9a-f]+, but ", RegexOptions.IgnoreCase)]
    private static partial Regex MergedOpening();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}|\b\d{1,2}:\d{2}:\d{2}\b|^\s+at\s+\S+|\b[A-Za-z]:\\|(?:/[\w.-]+){3,}|\\[\w.-]+\\[\w.-]+", RegexOptions.Multiline)]
    private static partial Regex LooksLikeLog();
}
