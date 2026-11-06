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
    /// How a branch takes the base, for the worker and both reviewers: merging it in is the way, and merge commits on a
    /// working branch are never a finding, because the merge queue rebases each PR and lands it linear. A review that called
    /// them a defect sent TIA1874's worker to rewrite its pushed branch, which then needed a force push only the owner could
    /// allow (DE1, 2026-10-30); TIA1910 and TIA1915 stalled the same way.
    /// </summary>
    internal const string BranchHistoryRule =
        "- A branch takes its base by a merge (`git fetch origin` then `git merge origin/<base>`). Merge commits on a working branch are\n" +
        "  fine and never a finding: the merge queue rebases each PR, so main stays linear whatever the branch holds. Never rebase a\n" +
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

    /// <summary>The line the planner is told: a leg's check fits its change, and the whole suite is CI's on the PR (pilot #752).</summary>
    internal const string CheckScopeRule =
        "A leg's check filters `dotnet test` over the test classes of the files the flight touches plus the classes the leg names (`--filter 'FullyQualifiedName~FooTests|FullyQualifiedName~BarTests'`). " +
        "The whole suite is CI's, on the PR: `leg plan` refuses a `dotnet test` with no --filter, suggesting one, unless it is given `--whole-suite \"<why>\"`.";

    /// <summary>The line the worker is told: run the check and the classes touched, never the whole suite, which CI runs on the PR and <c>pilot run</c> refuses (PIL821).</summary>
    internal const string WorkerScopeRule =
        "Run the check and the test classes you touch, never the whole suite: CI runs the whole suite on the PR, and an unrelated failure in it is not this leg's. " +
        "`pilot run` refuses a whole-suite `dotnet test` (one with no --filter, or only `Category!=` ones) and suggests the --filter from the test classes your flight touches; the check the leg was planned with still runs as planned. " +
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
