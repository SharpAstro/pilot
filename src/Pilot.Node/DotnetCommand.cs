namespace Pilot.Node;

/// <summary>
/// What a <c>dotnet</c> command does, read in one place: its verb, what it builds, and whether it compiles or is heavy. The test guard
/// (<see cref="TestGuard"/>: a pilot-driven session's own shell compiles nothing) and the job queue's heavy rule (<c>JobRunner.IsHeavy</c>:
/// two heavy jobs never run at once) both read commands through it, so they cannot disagree about what a command is (they did: the guard
/// and the queue each parsed the line their own way).
/// </summary>
public static class DotnetCommand
{
    /// <summary>What a build's target is.</summary>
    public enum TargetKind
    {
        /// <summary>A solution (<c>.sln</c>, <c>.slnx</c>).</summary>
        Solution,

        /// <summary>A test project (<c>*Tests*.csproj</c>).</summary>
        TestProject,

        /// <summary>Any other project.</summary>
        Project,

        /// <summary>A folder: what it holds (a solution, as a bare <c>dotnet build</c> builds) is built.</summary>
        Folder,
    }

    /// <summary>The verbs that compile, which the job system runs, never a session's own shell.</summary>
    public static readonly IReadOnlySet<string> CompilingVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test", "build", "run", "publish", "pack", "msbuild" };

    // The switches that take the next word as their value.
    private static readonly HashSet<string> ValueSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "--configuration", "-f", "--framework", "-o", "--output", "-r", "--runtime", "-v", "--verbosity", "-a", "--arch", "--os", "-bl", "--filter", "--project", "-p",
    };

    /// <summary>The verb of <paramref name="args"/> (the words after <c>dotnet</c>): the first that is no switch, lower case; null when there is none.</summary>
    public static string? Verb(IReadOnlyList<string> args) => args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();

    /// <summary>What <paramref name="args"/> name after the verb that is no switch and no switch's value: the targets of a build.</summary>
    public static List<string> Targets(IReadOnlyList<string> args)
    {
        var targets = new List<string>();
        var verbAt = -1;
        for (var k = 0; k < args.Count; k++)
        {
            if (!args[k].StartsWith('-'))
            {
                verbAt = k;
                break;
            }
        }
        for (var k = verbAt + 1; verbAt >= 0 && k < args.Count; k++)
        {
            var w = args[k];
            if (w.StartsWith('-'))
            {
                if (!w.Contains(':') && !w.Contains('=') && ValueSwitches.Contains(w))
                {
                    k++;
                }
                continue;
            }
            targets.Add(w);
        }
        return targets;
    }

    /// <summary>What a target is, from its name.</summary>
    public static TargetKind KindOf(string target)
    {
        var file = Path.GetFileName(target.TrimEnd('/', '\\'));
        return file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? TargetKind.Solution
            : file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? file.Contains("Tests", StringComparison.OrdinalIgnoreCase) ? TargetKind.TestProject : TargetKind.Project
            : Path.HasExtension(file) ? TargetKind.Project
            : TargetKind.Folder;
    }

    /// <summary>Whether <paramref name="args"/> compile: test, build, run, publish, pack or msbuild.</summary>
    public static bool Compiles(IReadOnlyList<string> args) => Verb(args) is { } verb && CompilingVerbs.Contains(verb);

    /// <summary>
    /// Whether <paramref name="args"/> are heavy: a test run, or a build of a solution, a test project or a folder (none named builds the
    /// folder's). One other project's build is not.
    /// </summary>
    public static bool IsHeavy(IReadOnlyList<string> args) =>
        Verb(args) switch
        {
            "test" => true,
            "build" => Targets(args) is var targets && (targets.Count == 0 || targets.Any(t => KindOf(t) is not TargetKind.Project)),
            _ => false,
        };

    /// <summary>Whether any <c>dotnet</c> part of a command line (parts joined by <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> or <c>|</c>) is heavy.</summary>
    public static bool IsHeavyLine(string command)
    {
        foreach (var segment in command.Split(["&&", "||", ";", "|"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var words = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"', '\'')).ToList();
            var at = words.FindIndex(w => Path.GetFileNameWithoutExtension(w).Equals("dotnet", StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && IsHeavy(words.Skip(at + 1).ToList()))
            {
                return true;
            }
        }
        return false;
    }
}
