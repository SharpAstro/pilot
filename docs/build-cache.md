# Build cache trial (PIL860, leg cache-trial)

Microsoft.MSBuildCache.Local (0.1.340-preview, github.com/microsoft/MSBuildCache) tried on pilot's own build, 2026-11-10, on DE1
(Windows 11 x64, 16 cores, Visual Studio 2026 with MSBuild 18.10.1, .NET SDK 10.0.403). It is an **opt-in that CI never sees**:
nothing below applies unless a build is given `-p:MSBuildCacheEnabled=true`.

## Verdict

- **It works, with Visual Studio's x64 MSBuild and the flags below.** The cache keys each project's outputs on a hash of everything the
  project's build read, not on timestamps, so a fresh checkout gets another checkout's unchanged projects from the cache.
- **Cold 1:15 to 2:26, warm 0:03 to 0:17** for a fresh checkout cut from this branch (nine projects, `Pilot.slnx`, Release). A warm build
  is under 3 minutes in every case measured with the final flags, including the worst one (a change to `Pilot.Contracts`, which every
  project references, rebuilds all nine: 1:15).
- **Nothing changed was served from the cache.** A one-string change in `Pilot.Contracts` rebuilt all nine projects and the new string is
  in all ten copies of `Pilot.Contracts.dll` in the tree (and the old one in none). A change in `Pilot.Tui` (which nothing references)
  rebuilt only `Pilot.Tui`.
- **It does not run under `dotnet build`.** It needs `-reportfileaccesses`, which exists only in Visual Studio's x64 `MSBuild.exe`.
  That makes it a Windows-only opt-in, and the node would have to start that MSBuild itself (a later leg).
- **The cache is 64 MB** after one cold build of pilot.

## The command line that gets a correct cache hit

Restore first, as its own step, with the same global properties as the build (the package is in the project's assets only if the
restore saw `MSBuildCacheEnabled=true`; a restore without it and a build with it was not tried):

```
dotnet restore <checkout>\src\Pilot.slnx -p:Configuration=Release -p:MSBuildCacheEnabled=true -p:UseLocalSiblings=false
```

Then Visual Studio's **x64** MSBuild, found through vswhere (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`):

```
vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\amd64\MSBuild.exe"

<that MSBuild.exe> <checkout>\src\Pilot.slnx -graph -reportfileaccesses -m -nologo -v:m -clp:Summary
    -p:Configuration=Release -p:MSBuildCacheEnabled=true -p:UseLocalSiblings=false
    -p:MSBuildCacheAsyncCacheMaterialization=false
    "-p:MSBuildCacheIdenticalDuplicateOutputPatterns=**\*"
    -p:MSBuildCacheLocalCacheRootPath=<cache root>
```

Run through `pilot run`, the MSBuild path has spaces, so the trial gave its 8.3 short form
(`C:\PROGRA~1\MICROS~2\18\PROFES~1\MSBuild\Current\Bin\amd64\MSBuild.exe`); a node that starts the process itself needs no such thing.

What each part is for, and what was seen without it:

| Part | Why | Seen without it |
|---|---|---|
| Visual Studio's MSBuild, not `dotnet build` | `-reportfileaccesses` is MSBuild's file-access reporting; the plugin builds each project's fingerprint from it | `dotnet build ... -reportfileaccesses`: `MSB1001: Unknown switch`. `dotnet build -graph` without the switch loads the plugin and ends `Build FAILED` with 0 errors, 0 warnings, in 0.4 s, nothing built and no cache log written (j4494, j4495) |
| `...\Bin\amd64\MSBuild.exe`, not `...\Bin\MSBuild.exe` | file-access reporting needs the 64-bit MSBuild | the 32-bit one throws `Reporting file accesses is only currently supported using the x64 flavor of MSBuild` |
| `-graph` | the plugin works on the static project graph | not tried without it (the README requires it) |
| restore as its own step | the package must be in each project's assets before the build; the README's own examples restore separately or with `/restore` | |
| `-p:MSBuildCacheAsyncCacheMaterialization=false` | on a hit the plugin writes a project's outputs to disk in the background by default, and a dependant's lookup can run before they are there | first warm build: **3 hits, 6 misses, 0:51** (every miss depends on `Pilot.Commands`, itself a hit 60 ms earlier; the cache log says `Skipping selector ... Probes/enumerations no longer match current filesystem state`; the rebuilt outputs had the same hashes as the cold ones). With it off: 9 of 9 hits |
| `-p:MSBuildCacheIdenticalDuplicateOutputPatterns=**\*` | see below | a rebuild after a source change failed |
| `-p:MSBuildCacheLocalCacheRootPath=<dir>` | where the cache lives; the default is `\MSBuildCache` at the root of the repo's drive, sized 100 GB (`MSBuildCacheLocalCacheSizeInMegabytes`) | |
| `-p:UseLocalSiblings=false` | the same switch the node's checks use: a build on the published packages | |

