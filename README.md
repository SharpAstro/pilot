# pilot

Claude Code as it is, with more control. There is a pilot, a flight plan, and you are the flight
controller.

- **Pilot**: an ordinary interactive Claude Code session in the repo's main checkout, with the pilot
  plugin loaded. It keeps the flight plan, writes a brief per leg, starts a worker for each, follows them
  and checks what they did. It does not edit product code.
- **Flight plan**: one Markdown file per pilot session: a flight number, an objective, a route of legs
  (each with its goal, the legs it waits for, and the check that proves it), and a log. The pilot writes
  it, `pilot.py` logs into it, and you read it on the dashboard and may amend it. The flight number is the
  repo's initials and a count (`WA1` for web-app), given when the plan opens, so every worker
  of the flight is named for it: `WA1 scaffold` in `claude agents`, Recents and the dashboard.
- **Tracking page**: one private claude.ai artifact per flight, for following it away from the
  dashboard: the objective, what waits on you, each leg's state, checklist progress and whether it is
  pushed, and the recent log. `pilot track` renders it from the plan and its workers, the pilot publishes
  it (only a Claude session can) when the flight changes, and `pilot track --url` records its link on the
  plan's `Tracking:` line, which the dashboard links. It is as fresh as its last publish, and says when
  that was.
- **Worker**: a Claude Code background session (`claude --bg -w`) in its own git worktree, branched from
  the repo's default branch. It flies one leg, keeps a checklist (its task list), commits on its own
  branch, and ends with a report whose first line is `DONE`, `BLOCKED` or `FAILED`. A leg has landed only
  when its checklist is complete: nothing is left open or for later. An item the worker cannot finish is a
  `BLOCKED` report, and the pilot sends it back or makes the item a leg of its own. A `DONE` with items
  open shows in amber. A command refused by a permission check, a hook or a Claude Code guard is a
  `BLOCKED` report too, never a script or another shell that does the same: a worker once got past the
  worktree guard by writing the refused `wsl ...` into a script and running `bash` on it.
- **Co-pilot**: a Sonnet subagent the pilot plugin ships (`agents/copilot.md`), read-only. The pilot
  settles engineering questions itself and asks the co-pilot for an independent opinion on each one that
  matters; they agree, or after one exchange the question goes to you. Only that, a budget raise, scope,
  anything outside a brief's permissions, anything irreversible, and what you meant come to the tower.
- **Reviewer**: an Opus subagent the pilot plugin ships (`agents/reviewer.md`), read-only and fresh every
  round. Once a pilot has pushed a landed leg and opened its PR, the reviewer reads every changed line
  against the leg's brief, *Done when* and checklist, and answers with a round file: `BUGS: <n>`, the
  checklist ticked off with where the diff meets each item, then bugs (each proven by a trace or a failing
  test), questions, noted and preferences, the classes and the "is it the leg's?" test of Sam's
  mission-review, without its coverage ledger and staff. `pilot review` records it. Bugs go back to the
  worker as `R<n> B<i>` checklist items and the pilot pushes the fixes; at most two rounds find bugs before
  what is left comes to you. A PR is *on approach* while a round is due, *go-around* while it is fixed,
  *holding* for CI, *cleared to land* once accepted and green (only then is the merge put to you), and *at
  the gate* once merged.
- **Controller**: you, the tower. The dashboard is your radar. From it you read the plan and every
  conversation, answer what the pilot waits on you for, message any live pilot or worker through the
  intercom, set budgets, stop workers, summarise, and launch pilots.

Everything else is Claude Code's own: background sessions, worktrees, subagents, `claude agents`,
`claude attach`, Remote Control, `SendMessage` between sessions. This started as a Windows take on Sam's
[runaway-agent](../runaway-agent) kit and keeps only what is not native: a fixed way to start a worker, a
spend ceiling per worker, his read guards, and the dashboard.

## Use it

In pwsh, `pilot` is a function in `$PROFILE`:

```powershell
cd C:\Users\<you>\source\repos\web-app
pilot                    # the pilot here: claude --plugin-dir <this repo>/plugins/pilot
                         #   then: /pilot:pilot <what the flight is for>
pilot dashboard          # http://127.0.0.1:5181/, in a second terminal
```

