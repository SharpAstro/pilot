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
those types in `Pilot.Node.Tests`, naming file and line, outside an explicit not-yet-migrated list that later legs empty. `Pilot.Gui.Tests` is
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
