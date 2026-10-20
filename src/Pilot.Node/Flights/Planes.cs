using System.Diagnostics;
using System.Text;

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

/// <summary>A plane as made: its repo's main checkout, its branch and worktree, and what the branch was made from.</summary>
public sealed record Plane(string RepoPath, string Branch, string Worktree, string Base);

/// <summary>
/// Planes: one worktree and one branch per flight, made from the repo's default branch, so a flight's legs fly in order
/// on one branch and land as one PR, with no merge-order race between a flight's PRs (v1's DIR.Lib #115 and #117,
/// tianwen #1613 and #1617).
/// </summary>
public sealed class Planes(IGit git)
{
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

    /// <summary>Makes the plane for <paramref name="code"/> in <paramref name="repoPath"/>, or throws with git's words.</summary>
    public async Task<Plane> MakeAsync(string repoPath, string code, CancellationToken cancellationToken)
    {
        // Fresh from the remote when there is one; offline, the plane is made from what the checkout has.
        await git.RunAsync(["fetch", "--quiet", "origin"], repoPath, cancellationToken);
        var head = await git.RunAsync(["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], repoPath, cancellationToken);
        var baseRef = head.Ok && head.Output.Trim().Length > 0 ? head.Output.Trim() : null;
        if (baseRef is null)
        {
            // No remote: the main checkout's branch, by name. Not HEAD, which inside the plane is the plane's own tip,
            // so a go-around's "commits past the base" would always read as none.
            var current = await git.RunAsync(["symbolic-ref", "--short", "HEAD"], repoPath, cancellationToken);
            baseRef = current.Ok && current.Output.Trim().Length > 0 ? current.Output.Trim() : "HEAD";
        }

        var name = code.ToLowerInvariant();
        var branch = $"flight/{name}";
        var worktree = Path.Combine(repoPath, ".claude", "worktrees", name);
        var add = await git.RunAsync(["worktree", "add", "--no-track", "-b", branch, worktree, baseRef], repoPath, cancellationToken);
        if (!add.Ok)
        {
            throw new FlightException($"git could not make {code}'s plane: {add.Why}");
        }
        return new Plane(repoPath, branch, worktree, baseRef);
    }

    /// <summary>Whether git has <paramref name="worktree"/> as a worktree of the repo, not merely a folder there.</summary>
    public async Task<bool> IsRegisteredAsync(string repoPath, string worktree, CancellationToken cancellationToken)
    {
        var list = await git.RunAsync(["worktree", "list", "--porcelain"], repoPath, cancellationToken);
        if (!list.Ok)
        {
            return false;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var wanted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        // One block per worktree, blank-line separated. A torn-down one is still listed, marked prunable: that is gone.
        foreach (var block in list.Output.Replace("\r", "", StringComparison.Ordinal).Split("\n\n"))
        {
            var lines = block.Split('\n');
            if (lines.FirstOrDefault(l => l.StartsWith("worktree ", StringComparison.Ordinal)) is { } head
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(head["worktree ".Length..].Trim())), wanted, comparison))
            {
                return !lines.Any(l => l.StartsWith("prunable", StringComparison.Ordinal));
            }
        }
        return false;
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
        DeleteLeftover(worktree);
        var add = await git.RunAsync(["worktree", "add", worktree, branch], repoPath, cancellationToken);
        if (!add.Ok)
        {
            throw new FlightException($"git could not check out the plane {branch} at {worktree} again: {add.Why}");
        }
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
    /// Removes a landed plane's worktree, keeping its branch. Refuses one with changes not committed: its work would go
    /// with it. A worktree already gone, or torn down by Claude Code, is fine.
    /// </summary>
    /// <returns>Null when the plane is gone; else why its folder is still there (something holds it), which the caller says.</returns>
    public async Task<string?> RemoveAsync(string repoPath, string worktree, CancellationToken cancellationToken)
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
        var status = await git.RunAsync(["status", "--porcelain"], worktree, cancellationToken);
        if (!status.Ok)
        {
            throw new FlightException($"git could not read {worktree}: {status.Why}");
        }
        if (status.Output.Trim().Length > 0)
        {
            throw new FlightException($"{worktree} holds changes not committed; commit or drop them, then land it");
        }
        // A worker's claude -w locks the worktree it enters; a clean one is let go before it is removed.
        await git.RunAsync(["worktree", "unlock", worktree], repoPath, cancellationToken);
        GitRun remove;
        var tries = 0;
        while (!(remove = await git.RunAsync(["worktree", "remove", worktree], repoPath, cancellationToken)).Ok)
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
            if (++tries >= 5 || !remove.Why.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                throw new FlightException($"git could not remove {worktree}: {remove.Why}");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        return null;
    }
}

/// <summary>A flight command that could not be done, with why: the command's error.</summary>
public sealed class FlightException(string message) : Exception(message);
