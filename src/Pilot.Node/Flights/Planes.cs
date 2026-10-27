using System.Diagnostics;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>One git run: its exit code and what it wrote.</summary>
public sealed record GitRun(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>What went wrong, in a line, for a command's error.</summary>
    public string Why => (Error.Trim().Length > 0 ? Error : Output).Trim().Split('\n')[0].Trim();
}

/// <summary>The git calls a plane needs, so a test decides what git says.</summary>
public interface IGit
{
    Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken);
}

/// <summary><see cref="IGit"/> by the git CLI on <c>PATH</c>.</summary>
public sealed class GitCli : IGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        // Never a prompt for credentials or an editor: the node has no terminal to show one in.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return new GitRun(process.ExitCode, await output, await error);
    }
}

/// <summary>A worktree git lists: its folder, its branch (null when detached) and whether its folder is gone (prunable).</summary>
public sealed record WorktreeEntry(string Path, string? Branch, bool Prunable);

/// <summary>A plane as made: its repo's main checkout, its branch and worktree, and what the branch was made from.</summary>
public sealed record Plane(string RepoPath, string Branch, string Worktree, string Base);

/// <summary>
/// Planes: one worktree and one branch per flight, made from the repo's default branch, so a flight's legs fly in order
/// on one branch and land as one PR, with no merge-order race between a flight's PRs (v1's DIR.Lib #115 and #117,
/// tianwen #1613 and #1617).
/// </summary>
public sealed class Planes(IGit git, RemovalQueue? queue = null)
{
    /// <summary>The folders waiting for a start to remove them; null where a test or a host keeps none.</summary>
    public RemovalQueue? Queue => queue;

