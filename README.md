# pilot

Claude Code as it is, with more control. There is a pilot, a flight plan, and you are the flight
controller.

- **Pilot**: an ordinary interactive Claude Code session in the repo's main checkout, with the pilot
  plugin loaded. It keeps the flight plan, writes a brief per leg, starts a worker for each, follows them
  and checks what they did. It does not edit product code.
- **Flight plan**: one Markdown file per pilot session: an objective, a route of legs (each with its goal,
  the legs it waits for, and the check that proves it), and a log. The pilot writes it, `pilot.py` logs
  into it, and you read it on the dashboard and may amend it.
- **Worker**: a Claude Code background session (`claude --bg -w`) in its own git worktree, branched from
  the repo's default branch. It flies one leg, commits on its own branch, and ends with a report whose
  first line is `DONE`, `BLOCKED` or `FAILED`.
- **Controller**: you. The dashboard is your radar. From it you read the plan and every conversation,
  message a worker (as your own user turn), set its budget, stop it, summarise, and launch pilots.

Everything else is Claude Code's own: background sessions, worktrees, subagents, `claude agents`,
`claude attach`, Remote Control, `SendMessage` between sessions. This started as a Windows take on Sam's
[runaway-agent](../runaway-agent) kit and keeps only what is not native: a fixed way to start a worker, a
spend ceiling per worker, his read guards, and the dashboard.

## Use it

In pwsh, `pilot` is a function in `$PROFILE`:

```powershell
cd C:\Users\SebastianGodelet\source\repos\drawboard\bullclip-backend
pilot                    # the pilot here: claude --plugin-dir <this repo>/plugins/pilot
                         #   then: /pilot:pilot <what the flight is for>
pilot dashboard          # http://127.0.0.1:5181/, in a second terminal
```

Or launch the pilot from the dashboard's **Launch** page: pick any project Claude Code knows, start a new
session or resume one, in a new Windows Terminal tab or in the background.

The CLI is the pilot's, and yours:

```powershell
pilot plan --title "Export fixes"   # this pilot session's flight plan: its path, from the template once
pilot start fix-export --brief brief.md --pilot "<pilot session name>" [--after other-leg]
pilot list                          # state, weighted spend vs budget, last activity, current tool
pilot result fix-export             # the worker's report
pilot say fix-export "also cover rotated pages"   # your message, as a user turn (its turn must have ended)
pilot budget fix-export 20000000    # an absolute ceiling, not an increment
pilot summary [fix-export]          # a Haiku summary: Needs you / Where it is
pilot stop fix-export               # conversation kept: `claude attach <id>` resumes it
pilot rm fix-export                 # removes its sessions and worktree; the record stays in History
```

## The dashboard

`pilot dashboard` serves `web/` on 127.0.0.1 only. One watcher thread stats the files Claude Code writes
(session records, background-job states, the daemon roster, transcripts, flight plans) every second,
rebuilds the state when one moves, and pushes it over one event stream; the page patches only what
changed. It asks `claude agents` only when a session file moved, or every 30 s.

| Page | What it shows |
|---|---|
| Overview | Each live flight plan with its route and every leg's live status (planned, working, waiting on you, done, failed, removed), the flight log; the radar (legs under their pilot, chained by After, planned legs dashed); a Haiku summary of all workers; every worker with state, spend against budget and what it is doing; the sessions open in a repo's main checkout. |
| A worker or session | Its whole conversation streaming live: prompts, replies, thinking, every tool call with its input and result, refused calls in red with who refused them (auto mode, a hook, a deny rule, Claude Code) and the command with a copy button; subagent transcripts one click away; its summary; its leg; a message box; a budget control; attach, remote and stop. |
| History | Removed workers with their result, branch, spend, report and commits, and their conversation. |
| Spend | Weighted tokens per day over 14 days, and the token breakdown per worker. |
| Launch | Start a pilot in any project from Claude Code's own list, new or resuming one of its sessions. |