**The hard-link trap.** By default the plugin puts a project's output files into the cache, and places them on a hit, as **hard links to
the cache's own files, which carry an `Everyone: Deny CreateFiles` ACL**. Verified on the failing file: `fsutil hardlink list` showed
the same file under the old checkout, the cache blob and the new checkout. So after a hit (or after the seeding build itself) any
task that rewrites such a file in place fails: with the warm tree and the one-string change, build j4479 died at
`MSB4018 ... GenerateMSBuildEditorConfig ... UnauthorizedAccessException: Access to the path
'...\obj\Release\net10.0\Pilot.Contracts.GeneratedMSBuildEditorConfig.editorconfig' is denied`. That is a build error, not a stale
output, but it is the common case for a plane (build, edit, build). The plugin takes a real, writable copy instead for the paths
matching `MSBuildCacheIdenticalDuplicateOutputPatterns` (its source: `GetFileRealizationMode`, `CopyNoVerify` with write access, on
both the seeding put and the hit placement), and the README warns that this setting costs performance. With `**\*` every output is a
copy: the outputs of both the cold and the warm tree were plain files (normal ACL, no link to the cache), the rebuild after a
change succeeded, and a warm all-hit build still took 4.5 s. The setting is on the plugin's list of properties ignored by the
fingerprint, so it does not split the cache.

## Numbers

Every run is a fresh `git clone` of this branch at `c2c2347f` into a scratch folder outside the plane, a fresh clone for each tree,
Release, `-p:UseLocalSiblings=false`, the cache root outside the plane and outside `%LOCALAPPDATA%\pilot2`. The machine is shared with
other flights and a tianwen training run, so the load differs from run to run; `machine` is what `pilot status` said just before the job
(`cpu`, and how much of it is outside pilot). The tree's `Pilot.slnx` has nine projects and 20 graph edges.

| Run | Flags | Time Elapsed | Cache hits / misses | machine just before |
|---|---|---|---|---|
| cold 1 (empty cache) | plugin defaults (`-graph -reportfileaccesses`, cache root) | 1:32.06 | 0 / 9 | cpu 98%, 39% of it outside pilot (tianwen r3-train-synth21, svchost) |
| warm 1 (fresh clone, cache of cold 1) | same | 0:51.06 | 3 / 6 | cpu 53%, 52% outside pilot (training, svchost) |
| cold 2 (empty cache) | `AsyncCacheMaterialization=false` added | 1:14.81 | 0 / 9 | not sampled (53% a few minutes before) |
| warm 2 (fresh clone) | same | **0:02.73** | **9 / 0** | cpu 49%, no process outside pilot named |
| warm 2 after a one-string change | same | 0:09 (build failed, the hard-link trap above) | 0 / 9 | not sampled |
| cold 3 (empty cache) | **final flags** (both added) | **2:26.05** | 0 / 9 | cpu 89%, 61% outside pilot (svchost, training, pilot-node) |
| warm 3 (fresh clone) | final | **0:04.53** | **9 / 0** | cpu 92%, 63% outside pilot (training, a `dotnet` in Pilot.Commands of another flight, svchost) |
| warm 3 + `Pilot.Contracts` changed | final | 1:14.73 | 0 / 9 | not sampled |
| warm 3 + `Pilot.Contracts` reverted, `Pilot.Tui` changed | final | 0:16.62 | 8 / 1 | not sampled |