    /// <summary>The main checkout of the repo <paramref name="path"/> is in, a linked worktree's included; null outside a repo.</summary>
    public async Task<string?> MainCheckoutAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }
        var common = await git.RunAsync(["rev-parse", "--path-format=absolute", "--git-common-dir"], path, cancellationToken);
        if (!common.Ok)
        {
            return null;
        }
        // The common git dir of a checkout is its .git; the main checkout is the folder it sits in.
        var gitDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(common.Output.Trim()));
        return Path.GetDirectoryName(gitDir);
    }

    /// <summary>
    /// What a plane is cut from and its PR goes into: the remote's default branch (<c>origin/main</c>), fresh from a fetch.
    /// A clone with no <c>origin/HEAD</c> (one made by <c>git init</c> and <c>remote add</c>) has it set from the remote,
    /// else <c>origin/main</c> is taken; never the branch the main checkout happens to be on, which once made two planes
    /// from a feature branch whose PR base was then deleted (PIL124 and PIL126, 2026-10-23). With no origin at all, the
    /// main checkout's branch by name (not HEAD, which inside the plane is the plane's own tip).
    /// </summary>
    public async Task<string> RemoteBaseAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (!(await git.RunAsync(["remote", "get-url", "origin"], repoPath, cancellationToken)).Ok)
        {
            var current = await git.RunAsync(["symbolic-ref", "--short", "HEAD"], repoPath, cancellationToken);
            return current.Ok && current.Output.Trim().Length > 0 ? current.Output.Trim() : "HEAD";
        }
        await git.RunAsync(["fetch", "--quiet", "origin"], repoPath, cancellationToken);
        if (await OriginHeadAsync() is { } head)
        {
            return head;
        }
        await git.RunAsync(["remote", "set-head", "origin", "--auto"], repoPath, cancellationToken);
        if (await OriginHeadAsync() is { } set)
        {
            return set;
        }
        return (await git.RunAsync(["rev-parse", "--verify", "--quiet", "refs/remotes/origin/main"], repoPath, cancellationToken)).Ok ? "origin/main" : "origin/master";

        async Task<string?> OriginHeadAsync()
        {
            var head = await git.RunAsync(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], repoPath, cancellationToken);
            return head.Ok && head.Output.Trim().Length > 0 ? head.Output.Trim() : null;
        }
    }

    /// <summary>Makes the plane for <paramref name="code"/> in <paramref name="repoPath"/>, or throws with git's words.</summary>
    public async Task<Plane> MakeAsync(string repoPath, string code, CancellationToken cancellationToken, WorkItem? item = null)
    {
        var baseRef = await RemoteBaseAsync(repoPath, cancellationToken);

        // The folder is the code alone; only the branch carries the tracker's link (flight/bul101645-sc-101645).
        var name = code.ToLowerInvariant();
        var branch = $"flight/{name}{item?.BranchSuffix}";
        var worktree = Path.Combine(repoPath, ".claude", "worktrees", name);
        var add = await git.RunAsync(["worktree", "add", "--no-track", "-b", branch, worktree, baseRef], repoPath, cancellationToken);
        if (!add.Ok)
        {
            throw new FlightException($"git could not make {code}'s plane: {add.Why}");
        }
        PlaneSettings.DisableRepoMcpServers(worktree);
        return new Plane(repoPath, branch, worktree, baseRef);
    }

    /// <summary>Whether git has <paramref name="worktree"/> as a worktree of the repo, not merely a folder there.</summary>
    public async Task<bool> IsRegisteredAsync(string repoPath, string worktree, CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var wanted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        // A torn-down one is still listed, marked prunable: that is gone.
        return await WorktreesAsync(repoPath, cancellationToken) is { } listed
            && listed.FirstOrDefault(w => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(w.Path)), wanted, comparison)) is { Prunable: false };
    }

    /// <summary>
    /// The repo's worktrees as <c>git worktree list --porcelain</c> says them, the main checkout first; null when git
    /// cannot list them.
    /// </summary>
    public async Task<IReadOnlyList<WorktreeEntry>?> WorktreesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var list = await git.RunAsync(["worktree", "list", "--porcelain"], repoPath, cancellationToken);
        if (!list.Ok)
        {
            return null;
        }
        var entries = new List<WorktreeEntry>();
        // One block per worktree, blank-line separated.
        foreach (var block in list.Output.Replace("\r", "", StringComparison.Ordinal).Split("\n\n"))
        {
            var lines = block.Split('\n');
            if (lines.FirstOrDefault(l => l.StartsWith("worktree ", StringComparison.Ordinal)) is not { } head)
            {
                continue;
            }
            var branch = lines.FirstOrDefault(l => l.StartsWith("branch ", StringComparison.Ordinal)) is { } b ? b["branch ".Length..].Trim() : null;
            entries.Add(new WorktreeEntry(head["worktree ".Length..].Trim(),
                branch is not null && branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch,
                lines.Any(l => l.StartsWith("prunable", StringComparison.Ordinal))));
        }
        return entries;
    }

    /// <summary>The remote's default branch as last fetched (<c>origin/main</c>), read without a fetch; null when the repo has no origin to say.</summary>
    public async Task<string?> DefaultRefAsync(string repoPath, CancellationToken cancellationToken)
    {
        var head = await git.RunAsync(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], repoPath, cancellationToken);
        if (head.Ok && head.Output.Trim().Length > 0)
        {
            return head.Output.Trim();
        }
        foreach (var guess in new[] { "origin/main", "origin/master" })
        {
            if ((await git.RunAsync(["rev-parse", "--verify", "--quiet", $"refs/remotes/{guess}"], repoPath, cancellationToken)).Ok)
            {
                return guess;
            }
        }
        return null;
    }

    /// <summary>The commits <paramref name="path"/>'s HEAD is ahead of and behind <paramref name="baseRef"/>; null when git cannot count them.</summary>
    public async Task<(int Ahead, int Behind)?> AheadBehindAsync(string path, string baseRef, CancellationToken cancellationToken)
    {
        // Left is what only the base has (behind), right what only HEAD has (ahead).
        var count = await git.RunAsync(["rev-list", "--left-right", "--count", $"{baseRef}...HEAD"], path, cancellationToken);
        var parts = count.Ok ? count.Output.Split(['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries) : [];
        return parts.Length == 2 && int.TryParse(parts[0], out var behind) && int.TryParse(parts[1], out var ahead) ? (ahead, behind) : null;
    }

    /// <summary>The diff of <paramref name="path"/>'s HEAD against <paramref name="baseRef"/> as a PR shows it (three dots); null when git cannot say.</summary>
    public async Task<(int Files, int Added, int Removed)?> DiffStatAsync(string path, string baseRef, CancellationToken cancellationToken)
    {
        var numstat = await git.RunAsync(["diff", "--numstat", $"{baseRef}...HEAD"], path, cancellationToken);
        if (!numstat.Ok)
        {
            return null;
        }
        var files = 0;
        var added = 0;
        var removed = 0;
        foreach (var row in numstat.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = row.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            files++;
            // A binary file is "-" and "-": a file, no lines.
            added += int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0;
            removed += int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0;
        }
        return (files, added, removed);
    }

    /// <summary>
    /// Whether <paramref name="path"/> holds changes not committed; null when git cannot say. For a main checkout,
    /// <paramref name="withoutPlanes"/> leaves out <c>.claude/worktrees</c>, which holds the planes and is no change of its own.
    /// </summary>
    public async Task<bool?> IsDirtyAsync(string path, CancellationToken cancellationToken, bool withoutPlanes = false)
    {
        var status = await git.RunAsync(withoutPlanes ? ["status", "--porcelain", "--", ".", ":(exclude).claude/worktrees"] : ["status", "--porcelain"], path, cancellationToken);
        return status.Ok ? status.Output.Trim().Length > 0 : null;
    }

    /// <summary>
    /// Makes sure the plane's worktree is there, on its branch, before a worker is sent into it. Claude Code tears down a
    /// worktree it entered with <c>-w</c> when that session's job ends by itself (its <c>rm</c>: "and its worktree when
    /// that is safe"), keeping the branch; and <c>-w</c> into a name with no worktree makes a new one on a new branch. The
    /// branch is the plane: a missing worktree is checked out from it again, and a leftover folder of a torn-down one
    /// goes first.
    /// </summary>
    public async Task EnsureAsync(string repoPath, string worktree, string branch, CancellationToken cancellationToken)
    {
        if (await IsRegisteredAsync(repoPath, worktree, cancellationToken))
        {
            return;
        }
        await git.RunAsync(["worktree", "prune"], repoPath, cancellationToken);
        // A folder queued for removal is tried once more first; still held, it is said as such.
        if (queue is not null && queue.Contains(worktree) && !queue.TryOne(worktree))
        {
            throw new FlightException($"{worktree} is queued for removal at the node's next start and a process still holds it; the plane cannot be checked out there until it lets go");
        }
        DeleteLeftover(worktree);
        var add = await git.RunAsync(["worktree", "add", worktree, branch], repoPath, cancellationToken);
        if (!add.Ok)
        {
            throw new FlightException($"git could not check out the plane {branch} at {worktree} again: {add.Why}");
        }
        PlaneSettings.DisableRepoMcpServers(worktree);
    }

    /// <summary>
    /// A worktree at <paramref name="worktree"/> detached at <paramref name="commit"/>, made afresh: a PR review's, pinned at
    /// the head it reviews, so nothing pushed after moves it. Whatever was there before goes first.
    /// </summary>
    /// <returns>The folder the worktree was made in: <paramref name="worktree"/>, or a sibling of it (<c>-2</c>, <c>-3</c>) when that one is held and queued for removal.</returns>
    public async Task<string> PinAsync(string repoPath, string worktree, string commit, CancellationToken cancellationToken)
    {
        // A path in the queue is tried once more before it is used.
        queue?.TryOne(worktree);
        if (queue is null || !queue.Contains(worktree))
        {
            if (await RemoveAsync(repoPath, worktree, cancellationToken, force: true) is { } held)
            {
                if (queue is null)
                {
                    throw new FlightException(held);
                }
                queue.Add(worktree, RemovalQueue.ReviewKind, Path.GetFileName(worktree), held);
            }
        }
        var path = worktree;
        for (var n = 2; queue is not null && queue.Contains(path); n++)
        {
            path = $"{worktree}-{n}";
            // A sibling left by an earlier hold is cleared if it can be.
            if (Directory.Exists(path) && !queue.Contains(path))
            {
                if (await RemoveAsync(repoPath, path, cancellationToken, force: true) is { } siblingHeld)
                {
                    queue.Add(path, RemovalQueue.ReviewKind, Path.GetFileName(path), siblingHeld);
                }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A folder the queue already deleted can still be registered, its remove having failed while held.
        await git.RunAsync(["worktree", "prune"], repoPath, cancellationToken);
        var add = await git.RunAsync(["worktree", "add", "--detach", path, commit], repoPath, cancellationToken);
        if (!add.Ok)
        {
            throw new FlightException($"git could not check out {commit[..Math.Min(8, commit.Length)]} at {path}: {add.Why}");
        }
        PlaneSettings.DisableRepoMcpServers(path);
        return path;
    }

    /// <summary>Removes the folder of a worktree git no longer has: what a torn-down worktree leaves when a file was held.</summary>
    private static void DeleteLeftover(string worktree)
    {
        if (!Directory.Exists(worktree))
        {
            return;
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(worktree, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(worktree, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FlightException($"{worktree} is left over from a worktree git no longer has, and could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// The plane's commits whose change is nowhere else: on no remote branch, and not in <paramref name="mainRef"/> by
    /// patch (a rebase-merged commit is in main under another id; a squash-merged one is not, so it counts while its
    /// branch is gone from the remote). Their subjects, oldest first; empty when every commit is pushed or merged, or the
    /// plane cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<string>> UnlandedAsync(string worktree, string mainRef, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree))
        {
            return [];
        }
        await git.RunAsync(["fetch", "--quiet", "origin"], worktree, cancellationToken);
        var unpushed = await git.RunAsync(["rev-list", "--reverse", "HEAD", "--not", "--remotes"], worktree, cancellationToken);
        var cherry = await git.RunAsync(["cherry", mainRef, "HEAD"], worktree, cancellationToken);
        if (!unpushed.Ok || !cherry.Ok)
        {
            return [];
        }
        var notInMain = cherry.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith('+')).Select(l => l[1..].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loose = new List<string>();
        foreach (var sha in unpushed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(notInMain.Contains))
        {
            var subject = await git.RunAsync(["log", "-1", "--format=%h %s", sha], worktree, cancellationToken);
            loose.Add(subject.Ok ? subject.Output.Trim() : sha[..Math.Min(9, sha.Length)]);
        }
        return loose;
    }

    /// <summary>
    /// The plane's head, and its commits whose change is not in <paramref name="mainRef"/> (<c>git cherry</c>, so a commit
    /// rebased into main counts as in), as <c>&lt;short sha&gt; &lt;subject&gt;</c>; null head when the plane is gone.
    /// </summary>
    public async Task<(string? Head, IReadOnlyList<string> Commits)> NotInMainAsync(string worktree, string mainRef, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree))
        {
            return (null, []);
        }
        var head = await git.RunAsync(["rev-parse", "HEAD"], worktree, cancellationToken);
        var cherry = await git.RunAsync(["cherry", mainRef, "HEAD"], worktree, cancellationToken);
        if (!head.Ok || !cherry.Ok)
        {
            return (null, []);
        }
        var commits = new List<string>();
        foreach (var sha in cherry.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith('+')).Select(l => l[1..].Trim()))
        {
            var subject = await git.RunAsync(["log", "-1", "--format=%h %s", sha], worktree, cancellationToken);
            commits.Add(subject.Ok ? subject.Output.Trim() : sha[..Math.Min(9, sha.Length)]);
        }
        return (head.Output.Trim(), commits);
    }

    /// <summary>The plane's commits past <paramref name="head"/> (subjects, oldest first): what a merge at that head left out.</summary>
    public async Task<IReadOnlyList<string>> PastAsync(string worktree, string head, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree))
        {
            return [];
        }
        var past = await git.RunAsync(["log", "--reverse", "--format=%h %s", $"{head}..HEAD"], worktree, cancellationToken);
        return past.Ok ? past.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }

    /// <summary>Whether the plane holds changes not committed, in a line, or null when it is clean (or gone).</summary>
    public async Task<string?> UncommittedAsync(string worktree, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree))
        {
            return null;
        }
        var status = await git.RunAsync(["status", "--porcelain"], worktree, cancellationToken);
        var lines = status.Ok ? status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
        return lines.Length == 0 ? null : $"{lines.Length} file{(lines.Length == 1 ? "" : "s")} not committed ({string.Join(", ", lines.Take(3).Select(l => l.Trim()))}{(lines.Length > 3 ? ", ..." : "")})";
    }

    /// <summary>
    /// Removes a landed plane's worktree, keeping its branch. Refuses one with changes not committed: its work would go
    /// with it. A worktree already gone, or torn down by Claude Code, is fine.
    /// </summary>
    /// <param name="force">Removes it whatever it holds: a review's worktree, where nothing is anyone's work.</param>
    /// <returns>Null when the plane is gone; else why its folder is still there (something holds it), which the caller says.</returns>
    public async Task<string?> RemoveAsync(string repoPath, string worktree, CancellationToken cancellationToken, bool force = false)
    {
        if (!Directory.Exists(worktree) || !await IsRegisteredAsync(repoPath, worktree, cancellationToken))
        {
            await git.RunAsync(["worktree", "prune"], repoPath, cancellationToken);
            try
            {
                DeleteLeftover(worktree);
            }
            catch (FlightException ex)
            {
                // A folder git no longer has, still held: the flight lands all the same, as it holds nothing of git's,
                // but the folder is said, never left in silence.
                return ex.Message;
            }
            return null;
        }
        if (!force)
        {
            var status = await git.RunAsync(["status", "--porcelain"], worktree, cancellationToken);
            if (!status.Ok)
            {
                throw new FlightException($"git could not read {worktree}: {status.Why}");
            }
            if (status.Output.Trim().Length > 0)
            {
                throw new FlightException($"{worktree} holds changes not committed; commit or drop them, then land it");
            }
        }
        // A worker's claude -w locks the worktree it enters; a clean one is let go before it is removed.
        await git.RunAsync(["worktree", "unlock", worktree], repoPath, cancellationToken);
        GitRun remove;
        var tries = 0;
        while (!(remove = await git.RunAsync(force ? ["worktree", "remove", "--force", worktree] : ["worktree", "remove", worktree], repoPath, cancellationToken)).Ok)
        {
            // Torn down under us between the look and the remove: as gone as it gets.
            if (remove.Why.Contains("is not a working tree", StringComparison.OrdinalIgnoreCase))
            {
                await git.RunAsync(["worktree", "prune"], repoPath, cancellationToken);
                try
                {
                    DeleteLeftover(worktree);
                }
                catch (FlightException ex)
                {
                    return ex.Message;
                }
                return null;
            }
            // A worker stopped a moment ago can still hold a file open on Windows: give it a few seconds.
            var held = remove.Why.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
            if (++tries >= 5 || !held)
            {
                var message = $"git could not remove {worktree}: {remove.Why}";
                // Still held after the wait (git says "Permission denied" or, for a file held without sharing, "failed to
                // delete ... Invalid argument"): with a queue it is the queue's to remove at the next start.
                if (queue is not null && (held || remove.Why.Contains("failed to delete", StringComparison.OrdinalIgnoreCase)))
                {
                    return message;
                }
                throw new FlightException(message);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        // git removed the worktree but, on Windows, leaves its folder when a process still sits in it (a session's
        // working folder): empty and unregistered, it would stay for good (pil173, pil187, SE1 2026-10-25).
        try
        {
            DeleteLeftover(worktree);
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        return null;
    }
}

/// <summary>A flight command that could not be done, with why: the command's error.</summary>
public sealed class FlightException(string message) : Exception(message);
