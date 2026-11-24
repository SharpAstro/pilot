using Pilot.Contracts;

namespace Pilot.Node.Tests;

/// <summary>
/// One kind of card the node raises: its kind word, the bins it is raised into (<c>Ring</c>, <c>ForYou</c>, <c>Handled</c>, joined by
/// <c>|</c>), the Pilot.Node files that raise it, and the test that pins its title and choices (<c>Class.Method</c> in this project,
/// which must exist and name the kind). A kind nothing runs pins is pinned by <see cref="CardKindCatalog.SourcePins"/> instead, in
/// <c>CardKindTests.SourcePinned</c>. A kind raised into the Ring bin also names the <c>Fact</c> it is checked against before it rings (what the
/// node read, from GitHub, a row, the session or the plane, that says the person must act) and the <c>Choices</c>: the <c>Verbs</c> its card offers,
/// each one the node accepts in the state the card is raised in (#799 rule 5: a ring is for what only the person can settle, with something to press).
/// <c>NodePressed</c> marks a kind the watchtower may press on the node's rules alone: <c>NodePress</c> presses it itself, so no press of it is a CLI command a classifier can refuse.
/// A kind whose choices are handed to the raiser by its callers (a start that failed offers the start again) lists those files as <c>ChoiceFiles</c>.
/// </summary>
internal sealed record CardKindEntry(string Kind, string Bins, string[] Files, string Pin, string? Fact = null, string[]? Choices = null, string[]? ChoiceFiles = null, bool NodePressed = false);

/// <summary>
/// Every kind of card the node raises (PIL474). <c>CardKindTests</c> reads the <c>RaiseCard</c> calls under Pilot.Node and fails when one
/// is not here, when a kind here is no longer raised, or when a title or a button says nothing specific; a new card is added here
/// with its pinning test, which is the point.
/// </summary>
internal static class CardKindCatalog
{
    public const string SourcePinned = "CardKindTests.SourcePinned";

    private const string LegAdvance = "Flights/LegCommander.Advance.cs";
    private const string LegPlan = "Flights/LegCommander.Plan.cs";
    private const string LegStart = "Flights/LegCommander.Start.cs";
    private const string LegFinish = "Flights/LegCommander.Finish.cs";
    private const string LegOpenPr = "Flights/LegCommander.OpenPr.cs";
    private const string LegPlanners = "Flights/LegCommander.Planners.cs";
    private const string LegReview = "Flights/LegCommander.Review.cs";
    private const string LegReviewHold = "Flights/LegCommander.ReviewHold.cs";
    private const string LegHoldRelease = "Flights/LegCommander.HoldRelease.cs";
    private const string HumanVerification = "Flights/LegCommander.HumanVerification.cs";
    private const string LegMerge = "Flights/LegCommander.Merge.cs";
    private const string Quiet = "Flights/LegCommander.QuietWaits.cs";
    private const string LegQueuedPush = "Flights/LegCommander.QueuedPush.cs";
    private const string Lay = "Flights/LegCommander.Lay.cs";
    private const string Shape = "Flights/LegCommander.Shape.cs";
    private const string Watch = "Flights/LegWatch.cs";
    private const string FlightCommanderConnect = "Flights/FlightCommander.Connect.cs";
    private const string FlightCommanderCreate = "Flights/FlightCommander.Create.cs";
    private const string FlightCommanderLand = "Flights/FlightCommander.Land.cs";
    private const string FlightCommanderReceive = "Flights/FlightCommander.Receive.cs";
    private const string FlightCommanderAdopt = "Flights/FlightCommander.Adopt.cs";
    private const string FlightCommanderRoute = "Flights/FlightCommander.Route.cs";
    private const string Move = "Flights/LegCommander.Move.cs";
    private const string Checks = "Flights/PrChecks.cs";
    private const string Reviews = "Flights/PrReviews.cs";
    private const string PrReviewsFixer = "Flights/PrReviews.Fixer.cs";
    private const string PrReviewsFixerRounds = "Flights/PrReviews.FixerRounds.cs";
    private const string PrReviewsFixerHold = "Flights/PrReviews.FixerHold.cs";
    private const string PrReviewsQueue = "Flights/PrReviews.Queue.cs";
    private const string PrReviewsLand = "Flights/PrReviews.Land.cs";
    private const string PrReviewsStart = "Flights/PrReviews.Start.cs";
    private const string PrReviewsStatus = "Flights/PrReviews.Status.cs";
    private const string PrReviewsSweep = "Flights/PrReviews.Sweep.cs";
    private const string PrReviewsVerdicts = "Flights/PrReviews.Verdicts.cs";
    private const string Recheck = "Flights/PrRecheck.cs";
    private const string Tower = "Flights/Watchtower.cs";
    private const string TowerStream = "Flights/Watchtower.Stream.cs";
    private const string Asks = "Flights/Watchtower.Asks.cs";
    private const string Refused = "Flights/Watchtower.Refused.cs";

