namespace Pilot.Node.Flights;

/// <summary>
/// A leg's check fits its change: it filters a <c>dotnet test</c> to the test classes the change touches, and leaves the whole
/// suite to CI on the PR. A check that ran every test of a project made each leg (and each go-around) pay for the whole
/// suite, and one unrelated failure in it failed a leg that had broken nothing. <c>leg plan</c> refuses such a check and
/// suggests the filter from the files the flight changed.
/// </summary>
public static class CheckScope
{
    /// <summary>The key of the plan-leg argument that says why a leg's check runs the whole suite.</summary>
    public const string WholeSuiteKey = "whole-suite";

    /// <summary>What a leg planned with <see cref="WholeSuiteKey"/> says in its done-when; a run by its sessions may then take the whole suite.</summary>
    public const string WholeSuiteMark = "The check runs the whole suite on purpose: ";

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules" };

    /// <summary>
    /// The first <c>dotnet test</c> segment of a check that has no <c>--filter</c> (so runs every test of what it names), or null
    /// when every one is filtered or the check runs none.
    /// </summary>
    public static string? WholeSuite(string check)
    {
        foreach (var segment in CheckPaths.Segments(check))
        {
            var tokens = CheckPaths.Tokenize(segment);
            var start = tokens.Count > 0 && tokens[0].Value == "&" ? 1 : 0;
            if (tokens.Count < start + 2
                || !Path.GetFileNameWithoutExtension(tokens[start].Value).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || !tokens[start + 1].Value.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var filtered = false;
            var help = false;
            foreach (var token in tokens.Skip(start + 2).TakeWhile(t => t.Value != "--"))
            {
                filtered |= token.Value.Equals("--filter", StringComparison.OrdinalIgnoreCase)
                    || token.Value.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase);
                help |= token.Value is "-h" or "--help" or "-?";
            }
            if (!filtered && !help)
            {
                return segment.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// A filter over the test classes of the changed files (repo-relative paths): a changed <c>Foo.cs</c> gives <c>FooTests</c>
    /// when that test class exists in the plane, a changed test file is its own class (<c>FooTests.cs</c> gives
    /// <c>FooTests</c>), and a partial's <c>Foo.Bar.cs</c> also tries <c>FooTests</c>. Null when no class is found.
    /// </summary>
    public static string? Suggest(IEnumerable<string> changed, string plane)
    {
        var tests = TestFiles(plane);
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in changed)
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(file.Trim());
            foreach (var name in new[] { stem, stem.Split('.')[0] }.Distinct())
            {
                if (name.EndsWith("Tests", StringComparison.Ordinal) && tests.Contains(name))
                {
                    classes.Add(name);
                }
                else if (tests.Contains(name + "Tests"))
                {
                    classes.Add(name + "Tests");
                }
            }
        }
        return classes.Count == 0 ? null : $"'{string.Join('|', classes.Select(c => "FullyQualifiedName~" + c))}'";
    }

    /// <summary>The refusal for a check that runs a whole suite: what it is, the suggested filter and where the suite runs.</summary>
    public static string Refusal(string segment, string? suggestion)
    {
        var how = suggestion is null
            ? "name the test classes the leg touches, as in --filter 'FullyQualifiedName~FooTests'"
            : $"filter it to the classes the flight touches: add --filter {suggestion}";
        return $"the check `{segment}` runs a whole test project or solution with no --filter, and the whole suite is CI's, on the PR: " +
            $"a leg's check runs the tests of what it changes. {how[..1].ToUpperInvariant()}{how[1..]}. " +
            $"If this leg really needs the whole suite, say why with {WholeSuiteKey}=<why> (--whole-suite \"<why>\")";
    }

    private static HashSet<string> TestFiles(string plane)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(plane))
        {
            return found;
        }
        var queue = new Queue<string>();
        queue.Enqueue(plane);
        while (queue.Count > 0)
        {
            var folder = queue.Dequeue();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    var name = Path.GetFileName(entry);
                    if (Directory.Exists(entry))
                    {
                        if (!Skipped.Contains(name) && !name.StartsWith('.'))
                        {
                            queue.Enqueue(entry);
                        }
                    }
                    else if (name.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(name[..^3]);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }
}
