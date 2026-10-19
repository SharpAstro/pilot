# pilot v2

pilot v2 runs every flight on a **node**, one per machine: the node owns all state and the long work, and the GUI,
TUI, CLI and home dashboard are clients of it over a per-user local socket. v1 (`pilot.py`, `web/`, `plugins/`,
`tests/` at the repo root) keeps flying until v2 replaces it phase by phase; nothing here touches it. The design is
the [Pilot v2 design](https://claude.ai/code/artifact/0ebd8a10-0b5c-475b-a909-2e57815df554) doc, the issue #41.

| Project | What it is |
| --- | --- |
| `Pilot.Contracts` | The wire, shared by the node and every client: `SessionRow`, the snapshot and delta events, commands, `NodeSocket`, `NodeClient`, `NodeMirror`, and the source-generated `PilotJsonContext`. `Fixtures/snapshot.sample.json` is a sample snapshot to draw from without a node. |
| `Pilot.Node` | The node, `pilot-node`: Kestrel on the socket only, native AOT. |
| `Pilot.Node.Tests` | xunit.v3 tests against a real node on a temp socket. |
| `Pilot.Gui` | The GUI, `pilot-gui`: every session on the machine as a row, live from the node, and a bar to say something to the selected one; on DIR.Lib and SdlVulkan.Renderer, native AOT. |
| `Pilot.Gui.Tests` | xunit.v3 tests rendering the sessions view on the CPU from the sample snapshot, the live feed on a fake stream, and the say bar. |

## Build, test, run

From the repo root:

```bash
dotnet build src/Pilot.slnx -c Release
dotnet test src -c Release
dotnet publish src/Pilot.Node -c Release -r win-arm64     # or linux-x64, osx-arm64: one native binary
dotnet publish src/Pilot.Gui -c Release -r win-arm64
```

`Pilot.Gui` compiles the sibling `DIR.Lib`, `SdlVulkan.Renderer` and `AppShell` clones when they sit beside this
repo (in a Claude Code worktree too) and restores the packages otherwise, as CI does. `-p:UseLocalDirLib=false
-p:UseLocalAppShell=false -p:UseLocalSdlVulkanRenderer=false` builds against the packages; a publish with `-r` always
does. Run the GUI:

```bash
pilot-gui [--node-socket <path>] [--exit-after 5s]           # the window, live from the node
pilot-gui [--node-socket <path>] --capture out.png [--size 1280x800]   # one CPU frame of the node's rows now, plus out.png.rows.txt
pilot-gui [--node-socket <path>] --say <session> <text...>   # say it through the node, print what became of it, exit
pilot-gui --snapshot <file.json> [--capture out.png]         # a snapshot file instead of the node (the contract's sample: Pilot.Contracts/Fixtures)
```

- **Node:** with no node on the default socket, the GUI starts one (`pilot-node` beside it, or the source tree's
  build of the same configuration), detached so it outlives the window; a socket named with `--node-socket` or
  `PILOT_NODE_SOCKET` is only connected to.
- **Rail:** the left rail, as tianwen's: Sessions (Ctrl+1), and Flights, Background tasks and Settings locked until
  the legs that bring them.
- **Live:** the window follows the node's event stream, resumes with `since` after a drop, and says so in its header
  while no node answers, trying again every 2 s. The selection follows its session as the rows reorder.
- **Say:** type in the bar at the foot (Ctrl+S takes the keyboard there from anywhere) and Enter says it to the selected session (the node's `say`: an idle background
  session is resumed with it, a busy or interactive one gets it as a message). `--say` finds a session by row id,
  session id or its start, or name.
- **Inspector:** a Debug build against the sibling clones attaches SdlVulkan.Renderer's debug inspector, so an agent
  can screenshot the real window and drive it. Release builds and package builds leave it out.

Run the node (it stays in the foreground; Ctrl+C stops it):

```bash
pilot-node [--node-socket <path>] [--data <folder>]
curl --unix-socket <path> http://localhost/api/v1/node
```

- **Socket:** `%LOCALAPPDATA%\pilot2\node.sock` on Windows; `$XDG_RUNTIME_DIR/pilot2/node.sock` elsewhere, or
  `~/.local/share/pilot2/node.sock` without one. `--node-socket` or the `PILOT_NODE_SOCKET` variable names another.
- **One node per socket:** `node.lock` beside the socket admits one node; a second exits with code 3 and says which
  node holds it.
- **Data:** `%LOCALAPPDATA%\pilot2` or `~/.local/share/pilot2` unless `--data` names another. The store is its
  `store` folder, the node its only writer: `snapshot.json` (the state as of a number, replaced atomically),
  `journal.jsonl` (each change after it, appended and flushed; folded into a new snapshot every 4096), and
  `blackbox.jsonl` (one line per command: when, who, verb, target, and what became of it; never compacted). A restart,
  a crash included, carries on with the same stream, the same numbers and every record.

## Flights

A flight is one issue's work on one repo, coded by the node from the repo's prefix and the issue (`TIA1611`, then
`TIA1611a` to `e` for further flights on it; a code is never given twice on the machine, landed ones included). Its
**plane** is one worktree and one branch, `<repo>/.claude/worktrees/<code>` on `flight/<code>`, made from the repo's
default branch: every leg flies on it in order and it lands as one PR. A session working in a plane's worktree belongs
to its flight, however it was started; a v2 worker's name says it too (`du1-tia1611-parser`, with the machine's
prefix, `--machine` or `PILOT_MACHINE`, else from its name).

