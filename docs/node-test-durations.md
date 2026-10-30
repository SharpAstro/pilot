# Pilot.Node.Tests durations (PIL501, leg measure)

Measured 2026-10-31 on DE1 (Windows 11, win-x64, 16 cores), Release build, 2267 tests (2265 pass, 2 skip).

Commands (from the repo root, TRX written outside the checkout):

```
cd src && dotnet build Pilot.slnx -c Release
dotnet test src/Pilot.Node.Tests -c Release --no-build --logger 'trx;LogFileName=node.trx' --results-directory <dir>/run1
dotnet test src/Pilot.Node.Tests -c Release --no-build --logger 'trx;LogFileName=node.trx' --results-directory <dir>/run4 -- xUnit.MaxParallelThreads=4
```

The TRX files were read with a short python script (per-test duration, summed per class, longest test per class).

## Wall time

| Run | Wall time | Sum of per-test time | Result |
|---|---|---|---|
| Default parallelism | 33m57s | 7938 s | 2265 passed, 2 skipped |
| `xUnit.MaxParallelThreads=4` (stand-in for a smaller or shared machine like SE1) | 42m27s | 8441 s | 1 failed, 2264 passed, 2 skipped |

The sum of per-test time is about 4x the wall time at default parallelism, so roughly four tests run at once on average.
Capping to 4 threads cost only 25% more wall time and the per-test times barely moved: these tests are not stretched by
CPU contention, they are slow on their own. As a check, `ASmallDiffOrLittleHeadroom...`, `ASyncOfTheMainCheckout...` and
`ANearIdleJobWithNothingQueued...` ran together on a quiet machine in 64 s in total, i.e. each is about a minute alone.

The one failure in the capped run, `JobTests.AJobPastItsTimeoutIsStoppedWithItsWholeTreeAndSaysSo`, read `child.pid`
before the job's child had written it (`FileNotFoundException` at `JobTests.cs:400`, after 41 s). It passed in the default run. It is a
race on a real child process that a slower start exposes; the next legs should make that wait a poll for the file, not a read.

## Slowest 20 tests (default parallelism, seconds)

Capped-run time in brackets where it was also in that run's top 20 or is known.

| s | Test | Cause |
|---|---|---|
| 176.0 (62.8) | LfsTests.ASyncOfTheMainCheckoutFillsItsLfsFilesWithTheirContent | real git and git-lfs processes: bare origin, clone, `lfs install/track/pull`, push, reset, then a real `RepoSync` |
| 128.4 | LfsTests.AMadePlaneTransfersNoLfsAndLeavesTheStoreAlone | same real git-lfs setup (`MakeRepoWithLfs`), then a real plane made |
| 113.6 | LfsTests.APushWithNoLfsChangeSkipsThePrePushUploadAndOneThatChangesLfsKeepsIt | real git-lfs setup, a real `HttpListener` LFS server, real pushes |
| 109.5 (88.3) | ReviewTests1.ASmallDiffOrLittleHeadroomIsAStandardReviewOnTheReviewersOwnModel | two `Review()` calls, each a full flight with a repo copy, plane, commit, open-pr: real git processes |
| 108.1 (74.2) | LfsTests.APushOfAMergeWhoseResolutionChangesAnLfsFileKeepsThePrePushUpload | real git-lfs setup, merge, pushes |
| 85.4 | ReviewTests3.AFlightAlreadyPushedWhoseBaseNeedsRepairHasTheBaseMergedInAndIsPushedPlain... | flight flown with real git: worktree, commits, merge of base, push |
| 72.9 / 70.7 | ReviewTests3.ACommitPushedToTheBranchFromElsewhere...(number "18" and "19") | theory rows, each a full flight plus a second clone pushing; real git |
| 70.3 (67.7) | JobTests.AJobThatKeepsWorkingRaisesNoCardAndTheCardClosesWhenTheQueueEmpties | fake clock, but `Hand` starts a real `sleep 60` shell job; a real process start and teardown per job |
| 67.8 | ReviewTests3.AMergeOnAPrBehindMainUpdatesItKeepsItsPassedReview... | flight with real git, base moved and merged in |
| 65.5 | ReviewTests3.AnUpdatedHeadWithPendingChecksIsNotQueuedByTheMergeOfferedAgain | flight with real git, second push |
| 63.9 (64.4) | JobTests.ANearIdleJobWithNothingQueuedBehindItRaisesNoCard | real `sleep 60` shell job under a fake clock |
| 63.8 (66.6) | JobTests.AJobWhoseCpuReadingFallsWhenAChildEndsAndRisesAgainRaisesNoCard | same |
| 63.1 | PrRecheckTests.AFlightsPrWithNothingToDoSaysSoAndLeavesTheLabelForWhereItStands | `PrOrigins.Make` plus clone per test, a passed flight flown with real git |
| 60.3 | PrRecheckTests.AFlightsPrWhoseBaseMergeConflictsIsHandedToTheWatchtower... | same, with a real conflicting merge |
| 59.4 | ReviewTests1.AConflictingPrIsLabelledConflictsWhateverItsReviewSaid... | flight with real git and a conflict |
| 59.1 | ReviewTests1.APrDroppedFromTheQueueOnAConflictHasTheBaseMergedIn... | flight with real git, base merged in |
| 58.4 (51.6) | PrFixTests3.ChangesOnRoundThreeRingsTheRoundsAndStartsNoFourthFixer | three rounds of flight and go-around with real git |
| 54.3 | ReviewTests3.AnArmedMergeAtGreenOnAPrBehindMainUpdatesItAndDropsNoMerge | flight with real git, armed merge, base merge |
| 53.5 (51.7) | ReviewTests1.APriorityRepoIsNeverReviewedLightly | flights with real git |

