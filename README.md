# pilot

One pilot session and some workers, for bullclip-backend, on one Claude Code account, on Windows.

- **Pilot**: the interactive session you talk to, in the repo's main checkout. It plans the work, writes a
  brief per worker, starts workers, follows them and checks their results. It does not edit product code.
- **Worker**: a background session (`claude --bg -w`) in its own git worktree, branched from
  `origin/develop`. It does one brief, commits on its own branch, and ends with a report whose first line
  is `DONE`, `BLOCKED` or `FAILED`.

This is the small, Windows-native take on Sam's [runaway-agent](../runaway-agent) kit. That kit's session
layer (launch.sh, session_pump.py, FIFOs, `/proc`, tmux) is Linux-only, and Claude Code now ships most of
it natively, so this keeps only the parts that are still needed: a fixed way to start a worker, a spend
ceiling per worker, and a dashboard.

## Use it

```bash
# 1. Start the pilot in the repo, with the pilot plugin loaded for this session only.
cd C:/Users/SebastianGodelet/source/repos/drawboard/bullclip-backend
claude --plugin-dir C:/Users/SebastianGodelet/source/repos/drawboard/pilot/plugins/pilot
#    then: /pilot:pilot, and describe the work

# 2. The dashboard, in another terminal: http://127.0.0.1:5181/
python C:/Users/SebastianGodelet/source/repos/drawboard/pilot/pilot.py dashboard
```

The pilot starts workers with `pilot.py start`, and the CLI works for you too:

```bash
python pilot.py start fix-export --brief brief.md --pilot "<pilot session name>"
python pilot.py list                 # state, weighted spend vs budget, last activity, current tool
python pilot.py result fix-export    # the worker's report
python pilot.py budget fix-export 20000000
python pilot.py stop fix-export      # conversation kept: `claude attach <id>` resumes it
python pilot.py rm fix-export        # deletes the session, and its worktree when that is safe
```

**Talking to a worker.** The pilot uses `SendMessage` to `w-<name>`. You use `claude attach <id>` in a
terminal, or the dashboard's **remote** link, which opens the worker in claude.ai (Remote Control, on your
phone as well). The worker sees your words there as a user turn. `claude agents` is Claude Code's own
full-screen view of every session.

## What is where

| What | Where |
|---|---|
| `pilot.py` | everything: start, list, result, budget, stop, rm, dashboard, and the budget hook |
| `dashboard.html` | the dashboard page `pilot.py dashboard` serves on 127.0.0.1 only. The server pushes the state over one event stream when a watched file changes, and the page patches only what changed |
| `pilot.json` | the repos (just bullclip-backend), the default model and effort (unset: the account's), the default budget |
| `plugins/pilot/` | the pilot skill, loaded with `--plugin-dir` into the session you want as pilot |
| `plugins/worker/` | the hooks every worker gets from `pilot.py start`: the budget checkpoint, and Sam's five hooks through `hooks/kit.sh` |
| `%LOCALAPPDATA%/pilot/` | runtime state: `workers/<name>.json`, `briefs/<name>.md` (the exact prompt sent), `meters/`, `hook-errors.log` |
| `bullclip-backend/.claude/worktrees/w-<name>` | each worker's worktree, branch `worktree-w-<name>`; already in `.git/info/exclude` |
| `~/.claude/projects/...` | transcripts, where Claude Code always keeps them |

Nothing is installed into `~/.claude`, and nothing is committed to bullclip-backend.

## Spend

A worker's spend is in **weighted tokens**, `input + 1.25*cache_write + 0.1*cache_read + 5*output`, the
ratios Sam's hooks use, counted once per message id over the worker's transcript and all its subagents.
At its budget (default 10M, see `pilot.json`) every tool but `SendMessage` is refused, and the worker
writes its report: a checkpoint, not a kill. `pilot.py budget` sets a new absolute ceiling. Claude Code
writes an assistant message to the transcript only after its tool calls are dispatched, so the meter lags
by the message in flight: a worker can overshoot by one message, never more.

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
| `claude --bg` (used here) | yes | yes | yes, from another session via `SendMessage`; from you via `attach` or Remote Control | `state: blocked` until you answer it in `attach` or Remote Control | Daemon-supervised, so it survives closing the terminal. `-w` gives it a worktree off `origin/develop`; `claude rm` removes that worktree and its branch. Takes the prompt on stdin. Listed in `claude agents` with `state` working, blocked or done. |

Traps found on the way:

- **Options that take several values swallow a prompt written after them.** `claude --bg --allowedTools
  "Bash(sleep *)" "Run ..."` started a session with no task; the prompt became a second tool rule.
  `--add-dir`, `--tools`, `--mcp-config` and `--disallowedTools` are the same. `pilot.py` sends the prompt
  on stdin, so there is nothing to swallow.
- **`--bg` refuses a directory whose workspace trust was never accepted**, and `-p` skips that check.
  bullclip-backend is trusted, and its `-w` worktrees inherit that.
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

**Sam's kit on Windows**

- **Session layer** (`launch.sh`, `session_pump.py`): Linux-only (`mkfifo`, `select` on pipes, `setsid`,
  `/proc`, signals). Replaced here by `claude --bg`, `claude agents`, `attach`, `stop` and `rm`, plus
  `SendMessage` for pilot-to-worker messages.
- **Campaigner**: builds and starts on Windows with no code change, and serves transcripts and the SPA. As
  measured, though, every session reads as dead (liveness is `/proc`), accounts are empty (an account
  counts only if the kit's agents are installed in it), and the reader refuses Windows paths
  (`PathGuard.cs:42` wants a leading `/`). Making it a read-only Windows viewer is a small patch, about 2 to
  3 days. The `GetProcessById` liveness check it needs is banned by its own AC-15 rule outside a named file.
  Its campaign and mission views do not apply to pilot plus workers.
- **Hooks**: see below.

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
- The dashboard can stop a worker but not message it. You use the remote link or `attach`, which keeps
  your words a user turn rather than another session's message.
- Workers run on the account's default model unless `pilot.json` or `--model` says otherwise. Every worker
  gets the same model; there is no per-role model matrix.
