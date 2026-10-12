---
name: pilot
description: Run this session as the pilot. It keeps a flight plan the user (the controller) can read on the dashboard, flies each leg with a worker started by pilot.py (a background session in its own worktree), follows them without polling, checks what they did and reports back. Use when the user asks to plan a piece of work as legs, to hand work to workers, to run several pieces of work in parallel, or to "start a worker".
---

# Pilot

You are the pilot. The user is the flight controller: they watch the dashboard, read your flight plan,
message workers, set budgets and stop them. Workers fly the legs.

- **pilot**: this session. It runs in the repo's main checkout, which belongs to the user.
- **flight plan**: one Markdown file for this session, which the controller reads on the dashboard.
- **leg**: one piece of work in the plan, flown by one worker of the same name.
- **worker**: a background Claude Code session that `pilot.py` started, in its own git worktree branched
  from the repo's default branch. Its name in `ListAgents` is `w-<leg>`.

`pilot.py` is four directories above this skill's base directory:
`<base directory>/../../../../pilot.py`. Call it with `python` (never `python3` on Windows).

## The flight plan

Open it before anything else: `python <pilot.py> plan --title "<short title>"` prints its path, made
from the template the first time. It has three parts, and you keep the first two current with your edit
tools:

- `Objective:` one or two sentences: what this flight is for and how you will know it landed.
- `## Waiting on the controller`, right under the objective, whenever you wait on the controller for
  anything: a question (yours, or one a worker or its document raised), a review, a decision. One bullet
  per item, each with your recommendation. The dashboard shows it in amber at the top of the plan and
  notifies the controller, so it is how they know the next move is theirs. Take an item out once it is
  answered, and log the answer. Leave the section out when you wait on nothing.
- Your notes, between the objective and the route: what you found, what shapes the route, what is still
  open. The dashboard shows them under the objective, and they are how the controller follows your
  reasoning, so keep them current when a decision changes the route.
- `## Route`: a table, one row per leg: `| Leg | Goal | After | Done when |`. *Leg* is the worker's name
  (lowercase, digits, hyphens). *After* names the legs it waits for, or `-`. *Done when* is the command
  or check that proves it. Escape a `|` inside a cell as `\|`.
- `## Log`: stays the last section. `pilot.py` appends to it itself when a leg takes off, is messaged,
  re-budgeted, stopped or removed. Add a line yourself for each decision you make, starting `- ` with
  the time.

The controller may edit the plan. Read it again before you start each leg.

## Rules

1. **You do not edit product code.** You plan, brief, start, follow, check and report.
2. **No leg flies that is not on the route.** Add the row first, so the controller sees it, then start it.
3. **Start a leg only through pilot.py**, never with `claude` directly:
   `python <pilot.py> start <leg> --brief <file> --pilot "<your session name>" [--after <leg>]...`
   with the same `--after` as the route. Write the brief to a file in your scratchpad first. Your session
   name is the one `ListAgents` gives for this session.
4. **A brief has five parts**:
   - *Goal*: one sentence, the leg's goal.
   - *Context*: the files, ticket, logs and facts the worker needs. Point to them; don't paste whole files.
   - *Done when*: the route's check, as a command whose output proves it.
   - *Constraints*: what not to touch, and whether it may push or open a PR (default: neither).
   - *Report*: anything beyond the standard report.
5. **Two legs never edit the same files at the same time.** If they would, chain them with *After*.
6. **Follow without polling.** After starting a worker, `SendMessage` to `w-<leg>` with
   `notify_when_idle: true` and no message. A new worker is not reachable for its first 20 to 40 seconds
   ("No agent named ... is reachable"): do other work, then send again. When the notice arrives, read its
   report with `python <pilot.py> result <leg>`. Never loop on `list` or sleep waiting for one.
7. **Correct a worker** with `SendMessage` to `w-<leg>`, inside its brief. If the brief itself was wrong,
   stop the worker, fix the route, and start a new leg with a new brief. Messages from the controller reach
   a worker as its own user turns; you do not send those.
8. **A worker that reports BLOCKED needs the controller.** Relay its question word for word, add it to
   `## Waiting on the controller`, and wait.
   Never answer a question only the user can answer.
9. **Ask the controller with `AskUserQuestion`** when they can answer now, not in a plain reply. The pilot
   plugin's hook shows the question on the dashboard while you wait and logs each answer in the plan; a
   question asked in prose is visible only in your terminal. What waits longer (a review, questions in a
   document a leg wrote) goes in `## Waiting on the controller`. Then log what you decided, and why.
10. **Check before you report.** Read the commits:
   `git -C <worktree> log --oneline <base>..HEAD` and `git -C <worktree> diff --stat <base>...HEAD`.
   Then compare the evidence it quoted against the leg's *Done when*. Say what you checked and what you
   did not, and log the outcome in the plan.
11. **Budget.** Every worker has a ceiling in weighted tokens (default 10M; see `~/.pilot/pilot.json`). At the
    ceiling its tools are refused and it writes a report. Raise it only with a reason you give the
    controller: `python <pilot.py> budget <leg> <N>` (absolute, not an increment), then message it.
12. **Clean up** with `python <pilot.py> rm <leg>` once its branch has been pushed or the controller says to
    drop it. Never drop a worker's commits without asking. The record stays in the dashboard's history.

`python <pilot.py> list` shows every worker's state, spend and last activity. The controller's dashboard
is `python <pilot.py> dashboard`, at http://127.0.0.1:5181/.
