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

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules" };

    /// <summary>
    /// The first <c>dotnet test</c> segment of a check that has no <c>--filter</c> (so runs every test of what it names), or null
    /// when every one is filtered or the check runs none. A <c>--filter</c> made only of negated <c>Category</c> clauses
    /// (<c>Category!=Heavy</c>, <c>Category!=Slow&amp;Category!=Heavy</c>) leaves out a few tests and runs every other, so it
    /// counts as no filter (PIL821: a worker's <c>Category!=Heavy</c> run took 10,892 tests for 83 minutes).
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
            var words = tokens.Skip(start + 2).TakeWhile(t => t.Value != "--").Select(t => t.Value).ToList();
            for (var i = 0; i < words.Count; i++)
            {
                if (words[i].Equals("--filter", StringComparison.OrdinalIgnoreCase))
                {
                    // `--filter X`: X is the next word; a filter with nothing after it is still a filter.
                    filtered |= i + 1 >= words.Count || !OnlyNegatedCategories(words[i + 1]);
                }
                else if (words[i].StartsWith(FilterEquals, StringComparison.OrdinalIgnoreCase))
                {
                    filtered |= !OnlyNegatedCategories(words[i][FilterEquals.Length..]);
                }
                help |= words[i] is "-h" or "--help" or "-?";
            }
            if (!filtered && !help)
            {
                return segment.Trim();
            }
        }
        return null;
    }

    private const string FilterEquals = "--filter=";

    /// <summary>Whether a <c>--filter</c> value is only negated <c>Category</c> clauses joined by <c>&amp;</c> (at least one, parentheses allowed).</summary>
    private static bool OnlyNegatedCategories(string filter)
    {
        var clauses = filter.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return clauses.Length > 0 && clauses.All(c => IsNegatedCategory(c.Trim('(', ')', ' ')));
    }

    private static bool IsNegatedCategory(string clause)
    {
        var at = clause.IndexOf("!=", StringComparison.Ordinal);
        if (at <= 0 || !clause[..at].Trim().Equals("Category", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // A plain trait value: anything with another operator in it (A|Category!=B, Name~X) is a different filter.
        var value = clause[(at + 2)..].Trim();
        return value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or ' ');
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
    public static string Refusal(string segment, string? suggestion) =>
        $"the check `{segment}` runs a whole test project or solution with no --filter (a filter of only Category!= clauses is none), and the whole suite is CI's, on the PR: " +
        $"a leg's check runs the tests of what it changes. {Advice(suggestion)}. " +
        $"If this leg really needs the whole suite, say why with {WholeSuiteKey}=<why> (--whole-suite \"<why>\")";

    /// <summary>
    /// The refusal for a <c>pilot run</c> of a whole test suite from a flight's own session (a worker, reviewer or planner). A run
    /// has no <c>--whole-suite</c> to say why (only <c>leg plan</c> does), so the way out named is the filter alone; a run that is
    /// one of the flight's planned checks is not refused, so it needs no word here.
    /// </summary>
    public static string RunRefusal(string segment, string? suggestion) =>
        $"the run `{segment}` is a whole test suite (no --filter, or only Category!= clauses), and the whole suite is CI's, on the PR (pilot #752, standing order o2): " +
        $"a flight's own run takes the tests of what the flight touches. {Advice(suggestion)}";

    /// <summary>What to do instead of a whole suite: the suggested filter, or how to name the classes.</summary>
    private static string Advice(string? suggestion)
    {
        var how = suggestion is null
            ? "name the test classes the leg touches, as in --filter 'FullyQualifiedName~FooTests'"
            : $"filter it to the classes the flight touches: add --filter {suggestion}";
        return $"{how[..1].ToUpperInvariant()}{how[1..]}";
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