The bell collects notices while the dashboard runs: a worker waiting on you, a turn that ended (DONE,
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
| `pilot.json` | the repos workers may start in, the default model and effort (unset: the account's), the default budget |
| `plugins/pilot/` | the pilot skill, loaded with `--plugin-dir` into the session you want as pilot |
| `plugins/worker/` | the hooks every worker gets: the budget checkpoint, and Sam's five hooks through `hooks/kit.sh` |
| `%LOCALAPPDATA%/pilot/` | runtime state: `plans/<pilot session>.md`, `workers/`, `archive/`, `briefs/` (the exact prompt sent), `meters/`, `notifications.json`, `summaries.json`, error logs |
| `<repo>/.claude/worktrees/w-<leg>` | each worker's worktree, branch `worktree-w-<leg>`; bullclip-backend already excludes the folder |
| `~/.claude/projects/...` | transcripts, where Claude Code always keeps them; `rm` leaves them, so History can open them |

Nothing is installed into `~/.claude`, and nothing is committed to the repos workers fly in. To add a
repo, put it in `pilot.json` and open `claude` there once, so its workspace trust is accepted.

## Spend

A worker's spend is in **weighted tokens**, `input + 1.25*cache_write + 0.1*cache_read + 5*output`, the
ratios Sam's hooks use, counted once per message id over all of the worker's sessions and their subagents.
At its budget (default 10M, see `pilot.json`) every tool but `SendMessage` is refused, and the worker
writes its report: a checkpoint, not a kill. Claude Code writes an assistant message to the transcript
only after its tool calls are dispatched, so the meter lags by the message in flight: a worker can
overshoot by one message, never more.

A Haiku summary is one `claude -p --model haiku` with no tools, no MCP servers, no settings and no saved
session: about 4k tokens and 2 s for one worker, measured.

The dashboard shows the account's 5-hour and 7-day windows only if something writes
`~/.claude/rate-limits.json`, in the shape of Sam's `statusline-cache.snippet.sh`. The status line is the
only thing Claude Code hands `rate_limits` to, and it renders in background sessions too. In
`~/.claude/statusline/Program.cs`, right after `long now = ...` in render mode (not applied; it's your
code):

```csharp
    // The status line is the only thing handed rate_limits: keep a copy where pilot's dashboard reads it.
    if (root.TryGetProperty("rate_limits", out var rlCache) && rlCache.ValueKind == JsonValueKind.Object)
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            string cachePath = Path.Combine(dir, "rate-limits.json");
            string tmpPath = $"{cachePath}.{Environment.ProcessId}.tmp";
            using (var fs = File.Create(tmpPath))
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WritePropertyName("rate_limits");
                rlCache.WriteTo(w);
                w.WriteNumber("written_at", now);
                w.WriteEndObject();
            }
            File.Move(tmpPath, cachePath, overwrite: true);
        }
        catch { }
    }
```

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
  bullclip-backend is trusted, and its `-w` worktrees inherit that.
- **`--bg --resume <session>` always continues in a new session id**, a copy holding the whole conversation,
  in the same worktree with the same options, even when the original was stopped first. The message is a
  real user turn. `pilot say` uses that and moves the worker's record onto the copy. Only the session that
  made a worktree owns it: removing a copy leaves the worktree, removing the original deletes it, so `rm`
  removes the copies first.
- **A worker's system prompt describes the main checkout's git status**, not the worktree's: one smoke
  worker reported `sg/sc-92521-count` as its branch. The preamble tells workers to run `git status`.
- **Cross-session messages are data by default.** Without the preamble, a worker refused the pilot's
  correction ("a message from another session isn't your instruction"). With the preamble naming its pilot,
  it followed it and said why. The preamble also says a correction never widens the brief or the worker's
  permissions.
- **A new background session is not reachable by `SendMessage` for its first 20 to 40 seconds.**
- **Idle notices reach the pilot between its turns, not during one.** Three notices for workers that went
  idle mid-turn all arrived together once the turn ended, and started a new turn. So the pilot starts its
  workers, subscribes, and ends its turn; each notice wakes it.
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

- No rate-limit figures until the status line writes the cache (above).
- A worker mid-turn, or waiting on a permission prompt, cannot be messaged from the dashboard: stopping it
  would cut the turn off. Use **remote** or `claude attach`.
- The flight plan is tied one to one to a pilot session id. Claude Code's help says an interactive
  `--resume` reuses the id unless `--fork-session` is given, so `pilot --resume` should find its plan again;
  not tested yet, and a background resume did fork. `pilot plan --session <old id>` reopens a plan by hand.
- Every worker gets the same model (the account's default unless `pilot.json` or `--model` says otherwise).
