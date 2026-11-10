using System.Security.Cryptography;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a leg's worker is told: everything it needs from the flight and the leg, and nothing of an earlier worker's
/// conversation. A go-around's worker is told why the last one stopped and what the plane holds now (#35: a long
/// go-around in one session drags its whole conversation along; a fresh one briefed from the work does not).
/// </summary>
public static partial class Briefs
{
    /// <summary>How a worker runs the CLI: its path, quoted when it holds a space, and the node's socket when it is not the default.</summary>
    public static string Run(string? cli, string? socket)
    {
        var pilot = cli ?? "pilot";
        var run = pilot.Contains(' ', StringComparison.Ordinal) ? $"\"{pilot}\"" : pilot;
        if (socket is not null)
        {
            run += $" --node-socket \"{socket}\"";
        }
        return run;
    }

    /// <summary>What the plane holds so far, for a go-around's brief: its commits past the base and the files they touch.</summary>
    public sealed record PlaneState(string Commits, string DiffStat);

    /// <summary>A re-review's brief: the last round, and the commits and stat of its head to the plane's HEAD.</summary>
    public sealed record ReReview(ReviewRound Last, PlaneState Range);

    /// <summary>
    /// The <c>pilot</c> CLI a worker runs, by its full path: beside the node (a publish puts them together), else the source
    /// tree's build of the same configuration, else <c>pilot</c> on <c>PATH</c>.
    /// </summary>
    public static string PilotCli(string? nodeDirectory = null)
    {
        var name = OperatingSystem.IsWindows() ? "pilot.exe" : "pilot";
        var directory = nodeDirectory ?? AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(directory, name)))
        {
            return Path.Combine(directory, name);
        }
        var tfm = new DirectoryInfo(directory);
        if (tfm.Parent is { } configuration && configuration.Parent is { Name: "bin" } bin && bin.Parent?.Parent is { } src
            && Path.Combine(src.FullName, "Pilot.Cli", "bin", configuration.Name, tfm.Name, name) is var built && File.Exists(built))
        {
            return built;
        }
        return "pilot";
    }

    /// <summary>
    /// The owner's hard rule, in every brief that writes or reviews code: nothing busy-spins. A leaked test fixture spun a core on SE1
    /// for four days (busy.ps1, 2026-11-04 to 11-08, about 52 h of CPU).
    /// </summary>
    internal const string NoSpinRule =
        "- NO BUSY SPINNING, anywhere in the code base, tests and scripts included: no loop on the CPU waiting for a condition, and no\n" +
        "  SpinWait, SpinUntil or SpinOnce. Wait on an event, a task, a process exit or a timer (await Task.Delay, Start-Sleep, sleep), and give\n" +
        "  every waiting loop a deadline so it ends with its owner. A unit test runs in milliseconds, in memory, against fakes: no real\n" +
        "  process, no network, no wall-clock wait. A review finds any spin a blocking finding.\n";

    /// <summary>
    /// How a session runs a command so Claude Code's allow rules match it (#896): a rule matches the command's text as written, so a
    /// chained, quoted or variable call of pilot misses <c>Bash(pilot answer:*)</c> and goes to the auto-mode classifier, which denied
    /// the watchtower's card answers as acting on the person's behalf (SE1, 2026-11-08).
    /// </summary>
    internal const string CommandShapeRule =
        "- Run each pilot command alone, exactly as this brief writes it: no `cd … &&` before it, no `;` or `&&` chain, no quotes added\n" +
        "  around its path and no variable for it. Run git in another folder as `git -C <folder> …`, never by cd. Pilot's MCP tools\n" +
        "  (answer, dismiss, cards, status and the rest) need no shell at all. Keep a note through the node (`pilot note`), never as a\n" +
        "  file under ~/.claude: a session writing its own settings or memory is refused.\n";

    /// <summary>
    /// How a branch takes the base, for the worker and both reviewers: merging it in is the way, and merge commits on a
    /// working branch are never a finding, because the merge queue lands each PR linear (squashed, or rebased where the repo's queue rebases; the node reshapes a branch only
    /// for a rebasing queue). A review that called
    /// them a defect sent TIA1874's worker to rewrite its pushed branch, which then needed a force push only the owner could
    /// allow (DE1, 2026-10-30); TIA1910 and TIA1915 stalled the same way.
    /// </summary>
    internal const string BranchHistoryRule =
        "- A branch takes its base by a merge (`git fetch origin` then `git merge origin/<base>`). Merge commits on a working branch are\n" +
        "  fine and never a finding: the merge queue squashes or rebases each PR, so main stays linear whatever the branch holds. Never rebase a\n" +
        "  pushed branch, never push with --force or --force-with-lease, and never ask for either.\n";

    /// <summary>
    /// What the worker and the planner (whose legs' Done-when may wait on outside work) are told about waiting: a leg waits on a
    /// file or a time with <c>leg wait</c>, never with a job that sleeps, which holds a slot of the queue while it does nothing.
    /// </summary>
    internal static string WaitRule(string run) =>
        $"- Outside work to wait for (a file another tool writes, a time of day): `{run} leg wait --for-file <path> [--until <time>] [why...]`, then end\n" +
        "  your turn; the node wakes you once when the file exists or the time comes. Never wait with a `run` job that sleeps or polls:\n" +
        "  it holds a slot of the queue and blocks every job behind it.";

    /// <summary>What every pilot-started session is told about saying it needs the person: it reports blocked, so every client shows it so.</summary>
    internal static string ReportRule(string run) =>
        $"- When you need the person to decide something and cannot go on, run `{run} session report blocked \"<what you need>\"` and end your turn.\n" +
        "  When you take up the work again the node sees it from your next hook, so no report is needed.";

    /// <summary>
    /// For a repo with LFS (its .gitattributes holds <c>filter=lfs</c>): the node filled the plane's LFS files from the shared
    /// store (<see cref="LfsFill"/>), so the brief says no pull is needed, or names the paths it could not fill (capped, with a
    /// count of the rest) and one <c>git lfs pull --include</c> for them, only if the step needs them. Nothing for a repo without LFS.
    /// </summary>
    /// <param name="folder">The plane or worktree the brief sends its agent to.</param>
    /// <param name="noun">What the brief calls that folder: "plane", or "worktree" for a PR's.</param>
    internal static string LfsRule(string? folder, string noun = "plane")
    {
        try
        {
            var attributes = folder is null ? null : Path.Combine(folder, ".gitattributes");
            if (attributes is null || !File.Exists(attributes) || !File.ReadAllText(attributes).Contains("filter=lfs", StringComparison.Ordinal))
            {
                return "";
            }
        }
        catch (IOException)
        {
            return "";
        }
        var missing = LfsFill.MissingPaths(folder!);
        if (missing.Count == 0)
        {
            return $"\nThis repo uses git LFS: the node already filled the {noun}'s LFS files from the shared store, so no pull is needed.\n";
        }
        var shown = missing.Take(MaxLfsPathsShown).ToList();
        var rest = missing.Count - shown.Count;
        var text = $"\nThis repo uses git LFS: the node already filled the {noun}'s LFS files from the shared store, except {missing.Count} the store lacked: " +
            string.Join(", ", shown) + (rest > 0 ? $" and {rest} more" : "") + ".\n";
        return text + $"Only if the step needs them, run `git lfs pull --include={string.Join(',', shown)}` once in the {noun}" +
            (rest > 0 ? " (for the paths listed; the rest wait until a step needs them)" : "") + ", never per file or per build.\n";
    }

    private const int MaxLfsPathsShown = 20;

    /// <summary>The repo's PILOT.md quoted whole under one heading; nothing when it has none.</summary>
    internal static string PilotMdSection(string? pilotMd) =>
        string.IsNullOrWhiteSpace(pilotMd) ? "" : $"\n## This repo's rules for pilot (PILOT.md)\n{pilotMd}\n\n";

    /// <summary>
    /// The line every brief carries: a background session a session starts itself is named with the node's machine code, as the
    /// node's own are, so the fleet's sessions are told apart (the merge guard refuses a <c>claude --bg -n</c> without it).
    /// Nothing when the node's machine is not known.
    /// </summary>
    internal static string NamingRule(string? machine, string scope)
    {
        if (machine is not { Length: > 0 })
        {
            return "";
        }
        var code = machine.ToLowerInvariant();
        return $"A background session you start yourself (`claude --bg`) is named `{code}-<flight or card>-<what>`, with the machine's code `{code}` written out,\n" +
            $"e.g. `{code}-{scope}-nudge`: never start one without that prefix.\n";
    }

    /// <summary>
    /// The line both the planner and the worker are told: build as CI does. A published package is built Release, so a Debug
    /// build on a worktree meets <c>#if DEBUG</c> types it never carries (SDL144) and fails for no reason of the change's.
    /// </summary>
    internal const string CiRule =
        "Checks, and your own builds and test runs, build as the repo's CI does: for a SharpAstro dotnet repo that is `-c Release`, not the Debug a bare\n" +
        "`dotnet build` gives. A check that cannot match CI says why in the Done-when, before it fails.";

    /// <summary>
    /// The line the planner and the worker are told, in autopilot as much as out of it: the machine is the person's, so nothing
    /// loads it on purpose without their okay on a card. TIA1910 ran a 24-process CPU burner to imitate a busy CI runner while
    /// it measured a flaky test (DE1 2026-10-29), and the person found it in Task Manager.
    /// </summary>
    internal static string MachineLoadRule(string run) =>
        "Never load the machine on purpose without the person's explicit okay, in autopilot too: no CPU or GPU burner, no stress or\n" +
        "soak loop, no pool of processes spinning to imitate a busy machine, nothing that holds many cores or the GPU for the load's\n" +
        $"own sake. Ask first with `{run} ring flight/<code>[/<leg>] \"<what load, for how long, and why>\"`, and go on with the rest; only the\n" +
        "person's answer on that card is the okay, never a guess that they would agree. The answer does not reach you by itself: if none\n" +
        "has reached your session when the rest of the leg is done, finish without the load and say in your report that it was not run\n" +
        "and why. A build or a test run that is merely heavy is not load in this sense.";

    /// <summary>
    /// The line every role that hands over a job or writes a check is told, in one line (pilot #697): which shell runs it. A bare
    /// <c>bash</c> on Windows is WSL's launcher, which ran a Linux dotnet (NETSDK1147), so the node runs it in Git Bash instead, and says
    /// so on the job's row and the first line of its log.
    /// </summary>
    internal const string JobShellRule =
        "A job or a check runs in pwsh on Windows; a check is given without a wrapper, and a job line headed `bash` or `sh` runs Git Bash (never WSL); calling dotnet directly is best.";

    /// <summary>The line a worker and a fixer are told: a repo-wide guard test carries the Invariant category the node widens checks with.</summary>
    internal const string InvariantRule =
        "A new test that guards the whole repo (a catalog, a size limit, a fixture, a rule over every source file) carries `[Trait(\"Category\", \"Invariant\")]` (`InvariantChecks.Category` or the test project's constant), so checks run it.";

    /// <summary>
    /// PIL795 (the TIA2000 post-mortem): a repeat workflow with a pull_request trigger ran 30 passes on every push to its own PR,
    /// 37 runner-hours for one PR. A proof that repeats is run once, on demand, at the end.
    /// </summary>
    internal const string OnDemandProofRule =
        "A repeat, soak or N-pass proof (a workflow that runs the same tests many times to show they are stable) is a `workflow_dispatch` workflow, run once at the end, on demand.\n" +
        "It is never a `pull_request` or `push` trigger, which would re-run it on every push to the PR, and never a done-when that every push must pass.";

    /// <summary>PIL795: a failure the flight did not cause gets its own issue; it does not become a leg of this flight (TIA2000 absorbed five unrelated races).</summary>
    internal static string UnrelatedFailureRule(string run) =>
        "When your check or the PR's CI fails on a test your flight does not touch (a test class and files your diff leaves alone, and the test fails on main too), it is not this flight's to fix:\n" +
        "file it as a new issue with `gh issue create`, with the failing log and the test's name, and link it from your report. Do not fix it here, and do not add a leg to the flight for it.\n" +
        $"The issue flies on its own, or the correlated-failure path (PIL716) takes it. Say in your report that the failure is not this flight's, with the issue's number; if it blocks your leg's check, `{run} leg finish failed` with that said, not a new leg.";

    /// <summary>PIL795: the planner's rule on growth. A flight's legs grow by one past its plan; the second extra leg rings the owner (<c>LegCommander.PlannedLegCap</c>).</summary>
    internal const string LegGrowthRule =
        "The legs you lay are the flight's plan: the node keeps their count when you say planned. A later `leg plan` may add one leg past it without a word; a second extra leg raises a card to the owner, so plan every leg the issue needs now.";

    /// <summary>PIL795: a reviewer treats a CI trigger that re-runs on its own PR pushes, or a job over 30 minutes on every PR, as blocking.</summary>
    internal const string SelfTriggeringCiRule =
        "A PR that adds or changes a CI workflow so that it re-runs on the PR's own pushes (a `pull_request` or `push` trigger on a repeat, soak or N-pass proof, or a path filter that includes the workflow itself), or so that a job over 30 minutes runs on every PR, is a blocking finding:\n" +
        "such a proof is `workflow_dispatch` only, run once at the end (TIA2000's 30-pass repeat on every push cost 37 runner-hours).";
    /// <summary>The line the planner is told: a leg's check fits its change, and the whole suite is CI's on the PR (pilot #752).</summary>
    internal const string CheckScopeRule =
        "A leg's check filters `dotnet test` over the test classes of the files the flight touches plus the classes the leg names (`--filter 'FullyQualifiedName~FooTests|FullyQualifiedName~BarTests'`). " +
        "The whole suite is CI's, on the PR: `leg plan` refuses a `dotnet test` with no --filter, suggesting one, unless it is given `--whole-suite \"<why>\"`.";

    /// <summary>The line the worker is told: run the check and the classes touched, never the whole suite, which CI runs on the PR.</summary>
    internal const string WorkerScopeRule =
        "Run the check and the test classes you touch, never the whole suite: CI runs the whole suite on the PR, and an unrelated failure in it is not this leg's. " +
        "The node refuses a `pilot run` of a `dotnet test` with no --filter (it names the filter it suggests); only `pilot run --whole-suite \"<why>\" -- <command>` runs one, and the why is kept on the job. " +
        "A check that fails only in tests your change does not touch (their test file, the class of a source you changed, or a name in the check's filter) is passed by the node, which matches each to an open issue or files one and reports it as not this flight's: do not chase it.";

    /// <summary>The line the planner is told: a check runs in the plane's root, so write it as CI runs it, working-directory included.</summary>
    internal const string CheckWhereRule =
        "A check runs in the plane's root: write it as CI runs it, including its working-directory (`cd <dir> && dotnet build <solution> -c Release`).\n" +
        "`leg plan` refuses a check naming a project, solution or folder that is not where it runs, and says where the file is.";

    /// <summary>
    /// The org's UI checklist beside the flight's clone (<c>.github/docs/ui-changes.md</c> in the folder holding its repo,
    /// as SharpAstro keeps it), or null where there is none.
    /// </summary>
    internal static string? UiChecklist(FlightRow flight) =>
        Path.GetDirectoryName(Path.GetFullPath(flight.RepoPath)) is { } root && Path.Combine(root, ".github", "docs", "ui-changes.md") is var path && File.Exists(path)
            ? path
            : null;

    /// <summary>
    /// GitHub takes no image from <c>gh</c>, so a PR body or review names an artifact (its title) and points at where the node
    /// keeps its copy, rather than trying to attach it.
    /// </summary>
    internal static string AttachmentRule(string run) =>
        "GitHub takes no image from gh: a PR body or a review names the artifact (its title) and says `" + run + " attachments <code>` "
        + "(the flight's code) for where the node keeps the copy, never an attachment of its own.";
}
