namespace Pilot.Node.Tests;

/// <summary>
/// One kind of card the node raises: its kind word, the bins it is raised into (<c>Ring</c>, <c>ForYou</c>, <c>Handled</c>, joined by
/// <c>|</c>), the Pilot.Node files that raise it, and the test that pins its title and choices (<c>Class.Method</c> in this project,
/// which must exist and name the kind). A kind nothing runs pins is pinned by <see cref="CardKindCatalog.SourcePins"/> instead, in
/// <c>CardKindTests.SourcePinned</c>.
/// </summary>
internal sealed record CardKindEntry(string Kind, string Bins, string[] Files, string Pin);

/// <summary>
/// Every kind of card the node raises (PIL474). <c>CardKindTests</c> reads the <c>RaiseCard</c> calls under Pilot.Node and fails when one
/// is not here, when a kind here is no longer raised, or when a title or a button says nothing specific; a new card is added here
/// with its pinning test, which is the point.
/// </summary>
internal static class CardKindCatalog
{
    public const string SourcePinned = "CardKindTests.SourcePinned";

    private const string Leg = "Flights/LegCommander.cs";
    private const string Lay = "Flights/LegCommander.Lay.cs";
    private const string Watch = "Flights/LegWatch.cs";
    private const string Flight = "Flights/FlightCommander.cs";
    private const string Move = "Flights/LegCommander.Move.cs";
    private const string Checks = "Flights/PrChecks.cs";
    private const string Reviews = "Flights/PrReviews.cs";
    private const string Recheck = "Flights/PrRecheck.cs";
    private const string Tower = "Flights/Watchtower.cs";

