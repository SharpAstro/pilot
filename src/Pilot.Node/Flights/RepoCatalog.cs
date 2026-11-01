using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The repos a flight can be made in, for the new-flight wizard: the git checkouts directly under each repo root. The
/// roots are the ones the node was given (<c>--repo-root</c>, <c>PILOT_REPO_ROOTS</c>) and the folders the flights' own
/// repos sit in, so an org root's sibling clones are offered once one of them has flown.
/// </summary>
public sealed partial class RepoCatalog(NodeState state, NodeOptions options)
{
    /// <summary>The variable naming more repo roots, separated as PATH is.</summary>
    public const string RootsVariable = "PILOT_REPO_ROOTS";

    [GeneratedRegex(@"\[remote ""origin""\][^\[]*?url\s*=\s*(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex OriginUrl();

    [GeneratedRegex(@"github\.com[:/]([^/\s]+)/([^/\s]+?)(?:\.git)?/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRepo();

    /// <summary>Every repo under the roots, the busiest first, then by name.</summary>
    public RepoInfo[] Repos()
    {
        var flights = state.Flights();
        var roots = options.RepoRoots
            .Concat((Environment.GetEnvironmentVariable(RootsVariable) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(flights.Select(f => Path.GetDirectoryName(f.RepoPath)).OfType<string>())
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var repos = new Dictionary<string, RepoInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var folder in folders)
            {
                // A clone's .git is a folder; a worktree's or a submodule's is a file, and neither is a repo to fly in.
                var git = Path.Combine(folder, ".git");
                if (!Directory.Exists(git) || repos.ContainsKey(folder))
                {
                    continue;
                }
                var name = Path.GetFileName(folder);
                repos[folder] = new RepoInfo
                {
                    Name = name,
                    Path = folder,
                    GitHub = GitHubOf(git),
                    Flights = flights.Count(f => string.Equals(f.RepoPath, folder, StringComparison.OrdinalIgnoreCase)),
                };
            }
        }
        return [.. repos.Values.OrderByDescending(r => r.Flights).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The page of a repo's short rules for pilot (#443): a repo that carries one is one pilot works in, before it has flown.</summary>
    public const string MarkerFile = "PILOT.md";

    /// <summary>
    /// The repos pilot works in, of <see cref="Repos"/>: on GitHub, and flown in (landed flights too) or reviewed in
    /// (<see cref="PullRequestWatch.TrackedRepos"/>), holding an idea the node has read, or carrying a <see cref="MarkerFile"/>. Only
    /// these are read on GitHub and offered for an idea: every other clone under a root (WebAssembly's wabt and wasi-libc, a work org's
    /// repos) cost four gh calls each read and filled the Ideas pane's milestone lists with repos pilot never touched (the owner,
    /// 2026-10-30, as #463 had found for the PR watch).
    /// </summary>
    public RepoInfo[] Tracked()
    {
        var worked = new HashSet<string>(PullRequestWatch.TrackedRepos(state.Flights(), state.PrReviews()), StringComparer.OrdinalIgnoreCase);
        var withIdeas = new HashSet<string>(state.Ideas().Select(i => i.Repo), StringComparer.OrdinalIgnoreCase);
        return [.. Repos().Where(r => r.GitHub is { } gitHub
            && (worked.Contains(gitHub) || withIdeas.Contains(r.Name) || File.Exists(Path.Combine(r.Path, MarkerFile))))];
    }

    /// <summary>The repo's GitHub <c>owner/name</c>, from its origin's URL; null for one not on GitHub.</summary>
    internal static string? GitHubOf(string gitFolder)
    {
        try
        {
            return OriginUrl().Match(File.ReadAllText(Path.Combine(gitFolder, "config"))) is { Success: true } origin
                   && GitHubRepo().Match(origin.Groups[1].Value) is { Success: true } repo
                ? $"{repo.Groups[1].Value}/{repo.Groups[2].Value}"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
