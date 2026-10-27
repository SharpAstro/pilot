# pilot

Claude Code as it is, with more control. Each piece of work is a flight, Claude Code sessions fly it, and you are
the controller.

pilot v2 runs every flight on a **node**, one per machine. The node owns all state and the long work; the GUI, the
TUI, the CLI and an MCP server are its clients over a per-user local socket, and nodes on the same network form a
fleet. Everything else is Claude Code's own: background sessions, worktrees, Remote Control, hooks. This page is the
overview; [`src/README.md`](src/README.md) is the full reference.

## The words

- **Flight**: one GitHub issue's work on one repo, coded from the repo's prefix and the issue (`TIA1611`, then
  `TIA1611a` for a further flight on it). It lands as one PR.
- **Plane**: a flight's one worktree and branch, `<repo>/.claude/worktrees/<code>` on `flight/<code>`, made from the
  repo's default branch. The node owns it and removes it once the flight lands; the branch stays.
- **Leg**: one step of a flight, with a goal, a checklist, a Done-when and a check. Legs fly in order on the plane,
  each by a fresh background **worker**. A leg is done only with every step ticked and its check, rerun by the node,
  passing.
- **Planner**: a session that reads the issue and plans the legs, after looking for overlap with flights already in
  the air.
- **Validator**: a short read-only session that checks a later leg's brief against the tree the earlier legs left
  (GO or NO-GO) before its worker starts.
- **Reviewer**: a fresh session per round that reads the PR against every leg's goal and Done-when. Bugs go back as a
  go-around; a pass with CI green puts the merge to you.
- **Watchtower**: one background session per node, with Remote Control on, briefed from the node's state. It watches
  the flights and is told every card, so you can reach it from claude.ai or a phone.
- **Cards**: what needs you. **Rings** wait for you (a merge, a failed leg), **For you** is a notice, **Handled** is
  what the node or watchtower did by itself.
- **Controller**: you. You create flights, answer cards, and merge.

**Nothing is left behind.** A leg is done only with its changes committed, a merge is refused while the plane holds
commits past the reviewed head, and a flight does not land while its plane holds work on no remote. PRs merge by
rebase unless the node's `merge-method` says otherwise.

## Install

A release is a tag (`v2.0.<n>`), whose run attaches `pilot-<version>-<rid>.zip` for win-x64, win-arm64 and linux-x64
to the GitHub Release. The repo is private, so sign gh in first (`gh auth login`).

1. Unzip the release for your machine into a folder on `PATH` (`%LOCALAPPDATA%\pilot2\bin` by convention). It holds
   `pilot-node`, `pilot`, `pilot-gui` and `pilot-tui`, and a `VERSION` marker.
2. Run `pilot-node setup` (with `--repo-root <org root>`, and `--machine <prefix>` if two machines' names start
   alike). It installs the hooks into `~/.claude/settings.json`, starts the node at logon and now, and turns the LAN
   on.
3. Open `pilot-gui` and go to **Settings** (Ctrl+8) for what is left: trusting the watchtower's folder once, gh
   signed in, and usage figures from a status line.

From a source build instead, `pilot update --adopt` moves the folder onto releases.

## Update

The node keeps itself current. `--update-policy` is `auto` (the default: it installs a newer release at once; running
jobs carry on and the new node picks them up), `ask` (a card with **Update**) or `off`. `--update-channel` is `stable` (the tagged releases, native AOT)
or `beta`: every push to main that builds and passes its tests, as a framework-dependent Debug build that needs the
.NET 10 runtime with ASP.NET Core. A beta version is the next patch after the latest tag, numbered by the CI run
(`2.0.4-beta.292` is run 292). Both are on the GUI's Settings page.

```bash
pilot update --check     # what is installed, and whether a newer one is out
pilot update             # install the latest now, whatever the policy
```

An update swaps the files in place and restarts the node; flights, cards, workers and the watchtower carry on.

`pilot node restart [why…]` restarts the node in place the same way, without an install, so a session never kills the
process itself: the node starts its successor and stops, jobs are not waited for and the next node follows them again, and
the answer says how many carry on.

## Use it

```bash
pilot status [--all]                      # flights in the air (--all: landed too), their legs, and the live sessions
pilot summary [--fleet] [--fresh]         # "where are we": what needs you, where it is (one Haiku call, kept with its age); --fleet covers every node
pilot issue create "<title>" --body "<text>" --repo <checkout>
pilot flight create <issue> --repo <checkout>   # the flight, its plane made
pilot flight plan <code>                  # a planner plans the legs; then they fly
pilot flight merge <code> <head>          # once its review has passed and CI is green
pilot nodes                               # the fleet; pilot --node <host:port> status reads another node
pilot job <id> cancel [--force]           # stop a job; --force kills its whole process tree at once
pilot mcp                                 # the same commands as an MCP server over stdio; an update hands the session to the new binary
```

The GUI has the same with buttons: Home, Sessions, Flights (with a new-flight wizard from a repo's issues), Repos,
Background tasks, Fleet and Settings. A flight's planner, workers, validator and reviewer report back through the same
CLI, so the black box records who did what.

Every list of sessions (`pilot sessions`, the GUI's Sessions page and Home, merged across nodes in the fleet) shows
which node a row runs on: a name without a machine prefix (`[a-z]{2}[0-9]+-`) is drawn with its node's code before it,
`se1-stargazer`, so an unprefixed ad hoc session on SE1 can be told from one on DE1. A prefixed name is shown as it is.

## Build and test

```bash
dotnet build src/Pilot.slnx -c Release
dotnet test src -c Release
```

`Pilot.Gui` compiles the sibling `DIR.Lib`, `SdlVulkan.Renderer` and `AppShell` clones when they sit beside this
repo, and restores the packages otherwise, as CI does. See [`src/README.md`](src/README.md) for publishing, the wire,
and every command.

## What is where

| What | Where |
| --- | --- |
| `src/` | v2: `Pilot.Node`, `Pilot.Cli`, `Pilot.Gui`, `Pilot.Tui`, `Pilot.Contracts` and their tests |
| `%LOCALAPPDATA%\pilot2\` | the node's socket, settings and store (`snapshot.json`, `journal.jsonl`, `blackbox.jsonl`), and the watchtower's folder; `~/.local/share/pilot2` on Linux |
| `<repo>/.claude/worktrees/<code>` | each flight's plane, on `flight/<code>` |
| `pilot.py`, `web/`, `plugins/`, `tests/` | v1, below |

## v1

v1 is the Python predecessor: `pilot.py` (the CLI, the budget hook and a dashboard server), the `web/` dashboard,
and the `plugins/` loaded into a pilot session and its workers. A pilot was an interactive session with
`--plugin-dir plugins/pilot` that kept a Markdown flight plan and started a `claude --bg -w` worker per leg; the
dashboard (`python pilot.py dashboard`, http://127.0.0.1:5181/) was where you watched and answered it. It stays in the
repo until v2 has replaced every part of it, and its offline suite still runs with `python -m unittest discover tests`.
Its full documentation is the README as of
[`145c28f`](https://github.com/SharpAstro/pilot/blob/145c28f/README.md).

## Licence

pilot is free software under the GNU General Public License, version 3 ([LICENSE](LICENSE)).
