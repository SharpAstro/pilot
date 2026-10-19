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
- **Data:** `%LOCALAPPDATA%\pilot2` or `~/.local/share/pilot2` unless `--data` names another. The node skeleton keeps
  its state in memory; the store leg writes it here.

## The wire (`NodeWire.Version` 1)

| Route | What it does |
| --- | --- |
| `GET /api/v1/node` | Which node this is: wire version, build version, machine, pid, started at, data folder, socket, stream. |
| `GET /api/v1/events` | The event stream, a WebSocket: a snapshot carrying the current number, then deltas each numbered one past the last. `?since=<n>&stream=<id>` resumes with the deltas after `n`, or a snapshot when the node no longer holds them or has another stream. |
| `POST /api/v1/commands` | Every command, `{verb, target, args, who}`. The result is the response and a `commandResult` event on the stream. Verbs: `note node <text>`, and `say session/<id> <text>`. |

HTTP responses come in a `ResponseEnvelope` (`response`, `error`, `statusCode`, `success`); stream events are sent
bare, each with its `kind` and `seq`. A client that falls behind is closed and resumes with `since`.
