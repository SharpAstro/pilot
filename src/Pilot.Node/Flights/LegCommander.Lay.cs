namespace Pilot.Node.Flights;

/// <summary>What <see cref="LegCommander.ProbeRebaseAsync"/> found: whether the branch rebases onto its base, and why not.</summary>
/// <param name="Clean">True when every commit of the branch replays onto the base.</param>
/// <param name="Files">The files left unmerged where the rebase stopped; empty when clean or when the probe could not run.</param>
/// <param name="Error">Why the probe could not run (a worktree that would not add, a base that is not there); null when it ran.</param>
public sealed record RebaseProbe(bool Clean, IReadOnlyList<string> Files, string? Error = null);

public sealed partial class LegCommander
{
    /// <summary>
    /// Whether the branch of the plane at <paramref name="plane"/> rebases onto <c>origin/&lt;baseBranch&gt;</c>, as a merge queue that
    /// rebases would do. A PR can merge cleanly (merge-tree is clean) and still not rebase: a keep-ours merge in its history, with later
    /// commits touching the same lines, stops a replay one commit at a time. The test runs in a detached throwaway worktree in a
    /// temp folder at the plane's HEAD, which is aborted and removed whatever happens; the plane and its branch are never touched.
    /// </summary>
    public async Task<RebaseProbe> ProbeRebaseAsync(string plane, string baseBranch, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-rebase-probe-" + Guid.NewGuid().ToString("N")[..12]);
        var added = false;
        try
        {
            var add = await git.RunAsync(["worktree", "add", "--detach", folder, "HEAD"], plane, cancellationToken);
            if (!add.Ok)
            {
                return new RebaseProbe(false, [], $"the rebase test worktree could not be added ({add.Why})");
            }
            added = true;
            var rebase = await git.RunAsync(["rebase", $"origin/{baseBranch}"], folder, cancellationToken);
            if (rebase.Ok)
            {
                return new RebaseProbe(true, []);
            }
            var unmerged = await git.RunAsync(["diff", "--name-only", "--diff-filter=U"], folder, cancellationToken);
            var files = unmerged.Ok
                ? unmerged.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            await git.RunAsync(["rebase", "--abort"], folder, cancellationToken);
            return files.Length == 0
                ? new RebaseProbe(false, [], $"the rebase onto origin/{baseBranch} stopped ({rebase.Why})")
                : new RebaseProbe(false, files);
        }
        finally
        {
            if (added)
            {
                await git.RunAsync(["rebase", "--abort"], folder, CancellationToken.None);
                await git.RunAsync(["worktree", "remove", "--force", folder], plane, CancellationToken.None);
            }
            await git.RunAsync(["worktree", "prune"], plane, CancellationToken.None);
            if (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held: git has forgotten it already, and the temp folder is swept by the OS.
                }
            }
        }
    }
}