    public static readonly CardKindEntry[] All =
    [
        // Workers, legs and planners.
        new("worker-stopped", "Ring", [Watch], "LegWatchTests.AStopWithNoResetTimeRingsAtOnce", "the worker's session row is stopped by the API (no reset time to resume at), the leg is flying and its flight has not landed", [nameof(Verbs.Say), nameof(Verbs.GoAround)]),
        new("flight-on-hold", "Handled", ["Flights/LegCommander.OnHold.cs", "Flights/LabelHold.cs", "Flights/RepoWatch.cs"], "FlightHoldTests.HoldNeedsWhyRecordsWhoAndWhenAndARepeatUpdatesTheWhy"),
        new("wait-followed", "Handled", ["Flights/FlightCommander.Follow.cs"], "ConnectTests.AFlightHeldOnALocalFlightThatMovesToAPeerWaitsForItThereWithNoLocalConnection_818"),
        new("merged-hold", "Ring", ["Flights/FlightCommander.MergedHold.cs"], "ConnectTests.AHoldOverTwoHoursOnAFlightWhosePrMergedRingsOneCardAcrossTwoSweeps_818", "GitHub says the flight a held flight waits for has its PR merged or its issue closed, the hold is over two hours old, and the sweep left it held", [nameof(Verbs.Disconnect)]),
        new("worker-limit-wait", "Handled", [Watch], "LegCardTests.AUsageLimitStopTheNodeResumesItselfIsAHandledLineAndNoCard"),
        new("worker-idle", "Ring", [Watch], "LegWatchTests.ANudgeTheNodeCannotDeliverRings", "the worker's session row is idle with its leg unfinished, nothing of its own (a job, the node's recheck) is what it waits on, and its flight has not landed", [nameof(Verbs.Say), nameof(Verbs.GoAround)]),
        new("worker-waiting", "Ring", [Watch], "LegWatchTests.AWaitingWorkerRingsWithItsQuestionAndTheCardClosesWhenItWorksAgain", "the worker's last message asks the person, its leg is not rechecking, and its flight has not landed", [nameof(Verbs.GoAround)]),
        new("worker-failed", "Ring", [Watch], "LegWatchTests.AFailedWorkerAndOneThatNeverShowedUpRing", "the worker's session row is failed (or its API said why), the leg is flying and its flight has not landed", [nameof(Verbs.Say), nameof(Verbs.GoAround)]),
        new("worker-missing", "Ring", [Watch], "LegWatchTests.AFailedWorkerAndOneThatNeverShowedUpRing", "the tracker's listing of sessions never showed the worker the node started", [nameof(Verbs.GoAround)]),
        new("worker-never-turned", "Ring|Handled", [Watch], "LegWatchTests.AWorkerThatNeverTakesATurnIsStoppedAndStartedOnceThenRungWhenTheSecondShowsNothing", "the worker showed no first turn after two starts, read from its session row", [nameof(Verbs.GoAround)]),
        new("worker-nudged", "Handled", [Watch], "LegWaitTests.AnIdleWorkerWhoseLongWaitEndsIsWokenOnceAndNotNudgedOnTopOfIt"),
        new("ticker-sent-round", "Handled", [Watch], "LegTickingPassTests.A_ticker_that_ended_with_step_two_unticked_sends_the_leg_round_naming_the_step_and_rings_nothing"),
        new("worker-told-job", "Handled", [Watch], "LegWatchTests.AnIdleWorkerWithAnEndedUntoldJobIsToldItAndNeitherNudgedNorRung"),
        new("untouched-findings", "Handled", ["Flights/LegCommander.UntouchedFindings.cs"], "UntouchedFindingsTests.TheSecondFinishAtTheSameHeadWithAReportGoesThroughWithAHandledCard"),
        new("worker-compacted","Handled", [Watch], "LegWatchTests.AnIdleWorkerAtNinetyFivePercentIsCompactedAndNotNudged"),
        new("worker-waiting-job", "Handled", [Watch], "LegWatchTests.AnIdleWorkerWaitingOnAJobOfItsOwnRaisesNoRingAndOneHandledLineSayingSo"),
        new("worker-replaced", "Handled", [Watch], "LegWatchTests.AnIdleWorkerStillFullAfterItsCompactionIsReplacedByAGoAroundAndAReplacementThatFillsAgainRings"),
        new("worker-resumed", "Handled", [Watch], "LegWatchTests.AWorkerTheUsageLimitStoppedIsResumedOnlyOnceItsLimitHasReset"),
        new("over-budget", "Ring", [Watch], "SignalTests.AWorkerPastItsBudgetRingsUntilTheBudgetIsRaised", "the worker's weighted spend in its session row is past the leg's budget", [nameof(Verbs.ExtendBudget), nameof(Verbs.GoAround)]),
        new("leg-stale", "Handled", [Watch], "StaleLegTests.ALegAtGoAroundThreeRaisesOneStaleCardWithATableOfItsCheckJobs"),
        new("review-stalled", "Ring|Handled", [Watch], "LegWatchTests.AReviewerThatStopsWithoutItsVerdictIsToldThenReviewedAfreshAndOnlyThenRings", "the reviewer's session ended with no verdict recorded on the flight, after one telling and one fresh review", [nameof(Verbs.StartReview)]),
        new("validation-stalled", "Ring", [Watch], "LegWatchTests.AValidatorThatStopsWithoutItsAnswerRingsWithGoAnywayAndRemove", "the validator's session ended with no answer recorded on the leg", [nameof(Verbs.StartLeg), nameof(Verbs.RemoveLeg)]),
        new("plan-stalled", "Ring|Handled", [Watch], "LegWatchTests.AnIdlePlannerWithNoLegsIsToldToHandThemInThenRungNeverReplaced", "the planner's session is idle and the flight still has no legs, after one telling", [nameof(Verbs.PlanFlight)]),
        new("session-waiting", "Ring|ForYou", [Watch], "WaitingCardTests.TheCardClosesWhenTheSessionWorksAgain", "the session's row is waiting on its person and no leg's worker is it", [nameof(Verbs.Say)]),
        new("leg-wait-late", "Ring", [Watch], "LegWaitTests.AFileWaitPastItsUntilRingsOnceAndKeepsWaiting", "the file or time the leg's worker waits for has not come by its limit, read from the leg's wait", [nameof(Verbs.GoAround)]),
        new("leg-failed", "Ring", [LegFinish], "PushBlockedTests.AFailedLegWhoseCheckFailsGetsTheOrdinaryCardNotThePushBlockedOne", "the leg's finish was failed and its check, rerun by the node, still fails", [nameof(Verbs.GoAround)]),
        new("push-blocked", "Ring", [LegFinish], "PushBlockedTests.ARebasedBranchWithAPassingCheckRaisesAPushBlockedCardNamingTheForcePush", "the plane's head is on a rebased branch the remote holds differently, and the check passes", [nameof(Verbs.FinishLeg), nameof(Verbs.GoAround)]),
        new("push-diverged", "ForYou", [LegOpenPr], "PushTests.A_conflicting_remote_commit_is_refused_with_both_heads_and_the_remote_is_left_as_it_was"),
        new("leg-self-finished", "Handled", [LegFinish], "LegSelfFinishTests.A_failed_leg_with_its_work_pushed_and_its_check_passing_is_finished_by_the_node"),
        new("leg-retired", "Handled", [LegFinish], "FalseRingTests.ALegRetiredOnTheControllersWordEndsRetiredWithNoCardAndNoChoices_c3623"),
        new("leg-no-go", "Ring", [LegStart], "CardSummaryRaiserTests.TheNoGoSummaryNamesTheChoicesTheCardOffers", "the validator's report says the tree is not as the leg's brief assumes", [nameof(Verbs.RemoveLeg), nameof(Verbs.PlanFlight)]),
        new("leg-validating", "Handled", [LegStart], "ValidatorTests.ALaterLegStartsAValidatorOnTheWorkerModelAtLowEffortAndNoWorker"),
        new("leg-cap", "ForYou|Handled", [LegPlan], "PlannedLegCapTests.TheSecondExtraLegRaisesAForYouCardNamingTheFlightAndTheCounts"),
        new("leg-ticker-started", "Handled", [LegStart], "LegTickingPassTests.An_ended_worker_with_commits_and_nothing_ticked_gets_a_ticker_that_finishes_the_leg_with_no_ring"),
        new("leg-removed", "Handled", [LegPlan], "LegTests.APlannedLegIsRemovedTheLegsAfterItKeepTheirOrderAndAHandledCardSaysWhy"),
        new("leg-holding", "Handled", ["Flights/LegCommander.Hold.cs"], "LegHoldTests.AWorkerFailingOnAPackageASiblingIsPublishingHoldsItsLegWithNoRingAndItsWorkerStopped"),
        new("start-failed", "Ring|Handled", ["Flights/LegCommander.Launch.cs"], "LockFreeStartTests.AStartThatFailsAfterTheAnswerClearsItsReservationAndSaysSoOnACard", "the start the node tried was refused or never answered, and nothing retries it by itself", [nameof(Verbs.StartLeg), nameof(Verbs.GoAround), nameof(Verbs.PlanFlight)], ChoiceFiles: [LegStart, "Flights/LegCommander.GoAround.cs", LegPlanners, LegReview]),
        new("next-step", "Ring|Handled", [LegAdvance, LegStart, LegPlanners, LegReview, FlightCommanderCreate], "FlightMoveTests.AnAdvanceThatThrowsAfterTheReceiveIsACardNotAFailedReceive", "the node's own next step after a receive was refused, on a flight that has not landed", [nameof(Verbs.GoAround)]),
        new("sibling-behind", "ForYou", [LegStart], "LegTests.ALegStartedWithASiblingBehindItsOriginRaisesOneCardNamingItAndThePullAndStillStarts"),
        new("sibling-pulled", "Handled", [LegStart], "LegTests.AStaleSiblingWhoseOriginWentUnreachableAfterTheCountIsStillPulled"),
        new("reboot", "Handled", ["RebootPrep.cs"], "RebootPrepTests.ARequestTurnsMaintenanceOnAndTellsEachLiveNodeSessionOnce"),
        new("reboot-ready", "ForYou", ["RebootPrep.cs"], "RebootPrepTests.AFileArrivingEndsItsSessionAndTheReadyCardComesOnlyWhenAllAreIn"),
        new("planner-started", "Handled", [LegPlanners], "LockFreeStartTests.APlanFlightAnswersStartedAtOnceAndItsCardComesWhenThePlannerIsUp"),
        new("planned", "ForYou|Handled", [LegPlanners, "NodeState.Flights.cs"], "LegTests.APlannerIsStartedInThePlaneAndItsPlannedOffersTheFirstLeg"),
        new("overlap", "Ring", ["Flights/LegCommander.Overlap.cs", "Flights/LegCommander.LegEnd.cs"], "OverlapTests.TheRecordedAnswerSurvivesAReplayOfTheJournal", "the flight's files overlap another flight's and the controller has not answered", [nameof(Verbs.Fold), nameof(Verbs.ConnectOverlap), nameof(Verbs.Separate)], NodePressed: true),
        new("overlap-parallel", "Handled", ["Flights/LegCommander.LegEnd.cs"], "LegEndOverlapTests.AnUnforeseenOverlapWithAFlightlessOpenPrIsAnsweredParallelByTheNodeWithAHandledNoteAndNoRing"),
        new("overlap-connected", "Handled", ["Flights/LegCommander.LegEnd.cs"], "LegEndOverlapTests.AnUnforeseenOverlapWithALocalFlightIsConnectedByTheNodeWithAHandledNoteNoRingAndTheNextLegHeld"),
        new("overlap-landed", "Handled", ["Flights/LegCommander.LegEnd.cs"], "LegEndOverlapTests.APendingRowWhoseLocalOtherHasLandedIsClosedByTheSweepWithAHandledLineAndTheNextLegStarts"),
        new("flown-elsewhere", "Handled", [LegPlan], "OneNodePerFlightTests.APlanOfAFlightWhoseBranchAPeerFliesIsRefusedWithARingNamingThePeer"),
        new("pilot-md-too-long", "Ring", [LegAdvance], "PilotMdTests.APilotMdOverTheLimitIsNotQuotedAndRaisesOneCardNamingTheRepoTheFlightAndTheLineCount", "the repo's PILOT.md line count, read from the file, is over the limit", [nameof(Verbs.PlanFlight)]),
        new("stopped-uncommitted", "Ring", ["Flights/StopGate.cs"], "StopGateTests.TheHookPrintsTheNodesDecisionAndNothingOnceLetThrough", "git status of the plane lists files not committed after the worker was told once", [nameof(Verbs.Say)]),
        new("package-late", "Ring", ["Flights/PackageWatch.cs"], "PackageWaitTests.APublishRunThatFailedRaisesTheCardAtOnce", "the package version the flight waits for is not on nuget.org and the producing run failed or is past its bound", [nameof(Verbs.FlyAnyway)]),
        new("admission-cycle", "Handled", ["Flights/AdmissionGate.cs"], "LegAdmissionTests.AFlightAdmittedToBreakACycleRaisesOneCardNamingBothFlights"),
        new("spend-alert", "Handled", [LegStart], "SpendAlertTests.ALegPastTheAlertShareRingsWithItsTokensAndShareBeforeItsNextRoundOncePerRound"),
        new("go-around-cap", "Ring", ["Flights/LegCommander.GoAround.cs"], "GoAroundCapTests.TheSixthGoAroundOfALegIsRefusedAndRaisesTheCardWithEveryRoundsReason", "the leg's go-around count on its row has reached the cap", [nameof(Verbs.GoAround)]),
        new("conflict-decision", "Ring", ["Flights/LegCommander.ChecksAndConflicts.cs"], "ConflictEscalationTests.TheLastRungsDecisionReportRingsOneCardNamingTheFilesTheBehavioursAndWhatEachChoiceKeeps", "the last of three conflict workers failed with a report saying the two sides change the same behaviour in opposite ways (the fixed shape), and the leg's row holds it", [nameof(Verbs.GoAround)]),
        new("review-kept", "Handled", [LegReview], "ReReviewTests.AHeadThatOnlyMergedTheBaseKeepsThePassWithNoReviewerStarted"),
        new("review-notes", "ForYou|Handled", [LegReview], "ReviewModelTests.APassWithNotesPlansTheReviewNotesLegAndSendsNoFindingsRound"),
        new("human-verification", "ForYou", [HumanVerification], "HumanVerificationFilingTests.AFlaggedPassFilesEachItemInTheMilestoneAndRaisesOneCard"),
        new("human-verification-refused", "Handled", [HumanVerification], "HumanVerificationFilingTests.ARefusedGhCallKeepsTheVerdictAndRaisesAHandledCard"),
        new("holding-cleared", "Handled", [LegReviewHold], "HoldingPatternTests.ClearByTheWatchtowerLetsTheNodeMergeIt"),
        new("pr-waits-for-legs", "Handled", [Quiet], "MergeFixtureCardTests.AReadyCardOfAFlightWithALegStillToFlyIsAHandledLineAndTheNodeMergesOnceTheLegLands"),
        new("merge-armed-by-node", "Handled", [Quiet], "MergeArmTests.ANodeArmedMergeWaitingOnItsChecksIsAHandledLineAndFailingChecksRaiseTheCard"),
        new("pr-ready-restored", "Handled", [Quiet], "PassedWithoutCardTests.AFlightPassedWithNoCardGetsOneAndTheNodeMergesIt"),
        new("holding-timeout", "Ring", [Checks], "HoldingPatternTests.PastTheBoundTheTimeoutCardRingsAndMergeAsItIsMerges", "the flight's holding pattern on its row is past its timeout and its PR is still open on GitHub", [nameof(Verbs.Merge), nameof(Verbs.Land)]),
        new("owner-decision", "ForYou", ["Flights/LegCommander.FollowUp.cs", "Flights/PrReviews.FollowUp.cs"], "DeferredPointTests.AnOwnerDecisionPointRaisesACardWithTheQuestionAndCreatesNoIssue"),
        new("review-notes-cap", "Ring", [LegReview], "ReviewNotesCapTests.APassWithNotesPastTheCapRingsTheOwnerAndPlansNoLeg", "the flight's re-reviews raised new notes after each of its review-notes legs, up to the cap, so the node plans no more", [nameof(Verbs.Clear), nameof(Verbs.Dismiss)]),
        new("notes-unmade", "Ring", [LegReviewHold], "NotesUnmadeTests.ANodeThatDoesNotAdvanceRingsTheOwnerAtOnceAndKeepsTheHold", "the flight's passed review left notes with no end (read from its rounds and the review-notes legs' reports), and the node could plan no review-notes leg (or has none left under the cap)", [nameof(Verbs.Clear), nameof(Verbs.Dismiss)]),
        new("review-loop", "Ring", [LegReview], "ReviewLoopTests.TheThirdRoundWithChangesRaisesAWatchtowerCardWithEachRoundsFindings", "the review's stored rounds show a third answer still asking for changes, and passing it over the findings is an override that is the person's", [nameof(Verbs.Review), nameof(Verbs.AllowRound)]),
        new("stray-session", "Ring", ["Flights/LegWatch.cs"], "WindDownTests.TheSweepRingsOnceForALiveSessionOfALandedFlightAndOnceForOneTheLedgerDoesNotKnow", "a live session's flight has landed (read from the flight row), or the session ledger does not know it", [nameof(Verbs.Stop)]),
        new("duplicate-worker-ended", "Handled", ["Flights/LegWatch.cs"], "StraySessionTests.AWorkerThatIsNotTheLegsCurrentOneIsEndedByTheNodeWithNoRing"),

        // Flights: landing, moving, PRs.
        new("review-fix-round", "Handled", ["Flights/LegCommander.ReviewChanges.cs"], "NodeFixRoundTests.AChangesVerdictSendsTheLastLegRoundAsTheNodeWithTheFindingsAsItsBrief"),
        new("review-changes", "Ring", ["Flights/LegCommander.ReviewChanges.cs"], "CardSummaryRaiserTests.AReviewThatAsksForChangesSaysWhichLegsFindingsAreFixedNotRedone", "the reviewer's verdict at the PR's current head asks for changes", [nameof(Verbs.GoAround)]),
        new("rebased-on-base", "Handled", [Shape], "BranchShapeTests.ABranchHoldingAMergeOfItsBaseIsPushedLinearOnARebaseBaseWithItsReviewCarried"),
        new("laid-on-base", "Handled", [Shape], "RebaseLayFlightTests.ABranchThatMergesCleanlyButDoesNotRebaseIsLaidOnItsBaseAsOneCommitAndQueuedOnce"),
        new("rebase-only", "Handled", ["Flights/LegCommander.RebaseOnly.cs"], "RebaseBlockedTests.WithALegFlyingNothingIsPushedAndTheFlightAndAHandledCardSayItLandsSquashedUntilTheLegEndsThenItReshapes"),
        new("lay-failed", "ForYou", [Shape], "RebaseLayFlightTests.AMergeThatIsNotCleanPushesNothingAndRingsOnce"),
        new("drop-unexplained", "Handled|Ring", [Lay], "RebaseLayFlightTests.ADroppedPrWhoseCauseIsNotFoundRingsOnceAndIsNeverQueuedAgain", "a PR the base dropped has no cause the node can find on GitHub", [nameof(Verbs.Merge)]),
        new("pr-ready", "Ring", [LegReview, Lay, LegMerge], "PrFlightReviewTests.AnApproveAtTheFlightsHeadPassesItsReviewAndMarksItsPrReady", "the PR's state on GitHub is open, the review passed at its current head, and its checks are read", [nameof(Verbs.Merge)]),
        new("pr-checks-failed", "Ring", [Checks, Recheck], "NodeCardTests.ThePrCheckAndConflictSummariesNoLongerSendSomeoneToTheFlightPage", "the PR's checks on GitHub failed at its current head", [nameof(Verbs.FixPr), nameof(Verbs.Merge)]),
        new("outside-review-unavailable", "Handled", ["Flights/LegCommander.OutsideReview.cs"], "NoModelReviewTests.TheCardKindIsPinnedAsAHandledCardTitledWithTheFlightTheCheckAndTheResetTime"),
        new("merge-taken-back", "Handled", [LegMerge, LegQueuedPush], "MergeTakeBackTests.PlanLegOnAQueuedMergeDequeuesAndTheCardSaysSo"),
        new("merged-under-leg", "ForYou", [FlightCommanderLand], "MergedUnderLegTests.ADoneLegGoesToANewPrWithTheMergedBaseMergedInAndPushedPlain"),
        new("follow-up-pr", "Handled", [LegOpenPr], "QueuedPushTests.AGh006PushOnAPrThatMergedMeanwhileMarksTheFlightAndTheSweepOpensTheFollowUpWithTheCommit"),
        new("not-landed", "Ring", [Checks], "MergedUnderLegTests.AMergeSeenWithUncommittedChangesKeepsThePlaneAndRings", "the PR's state on GitHub is merged, and landing the flight was refused for a reason it names; work of its own that missed the merge offers a PR with it instead", [nameof(Verbs.Land), nameof(Verbs.OpenPr)]),
        new("closed-landed", "Handled", [Checks], "ReviewTests.AFlightWhosePrWasClosedWithNothingOfItsOwnLeftLandsAndOneWithWorkRings"),
        new("closed-with-work", "Ring", [Checks], "ReviewTests.AFlightWhosePrWasClosedWithWorkOfItsOwnRingsOnceAndStaysFlying", "the PR's state on GitHub is closed unmerged and the plane holds work of its own", [nameof(Verbs.Land)]),
        new("queue-dropped", "ForYou|Handled", [Checks], "ReviewTests.APrThatLeftTheMergeQueueUnmergedIsACardSayingWhyWithMergeAgain"),
        new("failure-investigation", "ForYou", ["Flights/FailureInvestigations.cs"], "FailureInvestigationTests.AFirstFailureInATestThePrDoesNotTouchIsFiledAsOneInvestigationWithItsFlightAndCard"),
        new("failure-alert", "Ring", ["Flights/FailureAlerts.cs"], "RetryAndHoldTests.TheSameHeadFailingAgainAfterItsCiRetryRaisesOneAlertCardAndHoldsTheFlightWithNoGoAround", "the same untouched-test failure signature failed a second time, in a PR after its retry or in another flight, read from the failure ledger", [nameof(Verbs.FlightPriority)]),
        new("failure-retried", "Handled", ["Flights/FailureAlerts.cs"], "RetryAndHoldTests.AFirstUntouchedQueueRemovalIsQueuedAgainOnceAtTheSameHeadWithALineNamingTheInvestigation"),
        new("failure-released", "Handled", ["Flights/FailureAlerts.cs"], "RetryAndHoldTests.AFailureInALegCheckOfASecondFlightHoldsItAndLandingTheInvestigationReleasesEveryHoldAndDoesWhatEachWasHeldFrom"),
        new("checks-rerun", "Handled", [Checks], "ReviewTests.FailedChecksRunAgainOnceAHeadBeforeTheLegIsSentRound"),
        new("ci-cap", "ForYou", [Checks], "CiBudgetTests.PastTheBudgetThePushIsRefusedWithTheNumbersAndOneForYouCardOffersTheThreeAnswersOnce"),
        new("ci-cap-answered", "Handled", ["Flights/LegCommander.CiBudget.cs"], "CiBudgetTests.LandWhatIsGreenAndSplitAreRecordedForTheControllerAndTheStopStays"),
        new("merge-dropped", "ForYou", [Checks], "LegTests.AWaitingMergeWhoseChecksFailIsDroppedOnACardThatOffersItAgain"),
        new("autopilot-merge-refused", "Ring|ForYou", [Checks], "AutopilotMergeRefusalTests.AFailedRequiredCheckRingsWithReRunTheFailedCheckAndNoMergeAgain", "the PR's state on GitHub is still open (not merged) and the refusal's class is a person's (or a passing one that failed its third try), with the one action to take", [nameof(Verbs.RerunChecks), nameof(Verbs.OpenPr), nameof(Verbs.Dismiss), nameof(Verbs.Merge)]),
        new("pr-marks-refused", "Handled", [LegPlanners, "Flights/PrMarks.cs", PrReviewsStatus, PrReviewsSweep], "PrMarksTests.AWriteGitHubRefusesIsACardAndNeverFailsTheReviewOrLosesTheVerdict"),
        new("node-label-refused", "Handled", [Move, LegOpenPr, "Flights/RepoSetup.cs", FlightCommanderCreate, FlightCommanderAdopt], "PrMarksTests.ANodeLabelGitHubRefusesOnTheNewPrIsACardNotAFailedPr"),
        new("second-flight-made", "Handled", [FlightCommanderCreate], "DuplicateFlightTests.AnywayWithAWhyMakesTheSecondAndKeepsTheWhyOnAHandledCard"),
        new("duplicate-flights", "ForYou", [FlightCommanderCreate], "WithdrawFlightTests.ADuplicateMadeAnywayRaisesOneCardOfferingToWithdrawTheNewer"),
        new("withdrawn", "Handled", ["Flights/FlightCommander.Withdraw.cs"], "WithdrawFlightTests.AFreshFlightIsWithdrawnItsRowIsGoneItsCodeIsFreeAndAHandledCardSaysWhoAndWhy"),
        new("design-oversize", "ForYou", ["Designs/DesignRecord.cs"], "DesignRepoFilesTests.AnOversizedRenderIsNotCommittedTheSectionSaysSoAndOneCardIsRaisedNotOnePerPush"),
        new("design-mirror-failed", "Handled", ["Flights/DesignDecisionMirror.cs"], "DesignDecisionMirrorTests.DesignDecisionMirrorNotesAFailedCommentOnARingCardNamingThePrAndKeepsTheDecision"),
        new("designer-say-failed", "ForYou", ["Designs/Designers.cs"], "DesignerTests.ARefusedSayBacksOffDoublingAndRingsAForYouCardAtTheThirdThenClosesItOnceItGoes"),
        new("repo-setup", "Handled", ["Flights/RepoSetup.cs"], "RepoSetupTests.SetupAgainstAnOrgWithNoFieldAndNoLabelsMakesTheFieldAndTheReadLabelsOnceAndASecondRunMakesNothing"),
        new("landed", "Handled", [FlightCommanderLand], "CliTests.ADirectFlightIsFlownFromTheCliAloneUpToItsLanding"),
        new("issue-left-open", "ForYou", [FlightCommanderLand], SourcePinned),
        new("follow-up-no-flight", "ForYou", ["Flights/FollowUpSweep.cs"], "FollowUpCountTests.AnIssueWithNoFlightRingsOnceAfterADayNamingTheIssueAndTheCommand"),
        new("maintenance-run", "Handled", ["Flights/IdleMaintenance.cs"], "IdleMaintenanceTests.TheHandledLineIsPinnedExactly"),
        new("maintenance-first-run", "ForYou", ["Flights/IdleMaintenance.cs"], "MaintenanceFirstRunTests.AnUnsetSettingRaisesOneCardWithTheTotalsAndTheTopFivePerStepAndRunsNoRealStep"),
        new("disk-swept", "Handled", ["Flights/DiskSweep.cs"], "DiskSweepTests.ASweepThatFreedSomethingRaisesOneHandledLineNamingTheCountsAndTheUniqueBytes"),
        new("worktree-removed", "Handled", ["Flights/DiskSweep.cs"], "DiskSweepTests.AHandMadeWorktreeWhosePrMergedAndIsCleanIsRemovedWithAHandledLine"),
        new("worktree-dirty", "Handled", ["Flights/DiskSweep.cs"], "DiskSweepTests.ADirtyHandMadeWorktreeIsKeptWithOneHandledLineAndNoSecondOnTheNextSweep"),
        new("disk-low", "ForYou", ["Flights/DiskSweep.cs"], "DiskSweepTests.FreeSpaceBelowTheThresholdRaisesOneRingNamingTheLargestItemsAndClosesWhenSpaceIsBack"),
        new("stray-process", "ForYou", ["Flights/StraySweep.cs"], "StraySweepTests.OneCardPerStrayNamesTheProcessItsOwnerAndTheKillCommand", NodePressed: true),
        new("poller-caller", "Ring", ["Flights/PollerSweep.cs"], "PollerSweepTests.AFakeCallerAt300AnHourFor3HoursRingsExactlyOneCardNamingIt", "the node's own per-caller request tally shows a process that is none of a job's, a session's or pilot's own over 200 requests an hour for 2 full hours", [nameof(Verbs.EndPoller)]),
        new("poller-ended", "Handled", ["Flights/PollerSweep.cs"], "PollerSweepTests.PressingEndKillsOnlyWhileTheStartStillMatches"),
        new("stray-killed", "Handled", ["Flights/StraySweep.cs"], "StraySweepTests.AProcessOfAnEndedJobTheNodeLaunchedIsKilledThroughTheTableAndNothingElseIs"),
        new("plane-left", "Handled", [Move, FlightCommanderLand], "RemovalQueueTests.ALandedFlightWhosePlaneFolderIsHeldSaysSoOnAHandledLineAndQueuesIt"),
        new("branch-kept", "ForYou|Handled", [FlightCommanderLand, "Flights/LandedBranchSweep.cs"], "LandingBranchTests.ALandedFlightWhoseLocalBranchIsOneCommitPastTheMergedHeadKeepsItAndRaisesOneCardNamingTheCommit"),
        new("folded", "Handled", [FlightCommanderLand], "OverlapTests.FoldRetiresTheAskingFlightLandedAndMarkedFoldedWithNoLegsAndItsPlaneGoneButItsBranchKept"),
        new("routed", "Handled", [FlightCommanderRoute], "FleetRoutingTests.ARoutedFlightIsMadeOnTheWillingPeerWithItsOwnCheckoutAndSaysWhere"),
        new("connection-released", "Handled", [FlightCommanderConnect], "ConnectReleaseTests.ALandWithoutAPrReleasesTheWaiterFromThatFlightOnly"),
        new("archive-held", "Handled", ["Flights/FlightArchiveSweep.cs"], "FlightArchiveSweepTests.AFlightWhoseWorktreeExistsOrWhichHasAnOpenCardIsHeldBackAndTheLineSaysWhyExactlyOnce"),
        new("archive-sweep", "Handled", ["Flights/FlightArchiveSweep.cs"], "FlightArchiveSweepTests.AMixedSweepRaisesOneSummaryWithTheCountsByReason"),
        new("branch-sweep", "Handled", ["Flights/LandedBranchSweep.cs"], "LandedBranchSweepTests.ThirtyLandedRowsDeleteExactlyTheSafeOnesAndRaiseOneLineWithCountsByReason"),
        new("disconnected", "Handled", [FlightCommanderConnect], "DisconnectTests.TheDroppedConnectionStaysDroppedWhenTheJournalIsReplayed"),
        new("received", "Handled", [FlightCommanderReceive], "FlightMoveTests.AHandedFlightArrivesWithItsLegsReportsPrReviewAndOpenCardsOnAPlaneAtThePushedHead"),
        new("adopted", "Handled", [FlightCommanderAdopt], "AdoptTests.AFlightOfANodeSilentPastAdoptAfterIsTakenFromTheCopyWithItsPlaneLegsLabelCommentsAndAnAtRiskCard"),
        new("adopt-at-risk", "ForYou", [FlightCommanderAdopt], "AdoptTests.AFlightOfANodeSilentPastAdoptAfterIsTakenFromTheCopyWithItsPlaneLegsLabelCommentsAndAnAtRiskCard"),
        new("adopt-comment-refused", "Handled", [FlightCommanderAdopt], "AdoptTests.ARefusedCommentOrLabelIsACardAndTheAdoptionStands"),
        new("move-failed", "Ring", [Move], "FlightMoveTests.APushBehindTheRemoteLeavesTheFlightHereWithTheReasonAndARetryThatRunsTheMoveAgain", "the push of the flight's branch to hand it to the peer was refused, read from git", [nameof(Verbs.MoveFlight)]),
        new("planner-ended", "Handled", ["Flights/LegCommander.Planners.cs"], "PlannerWindDownTests.AnIdlePlannerOfAFlightWhoseLegFliesIsEndedAsPlannedAndItsSlotGoesToTheNextFlightOnTheSweep"),
        new("moved-away", "Handled", [Move], "FlightMoveTests.AHandMoveOutsideMaintenanceMovesTheFlightAsWell"),

        // PRs of the person's own, reviewed and rechecked.
        new("pr-review-ready", "ForYou", [PrReviewsVerdicts], "PrReviewTests.OnlyTheReviewersVerdictRaisesTheCardAndTheWorktreeGoesWhenTheCardCloses"),
        new("pr-review-waiting", "Handled|ForYou", [Reviews, PrReviewsSweep], "AdmissionReviewTests.APrReviewAtTheOpusCapAnswersQueuedLeavesOneHandledCardAndStartsOnTheSweepAndSaysItOnce"),
        new("pr-fix-pushed-unreviewed", "ForYou", [PrReviewsFixerRounds, PrReviewsFixerHold], "PrRecheckTests.TheFixersPushWhoseReviewCouldNotStartStaysAnOpenCardAfterTheSweep", NodePressed: true),
        new("pr-fixer-work-held", "ForYou", [PrReviewsFixerHold], "FixerHoldTests.FilesLeftUncommittedInTheFixWorktreeHoldThePrWithOneCard"),
        new("pr-review-started", "Handled", [PrReviewsStart], "PrReviewTests.APrInARepoWithACloneIsReviewedInThatClonePinnedAtItsHead"),
        new("pr-review-left", "Handled", [PrReviewsSweep], "RemovalQueueTests.AReviewWhoseWorktreeIsHeldClosesAtOnceWithOneHandledLineAndAQueueEntry"),
        new("pr-review-restarted", "Handled", [PrReviewsSweep], "PrReviewTests.AReviewingRowWhoseSessionIsGoneAfterARestartIsStartedAfreshAtTheFirstSweepOnce"),
        new("pr-review-lost", "ForYou", [PrReviewsSweep], "PrReviewTests.AReviewLostInARestartThatCannotStartAgainTakesItsStatusAndLabelBackAndRaisesACardWithReviewAgain"),
        new("pr-review-posted", "Handled", [PrReviewsVerdicts], "PrReviewTests.PostsItselfOnAnOpenPr"),
        new("pr-review-queued", "Handled", [PrReviewsQueue, PrReviewsLand], "PrReviewTests.ACleanCommentOnOnesOwnPrQueuesItAtTheReviewedHeadOnceAndSaysSo"),
        new("pr-review-merge", "ForYou", [PrReviewsQueue], "PrReviewTests.ABaseWithoutAMergeQueueIsMergedDirectlyByTheNodeOnce"),
        new("pr-review-queue-refused", "ForYou", [PrReviewsQueue, PrReviewsLand], SourcePinned),
        new("pr-review-design", "Ring", [PrReviewsFixer], "PrFixTests.ADesignQuestionRingsAndStartsNoFixer", "the reviewer's verdict on the PR names a design question, read from its review", [nameof(Verbs.FixPr)]),
        new("pr-review-design-settled", "Handled", [PrReviewsFixerRounds], "DesignSettledTests.AMergedPrDowngradesItsRingingDesignCardToANoticeNamingIt"),
        new("pr-review-no-fixer", "Ring", [PrReviewsFixer], "PrRecheckTests.ARecheckOfAChangesReviewWithNoFixerRingsTheOwnerOnceAndADismissalSticksUntilTheHeadMoves", "the review of the PR asks for changes and no fixer can start", [nameof(Verbs.FixPr)]),
        new("pr-fix-started", "Handled", [PrReviewsFixer], "PrFixTests.AChangesVerdictOnAnOwnPrInATrackedRepoStartsOneFixerOnThePrsBranch"),
        new("pr-fix-offer", "ForYou", [PrReviewsFixer], "PrFixTests.APrByAnotherLoginRaisesAForYouCardWhoseFixItStartsTheSameFixer"),
        new("pr-fix-failed", "ForYou", [PrReviewsFixer, PrReviewsFixerRounds, "Flights/PrReviews.FollowUp.cs"], SourcePinned),
        new("pr-fix-capped", "Ring", [PrReviewsFixer], "RingCountTests.ThePrRingTitlesTheRoundsInAllAndListsThePointsInThreeGroups", "the PR's fix rounds have reached the cap with changes still asked", [nameof(Verbs.AllowRound)]),
        new("pr-fix-ended", "ForYou", [PrReviewsFixerRounds], "PrFixTests.AFixerThatEndsWithoutPushingLeavesACardWithFixItAndNoReview", NodePressed: true),
        new("pr-fix-left", "Handled", [PrReviewsFixerRounds], SourcePinned),
        new("pr-fix-follow-up", "Handled", [PrReviewsFixerRounds], "FixFollowUpTests.AMergedPrsFixerCommitGoesToANewBranchAndAFollowUpPrThatIsReviewed"),
        new("pr-fix-follow-up-failed", "ForYou", [PrReviewsFixerRounds], "FixFollowUpTests.APushGitHubRefusesLeavesAForYouCardNamingTheCommitAndTheLocalBranch"),
        new("pr-fix-follow-up-conflict", "ForYou", [PrReviewsFixerRounds], "FixFollowUpTests.FixerCommitsThatConflictWithMainRaiseAForYouCardAndPushNothing"),
        new("pr-fix-follow-up-settled", "Handled", [PrReviewsFixerRounds], "FixFollowUpTests.AFixerCommitWhosePatchIsOnMainUnderAnotherShaOpensNoPrAndLeavesOnlyAHandledLine"),
        new("pr-fix-head-moved", "ForYou", [PrReviewsFixerRounds], "IdleFixerTests.AHeadSomeoneElseMovedUnderTheFixerStopsItRaisesACardAndStartsNoReview"),
        new("pr-fix-author-holds", "Ring", [PrReviewsFixer], "PrFixWorktreeTests.AWorktreeHeldByALiveNonFixerSessionStartsNoFixerAndRaisesOneCardNamingIt"),
        new("pr-fixer-finished","Handled", [PrReviewsFixerRounds], "IdleFixerTests.AnIdleFixerAfterAPushedAndApprovedRoundIsEndedWithOnlyAHandledLineAndNoCardToAnswer"),
        new("pr-recheck", "Handled", [Recheck], SourcePinned),
        new("pr-recheck-failed", "ForYou", [Recheck], "PrRecheckTests.AReviewThatCannotStartKeepsTheLabelAndRaisesAForYouCard"),
        new("pr-recheck-conflicts", "ForYou", [Recheck], "PrRecheckTests.AReviewedPrThatConflictsGetsAForYouCardAndTheConflictsLabel"),
        new("card-resolved", "Handled", ["Flights/StaleCards.cs"], "StaleCardTests.ASummaryRunLeavesACardItsCheckContradicts"),

        // The node itself, the watchtower and the person's own asks.
        new("watchtower-down", "Ring", [Tower, TowerStream], "WatchtowerTests.AnUntrustedFolderRingsWithTheFixAndIsTriedAgainLater", "the watchtower's folder is untrusted or its session did not start, read from its row", [nameof(Verbs.RestartWatchtower)]),
        new("watchtower-stopped", "Ring|Handled", [Tower], "WatchtowerTests.OneTheUsageLimitStoppedIsResumedAfterTheReset", "the watchtower's session row is stopped by the API with no reset to resume at", [nameof(Verbs.RestartWatchtower)]),
        new("watchtower-waits", "Handled", [Tower, TowerStream], "StartGateTests.TheWatchtowerKeptByTheNodeWaitsForTheWindowAndStartsOnceItHasResetButARestartAskedForGoesAhead"),
        new("watchtower-ask", "ForYou", [Asks], "WatchtowerChatTests.APromptAndAQuestionEachRaiseACardWhoseButtonsAnswerThemAndTheCardClosesOnTheChatsAnswer"),
        new("watchtower-refused", "Ring", [Refused], "WatchtowerChatTests.Refused.ARefusedPilotCommandRingsACardOfItsOwnAndARefusedOtherCallOrAPressThatRanRingsNothing", "the watchtower's stream shows Claude Code refused its pilot command, which pressed no card's button", [nameof(Verbs.Tell)]),
        new("watchtower-resumed", "Handled", [Tower], SourcePinned),
        new("watchtower-started", "Handled", [Tower], SourcePinned),
        new("watchtower-duplicate", "Handled", [Tower], "OneWatchtowerTests.StartingOneStopsAnotherLiveWatchtowerRowAndSaysSo"),
        new("watchtower-parked", "Handled", [Tower], SourcePinned),
        new("watchtower-churning", "ForYou", [Tower], "WatchtowerTests.ManyShortLivedWatchtowersMarkItChurningAndRaiseOneCardThatIsUpdatedAndClosed"),
        new("watchtower", "", ["NodeCommands.cs"], "WatchtowerTests.ARaiseIsACardFromWhoeverRaisedItAndATellNeedsAWatchtower"),
        new("persons-word", "Handled", ["NodeCommands.cs"], "PersonsWordTests.AFailedLegIsMarkedDoneAnywayAtThePersonsWord"),
        new("assistant-ask", "ForYou", ["Autopilot.cs"], "AutopilotTests.ANoAutoMergeReposAssistantsMergeRaisesAForYouConfirmCardAndDoesNotMerge"),
        new("autopilot-ran", "Handled", ["Autopilot.cs", Checks, "Flights/NodePress.cs"], "AutopilotTests.TheSameMergeGoesThroughRecordedWithTheSessionAndMessageAndAHandledCardSaysIt"),
        new("autopilot-repeat", "Handled", ["Flights/RepeatDetector.cs"], "RepeatDetectorTests.TheSecondCardOfAKindOnTheSameSubjectOpensOneHandledTaskNamingBothWithTheirEvidence"),
        new("investigation", "Handled", ["Flights/Investigator.cs"], "InvestigatorTests.AnActionOutcomeIsAHandledLineAndADecisionByTheInvestigatorAndEndsTheSession"),
        new("investigation-start", "Handled", ["Flights/Investigator.cs"], "InvestigatorTests.AStartedRunLeavesAHandledLineThatASecondNodeReadsBack"),
        new("investigation-join", "Handled", ["Flights/Investigator.cs"], "InvestigatorTests.AStartedRunLeavesAHandledLineThatASecondNodeReadsBack"),
        new("investigator-ring", "Ring", ["Flights/Investigator.cs"], "InvestigatorTests.ARingOutcomeOnASubjectWithNoHeldCardRaisesOneRingSayingWhatWasTried", "an investigator run the node started on the subject reported the ring outcome, naming what was tried and what is needed", [nameof(Verbs.Tell)]),
        new("investigator-bound", "Ring", ["Flights/Investigator.cs"], "InvestigatorTests.PastThePerDayBoundATriggerWithNoCardRingsOneBoundCard", "the node started the autopilot-investigations number of runs this local day and another trigger came", [nameof(Verbs.Set)]),
        new("grant-asked", "Ring", ["NodeLan.cs"], "NodeCardTests.AGrantAskIsARingCardWhoseChoicesSaySoInPlainWords", "the asking node's grant is not yet answered in the node's grants", [nameof(Verbs.AnswerGrant)]),
        new("update", "Ring|ForYou|Handled", ["Updates/NodeUpdater.cs", "Updates/UpdateRollback.cs"], "UpdateTests.AskRaisesACardWithUpdateOnce", "the published version is newer than the running node's, or the update record says the new version did not start twice and the node went back", [nameof(Verbs.Update), nameof(Verbs.NoteIdea)]),
        new("restart", "Handled", ["NodeSettingsCommands.cs"], "NodeRestartTests.ARestartStartsTheSuccessorStopsTheNodeAndLogsAHandledLineNamingTheCaller"),
        new("store", "Ring|ForYou", ["NodeState.cs", "StoreRecovery.cs"], SourcePinned, "the start could not read the stored files (FileJournal.Load or NodeState's constructor threw), set them aside and started from a backup or an empty store", [nameof(Verbs.NoteIdea)]),
        new("stop-unconfirmed", "Ring", ["Sessions/SessionMessenger.cs"], "SessionEndTests.AStopThatDoesNotTakeRaisesOneRingCardAndIsNotConfirmed", "the session's row still shows it live after the stop was asked", [nameof(Verbs.EndSession)]),
        new("turns-outage", "ForYou", ["Flights/TurnOutage.cs"], "TurnOutageTests.AnAccountChangeOpensOneOutageWithOneCardAndATurnLaterEndsItAndKeepsTheWindow"),
        new("account-limit", "ForYou", ["Flights/TurnOutage.cs"], "AccountLimitTests.OneCardNamesTheLimitAndTAndClosesAtT"),
        new("usage-high", "Handled", ["Sessions/UsageReader.cs"], "UsageTests.NearlySpentTheFiveHourWindowRaisesOneCardPerWindow"),
        new("usage-week", "ForYou", ["Flights/StartGate.cs"], "StartGateTests.TheWeekRingsOncePerWindowAndHoldsNothing"),
        new("removal-held", "Handled", ["Flights/RemovalQueue.cs"], "RemovalQueueTests.AFolderHeldPastThreeStartsIsOneHandledLineNamingTheHolderAndNoMoreOnceItIsReleased"),
        new("job-idle", "ForYou", ["Flights/JobRunner.cs"], "JobTests.ANearIdleJobWithAnotherQueuedBehindItRaisesOneCardNamingJobOwnerAndWaiter"),
        new("bench-result", "ForYou", ["Flights/BenchRun.cs"], "BenchRunTests.ASingleRefRunsOnceInADetachedShortNamedWorktreeAndTheCardHoldsItsTableUnderItsRefAndSha"),
        new("job-told-unanswered", "Ring", ["Flights/JobRunner.cs"], "JobRetellTests.IdleAndSilentIsRetoldOnceThenRings", "the job's owner was told once of its end and its session row shows no turn since", [nameof(Verbs.Say)]),
        new("job-bench-busy", "Ring", ["Flights/JobRunner.cs"], "BenchSlotTests.ABenchWaitingPastTheBoundForABusyCpuRingsOnceNamingTheProcessAndClosesWhenItStarts", "a process other than the node holds the CPU past the bench's bound, read from the process list", [nameof(Verbs.Say)]),
        new("job-long", "Ring", ["Flights/JobRunner.cs"], "LongJobRingTests.ARunningJobPastTheWindowWithAJobQueuedBehindItRaisesOneRingCardWhoseChoiceCancelsIt", "a running job has held the slot past the long-job window while another job waits behind it, read from the job rows and the queue", [nameof(Verbs.CancelJob)]),
        new("job-untold", "Ring", ["Flights/JobRunner.cs"], "JobTests.AnOwnerThatCannotBeToldRings", "the job's owner session cannot be told (its row is gone or it refuses messages)", [nameof(Verbs.Say)]),
        new("queued", "Handled", ["Flights/NamedLock.cs"], "LockWaitTests.ALegCommandWaitingPastFiveSecondsIsAnsweredWithWhatHoldsTheLockAndRunsOnceItFrees"),
        new("session-compacted", "Handled", ["Flights/Compactor.cs"], "CompactorTests.AWorkerWaitingOnItsJobIsCompactedWithWhatItWaitsOnAndOneNotWaitingIsNot"),
        new("watchtower-still-full", "ForYou", ["Flights/Compactor.cs"], "CompactorTests.AWatchtowerStillFullAfterACompactionAndARestartIsSaidOnceAndLeftAlone"),
        new("digest", "ForYou", ["Flights/Digest.cs"], "SignalTests.TheDigestListsWhatLandedWhatWasHandledAndWhatIsParkedAndReplacesTheLastOne"),
        new("peer-away", "Handled", ["Flights/PeerPresence.cs"], "PeerPresenceTests.The_presence_lines_land_as_Handled_cards_on_the_node"),
        new("peer-back", "Handled", ["Flights/PeerPresence.cs"], "PeerPresenceTests.The_presence_lines_land_as_Handled_cards_on_the_node"),
        new("stale-acted", "Handled", ["Flights/StaleWatch.cs"], "StaleWatchTests.HalfABoundNudgesAnIdleWorkerOnce"),
        new("idea-conflict", "ForYou", ["Flights/Ideas.cs"], "IdeaEditTests.AContradictionRaisesACardWithBothVersionsAndTheMergeAsDefaultAndWritesNothingUntilItIsAnswered"),
        new("idea-repo", "ForYou", ["Flights/Ideas.cs"], "IdeaRepoTests.WhenTwoReposFitEquallyACardAsksAndNothingIsCreatedUntilItIsAnswered"),
    ];

