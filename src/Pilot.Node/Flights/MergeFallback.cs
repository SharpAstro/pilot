namespace Pilot.Node.Flights;

/// <summary>
/// PIL690: GitHub refuses a rebase merge with "This branch can't be rebased" when the branch holds merge commits (the node brings a
/// branch up to date by merging its base in). That is no refusal to ring about: the same head is merged again by squash, or by merge
/// where the repo does not allow squash.
/// </summary>
internal static class MergeFallback
{
    /// <summary>The method that replaces rebase for a repo that allows <paramref name="allowed"/>: squash first, then merge; null with neither.</summary>
    internal static string? Replacement(IReadOnlyList<string> allowed) =>
        allowed.Contains("squash", StringComparer.OrdinalIgnoreCase) ? "squash"
        : allowed.Contains("merge", StringComparer.OrdinalIgnoreCase) ? "merge"
        : null;

    /// <summary>
    /// Merges as <see cref="IGitHub.MergeAsync"/> does. When <paramref name="method"/> is rebase and GitHub says the branch cannot be
    /// rebased, the same head is merged once by squash or merge in its place (the caller says so, it holds no lock here). A refusal
    /// of any other kind, or with neither squash nor merge allowed, is thrown as it was.
    /// </summary>
    /// <returns>True when the PR was queued, not merged; Used is the method that replaced rebase, else null.</returns>
    internal static async Task<(bool Queued, string? Used)> MergeAsync(IGitHub github, string repoPath, int number, string head, string method,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await github.MergeAsync(repoPath, number, head, method, cancellationToken), null);
        }
        catch (FlightException ex) when (string.Equals(method, "rebase", StringComparison.OrdinalIgnoreCase) && GhCli.CannotBeRebased(ex.Message))
        {
            var instead = Replacement(await github.AllowedMergeMethodsAsync(repoPath, cancellationToken));
            if (instead is null)
            {
                throw;
            }
            return (await github.MergeAsync(repoPath, number, head, instead, cancellationToken), instead);
        }
    }

    /// <summary>What the merge entry and its card say once rebase was refused and <paramref name="instead"/> merges the PR.</summary>
    internal static string Note(string instead) => $"rebase was refused (the branch can't be rebased); merging with {instead} instead";

    /// <summary>What the merge entry and its card say when the branch holds a merge commit, so <paramref name="instead"/> is used and rebase is never tried.</summary>
    internal static string PreferredNote(string instead) => $"the branch holds a merge commit, which a rebase cannot replay; merging with {instead} instead";
}
