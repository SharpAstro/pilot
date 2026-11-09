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
- A worker runs `dotnet test` and the build of a solution or test project only through `pilot run -- <command>` or the
  leg's check; a hook refuses them run directly and names the way. The node also looks every few minutes for strays in
  its own process tree (a job's or session's leftovers), raises a card for each with the kill command, and ends only
  what it launched once its owner has ended. A process of yours that outlives its job or session is flagged.

## How a flight merges

- main is protected by a merge queue that **squashes** each PR into one commit (the ruleset's `merge_method` is
  `SQUASH`). So a branch's own history never matters here: merge commits on it, a merge of main that resolved
  conflicts, many small commits, all land as one commit. A rebase that would conflict is no reason to hold a PR.
- Take main by a merge (`git fetch origin`, then `git merge origin/main`), never by a rebase, and never push with
  `--force`. A repo whose queue rebases says so in its own PILOT.md; tianwen's squashes too since 2026-11-08.

## Sessions and the watchtower

- A watchtower the node started is known by its **session id**, whatever it is called now. Any other role is read from
  the name when the node first sees the session, and kept by row after that, so a later rename does not lose it. Claude
  Code retitles a session after its work, and a resume or a compaction can continue it under a new id; a rename or a
  retitle is never a reason to stop or replace a session (#560). A name still counts at first sight, so do not name
  something `*-watchtower` that is not one.
- A resume keeps the name and model the node started the session with. Resumes and messages go through the node's own path
  (`SessionMessenger`, which builds them with `ClaudeCliDelivery`), never a bare `claude --resume`.
- Only the node stops a session, and it records why in its session ledger. The reason starts with a `SessionEndReasons`
  constant (`pilot session end` stores `<by>: <why>`): a replaced watchtower, a duplicate, a flight that landed or merged,
  or `pilot session end` from a person or the watchtower. A session that finishes or crashes on its own is out of scope;
  the ledger closes it as finished or crashed. A session never stops another with `claude stop`.
- One live watchtower per node. A change to how the node finds, messages, resumes or stops a session keeps
  `OneWatchtowerTests`, `WatchtowerTests` and `ResumeOptionsTests` passing.
- The fleet summary is kept by the watchtower: the node has it remade after changes while a client is attached, asks
  nothing with none, and `pilot summarise` and the panel's Refresh ask at once. The Haiku call runs only when the
  watchtower is down (`SummaryRow.NotFollowing` says so); only then does the panel name a price.

## Build and check

Always `-c Release`, as CI does; a Debug build of a consumer of a Release package can fail with a misleading
`CS0234` about a missing `Diagnostics` namespace.

```bash
cd src
dotnet build Pilot.slnx -c Release
dotnet test Pilot.Node.Tests -c Release --no-build
dotnet test Pilot.Gui.Tests -c Release --no-build
```

- A `Pilot.Node.Tests` test over 5 s fails on CI (GITHUB_ACTIONS=true or PILOT_TEST_TIME_STRICT=1) unless tagged `[Trait("Category", "Slow")]`; elsewhere a `slow-test:` line on stderr reports it without failing, since a loaded machine inflates wall clock.
- A git run costs 100 to 400 ms on Windows, so the git-backed `Pilot.Node.Tests` bases (`ReviewTestsBase`, `LegCommanderTestsBase`, `OverlapTestsBase`, `PrMarksTestsBase`, `LegHoldTestsBase`)
  run on `FakeRepoGit`, an in-memory git (`TestGit`), unless the test is about git itself: a test calls `RealGit()` first, or a class passes
  `realGit: true`. A command the model cannot answer throws, so the test says it needs real git; `PILOT_TEST_REAL_GIT=1` runs everything on real
  git, and `FakeRepoGitTests.TheModelAnswersAsGitDoes` holds the model to git's answers.
- Repo-wide guard tests carry `[Trait("Category", "Invariant")]` and run in seconds (`--filter Category=Invariant`); a new repo-wide guard gets the trait too.
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

## Human verification

- Checks only a person can make (hardware, UI, real-window feel: what tests and CPU captures cannot settle) are filed as
  issues in the milestone `Human verification: UI that needs eyes` (milestone 12 in pilot).
- Only a flight whose issue carries the `pilot: human verification` label files them, from the `## Human verification`
  heading of the reviewer's report. Without the label nothing is filed.
- Each issue says what to do and what to look for. The controller closes it once checked; a worker never does.

## Writing

No em-dashes, and no `--` in their place: a full stop, a comma, a semicolon or parentheses. This holds for prose,
comments and commit messages.
