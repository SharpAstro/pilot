using Pilot.Contracts;

namespace Pilot.Node.Tests;

/// <summary>
/// One kind of card the node raises: its kind word, the bins it is raised into (<c>Ring</c>, <c>ForYou</c>, <c>Handled</c>, joined by
/// <c>|</c>), the Pilot.Node files that raise it, and the test that pins its title and choices (<c>Class.Method</c> in this project,
/// which must exist and name the kind). A kind nothing runs pins is pinned by <see cref="CardKindCatalog.SourcePins"/> instead, in
/// <c>CardKindTests.SourcePinned</c>. A kind raised into the Ring bin also names the <c>Fact</c> it is checked against before it rings (what the
/// node read, from GitHub, a row, the session or the plane, that says the person must act) and the <c>Choices</c>: the <c>Verbs</c> its card offers,
/// each one the node accepts in the state the card is raised in (#799 rule 5: a ring is for what only the person can settle, with something to press).
/// A kind whose choices are handed to the raiser by its callers (a start that failed offers the start again) lists those files as <c>ChoiceFiles</c>.
/// </summary>
internal sealed record CardKindEntry(string Kind, string Bins, string[] Files, string Pin, string? Fact = null, string[]? Choices = null, string[]? ChoiceFiles = null);

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
    private const string LegHoldRelease = "Flights/LegCommander.HoldRelease.cs";
    private const string HumanVerification = "Flights/LegCommander.HumanVerification.cs";
    private const string LegMerge = "Flights/LegCommander.Merge.cs";
    private const string LegQueuedPush = "Flights/LegCommander.QueuedPush.cs";
    private const string LegChecksAndConflicts = "Flights/LegCommander.ChecksAndConflicts.cs";
    private const string Lay = "Flights/LegCommander.Lay.cs";
    private const string Shape = "Flights/LegCommander.Shape.cs";
    private const string Watch = "Flights/LegWatch.cs";
    private const string FlightCommanderConnect = "Flights/FlightCommander.Connect.cs";
    private const string FlightCommanderCreate = "Flights/FlightCommander.Create.cs";
    private const string FlightCommanderLand = "Flights/FlightCommander.Land.cs";
    private const string FlightCommanderReceive = "Flights/FlightCommander.Receive.cs";
    private const string FlightCommanderRoute = "Flights/FlightCommander.Route.cs";
    private const string Move = "Flights/LegCommander.Move.cs";
    private const string Checks = "Flights/PrChecks.cs";
    private const string Reviews = "Flights/PrReviews.cs";
    private const string PrReviewsFixer = "Flights/PrReviews.Fixer.cs";
    private const string PrReviewsFixerRounds = "Flights/PrReviews.FixerRounds.cs";
    private const string PrReviewsQueue = "Flights/PrReviews.Queue.cs";
    private const string PrReviewsStart = "Flights/PrReviews.Start.cs";
    private const string PrReviewsStatus = "Flights/PrReviews.Status.cs";
    private const string PrReviewsSweep = "Flights/PrReviews.Sweep.cs";
    private const string PrReviewsVerdicts = "Flights/PrReviews.Verdicts.cs";
    private const string Recheck = "Flights/PrRecheck.cs";
    private const string Tower = "Flights/Watchtower.cs";

    public static readonly CardKindEntry[] All =
    [
        // Workers, legs and planners.
        new("worker-stopped", "Ring", [Watch], "LegWatchTests.AStopWithNoResetTimeRingsAtOnce", "the worker's session row is stopped by the API (no reset time to resume at), the leg is flying and its flight has not landed", [nameof(Verbs.Say), nameof(Verbs.GoAround)]),
        new("worker-limit-wait", "Handled", [Watch], "LegCardTests.AUsageLimitStopTheNodeResumesItselfIsAHandledLineAndNoCard"),
        new("worker-idle", "Ring", [Watch], "LegWatchTests.ANudgeTheNodeCannotDeliverRings", "the worker's session row is idle with its leg unfinished, nothing of its own (a job, the node's recheck) is what it waits on, and its flight has not landed", [nameof(Verbs.Say), nameof(Verbs.GoAround)]),
        new("worker-waiting", "Ring", [Watch], "LegWatchTests.AWaitingWorkerRingsWithItsQuestionAndTheCardClosesWhenItWorksAgain", "the worker's last message asks the person, its leg is not rechecking, and its flight has not landed", [nameof(Verbs.GoAround)]),
        new("worker-failed", "Ring", [Watch], "LegWatchTests.AFailedWorkerAndOneThatNeverShowedUpRing", "the worker's session row is failed (or its API said why), the leg is flying and its flight has not landed", [nameof(Verbs.Say), nameof(Verbs.GoAround)]),
        new("worker-missing", "Ring", [Watch], "LegWatchTests.AFailedWorkerAndOneThatNeverShowedUpRing", "the tracker's listing of sessions never showed the worker the node started", [nameof(Verbs.GoAround)]),
        new("worker-never-turned", "Ring|Handled", [Watch], "LegWatchTests.AWorkerThatNeverTakesATurnIsStoppedAndStartedOnceThenRungWhenTheSecondShowsNothing", "the worker showed no first turn after two starts, read from its session row", [nameof(Verbs.GoAround)]),
        new("worker-nudged", "Handled", [Watch], "LegWaitTests.AnIdleWorkerWhoseLongWaitEndsIsWokenOnceAndNotNudgedOnTopOfIt"),
        new("worker-told-job", "Handled", [Watch], "LegWatchTests.AnIdleWorkerWithAnEndedUntoldJobIsToldItAndNeitherNudgedNorRung"),
        new("worker-compacted", "Handled", [Watch], "LegWatchTests.AnIdleWorkerAtNinetyFivePercentIsCompactedAndNotNudged"),
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
        new("leg-failed", "Ring", [LegFinish], "PushBlockedTests.AFailedLegWhoseCheckFailsGetsTheOrdinaryCardNotThePushBlockedOne", "the leg's finish was failed and its check, rerun by the node, still fails", [nameof(Verbs.GoAround), nameof(Verbs.FinishLeg)]),
        new("push-blocked", "Ring", [LegFinish], "PushBlockedTests.ARebasedBranchWithAPassingCheckRaisesAPushBlockedCardNamingTheForcePush", "the plane's head is on a rebased branch the remote holds differently, and the check passes", [nameof(Verbs.FinishLeg), nameof(Verbs.GoAround)]),
        new("push-diverged", "ForYou", [LegOpenPr], "PushTests.A_conflicting_remote_commit_is_refused_with_both_heads_and_the_remote_is_left_as_it_was"),
        new("leg-self-finished", "Handled", [LegFinish], "LegSelfFinishTests.A_failed_leg_with_its_work_pushed_and_its_check_passing_is_finished_by_the_node"),
        new("leg-retired", "Handled", [LegFinish], "FalseRingTests.ALegRetiredOnTheControllersWordEndsRetiredWithNoCardAndNoChoices_c3623"),
        new("leg-no-go", "Ring", [LegStart], "CardSummaryRaiserTests.TheNoGoSummaryNamesTheChoicesTheCardOffers", "the validator's report says the tree is not as the leg's brief assumes", [nameof(Verbs.StartLeg), nameof(Verbs.RemoveLeg), nameof(Verbs.PlanFlight)]),
        new("leg-validating", "Handled", [LegStart], "ValidatorTests.ALaterLegStartsAValidatorOnTheWorkerModelAtLowEffortAndNoWorker"),
        new("leg-ticker-started", "Handled", [LegStart], "LegTickingPassTests.An_ended_worker_with_commits_and_nothing_ticked_gets_a_ticker_that_finishes_the_leg_with_no_ring"),
        new("leg-removed", "Handled", [LegPlan], "LegTests.APlannedLegIsRemovedTheLegsAfterItKeepTheirOrderAndAHandledCardSaysWhy"),
        new("leg-holding", "Handled", ["Flights/LegCommander.Hold.cs"], "LegHoldTests.AWorkerFailingOnAPackageASiblingIsPublishingHoldsItsLegWithNoRingAndItsWorkerStopped"),
        new("start-failed", "Ring", ["Flights/LegCommander.Launch.cs"], "LockFreeStartTests.AStartThatFailsAfterTheAnswerClearsItsReservationAndSaysSoOnACard", "the start the node tried was refused or never answered, and nothing retries it by itself", [nameof(Verbs.StartLeg), nameof(Verbs.GoAround), nameof(Verbs.PlanFlight)], ChoiceFiles: [LegStart, "Flights/LegCommander.GoAround.cs", LegPlanners, LegReview]),
        new("next-step", "Ring|Handled", [LegAdvance, LegStart, LegPlanners, LegReview, FlightCommanderCreate], "FlightMoveTests.AnAdvanceThatThrowsAfterTheReceiveIsACardNotAFailedReceive", "the node's own next step after a receive was refused, on a flight that has not landed", [nameof(Verbs.GoAround)]),
        new("sibling-behind", "ForYou", [LegStart], "LegTests.ALegStartedWithASiblingBehindItsOriginRaisesOneCardNamingItAndThePullAndStillStarts"),
        new("sibling-pulled", "Handled", [LegStart], "LegTests.AStaleSiblingWhoseOriginWentUnreachableAfterTheCountIsStillPulled"),
        new("planner-started", "Handled", [LegPlanners], "LockFreeStartTests.APlanFlightAnswersStartedAtOnceAndItsCardComesWhenThePlannerIsUp"),
        new("planned", "ForYou", [LegPlanners], "LegTests.APlannerIsStartedInThePlaneAndItsPlannedOffersTheFirstLeg"),
        new("overlap", "Ring", ["Flights/LegCommander.Overlap.cs", "Flights/LegCommander.LegEnd.cs"], "OverlapTests.TheRecordedAnswerSurvivesAReplayOfTheJournal", "the flight's files overlap another flight's and the controller has not answered", [nameof(Verbs.Fold), nameof(Verbs.ConnectOverlap), nameof(Verbs.Separate)]),
        new("overlap-parallel", "Handled", ["Flights/LegCommander.LegEnd.cs"], "LegEndOverlapTests.AnUnforeseenOverlapWithAFlightlessOpenPrIsAnsweredParallelByTheNodeWithAHandledNoteAndNoRing"),
        new("overlap-connected", "Handled", ["Flights/LegCommander.LegEnd.cs"], "LegEndOverlapTests.AnUnforeseenOverlapWithALocalFlightIsConnectedByTheNodeWithAHandledNoteNoRingAndTheNextLegHeld"),
        new("overlap-landed", "Handled", ["Flights/LegCommander.LegEnd.cs"], "LegEndOverlapTests.APendingRowWhoseLocalOtherHasLandedIsClosedByTheSweepWithAHandledLineAndTheNextLegStarts"),
        new("flown-elsewhere", "ForYou", [LegPlan], "OneNodePerFlightTests.APlanOfAFlightWhoseBranchAPeerFliesIsRefusedWithARingNamingThePeer"),
        new("pilot-md-too-long", "Ring", [LegAdvance], "PilotMdTests.APilotMdOverTheLimitIsNotQuotedAndRaisesOneCardNamingTheRepoTheFlightAndTheLineCount", "the repo's PILOT.md line count, read from the file, is over the limit", [nameof(Verbs.PlanFlight)]),
        new("stopped-uncommitted", "Ring", ["Flights/StopGate.cs"], "StopGateTests.TheHookPrintsTheNodesDecisionAndNothingOnceLetThrough", "git status of the plane lists files not committed after the worker was told once", [nameof(Verbs.Say)]),
        new("package-late", "Ring", ["Flights/PackageWatch.cs"], "PackageWaitTests.APublishRunThatFailedRaisesTheCardAtOnce", "the package version the flight waits for is not on nuget.org and the producing run failed or is past its bound", [nameof(Verbs.FlyAnyway)]),
        new("admission-cycle", "Handled", ["Flights/AdmissionGate.cs"], "LegAdmissionTests.AFlightAdmittedToBreakACycleRaisesOneCardNamingBothFlights"),
        new("spend-alert", "ForYou", [LegStart], "SpendAlertTests.ALegPastTheAlertShareRingsWithItsTokensAndShareBeforeItsNextRoundOncePerRound"),
        new("go-around-cap", "Ring", ["Flights/LegCommander.GoAround.cs"], "GoAroundCapTests.TheSixthGoAroundOfALegIsRefusedAndRaisesTheCardWithEveryRoundsReason", "the leg's go-around count on its row has reached the cap", [nameof(Verbs.FinishLeg)]),
        new("review-kept", "Handled", [LegReview], "ReReviewTests.AHeadThatOnlyMergedTheBaseKeepsThePassWithNoReviewerStarted"),
        new("review-notes", "ForYou", [LegReview], "ReviewModelTests.APassWithNotesRaisesTheWatchtowerCardAndSendsNoLegRound"),
        new("human-verification", "ForYou", [HumanVerification], "HumanVerificationFilingTests.AFlaggedPassFilesEachItemInTheMilestoneAndRaisesOneCard"),
        new("human-verification-refused", "Handled", [HumanVerification], "HumanVerificationFilingTests.ARefusedGhCallKeepsTheVerdictAndRaisesAHandledCard"),
        new("holding-cleared", "Handled", [LegReview], "HoldingPatternTests.ClearByTheWatchtowerLetsAutopilotMergeIt"),
        new("hold-released", "Handled", [LegHoldRelease], "HoldingPatternTests.AHoldWhoseWatchtowerEndedIsReleasedWithItsNotesOnThePrAndMergesOnAutopilot"),
        new("pr-ready-restored", "Handled", [LegReview], "PassedWithoutCardTests.AFlightPassedWithNoCardGetsOneAndAutopilotMergesIt"),
        new("holding-timeout", "Ring", [Checks], "HoldingPatternTests.PastTheBoundTheTimeoutCardRingsAndMergeAsItIsMerges", "the flight's holding pattern on its row is past its timeout and its PR is still open on GitHub", [nameof(Verbs.Merge), nameof(Verbs.Land)]),
        new("review-loop", "ForYou", [LegReview], "ReviewLoopTests.TheThirdRoundWithChangesRaisesAWatchtowerCardWithEachRoundsFindings"),
        new("stray-session", "Ring", ["Flights/LegWatch.cs"], "WindDownTests.TheSweepRingsOnceForALiveSessionOfALandedFlightAndOnceForOneTheLedgerDoesNotKnow", "a live session's flight has landed (read from the flight row), or the session ledger does not know it", [nameof(Verbs.Stop)]),

        // Flights: landing, moving, PRs.
        new("review-changes", "Ring", [LegReview], "CardSummaryRaiserTests.AReviewThatAsksForChangesSaysWhichLegsFindingsAreFixedNotRedone", "the reviewer's verdict at the PR's current head asks for changes", [nameof(Verbs.GoAround)]),
        new("rebased-on-base", "Handled", [Shape], "BranchShapeTests.ABranchHoldingAMergeOfItsBaseIsPushedLinearOnARebaseBaseWithItsReviewCarried"),
        new("laid-on-base", "Handled", [Shape], "RebaseLayFlightTests.ABranchThatMergesCleanlyButDoesNotRebaseIsLaidOnItsBaseAsOneCommitAndQueuedOnce"),
        new("lay-failed", "ForYou", [Shape], "RebaseLayFlightTests.AMergeThatIsNotCleanPushesNothingAndRingsOnce"),
        new("drop-unexplained", "Handled|Ring", [Lay], "RebaseLayFlightTests.ADroppedPrWhoseCauseIsNotFoundRingsOnceAndIsNeverQueuedAgain", "a PR the base dropped has no cause the node can find on GitHub", [nameof(Verbs.Merge)]),
        new("pr-ready", "Ring", [LegReview, Lay], "PrFlightReviewTests.AnApproveAtTheFlightsHeadPassesItsReviewAndMarksItsPrReady", "the PR's state on GitHub is open, the review passed at its current head, and its checks are read", [nameof(Verbs.Merge)]),
        new("pr-conflicts", "Ring", [LegChecksAndConflicts], "NodeCardTests.ThePrCheckAndConflictSummariesNoLongerSendSomeoneToTheFlightPage", "the PR's mergeable state on GitHub is conflicting", [nameof(Verbs.GoAround)]),
        new("pr-checks-failed", "Ring", [Checks, Recheck], "NodeCardTests.ThePrCheckAndConflictSummariesNoLongerSendSomeoneToTheFlightPage", "the PR's checks on GitHub failed at its current head", [nameof(Verbs.FixPr), nameof(Verbs.Merge)]),
        new("merge-taken-back", "Handled", [LegMerge, LegQueuedPush], "MergeTakeBackTests.PlanLegOnAQueuedMergeDequeuesAndTheCardSaysSo"),
        new("merged-under-leg", "ForYou", [FlightCommanderLand], "MergedUnderLegTests.ADoneLegGoesToANewPrWithTheMergedBaseMergedInAndPushedPlain"),
        new("follow-up-pr", "Handled", [LegOpenPr], "QueuedPushTests.AGh006PushOnAPrThatMergedMeanwhileMarksTheFlightAndTheSweepOpensTheFollowUpWithTheCommit"),
        new("not-landed", "Ring", [Checks], "MergedUnderLegTests.AMergeSeenWithUncommittedChangesKeepsThePlaneAndRings", "the PR's state on GitHub is merged, and landing the flight was refused for a reason it names; work of its own that missed the merge offers a PR with it instead", [nameof(Verbs.Land), nameof(Verbs.OpenPr)]),
        new("closed-landed", "Handled", [Checks], "ReviewTests.AFlightWhosePrWasClosedWithNothingOfItsOwnLeftLandsAndOneWithWorkRings"),
        new("closed-with-work", "Ring", [Checks], "ReviewTests.AFlightWhosePrWasClosedWithWorkOfItsOwnRingsOnceAndStaysFlying", "the PR's state on GitHub is closed unmerged and the plane holds work of its own", [nameof(Verbs.Land)]),
        new("queue-dropped", "ForYou|Handled", [Checks], "ReviewTests.APrThatLeftTheMergeQueueUnmergedIsACardSayingWhyWithMergeAgain"),
        new("checks-rerun", "Handled", [Checks], "ReviewTests.OnAutopilotFailedChecksRunAgainOnceAHeadBeforeTheLegIsSentRound"),
        new("merge-dropped", "ForYou", [Checks], "LegTests.AWaitingMergeWhoseChecksFailIsDroppedOnACardThatOffersItAgain"),
        new("autopilot-merge-refused", "Ring|ForYou", [Checks], "ReviewTests.AnAutopilotMergeRefusedRingsOnceAndItsCardClosesWhenTheBaseIsMergedInAndItsPassCarriedOver", "the PR's state on GitHub is still open (not merged) when the autopilot's merge was refused", [nameof(Verbs.Merge)]),
        new("pr-marks-refused", "ForYou", [LegPlanners, "Flights/PrMarks.cs", PrReviewsStatus, PrReviewsSweep], "PrMarksTests.AWriteGitHubRefusesIsACardAndNeverFailsTheReviewOrLosesTheVerdict"),
        new("node-label-refused", "ForYou|Handled", [Move, LegOpenPr, "Flights/RepoSetup.cs", FlightCommanderCreate], "PrMarksTests.ANodeLabelGitHubRefusesOnTheNewPrIsACardNotAFailedPr"),
        new("design-mirror-failed", "ForYou", ["Flights/DesignDecisionMirror.cs"], "DesignDecisionMirrorTests.DesignDecisionMirrorNotesAFailedCommentOnARingCardNamingThePrAndKeepsTheDecision"),
        new("repo-setup", "Handled", ["Flights/RepoSetup.cs"], "RepoSetupTests.SetupAgainstAnOrgWithNoFieldAndNoLabelsMakesTheFieldAndTheReadLabelsOnceAndASecondRunMakesNothing"),
        new("landed", "Handled", [FlightCommanderLand], "CliTests.ADirectFlightIsFlownFromTheCliAloneUpToItsLanding"),
        new("issue-left-open", "ForYou", [FlightCommanderLand], SourcePinned),
        new("disk-swept", "Handled", ["Flights/DiskSweep.cs"], "DiskSweepTests.ASweepThatFreedSomethingRaisesOneHandledLineNamingTheCountsAndTheUniqueBytes"),
        new("worktree-removed", "Handled", ["Flights/DiskSweep.cs"], "DiskSweepTests.AHandMadeWorktreeWhosePrMergedAndIsCleanIsRemovedWithAHandledLine"),
        new("worktree-dirty", "ForYou", ["Flights/DiskSweep.cs"], "DiskSweepTests.ADirtyHandMadeWorktreeRaisesOneForYouCardAndNoSecondOnTheNextSweep"),
        new("disk-low", "ForYou", ["Flights/DiskSweep.cs"], "DiskSweepTests.FreeSpaceBelowTheThresholdRaisesOneRingNamingTheLargestItemsAndClosesWhenSpaceIsBack"),
        new("plane-left", "ForYou|Handled", [Move, FlightCommanderLand], "RemovalQueueTests.ALandedFlightWhosePlaneFolderIsHeldSaysSoOnAHandledLineAndQueuesIt"),
        new("folded", "Handled", [FlightCommanderLand], "OverlapTests.FoldRetiresTheAskingFlightLandedAndMarkedFoldedWithNoLegsAndItsPlaneGoneButItsBranchKept"),
        new("routed", "Handled", [FlightCommanderRoute], "FleetRoutingTests.ARoutedFlightIsMadeOnTheWillingPeerWithItsOwnCheckoutAndSaysWhere"),
        new("connection-released", "Handled", [FlightCommanderConnect], "ConnectReleaseTests.ALandWithoutAPrReleasesTheWaiterFromThatFlightOnly"),
        new("disconnected", "Handled", [FlightCommanderConnect], "DisconnectTests.TheDroppedConnectionStaysDroppedWhenTheJournalIsReplayed"),
        new("received", "Handled", [FlightCommanderReceive], "FlightMoveTests.AHandedFlightArrivesWithItsLegsReportsPrReviewAndOpenCardsOnAPlaneAtThePushedHead"),
        new("move-failed", "Ring", [Move], "FlightMoveTests.APushBehindTheRemoteLeavesTheFlightHereWithTheReasonAndARetryThatRunsTheMoveAgain", "the push of the flight's branch to hand it to the peer was refused, read from git", [nameof(Verbs.MoveFlight)]),
        new("planner-ended", "Handled", ["Flights/LegCommander.Planners.cs"], "PlannerWindDownTests.AnIdlePlannerOfAFlightWhoseLegFliesIsEndedAsPlannedAndItsSlotGoesToTheNextFlightOnTheSweep"),
        new("moved-away", "Handled", [Move], "FlightMoveTests.AHandMoveOutsideMaintenanceMovesTheFlightAsWell"),

        // PRs of the person's own, reviewed and rechecked.
        new("pr-review-ready", "ForYou", [PrReviewsVerdicts], "PrReviewTests.OnlyTheReviewersVerdictRaisesTheCardAndTheWorktreeGoesWhenTheCardCloses"),
        new("pr-review-waiting", "Handled|ForYou", [Reviews, PrReviewsSweep], "AdmissionReviewTests.APrReviewAtTheOpusCapAnswersQueuedLeavesOneHandledCardAndStartsOnTheSweepAndSaysItOnce"),
        new("pr-fix-pushed-unreviewed", "ForYou", [PrReviewsFixerRounds], "PrRecheckTests.TheFixersPushWhoseReviewCouldNotStartStaysAnOpenCardAfterTheSweep"),
        new("pr-review-started", "Handled", [PrReviewsStart], "PrReviewTests.APrInARepoWithACloneIsReviewedInThatClonePinnedAtItsHead"),
        new("pr-review-left", "Handled", [PrReviewsSweep], "RemovalQueueTests.AReviewWhoseWorktreeIsHeldClosesAtOnceWithOneHandledLineAndAQueueEntry"),
        new("pr-review-restarted", "Handled", [PrReviewsSweep], "PrReviewTests.AReviewingRowWhoseSessionIsGoneAfterARestartIsStartedAfreshAtTheFirstSweepOnce"),
        new("pr-review-lost", "ForYou", [PrReviewsSweep], "PrReviewTests.AReviewLostInARestartThatCannotStartAgainTakesItsStatusAndLabelBackAndRaisesACardWithReviewAgain"),
        new("pr-review-posted", "Handled", [PrReviewsVerdicts], "PrReviewTests.PostsItselfOnAnOpenPr"),
        new("pr-review-queued", "Handled", [PrReviewsQueue], "PrReviewTests.ACleanCommentOnOnesOwnPrQueuesItAtTheReviewedHeadOnceAndSaysSo"),
        new("pr-review-merge", "ForYou", [PrReviewsQueue], "PrReviewTests.ABaseWithoutAMergeQueueRaisesTheMergeCard"),
        new("pr-review-queue-refused", "ForYou", [PrReviewsQueue], SourcePinned),
        new("pr-review-design", "Ring", [PrReviewsFixer], "PrFixTests.ADesignQuestionRingsAndStartsNoFixer", "the reviewer's verdict on the PR names a design question, read from its review", [nameof(Verbs.FixPr)]),
        new("pr-review-design-settled", "Handled", [PrReviewsFixerRounds], "DesignSettledTests.AMergedPrDowngradesItsRingingDesignCardToANoticeNamingIt"),
        new("pr-review-no-fixer", "Ring", [PrReviewsFixer], "PrRecheckTests.ARecheckOfAChangesReviewWithNoFixerRingsTheOwnerOnceAndADismissalSticksUntilTheHeadMoves", "the review of the PR asks for changes and no fixer can start", [nameof(Verbs.FixPr)]),
        new("pr-fix-started", "Handled", [PrReviewsFixer], "PrFixTests.AChangesVerdictOnAnOwnPrInATrackedRepoStartsOneFixerOnThePrsBranch"),
        new("pr-fix-offer", "ForYou", [PrReviewsFixer], "PrFixTests.APrByAnotherLoginRaisesAForYouCardWhoseFixItStartsTheSameFixer"),
        new("pr-fix-failed", "ForYou", [PrReviewsFixer, PrReviewsFixerRounds], SourcePinned),
        new("pr-fix-capped", "Ring", [PrReviewsFixer], "PrFixTests.ChangesOnRoundThreeRingsTheRoundsAndStartsNoFourthFixer", "the PR's fix rounds have reached the cap with changes still asked", [nameof(Verbs.FixPr)]),
        new("pr-fix-ended", "ForYou", [PrReviewsFixerRounds], "PrFixTests.AFixerThatEndsWithoutPushingLeavesACardWithFixItAndNoReview"),
        new("pr-fix-left", "ForYou", [PrReviewsFixerRounds], SourcePinned),
        new("pr-fix-follow-up", "Handled", [PrReviewsFixerRounds], "FixFollowUpTests.AMergedPrsFixerCommitGoesToANewBranchAndAFollowUpPrThatIsReviewed"),
        new("pr-fix-follow-up-failed", "ForYou", [PrReviewsFixerRounds], "FixFollowUpTests.APushGitHubRefusesLeavesAForYouCardNamingTheCommitAndTheLocalBranch"),
        new("pr-recheck", "Handled", [Recheck], SourcePinned),
        new("pr-recheck-failed", "ForYou", [Recheck], "PrRecheckTests.AReviewThatCannotStartKeepsTheLabelAndRaisesAForYouCard"),
        new("pr-recheck-conflicts", "ForYou", [Recheck], "PrRecheckTests.AReviewedPrThatConflictsGetsAForYouCardAndTheConflictsLabel"),
        new("card-resolved", "Handled", ["Flights/StaleCards.cs"], "StaleCardTests.ASummaryRunLeavesACardItsCheckContradicts"),

        // The node itself, the watchtower and the person's own asks.
        new("watchtower-down", "Ring", [Tower], "WatchtowerTests.AnUntrustedFolderRingsWithTheFixAndIsTriedAgainLater", "the watchtower's folder is untrusted or its session did not start, read from its row", [nameof(Verbs.RestartWatchtower)]),
        new("watchtower-stopped", "Ring|Handled", [Tower], "WatchtowerTests.OneTheUsageLimitStoppedIsResumedAfterTheReset", "the watchtower's session row is stopped by the API with no reset to resume at", [nameof(Verbs.RestartWatchtower)]),
        new("watchtower-waits", "Handled", [Tower], "StartGateTests.TheWatchtowerKeptByTheNodeWaitsForTheWindowAndStartsOnceItHasResetButARestartAskedForGoesAhead"),
        new("watchtower-resumed", "Handled", [Tower], SourcePinned),
        new("watchtower-started", "Handled", [Tower], SourcePinned),
        new("watchtower-duplicate", "Handled", [Tower], "OneWatchtowerTests.StartingOneStopsAnotherLiveWatchtowerRowAndSaysSo"),
        new("watchtower-parked", "Handled", [Tower], SourcePinned),
        new("watchtower-churning", "ForYou", [Tower], "WatchtowerTests.ManyShortLivedWatchtowersMarkItChurningAndRaiseOneCardThatIsUpdatedAndClosed"),
        new("watchtower", "", ["NodeCommands.cs"], "WatchtowerTests.ARaiseIsACardFromWhoeverRaisedItAndATellNeedsAWatchtower"),
        new("persons-word", "Handled", ["NodeCommands.cs"], "PersonsWordTests.AFailedLegIsMarkedDoneAnywayAtThePersonsWord"),
        new("assistant-ask", "ForYou", ["Autopilot.cs"], "AutopilotTests.ASettingAnAssistantAsksForIsAskedNotSet"),
        new("autopilot-ran", "Handled", ["Autopilot.cs", Checks], "AutopilotTests.WithAutopilotOnTheSameMergeGoesThroughRecordedWithTheSessionAndMessageAndAHandledCardSaysIt"),
        new("grant-asked", "Ring", ["NodeLan.cs"], "NodeCardTests.AGrantAskIsARingCardWhoseChoicesSaySoInPlainWords", "the asking node's grant is not yet answered in the node's grants", [nameof(Verbs.AnswerGrant)]),
        new("update", "Ring|ForYou|Handled", ["Updates/NodeUpdater.cs"], "UpdateTests.AskRaisesACardWithUpdateOnce", "the published version is newer than the running node's", [nameof(Verbs.Update)]),
        new("restart", "Handled", ["NodeSettingsCommands.cs"], "NodeRestartTests.ARestartStartsTheSuccessorStopsTheNodeAndLogsAHandledLineNamingTheCaller"),
        new("store", "ForYou", ["NodeState.cs"], SourcePinned),
        new("stop-unconfirmed", "Ring", ["Sessions/SessionMessenger.cs"], "SessionEndTests.AStopThatDoesNotTakeRaisesOneRingCardAndIsNotConfirmed", "the session's row still shows it live after the stop was asked", [nameof(Verbs.EndSession)]),
        new("turns-outage", "ForYou", ["Flights/TurnOutage.cs"], "TurnOutageTests.AnAccountChangeOpensOneOutageWithOneCardAndATurnLaterEndsItAndKeepsTheWindow"),
        new("usage-high", "Handled", ["Sessions/UsageReader.cs"], "UsageTests.NearlySpentTheFiveHourWindowRaisesOneCardPerWindow"),
        new("usage-week", "ForYou", ["Flights/StartGate.cs"], "StartGateTests.TheWeekRingsOncePerWindowAndHoldsNothing"),
        new("removal-held", "ForYou", ["Flights/RemovalQueue.cs"], "RemovalQueueTests.AFolderHeldPastThreeStartsRingsOnceNamingTheHolderAndTheCardClosesOnceItIsReleased"),
        new("job-idle", "ForYou", ["Flights/JobRunner.cs"], "JobTests.ANearIdleJobWithAnotherQueuedBehindItRaisesOneCardNamingJobOwnerAndWaiter"),
        new("bench-result", "ForYou", ["Flights/BenchRun.cs"], "BenchRunTests.ASingleRefRunsOnceInADetachedShortNamedWorktreeAndTheCardHoldsItsTableUnderItsRefAndSha"),
        new("job-told-unanswered", "Ring", ["Flights/JobRunner.cs"], "JobRetellTests.IdleAndSilentIsRetoldOnceThenRings", "the job's owner was told once of its end and its session row shows no turn since", [nameof(Verbs.Say)]),
        new("job-bench-busy", "Ring", ["Flights/JobRunner.cs"], "BenchSlotTests.ABenchWaitingPastTheBoundForABusyCpuRingsOnceNamingTheProcessAndClosesWhenItStarts", "a process other than the node holds the CPU past the bench's bound, read from the process list", [nameof(Verbs.Say)]),
        new("job-untold", "Ring", ["Flights/JobRunner.cs"], "JobTests.AnOwnerThatCannotBeToldRings", "the job's owner session cannot be told (its row is gone or it refuses messages)", [nameof(Verbs.Say)]),
        new("queued", "Handled", ["Flights/NamedLock.cs"], "LockWaitTests.ALegCommandWaitingPastFiveSecondsIsAnsweredWithWhatHoldsTheLockAndRunsOnceItFrees"),
        new("session-compacted", "Handled", ["Flights/Compactor.cs"], "CompactorTests.AWorkerWaitingOnItsJobIsCompactedWithWhatItWaitsOnAndOneNotWaitingIsNot"),
        new("digest", "ForYou", ["Flights/Digest.cs"], "SignalTests.TheDigestListsWhatLandedWhatWasHandledAndWhatIsParkedAndReplacesTheLastOne"),
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
        ["Flights/Digest.cs|title"] = "Digest.Compose names what landed, what was handled and what is parked",
        ["Flights/LegWatch.cs|title"] = "the parameter of LegWatch.Ring, whose titles are the literals at each Ring( call, which the guard reads as it reads RaiseCard",
        ["Flights/LegCommander.Overlap.cs|title"] = "the overlap title built in LegCommander.Overlap names both flights, the files they share and the choice",
        ["Flights/LegCommander.Move.cs|title"] = "the move's own failure text: its first part is the reason, built where the move is refused",
        ["Flights/LegCommander.Move.cs|text"] = "the move's own line: who handed the flight to whom",
        ["Flights/PrReviews.Cards.cs|title"] = "the parameter of PrReviews.RaiseOnce, whose titles are the literals at each RaiseOnce( call, which the guard reads as it reads RaiseCard",
        ["Flights/PackageWatch.cs|late.Title"] = "PackageWatch.LateAsync names the package, its version and the run",
        ["Flights/FlightCommander.Receive.cs|card.Title"] = "the card a refused receive carried over from the node that raised it",
        ["Flights/FlightCommander.Receive.cs|way"] = "the line the routing or the move wrote: where the flight went and why",
        ["Flights/FlightCommander.Route.cs|way"] = "the line the routing or the move wrote: where the flight went and why",
        ["NodeCommands.cs|title"] = "the title the person, the watchtower or a session gave to pilot raise",
        ["NodeSettingsCommands.cs|line"] = "the line the restart wrote: who restarted the node and how",
        ["Updates/NodeUpdater.cs|line"] = "the line the install wrote: the version installed and the node restarted",
        ["Flights/PrChecks.cs|LegCommander.ChecksFailedTitle(flight.Code, number, checks?.FailedName, why)"] = "ChecksFailedTitle names the check and why the leg was not sent back",
        ["Flights/PrChecks.cs|LegCommander.ChecksFailedTitle(flight.Code, number, null, why)"] = "ChecksFailedTitle names the check and why the leg was not sent back",
        ["Flights/PrRecheck.cs|LegCommander.ChecksFailedTitle(code, entry.Number, null, why)"] = "ChecksFailedTitle names the check and why the leg was not sent back",
    };

    /// <summary>
    /// The kinds no test runs: a fragment their title must keep and, where they have a button, a fragment of its label. A title
    /// reworded to something generic breaks these as a pinning test would.
    /// </summary>
    public static readonly (string Kind, string TitleHas, string? LabelHas)[] SourcePins =
    [
        ("issue-left-open", "is still open", null),
        ("pr-review-queue-refused", "refused to queue", "again"),
        ("pr-fix-failed", "the fixer could not start", "Start the fixer on"),
        ("pr-fix-left", "fixer worktree is still there", null),
        ("pr-recheck", "recheck of", null),
        ("watchtower-resumed", "resumed after its usage limit reset", null),
        ("watchtower-started", "Started a fresh watchtower", null),
        ("watchtower-parked", "no flight in the air and no UI connected", null),
        ("store", "could not write its snapshot", null),
    ];
}
