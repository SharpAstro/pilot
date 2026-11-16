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
