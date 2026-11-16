# Fixtures/gh: what gh and GitHub answered

Every reader in `GhCli` that parses gh's JSON has its parsing half callable on a string (`GhCli.Parse*`) and its `--json` field
list in one constant (`GhCli.*Fields`, or the GraphQL query constant). A fake `IGitHub` is built by running a recording through
the real parser, so it carries exactly what the real reader fills and nothing a hand-built record could invent (pilot #1106).

`GhRecordingTests` parses each recording with the real parser and pins the result. `GhFieldListTests` compares each recording's
keys with the method's field list, so a field added to one and not the other fails a unit test instead of surfacing at run time.
Neither test runs gh; the recordings are plain files.

## Layout

One folder per method, one file per state the method can return. The csproj copies `Fixtures\gh\**\*.json`.

| Folder | Method | Field list | Recorded or shaped |
| --- | --- | --- | --- |
| `pull-request-by-branch/` | `PullRequestAsync` | `PullRequestByBranchFields` | `draft`, `merged`, `closed` recorded |
| `pull-request-by-number/` | `PullRequestOfAsync` | `PullRequestByNumberFields` | `draft`, `merged`, `closed`, `cross-repository` recorded (the last from a public fork PR in dotnet/runtime) |
| `merged-pull-requests/` | `MergedPullRequestsAsync` | `MergedPullRequestFields` | `merged` recorded; `none` is `[]` |
| `open-pull-requests/` | `OpenPullRequestsAsync` | `OpenPullRequestFields` | `draft` recorded (the one open PR at the time); `none` is `[]` |
| `workflow-runs/` | `WorkflowRunsAsync` | `WorkflowRunFields` | `completed` recorded; `none` is `[]` |
| `checks/` | `ChecksSummaryAsync` | `ChecksFields` | `passed`, `failed` recorded |
| `merge-state/` | `MergeStateAsync` | `MergeStateFields` | `merged`, `draft`, `closed` recorded; the files directly in `gh/` (`ready.json`, `armed.json`, `behind.json` and the rest, used by `MergeStateTests`) are the earlier set: `pending` and `merged` of those were recorded (#249, #242), the others shaped |
| `auto-merge-armed/` | `AutoMergeArmedAsync` | `AutoMergeArmedFields` | `not-armed` recorded; `armed` shaped (its object cut from `gh/armed.json`) |
| `gh/queue-*.json` | `MergeQueueAsync` | `QueueStateQuery` (GraphQL) | `queue-queued`, `queue-idle`, `queue-dropped` shaped as GitHub answers them |

A recording keeps gh's keys and value shapes exactly. A GraphQL recording is compared the other way: every key it has must be in
the query, and every field the query selects must show in at least one recording (an idle PR has no queue position).

## Refreshing a recording

A manual step, never run by a test. Pass exactly the method's field list; `<n>` is a PR in the state wanted, `<sha>` a commit.

```
gh pr view <n> -R SharpAstro/pilot --json number,state,headRefOid,url,mergeable,labels,mergeCommit,mergedAt,isDraft   # pull-request-by-branch
gh pr view <n> -R SharpAstro/pilot --json number,state,headRefOid,url,title,author,baseRefName,headRefName,isDraft,isCrossRepository   # pull-request-by-number
gh pr list -R SharpAstro/pilot --state merged --limit 5 --json number,headRefName,headRefOid   # merged-pull-requests
gh pr list -R SharpAstro/pilot --state open --limit 100 --json number,title,isDraft,headRefName,url,statusCheckRollup,labels   # open-pull-requests
gh run list -R SharpAstro/pilot --commit <sha> --limit 30 --json databaseId,workflowName,status,conclusion,url,event,updatedAt   # workflow-runs
gh pr checks <n> -R SharpAstro/pilot --json bucket,name,link   # checks (exits non-zero on a failed check; the JSON is still printed)
gh pr view <n> -R SharpAstro/pilot --json state,isDraft,mergeStateStatus,mergeable,reviewDecision,autoMergeRequest,statusCheckRollup   # merge-state
gh pr view <n> -R SharpAstro/pilot --json autoMergeRequest   # auto-merge-armed
```

The cross-repository recording is `gh pr view <n> -R <any public repo> --json <the by-number list>` on a PR from a fork.
The merge queue's recordings come from `gh api graphql` with `GhCli.QueueStateQuery` (`-F owner=... -F name=... -F number=...`).

## Stripping rule

Keep the keys and value shapes exactly as gh gave them. Replace the login of anyone outside the org (`sebgod`, `SharpAstro`, bots)
with `some-user`, blank their `name`, and replace their node `id` with a placeholder; drop tokens and anything else private.
Compact the JSON to one line, as gh prints it when piped.

## Builders

`GhAnswers` (beside the tests) is the only place a test builds what a gh reader returns. A builder takes a recording state and named
overrides, edits only keys in the method's field list (a key outside it throws, naming the method and the list), and returns what the
real `Parse*` makes of the edited JSON. `GhFakeGuardTests` (`Category=Invariant`) fails a direct `new PullRequest(`, `new MergedPullRequest(`,
`new MergeQueueState(`, `new ChecksSummary(`, `new PullRequestStatus`, `new WorkflowRun(`, `new CiRun(` or a target-typed `new(` assigned to
those types in `Pilot.Node.Tests`, naming file and line, outside `GhAnswers.cs`, the guard itself, `GhRecordingTests` and `MergeQueueTests` (parser expectations); `NoFileIsAddedToTheAllowedList` pins that list. `Pilot.Gui.Tests` is
out of scope: its page data builds wire contracts, not gh reads.

## Fakes found impossible (PIL1106)

Per test whose fake could not exist: what the fake set that the reader never fills, what its expectation is now, and whether the code under test was wrong too.
Later legs append here; the PR body is drawn from this section.

- `LegTests.FakeGitHub.ChecksSummaryAsync` (the `Checks` shortcut, used by every test that sets `_github.Checks`): answered `new ChecksSummary(status, 0, 0)`,
  a status with no check behind it. `ChecksSummary` is only built by `ParseChecks`, which returns null for no checks and counts every check it
  gives a status for, so a status with `Done = 0, Total = 0` cannot be read. It now answers `GhAnswers.ChecksOf(status)`: one check, `gate`, in the bucket
  of that status (passing 1 of 1, pending 0 of 1, failing 1 of 1). No test's expectation changed. The code under test was not wrong.
- Checked and found possible, so unchanged in meaning: `LegTests.FakeGitHub.CreateAsync` (by-branch fills everything it set, and `CreateAsync` reads
  back through `PullRequestAsync`); `LandedBranchSweepTests.ThePrIsAskedForByNumberWhenTheListLacksIt` (its fake set state, head and head branch,
  never `MergedAt`, all of which `PullRequestOfAsync` fills); `PackageWaitTests`' merged fake (`MergedAt` and `MergeCommit` are on the by-branch
  list); `PackageWaitTests` workflow runs (all seven fields are on `WorkflowRunsAsync`'s list).
- **Merge-queue removal reasons** (`ConflictResolutionTests`, `FailureInvestigationTests`, `LandingPathTests`, `MergesTests`, `QueuedPushTests`, `RebaseLayFlightTests`,
  `RetryAndHoldTests`, `ReviewTests`, `LegTests.FakeGitHub`): fakes set `RemovedReason` to free text ("failed checks", "merge conflict", "manually removed",
  "a required check failed"). `RemovedFromMergeQueueEvent.reason` is a GraphQL enum (`FAILED_CHECKS`, `MERGE_CONFLICT`, `MANUALLY_REMOVED`, ...), so no gh
  answer carries that text. `GhAnswers.MergeQueue` refuses anything but an upper-case enum value. Expectations that quoted the text now quote the enum value
  (`ReviewTests` card details, `MergesTests` entry why). The code under test was not wrong: it passes the reason through and tests it for "conflict" and "merged" case-insensitively.
- **Merge-queue state text** (`RebaseLayFlightTests`, `MergesTests`, `PullRequestsTests`): `new MergeQueueState("UNQUEUED", "", false)`, `("QUEUED", "", true, position)` and
  `("CLOSED", head, false)` carried a state and an empty head that GraphQL never answers (state is the PR's OPEN, CLOSED or MERGED; the head is its commit). They are now
  the idle, queued and (PR state CLOSED) recordings with the PR's head. No expectation changed. The code under test was not wrong.
- **A queued PR with no position** (`PullRequestsTests.Of(queued: true)` with no position, expecting the label "In merge queue"): GitHub's `isInMergeQueue` comes with a
  `mergeQueueEntry` and its position, so a queued PR always has a number. The helper now defaults the position to 1 and the expectation is "In merge queue #1". The
  reader's unnumbered label is defensive code and stays; the code under test was not wrong.
- **Check counts** (`ReviewTests`, `MergesTests`, `PrReviewTests`, `StaleCardTests`): `new ChecksSummary(Pending, 1, 3)` and the like are possible (`ChecksOf(status, done, total)` builds the
  checks that give those counts), but `ChecksSummary(Passing, 3, 7)` (StaleCardTests passes `3, 7` for pending and failing only) and `ChecksSummary(status, 1, 1)` with status Pending
  (`PrReviewTests.ReviewedWidget`, one check cannot be pending and done) are not: passing means every check done, pending means one is not. `ChecksOf` throws on such a count;
  `ReviewedWidget` now gives pending as 0 of 1. No expectation changed. The code under test was not wrong.
- **An open PR with no URL** (`CliTests.ReposWriteMarkdownWithTablesAndLeavePlainAsItWas`, PR #8): `new PullRequestStatus { ... }` with no `Url`; `gh pr list --json ... url` always
  fills it. The PR now has a URL and its row is a link, `| [#8 wip](https://example.com/pull/8) | ...`. The code under test was not wrong (it links whatever URL it is given).
- **Open PRs generally** (`RepoStatusTests`, `RepoWatchTests`, `LegEndOverlapTests`, `PrRecheckTests`): the builder gives an open PR the recording's URL and a rollup from its `checks` argument; no expectation changed.

Across the whole flight (legs 1 to 4) the tests whose expectation changed are: `CliTests.ReposWriteMarkdownWithTablesAndLeavePlainAsItWas` (the PR #8 row),
`PullRequestsTests` (the "In merge queue" label with no number), `ReviewTests` queue-card details and `MergesTests` entry why (enum text instead of free text). The
code under test was wrong in none of them; the PIL1101 case (a by-number fake carrying `MergedAt`) is a fake the builders now refuse.