Or launch the pilot from the dashboard's **Launch** page: pick any project Claude Code knows, start a new
session or resume one, in a new Windows Terminal tab or in the background.

The CLI is the pilot's, and yours:

```powershell
pilot plan --title "Export fixes"   # this pilot session's flight plan: its path, from the template once
pilot track --out <scratch>/track.html   # its tracking page, for the pilot to publish as an artifact
pilot track --url <artifact url>    # where it was published: the plan's Tracking: line
pilot start fix-export --brief brief.md --pilot "<pilot session name>" [--after other-leg]
pilot list                          # state, weighted spend vs budget, last activity, current tool
pilot result fix-export             # the worker's report
pilot tell w-fix-export -m "also cover rotated pages"   # through the intercom, mid-turn too
pilot say fix-export "also cover rotated pages"   # your message, as a user turn (its turn must have ended)
pilot review fix-export --file round1.md --post   # record a review round of its PR (and post it there)
pilot refuel fix-export 16000000 --reason "API and tests left, ~3M each"   # the watchtower approves, or it goes to you
pilot budget fix-export 20000000    # the controller's override: an absolute ceiling, not an increment
pilot summary [fix-export]          # a Haiku summary: Needs you / Where it is
pilot stop fix-export               # conversation kept: `claude attach <id>` resumes it
pilot rm fix-export                 # removes its sessions and worktree; the record stays in History
```

## The dashboard

`pilot dashboard` serves `web/` on 127.0.0.1 only. It runs the server as a child of a small supervisor, so
it can be restarted on the code now on disk without leaving the terminal: press **R** there, or **Restart
now** in the page's "running older code" line, which then reloads the page too. Ctrl-C stops both, and the
server ends with its supervisor however that goes, so it never holds the port alone. `--preview` runs in
one process, with no supervisor. One watcher thread stats the files Claude Code writes
(session records, background-job states, the daemon roster, transcripts, flight plans) every second,
rebuilds the state when one moves, and pushes it over one event stream; the page patches only what
changed. It asks `claude agents` only when a session file moved, or every 30 s.

