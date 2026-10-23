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
| `Pilot.Cli` | The CLI, `pilot`: the node's commands, for the controller, workers and scripts; native AOT. |
| `Pilot.Gui` | The GUI, `pilot-gui`: every session on the machine as a row, live from the node, and a bar to say something to the selected one; on DIR.Lib and SdlVulkan.Renderer, native AOT. |
| `Pilot.Gui.Tests` | xunit.v3 tests rendering the sessions view on the CPU from the sample snapshot, the live feed on a fake stream, the say bar, and a flight's page (its legs, checklists and artifacts) as a view model, on pixels and in cells. |

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
pilot-gui [--node-socket <path>] --capture out.png --page flights --flight TIA1611   # the page of one flight (legs, checklists, artifacts)
pilot-gui [--node-socket <path>] --say <session> <text...>   # say it through the node, print what became of it, exit
pilot-gui --snapshot <file.json> [--capture out.png]         # a snapshot file instead of the node (the contract's sample: Pilot.Contracts/Fixtures)
```

- **Node:** with no node on the default socket, the GUI starts one (`pilot-node` beside it, or the source tree's
  build of the same configuration), detached so it outlives the window; a socket named with `--node-socket` or
  `PILOT_NODE_SOCKET` is only connected to.
- **Rail:** the left rail, as tianwen's: Sessions (Ctrl+1), Flights (Ctrl+2), Background tasks (Ctrl+3), Fleet
  (Ctrl+4) and Settings (Ctrl+5).
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

## Install and update

A release is a tag: pushing `v2.0.<n>` on `main` (its `2.0` must be `VersionMajorMinor` in `Directory.Build.props`)
runs `.github/workflows/release.yml`, which publishes all four binaries with native AOT and attaches
`pilot-<version>-<rid>.zip` for win-x64, win-arm64 and linux-x64 to that tag's GitHub Release. Main's pushes build and
test, and publish the beta channel (below); no AOT minutes are spent on them. Each zip carries a `VERSION` marker (`<version> <rid>`, then its files),
which is what makes a folder an **installed release**.

Install one by unzipping it into a folder on `PATH` (`%LOCALAPPDATA%\pilot2\bin` by convention) and running
`pilot-node hooks install` from there. A folder holding a build of one's own (no marker) is moved onto releases with
`pilot update --adopt`.

`pilot-node setup` does the rest for a machine joining the fleet (see *Another machine in the fleet*). Under it,
`pilot-node autostart install -- <node options>` starts the node at logon with those options (none, once the settings
file holds them), and now if none answers
on its socket: a value in the user's `Run` key on Windows (through `conhost --headless`, so no console window), a
systemd user unit on Linux (`Restart=always`, so a self-update's restart is the unit's own; exit code 3, another node on
the socket, is not retried). The options are checked by the node's own parser before anything is written.
`autostart show` says what is there, `autostart remove` takes it out. Without it the node runs while something
started it: by hand, or the GUI, whose node listens on its socket only, never on the LAN.

An installed node keeps itself on the latest release by `--update-policy`:

| Policy | When a newer release is out |
| --- | --- |
| `auto` (default) | The node installs it once no job is running or waiting (a job is its child, and a restart would cut it short), and says so on a Handled card. |
| `ask` | A For-you card, *pilot 2.0.12 is out*, with **Update**. |
| `off` | It does not look. |

`--update-channel` (the Settings page's **Update channel**) says which releases it follows:

| Channel | Follows |
| --- | --- |
| `stable` (default) | The tagged releases, native AOT. |
| `beta` | Main as it moves. Every push to main that builds and passes its tests also publishes a framework-dependent Debug build of each runtime (a minute, no AOT) as the zips of the one `beta` prerelease, replaced each time; its title is their version, the next patch after the latest tag as a prerelease numbered by the run (`2.0.4-beta.57`). A beta node takes the newer of that and the latest release, so tagging `2.0.4` moves it onto the release, and the next push onto `2.0.5-beta.N`. It needs the .NET 10 runtime with ASP.NET Core's on the machine. Back on stable, a node waits for a release newer than the beta it runs rather than going back. |

A prerelease is never GitHub's latest release, so a stable node never sees the beta. A tag's release run also deletes
the Actions artifacts earlier runs left (the zips that matter are release assets, which Actions storage does not count).

It looks a minute after it starts and every 6 hours (15 minutes on beta), through `gh` (the repo is private, and gh's login reads it).
`pilot update` installs the latest now, whatever the policy; `pilot update --check` only says. An install downloads the
runtime's zip into `update/` beside the binaries, checks it against the SHA-256 GitHub lists, runs the staged
`pilot-node --version`, then renames each file it replaces to `<name>~old` and copies the new one in, the marker last;
one that cannot finish is put back. The node then starts the new `pilot-node --after <its pid>` with its own arguments
and stops; the new one waits for it and takes the socket. Flights, legs and cards are the store's and carry on, clients
resume their stream, workers and the watchtower keep running. A GUI or TUI open at the time runs on from its renamed
binary until it is reopened; the `~old` files go at a later start, once nothing runs from them.

The update code (`Pilot.Node/Updates`: `ReleaseFeed`, `ReleaseInstall`, `UpdatePolicy`) holds no pilot type: it began
as a copy of YtPlayer's self-update and is meant to move to SharpAstro.AppShell once a second app updates itself the
same way. `NodeUpdater` is pilot's use of it.

## Flights

A flight is one issue's work on one repo, coded by the node from the repo's prefix and the issue (`TIA1611`, then
`TIA1611a` to `e` for further flights on it; a code is never given twice on the machine, landed ones included). Its
**plane** is one worktree and one branch, `<repo>/.claude/worktrees/<code>` on `flight/<code>`, made from the repo's
default branch: every leg flies on it in order and it lands as one PR. A session working in a plane's worktree belongs
to its flight, however it was started; a v2 worker's name says it too (`du1-tia1611-parser`, with the machine's
prefix, `--machine` or `PILOT_MACHINE`, else from its name).

| Command | What it does |
| --- | --- |
| `create-flight repo/<path> <issue> [title…]` | Codes the flight and makes its plane; `<path>` may be any checkout of the repo, a worktree included. Without a title it takes the issue's; the result names the code it made. |
| `plan-flight flight/<code>` | A planner, `<machine>-<flight>-plan`, in the plane: it reads the issue and the code and plans the legs (`plan-leg`), writing no code, then says `planned`. |
| `planned flight/<code> [summary…]` | The planner is done: a For-you card lists the legs, with **Start** on the first. No leg may be named `plan`. |
| `connect flight/<code> <other>` | The flight holds until the other has landed (a cycle is refused). |
| `land flight/<code>` | Its work is in: the worktree goes when clean (never with changes not committed, nor with a commit on no remote and not in its base), the branch and code stay, and flights waiting for it are cleared. |

**Nothing is left behind.** A flight's work reaches main or is said, never kept where nobody looks: a leg finishes as
done only with every change committed (its worker's brief says so, and the node refuses a dirty plane); a merge is
refused while the plane holds commits past the reviewed head, so a late commit cannot miss the PR; landing by hand is
refused while the plane holds a commit on no remote and not in its base (compared by patch, so rebase-merged work
counts as in); a flight whose PR was merged on GitHub with something left out does not land but rings with it. Before
any of that, a session ending its turn in a plane with files not committed is sent back to commit them (the Stop gate,
under Hooks). Once
landed, the plane's worktree is removed and its branch kept. PRs are merged by rebase unless the node's `merge-method`
says otherwise, so every commit's subject, which a repo's docs may cite, reaches main.

### Legs

A flight's legs fly in their order on its plane, one at a time, each by a fresh background worker named
`<machine>-<flight>-<leg>` and briefed from the leg: its goal, checklist and Done-when, the legs before it, and the
house rules (work in the plane, commit, never push). A leg is done only with every step ticked and its `check`, rerun
by the node in the plane, passing. A go-around stops the worker and starts a fresh one told why and what the plane
holds (its commits and diff), never the last worker's conversation. A worker's row is measured against its leg's
budget.

**A stale sibling is said before the leg starts.** A repo's `UseLocal*` properties compile against the sibling clones
beside it (`../DIR.Lib`) whatever state they are in, so a clone behind its origin fails a leg's check with errors that
look like the leg's own. Before `start-leg` the node reads the plane's `Directory.Build.props`, `.targets` and `.csproj`
files for `UseLocal*` properties and the siblings their `Exists()` probes name (a probe of the repo's own files or a
package folder names none), fetches each clone that is there (quietly, 20 seconds at most, the siblings at once) and
counts its commits behind its upstream. Any behind raise one For-you card, `sibling-behind`, about the leg: each
sibling, how far behind, and the `git -C <path> pull --ff-only` to run. The leg starts regardless, and a sibling that
cannot be asked (no upstream, no network) is left out. Each sibling is asked at most once in five minutes, so legs
started in a row cost one fetch; the card closes when the leg is done or goes around.

| Command | What it does |
| --- | --- |
| `plan-leg flight/<code> name=… goal=… done-when=… [check=…] [budget=…] [model=…] [effort=…] [item=…]…` | A leg at the end of the order, or a planned one replaced. |
| `remove-leg flight/<code>/<leg> [why…]` | Takes a planned leg out of the flight; refused, with the leg's state, for one flying, done or failed. |
| `start-leg flight/<code>/<leg>` | Its worker: `claude --bg -w <plane> -n <name> --permission-mode auto` from the repo's main checkout (a plane git made is not a trusted folder of its own; `-w` enters it under the repo's trust). |
| `check flight/<code>/<leg> <n or text> [undo]` | Ticks a step. |
| `finish-leg flight/<code>/<leg> done\|failed [report…]` | Done reruns the check; a failing one leaves the leg flying with the output in its report. |
| `go-around flight/<code>/<leg> <why…>` | A fresh worker for a flying or failed leg. |
| `merge flight/<code> <head> [squash\|rebase\|merge]` | With every leg done: `gh pr merge --match-head-commit`, by the node's `merge-method` (rebase unless set otherwise) when none is named; refused when the PR moved past the head you reviewed, or the plane holds commits past it; then the flight lands. |

**A planned leg can be taken out** (`remove-leg`, `pilot leg remove <code> <leg> [why]`, **Remove** on the Flights page
for the next planned leg, which half-writes the command bar for its why): a leg planned by mistake, or made redundant by
an earlier one, no longer waits in the order for the node to fly it. Only a planned leg goes; a flying, done or failed
one is refused, naming its state. The legs after it close up the order (so their numbering and the next `plan-leg`
stay contiguous), a Handled card says the leg went, who took it out and why, and any card offering its start closes.
The flight then goes on past it as it does after a leg is done: its next planned leg starts, or, with every leg done,
its PR opens. The removal is journaled as a `legRemoved` event (the renumbered legs as `legChanged` ones), so a
restarted node and a client resuming the stream both replay it.

### Reviews

`flight pr` pushes the plane (every time, so a go-around's commits reach the PR) and starts a fresh reviewer,
`<machine>-<flight>-review`, in the plane: briefed with every leg's goal and Done-when, the commits and the files, told
to read the diff and rerun what proves each Done-when, and to change nothing.

The node picks the review's **depth** when it starts it (`ReviewPlan.Choose`), from the usage figures (fresh ones only,
under 30 minutes old, of a window that has not reset), whether the repo is in `priority-repos`, and the diff's size
(`git diff --numstat` against the base: lines changed and files touched). The thresholds are named constants, not settings.

- **light**: the 5-hour share is at 80% or more, or the 7-day at 90% or more. One pass, no subagents, an effort one
  step below the reviewer's (low when none is set).
- **deep**: both windows below 50% and 70%, and a large diff (400 lines or 15 files; 200 or 8 for a priority repo).
  An effort one step above the reviewer's (high when none is set), the model from `reviewer-deep-model` when set, and up
  to three focused subagents, each checking one claim against the tree; `reviewer-deep-effort` replaces the step.
- **standard**: anything else, and any case where no fresh figures or no diff measure say otherwise: the reviewer's own
  model and effort, on its own, with no subagents or one at most.
- A **priority repo is never lighter than standard**, however high the usage; its deep threshold is halved.

The brief says the depth and why (`Depth: light (usage at 88%).`), the review row keeps both, and the PR-ready card
carries them (`Reviewed lightly, usage at 88%: <the reviewer's words>`).

