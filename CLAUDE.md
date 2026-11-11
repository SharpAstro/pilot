# CLAUDE.md

The working instructions for this repo are in [`PILOT.md`](PILOT.md): the build and test commands, what a plane can
and cannot do, how a flight merges. The node quotes that page into the sessions it starts. A session started by hand
gets it only through this pointer, so **read `PILOT.md` before you work here.**

Two facts from it that a session started by hand needs at once:

- **main has a merge queue.** Nothing is pushed to main; every change, a docs-only one included, is a PR that lands
  through the queue, squashed into one commit.
- **`gh pr view` cannot see the queue.** A queued PR reads `OPEN`, `CLEAN` and not auto-merging, exactly as one nobody
  queued. To say whether a PR is merged, queued or only open, use the `merge-queue` skill:
  `bash ~/.claude/skills/merge-queue/mq.sh status <pr>`, and `mq.sh watch <pr>` to see it land.