| Command | What it does |
| --- | --- |
| `create-flight repo/<path> <issue> [title…]` | Codes the flight and makes its plane; `<path>` may be any checkout of the repo, a worktree included. |
| `connect flight/<code> <other>` | The flight holds until the other has landed (a cycle is refused). |
| `land flight/<code>` | Its work is in: the worktree goes when clean (never with changes not committed), the branch and code stay, and flights waiting for it are cleared. |

The GUI's rail opens the **Flights** page (Ctrl+2): each flight's code, title, repo and issue, state (held: waiting
for which), branch and sessions.

## Hooks

Claude Code's hooks tell the node the moment a session changes, where the agents listing is read every few seconds:
working on a prompt or a tool call, waiting on a permission prompt (with its words), idle after a turn, stopped by an
API error or a usage limit. They run in every session, interactive, `--bg` and `-p` alike.

```bash
pilot-node hooks install [--settings <file>] [--node-socket <path>]   # default: ~/.claude/settings.json
pilot-node hooks remove  [--settings <file>]
```

`install` adds one handler per event (SessionStart, UserPromptSubmit, PreToolUse, PermissionRequest, Notification,
Stop, StopFailure, SessionEnd), exec-form `pilot-node hook` and `async`, beside whatever hooks the file has, and a
second install changes nothing; `remove` takes out only pilot's. `pilot-node hook` forwards the hook's stdin to the
node and always exits 0 and prints nothing, so a missing or slow node costs a session nothing.

A session the API stopped (its transcript's `apiError`, `usage_limit_reached` with when the limit resets) shows as
**stopped** with why and when it can go on, hooks or not.

## The wire (`NodeWire.Version` 1)

| Route | What it does |
| --- | --- |
| `GET /api/v1/node` | Which node this is: wire version, build version, machine, pid, started at, data folder, socket, stream. |
| `GET /api/v1/events` | The event stream, a WebSocket: a snapshot carrying the current number, then deltas each numbered one past the last. `?since=<n>&stream=<id>` resumes with the deltas after `n`, or a snapshot when the node no longer holds them or has another stream. |
| `POST /api/v1/commands` | Every command, `{verb, target, args, who}`. The result is the response and a `commandResult` event on the stream. Verbs: `note node <text>`, `say session/<id> <text>`, and the flight verbs above. |
| `POST /api/v1/hooks` | One Claude Code hook's stdin, as `pilot-node hook` forwards it; always 204. |

HTTP responses come in a `ResponseEnvelope` (`response`, `error`, `statusCode`, `success`); stream events are sent
bare, each with its `kind` and `seq`. A client that falls behind is closed and resumes with `since`.