- `pilot review pass <report>` from the plane offers the merge: the PR-ready card, with the review's words.
- `pilot review changes <changes>` rings with **Go around** (the last leg, told the changes; a done leg may go around
  for a review) and **Ready anyway**. The go-around's finish pushes and starts a fresh review by itself.
- GitHub is told the verdict (`gh pr ready`) before the node keeps it: **changes** mark the PR a draft (`--undo`), so it
  cannot be merged on GitHub with the fixes pending, and a pass (the reviewer's, or "ready anyway") marks it ready
  before the merge card is raised. A gh that fails refuses the verdict, which the reviewer says again. A PR opened for
  a review round starts ready, as the review runs on it; the merge card's merge still refuses a moved head.
- A passed PR whose checks then fail is taken as changes, once a head: the PR goes back to draft, the merge card
  closes, and the review's ring offers **Go around**, told to read the failed checks and make them pass on every runner.
- Only the reviewer gives the verdict; the controller may pass one that asked for changes ("ready anyway"); the
  watchtower may do neither. A merge needs a passed review. A reviewer that stops without its verdict, or never shows
  up, rings with **Review again** (`pilot flight review <code>`). No leg may be named `review`.

### Liveness: no flight derails silently

The node watches every flying leg's worker (`LegWatch`, every 30 s) and never leaves a stalled one unnoticed:

| The worker | What the node does |
| --- | --- |
| stopped by the usage limit, with a reset time | A For-you card saying when it resumes; 2 min after the reset, says "carry on" to it (resumed as a real turn) and closes the card. Stopped again on the same reset, it rings. |
| stopped by the API with no reset time | Rings, with **Resume** and **Go around**. |
| idle (or ended) with its leg unfinished, 3 min past its last answer | Nudges it, twice at most, with what is left and how to finish or give up; then rings, with **Nudge** and **Go around**. |
| waiting on a person | Rings with what it waits on; it is answered in its own session, or gone around. |
| failed, or never showed up 3 min after its start | Rings, with **Go around**. |

Each nudge and resume is a Handled card. The watch's cards close by themselves once the worker works again, and a
go-around or a finished leg closes every card about the leg. What it resumed and nudged is kept in memory, so a node
restarted mid-flight may nudge a worker once more than it would have: a turn, never a stall.

The GUI's rail opens the **Flights** page (Ctrl+2): each flight's code, title, repo and issue, state (held: waiting
for which), legs (how many done, which flying), branch and sessions. As the window narrows it drops Sessions, then Branch.

**A session's transcript** opens in place of the table: double-click its row, press Enter on it, or **Open transcript**
in the header. It shows what was asked, what the assistant said, and each tool call in a line with what it gave back
folded under it to its first lines (**+N lines** opens the rest). What the assistant wrote, its thinking and a
subagent's report read as Markdown (DIR.Lib's `Layout.Builder.Markdown`): headings, lists, tables, code and links,
wrapped at the pane's width; a link opens in the browser, and only a web link opens. A dot gives the call's state: green done, red failed
or refused, amber running, grey cut off by the session ending. A refused call says what refused it (auto mode and its
reason, a safety check, don't-ask mode, a permission rule, or you) with the command and **Copy the command**. Prompts
read as a person says them: Claude Code's reminders left out, a background task finishing or a command's output as a
dim note, a slash command as typed, a subagent's hand-back under its name. It follows the session live, pinned to its
end until you scroll up, when **Jump to end** follows it again; the header says whether the session is live or ended,
and the say bar stays under it. What you said while the session was busy shows last, dim, as **queued**, until the
session reads it at its next step. A card about a leg's worker (stalled, idle, past its budget) has **Its
transcript**: the worker's transcript from the moment the card was raised, that entry marked, until **Jump to end**.
Escape, or **Back to the sessions**, goes back. The node reads each transcript on from where its last read stopped
(`GET /api/v1/sessions/<id>/transcript?after=<n>`, a grant's on the LAN).

**Subagents.** A session that started subagents says how many under its name (**▸ 2 subagents, 1 working**); a click
there, or Right on the selected row, lists them under it (Left puts them away): each one's type, what it was asked, and
whether it is working or done, from Claude Code's record of it (`agent-<id>.meta.json`) and its hand-back. A click on
one opens its own transcript (`…/transcript?agent=<id>`, only an id the row lists). In a transcript, chips go between
the session's own and each subagent's, and the call that started a subagent, or its hand-back, leads to it.

Ended sessions can be put away: **Archive** on a selected ended row, **Archive ended (N)** in the Sessions header for
all of them, and **Show archived** to list them again (with **Restore**). An archived session is only left out of the
lists; it stays a row until the node forgets it as any ended one (24 h), and one that runs again is back by itself.

**New flight**, in the Flights header, is the way to start one without typing: pick the repos (in the order their
flights land, a library before the app that takes it), an open issue in each, and Create. Each repo gets a flight on its
issue, waiting for the one before it, and a planner; its card offers the first leg's start. The repos are the git clones
under the node's repo roots (`--repo-root`, `PILOT_REPO_ROOTS`, and the folders the flights' repos sit in), the issues
come through gh, and the TUI has the same wizard.

The **Sessions** page drops Spend, Last response, the archive button and Model as the window narrows (in pilot-tui too), so
the Session column keeps room for the role and the session name.

Every page is laid out by the layout engine, never by hand: rows size to their content, a column that cannot have its
whole width collapses (`CollapseBelow`), and nothing multiplies by the DPI or measures a width to choose what to show.
`LayoutDrivenTests` reads the views' sources and fails on a computed size, a hand-applied DPI or a width breakpoint, as
tianwen's own checks do.

## The CLI

`pilot` (Pilot.Cli, native AOT) spells the node's commands for the controller, the watchtower, workers and scripts. All three binaries parse their command lines with System.CommandLine, as tianwen's CLI does, so `--help` on any command lists its arguments and options:

```bash
pilot status                                   # flights, their legs and artifacts, and the live sessions
pilot sessions
pilot flight create <issue> [title…] [--repo <path>]
pilot flight plan <code>                       # a planner plans the legs from the issue
pilot flight planned <code> [summary…]         # what the planner says when it is done
pilot flight connect <code> <other>
pilot flight pr <code> [title…]                # pushes the plane, opens its PR (body: the legs, Closes #<issue>)
pilot flight merge <code> <head> [--method squash|rebase|merge]   # or --squash, --rebase, --merge
pilot flight land <code>
pilot leg plan <code> <name> --goal … --done-when … [--check …] [--budget …] [--model …] [--effort …] [--item …]…
pilot leg remove <code> <leg> [why…]           # takes a planned leg out of the flight
pilot leg start <code> <leg>
pilot leg finish done|failed [report…]         # done: the node reruns the check
pilot leg go-around <why…>
pilot check <step number or text> [--undo]
pilot artifact <path or URL> [title…]
pilot say <session> <text…>
pilot compact <session> [focus…]               # an idle background session's /compact, as its next turn (see Compaction)
pilot note <text…>
pilot update [--check] [--adopt]               # the latest release, installed where the node runs from (see Install and update)
pilot archive <session> | --ended [--undo]      # put ended sessions away, out of the lists (or back)
pilot run [--timeout 90m] -- <command…>        # a long run handed to the node as a job (see Jobs)
pilot jobs                                     # the node's jobs, running first
pilot job <id>                                 # one job, and where its whole output is
pilot job cancel <id>                          # stops it, running or waiting; its owner is told
```

Inside a plane a leg command names no leg: the flight is the one whose worktree holds the folder, the leg its one
flying, and the command is sent as that leg's worker, so the black box says which worker ticked what. A worker's brief
gives it these commands by the CLI's full path (beside the node, else the source tree's build), with `--node-socket`
when its node is not on the default socket. Exit codes: 0 done, 1 refused by the node (it says why), 2 usage, 3 no node.