| Page | What it shows |
|---|---|
| Overview | Each flight plan, folded to one line with a tally of its legs once it has landed (click to open; your choice sticks, and a folded plan leaves only its box on the radar): what its pilot is doing (working, idle and following its legs, or waiting on you, with the question it asked and where to answer it), what the pilot is waiting on you for (its `## Waiting on the controller` section, in amber), its last message in full when its turn has ended with nothing in the air, the pilot's notes (what it found, why the route is this shape), the route with every leg's live status and its checklist (done of total, and the item it is on) (planned, working and its current tool call, waiting on you, done, failed, removed), and what it leaves in git (commits past its base, pushed or how many are on no remote branch under any name, uncommitted files), totalled in the plan's header as `all pushed` or the legs that are not, and the flight log with every question and answer; the intercom, one line to message live sessions (everyone, all pilots, all workers, a flight, or one); the radar (legs under their pilot, chained by After, planned legs dashed); a Haiku summary of all workers; every worker with state, spend against budget and what it is doing; the sessions open in a repo's main checkout. |
| A worker or session | Its whole conversation streaming live, folded as Claude Code's remote view folds it: what it says in full, and each run of tool calls as one line ("Read 5 files, ran 3 commands (1 failed)") that opens to every call with its input and result, a subagent's run named by its task; a live line under it with what it is doing now (Claude Code's own one-line detail) and how long the turn has run; refused calls in red with who refused them (their run opens itself) (auto mode, a hook, a deny rule, Claude Code) and the command with a copy button; subagent transcripts one click away; its summary; its leg; a message box that goes through the intercom (or, for a worker whose turn has ended, as your own turn); a budget control, and **⛽ refuel to N**, one more tank of the leg's first budget past what it has spent, in one click (also on its row once it is at 75% and in the over-budget banner; you are the tower, so no watchtower). Fuel does not wake a worker whose turn has ended, so it then asks whether to tell it to carry on; attach, remote and stop. **What it leaves**: its repo, branch and tip, commits past its base, the remote branches holding them, its uncommitted files, and its report's `Outside the worktree:` line (scratch folders, other repos, a WSL clone, config), which no git command can find. For a pilot, a **Terminal** card: its screen, read every 2 s while open, and a line to type into it (an answer, a key, `/remote-control`), after a confirm, logged in the plan. |
| History | Removed workers with their result, branch (pushed or not, or deleted), spend, report and commits, and their conversation. |
| Spend | Weighted tokens per day over 14 days, and the token breakdown per worker. |
| Launch | Start a pilot in any project from Claude Code's own list, new or resuming one of its sessions. |

The bell collects notices while the dashboard runs: a pilot or worker waiting on you, a turn that ended (DONE,
BLOCKED or FAILED), a budget reached, 30 quiet minutes, an API, login or limit error. **desktop** turns them
into Windows notifications. A script error shows in a red line at the top of the page.

Writes (message, budget, stop, summary, launch, notices read) need the page's own header and a loopback
Host and Origin, so another site cannot drive them, and Launch only starts in a directory from Claude
Code's own project list.

## What is where

| What | Where |
|---|---|
| `pilot.py` | everything: the CLI, the budget hook, the dashboard server |
| `web/` | the dashboard page: `index.html`, `app.js`, `app.css`, no build step |
| `tests/` | the offline suite: `python -m unittest discover tests`, standard library only, no Claude Code needed (see Tests) |
| `plugins/pilot/` | the pilot skill, loaded with `--plugin-dir` into the session you want as pilot, and the hook that shows its questions on the dashboard and logs your answers in the plan |
| `plugins/worker/` | the hooks every worker gets: the budget checkpoint, and Sam's five hooks through `hooks/kit.sh` |
| `~/.pilot/pilot.json` | this machine's settings, kept out of the repo since they name local paths: the repos workers may start in, the default model and effort (unset: the account's), the default budget. Missing, it means no repos and a 10M budget |
| `%LOCALAPPDATA%/pilot/` | runtime state: `plans/<pilot session>.md`, `flights.json` (the last flight number per repo), `blackbox.jsonl` (every decision), `autopilot.json`, `track/` (tracking pages rendered with no `--out`), `workers/`, `archive/`, `briefs/` (the exact prompt sent), `meters/`, `notifications.json`, `summaries.json`, error logs |
| `<repo>/.claude/worktrees/w-<leg>` | each worker's worktree, branch `worktree-w-<leg>`; `start` adds the folder to the repo's `.git/info/exclude` |
| `~/.claude/projects/...` | transcripts, where Claude Code always keeps them; `rm` leaves them, so History can open them |

Nothing is installed into `~/.claude`, and nothing is committed to the repos workers fly in. To add a
repo, put it in `~/.pilot/pilot.json` and open `claude` there once, so its workspace trust is accepted.
A worker starts in the repo whose main checkout the pilot runs in; `start` refuses a checkout that is not
listed, rather than fly the leg somewhere else.

```json
{
  "repos": { "web-app": "C:/Users/<you>/source/repos/web-app" },
  "model": null,
  "effort": null,
  "budget": 10000000
}
```

## Autopilot and black box

**The autopilot** is for when you are away, asleep. The top bar's switch starts it: an Opus 5.5 background
session at xhigh effort, named `autopilot`, in auto mode, on standby until something needs the tower.
While it is on, the dashboard sends it each live plan's `## Waiting on the controller` items once per
change, through the intercom, and it answers the pilot with `pilot tell --as autopilot`, marked as its
answers on your behalf. It may decide engineering questions, reviews (approving a design document to build
on), route changes within a flight's objective, and refuels up to 3 times a leg's first budget
(`pilot budget` refuses it more). It never merges, pushes to a default branch, deletes anything or sets a new
objective: those stay in the plan for you. In auto mode the classifier checks every command it runs, with
its whole transcript as context; a refused one leaves that item for you. Switching it off stops the
session. The routing runs in the dashboard, so the dashboard has to run too.

