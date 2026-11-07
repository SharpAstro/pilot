---
name: merge-queue
description: Tell whether a PR is merged, queued, auto-merging or just open on a repo whose base branch has a GitHub merge queue, watch it land, and tidy up after it merged. Use when the user asks "is #N merged", "is it in the queue", "watch it land", when a push to main is refused with "Changes must be made through the merge queue" (GH013), or after a merge to bring main up to date and delete the landed branch. Installed by pilot (pilot-node setup, setting user-skills).
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
| `bash <this skill's folder>/mq.sh watch <pr>` | polls each 60 s until merged or closed; exits 1 with the removal event if the PR LEFT the queue unmerged (a failed queue build) |

The folder is `~/.claude/skills/merge-queue` (or under `CLAUDE_CONFIG_DIR`). An optional last argument
`owner/repo` targets another repo.

## Workflows

**"Is #N merged?" / "where is it?"**: `mq.sh status N`. Report the line as it is. If QUEUED, say its
position; if OPEN and checks pass, say it is ready to queue.

**Queue a PR: a session does not.** pilot's merge guard (a PreToolUse hook) refuses `gh pr merge`, the GraphQL
`enqueuePullRequest` / `mergePullRequest` and a push to a default branch, and points at
`pilot flight merge <code> <head>`. A flight's PR merges that way. Any other PR is the user's to queue: from the
PR page, or `! gh pr merge <N>` typed in the prompt (a `!` command is the user's own, and no hook sees it). On a
queue-protected base `gh pr merge N` needs no strategy flag: with checks green it joins the queue, with checks
pending it arms auto-merge. Never suggest `--admin`, which bypasses the queue.

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
`git branch -d` refuses ("not fully merged"). Prove the content landed, then force-delete:
```bash
git checkout main && git pull --ff-only
git diff --stat <branch> main -- <the files the PR changed>   # empty = landed
git branch -D <branch>
```
The remote branch is normally deleted by the merge; `git fetch -p` drops the stale ref.

## Where an issue link points

A link to `blob/main/...#anchor` is dead until the PR carrying the file is merged; say so when filing issues
against a document that is still in a PR.
