using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What kind of command a job is, for what it costs in memory: the repo (normalised to its clone, so a plane of a flight is
/// the clone's), the tool and verb, the project or solution it works on (relative to the repo), and the flags that change
/// the work (<c>--no-build</c>, <c>-c</c>, whether a <c>--filter</c> is given). The filter's text, the logger and the verbosity
/// are left out: they change what is shown or which tests run, not what kind of run it is.
/// </summary>
/// <remarks>Written as <c>repo|tool verb|target|flags</c> on the job row, so the history of runs needs nothing but the rows.</remarks>
public sealed record JobKey(string Repo, string ToolVerb, string Target, string Flags)
{
    /// <summary>The flags of dotnet's verbs that take a value, so the value is not mistaken for the project.</summary>
    private static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "--configuration", "-f", "--framework", "-r", "--runtime", "--arch", "-a", "--os", "-o", "--output", "--filter", "--logger", "-l",
        "-v", "--verbosity", "--results-directory", "-p", "--property", "--settings", "-s", "--test-adapter-path", "--diag", "-d", "--collect",
        "--source", "--project", "--solution", "--artifacts-path", "--version-suffix",
    };

    private static readonly string[] ProjectExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"];

    // Commands that shape or report a run's output rather than do its work: a job's key is never one of them while there is work beside it.
    private static readonly HashSet<string> Plumbing = new(StringComparer.OrdinalIgnoreCase)
    {
        "tail", "head", "grep", "egrep", "sed", "awk", "cut", "sort", "uniq", "wc", "tee", "cat", "echo", "printf", "true", "false",
        "select-object", "select-string", "out-null", "out-file", "out-host", "tee-object", "write-output", "write-host",
    };

    public override string ToString() => $"{Repo}|{ToolVerb}|{Target}|{Flags}";

    /// <summary>Reads a key written by <see cref="ToString"/>; null for anything else.</summary>
    public static JobKey? Parse(string? text)
    {
        var parts = text?.Split('|');
        return parts is { Length: 4 } ? new JobKey(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    /// <summary>
    /// The key of a command run in <paramref name="cwd"/>: of the last command in it that does work. A <c>cd</c> only moves the
    /// folder, and what only shapes the output (<c>| tail -20</c>, <c>; echo done</c>) is not the work: keyed on it, every piped
    /// job shared one wrong estimate (review of #357).
    /// </summary>
    public static JobKey Of(string command, string cwd)
    {
        var folder = cwd;
        JobKey? key = null;
        foreach (var words in Segments(command))
        {
            if (words.Count == 0)
            {
                continue;
            }
            var tool = ToolName(words[0]);
            if (tool is "cd" or "set-location" or "pushd" or "sl")
            {
                if (words.Count > 1 && words.Skip(1).FirstOrDefault(w => !w.StartsWith('-')) is { } to)
                {
                    folder = Resolve(folder, to);
                }
                continue;
            }
            if (key is null || !Plumbing.Contains(tool))
            {
                key = KeyOf(tool, words, folder);
            }
        }
        return key ?? new JobKey(RepoOf(cwd), "", "", "");
    }

    private static JobKey KeyOf(string tool, List<string> words, string folder)
    {
        var rest = words.Skip(1).ToList();
        var verb = rest.FirstOrDefault(w => !w.StartsWith('-')) ?? "";
        var flags = new List<string>();
        string? positional = null;
        var seenVerb = false;
        for (var i = 0; i < rest.Count; i++)
        {
            var word = rest[i];
            if (!word.StartsWith('-'))
            {
                if (!seenVerb && word == verb)
                {
                    seenVerb = true;
                }
                else
                {
                    positional ??= word;
                }
                continue;
            }
            // --name=value and -name:value, as well as --name value.
            var cut = word.IndexOfAny(['=', ':']);
            var name = cut > 0 ? word[..cut] : word;
            var inline = cut > 0 ? word[(cut + 1)..] : null;
            var takes = ValueFlags.Contains(name);
            var value = inline ?? (takes && i + 1 < rest.Count ? rest[++i] : null);
            switch (name.ToLowerInvariant())
            {
                case "--no-build":
                    flags.Add("no-build");
                    break;
                case "-c" or "--configuration":
                    flags.Add($"c={value?.ToLowerInvariant()}");
                    break;
                case "--filter":
                    flags.Add("filter");
                    break;
            }
        }
        flags.Sort(StringComparer.Ordinal);
        var full = Path.GetFullPath(folder);
        // The target is relative to the checkout it is in, so a plane's is its clone's; the repo is the clone.
        var target = tool == "dotnet" ? TargetOf(positional, full, RootOf(full)) : "";
        return new JobKey(RepoOf(full),$"{tool} {verb}".TrimEnd(), target, string.Join(',', flags));
    }

    /// <summary>
    /// The project or solution the command works on, relative to the checkout's top (<paramref name="repo"/>): the one named, or the one in the folder named (or
    /// the folder it runs in) when it names none. A folder with no project in it stands for itself.
    /// </summary>
    private static string TargetOf(string? named, string folder, string repo)
    {
        var path = named is null ? folder : Resolve(folder, named);
        if (Directory.Exists(path))
        {
            var found = ProjectIn(path);
            path = found ?? path;
        }
        return Relative(repo, path);
    }

    /// <summary>The solution of a folder, else its one project; null when there is none or there are several projects.</summary>
    private static string? ProjectIn(string folder)
    {
        try
        {
            string[] solutions = [.. Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
            if (solutions.Length == 1)
            {
                return solutions[0];
            }
            string[] projects = [.. Directory.EnumerateFiles(folder).Where(f => ProjectExtensions.Skip(2).Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))];
            return solutions.Length == 0 && projects.Length == 1 ? projects[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Resolve(string folder, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));

    private static string Relative(string repo, string path)
    {
        var relative = Path.GetRelativePath(repo, path).Replace('\\', '/');
        return (relative == "." ? "" : relative).ToLowerInvariant();
    }

    /// <summary>
    /// The clone a folder belongs to: the nearest folder holding a <c>.git</c>, and for a plane of a flight (a worktree under
    /// <c>.claude/worktrees</c> of the clone) the clone itself. A folder in no repo is its own.
    /// </summary>
    public static string RepoOf(string folder)
    {
        var full = Path.GetFullPath(folder);
        var marker = $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}";
        var worktree = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return Normal(worktree > 0 ? full[..worktree] : RootOf(full));
    }

    /// <summary>The top of the checkout a folder is in (the nearest folder holding a <c>.git</c>, a plane's own for a plane); the folder itself in none.</summary>
    private static string RootOf(string full)
    {
        for (var at = new DirectoryInfo(full); at is not null; at = at.Parent)
        {
            if (Directory.Exists(Path.Combine(at.FullName, ".git")) || File.Exists(Path.Combine(at.FullName, ".git")))
            {
                return at.FullName;
            }
        }
        return full;
    }

    private static string Normal(string path) => path.TrimEnd('\\', '/').Replace('\\', '/').ToLowerInvariant();

    private static string ToolName(string word) => Path.GetFileNameWithoutExtension(word.Replace('\\', '/')).ToLowerInvariant();

    /// <summary>The command's words, split into the commands it chains with <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>|</c>; quotes group, redirections go.</summary>
    internal static List<List<string>> Segments(string command)
    {
        var segments = new List<List<string>> { new() };
        var word = new System.Text.StringBuilder();
        var has = false;
        char? quote = null;

        void EndWord()
        {
            if (has && !IsRedirection(word.ToString()))
            {
                segments[^1].Add(word.ToString());
            }
            word.Clear();
            has = false;
        }

        void EndSegment()
        {
            EndWord();
            if (segments[^1].Count > 0)
            {
                segments.Add([]);
            }
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                has = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                EndWord();
            }
            else if (c == '&' && (word.Length > 0 && word[^1] == '>' || i + 1 < command.Length && command[i + 1] == '>'))
            {
                // Part of a redirection (2>&1, >&2, &>file), not a separator: 2>&1 once split a job into a segment "1".
                word.Append(c);
                has = true;
            }
            else if (c is ';' or '|' or '&')
            {
                // && and || are one separator, as a lone | or ;.
                if (i + 1 < command.Length && command[i + 1] == c)
                {
                    i++;
                }
                EndSegment();
            }
            else
            {
                word.Append(c);
                has = true;
            }
        }
        EndSegment();
        return [.. segments.Where(s => s.Count > 0)];
    }

    private static bool IsRedirection(string word) => word.Length > 0 && (word[0] == '>' || word[0] == '<' || (char.IsDigit(word[0]) && word.Contains('>')));
}

/// <summary>What a queued job is expected to need, and what that rests on.</summary>
/// <param name="Bytes">The estimate: the median of its key's last runs' medians, raised to the latest run's when that was higher.</param>
/// <param name="Peak">The highest peak of those runs; shown, never gated on. Null when nothing is known.</param>
/// <param name="Runs">How many runs the estimate rests on; 0 for the guess.</param>
/// <param name="Basis">Which of the fallbacks was used.</param>
public sealed record MemoryEstimate(long Bytes, long? Peak, int Runs, MemoryBasis Basis)
{
    public static string Gigabytes(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

    /// <summary>What the estimate is made of, for a step line: <c>median of its last 3 runs, peak 3.1 GB</c>.</summary>
    public string Describe()
    {
        var peak = Peak is { } p ? $", peak {Gigabytes(p)}" : "";
        return Basis switch
        {
            MemoryBasis.Key => $"median of its last {Plural(Runs)}{peak}",
            MemoryBasis.Target => $"median of {Plural(Runs)} of this target, other flags{peak}",
            MemoryBasis.Verb => $"median of {Plural(Runs)} of the same command in this repo{peak}",
            _ => "a guess, it has not run before",
        };
    }

    private static string Plural(int runs) => runs == 1 ? "run" : $"{runs} runs";
}

public enum MemoryBasis
{
    /// <summary>The same key.</summary>
    Key,

    /// <summary>The same target, other flags.</summary>
    Target,

    /// <summary>The same tool and verb in the repo.</summary>
    Verb,

    /// <summary>Nothing run before: <see cref="JobMemory.Guess"/>.</summary>
    Guess,
}

/// <summary>
/// What a job needs in memory, from what the same kind of command used before: the median of its key's last
/// <see cref="Window"/> runs, each run's value the median of the samples of its process tree (so a short spike in a run never
/// moves it; its peak is shown only). A run whose median is above that raises the estimate at once, as the latest.
/// </summary>
public static class JobMemory
{
    /// <summary>How many of a key's last runs the estimate is the median of.</summary>
    public const int Window = 5;

    /// <summary>What a command nothing is known of is taken to need.</summary>
    public const long Guess = 1L << 30;

    /// <summary>Whether a run says what its command needs: it ended by itself (not cancelled or timed out) and was measured.</summary>
    public static bool Counts(JobRow job) =>
        job.MemoryMedian is > 0 && (job.State == JobState.Succeeded || job is { State: JobState.Failed, ExitCode: not null });

    /// <summary>The estimate for a job whose key is <paramref name="key"/>, from the finished <paramref name="jobs"/>: the key, else the same target, else the same verb in the repo, else <see cref="Guess"/>.</summary>
    public static MemoryEstimate Estimate(IEnumerable<JobRow> jobs, JobKey key)
    {
        var runs = jobs.Where(Counts)
            .Select(j => (Job: j, Key: JobKey.Parse(j.MemoryKey)))
            .Where(r => r.Key is not null && r.Job.EndedAt is not null && r.Key.Repo == key.Repo && r.Key.ToolVerb == key.ToolVerb)
            .OrderByDescending(r => r.Job.EndedAt)
            .ToList();
        foreach (var (basis, match) in new (MemoryBasis, Func<JobKey, bool>)[]
        {
            (MemoryBasis.Key, k => k == key),
            (MemoryBasis.Target, k => k.Target == key.Target),
            (MemoryBasis.Verb, _ => true),
        })
        {
            var last = runs.Where(r => match(r.Key!)).Take(Window).Select(r => r.Job).ToList();
            if (last.Count > 0)
            {
                // The latest is first: one that is above the median raises the estimate to itself at once.
                var bytes = Math.Max(Median([.. last.Select(j => j.MemoryMedian!.Value)]), last[0].MemoryMedian!.Value);
                return new MemoryEstimate(bytes, last.Max(j => j.MemoryPeak ?? j.MemoryMedian), last.Count, basis);
            }
        }
        return new MemoryEstimate(Guess, null, 0, MemoryBasis.Guess);
    }

    /// <summary>The middle of the values; of an even number, the mean of the two in the middle. 0 for none.</summary>
    public static long Median(IReadOnlyList<long> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