    /// <summary>
    /// A raise whose kind is a variable the call is handed (a helper's parameter, a card carried over from another node): the file
    /// and the expression. The kinds that reach it are raised, and so catalogued, at the calls of the helper.
    /// </summary>
    public static readonly HashSet<string> VariableKinds =
    [
        "Flights/LegWatch.cs|kind",
        "Flights/LegWatch.cs|cardKind",
        "Flights/StartGate.cs|kind",
        "Flights/FlightCommander.Receive.cs|card.Kind",
        "Flights/PrReviews.Cards.cs|kind",
    ];

    /// <summary>
    /// A raise whose title is no literal in the call (a variable built elsewhere, a field of a carried-over card): the file and the
    /// expression, with where the words come from. Their literals are checked where the variable is built, when it is in the same file.
    /// </summary>
    public static readonly Dictionary<string, string> TitlesFrom = new()
    {
        ["Flights/PeerPresence.cs|title"] = "PeerPresence.Line names the peer and since when it is away, or says that it answers again; PeerPresenceTests pins both",
        ["Flights/IdleMaintenance.cs|run.Line()"] = "MaintenanceRun.Line, whose text IdleMaintenanceTests.TheHandledLineIsPinnedExactly pins",
        ["Flights/Digest.cs|title"] = "Digest.Compose names what landed, what was handled and what is parked",
        ["Flights/LegWatch.cs|title"] = "the parameter of LegWatch.Ring, whose titles are the literals at each Ring( call, which the guard reads as it reads RaiseCard",
        ["Flights/LegCommander.Overlap.cs|title"] = "the overlap title built in LegCommander.Overlap names both flights, the files they share and the choice",
        ["Flights/LegCommander.Move.cs|title"] = "the move's own failure text: its first part is the reason, built where the move is refused",
        ["Flights/LegCommander.Move.cs|text"] = "the move's own line: who handed the flight to whom",
        ["Flights/LegCommander.FollowUp.cs|title"] = "the title DeferredRoutes.RouteAsync builds: the subject and the owner-decision question, pinned by DeferredPointTests",
        ["Flights/PrReviews.FollowUp.cs|title"] = "the title DeferredRoutes.RouteAsync builds: the subject and the owner-decision question, pinned by DeferredPointTests",
        ["Flights/PrReviews.Cards.cs|title"] = "the parameter of PrReviews.RaiseOnce, whose titles are the literals at each RaiseOnce( call, which the guard reads as it reads RaiseCard",
        ["Flights/PackageWatch.cs|late.Title"] = "PackageWatch.LateAsync names the package, its version and the run",
        ["Flights/FlightCommander.Receive.cs|card.Title"] = "the card a refused receive carried over from the node that raised it",
        ["Flights/FlightCommander.Receive.cs|way"] = "the line the routing or the move wrote: where the flight went and why",
        ["Flights/FlightCommander.Route.cs|way"] = "the line the routing or the move wrote: where the flight went and why",
        ["Flights/Watchtower.Asks.cs|title"] = "RaiseAskCard: a prompt's title names the call it asks leave for, a question's its first question",
        ["NodeState.Flights.cs|logged.Title"] = "the planned card logged as Handled while its flight was held, raised again for the person once the hold lifts: \"<code>: <n> legs planned\"",
        ["NodeCommands.cs|title"] = "the title the person, the watchtower or a session gave to pilot raise",
        ["NodeSettingsCommands.cs|line"] = "the line the restart wrote: who restarted the node and how",
        ["Updates/NodeUpdater.cs|line"] = "the line the install wrote: the version installed and the node restarted",
        ["Flights/PrChecks.cs|LegCommander.ChecksFailedTitle(flight.Code, number, checks?.FailedName, why)"] = "ChecksFailedTitle names the check and why the leg was not sent back",
        ["Flights/PrChecks.cs|LegCommander.ChecksFailedTitle(flight.Code, number, null, why)"] = "ChecksFailedTitle names the check and why the leg was not sent back",
        ["Flights/PrRecheck.cs|LegCommander.ChecksFailedTitle(code, entry.Number, null, why)"] = "ChecksFailedTitle names the check and why the leg was not sent back",
    };