- `dotnet restore Pilot.slnx` of a fresh clone took 2.4 to 19.4 s (the NuGet global cache was warm) and is not in Time Elapsed.
- A single project (`Pilot.Contracts`) tried first: cold 31 s, fresh clone at another path 5 s (a hit, "saved 21.6 project-seconds"): the entries
  are keyed on repo-relative paths, so clones at different paths share them (the cold and warm trees above sit in different folders).
- The cold times move with the machine, from 1:15 to 2:26, and no single reading explains them (cold 1 at cpu 98% took 1:32, cold 3 at 89%
  took 2:26). The warm builds that hit everything were 3 to 5 s at cpu 49% and at 92% alike, which is the number to take from here.
- Outputs of a warm tree are identical to the cold tree's: `Pilot.Contracts.dll` and `Pilot.Node.Tests.dll` hashed the same.

### Is a warm build under 3 minutes?

Yes, in every warm case measured with the final flags: 4.5 s when nothing changed, 17 s when a leaf project changed, 1:15 when the project
everything references changed (all nine rebuilt, so as slow as a cold build). Cold 3 took 2:26 under a heavy load, so that last case
could come close to the 3-minute line on a busy machine; it was not measured there. The first warm build (0:51) is not an
exception: it ran without the materialization flag and is the failure that flag fixes.

## Staleness probe

Done in a warm tree (fresh clone, cache seeded by another clone):

1. `src/Pilot.Contracts/CardContext.cs`: `private const string Open = "[pilot card ";` became `"[pilot-card ";`. A test reads it:
   `CardChatTests` asserts `[pilot card {card.Id}]` (line 57). The change was **uncommitted** in the clone. It is a private constant, so
   the project's reference assembly is unchanged and only the implementation `Pilot.Contracts.dll` differs: the case where a cache is
   most likely to hand a dependant a stale copy.
2. Build: **0 hits, 9 misses**, `Build succeeded`. Every project lists `Pilot.Contracts` as a dependency, so every one rebuilt.
3. The output carries the change: a search for the UTF-16 string in the ten `Pilot.Contracts.dll` files of the tree (the project's `bin`
   and `obj`, and the copies in the `bin` of Cli, Commands, Gui, Gui.Tests, Node, Node.Tests, Tui and Ui) found `[pilot-card ` in all ten and
   `[pilot card ` in none. The cold tree's copy has the old string only.
4. Reverted `CardContext.cs`, changed one string in `src/Pilot.Tui/Program.cs` (nothing references Tui): **8 hits, 1 miss**; the miss is
   `Pilot.Tui`, its `pilot-tui.dll` holds the new string, and the reverted `Pilot.Contracts.dll` holds the old one again (the revert
   found its old entry by content).

