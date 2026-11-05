namespace Pilot.Node.Flights;

/// <summary>
/// What a whole command is, for how long it takes: the repo (so a plane's runs are its clone's) and every work step in order, each
/// as <see cref="JobKey"/> keeps it (tool, verb, target, flags) plus the full text of its <c>--filter</c>. A <c>cd</c> only moves
/// the folder the next step runs in, and plumbing (<c>| tail -20</c>) is not a step. Unlike <see cref="JobKey"/>, which names the
/// last step only (for memory), two commands share a key only when they do the same work from start to end (PIL767).
/// </summary>
/// <param name="Repo">The clone the command runs in.</param>
/// <param name="Steps">Each work step as text, in order.</param>
public sealed record CommandKey(string Repo, IReadOnlyList<string> Steps)
{
    /// <summary>The key as one string; equal for equal commands. A command of one step is that step's own text.</summary>
    public string Text => Steps.Count == 0 ? Repo : string.Join(" && ", Steps);

    public bool Equals(CommandKey? other) => other is not null && Text == other.Text;

    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Text;

    /// <summary>The key of <paramref name="command"/> run in <paramref name="cwd"/>.</summary>
    public static CommandKey Of(string command, string cwd)
    {
        cwd = InClone(cwd);
        var folder = cwd;
        var steps = new List<string>();
        foreach (var words in JobKey.Segments(command))
        {
            var tool = JobKey.ToolName(words[0]);
            if (tool is "cd" or "set-location" or "pushd" or "sl")
            {
                if (words.Skip(1).FirstOrDefault(w => !w.StartsWith('-')) is { } to)
                {
                    folder = JobKey.Resolve(folder, to);
                }
                continue;
            }
            if (JobKey.IsPlumbing(tool))
            {
                continue;
            }
            steps.Add($"{JobKey.KeyOf(tool, words, folder)}|filter={FilterOf(words)}");
        }
        return new CommandKey(JobKey.RepoOf(cwd), steps);
    }

    /// <summary>
    /// A folder inside a plane (<c>&lt;clone&gt;/.claude/worktrees/&lt;code&gt;/rest</c>) as the same folder in the clone. A plane is
    /// removed when its flight lands, and a removed plane's folders resolve differently on disk (no <c>.git</c> of its own, no
    /// project found) than a live one's, so every row is keyed against the clone, which is always there.
    /// </summary>
    internal static string InClone(string cwd)
    {
        var full = Path.GetFullPath(cwd);
        var sep = Path.DirectorySeparatorChar;
        var marker = $"{sep}.claude{sep}worktrees{sep}";
        var at = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
        {
            return cwd;
        }
        var afterMarker = at + marker.Length;
        var next = full.IndexOf(sep, afterMarker);
        var rest = next < 0 ? "" : full[next..];
        return full[..at] + rest;
    }

    /// <summary>The key of one of this command's steps run alone.</summary>
    public CommandKey Step(int index) => new(Repo, [Steps[index]]);

    private static string FilterOf(List<string> words)
    {
        for (var i = 1; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < words.Count ? words[i + 1] : "";
            }
            if (word.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            {
                return word["--filter=".Length..];
            }
        }
        return "";
    }
}