Also slow in the capped run only: ReviewTests3.TwoConnectedFlightsLandWithNoPilotOfTheirOwn (72.0 s), PrRecheckTests.AFlightsPrBehindItsBase... (67.8 s),
CliTests.ADirectFlightIsFlownFromTheCliAloneUpToItsLanding (52.8 s), LockFreeGitTests.AnUpdateHeldInAFetch... (52.1 s).

The Cause column for the review and recheck tests is read from the test base (`ReviewTestsBase`: `SeedRepo.Make`, `Fly`, real `git` via
`Process.Start`) and for the job tests from `Idle()`/`Hand()`; it was not profiled below that, so how the ~60 s splits between process starts, Defender
scanning and the file tree copies is still to be measured by the first fix leg.

## Slowest 15 classes by summed time (default parallelism)

xunit runs the tests of one class serially, so a class's summed time is also the shortest time it can take. Capped-run sum in brackets.

| Sum s | Tests | Longest s | Class |
|---|---|---|---|
| 1114.7 (870.1) | 34 | 85.4 | ReviewTests3 |
| 668.4 (762.2) | 34 | 109.5 | ReviewTests1 |
| 581.9 (705.6) | 28 | 63.1 | PrRecheckTests |
| 526.1 (211.9) | 5 | 176.0 | LfsTests |
| 417.2 (516.2) | 59 | 70.3 | JobTests |
| 349.9 (375.1) | 32 | 28.5 | FlightMoveTests |
| 298.6 (226.0) | 18 | 26.8 | ReviewTests2 |
| 219.7 | 11 | 35.0 | PackageWaitTests1 |
| 206.2 (163.8) | 29 | 30.6 | LegCommanderTests2 |
| 188.6 | 22 | 25.2 | LegCommanderTests1 |
| 187.3 (319.9) | 19 | 14.6 | ConnectTests |
| 182.7 | 11 | 27.9 | PrMarksTests |
| 146.9 | 6 | 51.5 | CardSummaryRaiserTests |
| 135.3 (245.3) | 8 | 35.6 | LockFreeGitTests |
| 124.8 | 5 | 58.4 | PrFixTests3 |

The three review and recheck classes alone are 2365 s of the 7938 s total, and ReviewTests3 alone runs for about 19 minutes serially, a floor on the suite wall time until it is split.
`NodeLogTests` is a `DisableParallelization` collection; it is not in the top 15 and was not what held the run.