A pilot stopped on a prompt in its terminal, most often a permission prompt, reaches the autopilot too:
the dashboard reads its screen (the Terminal card's `console-io`) and sends it, once per wait, for pilots
that fly a plan. The autopilot answers with `pilot type <pilot> --text 1` (yes, this once) when the command
is within its scope and can be undone, and with `--key esc` and a message otherwise; it never chooses the
option that stops the prompt asking, which would change the pilot's permissions for good. The classifier
checks its `pilot type` like any command, and refused console typing once before; a refusal leaves the
prompt for you. Every keystroke is in the plan's log and the black box as the autopilot's.

**The black box** (`<state>/blackbox.jsonl`) records every decision and event across all flights,
append-only: everything `pilot.py` writes into a plan's log, who acted (you, the autopilot, the watchtower,
a pilot, `pilot.py`), the flight, and events outside plans, such as the autopilot going on and off. The
**Black box** page reads it back with the lines pilots wrote into their plans themselves merged in, newest
first, filtered by who and by flight. Deleting a plan leaves its record.

## Flying this repo with itself

It is in `~/.pilot/pilot.json` like any repo. It has no remote, so its workers branch from local `main`:
with no `origin`, `claude --bg -w` takes the main checkout's branch (measured), and `pilot.py` names that as
the base. The live dashboard and every running pilot use the main checkout, so a worker cannot break the
tooling that watches it: its change reaches them only when its branch is merged into `main` (the
controller's call), the server only after a restart, the page on a reload. Nobody edits `main` by hand
while a flight runs here.

A worker checks a dashboard change with its own copy, beside the live one:

```bash
python pilot.py dashboard --preview     # port 5182; raises and writes no notices; session streams close after their backlog
"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless=new --disable-gpu --hide-scrollbars   --force-dark-mode --window-size=1400,1000 --virtual-time-budget=4000 --user-data-dir=<scratch dir>   --screenshot=<scratch dir>/page.png "http://127.0.0.1:5182/?nostream#/s/<session id>"
```

then reads the PNG. `?nostream` takes the state once; `--preview` closes a session page's stream, which
would otherwise keep headless Edge from ever settling.

## Tests

```bash
python -m unittest discover tests      # from the repo root; under a second
```

The offline suite runs `pilot.py`'s own logic against fixtures and needs no Claude Code, no daemon and
no account. It covers parsing plans (the route, the waiting section, the notes, header lines), flight
numbers, the plan log and the black box, weighted spend, checklists rebuilt from a transcript, who refused a
call, the refuel gates and headroom projections, the budget and ask hooks, and the tracking page. Each test
gets its own empty `PILOT_STATE` and `CLAUDE_CONFIG_DIR` in a temporary folder, and `claude agents` is
replaced by fixed rows, so the suite never touches this machine's real state. A fixture transcript has to
use Claude Code's real wording: `replay_tasks` skips any line without "ask" in it, as a fast pre-filter.

## Spend

A worker's spend is in **weighted tokens**, `input + 1.25*cache_write + 0.1*cache_read + 5*output`, the
ratios Sam's hooks use, counted once per message id over all of the worker's sessions and their subagents.
At its budget (default 10M, see `~/.pilot/pilot.json`) every tool but `SendMessage` is refused, and the worker
writes its report: a checkpoint, not a kill. Claude Code writes an assistant message to the transcript
only after its tool calls are dispatched, so the meter lags by the message in flight: a worker can
overshoot by one message, never more.

**Refuelling.** A pilot asks for more with `pilot refuel <leg> <N> --reason`, and the reason is required.
Gates come first and need no model: the usage figures are fresh (at most 30 minutes old), both windows
are under 80% and not projected to run dry before they reset (the status line's rules), and N is at most
twice the leg's first budget. The 7-day projection holds a refuel only once the week is 50% used and the dry
day is at most 3 days off: early in the week it is a guess from a few days' burn. Past them the **watchtower**, one Haiku call with no tools (about 3 s),
reads the leg's goal and Done when, the checklist, the spend, the worker's last message and the reason,
and approves or sends it to the tower. It checks the reason against the checklist: in testing it refused a
request whose reason claimed a step was finished that the checklist still had in progress. Either way the
plan's log has the verdict and the reason, and the bell has every refuel. The top bar's ⛽ says which way
a refuel would go right now, and is a switch: **auto** (the default) lets the watchtower approve within its
gates, shown as `auto · held` while the account has no headroom; **manual** sends every refuel to the tower,
you or the autopilot while it is on. The switch is in `<state>/refuel.json`, and each change is in the black
box. `~/.pilot/pilot.json` takes `"refuel": {"maxUsedPct": 80, "maxFactor": 2,
"maxAgeMinutes": 30, "weekDryPct": 50, "weekDryDays": 3}`.

A Haiku summary is one `claude -p --model haiku` with no tools, no MCP servers, no settings and no saved
session: about 4k tokens and 2 s for one worker, measured.

**Account usage.** The status line is the only thing Claude Code hands `rate_limits` to, and it renders
in background sessions too, so `~/.claude/statusline/Program.cs` keeps a copy: `CacheRateLimits` writes
`~/.claude/rate-limits.json` (the figures plus `written_at`) when they change or the copy is a minute
old, through a temp file so a reader never sees half of it. `./update.sh` there rebuilds it and syncs its
gist. The top bar reads that file and draws it by the status line's own rules, so the two never disagree:
peak or off-peak with a countdown (peak is weekdays 13:00-19:00 UTC); each window's used share and reset;
and a burn projection with its gauge, `▰▱▱` runs dry before the reset, `▰▰▱` only just makes it, `▰▰▰`
comfortable. The 5-hour window projects on the wall clock, the 7-day one over working hours only (07:30 to
21:00 local), and the 5-hour one goes hot pink when it runs dry within 15 minutes with the reset further off
than that. Figures more than 15 minutes old say when they were read; hover any of them for the sentence.

## Why it is built this way: measured on Windows 11, Claude Code 2.1.296

**Ways to run a session headless**

| Mode | Works on Windows | Multi-turn | Message mid-run | Permission prompts | Notes |
|---|---|---|---|---|---|
| `claude -p "prompt"` | yes | no | no | denied | With no stdin redirect it waits 3 s for stdin and warns. The prompt is capped by the 32,767-character Windows command line: at 33k Git Bash says "Argument list too long", pwsh "The filename or extension is too long". |
| `claude -p < prompt.txt` | yes | no | no | denied | No length cap. `claude -p "question" < data.txt` treats stdin as data and the argument as the question. |
| `claude < prompt.txt > out.txt` (no `-p`) | yes | no | no | denied | With stdout redirected it behaves exactly like `-p`. |
| `claude -p --input-format stream-json --output-format stream-json --verbose` | yes | yes, while stdin is open | yes: a message sent during a `sleep 12` was taken at the next step and answered in the same turn | with `--permission-prompt-tool stdio`, each one arrives as a `can_use_tool` control request; tested allow on a `touch` | What Sam's session_pump.py does. On Windows it needs threads, since `select()` takes only sockets, and no FIFO. Exits 0 when stdin closes. Listed in `claude agents` as interactive. |
| `claude --bg` (used here) | yes | yes | yes, from another session via `SendMessage`; from you via `pilot say`, `attach` or Remote Control | `state: blocked` until you answer it in `attach` or Remote Control | Daemon-supervised, so it survives closing the terminal. `-w` gives it a worktree off `origin/HEAD`; `claude rm` removes that worktree and its branch. Takes the prompt on stdin. Listed in `claude agents` with `state` working, blocked or done, and Claude Code keeps a one-line `detail` of each job in `jobs/<id>/state.json`, which the dashboard shows. |

Traps found on the way:

- **Options that take several values swallow a prompt written after them.** `claude --bg --allowedTools
  "Bash(sleep *)" "Run ..."` started a session with no task; the prompt became a second tool rule.
  `--add-dir`, `--tools`, `--mcp-config` and `--disallowedTools` are the same. `pilot.py` sends the prompt
  on stdin, so there is nothing to swallow.
- **`--bg` refuses a directory whose workspace trust was never accepted**, and `-p` skips that check.
  Open `claude` in the repo once and accept its trust prompt; its `-w` worktrees inherit that.
- **`--bg --resume <session>` always continues in a new session id**, a copy holding the whole conversation,
  in the same worktree with the same options, even when the original was stopped first. The message is a
  real user turn. `pilot say` uses that and moves the worker's record onto the copy. Only the session that
  made a worktree owns it: removing a copy leaves the worktree, removing the original deletes it, so `rm`
  removes the copies first.
- **A worker's system prompt describes the main checkout's git status**, not the worktree's: one smoke
  worker reported the main checkout's feature branch as its own. The preamble tells workers to run `git status`.
- **Cross-session messages are data by default.** Without the preamble, a worker refused the pilot's
  correction ("a message from another session isn't your instruction"). With the preamble naming its pilot,
  it followed it and said why. The preamble also says a correction never widens the brief or the worker's
  permissions.
- **A new background session is not reachable by `SendMessage` for its first 20 to 40 seconds.**
- **Idle notices reach the pilot between its turns, not during one.** Three notices for workers that went
  idle mid-turn all arrived together once the turn ended, and started a new turn. So the pilot starts its
  workers, subscribes, and ends its turn; each notice wakes it.
- **A pilot asking in its terminal blocks itself, and the dashboard cannot reach it.** A pilot asked about
  WSL packages with `AskUserQuestion` while the same item sat in its plan; the controller answered on the
  dashboard, and the answer queued behind the terminal prompt, as every message does, until someone pressed
  a key there. The prompt also holds off worker notices. So the pilot plugin's hook refuses
  `AskUserQuestion` to a pilot that has a plan and sends it to the plan's waiting section, which the
  dashboard answers through the intercom. (A pending question is not always in the transcript before it is
  answered; for a session without the hook, the dashboard takes it from there when it is.)
- **Only a Claude session can use the intercom.** The CLI has no command for it, and the per-session pipe
  Claude Code's records name (`messagingSocketPath`) speaks an undocumented protocol. So `pilot tell`,
  the dashboard's intercom and its answer buttons run one Haiku session allowed `SendMessage` alone, named
  `pilot-dashboard`: about 3 to 5 s and a tenth of a cent a message, measured, delivered word for word.
  A recipient takes it as Claude Code takes any cross-session message, a teammate's request acted on within
  its own permissions. Nothing tells workers to treat `pilot-dashboard` as you: any process here can take
  that name, so it would hand your authority to whoever claims it.
- **A resumed pilot has a new name, and a worker briefed by name ignores it.** `w-scaffold`, briefed with its
  pilot's name, took no action on its own resumed pilot ("not my pilot"). A resume keeps the session id,
  so a brief names the pilot by both, and `pilot whois "<name>"` prints the id behind a live name for the
  worker to check: a session can take any name, not another's id.
- **A finished checklist disappears from disk.** Once every item is completed, Claude Code deletes the
  items under `tasks/<session>/` and keeps only `.highwatermark` and `.lock`, so a landed leg looked as if it
  kept no checklist. `pilot.py` rebuilds it from the transcript's successful `TaskCreate` and `TaskUpdate`
  calls (25 ms for a worker's whole transcript, then cached).
- **The dashboard can read and type into a pilot's console.** `pilot.py console-io <pid>` attaches to the
  console of that process (AttachConsole, which needs a process with no console of its own, so the
  dashboard runs it detached), reads the visible screen with ReadConsoleOutputCharacter, and types with
  WriteConsoleInput into that one console's input buffer: no window focus, so nothing lands in another
  Windows Terminal tab. Typed text and Enter ran in a test console and an Up arrow recalled it; a live
  pilot's screen reads whole (an emoji spans two cells and reads as `?`). Only pilots: a background worker
  has no console. It began as a script that re-linked dropped Remote Control sessions by typing
  `/remote-control` into them; auto mode refused that script, so this was built on the owner's say-so.
- **A stale daemon lock stops every background session.** `~/.claude/daemon.lock` named pid 42084, which
  Windows had since given to an unrelated process, so `claude --bg` started no daemon and no worker
  started. The lock records the daemon's pid and its process's start time (`procStartFt`, a FILETIME), so
  `pilot.py` compares both, and a process that has exited but is still held open counts as gone.
  `start` refuses with the reason, the dashboard shows it with a button that removes the lock (only while it
  is still the stale one), and the pilot leaves it to the controller: it is Claude Code's file.
- **Windows Terminal splits its command line on `;`.** A launch command with the first message inline broke
  in two ("error 0x80070002 when launching ..."). Launch passes the message in a file and escapes `;`.
- **Headless Edge captures before the page's fetches land** when an event stream is open, whatever
  `--timeout` says, and `--virtual-time-budget` never ends with one open. `?nostream` makes the page fetch
  the state once, for screenshots.

**Sam's kit on Windows**

- **Session layer** (`launch.sh`, `session_pump.py`): Linux-only (`mkfifo`, `select` on pipes, `setsid`,
  `/proc`, signals). Replaced here by `claude --bg`, `claude agents`, `attach`, `stop` and `rm`, plus
  `SendMessage` for pilot-to-worker messages.
- **Campaigner**: builds and starts on Windows with no code change, and serves transcripts and the SPA. As
  measured, though, every session reads as dead (liveness is `/proc`), accounts are empty (an account
  counts only if the kit's agents are installed in it), and the reader refuses Windows paths
  (`PathGuard.cs:42` wants a leading `/`). Making it a read-only Windows viewer is a small patch, about 2 to
  3 days. The `GetProcessById` liveness check it needs is banned by its own AC-15 rule outside a named file.
  Its campaign and mission views do not apply to a pilot and its workers.

**Sam's hooks** run on every worker, and only there; the pilot gets none of them.

- **They run under Git Bash with jq 1.8.2 from winget**, against both `C:/` and `C:\` transcript paths.
  Two were watched firing in a live worker. `big_read_guard.sh` refused a whole `Read` of a 212 KB file.
  `agent_call_budget.sh` let a subagent briefed with `[[CALL_BUDGET=1]]` make its first call, then
  refused the second with its checkpoint text.
- **Wired through `plugins/worker/hooks/kit.sh`**, which runs them from the sibling `../runaway-agent`
  checkout in place, so his fixes arrive with `git pull`. It also puts winget's jq on PATH. A Claude Code
  daemon started before jq was installed does not have it, and his hooks fail open without it.
- **They write state** under `~/.claude/state/` (`spawn-count`, `token-budget`, `fat-result`).
- **Gaps.** `big_read_guard.sh` misses `cat C:/...`, because its regex excludes `:`, and 8.3 short names
  with `~`. `agent_ratelimit_gate.sh` does nothing until the status line writes `rate-limits.json`.
  jq 1.8.2 writes CRLF to pipes. The hooks survive that because `$(...)` strips `\r`, but a new loop that
  reads jq output directly would not.
- **His suite under Git Bash: 203 of 265 checks pass.** All 62 failures are environmental: launch.sh's
  POSIX session layer, `/proc`, `/home/sam` paths, and test fixtures that build 50 KB arguments past the
  32 KB command-line limit. None is a bug in a hook.

## Not done yet

- A question a session asks in its terminal (`AskUserQuestion`) cannot be answered from the dashboard:
  Claude Code has no way in from outside. Pilots with a plan are refused it and use the plan instead; for
  anything else the dashboard shows the question and links **remote**, Claude's own choice UI.
- The flight plan is tied one to one to a pilot session id. An interactive `--resume` keeps the id, so a
  resumed pilot finds its plan again (measured: a pilot whose session ended came back as a new name, same
  id). What a resume loses is the pilot's `notify_when_idle` subscriptions, which lived in the old process:
  a resumed pilot subscribes to its flying workers again. A background resume does fork the id; then
  `pilot plan --session <old id>` reopens the plan.
- Every worker gets the same model (the account's default unless `~/.pilot/pilot.json` or `--model` says otherwise).