### Jobs

A full test suite, a build or a training run is a job, not a worker's turn: `pilot run -- dotnet test` hands it to
the node, which runs it in the folder it was handed from, and the worker ends its turn instead of sitting in a poll
loop. When it ends (by itself, on its timeout, 2 h by default and 8 h at most, or cancelled) the node wakes the session
that handed it over with how it ended, its last lines and the path of its whole log. Jobs from every flight share the
machine: `pilot-node --max-jobs <n>` run at once (1 by default) and only with `--job-memory <GB>` free (2 by default);
the rest wait in the order they came, each saying what it waits on. A job's log is kept in the node's data folder
(`jobs/<id>.log`), and its end is served for the GUI (`GET /api/v1/jobs/<id>/log`, a grant's on the LAN).

A worker that finishes its own leg is stopped after 90 s, once it has said its report. Claude Code tears down a worktree
its `-w` session entered when that job ends by itself, keeping the branch; the node checks a plane out again from its
branch before any worker goes in, and lands one that is already gone.

## The GUI

`pilot-gui` flies a flight on its own. The rail has **Sessions** (every Claude session on the machine: model, effort,
context against its window, last response, state, spend, usage in the header; a bar to say something to the selected
one) and **Flights**. On Flights the selected flight shows its legs (steps, go-arounds), its review, its PR and
artifacts (a click opens them), and what can be done with it now: **Start <leg>**, **Open PR**, **Review again** run
at once; **Go around** and **Plan a leg** half-write the command bar for the words they need. The command bar (Ctrl+K)
takes the CLI's own commands, run in-process through the same command tree (`Pilot.Commands`) the `pilot` binary and,
later, an MCP server (#70) host: `flight create 1611 --repo C:\src\tianwen`, `leg plan …`, `cards`.

**A flight's page** opens in place of the table: Enter on the selected flight, a double click, **Open flight** in its
panel or right-click menu; Escape or **Back to the flights** goes back. The wheel scrolls it, and so do Up and Down (a
line), PageUp and PageDown, Home and End, which is how pilot-tui reaches the lower legs. Its header has the flight's state, its PR and
review, and the same actions as the panel. Each leg follows in the flight's order as a block: its state, how many steps
are ticked and its go-arounds, then its **Goal**, **Done when** (and the **Check** the node reruns, when it has one),
every checklist step as `[x]` or `[ ]`, the worker's **Report** (**Why it failed** on a failed leg) and the artifacts
its sessions recorded (`pilot artifact`), each at its latest (a title recorded again replaces the earlier one), newest
first. An artifact's title opens it: a URL in the browser, a path in its viewer, and a path also has **folder**, which
opens the folder it is in. All of it is the node's own leg rows (`LegRow.Checklist`, `.DoneWhen`, `.Artifacts`), so the
page needs nothing the node does not already send, and pilot-tui shows the same page (the view model is
`FlightDetail` in `Pilot.Ui`). `pilot-gui --capture out.png --page flights --flight <code>` renders one, with its text
in `out.png.rows.txt`.

The right panel is the watchtower's: its state and context, its Remote Control link, a box to tell it things, then the
cards with their buttons (Merge is a card's) and what was handled.

The **Background tasks** page (Ctrl+3) is the node's jobs: running first, then those waiting in the order they will
start, then the ended, last ended first. Each row has the job's command, whom it wakes (the flight and leg, a session,
or a terminal), its state with what it is doing, waits on or came to, when it started and how long it ran; a job that
ended without its owner being told says so. The selected job shows its folder, log and timeout beside the end of its
output (its last 200 lines, read from the node every 2 s while selected), with **Cancel** while it runs or waits,
**Its session** (the transcript of the session it wakes, from when it was handed over) and **Open log**; its
right-click menu adds its folder and Copy entries.

## The TUI

`pilot-tui` is the same dashboard in a terminal, over SSH as well as locally: the GUI's own pages and panels
(`Pilot.Ui`), their DIR.Lib trees arranged in terminal cells by Console.Lib (`CellMeasureContext.PixelAuthored`)
instead of painted on pixels, so a row, a button or the command bar is the same node on both. **1** Sessions, **2**
Flights, **3** Background tasks, **4** Fleet, **5** Settings, **Ctrl+K** the command bar, **Ctrl+S** the say bar, **Ctrl+T** the watchtower's box, Up/Down to select, a
click on a cell runs what a click on the pixels would, **q** quits. The watchtower and cards panel shows beside the page
from 120 columns. `--node host:port` flies a node on the LAN, as everywhere.

The views moved into `Pilot.Ui` (DIR.Lib only, no SDL) for it: the GUI paints `Render`, the TUI arranges `Tree`, and the
selected row states itself as a background as well as a focus, since a terminal paints no focus of its own.

## The watchtower

The node keeps one watchtower session, `<machine>-watchtower`: a background session with Remote Control on, so you
reach it from claude.ai or a phone, in its own folder (`%LOCALAPPDATA%\pilot2\watchtower`, or `--watchtower-folder`,
`PILOT_WATCHTOWER_FOLDER`). It is briefed from the node's state (flights, legs, open cards, what it may and may not do),
never from an earlier watchtower's conversation, and the node says every Ring and For-you card to it as it opens.

| When | The node |
| --- | --- |
| there is something to watch (a flight not landed, a job running or waiting, a ring open) and none runs, or it ended or failed | starts a fresh one (a Handled line says why) |
| nothing to watch | starts none; one already running is stopped once it has been idle 15 min with nothing to watch (a Handled line). With nothing in the air a watchtower costs nothing; the next flight, job or ring starts a fresh one, briefed from the state |
| it is running on a brief that has since changed (a node restarted after an update, or the node's own brief settings changed) | stops it and starts a fresh one before saying it anything, with a Handled line (`its brief changed since the node started it`). A say resumes the same session, which keeps the brief it had, so only a fresh start carries the new one. The node records a fingerprint of the brief's fixed part (its template and rules, not the flights and cards it lists) in `watchtower-brief` in its data folder when it starts a watchtower, so a restarted node still compares; one with no record is taken as started by an older node |
| idle 50 min with more than 100k of context, or past 70% of its window | compacts it (see Compaction): it keeps its conversation as a summary, never replaced by a fresh one for its size |
| stopped by the usage limit | a For-you card until the reset, then resumes it |
| it cannot be started | rings with why: Claude Code starts no background session in a folder never trusted, so run `claude` in that folder once and accept the prompt |

It may answer go-around, resume and nudge cards, say things to workers, dismiss what has passed, and plan and start
legs; the node refuses a merge or a landing from it, a card's included. The CLI knows a command came from a Claude
session by `CLAUDE_CODE_SESSION_ID` and sends it under that session's name. A session the node resumes keeps Remote
Control on if it had it.

```bash
pilot tell <text…>                             # to the watchtower, whichever session it is now
pilot watchtower restart [why…]                # a fresh one, briefed from the node's state
pilot ring <subject> <title> [--detail …]      # a card that needs the controller
pilot flag <subject> <title> [--detail …]      # a card for them, no push
pilot handled <subject> <line>                 # a Handled line
```

`pilot-node --no-watchtower` keeps none.

### Compaction

Claude Code's prompt cache lives an hour, and a session woken after that reads its whole context again at full price.
The node's compactor is a heartbeat over the background sessions it will wake again: the watchtower while it has
something to watch, and any session waiting on a job of its own (a worker that handed a suite over with `pilot run`,
idle until the job wakes it). Each one idle 50 min with more than 100k of context is compacted while its cache is
still warm, and one past 70% of its window as soon as it is idle: `compact session/<id>`, which resumes it with Claude
Code's `/compact` as its next turn, its focus what the summary keeps (for a worker, its task and the job it waits on).
Below 100k a cold read costs little more than the summary, and a fresh session starts near 50k anyway. A session is
compacted once per stretch of idleness, not again until it has answered something; one mid-turn is refused, never sent
`/compact` as words, and an interactive session is never touched. Each compaction is a Handled line, and one refused
is tried again in 10 min. This replaced the watchtower's keep-warm round every 50 min: finding stalls is the leg watch's.
`pilot compact <session> [focus…]` does it by hand.

### Signals

- **Rings reach your phone.** The watchtower pushes a ring card it leaves open with Claude Code's push notification,
  once; a For-you card is never pushed.
- **The digest**, at 08:00 and 18:00 local time (`pilot digest` for one now): a For-you card of what landed, what the
  node and the watchtower handled, line by line, and what is parked (held flights, failed legs, open cards). A newer
  digest closes the last; the watchtower pushes it as one short message.
- **Budgets** are applied by the node: a worker past its leg's budget rings with **Extend** (by half) and **Go around**,
  and keeps working until you choose. `pilot leg extend <tokens>` raises it by hand.

### Usage

The node reads the account's 5-hour and 7-day usage once a minute from `rate-limits.json` in the Claude config folder,
which the controller's status line writes from the `rate_limits` Claude Code hands it, and **whose** it is from Claude
Code's `.claude.json` (its `oauthAccount`: email, display name, organisation and plan; inside `CLAUDE_CONFIG_DIR`
when that is set, else in the home folder). No token is read and no API called. The account rides on the usage row, so
it shows even where no status line writes figures, and a login changed to another account starts its figures over
rather than merging them. `pilot status`, the Sessions header (`ada@example.org (Example, team) · usage …`) and the
`?` card say it, so on a fleet of machines on different accounts it is plain whose node, sessions and usage these are. Every open session's status line writes that file from its own last response, so the node keeps only the newest
figures per window: a later reset is a new window, an earlier one is a stale writer and is ignored, and within one
window the higher share wins (v1's figures flipped between whoever repainted last). `pilot status` and the GUI's
header show it (`5h 42% (resets 14:00) · 7d 64% (resets Sat 10:00)`, with its age once it is old); past 90% of the
5-hour window a For-you card says workers will stop, once per window.

**The window holds every start the node makes by itself.** One gate (`StartGate`) serves a leg's start (the sweep's, after
a leg is done), a planner (`flight plan`), a reviewer (the PR's, a rebase's, a go-around's fresh one), the watchtower the
node keeps, and a compaction's resume (`compact`, sent by the compactor). While the 5-hour window is at or over the
`start-limit` (90 % unless set), the start waits and one Handled card says what waits and until when
(`TIA1 waits to start its reviewer: the 5-hour window is 95 % spent (the limit is 90 %); it goes on after 14:00`), once
per window however often the sweep looks. The node's sweep (every minute; the watchtower's and compactor's looks too)
takes the start once the window has reset, with no one asking again. A flight of a repo in `priority-repos` uses
`priority-start-limit` (98 % unless set) for all of these, a session of no flight (the watchtower) the plain limit. The
limits are read at each start, so a saved setting counts at once, with no restart.

- **No figures, or ones older than 30 minutes, hold nothing:** a node that cannot see the window starts as before. So
  does a window whose reset has passed.
- **A held reviewer keeps no review.** The PR is open and its review is none, so no old pass lets the merge by a head
  nobody read (a rebase or a go-around's fix is a new head); the sweep starts a review for such a flight by itself.
- **A person's own say is theirs:** `leg start`, `restart-watchtower` (its card's Try now) and `compact` typed by hand
  go ahead over a spent window. `flight plan` and `flight review` by hand are held like the node's own (the command
  succeeds, the card says it waits). A held planner is kept in memory, so a node restarted while it waits asks again
  (`flight plan`).
- **The 7-day window rings and never holds:** at or past `week-limit` (90 % unless set), the first start of each 7-day
  window raises a Ring card (`usage-week`, closed when the next window's opens), and the start goes ahead; only the
  5-hour window decides whether a start waits.

## The LAN

`pilot-node --lan` (port 47731, `--lan-port` for another) listens on the LAN beside its socket and announces itself
with LAN.Lib's beacon as `pilot-node`. **Seeing is free; a command needs a grant.** Anyone who reaches the port may
read the node, its sessions and its event stream. A command from the LAN needs a grant's bearer token, and goes out
under the grant's label (`se2-gui/gui`), so it passes for nobody here, the watchtower least. Hooks and the list of
grants stay this machine's, and so does answering or revoking a grant. Tokens are kept as hashes (LAN.Lib's
`LanGrants`); over plain HTTP one can be read off the wire, so this closes "anyone who can reach the port", not a
hostile network.

**WSL2:** LAN discovery does not cross WSL2's default NAT networking. A node running inside WSL2 is not seen by
`pilot nodes` from Windows or the LAN, because the beacon does not leave the WSL2 virtual network. Reach such a node by
its address instead: the WSL2 address (`ip addr` inside WSL2, usually `172.x.x.x`), as `pilot connect <wsl-ip>:47731`
and `pilot --node <wsl-ip>:47731`. WSL mirrored networking (`networkingMode=mirrored` in `.wslconfig`) may carry the
beacon too; the address route works either way.

```bash
pilot nodes                                    # the nodes announcing themselves on the LAN, by host:port
pilot connect 192.168.1.20:47731 [--as se2-gui]  # rings an Allow/Deny card on that node; the token is kept here
pilot --node 192.168.1.20:47731 status         # any command, against that node (or PILOT_NODE=...)
pilot-gui --node 192.168.1.20:47731            # the whole window, its buttons and command bar, on that node
pilot grants / pilot grants revoke <id>        # on the node: who may command it, and taking that back
```

An ask lives while its asker keeps reading its ticket (LAN.Lib's `LanInvites`: one at a time, gone when the asker
goes); the token comes back once, on the first read after the Allow, and lives in this user's
`<data>/client-grants.json`.

### Another machine in the fleet

Each machine runs its own node, on its own person's Claude account: a node is the one writer of its machine's
sessions, flights and jobs, and never speaks for another. To bring a second machine (a desktop beside a laptop) in:

1. **Install a release** there as above (`gh auth login` first: the releases are the private repo's, and so is the
   self-update).
2. **`pilot-node setup`** (with `--repo-root <org root>`, and `--machine SE2` where the machine name's first two letters
   would clash with another machine's prefix). It saves the LAN on (and what was given) in the node's settings file,
   installs the hooks, installs the logon entry and starts the node, and asks Windows for elevation once to let
   `pilot-node` in on private networks. It ends with the checklist: what is left is a person's.
3. **What is left**, which the GUI's **Settings** page (Ctrl+5) also lists with a button for each the node can fix:
   the watchtower's folder trusted once (Claude Code's own prompt, which the page opens in a titled window; a folder
   above it that is trusted already covers it), gh signed in, and usage figures (a status line that writes
   `rate-limits.json`; without it the node still says whose account it is, with no figures).
4. **From the other machine:** its Fleet page hears the new node by itself; **Connect** on its row (or
   `pilot connect <host>:47731`) rings an Allow/Deny card on it, to be allowed there; then **Open** flies it.

**The Fleet page** (Ctrl+4 in the GUI, 4 in the TUI) is every node at once: this window's first, then every node heard
on the LAN or granted to this user, each on a feed of its own (its event stream, watched without a grant as anyone on
the LAN may). A row says the node's machine and where it is reached, whose account it runs on and its usage, its live
sessions and how many wait on you, its flights and jobs, and its rings; a node not reached says what its connection is
doing instead. **Open** opens a window flying that node (a window per node: the one-window-per-user gate is per node,
so a second Open of the same node raises its window); **Connect** asks a node for a grant, as `pilot connect` does,
when this user has none, and the node watched only says so. A node heard twice (on its socket and on the LAN) is one
row, by its stream. In the TUI, Open says the `pilot-tui --node` to run in another terminal.

### Settings

The node's settings live in `settings.json` in its data folder, read at every start: on the LAN, its machine prefix,
repo roots, update channel and policy, jobs at once, job memory, the LAN port, and a model and an effort per kind of
session it starts (#114). The watchtower and workers start on Sonnet and planners and reviewers on Opus unless set
otherwise; a leg's own `--model` or `--effort` still wins for its worker; `none` passes no flag, leaving Claude Code's
own default. The watchtower's brief says its model, so a lighter one rings for a judgement rather than makes it. The command line wins over the
file (an autostart entry made before the file existed still pins what it names), and a key the file leaves out takes
the default; a value it cannot read is passed over, never fatal. `pilot-node setup` writes it, and so does the
**Settings** page (Ctrl+5 in the GUI, 5 in the TUI), or `pilot settings set <key> [value…]`.

A role's model and effort, and the job limits, are in force at once (the next session of that kind starts with
them; a waiting job starts as soon as the new limit allows). Any other change restarts the node to run with it, once no
job runs or waits (a job is its child): the node starts its
successor with its own arguments and `--after <its pid>`, and stops, as a self-update does, so flights, sessions and
the watchtower carry on and clients resume their stream. A setting the command line gives is saved but says it does
not count until the command line stops giving it.

Above the settings, the page has the setup checklist: started at logon, Claude Code's hooks for this binary, on the
LAN, the firewall rule for this binary (Windows), the watchtower's folder trusted (read from Claude Code's own record,
a folder above it counting), gh signed in, and usage figures. Each says how it stands; those the node can fix have a
button (`setup node <check>`): the logon entry, the hooks, the LAN, the firewall rule (one elevation prompt, which
replaces an earlier install's rule), the trust prompt (opened in a window titled for what it is). `pilot settings`
prints both. The route and both verbs are this machine's only: a grant from the LAN never reaches them, and nor does
the watchtower.

One more optional check is **Runaway-agent guard hooks** (`setup node guard-hooks`): it says whether three of
runaway-agent's generic hooks are in `~/.claude/settings.json`: `big_read_guard.sh` (refuses a whole-file `Read` and a bare `cat`),
`fat_result_notice.sh` (says a tool result was unusually large) and `pgrep_self_match_guard.sh` (refuses an inline
`pgrep -f`/`pkill -f`). It counts a guard wired by hand in any spelling (`bash path`, quoted, `$HOME/...`). Its button
takes the scripts from a runaway-agent clone, the first `source/repos/*/runaway-agent` under the user's profile that
has all three, copies them into `hooks/` beside the settings file as runaway-agent's own `install.sh` does, and adds a
handler each under the event and matcher of its `settings-fragment.json` (PreToolUse for the first, PreToolUse on
`Bash` for the third, PostToolUse for the second), leaving the rest of the file alone. A guard already wired is not
added again, a script already there with the same content is not copied again, and a second run changes nothing. The
copies would otherwise go stale, so while a clone is found, a script in `hooks/` that differs from the clone's fails the
check too, its line naming the scripts older than the clone, and the button copies them afresh. With no clone the line
says to get it from `github.com/DrawboardLtd/runaway-agent` and clone it beside the others, and there is no button. Its
token and call budgets and hand-back gates are not offered: those are for Sam's campaigner setup, and pilot budgets
legs at the session level.

A third optional check, **MSBuild node reuse off** (`setup node build-rsp`), says whether runaway-agent's
`machine/Directory.Build.rsp` is in the user's home folder, as its `install.sh` installs it. MSBuild reads one found by
walking up from the project, so every build under home, a flight's included, runs with `-nodeReuse:false` and its worker
nodes exit with the build; idle nodes had held worktree folders (a removal refused on 15 Oct) and about 40 GB. The
checklist shows it installed, missing, or differing from the clone's copy. Its button copies the file from the same
runaway-agent clone the guard hooks use; a file that differs is first copied to `Directory.Build.rsp.bak-<UTC
timestamp>` beside it, as `install.sh` does, one that is the same is left alone, and so a second run changes nothing and
makes no backup. With no clone the line says to get runaway-agent from `github.com/DrawboardLtd/runaway-agent`, with no
button (a file that is there then passes, as there is nothing to compare it with); a clone too old to have the file says
so, also with no button.

## Cards

An event that needs the controller is one card, with its choices on it (after Sam's campaigner): **Rings** (a gate
before an irreversible step, a stop: waits until someone acts), **For you** (a card, no push) and **Handled** (what the
node or the watchtower did itself: one line, born closed). A session going quiet is not a card by itself; it changes
how its row looks. A flying leg's worker stalling is: its flight is derailing (see Liveness). A card leaves when its
thing is done: answered, dismissed, or closed by the node when its flight lands or its leg finishes. Raised so far: a
PR opened with every leg done rings with **Merge** on that head; a failed leg rings with **Go around**; a landed flight
is a For-you card; a leg started with a sibling clone behind its origin raises a For-you `sibling-behind` card; the leg
watch's cards (`worker-stopped`, `-idle`, `-waiting`, `-failed`, `-missing`, and the Handled `worker-resumed` and
`worker-nudged`); and a session that stopped twice in a turn with files not committed in its plane rings
(`stopped-uncommitted`, see Hooks).

Each choice is a command; `answer card/<id> <n or label>` runs it as the one answering, through the same route as any
command, and closes the card (opening it again if the command is refused). `dismiss card/<id> [why]` closes it with
nothing done. The GUI shows open cards down the right, with their buttons, when there are any; `pilot cards`,
`pilot answer` and `pilot dismiss` do the same from a terminal.

## Hooks

Claude Code's hooks tell the node the moment a session changes, where the agents listing is read every few seconds:
working on a prompt or a tool call, waiting on a permission prompt (with its words), idle after a turn, stopped by an
API error or a usage limit. They run in every session, interactive, `--bg` and `-p` alike.

```bash
pilot-node hooks install [--settings <file>] [--node-socket <path>]   # default: ~/.claude/settings.json
pilot-node hooks remove  [--settings <file>]
```

`install` adds one handler per event (SessionStart, UserPromptSubmit, PreToolUse, PermissionRequest, Notification,
Stop, StopFailure, SessionEnd), exec-form `pilot-node hook` and `async` but for Stop, beside whatever hooks the file
has, and a second install changes nothing; `remove` takes out only pilot's. `pilot-node hook` forwards the hook's stdin
to the node and always exits 0, printing nothing but the node's answer to a Stop it holds (below), so a missing or slow
node costs a session nothing. An install from before the Stop gate shows in setup as not installed; installing again
puts it right.

**The Stop gate: nothing is left behind in a plane.** A session that ends its turn in a flight's plane (its cwd the
plane's worktree or a folder in it, of a flight not landed) with files not committed there is sent back: the node
answers `{"decision":"block","reason":"…"}`, the reason naming the files (`git status --porcelain`, the first three)
and telling it to commit or drop them, and Claude Code carries on with that as the session's next instruction. Once
per turn: a stop Claude Code marks `stop_hook_active` (one a Stop hook already sent back) is let through, and if the
files are still there the node rings (`stopped-uncommitted`, about the flying leg, with **Tell it again**); a later
stop with the plane clean closes the ring. Only a plane the node flies counts, not Claude Code's own worktrees beside
it under `.claude/worktrees`; every other session, a subagent and any other event get no answer. This is why Stop's
handler is not `async`: Claude Code never reads an async hook's output. It waits for the node at most 2 s, and no
node, a slow one or a hook it cannot read prints nothing, which lets the stop through.

A session the API stopped (its transcript's `apiError`, `usage_limit_reached` with when the limit resets) shows as
**stopped** with why and when it can go on, hooks or not.

## The wire (`NodeWire.Version` 2)

| Route | What it does |
| --- | --- |
| `GET /api/v1/node` | Which node this is: wire version, build version, machine, pid, started at, data folder, socket, stream. |
| `GET /api/v1/events` | The event stream, a WebSocket: a snapshot carrying the current number, then deltas each numbered one past the last. `?since=<n>&stream=<id>` resumes with the deltas after `n`, or a snapshot when the node no longer holds them or has another stream. |
| `POST /api/v1/commands` | Every command, `{verb, target, args, who}`. The result is the response and a `commandResult` event on the stream. Verbs: `note node <text>`, `say session/<id> <text>`, `compact session/<id> [focus]`, and the flight verbs above. |
| (cards) | `answer card/<id> <choice>`, `dismiss card/<id> [why]`: verbs on `POST /api/v1/commands`. |
| `GET /api/v1/sessions/<id>/transcript?after=<n>` | A session's transcript as entries after the n-th (its last 300 without one); a grant's on the LAN. |
| `GET /api/v1/jobs/<id>/log` | The end of a job's output: its last 200 lines from its last 64 KiB, the log's length, and whether it was clipped; a grant's on the LAN. |
| `GET /api/v1/repos` | The repos a flight can be made in, with their GitHub names; a grant's on the LAN. |
| `GET /api/v1/repos/issues?path=` | A repo's open issues, through gh; a grant's on the LAN. |
| `POST /api/v1/hooks` | One Claude Code hook's stdin, as `pilot-node hook` forwards it; always 204. |

HTTP responses come in a `ResponseEnvelope` (`response`, `error`, `statusCode`, `success`); stream events are sent
bare, each with its `kind` and `seq`. A client that falls behind is closed and resumes with `since`.