## Fixes by cause, for the next legs

1. **A repo copy and real git per flight (the review, recheck, fix and merge classes).** Most of the top 20. Ways to cut it: give
   `ReviewTestsBase` a fake for the git work the test is not about (the tests assert on cards, labels, marks and briefs, not on git history); keep real git
   only in the few tests that assert on a push or a merge. Where state allows, build one seeded repo per class, not per test (`SeedRepo` already builds one template and copies it).
   Measure where a flight's time goes first (process starts vs file copies), since each flight is about 30 to 40 s.
2. **Serial long classes.** Split ReviewTests3, ReviewTests1, PrRecheckTests and JobTests into more classes (as `ReviewTests1/2/3` already was)
   so xunit runs the parts side by side; the target is no class above about 150 s summed. This helps wall time at once and costs no test change.
3. **git-lfs setup (LfsTests, 5 tests, 526 s).** The five tests repeat `MakeRepoWithLfs`. Build the LFS origin once per class (a template copied per test, like `SeedRepo`),
   and put the class in its own collection so its real git-lfs processes do not stack with others.
4. **Real `sleep 60` shell jobs under a fake clock (JobTests idle-card tests, about 64 to 70 s each).** The clock is already injected, so the real child
   is only a job to exist. Use a short or fake process (or an injected process runner) so the test does not start and stop a real shell for each.
5. **Fixed `Task.Delay` waits (JobTests has 31 `Task.Delay` calls, up to 2000 ms; RestartTests, LegTests and CliTests hold similar ones).** Replace with polls on the state
   awaited, or shorter injected intervals; `Pilot.Node` has few `TimeProvider` uses, so some intervals need one added before a test can fake them.
6. **Flaky wait on a child's file (`AJobPastItsTimeout...`).** Poll for `child.pid` before reading it; seen once in the capped run.
7. **Per-test node start-up (`NodeHarness.StartAsync`).** Start one node per class where a class's tests do not share state; not a top-20 cause by itself, but it is the
   floor under the many tests of 5 to 30 s in FlightMoveTests, LegCommanderTests, ConnectTests and PrMarksTests.

## After fix-waits (PIL501, leg fix-waits)

Cause found for the "real `sleep 60` under a fake clock" tests: their `JobRunner` is never started, so `StopAsync` never cancels the job
watches and `Dispose` waited out the real `sleep 60`. The clock was already injected; no new `TimeProvider` or options interval was needed for them.
The three tests now end their job with the existing `gone(first)` helper before `StopAsync`; their assertions are unchanged.

| Test | Before s | After s |
|---|---|---|
| JobTests.ANearIdleJobWithNothingQueuedBehindItRaisesNoCard | 63.9 | about 1 |
| JobTests.AJobThatKeepsWorkingRaisesNoCardAndTheCardClosesWhenTheQueueEmpties | 70.3 | about 1 |
| JobTests.AJobWhoseCpuReadingFallsWhenAChildEndsAndRisesAgainRaisesNoCard | 63.8 | about 1 |
| JobTests.AJobPastItsTimeoutIsStoppedWithItsWholeTreeAndSaysSo | 5+ (flaky) | about 10 |

The last one is a real child process under a real job timeout; the timeout rose from 5 s to 10 s and the `child.pid` poll from 15 s to 60 s,
because a loaded machine (a full run, 16 tests at once) failed it twice: the job was stopped before its child wrote `child.pid`. It cannot be made
fast without changing what it proves, so it is left for the guard leg to tag as slow.

JobTests as a class: 59 passed in 4m08s alone (417 s summed before). Its remaining time is real processes (`sleep`, pwsh) and polls
for them; the other `Task.Delay` waits in JobTests, RestartTests, LegTests, CliTests, CommandReplyTests and LockWaitTests were not changed in this leg
and stay on the list for a later leg, as does everything git-bound (the Review, Recheck, Lfs, PrFix classes), which is not a timer cause.

Full Pilot.Node.Tests with the first version of the fix (shared machine, other flights running): 2264 passed, 1 failed (the timeout test above), 41m10s wall.