    /// <summary>
    /// The kinds whose cards offer one of <c>NodePress</c>'s verbs and still stay the person's, each with the reason from the source
    /// (#1311). <c>CardKindTests</c> fails on a kind that offers such a verb and is neither <c>NodePressed</c> nor named here.
    /// </summary>
    public static readonly Dictionary<string, string> PersonsOwn = new()
    {
        ["pr-fix-offer"] = "another author's PR: the person gives Fix it, the node's fixer rule covers only an ended fixer",
        ["pr-review-design"] = "a design question: the person answers it, and the fixer starts on that answer",
        ["pr-review-no-fixer"] = "no fixer may start (round cap, author or design question): nothing the node's rule lets through",
        ["pr-fix-failed"] = "the fixer could not start: the same start again is not a rule, the person looks at why",
        ["pr-review-lost"] = "a review lost in a restart that cannot start again: the person decides on the retry",
        ["pr-recheck-failed"] = "a review that cannot start: the node's own recheck already tried it",
        ["pr-checks-failed"] = "failed checks: Fix it there is the person's or the flight's word, not a node rule",
        ["pr-fix-capped"] = "the round cap: only the person allows another round",
        ["pr-fix-follow-up-failed"] = "a push GitHub refused: the person looks at it before any new review",
        ["pr-fix-follow-up-conflict"] = "commits that conflict with the base: the person takes what is still needed by hand",
        ["pr-fixer-work-held"] = "work left in the fixer's worktree: only a person says whether to push it or let it go",
        ["pr-recheck-conflicts"] = "a PR that conflicts: the person settles it",
        ["pr-review-waiting"] = "a review waiting on a cap: the node starts it itself on its sweep",
    };

    /// <summary>
    /// The kinds no test runs: a fragment their title must keep and, where they have a button, a fragment of its label. A title
    /// reworded to something generic breaks these as a pinning test would.
    /// </summary>
    public static readonly (string Kind, string TitleHas, string? LabelHas)[] SourcePins =
    [
        ("issue-left-open", "is still open", null),
        ("pr-review-queue-refused", "refused to queue", "again"),
        ("pr-fix-failed", "the fixer could not start", "Start the fixer"),
        ("pr-fix-left", "fixer worktree is still there", null),
        ("pr-recheck", "recheck of", null),
        ("watchtower-resumed", "resumed after its usage limit reset", null),
        ("watchtower-started", "Started a fresh watchtower", null),
        ("watchtower-parked", "Stopped the watchtower", null),
        ("store", "could not write its snapshot", null),
        ("store", "could not be read", "unreadable store"),
    ];
}