    public static readonly CardKindEntry[] All =
    [
        // Workers, legs and planners.
        new("worker-stopped", "Ring", [Watch], "LegWatchTests.AStopWithNoResetTimeRingsAtOnce"),
        new("worker-limit-wait", "Handled", [Watch], "LegCardTests.AUsageLimitStopTheNodeResumesItselfIsAHandledLineAndNoCard"),
        new("worker-idle", "Ring", [Watch], "LegWatchTests.ANudgeTheNodeCannotDeliverRings"),
        new("worker-waiting", "Ring", [Watch], "LegWatchTests.AWaitingWorkerRingsWithItsQuestionAndTheCardClosesWhenItWorksAgain"),
        new("worker-failed", "Ring", [Watch], "LegWatchTests.AFailedWorkerAndOneThatNeverShowedUpRing"),
        new("worker-missing", "Ring", [Watch], "LegWatchTests.AFailedWorkerAndOneThatNeverShowedUpRing"),
        new("worker-nudged", "Handled", [Watch], "LegWaitTests.AnIdleWorkerWhoseLongWaitEndsIsWokenOnceAndNotNudgedOnTopOfIt"),
        new("worker-resumed", "Handled", [Watch], "LegWatchTests.AWorkerTheUsageLimitStoppedIsResumedOnlyOnceItsLimitHasReset"),
        new("over-budget", "Ring", [Watch], "SignalTests.AWorkerPastItsBudgetRingsUntilTheBudgetIsRaised"),
        new("review-stalled", "Ring|Handled", [Watch], "LegWatchTests.AReviewerThatStopsWithoutItsVerdictIsToldThenReviewedAfreshAndOnlyThenRings"),
        new("validation-stalled", "Ring", [Watch], "LegWatchTests.AValidatorThatStopsWithoutItsAnswerRingsWithGoAnywayAndRemove"),
        new("plan-stalled", "Ring|Handled", [Watch], "LegWatchTests.AnIdlePlannerWithNoLegsIsToldToHandThemInThenRungNeverReplaced"),
        new("session-waiting", "Ring|ForYou", [Watch], "WaitingCardTests.TheCardClosesWhenTheSessionWorksAgain"),
        new("leg-wait-late", "Ring", [Watch], "LegWaitTests.AFileWaitPastItsUntilRingsOnceAndKeepsWaiting"),
        new("leg-failed", "Ring", [Leg], "PushBlockedTests.AFailedLegWhoseCheckFailsGetsTheOrdinaryCardNotThePushBlockedOne"),
        new("push-blocked", "Ring", [Leg], "PushBlockedTests.ARebasedBranchWithAPassingCheckRaisesAPushBlockedCardNamingTheForcePush"),
        new("push-diverged", "Ring", [Leg], "PushTests.A_conflicting_remote_commit_is_refused_with_both_heads_and_the_remote_is_left_as_it_was"),
        new("leg-self-finished", "Handled", [Leg], "LegSelfFinishTests.A_failed_leg_with_its_work_pushed_and_its_check_passing_is_finished_by_the_node"),
        new("leg-no-go", "Ring", [Leg], "CardSummaryRaiserTests.TheNoGoSummaryNamesTheChoicesTheCardOffers"),
        new("leg-validating", "Handled", [Leg], "ValidatorTests.ALaterLegStartsAValidatorOnTheWorkerModelAtLowEffortAndNoWorker"),
        new("leg-removed", "Handled", [Leg], "LegTests.APlannedLegIsRemovedTheLegsAfterItKeepTheirOrderAndAHandledCardSaysWhy"),
        new("leg-holding", "Handled", ["Flights/LegCommander.Hold.cs"], "LegHoldTests.AWorkerFailingOnAPackageASiblingIsPublishingHoldsItsLegWithNoRingAndItsWorkerStopped"),
        new("start-failed", "Ring", ["Flights/LegCommander.Launch.cs"], "LockFreeStartTests.AStartThatFailsAfterTheAnswerClearsItsReservationAndSaysSoOnACard"),
        new("next-step", "Ring|Handled", [Leg, Flight], "FlightMoveTests.AnAdvanceThatThrowsAfterTheReceiveIsACardNotAFailedReceive"),
        new("sibling-behind", "ForYou", [Leg], "LegTests.ALegStartedWithASiblingBehindItsOriginRaisesOneCardNamingItAndThePullAndStillStarts"),
        new("sibling-pulled", "Handled", [Leg], "LegTests.AStaleSiblingWhoseOriginWentUnreachableAfterTheCountIsStillPulled"),
        new("planner-started", "Handled", [Leg], "LockFreeStartTests.APlanFlightAnswersStartedAtOnceAndItsCardComesWhenThePlannerIsUp"),
        new("planned", "ForYou", [Leg], "LegTests.APlannerIsStartedInThePlaneAndItsPlannedOffersTheFirstLeg"),
        new("overlap", "Ring", ["Flights/LegCommander.Overlap.cs"], "OverlapTests.TheRecordedAnswerSurvivesAReplayOfTheJournal"),
        new("flown-elsewhere", "Ring", [Leg], "OneNodePerFlightTests.APlanOfAFlightWhoseBranchAPeerFliesIsRefusedWithARingNamingThePeer"),
        new("pilot-md-too-long", "Ring", [Leg], "PilotMdTests.APilotMdOverTheLimitIsNotQuotedAndRaisesOneCardNamingTheRepoTheFlightAndTheLineCount"),
        new("stopped-uncommitted", "Ring", ["Flights/StopGate.cs"], "StopGateTests.TheHookPrintsTheNodesDecisionAndNothingOnceLetThrough"),
        new("package-late", "Ring", ["Flights/PackageWatch.cs"], "PackageWaitTests.APublishRunThatFailedRaisesTheCardAtOnce"),
        new("admission-cycle", "Handled", ["Flights/AdmissionGate.cs"], "LegAdmissionTests.AFlightAdmittedToBreakACycleRaisesOneCardNamingBothFlights"),
        new("spend-alert", "Ring", [Leg], "SpendAlertTests.ALegPastTheAlertShareRingsWithItsTokensAndShareBeforeItsNextRoundOncePerRound"),
        new("review-kept", "Handled", [Leg], "ReReviewTests.AHeadThatOnlyMergedTheBaseKeepsThePassWithNoReviewerStarted"),
        new("review-notes", "ForYou", [Leg], "ReviewModelTests.APassWithNotesRaisesTheWatchtowerCardAndSendsNoLegRound"),
        new("review-loop", "ForYou", [Leg], "ReviewLoopTests.TheThirdRoundWithChangesRaisesAWatchtowerCardWithEachRoundsFindings"),
        new("stray-session", "Ring", ["Flights/LegWatch.cs"], "WindDownTests.TheSweepRingsOnceForALiveSessionOfALandedFlightAndOnceForOneTheLedgerDoesNotKnow"),

        // Flights: landing, moving, PRs.
        new("review-changes", "Ring", [Leg], "CardSummaryRaiserTests.AReviewThatAsksForChangesSaysWhichLegsFindingsAreFixedNotRedone"),
        new("laid-on-base", "Handled", [Lay], "RebaseLayFlightTests.ABranchThatMergesCleanlyButDoesNotRebaseIsLaidOnItsBaseAsOneCommitAndQueuedOnce"),
        new("lay-failed", "Ring", [Lay], "RebaseLayFlightTests.AMergeThatIsNotCleanPushesNothingAndRingsOnce"),
        new("drop-unexplained", "Handled|Ring", [Lay], "RebaseLayFlightTests.ADroppedPrWhoseCauseIsNotFoundRingsOnceAndIsNeverQueuedAgain"),
        new("pr-ready", "Ring", [Leg, Lay], "PrFlightReviewTests.AnApproveAtTheFlightsHeadPassesItsReviewAndMarksItsPrReady"),
        new("pr-conflicts", "Ring", [Leg], "NodeCardTests.ThePrCheckAndConflictSummariesNoLongerSendSomeoneToTheFlightPage"),
        new("pr-checks-failed", "Ring", [Checks, Recheck], "NodeCardTests.ThePrCheckAndConflictSummariesNoLongerSendSomeoneToTheFlightPage"),
        new("merge-taken-back", "Handled", [Leg], "MergeTakeBackTests.PlanLegOnAQueuedMergeDequeuesAndTheCardSaysSo"),
        new("merged-under-leg", "Ring", [Flight], "MergedUnderLegTests.ADoneLegGoesToANewPrWithTheMergedBaseMergedInAndPushedPlain"),
        new("not-landed", "Ring", [Checks], "MergedUnderLegTests.AMergeSeenWithUncommittedChangesKeepsThePlaneAndRings"),
        new("closed-landed", "Handled", [Checks], "ReviewTests.AFlightWhosePrWasClosedWithNothingOfItsOwnLeftLandsAndOneWithWorkRings"),
        new("closed-with-work", "Ring", [Checks], "ReviewTests.AFlightWhosePrWasClosedWithWorkOfItsOwnRingsOnceAndStaysFlying"),
        new("queue-dropped", "ForYou|Handled", [Checks], "ReviewTests.APrThatLeftTheMergeQueueUnmergedIsACardSayingWhyWithMergeAgain"),
        new("checks-rerun", "Handled", [Checks], "ReviewTests.OnAutopilotFailedChecksRunAgainOnceAHeadBeforeTheLegIsSentRound"),
        new("merge-dropped", "ForYou", [Checks], "LegTests.AWaitingMergeWhoseChecksFailIsDroppedOnACardThatOffersItAgain"),
        new("autopilot-merge-refused", "Ring", [Checks], "ReviewTests.AnAutopilotMergeRefusedRingsOnceAndItsCardClosesWhenTheBaseIsMergedInAndItsPassCarriedOver"),
        new("pr-marks-refused", "ForYou", [Leg, "Flights/PrMarks.cs", Reviews], "PrMarksTests.AWriteGitHubRefusesIsACardAndNeverFailsTheReviewOrLosesTheVerdict"),
        new("node-label-refused", "ForYou|Handled", [Flight, Move, Leg], "PrMarksTests.ANodeLabelGitHubRefusesOnTheNewPrIsACardNotAFailedPr"),
        new("landed", "Handled", [Flight], "CliTests.ADirectFlightIsFlownFromTheCliAloneUpToItsLanding"),
        new("issue-left-open", "ForYou", [Flight], SourcePinned),
        new("plane-left", "ForYou|Handled", [Flight, Move], "RemovalQueueTests.ALandedFlightWhosePlaneFolderIsHeldSaysSoOnAHandledLineAndQueuesIt"),
        new("folded", "Handled", [Flight], "OverlapTests.FoldRetiresTheAskingFlightLandedAndMarkedFoldedWithNoLegsAndItsPlaneGoneButItsBranchKept"),
        new("routed", "Handled", [Flight], "FleetRoutingTests.ARoutedFlightIsMadeOnTheWillingPeerWithItsOwnCheckoutAndSaysWhere"),
        new("disconnected", "Handled", [Flight], "DisconnectTests.TheDroppedConnectionStaysDroppedWhenTheJournalIsReplayed"),
        new("received", "Handled", [Flight], "FlightMoveTests.AHandedFlightArrivesWithItsLegsReportsPrReviewAndOpenCardsOnAPlaneAtThePushedHead"),
        new("move-failed", "Ring", [Move], "FlightMoveTests.APushBehindTheRemoteLeavesTheFlightHereWithTheReasonAndARetryThatRunsTheMoveAgain"),
        new("moved-away", "Handled", [Move], "FlightMoveTests.AHandMoveOutsideMaintenanceMovesTheFlightAsWell"),

        // PRs of the person's own, reviewed and rechecked.
        new("pr-review-ready", "ForYou", [Reviews], "PrReviewTests.OnlyTheReviewersVerdictRaisesTheCardAndTheWorktreeGoesWhenTheCardCloses"),
        new("pr-review-waiting", "Handled|ForYou", [Reviews], "AdmissionReviewTests.APrReviewAtTheOpusCapAnswersQueuedLeavesOneHandledCardAndStartsOnTheSweepAndSaysItOnce"),
        new("pr-fix-pushed-unreviewed", "ForYou", [Reviews], "PrRecheckTests.TheFixersPushWhoseReviewCouldNotStartStaysAnOpenCardAfterTheSweep"),
        new("pr-review-started", "Handled", [Reviews], "PrReviewTests.APrInARepoWithACloneIsReviewedInThatClonePinnedAtItsHead"),
        new("pr-review-left", "Handled", [Reviews], "RemovalQueueTests.AReviewWhoseWorktreeIsHeldClosesAtOnceWithOneHandledLineAndAQueueEntry"),
        new("pr-review-restarted", "Handled", [Reviews], "PrReviewTests.AReviewingRowWhoseSessionIsGoneAfterARestartIsStartedAfreshAtTheFirstSweepOnce"),
        new("pr-review-lost", "ForYou", [Reviews], "PrReviewTests.AReviewLostInARestartThatCannotStartAgainTakesItsStatusAndLabelBackAndRaisesACardWithReviewAgain"),
        new("pr-review-posted", "Handled", [Reviews], "PrReviewTests.PostsItselfOnAnOpenPr"),
        new("pr-review-queued", "Handled", [Reviews], "PrReviewTests.ACleanCommentOnOnesOwnPrQueuesItAtTheReviewedHeadOnceAndSaysSo"),
        new("pr-review-merge", "ForYou", [Reviews], "PrReviewTests.ABaseWithoutAMergeQueueRaisesTheMergeCard"),
        new("pr-review-queue-refused", "ForYou", [Reviews], SourcePinned),
        new("pr-review-design", "Ring", [Reviews], "PrFixTests.ADesignQuestionRingsAndStartsNoFixer"),
        new("pr-review-no-fixer", "Ring", [Reviews], "PrRecheckTests.ARecheckOfAChangesReviewWithNoFixerRingsTheOwnerOnceAndADismissalSticksUntilTheHeadMoves"),
        new("pr-fix-started", "Handled", [Reviews], "PrFixTests.AChangesVerdictOnAnOwnPrInATrackedRepoStartsOneFixerOnThePrsBranch"),
        new("pr-fix-offer", "ForYou", [Reviews], "PrFixTests.APrByAnotherLoginRaisesAForYouCardWhoseFixItStartsTheSameFixer"),
        new("pr-fix-failed", "ForYou", [Reviews], SourcePinned),
        new("pr-fix-capped", "Ring", [Reviews], "PrFixTests.ChangesOnRoundThreeRingsTheRoundsAndStartsNoFourthFixer"),
        new("pr-fix-ended", "ForYou", [Reviews], "PrFixTests.AFixerThatEndsWithoutPushingLeavesACardWithFixItAndNoReview"),
        new("pr-fix-left", "ForYou", [Reviews], SourcePinned),
        new("pr-recheck", "Handled", [Recheck], SourcePinned),
        new("pr-recheck-failed", "ForYou", [Recheck], "PrRecheckTests.AReviewThatCannotStartKeepsTheLabelAndRaisesAForYouCard"),
        new("pr-recheck-conflicts", "ForYou", [Recheck], "PrRecheckTests.AReviewedPrThatConflictsGetsAForYouCardAndTheConflictsLabel"),
        new("card-resolved", "Handled", ["Flights/StaleCards.cs"], "StaleCardTests.ASummaryRunLeavesACardItsCheckContradicts"),

        // The node itself, the watchtower and the person's own asks.
        new("watchtower-down", "Ring", [Tower], "WatchtowerTests.AnUntrustedFolderRingsWithTheFixAndIsTriedAgainLater"),
        new("watchtower-stopped", "Ring|Handled", [Tower], "WatchtowerTests.OneTheUsageLimitStoppedIsResumedAfterTheReset"),
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
        new("grant-asked", "Ring", ["NodeLan.cs"], "NodeCardTests.AGrantAskIsARingCardWhoseChoicesSaySoInPlainWords"),
        new("update", "Ring|ForYou|Handled", ["Updates/NodeUpdater.cs"], "UpdateTests.AskRaisesACardWithUpdateOnce"),
        new("restart", "Handled", ["NodeSettingsCommands.cs"], "NodeRestartTests.ARestartStartsTheSuccessorStopsTheNodeAndLogsAHandledLineNamingTheCaller"),
        new("store", "ForYou", ["NodeState.cs"], SourcePinned),
        new("usage-high", "Handled", ["Sessions/UsageReader.cs"], "UsageTests.NearlySpentTheFiveHourWindowRaisesOneCardPerWindow"),
        new("usage-week", "Ring", ["Flights/StartGate.cs"], "StartGateTests.TheWeekRingsOncePerWindowAndHoldsNothing"),
        new("removal-held", "ForYou", ["Flights/RemovalQueue.cs"], "RemovalQueueTests.AFolderHeldPastThreeStartsRingsOnceNamingTheHolderAndTheCardClosesOnceItIsReleased"),
        new("job-idle", "ForYou", ["Flights/JobRunner.cs"], "JobTests.ARealCommandWithASleepAmongOtherWorkIsNotRefused"),
        new("job-untold", "Ring", ["Flights/JobRunner.cs"], "JobTests.AnOwnerThatCannotBeToldRings"),
        new("queued", "Handled", ["Flights/NamedLock.cs"], "LockWaitTests.ALegCommandWaitingPastFiveSecondsIsAnsweredWithWhatHoldsTheLockAndRunsOnceItFrees"),
        new("session-compacted", "Handled", ["Flights/Compactor.cs"], "CompactorTests.AWorkerWaitingOnItsJobIsCompactedWithWhatItWaitsOnAndOneNotWaitingIsNot"),
        new("digest", "ForYou", ["Flights/Digest.cs"], "SignalTests.TheDigestListsWhatLandedWhatWasHandledAndWhatIsParkedAndReplacesTheLastOne"),
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
        "Flights/StartGate.cs|kind",
        "Flights/FlightCommander.cs|card.Kind",
        "Flights/PrReviews.cs|kind",
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
        ["Flights/PrReviews.cs|title"] = "the parameter of PrReviews.RaiseOnce, whose titles are the literals at each RaiseOnce( call, which the guard reads as it reads RaiseCard",
        ["Flights/PackageWatch.cs|late.Title"] = "PackageWatch.LateAsync names the package, its version and the run",
        ["Flights/FlightCommander.cs|card.Title"] = "the card a refused receive carried over from the node that raised it",
        ["Flights/FlightCommander.cs|way"] = "the line the routing or the move wrote: where the flight went and why",
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
        ("pr-review-queue-refused", "refused to queue", "once more at"),
        ("pr-fix-failed", "the fixer could not start", "Start the fixer on"),
        ("pr-fix-left", "fixer worktree is still there", null),
        ("pr-recheck", "recheck of", null),
        ("watchtower-resumed", "resumed after its usage limit reset", null),
        ("watchtower-started", "Started a fresh watchtower", null),
        ("watchtower-parked", "nothing to watch", null),
        ("store", "could not write its snapshot", null),
    ];
}
