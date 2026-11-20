---
name: merge-queue
description: "Tell whether a PR is merged, queued, auto-merging or just open on a repo whose base branch has a GitHub merge queue, watch it land, and tidy up after it merged. Use when the user asks 'is #N merged', 'is it in the queue', 'watch it land', when a push to main is refused with 'Changes must be made through the merge queue' (GH013), or after a merge to bring main up to date and delete the landed branch. Installed by pilot (pilot-node setup, setting user-skills)."
---

# merge-queue: read where a PR stands, watch it land, clean up after it

A repo whose ruleset says "Changes must be made through the merge queue" cannot be pushed to directly:
`git push origin main` fails with `GH013 ... Changes must be made through the merge queue` and
`Required status check "..." is expected`. Everything reaches main as a PR through the queue, docs-only
changes included, even when the user says "straight to main".

## The trap this skill exists for

**`gh pr view --json` cannot see the queue.** It has no field for queue membership: a queued PR reads
`state OPEN`, `mergeStateStatus CLEAN`, `autoMergeRequest null`, which looks exactly like "green, nobody has
queued it". Answering "it needs adding to the queue" from those fields is wrong whenever the user already
queued it. Queue state is GraphQL only (`PullRequest.mergeQueueEntry`), and the issue timeline records
`added_to_merge_queue` / `removed_from_merge_queue`. **Always read it through `mq.sh status`.**

## Commands (`mq.sh` beside this file, run from inside the repo)

| Command | Answers |
|---|---|
| `bash <this skill's folder>/mq.sh status <pr>` | one line: `MERGED at ...`, `QUEUED position N state S since ...`, `AUTO ...` (enters the queue when checks pass), `OPEN not queued; mergeState ... checks ...`, or `CLOSED` |
| `bash <this skill's folder>/mq.sh queue` | every entry in main's queue, in order |
| `bash <this skill's folder>/mq.sh watch <pr>` | polls each 60 s: exit 0 once merged or closed; exit 1 with the removal event if the PR LEFT the queue unmerged (a failed queue build); a failed poll is retried, and exit 3 means five failed in a row, so the PR's state is unknown, never that it left the queue |
| `bash <this skill's folder>/mq.sh enqueue <pr>` | puts the PR in the queue and prints its status line; refused by GitHub until every required check has reported |
| `bash <this skill's folder>/mq.sh ready <pr>` | "Merge when ready": tries `enqueue` each 120 s until GitHub takes it, then watches it land; exit 0 merged, exit 1 a check failed or it left the queue, exit 3 state unknown, exit 4 not in after 2 hours |

The folder is `~/.claude/skills/merge-queue` (or under `CLAUDE_CONFIG_DIR`). An optional last argument
`owner/repo` targets another repo.

## Workflows

**"Is #N merged?" / "where is it?"**: `mq.sh status N`. Report the line as it is. If QUEUED, say its
position; if OPEN and checks pass, say it is ready to queue.

**Queue a PR: whose it is depends on who started the session.**

- **A flight's PR is the node's.** On autopilot the node queues it once it is reviewed and green; a session does not
  race it (`pilot flight merge <code> <head>` is the command when a person wants it merged).
- **A session the node started** (a worker, planner, validator or reviewer in a plane under `.claude/worktrees/`, or
  the watchtower) is refused by pilot's merge guard, a PreToolUse hook: `gh pr merge`, the GraphQL
  `enqueuePullRequest` / `mergePullRequest` and a push to a default branch all come back with a deny that points at
  `pilot flight merge`. Do not route around it.
- **The person's own session, and one they steer, queues its own PRs.** The guard lets it through (`MergeGuard`, "the
  person's own session ... merges as they say"). A PR opened by hand there is queued by the session that opened it,
  in the same turn, without being asked: run `mq.sh ready <N>` in the background right after `gh pr create` and
  report when it exits.

**"Merge when ready" has no API on these repos; `mq.sh ready` stands in for it.** Three facts, each tried:

- `enqueuePullRequest` (what `mq.sh enqueue` sends) is refused while a required check has not reported: `Pull request
  Required status check "gate" is expected`. It is accepted the moment the check reports (pilot#1242, 2026-11-21:
  refused at open, accepted when `gate` reported, queued at position 1).
- `gh pr merge N`, `gh pr merge --auto` and the GraphQL `enablePullRequestAutoMerge` all ask for auto-merge and fail
  with `Auto merge is not allowed for this repository`: pilot and tianwen have the repo setting "Allow auto-merge"
  off (`gh api repos/<owner>/<repo> --jq .allow_auto_merge` says `false`). The web page's "Merge when ready" button
  works all the same, which is why the button and the API look like two different things.
- Switching "Allow auto-merge" on (`gh repo edit --enable-auto-merge`) should make `gh pr merge N` arm a PR as the
  button does. Not tried: it is the owner's setting, and an armed PR merges whatever head it has when its checks pass.

So **do not offer or try `gh pr merge N`** on these repos, and never `--admin`, which bypasses the queue.

**Watch it land**: run `mq.sh watch N` in the background and report when it exits. A queue run builds the PR on
top of main, so a PR green on its branch can still be thrown out; exit 1 means that, and the queue's own check
run is where the failure is (`gh run list --branch gh-readonly-queue/main/...`).

**A push to main was refused (GH013)**: the commit is now on local main only. Move it, never `git reset`:
```bash
git checkout -b <topic-branch>        # the branch keeps the commit
git branch -f main origin/main        # main back to the remote, without touching the working tree
git push -u origin <topic-branch>
gh pr create ...                      # milestone and body as the repo's own instructions ask
```

**After it merged**: a queue lands a PR by rebase or squash, so main holds the same changes under NEW hashes and
`git branch -d` refuses ("not fully merged"). Prove the PR's own PATCH landed, then force-delete:
```bash
git checkout main && git pull --ff-only
git cherry main <branch>      # rebase merge: every line '-' = each commit's patch is on main
# squash merge (one commit on main for the whole branch): the two patch ids must match
git diff $(git merge-base main <branch>) <branch> | git patch-id --stable
git show <the squash commit on main> | git patch-id --stable
git branch -D <branch>        # only after one of the two checks above passed
```
Never prove it by diffing the branch against main's files: once main has moved on, a later PR touching the
same file makes that diff non-empty, and an empty one says nothing about whether this PR's lines are there.
The remote branch is normally deleted by the merge; `git fetch -p` drops the stale ref.

## Where an issue link points

A link to `blob/main/...#anchor` is dead until the PR carrying the file is merged; say so when filing issues
against a document that is still in a PR.