Not exercised here, so not claimed: a new source file that only a glob picks up (the plugin records directory enumerations and
probes in each project's path set, so it is meant to be caught), an edit to a file outside the projects (`Directory.Build.props`),
a different SDK patch version (the plugin keys on the exact SDK version unless `MSBuildCacheIgnoreDotNetSdkPatchVersion`), and untracked files.

What the plugin hashes, from its README and from the logs of these runs: the global properties and requested targets of the project, the
project's own inputs (source files by the content git reports, NuGet package files by directory, SDK files), and everything the build
reads or probes while it runs, as reported by `-reportfileaccesses`; a hit must match all of it. Properties that only configure the plugin are
excluded, so the cache flags above do not split the cache.

## Size and where it lives

- After one cold build: **64.3 MB in 437 files** (`Shared` 64.2 MB, the small `RocksDbMemoizationStore` the rest). The clone's `bin` and
  `obj` were 173 MB. After two more states (the `Pilot.Contracts` change, then the `Pilot.Tui` change): 86.3 MB in 518 files.
  Content that is the same across projects (a dependency copied into each `bin`) is stored once. A cap applies
  (`MSBuildCacheLocalCacheSizeInMegabytes`, default 100 GB); a node setting should pick a smaller one.
- The plugin also writes **logs into the checkout**, `MSBuildCacheLogs\` at the repo root (untracked, 9.1 MB after a cold build,
  3.6 MB after a warm one). In a plane that is an untracked folder the node refuses a finished leg for: the node must pass
  `-p:MSBuildCacheLogDirectory=<outside the plane>` (documented in the README, **not exercised in the trial**, where the clones were not planes).
- Defender: the trial did not change Defender, so all numbers have it scanning the scratch trees and the cache. `pilot status` on this machine lists
  MsMpEng among the top processes outside pilot at times (cpu 70%, 74% of it outside pilot: MsMpEng, svchost, pilot-node, earlier today), which is
  why the issue asks for the exclusion. Excluding the **cache root** (written by every build, a few hundred files of tens of MB) and the
  folder holding the planes is an admin step (`Add-MpPreference -ExclusionPath`), not one a plane can take; its effect on these times is **not measured**.

## The issue's bar for tianwen and DIR.Lib

The issue says to roll out to tianwen and DIR.Lib only if a warm plane build is under 3 minutes. On pilot it is: 4.5 s to 17 s for a warm
tree with nothing, or a leaf project, changed, and 1:15 when the most shared project changed. That clears the bar **on pilot**. It does
not measure tianwen (the issue puts its cold build at 15 to 45.6 minutes) or DIR.Lib: how much of a tianwen plane's build a leg changes,
and whether its native, Vulkan and AOT projects behave under file-access reporting, are unmeasured. The suggested next step is the same
trial on one of them, with the same command line and the same staleness probe, before a rollout.

## What the next legs need to know

- The node would have to find and start Visual Studio's x64 MSBuild for the cached check, and restore separately first. There is no
  `dotnet build` form. On a machine without Visual Studio (and on Linux) the cache stays off and a plain build is as before.
- A plane's cached build needs its log directory outside the plane (above) and the flags of the command line above; a seeding build is a
  cold build of main.
- CI is unchanged. `src/Directory.Packages.props` and `src/Directory.Build.props` hold the `Microsoft.MSBuildCache.Local` and
  `Microsoft.MSBuildCache.SharedCompilation` pins and references only under `MSBuildCacheEnabled == 'true'`, and `BuildCacheOptInTests`
  (Category=Invariant) fails if either loses that condition. The SharedCompilation package is the README's advice for C# (the
  compiler server is a detached process whose file reads MSBuild cannot see); the trial did not run without it.

## Seeding from main

With `job-build-cache` on, the node seeds the cache from main (`BuildSeed`, called by `RepoSync` after each clone's sync). When a sync
fast-forwards a tracked repo's main checkout (outcome `updated`, so its HEAD moved), the node queues one build job in that clone: the
build a plane's check runs, `dotnet build <solution> -c Release`, which the cached form rewrites to the same restore and MSBuild command with the
same global properties a plane's gets, `UseLocalSiblings=false` among them (a test holds the two commands equal), so the cache keys match,
in the folder holding the first `.slnx` (else `.sln`) at the repo root or under `src`. A repo with neither gets no seed. The last seeded
HEAD is remembered per clone, so a resync of the same HEAD queues nothing, and nothing is queued with the setting off or when HEAD did not
move. The job is an ordinary node job: below normal priority, through the queue, owned by no session. `JobRunner.QueueSeed` marks it, so it
gets the cache variables and the cached restore plus MSBuild form (`BuildCache.Variables` / `With` with `seed: true`) although a main
clone is no plane. What was seeded, and which jobs are seeds, is kept in the node's process: after a restart the next moved HEAD seeds again.

Covered by `BuildCacheTests` and `RepoSyncTests` against fakes (setting off, HEAD unmoved, moved once, same HEAD twice, no solution). Turning the
setting on and measuring a warm plane build on the live node (under 3 minutes or not) is the controller's, not done from a plane. For that run,
three things are open and unmeasured:

- Two cached builds sharing one cache root at once (a seed and a plane build, or two planes) are not covered here.
- The cache has no size cap: `MSBuildCacheLocalCacheSizeInMegabytes` is not passed, so the plugin's default applies.
- With `job-plane-output` on, plane jobs also get `UseArtifactsOutput` and `ArtifactsPath`, and a seed does not. That setting is off by default
  and was not measured together with the cache.
