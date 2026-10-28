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
| `Pilot.Cli` | The CLI, `pilot`: the node's commands, for the controller, workers and scripts, and `pilot mcp`, the same as an MCP server; native AOT. |
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
- **Rail:** the left rail, as tianwen's, its chords counting down it: Home (Ctrl+1), Chat (Ctrl+2), Sessions (Ctrl+3),
  Flights (Ctrl+4), Repos (Ctrl+5), Background tasks (Ctrl+6), Fleet (Ctrl+7) and Settings (Ctrl+8).
- **Back:** one Back and Forward for the whole window: **←** and **→** at the top of the rail (disabled when there is nothing to
  go back or forward to), Alt+Left and Alt+Right, and the mouse's back and forward buttons. Every move between places (a
  rail page, a transcript or a flight's page, a link from Home, Fleet, a card, Jobs or Repos) is recorded with the page,
  node filter and selection it left; Escape and the **← Back** pill on a transcript or a flight's page are the same Back.
- **Live:** the window follows the node's event stream, resumes with `since` after a drop, and says so in its header
  while no node answers, trying again every 2 s. The selection follows its session as the rows reorder.
- **Say:** type in the bar at the foot (Ctrl+S takes the keyboard there from anywhere) and Enter says it to the selected session (the node's `say`: an idle background
  session is resumed with it, a busy or interactive one gets it as a message). `--say` finds a session by row id,
  session id or its start, or name. With text in the bar, a **Say to all shown (N)** pill in the header says it to every shown row
  `pilot say-all` would pick with its defaults (active, no validators, no watchtower, none archived), after the archived toggle
  and the node pills; a watch-only node's rows are skipped, and the status line sums up (`said to 3, 1 refused`).
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
`pilot-node --version`, then renames each file it replaces (and each of the old release's the new one lacks) to
`<name>~old-<n>`, a name nothing has: `n` counts up past every aside that exists, and an aside is never deleted to be
reused, since a process (`pilot mcp` in every session, a hook, a worker's CLI) may still hold it. The new file is copied
in, the marker last. The swap is all or nothing: a file that cannot be replaced fails the whole install, every rename is
put back (and the folders it made removed), and the message names the process holding it where Windows' Restart Manager
can tell, else gives the OS's own. The node then starts the new `pilot-node --after <its pid>` with its own arguments
and stops; the new one waits for it and takes the socket. Flights, legs and cards are the store's and carry on, clients
resume their stream, workers and the watchtower keep running. A GUI or TUI open at the time runs on from its renamed
binary until it is reopened. At every start the node sweeps each aside it finds (`~old`, `~old<n>` of older installs,
`~old-<n>`), deleting those nothing holds any more and leaving the held ones for the start after.

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
| `create-flight repo/<path> <item> [title…]` | Codes the flight and makes its plane; `<path>` may be any checkout of the repo, a worktree included. `<item>` is a GitHub issue (`254`, `#254`) or a Shortcut story (`sc-101645` or its `app.shortcut.com/<org>/story/N` URL); see **Trackers**. Without a title it takes the item's; the result names the code it made. |
| `plan-flight flight/<code>` | A planner, `<machine>-<flight>-plan`, in the plane: it reads the issue and the code and plans the legs (`plan-leg`), writing no code, then says `planned`. |
| `planned flight/<code> [summary…]` | The planner is done: a For-you card lists the legs, with **Start** on the first. No leg may be named `plan`. |
| `overlap flight/<code> <other> <why…>` | The planner found the flight to look like part of another. A pair not asked before is recorded pending on the flight and rings one card (**Fold into <other>**, **Plan separately**); `plan-leg` and `planned` on the flight are refused while it waits, and the planner is told to end its turn (it is started again with the answer). A pair answered already returns the answer, with no card. Refused for an unknown, landed or the same flight. |
| `fold flight/<code> <other>` | The answer that the flight is part of the other: recorded, the card closed, the flight's issue added to the other (`FoldedIn`: the number and the flight it came from), the flight retired as `land` does (its planner stopped, its clean plane removed, its code and branch kept) but marked `FoldedInto` the other and with a Handled `folded` card instead of a landed one, and the other's planner started (one already planning it is stopped for it) whose brief names each folded issue and its flight and says to plan its points as legs after the ones it has. The other's PR then closes every folded issue beside its own. Refused into a landed flight, one whose PR is open (it would not close the issue) or one of another repo; a plane holding work of its own refuses and changes nothing. |
| `separate flight/<code> <other>` | The answer to plan on its own: recorded on the flight (kept over a restart), the card closed, the waiting planner stopped and a fresh one started, whose brief lists the flight's overlap answers (so it connects to the other if it still depends on it). |
| `connect flight/<code> <other>` | The flight holds until the other has landed (a cycle is refused). |
| `sync-repos node` | `git fetch -p --all` in each tracked repo, then a fast-forward-only merge of `origin/<default>` into its main checkout. Never a merge commit or a pull, and a checkout that is dirty, on another branch, diverged from origin or without an origin default is left alone and the outcome says why; a failed fetch or fast-forward is that repo's `failed`. Each git call is cut off after 2 minutes. The result's `made` is one `name<TAB>updated\|current\|skipped\|failed<TAB>detail` line per repo, and the repo status is read afresh after it. The controller's: refused from the watchtower, and from the LAN without a grant. |
| `create-issue repo/<path> <title> [body…]` | A new issue through `gh issue create`; the result's `made` is its number. The controller's, as `sync-repos` is. |
| `land flight/<code>` | Its work is in: the worktree goes when clean (never with changes not committed, nor with a commit on no remote and not in its base), the branch and code stay, and flights waiting for it are cleared. |

**Trackers (#254).** A flight's work item is a GitHub issue or a Shortcut story, and the flight records which. The code
is the repo prefix and the number (`BUL101645` for story 101645), with the same `a` to `e` suffixes. A Shortcut flight's
branch is `flight/bul101645-sc-101645` and its PR title or body carries `sc-101645`, so Shortcut's GitHub integration
links the card; the body has no `Closes #`, and landing never tries to close the card. Every brief reads a Shortcut
flight's story, not `gh issue view`: through the Shortcut MCP tool `stories-get-by-id` when the session has it, else
`curl` against the REST API with the token. A flight made before trackers existed reads as GitHub. Folding a flight into
one on another tracker is refused; overlap works across trackers.
- **The Shortcut token** reads a story's title when `create-flight` is given none. It is the `SHORTCUT_API_TOKEN`
  variable, which wins, else the node's `shortcut-token` setting. With neither, and no title given, the create is refused
  naming what is missing. Settings and the setup checklist show only whether it is present, never the token.
- **A bare number** means the repo's tracker default (`repo-trackers`, under Settings), GitHub for a repo with none.
  `#254` is always a GitHub issue and `sc-N` always a story. The new-flight wizard lists a repo's GitHub issues, so it
  always sends `#N`.
- **Other repo roots and default branches.** `repo-roots` takes several folders (`pilot settings set repo-roots
  C:\src\sharpastro C:\src\drawboard`), and the wizard and `repos` list the clones under each. A plane is cut from the
  remote's default branch as `origin/HEAD` says it, so a repo whose default is `develop` flies from `origin/develop`
  and its PR goes there; `main` is only the guess when `origin/HEAD` cannot be set.

**The planner looks first.** Before it plans a leg, a planner's brief has it list the flights not landed (`pilot status`
here, and `pilot nodes` then `pilot --node <host:port> status` for each node it sees), read their issues and legs'
goals and the files their planes touch (`git -C <plane> diff --stat <base>...`), and compare them with its own issue.
A real overlap is the same area of the issue, or legs changing the same files: one flight would redo or fight the other.
- **Overlap with a flight on this node:** the planner runs `pilot flight overlap <code> <other> <why>` and ends its turn.
  One card asks the controller to **Fold** or **Plan separately**, the answer is kept per pair (so it is never asked
  twice), and the flight's leg planning waits meanwhile.
- **Fold** puts the flight's issue into the other flight: the other's planner plans its points as more legs after its own,
  its PR closes both issues, and the asking flight is retired with nothing of its own, marked as folded into the other.
- **Plan separately** starts a fresh planner told the answer. It connects (`pilot flight connect`, which holds the
  flight until the other has landed; its legs can still be planned and `planned` said while held) only on a real
  dependency, the two still changing the same files or this one needing the other's change, and says which and why in its
  planned summary. Otherwise it connects nothing.
- **Overlap with a flight on another node** can be neither folded nor connected from here, so the planner plans as usual
  and names that flight and the overlap in its planned summary.
- **No overlap:** it plans and says `planned` as always, and no overlap card appears.

**Nothing is left behind.** A flight's work reaches main or is said, never kept where nobody looks: a leg finishes as
done only with every change committed (its worker's brief says so, and the node refuses a dirty plane); a merge is
refused while the plane holds commits past the reviewed head, so a late commit cannot miss the PR; landing by hand is
refused while the plane holds a commit on no remote and not in its base (compared by patch, so rebase-merged work
counts as in); a flight whose PR was merged on GitHub with something left out does not land but rings with it. Before
any of that, a session ending its turn in a plane with files not committed is sent back to commit them (the Stop gate,
under Hooks). Once
landed, the plane's worktree is removed and its branch kept. PRs are merged by rebase unless the node's `merge-method`
says otherwise, so every commit's subject, which a repo's docs may cite, reaches main.

**A folder Windows holds open** (a review's worktree, a landed plane, a cached clone) does not keep its review or flight
open with a card retried every sweep. It goes in the removal queue, `pending-removals.json` in the node's data folder
(path, what it belonged to, when queued, why, starts tried), and the review or landing finishes anyway with a Handled
line. The queue is tried once at the node's start, before anything touches those paths, and again when no session has
been working for an hour (at most once an hour); the PR review sweep never tries it. A folder queued for more than a day,
or that has outlasted more than 3 starts, rings once on a For-you card (kind `removal-held`, subject the node) listing
each such folder with its owner, when it was queued, its starts and the process holding it (Restart Manager over the
files under it, else `handle.exe` when on PATH, else "holder unknown"). The card is kept, not raised again, while such
folders stand, and closes when none is left. A new pin at a held queued path is made beside it.

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

**A later leg is checked GO or NO-GO before it starts.** The route and Done-whens are planned up front, so by the time
a leg's turn comes the earlier legs may have changed what its brief assumes. When a leg that has earlier legs is
started (by the node itself, or by `start-leg`), the node first starts a short validator session in the plane, on the
worker model at `low` effort, with a ceiling of a few minutes and about thirty tool calls. It reads only: it checks at
most five facts the leg rests on (files, types, verbs, tests it names) against the tree the earlier legs left, and
changes nothing. A first leg is never validated, and neither is one started with `anyway`. The validator answers with
`pilot leg validate go|no-go "<report>"` from the plane, which knows the leg by its pending validation (the CLI, and the
MCP server's `leg_validate`). **GO** starts the leg's worker as before, through the same `StartGate`, so a spent 5-hour
window holds the start and the sweep starts it once the window resets. **NO-GO** keeps the leg planned and rings with
the stale facts, each with the command that showed it, offering three choices: **Go anyway** (starts the worker without
a validation), **Remove leg**, and a **Re-plan** of the flight's legs. The sweep waits on a leg that is pending or
no-go, and planning the leg again validates it afresh. A validator that never shows up, fails, or stops without its answer
is watched as a reviewer is: it rings (`validation-stalled`) with **Go anyway** and **Remove leg**, so no leg waits on
an answer that is not coming.

| Command | What it does |
| --- | --- |
| `plan-leg flight/<code> name=… goal=… done-when=… [check=…] [ci-differs=…] [budget=…] [model=…] [effort=…] [item=…]…` | A leg at the end of the order, or a planned one replaced. A `check` running `dotnet build`/`dotnet test` without the `-c` the repo's `.github/workflows` build with (Release, in SharpAstro repos) is refused, naming the workflow, unless `ci-differs` says why it cannot match CI; the reason is kept in the Done-when. |
| `remove-leg flight/<code>/<leg> [why…]` | Takes a planned leg out of the flight; refused, with the leg's state, for one flying, done or failed. |
| `start-leg flight/<code>/<leg>` | Its worker: `claude --bg -w <plane> -n <name> --permission-mode auto` from the repo's main checkout (a plane git made is not a trusted folder of its own; `-w` enters it under the repo's trust). |
| `validate-leg flight/<code>/<leg> go\|no-go [report…]` | A validator's answer: go starts the leg's worker; no-go keeps the leg planned and rings. |
| `check flight/<code>/<leg> <n or text> [undo]` | Ticks a step. |
| `finish-leg flight/<code>/<leg> done\|failed [report…]` | Done reruns the check; a failing one leaves the leg flying with the output in its report. |
| `go-around flight/<code>/<leg> <why…>` | A fresh worker for a flying or failed leg. |
| `merge flight/<code> <head> [squash\|rebase\|merge]` | With every leg done: `gh pr merge --match-head-commit`, by the node's `merge-method` (rebase unless set otherwise) when none is named; refused when the PR moved past the head you reviewed, or the plane holds commits past it; then the flight lands. |

**A planned leg can be taken out** (`remove-leg`, `pilot leg remove <code> <leg> [why]`, **Remove** on the Flights page
for the next planned leg, which asks its why in a one-line field under its button): a leg planned by mistake, or made redundant by
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
- A passed PR that conflicts with its base and does not rebase cleanly is taken as changes too: the ring's **Go around**
  sends the last leg's worker to rebase it, keeping both sides' work.
- A flight whose legs are all done and whose PR merged on GitHub lands on the next look even with no merge card open
  (dismissed after a rebase by hand moved its head), as one merged through its card does.
- Only the reviewer gives the verdict; the controller may pass one that asked for changes ("ready anyway"); the
  watchtower may do neither. A merge needs a passed review. A reviewer that stops without its verdict, or never shows
  up, rings with **Review again** (`pilot flight review <code>`). No leg may be named `review`.

### Any PR's review

A PR pilot did not make, in a repo it may not fly, is reviewed from its link (#113):
`pilot review pr <owner>/<repo>#<n>` or `pilot review pr <its URL>` (the node's `review-pr pr/<owner>/<repo>#<n>`).

- **The clone.** The node reads the PR through gh (`gh pr view <n> -R <owner>/<repo>`: its head, base, title and author;
  one not open is refused) and uses the repo's clone under its repo roots, found by its origin, as the new-flight wizard
  lists them. With none there it clones the repo once into its clone cache, `<data>/clones/<owner>/<repo>` (`gh repo
  clone`, so a private repo clones under gh's sign-in, and later fetches there ask gh for credentials and nothing else),
  and fetches into it after.
- **The worktree.** `git fetch origin pull/<n>/head`, then a worktree of the node's own,
  `<clone>/.claude/worktrees/review-<repo>-<n>`, detached at the head gh named: pinned, so a push after it does not
  move what is reviewed. A head that did not come with the fetch (it moved meanwhile) is refused, to be asked again.
- **The reviewer.** `<machine>-review-<repo>-<n>`, a fresh background session started as a flight's reviewer is (from
  the clone, `-w` into the worktree, on the reviewer's model), a job of the node: the leg watch rings when it stalls or
  never shows up, with **Review again**, and a session idle on a job of its own is compacted (see Compaction). Its
  brief is the flight reviewer's with no Done-when: read the PR's description and discussion and the whole diff against
  `origin/<base>`, check each claim against the tree rather than the diff, never assert a defect it cannot prove (a
  doubt is a question), name each finding's file and line, change and post nothing, and keep to one session.
  Claude Code starts no background session in a folder never trusted, so the clone cache needs `claude` run in
  `<data>/clones` once (which covers every clone made there); a start refused for it says so, and leaves no worktree.
- **The verdict.** From its worktree the reviewer says `pilot review approve|changes|comment "<summary>" --finding
  "<path:line> <what is wrong>" …` (the node's `pr-verdict`), once, with a `--finding` for each finding on a line of the
  head (its path from the repo's root): only it may, and on the gh login's own PR only `comment`, as GitHub takes
  nothing else there (its brief says so too). The node diffs the worktree against `origin/<base>` (three dots, as
  GitHub does) and marks each finding whose line is in a hunk's new side inline; GitHub refuses a whole review over
  one comment outside the diff, so the others go in the review's body under *On lines outside the diff*. It writes
  the review as GitHub takes it to `<data>/reviews/<owner>/<repo>-<n>.json` (the draft: `commit_id` the pinned head,
  `event`, `body` the summary and those findings, `comments` one per inline finding on its `RIGHT` side), and rings:
  a `pr-review-ready` card, *<repo>#<n>: review ready, <verdict>*, with the author, the head it read, the event it
  posts as and under whom, how many comments go inline, the draft's path and the findings, linked to the PR, and one
  choice, **Post**. The reviewer is stopped 90 s later.
- **Post.** The card's **Post** (`post-review pr/<owner>/<repo>#<n>`) puts the review on the PR as one review, under
  gh's login: the draft is written again with the event that login takes (`APPROVE` for approve, `REQUEST_CHANGES` for
  changes, `COMMENT` for comment, and `COMMENT` whatever the verdict on the login's own PR, as GitHub refuses the others
  there), then posted in exactly one call: `gh api -X POST
  repos/<owner>/<repo>/pulls/<n>/reviews --input <the draft's full path> -q '.id, .state, .html_url'`, run by the node
  with no shell. Never twice: the post is marked on the review before the call, the review GitHub made (its id, state
  and page) is kept on it and the card's outcome says it (*Post: review <id>, <state>, under <login>*, the card then
  linking to the review), and a second post is refused, as is one after a call that went and never said back (it may
  be on the PR; look there). Only a post GitHub refused outright may go again. Posting is a write to GitHub in the
  controller's name, a gate like a merge: refused from the watchtower, and from the reviewer.
- **Queued when green.** A review whose verdict is approve or comment puts its PR in the base's merge queue by itself,
  at the reviewed head (`IGitHub.MergeAsync`, `expectedHeadOid`), once the PR is open and not a draft, its head is
  still the reviewed head and its checks pass; the sweep polls, also after the card closed, and a Handled
  `pr-review-queued` card says it (*reviewed <verdict>, checks green*). Pending checks wait; failed checks, a new
  head, a closed PR or a changes verdict queue nothing, and the review is decided once (`QueuedAt` or `QueueNote` on
  it, so a restart does not redo it). A base with no merge queue raises a For-you `pr-review-merge` card with a
  **Merge** choice (`merge pr/<owner>/<repo>#<n> <head>`); GitHub refusing to queue is a `pr-review-queue-refused`
  card with the same choice. A repo in `no-auto-merge-repos` is never queued by the node by itself: its green review
  raises the same `pr-review-merge` card instead.
- **Nothing left behind.** Once that card closes (dismissed, or answered), the reviewer is stopped if it still runs and
  the worktree is removed, whatever it holds (a build's output is nobody's work), at once and by a sweep every 30 s
  that a node restart cannot skip. One held open is said on a For-you `pr-review-left` card and tried again. A fresh
  `review pr` of the same PR ends the last review first. The clone, the cache's or the person's, stays, and so does
  the draft.

- **A fixer for a changes verdict** (#290). Once a changes verdict is posted on a PR no flight owns, in a repo the node
  tracks (a clone under the repo roots, never the clone cache): on the controller's own PR the node starts one fixer,
  `<machine>-fix-<repo>-<n>`, in a worktree `<clone>/.claude/worktrees/fix-<repo>-<n>` on the PR's own branch, told
  the report and every finding, the repo's CI build and test (`-c Release` for a SharpAstro repo), and to merge
  `origin/<base>` in rather than rebase, push without force, comment on the PR once with each finding and its fix, and
  end its turn. On anyone else's PR a For-you `pr-fix-offer` card has a **Fix it** choice (`fix-pr pr/<owner>/<repo>#<n>`)
  that starts the same fixer. A finding the reviewer starts with `design:` is a question for the author: it rings a
  `pr-review-design` card and starts no fixer. An untracked repo gets nothing. One fixer per PR, never a second, and the
  review ending takes neither the fixer nor its worktree (a fresh review of the PR carries them on).
  **Rounds.** Each sweep looks at a PR a fixer is on. A head past the reviewed one means it pushed: the fixer is stopped,
  its worktree removed (one held open is a card, tried again) and one fresh review starts at the new head, carrying
  `FixRound`. That review is queued by the same green-checks path as any approve or comment; a changes verdict starts
  the next fixer with the round plus one and the new findings, and after round 3 (the same cap as a flight's review
  go-arounds) rings a `pr-fix-capped` card with the findings and a link instead of starting a fourth. A fixer whose
  session ended with the head unmoved leaves a For-you `pr-fix-ended` card with **Fix it**; a PR merged or closed
  ends the fixer and its worktree. A review started by hand keeps the round count rather than resetting it.

`pilot status` lists the reviews not over, and under a review whose fixer is on, `fixing, <fixer>, round <n>`; such a
review stays listed after it closed.

### The `pilot: recheck` label

The node sets one `pilot:` label on a PR to say where it stands (`reviewing`, `changes`, `passed`, `conflicts`, `queued`).
`pilot: recheck` runs the other way (#286): a person puts it on any open PR of a repo the node tracks to have pilot look
at it again, whoever worked on it. Every minute the node asks each tracked GitHub repo for its open PRs carrying the label (`PrRecheck`, one light
`gh pr list --label` call per repo, no checks read) and takes a labelled one when it is its own: it holds the flight on that PR, else its own review of it, else no peer node it holds a
grant for does (a peer that does not answer holds nothing). A PR a peer holds is left to that peer, label and all.

- One recheck of a PR runs at a time, keyed by repo and number, in memory. A label put back while one runs (seen gone,
  then seen again) runs it once more after it, never beside it; one seen again after the recheck consumed it runs it again.
- A PR pilot never touched starts a review as `pilot review pr` does, and its label becomes `pilot: reviewing`. One
  merged or closed meanwhile loses the label; one already in the merge queue is left with `pilot: queued`; each leaves a
  Handled line (*recheck of pilot#253: already queued*).
- A recheck that cannot run (gh cannot read the PR or the review cannot start) keeps the label and raises a For-you
  `pr-recheck-failed` card. It is not tried again until the label has been seen gone, so a PR that cannot be rechecked is
  not tried at every look: take it off and put it back to try again.
- A recheck reads the PR afresh from GitHub, does one thing, replaces `pilot: recheck` with the one `pilot:` label for where
  the PR then stands, and leaves a Handled line saying what it did. A PR already merged or closed, or in the merge queue,
  is handled the same for all three kinds below.
- **A flight's PR** (the node's own flight on it): a conflict or a PR behind its base is rebased as the queue-conflict path
  does, and its review restarts (a rebase that conflicts goes back to the last leg as changes); red checks on a passed
  review go back as changes; a head the review never read is reviewed afresh; a passed, green, clean one goes to the merge
  queue by the usual merge rule. The explicit ask bypasses the once-a-head sets of the timed `PrChecks` look.
- **A PR pilot reviewed with `pilot review pr`** (no flight): a head other than the review's starts a fresh review
  (*head ab12cd34 was not the one reviewed (0f9e8d7c), review restarted*, label `pilot: reviewing`); a conflict or a PR
  behind its base on the controller's own branch (author is the gh login, branch in the base repo, not a fork, no fixer
  on it) is rebased onto its base in a throwaway worktree of the clone pilot holds, pushed with `--force-with-lease`, and
  reviewed afresh (*rebased onto main, review restarted*, label `pilot: reviewing`); anyone else's, or one whose rebase
  conflicts or cannot be pushed, raises a For-you `pr-recheck-conflicts` card saying why, and the label becomes
  `pilot: conflicts`; an approved or commented review at
  its head whose checks are green goes to the merge queue through the same rule as #279 (`QueueWhenGreenAsync`, which
  keeps a no-auto-merge repo and a base without a merge queue as a card for you), label `pilot: queued`; anything else
  says *nothing to do* and why (checks still running, review asks for changes, review still reading it), with the label
  for where the review stands (`passed`, `changes`, `reviewing`).
- **A PR pilot never touched** starts a review, as `pilot review pr` does (above).

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

The GUI's rail opens the **Flights** page (Ctrl+4): each flight's code, title, repo and issue, state (held: waiting
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
Escape, or **← Back**, goes back to where it was opened from. The node reads each transcript on from where its last read stopped
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

The **Sessions** and **Jobs** pages build their header and rows over one set of columns (`ListTable`, on DIR.Lib's
`TableHeader`/`TableRow`), so a drag on the divider between two titles resizes that column in the header and in every
row. The fixed columns resize; the one that takes the slack and the optional ones that drop on a narrow window (stars
capped at their width) stay as the layout gives them. The dragged widths are kept by column title, per page, in
`gui-settings.json` beside the node's data folder (`%LOCALAPPDATA%\pilot2`), written a moment after the drag settles and
when the window closes; a missing or unreadable file means the defaults. pilot-tui does not keep them.

Every page is laid out by the layout engine, never by hand: rows size to their content, a column that cannot have its
whole width collapses (`CollapseBelow`), and nothing multiplies by the DPI or measures a width to choose what to show.
`LayoutDrivenTests` reads the views' sources and fails on a computed size, a hand-applied DPI or a width breakpoint, as
tianwen's own checks do.

## Repo status

`RepoWatch` keeps a `RepoStatus` for every repo `RepoCatalog` lists (the clones under the roots), so the Repos tab and
`pilot repos` never wait on git or gh. Per repo it holds the remote's default branch (`origin/HEAD`, as last fetched; the
node fetches nothing here), the main checkout's branch, whether it is dirty (the planes under `.claude/worktrees` do not
count) and how far HEAD is ahead of and behind `origin/<default>`; its other worktrees (path, branch, the flight whose plane
it is by the plane's path, dirty, and the diff against `origin/<default>` as files, lines added and lines removed, three-dot
as a PR shows it); and its open PRs from one `gh pr list` call (number, title, head, draft or ready, checks as
`passing`/`pending`/`failing`, and the flight whose branch it is).

A repo is **tracked** when a flight has flown in it (landed ones included) or it holds a worktree or an open PR of a flight;
every other repo under the roots is kept with `Tracked: false`, so a client shows either. A git or gh call that fails
leaves its part empty and adds a note to the repo's `Errors`; the rest of the status stands.

The cache is read every three minutes once something has asked (a node nobody asks of reads no repo) and on demand with
`?refresh=true`; one refresh runs at a time, and one asked for while another runs gets that one's result. No model is used.

## The CLI

`pilot` (Pilot.Cli, native AOT) spells the node's commands for the controller, the watchtower, workers and scripts. All three binaries parse their command lines with System.CommandLine, as tianwen's CLI does, so `--help` on any command lists its arguments and options:

```bash
pilot status [--all]                           # flights in the air, their legs and artifacts, and the live sessions; --all adds landed ones
pilot status --output-format markdown          # the same as Markdown (see Output formats)
pilot sessions
pilot repos [--all] [--refresh] [--output-format markdown]   # each tracked repo: branch, worktrees (flight, diff stat) and open PRs; --all adds untracked ones
pilot repos sync                               # fetch -p --all, then fast-forward each tracked repo's default branch; prints each repo's outcome
pilot issue create <title> [--body <text>] [--repo <path>]   # a new issue through gh; prints its number
pilot flight create <issue> [title…] [--repo <path>] [--anyway]   # labels the issue (or story) `pilot node: <this node>`; refused when it carries another node's, unless --anyway (which comments why)
# The node label's life: put on the item at create, added to the flight's PR when it opens (never swapped with the `pilot:` state
# labels), taken off the item when the flight lands, lands after its merge or is folded (a failure is said on the landed card), and
# swept off any open GitHub issue no flying or held flight of this node is on at the next repo look (Shortcut stories: landing only).
pilot flight plan <code>                       # a planner plans the legs from the issue
pilot flight planned <code> [summary…]         # what the planner says when it is done
pilot flight overlap <code> <other> <why…>     # the planner: this flight looks like part of <other>; rings a Fold / Plan separately card
pilot flight separate <code> <other>           # plan it on its own: a fresh planner is started, told the answer
pilot flight connect <code> <other>
pilot flight pr <code> [title…]                # pushes the plane, opens its PR (body: the legs, Closes #<issue>)
pilot flight merge <code> <head> [--method squash|rebase|merge]   # or --squash, --rebase, --merge
pilot flight land <code>
pilot leg plan <code> <name> --goal … --done-when … [--check …] [--ci-differs …] [--budget …] [--model …] [--effort …] [--item …]…
pilot leg remove <code> <leg> [why…]           # takes a planned leg out of the flight
pilot leg start <code> <leg>
pilot leg validate go|no-go [report…]          # a leg's validator, from its plane (--leg <code>/<leg> elsewhere)
pilot leg finish done|failed [report…]         # done: the node reruns the check
pilot leg wait --for-file <path> [--until <time>] [why…]   # wait on outside work without a job (--leg <code>/<leg> elsewhere)
pilot leg go-around <why…>
pilot review pass|changes <report…>            # a flight's reviewer, from its plane (--flight <code> elsewhere)
pilot review pr <owner>/<repo>#<n>|<url>       # a reviewer on any PR, pinned at its head (see Any PR's review)
pilot review approve|changes|comment <report…> [--finding "<path:line> <body>"]…   # a PR's reviewer, from its worktree (--pr <owner>/<repo>#<n> elsewhere)
pilot check <step number or text> [--undo]
pilot artifact <path or URL> [title…]
pilot say <session> <text…>
pilot say-all <text…> [--all] [--role worker|planner|reviewer|pilot|adHoc|watchtower|validator]… [--flight <code>]… [--repo <name or checkout>]… [--dry-run]
                                               # the same say to each session the filters pick (see Say to all)
pilot compact <session> [focus…]               # an idle background session's /compact, as its next turn (see Compaction)
pilot note <text…>
pilot update [--check] [--adopt]               # the latest release, installed where the node runs from (see Install and update)
pilot archive <session> | --ended [--undo]      # put ended sessions away, out of the lists (or back)
pilot run [--timeout 90m] -- <command…>        # a long run handed to the node as a job (see Jobs)
pilot jobs                                     # the node's jobs, running first
pilot job <id>                                 # one job, and where its whole output is
pilot job cancel <id>                          # stops it, running or waiting; its owner is told
pilot mcp                                      # these commands as an MCP server over stdio (see The MCP server)
```

Inside a plane a leg command names no leg: the flight is the one whose worktree holds the folder, the leg its one
flying, and the command is sent as that leg's worker, so the black box says which worker ticked what. A worker's brief
gives it these commands by the CLI's full path (beside the node, else the source tree's build), with `--node-socket`
when its node is not on the default socket. Exit codes: 0 done, 1 refused by the node (it says why), 2 usage, 3 no node.

### Output formats

`--output-format auto|rich|markdown|plain` is taken on any command, and `status`, `cards` and `repos` read it. `plain` is the text above.
`markdown` is the Markdown source: the issue and PR as links, each flight's legs in a table, and a leg's screenshot as an
image; for `cards` a heading per card, for `repos` a heading per repo with its worktrees and open PRs as tables. `rich` is that Markdown rendered for the terminal by Console.Lib's `MarkdownOutput`, as `mdcat` renders it: colour,
clickable links, and the screenshot itself on a terminal that can show it. `auto`, the default, is rich only when
standard output is a terminal, `NO_COLOR` is unset and the terminal answers that it shows Sixel. Anything else (a pipe,
a script, an agent's shell) is plain, and the terminal is not asked. Only the CLI renders, through the `RichOutput` it
hands the command tree, so the tree takes no Console.Lib; the GUI and `pilot mcp` write plain for auto
and the source for rich.

### Say to all

`pilot say-all <text…>` says the text to every session its filters pick, one `say` each (resumed when idle, relayed when
mid-turn), and prints a line per session: `ok session/7734b775 (relayed)  <name>  <role>  <state>`, or `refused
session/<id> …` and why. It exits 1 if any say failed, else 0; no session picked says so. By default it picks the
active sessions, working or waiting; `--all` adds idle ones; ended, failed and archived sessions are never picked.
`--role` (once per role: `worker`, `planner`, `reviewer`, `pilot`, `adHoc`, `watchtower`, `validator`), `--flight` (a
code, once per flight) and `--repo` (a repo's folder name, or a checkout's path, which takes the sessions in or under it)
combine, each narrowing the others; within one, any value matches. Two kinds are kept safe: a validator (a session named
`…-validator`: it checks a commit as it stands) is picked only when `--role validator` names it, and the watchtower only
when `--role watchtower` does. The session running the command is skipped. `--dry-run` lists who would be told and sends
nothing. Only the node `--node` names is used, else this machine's. The picking is `SayAllFilter.Pick` in
`Pilot.Commands`, and `pilot mcp` offers the command as the `say-all` tool.

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

### The MCP server

`pilot mcp` serves the same commands to any MCP client (Claude Code, the desktop app) over stdio, from the same binary.
To add it to Claude Code, for every project:

```bash
claude mcp add --scope user pilot -- pilot mcp                  # pilot on PATH; else its full path
claude mcp add --scope user pilot-desk -- pilot mcp --node desk:7700   # a node on the LAN, after pilot connect
```

The **tools** are the command tree itself, so a new command is a tool with no change here: every command with an action
is one, named by its path with `_` (`status`, `flight_create`, `flight_review`, `leg_plan`, `leg_go-around`,
`job_cancel`), its arguments and options its input schema (by their long names, `done-when`; a `string[]` argument an
array of words, a flag a boolean, a choice an enum). A call is spelled back into a command line and run by the CLI's
own handler, from the folder the server was started in, so inside a plane the leg tools need no leg named. The result
is what the command printed; a refusal (with the node's reason), a usage error and no node are results with
`isError`, as exit codes 1, 2 and 3 are on the CLI. A call is from the Claude session running the server where the node
knows it (`CLAUDE_CODE_SESSION_ID`, matched against its current id and the ones before the node resumed it), as a CLI
command is, and a session the node does not know yet, run inside a flight's plane, is that plane's own session (its leg's
worker, a validator, the reviewer, else the planner `<machine>-<flight>-plan` of a flight that has no session row yet; the node also reads a
`<machine>-<flight>-plan`, `-review` or `-<leg>` name as that flight's with no row), so the watchtower's own merge or land is still refused and a flight's is never an assistant's;
else from the client by the name it gave in `initialize`; else `mcp`. Every call carries the `Agent` flag (an MCP
client is a Claude session), so it is an assistant's unless a session the node knows for a flight or the lookout says otherwise
(**Who may do what**).

The **resources** are the snapshot's pieces as JSON, each row as the node's wire writes it: `pilot://flights`,
`pilot://legs`, `pilot://cards`, `pilot://sessions`. The server is a hand-written JSON-RPC 2.0 loop (`initialize`,
`ping`, `tools/list`, `tools/call`, `resources/list`, `resources/read`) on source-generated JSON, AOT like the rest.

## The GUI

`pilot-gui` flies a flight on its own. The rail has **Sessions** (every Claude session on the machine: model, effort,
context against its window, last response, state, spend, usage in the header; a bar to say something to the selected
one) and **Flights**. On Flights the selected flight shows its legs (steps, go-arounds), its review, its PR and
artifacts (a click opens them), and what can be done with it now: **Start <leg>**, **Open PR**, **Review again** run
at once. **Go around** and **Remove** need words, so each opens a one-line field under its button (also in a flight's page and
from the row menu) asking why: Enter sends the verb (`go-around`, `remove-leg`) with that why to the flight's own node, Escape
closes the field. **Plan a leg** needs a goal and a Done-when, which the planner writes: it opens the Chat page with
`plan a leg on <code>: ` in its box for the person to finish, and the watchtower's plan card comes back to confirm; only on this window's own node's flights, since the chat's watchtower runs `pilot` against that node and another node's flight has no such leg there. There is no
command bar: the page says what the last action answered in one line under the flights, and anything else is said in the chat
(or run with the `pilot` binary and its MCP server, `pilot mcp`).

**A flight's page** opens in place of the table: Enter on the selected flight, a double click, **Open flight** in its
panel or right-click menu; Escape or **← Back** goes back to where it was opened from. The wheel scrolls it, and so do Up and Down (a
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

The **Repos** page (Ctrl+5) is every tracked repo as a row (a repo is tracked once it has flown, or holds a worktree or an
open PR of a flight; **All repos** in the header adds the rest under the node's roots). A repo row shows its main checkout
(branch, clean or dirty, ahead and behind `origin/<default>`) and its open PRs (number and title, draft or ready, where the
checks stand; a click opens the PR). Right, or a click on the triangle, lists its worktrees under it as the Sessions page
lists subagents (Left puts them away). A worktree row shows its branch, its diff against the default branch (files, `+`
added, `-` removed), its flight (a click opens it on the Flights page), its PR, and three things derived from the feed the
window already has, not from calls of their own: **the session** working in it (its folder, or any live session of its
flight): `working`, `waiting` (on you), `idle` or `none`, with what it last said (what it waits on, its API error, or when it
last responded), the session's name opening its transcript; and **the action**: `card waiting: <title>` when an open card is
about its flight or one of its legs, `leg <name> flying`, or `nothing`. The rows are the node's `RepoStatus`
(`GET /api/v1/repos/status`, cached by the node), merged across the fleet and filtered by the header's node pills like the
other pages; the window asks each node for it every 15 s only while the page is shown and for a minute after, so a closed page
costs nothing. Columns drop as the window narrows. `pilot-gui --snapshot <file> --capture out.png --page repos` renders it
(from the bundled `Fixtures/repos.sample.json`, or from the live node's status without `--snapshot`), the worktrees listed.

The page acts too. **Sync**, at the top, runs `sync-repos` on this window's node: the button reads *Syncing…* while it runs,
then each repo's outcome is listed under the header (updated, current, skipped with why, failed with why) until the next
sync or the ×, and the page's status is read again. Each repo row has **New flight**: it opens the Flights page's new-flight
wizard on that repo, at its issue step. There **New issue** (the footer; Ctrl+Enter creates) takes a title and an optional
text, makes the issue through gh (`create-issue`) and picks it, so the review and Create (which makes the flight and starts
its planner) follow with no command typed; Escape closes the form first.

The right panel is the watchtower's: its state and context, its Remote Control link, then the cards with their buttons
(Merge is a card's) and what was handled, and at its foot a **Chat with the watchtower** shortcut to the Chat page (the
panel once had a box to tell it things; that box is the chat's now).

A node the window cannot reach (its stream is down: the same `Connected` the Fleet page shows) leaves the panel with its
cards, which may be long answered and cannot be commanded now (#325). In their place, at the foot of **For you**, one dim
line per such node reads `DE1 is unreachable since 10:31 · 3 ringing, 2 for you held` (the time is today's clock, or the
day before it; `NodeFeed` stamps `DownSince` on its first failed open or drop, and clears it when the node answers). The
counts are those of its last-known open cards. The panel's own summary and Home's **Waiting** count the nodes that answer
only. A long node name is cut with an ellipsis, the whole line on hover; a click opens the Fleet page with that node's row
selected. When the node answers again its cards come back from what it reports, and the line goes.

The flights of such a node are its last-known rows, and **Home** and **Flights** say so from the same state (`FleetPages.Merge`
sets `FlightLine.Stale` from the feed's reachability, once per node per merge): the Home chip reads `ORB2 · stale (DE1
unreachable)` on a grey chip, the list row, canvas card and grid card carry `stale (DE1 unreachable)` in their state line,
and the flight's page has a wrapped line saying why its buttons are dead. A stale flight is not counted in Home's **Flying**
or **Waiting**. Its action buttons, the page's **Merge**, its transcript buttons and the menu's node commands are dead
(drawn, not live; the menu entries disabled with the reason on hover); opening the page, a PR, a folder or copying a code
stay. The marks go when the node answers again.

**The Chat page** (#207, Ctrl+2) is the conversation with the node's watchtower, in the window, where a command bar once was.
It is a page of the rail, not a dock under the right panel: the panel is 300 units wide, a conversation with tables of
output and Markdown replies wants the page's width, and a page keeps its own scroll and the keyboard the way Sessions' say
bar does. It reads the chat feed (`NodeClient.GetChatAsync`, above) while it is the page shown, pinned to its end until
scrolled up (**Jump to end** returns), reading only what came after the last entry it holds every 2 s (a message just sent
asks for a read at once); leaving the page stops the reads and coming back resumes after what it kept. What it shows:

- the person's messages (`you`) and the watchtower's replies in full Markdown, never folded at 40 lines as the Sessions
  page's are; the node's own notes dim;
- each tool call as one folded line (check, tool, what it does, when), a click unfolding the command and its output: the
  Sessions page's own rows, through one `TranscriptEntryView` that both pages build their rows from;
- **● working…** while the watchtower is on a turn, **● sending…** while a message is on its way;
- a rule where a fresh watchtower began (`A fresh watchtower started: its brief changed`);
- what was said and not yet taken, dim under the messages (`queued · not read yet`);
- a question it asked, with its choices as buttons: a click says the choice to the watchtower, as typing it would.

The box at the foot sends through the node's `tell` (`ChatCommands.TellAsync`, as the panel's box did): **Enter** sends,
**Shift+Enter** (Alt+Enter, for a terminal that cannot tell Shift) starts a new line. DIR.Lib's text field is a single
line, so `ChatComposer` keeps the lines before the one being typed and shows them above the field, read-only: Backspace
on an empty line takes the line before back into the field, and a paste of several lines lands as several. Moving the
caret up into an earlier line waits for a multi-line field in DIR.Lib. A refusal (`refused: …`, the message clipped)
is said in the chat itself, in red, until the next send goes through. A snapshot or capture has no node to tell, so the
page shows no box and no buttons there.

**The Autopilot switch** (#208) stands at the top right of every page's header, beside the **?** (on Home, on the title
row): a button that says *Autopilot*, plain while the node in view has its autopilot off and lit (the accent blue) while it
is on (`AutopilotSwitch`, shared by every page through `PilotChrome.Autopilot`). It follows the node's own answer
(`NodeInfo.Autopilot`, read with the other node facts every few seconds), never the click. A click sends `set node autopilot
on|off` to this window's node as `gui`, which is the person (the node takes the setting from the person only), and the status
line says what became of it, a refusal included. Over a remote node (`--node`) it shows that node's state and does not click: the node keeps the setting for its own machine, as the Settings page is gated. Its tooltip says what the state means. The **Fleet** page says it per node:
`autopilot on` or `autopilot off` on each row that answers (nothing for a node not reached, whose last word would read as
now), beside the maintenance mark, and the row's dump line ends in it.

The **Background tasks** page (Ctrl+6) is the node's jobs: running first, then those waiting in the order they will
start, then the ended, last ended first. Each row has the job's command, whom it wakes (the flight and leg, a session,
or a terminal), its state with what it is doing, waits on or came to, when it started and how long it ran; a job that
ended without its owner being told says so. The selected job shows its folder, log and timeout beside the end of its
output (its last 200 lines, read from the node every 2 s while selected), with **Cancel** while it runs or waits,
**Its session** (the transcript of the session it wakes, from when it was handed over) and **Open log**; its
right-click menu adds its folder and Copy entries.

**One window for the fleet.** Sessions, Flights, Background tasks and the cards panel are every node's at once, not
only this window's: the feeds the Fleet page already keeps (this window's node, and each node heard on the LAN or
granted to you, one stream each) are merged into one list per page (`FleetWatch.Pages`), and a node heard at two
addresses, or this window's own heard again on the LAN, is counted once by its stream. With more than one node every
row says whose it is: a **Node** column (`SE1`, the machine prefix, else the machine's name), and a card's `RING` or
`for you` line ends in its node. Each header then carries the node selection as pills: **All nodes** (the default), or
one node, or several (a click adds or takes one away). It filters every page and the cards panel alike, the Fleet page
too, and is the window's own, kept in memory for as long as it is open, so switching pages keeps it. The selection on
Sessions follows the session as rows of several nodes interleave (two nodes' rows may share an id, so another node's
row is keyed by its node as well). A row's buttons, say bar, archive, card answers and Cancel go to the node the row came
from, with the grant `pilot connect` saved for it (`FleetClients`: this window's own socket client for its node's rows, one
`NodeClient.Remote` per other endpoint, cached). Whether a node can be steered is this window's node, or a grant held
and not since refused: the fleet tick (every 5 s) re-reads the grants, and a node that answers a command with 401 or 403
(a revoked token) reads as watch-only until a different token is saved for it. A **watch-only** row (a node with no
usable grant) keeps its transcript and log readable, but its say bar, Stop, Archive, Cancel and answers are disabled: the
say bar and the row menu say *Connect to LE1 to steer*, and a **Connect** pill stands there (and in place of Cancel on
Background tasks), running the same ask as the Fleet page's. There is no second window per node. The new-flight wizard stays this window's
node's. pilot-tui shows the same merged pages.

## The TUI

`pilot-tui` is the same dashboard in a terminal, over SSH as well as locally: the GUI's own pages and panels
(`Pilot.Ui`), their DIR.Lib trees arranged in terminal cells by Console.Lib (`CellMeasureContext.PixelAuthored`)
instead of painted on pixels, so a row, a button or a why field is the same node on both. **1** Home, **2** Sessions,
**3** Flights, **4** Background tasks, **5** Fleet, **6** Settings, **7** Repos, **8** Chat, **Ctrl+S** the say bar, **Ctrl+T** the chat with the watchtower (the box focused), Up/Down to select, a
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

### The chat (#207)

The conversation with the watchtower is one chat, and the node serves it as one feed (`GET /api/v1/chat?after=<n>`, a
`ChatPage`; `NodeClient.GetChatAsync(after)`; a grant's on the LAN, as a transcript is). A fresh watchtower is a new session
with no history of its own, so the feed is the transcript of every watchtower there has been, oldest first, as one run:

- **Entries** are the transcript page's `TranscriptEntry` (a prompt, the assistant's words, a tool call and its result, an
  error), their `Index` counting across all the watchtowers, so a client asks for what came after the last index it has
  whichever watchtower said it. Without `after` it gives the last 300; later reads give at most 500. Where a fresh watchtower
  began there is a `divider` entry (its text says why: `A fresh watchtower started: its brief changed`, its time the start's).
  A reply's `<ask>` block (below) shows as its question, without its choices.
- **`Working`** is whether the watchtower is on a turn now (the chat's "typing").
- **`PendingQuestion`** and **`Choices`** are the question the watchtower's last reply asked in an `<ask>` block that nothing
  answered, and its choice labels in the order it gave them. The block is the question, then one `- choice` line each, then
  `</ask>`. The watchtower is a background session, whose `AskUserQuestion` blocks until someone answers it in `claude agents`
  (a say is read only between tool calls, so never answers it), so the node starts it with that tool denied and its brief has it
  end its turn on the block instead. The question is answered by the person saying anything but the node's own `[node]`
  messages, or by a later `<ask>` replacing it, or, when no `[node]` message has come since the question, by the session's
  next reply in the same turn. A card the node says resumes the watchtower and its reply to the card does not drop the
  question: the buttons stay until the person says something. The person's answer is a say to the watchtower (`tell`), a
  choice's label or their own words, and it resumes the idle session like any say to an idle background session.
- **`Queued`** is what was said to it that it has not taken yet.
- **Notes.** What the node says to the watchtower in its own name (a card it says, a nudge, a wake-up: a `say` whose `Who` is
  `node`) begins `[node] ` (`NodeVoice.Prefix`), and a compaction is its `/compact`; both come back as entries with `Note` set
  (the prefix cut off) so a client shows them dim, apart from the person's words. The closing line a relayed message carries
  is cut off every prompt.

Which watchtowers there have been is kept in `watchtower-sessions` in the node's data folder: a line each (the row it ran as,
its latest Claude session id, when it began, why), written when the node starts one and kept current as the row's session id
moves (a say resumes a session under a new id, whose transcript holds the earlier history). Rows of the watchtower's name the
node has seen are folded in, so one an older node started is in the chat too. After a restart, or once the rows are archived,
the earlier conversations stay readable. A watchtower that never said anything has no transcript and no divider.

The brief makes the watchtower the person's assistant in the window as well as the fleet's lookout: it answers in plain
words, asks with an `<ask>` block when a choice is the person's, tells the node's `[node]` messages from theirs, and
carries out "let's launch a flight for X" as `pilot issue create`, then `pilot flight create`, then `pilot flight plan` (the
planner's own overlap check covers "already flying"), saying back what it made with links. "Plan a leg on <code>: …" is
`pilot leg plan` then `pilot flight planned`, so the plan card comes back. What is the person's alone (merge, land, the
settings, grants, updates) it asks for as its command, which the node turns into the Allow card (see **Who may do what**),
and never does silently.

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
pilot-gui --node 192.168.1.20:47731            # the whole window, its buttons and chat, on that node
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
3. **What is left**, which the GUI's **Settings** page (Ctrl+8) also lists with a button for each the node can fix:
   the watchtower's folder trusted once (Claude Code's own prompt, which the page opens in a titled window; a folder
   above it that is trusted already covers it), gh signed in, and usage figures (a status line that writes
   `rate-limits.json`; without it the node still says whose account it is, with no figures).
4. **From the other machine:** its Fleet page hears the new node by itself; **Connect** on its row (or
   `pilot connect <host>:47731`) rings an Allow/Deny card on it, to be allowed there; its rows are then steered from the
   merged lists.

**The Fleet page** (Ctrl+7 in the GUI, 5 in the TUI) is every node at once: this window's first, then every node heard
on the LAN or granted to this user, each on a feed of its own (its event stream, watched without a grant as anyone on
the LAN may). A row says the node's machine and where it is reached, whose account it runs on and its usage, its live
sessions and how many wait on you, its flights and jobs, and its rings; a node not reached says what its connection is
doing instead. **Connect** asks a node for a grant, as `pilot connect` does, when this user has none, and the node
watched only says so. The page is the per-node summary (account, usage, connection state, Connect); it has no Open
button, since the merged lists steer every node from this window (`FleetView.OnOpen` is there for a host that wants
one, and neither the GUI nor the TUI sets it). A node heard twice (on its socket and on the LAN) is one row, by its
stream. A row's **N waiting on you** is a button (`fleet:<where>:waiting`, `PilotChrome.LinkFleet`, wired in both hosts):
it opens the Sessions page filtered to that node alone (the window's node selection), the first session waiting on you
selected and the list scrolled to its head, where the waiting rows sort, so they are on screen. A node held in
`client-grants.json` but never reached (no hello ever: not connected and no node info received) offers **Forget**
(`fleet:<where>:forget`, `FleetView.OnForget`, wired in both hosts), the Settings page's held-grant Forget: the token is
dropped (`NodeTargets.Forget`) and `FleetWatch.Forget` removes and disposes the endpoint's feed, so the row is gone at
once and the next 5 s look does not add it back from the grants. A node still heard on the LAN is known again by the
next look, as a node without a grant; a reached node, or this window's, offers no Forget.

### Settings

The node's settings live in `settings.json` in its data folder, read at every start: on the LAN, its machine prefix,
repo roots, update channel and policy, jobs at once, job memory, the LAN port, and a model and an effort per kind of
session it starts (#114). The watchtower and workers start on Sonnet and planners and reviewers on Opus unless set
otherwise; a leg's own `--model` or `--effort` still wins for its worker; `none` passes no flag, leaving Claude Code's
own default. The watchtower's brief says its model, so a lighter one rings for a judgement rather than makes it. The command line wins over the
file (an autostart entry made before the file existed still pins what it names), and a key the file leaves out takes
the default; a value it cannot read is passed over, never fatal. `pilot-node setup` writes it, and so does the
**Settings** page (Ctrl+8 in the GUI, 6 in the TUI), or `pilot settings set <key> [value…]`.

The node answers the settings and the setup checks apart (#331). `GET /api/v1/settings` reads `settings.json` and the
node's own state alone, so it answers at once: the settings rows, `Restart` and `File`, and no process runs. The setup
checks run programs (`netsh`, `gh`), so they have their own route, `GET /api/v1/settings/checks`: `SetupChecks`, the
checks as the node last measured them with `CheckedAt`, the time they were measured, and per check `Runs`, what reading it
runs (`runs netsh`, `runs gh, which asks GitHub`) for a page's "checking…" line. They are cached for 10 seconds;
`?refresh=true` runs them again, and `setup node <check>` refreshes them after its fix. Both routes are this machine's
alone. `set node <key> …` answers with the changed setting's new row in `CommandResult.Setting` (a `SettingRow`, as the page
would show it after the change, on every path that saved it, the command-line-wins one included), so a page shows the new
value from the answer and need not read the page again. `pilot settings` reads both routes and prints the checklist and
the settings as before.

A role's model and effort, and the job limits, are in force at once (the next session of that kind starts with
them; a waiting job starts as soon as the new limit allows). So are `maintenance` and `accept-fleet-flights` (#177),
two switches, `on` or `off`. While `maintenance` is on the node takes no new flight: a `create-flight` is refused with
`<machine> is in maintenance: it takes no new flights`, and its flights are moved out (see **Moving a node's flights out**
below) rather than left to carry on: a flight's leg flying finishes, reviews and landings of what is here carry on, jobs
carry on, but no new leg starts here. `accept-fleet-flights` (off unless set) says the node is willing to
take a flight another node of the fleet routes to it. Both are in `GET /api/v1/node` (`Maintenance`,
`AcceptsFleetFlights`), and `pilot status` prints a `maintenance` line while it is on. `set node maintenance on|off` is
the one setting a LAN grant may change (the Fleet row menu's way); every other `set` and `setup` stays this machine's.

`autopilot` (#208, `on` or `off`, off unless saved on) is in force at once too, in `GET /api/v1/node` as `Autopilot`, and
`pilot status` prints an `autopilot` line, on or off. Only the person changes it (see **Who may do what**): a `set` of it
from an assistant, the lookout, a flight's session or a grant is refused, taking it out (`set autopilot` with no value)
included.

Two per-repo settings (#254) are lists, keyed by the repo's folder name and in force at once, like `priority-repos`:
- `repo-trackers` takes `repo=tracker` entries (`pilot settings set repo-trackers bullclip-backend=shortcut`), `tracker`
  being `github` or `shortcut`. A bare number given to `create-flight` for that repo is a story of that tracker; a repo
  left out means GitHub. A later entry for a repo replaces the earlier one.
- `no-auto-merge-repos` names repos whose PRs the node never merges by itself, autopilot on or not (`pilot settings set
  no-auto-merge-repos bullclip-backend`). An assistant's `merge` of such a flight is not run on autopilot: it raises the
  For-you confirm card as it does with autopilot off, and the asking session is told the answer. A passed flight's
  PR-ready card there rings the controller as for any repo. The person's own merge (the card's **Allow**, the **Merge**
  button, `pilot flight merge` by hand) is not held.

`shortcut-token` is in force at once, and the setup checklist has an optional **Shortcut token** line saying whether one
is present (the variable or the setting).

A `create-flight` that reaches a node in maintenance goes to a willing node instead (#177). The candidates are the
nodes this node holds a grant for (`client-grants.json` beside its data, the tokens `pilot connect` saved; the
`PILOT_CLIENT_GRANTS` file in a test). A node qualifies when it is reachable, accepts the grant, is not in maintenance,
has `accept-fleet-flights` on, and has a checkout of the same GitHub remote under its repo roots (its `repos` route;
paths are each machine's own, so the repo is found by its `owner/name`). Among those the one with more 5-hour
headroom wins (a window that has reset counts as all of it, a node with no figures comes last), then the one with
fewer flights in the air. The node sends `create-flight repo/<the peer's checkout> <issue> [title]` to it, returns the
code the peer made, raises a Handled card, and says where it went in the result's way (`PIL180 is flying on DE1: SE1 is in
maintenance`), which `pilot flight create`, the MCP tool and the new-flight wizard show; the flight lives on the peer
(its code is not in this node's flights). A peer that refuses the flight leaves it to the next. With none willing the
command is refused, naming each node and why it did not qualify (`in maintenance`, `not willing`, `unreachable`,
`no grant`, `no checkout of <owner/name>`); with no other node known, it is refused with the maintenance reason alone.

**Handing a flight to another node (#376).** Routing moves a flight that does not exist yet; `receive-flight flight/<code>
<record>` moves one that does, so a node can take over a flight another node hands it. The record (`FlightMove`, JSON, the one
argument) is the flight's whole row (its PR, its review), every leg with its report, checklist, artifacts and go-arounds, the
cards about the flight still open, the sending node's prefix, the last leg done there, and the repo's GitHub `owner/name`.
The plane is not in it: the sender pushes the flight's branch first (`FlightMoves.Gather` builds the record,
`IFleetPeer.SendAsync(move, who)` hands it over, under the grant it holds for the peer). The receiver:
- refuses unless the command comes under a LAN grant, it is not in maintenance (`<machine> is in maintenance: it takes no new
  flights`) and has `accept-fleet-flights` on, it does not have the code already, whatever its state (`TIA900 is already here
  (landed on DE1)`: a code is one flight on a node for good), no leg of the flight is flying (it is handed over once the leg
  has finished), the flight is flying, and it has a checkout of the repo's GitHub remote under its repo roots (`<machine> has no
  checkout of <owner/name>`); a branch the sender did not push is refused too (`<branch> is not on origin of <checkout>`). A
  refusal changes nothing: no flight, leg, card or plane is left;
- makes the plane in its own checkout from the pushed branch (`git fetch` of the branch, then `git worktree add` of
  `<checkout>/.claude/worktrees/<code>` on `origin/<branch>`), so the worktree stands at the pushed head, and the flight's
  `RepoPath`, `Worktree` and `Branch` are this machine's own;
- puts the flight, its legs as they were, and the open cards about the flight (raised again here, numbered by this node), and
  records where it came from in `FlightRow.MovedFrom` (`From`, `AfterLeg`, `At`, journaled with the row; `FlightMoved.Text` says it
  for every client: `moved from SE1 after leg parser`), plus a Handled `received` card saying the same;
- then takes the flight's next step by the node's ordinary advance (`LegCommander.AdvanceFlightAsync`, the sweep's own step for
  one flight): the next planned leg starts (a leg after a done one with its validator first, as anywhere), or the PR and its
  review once every leg is done. The result's `Made` is the code and its `Way` the same words as the card.

An artifact a leg recorded keeps the sender's path or URL: the images it names are not copied to the receiver.

**Moving a node's flights out (#376).** A node in maintenance hands its flights to the fleet at a leg boundary, by the node's own
sweep (`LegCommander.AdvanceAsync`, the minute's `AdvanceAllAsync`, and the moment a leg's `finish` says done), and by hand with
`move-flight flight/<code>` (`pilot flight move <code>`, the person's alone; it works out of maintenance too).
- **When.** The leg flying finishes first (a hand move of a flight with a leg flying is refused: `TIA900/parser is flying: a flight
  moves once its leg has finished`); in maintenance the sweep starts no new leg (nor the validator that reads one). A flight is due once
  nothing of it is flying or being started: its legs done or planned (the next one is the receiver's to start), no reviewer
  reading, no session of it working, no validator reading, no package it holds for, no overlap's answer pending, and no failed leg.
  A flight due is moved instead of taking its next step (its next leg, its PR, its review), so a flight whose PR is open and
  reviewed, waiting for its merge, moves too, with its PR, review and merge-when-green as they are.
- **Make-safe** (`LegCommander.MakeSafeAsync`), under the flight's `Calling` guard with the lock let go: the plane must be clean
  (`git status --porcelain`); origin's head of the branch is read (`git ls-remote`); a remote head the plane does not hold is a
  refusal (`the push was refused, the remote holds commits the plane lacks`), never overwritten or forced; otherwise the branch
  is pushed `git push -u origin <branch>`, with no `--force` and no lease, after GitHub's auto-merge on its PR is checked off
  (nothing is pushed when origin has the plane's head already); then origin's head is read back and must equal the plane's head.
- **Choice and send.** The target is chosen as `create-flight` routing chooses (`FleetChoice`: not in maintenance, willing, a
  checkout of the repo's GitHub remote, then by headroom and flights in the air; no label preference yet, as #255's labels are not
  here), and the flight is sent with `receive-flight` to each willing node in turn, its record as `FlightMoves.Gather` makes it, without
  the sender's `MoveFailure` or its `move-failed` card.
- **One node only.** From the check that nothing of it is flying until the move has ended, no session of the flight starts here
  (`start-leg`, a go-around, a planner or a reviewer are refused `the flight is being moved to another node`), and its PR verbs are
  refused as for any call out. Once the receiver has answered that it took the flight, the sender records at once (under its lock,
  before anything else) `FlightRow.MovedTo` and puts it `Landed`, as a folded flight is: its plane is removed (its branch stays),
  its sessions stopped, its cards closed with a Handled `moved-away` line naming the node (`TIA900 moved to DE1 after leg parser`),
  and its `pilot node:` label taken off the item (the receiver adds its own). `pilot status` and the Flights page say `moved to DE1`
  in place of `landed`, and a flight that waits for it keeps waiting. A code is one flight on a node for good, so a flight that
  moved away is not taken back by the same node. The one window that remains is the receiver's answer being lost on the wire: the
  receiver then flies a flight the sender still holds, and the sender's retry is refused by the receiver (`TIA900 is already here`).
- **Failure.** Any failure (dirty plane, a push refused or behind the remote, origin's head not the plane's after the push, no node
  willing, none known, a receiver that refuses or cannot be reached) keeps the flight here and no new leg starts while the node is in
  maintenance. `FlightRow.MoveFailure` says what happened and what to do (`PIL294 could not move to DE1: the push was refused, the
  remote holds commits the plane lacks. Merge them in, or move it again.`), and a Ring `move-failed` card carries the same with a
  *Retry move* choice that runs `move-flight` again (the card stays open while it runs, and a second failure rewrites its text). The
  sweep does not retry a failed move by itself; the flight's PR and review go on here as they do anywhere. Once the flight goes on
  here again (the node is out of maintenance and its next step ran) the failure and its card are dropped.

Any other setting's change restarts the node to run with it, once no
job runs or waits (a job is its child): the node starts its
successor with its own arguments and `--after <its pid>`, and stops, as a self-update does, so flights, sessions and
the watchtower carry on and clients resume their stream. A setting the command line gives is saved but says it does
not count until the command line stops giving it.

Above the settings, the page has the setup checklist: started at logon, Claude Code's hooks for this binary, on the
LAN, the firewall rule for this binary and for `pilot`, `pilot-gui` and `pilot-tui` beside it, which listen for other
nodes' beacons and are let in on the discovery port alone (Windows), the watchtower's folder trusted (read from Claude Code's own record,
a folder above it counting), gh signed in, and usage figures. Each says how it stands; those the node can fix have a
button (`setup node <check>`): the logon entry, the hooks, the LAN, the firewall rule (one elevation prompt, which
replaces an earlier install's rule), the Windows Defender exclusions (below), the trust prompt (opened in a window titled for what it is). `pilot settings`
prints both. The route and both verbs are this machine's only: a grant from the LAN never reaches them, and nor does
the watchtower.

The **Windows Defender exclusions** check (`setup node defender`, optional, Windows only) reads `Get-MpPreference` and lists
the folders a build machine wants left unscanned that are not excluded yet: each repo root, the NuGet package folder
(`NUGET_PACKAGES`, else `~/.nuget/packages`), the node's data folder, `~/.claude` and the dotnet root. A folder on a Dev
Drive is skipped (Defender's performance mode covers it), and it is fine when Defender is not the antivirus in force. Its
button runs one elevated script of `Add-MpPreference -ExclusionPath` lines, so one prompt; `pilot settings setup defender
--processes` adds `-ExclusionProcess` for `pilot-node.exe`, `dotnet.exe`, `MSBuild.exe`, `VBCSCompiler.exe` and
`testhost.exe` too. Defender shows its exclusions to an elevated process only, so from the node the check may not be able
to tell and says so, offering the (idempotent) fix.

The **Merge guard** check (`setup node merge-guard`, not optional) says whether `~/.claude/settings.json` has the merge
guard for this binary: a user-wide `PreToolUse` hook on `Bash` and `PowerShell` (matcher `Bash|PowerShell`) running
`pilot-node merge-guard` (see Hooks). It is TODO until its button (or `pilot-node setup`, or `pilot settings setup
merge-guard`) installs it; a guard for another path of the binary, or an older one on `Bash` alone, fails it until the
fix replaces it, and a second install changes nothing.

One more optional check is **Guard hooks** (`setup node guard-hooks`): it says whether three generic guard hooks
are in `~/.claude/settings.json`: `big_read_guard.sh` (refuses a whole-file `Read` and a bare `cat`),
`fat_result_notice.sh` (says a tool result was unusually large) and `pgrep_self_match_guard.sh` (refuses an inline
`pgrep -f`/`pkill -f`). It counts a guard wired by hand in any spelling (`bash path`, quoted, `$HOME/...`). Its button
takes the scripts from a `runaway-agent` clone (the guards' source), the first `source/repos/*/runaway-agent` under the
user's profile that has all three, copies them into `hooks/` beside the settings file, and adds a
handler each under the event and matcher of its `settings-fragment.json` (PreToolUse for the first, PreToolUse on
`Bash` for the third, PostToolUse for the second), leaving the rest of the file alone. A guard already wired is not
added again, a script already there with the same content is not copied again, and a second run changes nothing. The
copies would otherwise go stale, so while a clone is found, a script in `hooks/` that differs from the clone's fails the
check too, its line naming the scripts older than the clone, and the button copies them afresh. With no clone the line
says to clone `github.com/DrawboardLtd/runaway-agent` beside the others, and there is no button. Only these three
guards are offered; pilot budgets legs at the session level.

A third optional check, **MSBuild node reuse off** (`setup node build-rsp`), says whether the clone's
`machine/Directory.Build.rsp` is in the user's home folder. MSBuild reads one found by
walking up from the project, so every build under home, a flight's included, runs with `-nodeReuse:false` and its worker
nodes exit with the build; idle nodes had held worktree folders (a removal refused on 15 Oct) and about 40 GB. The
checklist shows it installed, missing, or differing from the clone's copy. Its button copies the file from the same
clone the guard hooks use; a file that differs is first copied to `Directory.Build.rsp.bak-<UTC timestamp>` beside it,
one that is the same is left alone, and so a second run changes nothing and makes no backup. With no clone the line
says to clone it, with no button (a file that is there then passes, as there is nothing to compare it with); a clone too old to have the file says
so, also with no button.

Below the settings, a **Grants** section (#118) lists, from the same rows and pills, the grants this node has given
(label, since when, from the node's grants route) each with **Revoke**, which sends `revoke-grant` for that id and cuts
that machine back to watch-only at once (the list reads again after it, and on opening the page); and the nodes this
user holds a token for (the token file `pilot connect` writes) each with **Forget**, which drops the token: that node's
rows in the merged lists turn watch-only with Connect offered, from the next fleet tick (Forget asks for it at once).
`pilot grants` and `pilot revoke` do as before.

## Who may do what

The node cannot tell its person from any Claude session on the machine by the socket alone, so a command says who sends it
(#208): `Who` (`gui`, `cli`, a session's name, an MCP client's name), and `Agent`, set by the CLI when `CLAUDECODE` is in
its environment (Claude Code puts it in everything its sessions run) and always by `pilot mcp` (an MCP client is a Claude
session); the GUI and the TUI never set it. One place, `Standing.Of` on the node, sorts the sender, first match wins:

| Standing | Known by | May do |
|---|---|---|
| **Grant** | the LAN token's grant (never read from the wire) | as before: never the settings, setup or grants of this machine (but `maintenance`) |
| **Lookout** | `Who` is the watchtower's session name, whatever the flag, unless it is acting on the person's message (an assistant, below) | watch, ring, nudge, answer (a ring card too, with a button whose command it may send itself: a stall's Resume, Nudge or Go around); nothing your-only (so no dismissing a ring card), and not a review's verdict or posting, `sync-repos`, `create-issue`, `fold` or `separate` |
| **Flight's session** | `Who` is a leg's worker or validator, a flight's reviewer or a PR review's reviewer, or a planner, worker or reviewer session row that names its flight | its own flight's legs, checks, artifacts and reviews, and a PR review its own PR; a `flight/` or `pr/` target that is another's is refused, as is `stop` or `compact` of a session and `cancel-job` of a job that is not its flight's (or its own); nothing your-only, nor `post-review`, `fold`, `separate`, `create-issue`, `create-flight` or `sync-repos` |
| **Assistant** | the `Agent` flag, from no session above; and the watchtower while it acts on a message the person sent it (the node's `tell`, the GUI or TUI chat, a human's `pilot say`) | everything asked in words; its your-only commands follow the autopilot rule, below |
| **Person** | the rest: a GUI click, a human-run CLI (no `CLAUDECODE`), the node's own steps (`Who` is `node`) | everything |

The **your-only** commands are `merge`, `land`, `set`, `setup`, `answer-grant`, `revoke-grant`, `update`,
`restart-watchtower`, and dismissing or answering an open card in the ring bin (the answer to any other card is its chosen button's
command, so it is your-only exactly when that is). `update --check` only says whether a release is out, so it is nobody's alone. Every route runs them through `NodeCommands.RunAsync`, so a card's button is fenced like the
command by hand, and an answer keeps the answerer's standing. A refused command says why and leaves its card open.
`connect flight/<code> <other>` is a flight's dependency, not a grant's, and is not your-only.

The watchtower is told apart by what began its turn: the node notes who sent the latest message to its session (the person's
chat, a `tell`, or the node's own card and nudge, which are not theirs), and while that was the person's and the session has
not gone idle since, `Standing.Of` takes the watchtower for an assistant, so "merge TIA1" said in the chat gets the Allow card
(autopilot off) or runs at once with a Handled card (on). Any other turn it is the lookout.

### An assistant's your-only request: the autopilot rule

What an assistant (a Claude session the person talks to) sends of the your-only list is neither refused nor run blindly, but
sorted by the `autopilot` setting (`Autopilot` on the node):

- **Off (the default).** The command is not run. The node raises a For-you card (kind `assistant-ask`, about the flight or
  target), `<session> asks to merge #63 (TIA1)`, with the message the assistant acted on as its detail and two choices:
  **Allow**, which is the original command, run as whoever presses it (the person), and **Deny**, which closes the ask. The
  command's own result is a refusal saying it waits on that card, so the assistant knows to wait; the same command asked
  again is the same card (a second session asking it is added to the card's `AlsoAskedBy`), and another your-only command about the same flight replaces it. When the card is answered,
  the node says the outcome to each asking session through `say` (found by `Who` in the session rows: an MCP client with no
  row of its name cannot be told, and the card says so) and closes the card with who answered. `dismiss` on the card is a
  Deny. An Allow the command refuses (a merge past the head, say) leaves the ask open and tells no one.
- **On.** The command runs at once. The command's result and its black-box line carry `Autopilot` and the message (`Why`)
  beside `Who`, which is the session; and a Handled card (`autopilot-ran`) says `<session> merged #63 (TIA1) on autopilot`
  with that message, or `No message given`. A command the flight then refuses leaves no Handled card, only the black box.
- **Either way**, answering or dismissing an `assistant-ask` card is itself your-only, and refused to an assistant or the
  lookout whatever the autopilot says; a flight's session is refused every your-only command, autopilot on or off; and the
  person, a human-run CLI and a grant are not in the rule at all.

The message is the optional `Why` of a command (`CommandRequest.Why`), filled by `--why "<message>"` on `pilot flight merge`,
`pilot flight land`, `pilot settings set`, `pilot settings setup` and `pilot update` (and the matching MCP tool inputs); it is
empty when none is given.

## Cards

An event that needs the controller is one card, with its choices on it: **Rings** (a gate
before an irreversible step, a stop: waits until someone acts), **For you** (a card, no push) and **Handled** (what the
node or the watchtower did itself: one line, born closed). A session going quiet is not a card by itself; it changes
how its row looks. A flying leg's worker stalling is: its flight is derailing (see Liveness). A card leaves when its
thing is done: answered, dismissed, or closed by the node when its flight lands or its leg finishes. Raised so far: a
PR opened with every leg done rings with **Merge** on that head; a failed leg rings with **Go around**; a landed flight
is a For-you card; a leg started with a sibling clone behind its origin raises a For-you `sibling-behind` card; the leg
watch's cards (`worker-stopped`, `-idle`, `-waiting`, `-failed`, `-missing`, and the Handled `worker-resumed` and
`worker-nudged`); a session that stopped twice in a turn with files not committed in its plane rings
(`stopped-uncommitted`, see Hooks); and a PR's review rings with its verdict (`pr-review-ready`, about
`pr/<owner>/<repo>#<n>`) and **Post**, which posts it to GitHub once, its worktree going when the card closes (see Any
PR's review).

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

**The merge guard: merges lead back to the node.** `pilot-node merge-guard` is a second hook, installed user-wide by
`pilot-node setup` (the **Merge guard** check; `pilot-node hooks remove` takes it out with the others) as a `PreToolUse`
handler on `Bash` and `PowerShell` (the sessions' main shell on Windows), in step with the session since a deny is never
read from an async hook. It reads the hook's JSON and
refuses, naming `pilot flight merge <code> <head>` (or the Merge button on the flight's card), a command with a part
that is `gh pr merge`, `gh api graphql` with `enqueuePullRequest` or `mergePullRequest`, `gh api -X PUT
repos/.../pulls/N/merge`, or a `git push` whose destination is `main`, `master` or the repo's default branch
(`origin/HEAD`): a refspec's right side (`HEAD:master`, `+main`, `:main` too), `HEAD` read as the checkout's branch,
`--all` and `--mirror`, and a bare `git push` (or one naming only a remote) from a checkout on the default branch. A line is
split on `&&`, `||`, `;`, `|`, `&`, newlines, backticks and parentheses outside quotes and each part is matched, after
leading `VAR=x`, shell keywords (`if`, `then`, `do`, `!`, `{`…) and wrappers (`sudo`/`env`/`command`/`nice`, `timeout <duration>`,
`xargs`), and with `gh`'s `-R`/`--repo` value skipped before `pr` or `api`; a bash backslash-newline joins the line, a
heredoc's body (`<<EOF` … `EOF`) and a PowerShell here-string (`@'`…`'@`) are text, not commands; a `$(…)` or backticks inside double quotes are commands of their own
(so `git commit -m "$(cat <<'EOF' … EOF)"` is a commit whatever quotes its body holds, and a merge inside one is still refused); a redirection (`2>&1`, `> /dev/null`) is not a refspec, and
`git push --tags` alone moves no branch and passes (`--follow-tags` pushes the branch too); `cd` and `git -C` move where the checkout is read, with `~`, `$HOME` and Git Bash's `/c/…` mapped to
real paths (a `cd`, `pushd` or `Set-Location` inside `( … )`, `$( … )` or backticks is gone when they end, and `popd` / `Pop-Location` undoes a
`pushd` / `Push-Location`, so the push after `(cd ../main && git fetch)` is read where the session is), and `bash -c '…'` (also in combined flags, `-lc`, `-ec`) and `eval` are read inside.
A `#` that starts a word outside quotes is a comment to the end of the line (in PowerShell `<# … #>` too), so a merge or an apostrophe in
one is no code. A `PowerShell` call is read with PowerShell's own quoting (the backtick is the escape, a
backslash is a path's, `''` and `""` are literal quotes), split on `{` and `}` too, and `Set-Location`, `iex` /
`Invoke-Expression` and `pwsh -Command '…'` are followed as `cd`, `eval` and `bash -c` are, and a leading `$name =` assignment is skipped
(`$out = gh pr merge 1` is a merge). It also refuses a background session started without the machine's prefix: a `claude`
call with `--bg` and a name (`-n NAME`, `--name NAME`, `--name=NAME`), through wrappers and `pwsh -Command` too, whose name does not start
with `<machine>-` (case-insensitive) is denied, the reason giving the prefixed name to use (`de1-tia1241-nudge`). The machine is
resolved as the node does, without asking it: the `machine` it saved in `settings.json` of the default data folder, else
`PILOT_MACHINE`, else the machine name's; a `claude` call without `--bg` or a name passes. Every brief (worker, validator, planner,
reviewer, PR reviewer, watchtower) tells the session the same rule, with the node's machine code written out. Everything else, a push of a
flight branch included, passes untouched. Like `pilot-node hook` it
is fail-open (input it cannot read passes, exit 0 always) and never calls the node; the node's own merge
(`pilot flight merge` runs `gh pr merge` itself) is not a session's tool call, so the hook never sees it.

A session the API stopped (its transcript's `apiError`, `usage_limit_reached` with when the limit resets) shows as
**stopped** with why and when it can go on, hooks or not.

## The wire (`NodeWire.Version` 2)

| Route | What it does |
| --- | --- |
| `GET /api/v1/node` | Which node this is: wire version, build version, machine, pid, started at, data folder, socket, stream. |
| `GET /api/v1/events` | The event stream, a WebSocket: a snapshot carrying the current number, then deltas each numbered one past the last. `?since=<n>&stream=<id>` resumes with the deltas after `n`, or a snapshot when the node no longer holds them or has another stream. |
| `POST /api/v1/commands` | Every command, `{verb, target, args, who}`. The result is the response and a `commandResult` event on the stream. Verbs: `note node <text>`, `say session/<id> <text>`, `compact session/<id> [focus]`, and the flight and review verbs above. |
| (cards) | `answer card/<id> <choice>`, `dismiss card/<id> [why]`: verbs on `POST /api/v1/commands`. |
| `GET /api/v1/sessions/<id>/transcript?after=<n>` | A session's transcript as entries after the n-th (its last 300 without one); a grant's on the LAN. |
| `GET /api/v1/chat?after=<n>` | The watchtower's conversation as one feed across every watchtower there has been: a `ChatPage` of entries after the n-th (the last 300 without one) with a `divider` entry where a fresh watchtower began, whether it is working, its pending question and choices, and what is queued for it (see The chat); a grant's on the LAN. |
| `GET /api/v1/jobs/<id>/log` | The end of a job's output: its last 200 lines from its last 64 KiB, the log's length, and whether it was clipped; a grant's on the LAN. |
| `GET /api/v1/repos` | The repos a flight can be made in, with their GitHub names; a grant's on the LAN. |
| `GET /api/v1/repos/status` | Each repo's state, a `RepoStatus[]` from the node's cache (see Repo status); `?refresh=true` waits for a fresh read. A grant's on the LAN. |
| `GET /api/v1/repos/issues?path=` | A repo's open issues, through gh; a grant's on the LAN. |
| `POST /api/v1/hooks` | One Claude Code hook's stdin, as `pilot-node hook` forwards it; always 204. |

HTTP responses come in a `ResponseEnvelope` (`response`, `error`, `statusCode`, `success`); stream events are sent
bare, each with its `kind` and `seq`. A client that falls behind is closed and resumes with `since`.
