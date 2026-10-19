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
        var baseRef = head.Ok && head.Output.Trim().Length > 0 ? head.Output.Trim() : "HEAD";

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

    /// <summary>
    /// Removes a landed plane's worktree, keeping its branch. Refuses one with changes not committed: its work would go
    /// with it. A worktree already gone is fine.
    /// </summary>
    public async Task RemoveAsync(string repoPath, string worktree, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree))
        {
            await git.RunAsync(["worktree", "prune"], repoPath, cancellationToken);
            return;
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
        var remove = await git.RunAsync(["worktree", "remove", worktree], repoPath, cancellationToken);
        if (!remove.Ok)
        {
            throw new FlightException($"git could not remove {worktree}: {remove.Why}");
        }
    }
}

/// <summary>A flight command that could not be done, with why: the command's error.</summary>
public sealed class FlightException(string message) : Exception(message);
