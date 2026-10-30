# PILOT.md

What a plane can and cannot do in this repo. The node quotes this page whole into the planner, worker and reviewer
briefs, so it stays short. v2 is the solution under `src/`; v1 (`pilot.py`, `web/`, `plugins/`, `tests/`) is Python and
is neither built nor tested by CI.

## What a plane can and cannot do

- A plane is a git worktree at `<repo>/.claude/worktrees/<code>` on `flight/<code>`. Work and commit only there. Do not
  push, open a PR or merge; the controller lands the flight. Leave nothing uncommitted in the plane, and put scratch
  output outside it.
- Sibling clones are not yours. `Pilot.Gui` compiles the sibling `DIR.Lib`, `SdlVulkan.Renderer` and `AppShell` source
  when they sit beside this repo, and restores their packages otherwise (as CI does). Editing a sibling changes this
  build with no version bump, so a change that spans repos is not a step of this plane; name it and leave it.
- The live node is the controller's, one per machine. A plane must not start, stop or restart it, install an update,
  or run `pilot-node setup`: it would touch the real socket, settings and store under `%LOCALAPPDATA%\pilot2\`. Tests
  run a node in memory or on a temporary socket; use those.
- A GUI or TUI window cannot be started from a plane and looked at: there is no display to rely on, and a window that
  opens must show what it is. Local-only starts (the GUI, the node, anything needing the real machine) are not planned
  as a step, or are planned with the way round named: the CPU-rendered tests (`Pilot.Gui.Tests`) draw a page from a
  sample snapshot with no display and no GPU.
- The inspector (the debug layer of `DIR.Lib.Diagnostics` and its renderers) exists only in a Debug build. A Release
  build, which is what CI and the checks use, does not have it, so nothing here may measure through it.
- Never wait with a job that sleeps or polls: it holds a slot of the queue and blocks every job behind it. Use
  `pilot leg wait --for-file <path> [--until <time>]` and end the turn; the node wakes the leg once. Long builds and
  test runs go through `pilot run -- <command>`.
- The machine is shared with other flights, so a job may wait its turn first. That costs the leg nothing.

## Build and check

Always `-c Release`, as CI does; a Debug build of a consumer of a Release package can fail with a misleading
`CS0234` about a missing `Diagnostics` namespace.

```bash
cd src
dotnet build Pilot.slnx -c Release
dotnet test Pilot.Node.Tests -c Release --no-build
dotnet test Pilot.Gui.Tests -c Release --no-build
```

- A `Pilot.Node.Tests` test over 5 s fails, naming itself, unless it is tagged `[Trait("Category", "Slow")]`; CI runs the slow ones in their own step.
- CI builds the whole solution and runs exactly two test projects, `Pilot.Node.Tests` and `Pilot.Gui.Tests`, on
  ubuntu and windows (win-x64). A check that matches CI runs both; a narrower one filters with
  `--filter FullyQualifiedName~<Name>`.
- Tests are xunit.v3 with Shouldly. Code targets `net10.0`; there is no `global.json`.
- No AOT publish runs on a push to main. Native AOT is only in `release.yml`, on a `v<major>.<minor>.<patch>` tag, so
  trim and AOT analyzer warnings do not show in an ordinary build.
- `MAJOR.MINOR` is written once, as `VersionMajorMinor` in `src/Directory.Build.props`. Do not hand-edit anything
  derived from it.
- v1 has its own offline suite, `python -m unittest discover tests`. Run it only when a leg changes v1. The command is
  `python`, never `python3`, and the shell is `pwsh`, never `powershell`.

## Measuring

- A number about the node's behaviour comes from a test or from the node's own state read through the CLI
  (`pilot status`, `pilot jobs`), not from starting a second node.
- Rendering is measured by the CPU-rendered GUI tests and the text-over-text overlap check that runs in them, not by
  a screenshot of a live window.
- The wire is versioned (`NodeWire.Version`); a change to a contract type is checked by the contracts' tests and the
  node and CLI tests that use it.

## Done-when kinds and what each needs

- **Build and test passes:** the Release build and the named test filter, run in `src`. Name the project and the
  filter; a bare `dotnet test` at the repo root is not the check.
- **A file exists or has a property** (line count, no em-dashes, a section present): a test or a short command that
  reads it. Prefer a test that finds the repo root by walking up from the test's directory to the one holding
  `Pilot.slnx` under `src`.
- **A page or window looks right:** needs the CPU render of a Gui test; a live window is the controller's to look at.
  Say so in the Done-when instead of promising a screenshot.
- **A behaviour of the live node, an update, or a fleet peer:** cannot be checked from a plane. Check it by a test
  against the in-memory node and say that the live run is the controller's.
- **A change in a sibling repo or a published package:** not checkable here; it is its own flight. A restore failing
  NU1101 or NU1102 on a package a sibling is publishing is held by the node, not fixed in the plane.
- **Docs only** (`README.md`, `src/README.md`, this file): the Release build still runs, since CI does for any change
  under `src`, and a PR touching nothing of `src` skips the build and passes the gate.

## Writing

No em-dashes, and no `--` in their place: a full stop, a comma, a semicolon or parentheses. This holds for prose,
comments and commit messages.
