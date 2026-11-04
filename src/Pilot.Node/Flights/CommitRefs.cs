using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// A commit as the node shows it (#691): 9 characters of its sha, which is a link to the commit on GitHub, beside the words that
/// say what it is. The one place the short form and the URL are made.
/// </summary>
public static partial class CommitRefs
{
    /// <summary>How many characters of a sha show.</summary>
    public const int ShortLength = 9;

    /// <summary>The sha cut to <see cref="ShortLength"/> characters (all of it when it is shorter).</summary>
    public static string Short(string sha) => sha.Length > ShortLength ? sha[..ShortLength] : sha;

    /// <summary>The commit's page on GitHub: <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/commit/&lt;sha&gt;</c>, for <paramref name="repo"/> as <c>owner/name</c>.</summary>
    public static string Url(string repo, string sha) => $"https://github.com/{repo.Trim('/')}/commit/{sha}";

    [GeneratedRegex(@"(?<![0-9A-Za-z])(?=[0-9a-f]*[0-9])(?=[0-9a-f]*[a-f])[0-9a-f]{9}(?![0-9A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex ShortSha();

    [GeneratedRegex(@"github\.com/(?<repo>[^/\s]+/[^/\s#?]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RepoOfUrl();

    /// <summary>The <c>owner/name</c> a GitHub URL (a PR's, a commit's) is in; null for any other.</summary>
    public static string? RepoOf(string? url) => url is not null && RepoOfUrl().Match(url) is { Success: true } m ? m.Groups["repo"].Value : null;

    /// <summary>
    /// The commits the texts name, as the card carries them: each 9-character sha that mixes digits and letters (a word or a number is
    /// none), mapped to its commit page in <paramref name="repo"/>. Empty when the repo is not known.
    /// </summary>
    public static Dictionary<string, string> Named(string? repo, IEnumerable<string?> texts)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(repo))
        {
            return found;
        }
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            foreach (Match m in ShortSha().Matches(text))
            {
                found.TryAdd(m.Value, Url(repo, m.Value));
            }
        }
        return found;
    }
}
