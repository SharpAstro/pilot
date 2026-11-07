using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A failing test the flight's change does not touch does not fail its leg (#752). When the node reruns a leg's check at finish and
/// it fails only on tests whose classes the flight's diff and the check's filter do not name, each is matched to an open issue or
/// gets one filed, and the leg is done with them listed as not this flight's. A class is the flight's when its test file is changed,
/// when it is the test class of a changed source by <see cref="CheckScope"/>'s rule (<c>Foo.cs</c> and <c>Foo.Bar.cs</c> give
/// <c>FooTests</c>), when a name in the check's filter matches it, or when it is an invariant guard of the plane (a class in a file
/// carrying <c>Category=Invariant</c>, which a change breaks without touching it). A failure no open issue names is passed only once
/// it is shown to fail on the base too. A build error, a timeout, or a failed run with no failing test read from its output is not
/// this: it fails as before.
/// </summary>
public static partial class UnrelatedFailures
{
    /// <summary>The words the leg's report lists them under: <c>not this flight's: Class.Method (#n), ...</c>.</summary>
    public const string ReportHead = "not this flight's: ";

    /// <summary>How many times the node reruns a check with the unrelated classes left out before it fails the leg as before.</summary>
    public const int MostReruns = 3;

    /// <summary>
    /// The failed tests the run's output names, as <c>Class.Method</c>, by PIL731's reader (<see cref="TestFailures"/>); null when the
    /// output holds a build error or names no failed test, since then nothing is known to be someone else's.
    /// </summary>
    public static IReadOnlyList<string>? FailedTests(string output)
    {
        var reader = new TestFailures();
        var tests = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            if (reader.Read(line.TrimEnd('\r')) is not { } failure)
            {
                continue;
            }
            if (failure.IsBuildError)
            {
                return null;
            }
            tests.Add(failure.Name);
        }
        return tests.Count == 0 ? null : tests;
    }

    /// <summary>The class of a <c>Class.Method</c> test name.</summary>
    public static string ClassOf(string test) => test.LastIndexOf('.') is > 0 and var dot ? test[..dot] : test;

    /// <summary>The test classes the flight's changed files (repo-relative paths) make its own.</summary>
    public static HashSet<string> ClassesOf(IEnumerable<string> changed)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in changed)
        {
            if (!file.Trim().EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(file.Trim());
            foreach (var name in new[] { stem, stem.Split('.')[0] })
            {
                classes.Add(name.EndsWith("Tests", StringComparison.Ordinal) ? name : name + "Tests");
            }
        }
        return classes;
    }

    /// <summary>The names a check's filters select by (<c>FullyQualifiedName~X</c>, <c>Name=X</c>, <c>ClassName~X</c>); an excluding <c>!~</c> names none.</summary>
    public static IReadOnlyList<string> FilterNames(string check)
    {
        var names = new List<string>();
        foreach (var expression in Filters(check))
        {
            foreach (Match m in FilterTerm().Matches(expression))
            {
                if (!m.Groups["op"].Value.StartsWith('!') && m.Groups["value"].Value.Trim() is { Length: > 0 } value)
                {
                    names.Add(value);
                }
            }
        }
        return names;
    }

    /// <summary>Whether a failed test (<c>Class.Method</c>) is the flight's: its class is one the change makes its own, or the filter names it.</summary>
    public static bool IsFlights(string test, ISet<string> classes, IReadOnlyList<string> filterNames)
    {
        var cls = ClassOf(test);
        var last = cls.LastIndexOf('+') is >= 0 and var plus ? cls[(plus + 1)..] : cls;
        return classes.Contains(cls) || classes.Contains(last)
            || filterNames.Any(n => test.Contains(n, StringComparison.Ordinal) || n.EndsWith("." + cls, StringComparison.Ordinal) || n == cls);
    }

    /// <summary>
    /// The test classes declared in a source file of <paramref name="root"/> that carries the invariant trait
    /// (<c>[Trait("Category", "Invariant")]</c>, or the category given by a constant such as <c>InvariantChecks.Category</c>). A guard
    /// like that breaks when a change forgets the thing it guards (a card kind left out of the catalog) without touching the guard's
    /// class, so its failure is always the flight's. <c>bin</c>, <c>obj</c> and dot folders are skipped; an unreadable file is skipped.
    /// </summary>
    public static HashSet<string> InvariantClasses(string root)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return classes;
        }
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(sub);
                    if (!name.StartsWith('.') && name is not ("bin" or "obj" or "node_modules"))
                    {
                        pending.Push(sub);
                    }
                }
                foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
                {
                    var text = File.ReadAllText(file);
                    if (InvariantTrait().IsMatch(text))
                    {
                        foreach (Match m in ClassName().Matches(text))
                        {
                            classes.Add(m.Groups["name"].Value);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return classes;
    }

    /// <summary>Whether a failed test (<c>Class.Method</c>, the class possibly nested as <c>Outer+Inner</c>) is in one of <paramref name="invariant"/>.</summary>
    public static bool IsInvariant(string test, ISet<string> invariant)
    {
        var cls = ClassOf(test);
        return invariant.Contains(cls) || cls.Split('+', '.').Any(invariant.Contains);
    }

    /// <summary>
    /// The check with each <c>dotnet test</c> segment told to run only <paramref name="test"/> (<c>Class.Method</c>): its filter becomes
    /// <c>'FullyQualifiedName~Class.Method'</c>. The run on the base that shows a failure is not the flight's. Null when no segment could
    /// be rewritten.
    /// </summary>
    public static string? Only(string check, string test) =>
        test.Contains('\'') ? null : Refilter(check, _ => $"FullyQualifiedName~{test}");

    /// <summary>
    /// The check with each <c>dotnet test</c> segment told to leave out <paramref name="classes"/>: its filter becomes
    /// <c>'(&lt;expr&gt;)&amp;FullyQualifiedName!~A&amp;...'</c>, or a segment with none gets <c>--filter 'FullyQualifiedName!~A&amp;...'</c>.
    /// Null when no segment could be rewritten (a filter holding a single quote, or no <c>dotnet test</c> at all).
    /// </summary>
    public static string? Excluding(string check, IReadOnlyCollection<string> classes)
    {
        if (classes.Count == 0)
        {
            return check;
        }
        var without = string.Join('&', classes.Order(StringComparer.Ordinal).Select(c => "FullyQualifiedName!~" + c));
        return Refilter(check, expression => expression is null ? without : $"({expression})&{without}");
    }

    // Each dotnet test segment's filter replaced by what `filter` makes of it (null: the segment had none); null when none changed.
    private static string? Refilter(string check, Func<string?, string> filter)
    {
        var result = check;
        var cursor = 0;
        var changed = false;
        foreach (var segment in CheckPaths.Segments(check))
        {
            var at = result.IndexOf(segment, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            var rewritten = Rewrite(segment, filter);
            changed |= rewritten != segment;
            result = string.Concat(result.AsSpan(0, at), rewritten, result.AsSpan(at + segment.Length));
            cursor = at + rewritten.Length;
        }
        return changed ? result : null;
    }

    /// <summary>The open issue that names the test (by <c>Class.Method</c>, else by its class), or null.</summary>
    public static IssueInfo? Matching(IEnumerable<IssueInfo> issues, string test)
    {
        var list = issues as IReadOnlyCollection<IssueInfo> ?? [.. issues];
        var method = test.LastIndexOf('.') is > 0 and var dot ? test[(dot + 1)..] : test;
        return list.FirstOrDefault(i => i.Title.Contains(test, StringComparison.Ordinal))
            ?? list.FirstOrDefault(i => Word(i.Title, ClassOf(test)) && Word(i.Title, method))
            ?? list.FirstOrDefault(i => Word(i.Title, ClassOf(test)));
    }

    /// <summary>The title of the issue filed for a test that failed outside a flight's change.</summary>
    public static string IssueTitle(string test, string flight) => $"{test} fails outside the flight's change ({flight})";

    /// <summary>The body of that issue: the test, the leg, the job and the last of its output.</summary>
    public static string IssueBody(string test, string flight, string leg, string? job, string output) =>
        $"""
        `{test}` failed in the check of {flight}/{leg}, and the flight's change does not touch its class (its test file, the class of a changed source, or a name in the check's filter), so the node passed the leg and filed this.

        - Test: `{test}`
        - Leg: {flight}/{leg}
        - Job: {job ?? "none recorded"}

        Last of its output:

        ```
        {output.Trim()}
        ```
        """;

    /// <summary>The line of a leg's report that lists them, each with its issue.</summary>
    public static string ReportLine(IEnumerable<(string Test, int Issue)> found) =>
        ReportHead + string.Join(", ", found.Select(f => $"{f.Test} (#{f.Issue})"));

    private static bool Word(string text, string word) =>
        word.Length > 0 && Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape(word)}(?![\w])");

    private static IEnumerable<string> Filters(string check)
    {
        foreach (var segment in CheckPaths.Segments(check))
        {
            var tokens = CheckPaths.Tokenize(segment);
            var start = DotnetTest(tokens);
            if (start < 0)
            {
                continue;
            }
            for (var i = start + 2; i < tokens.Count && tokens[i].Value != "--"; i++)
            {
                if (tokens[i].Value.Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
                {
                    yield return tokens[i + 1].Value;
                }
                else if (tokens[i].Value.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
                {
                    yield return tokens[i].Value["--filter=".Length..];
                }
            }
        }
    }

    // The index of a segment's "dotnet" when it is a dotnet test, else -1.
    private static int DotnetTest(List<CheckPaths.Token> tokens)
    {
        var start = tokens.Count > 0 && tokens[0].Value == "&" ? 1 : 0;
        return tokens.Count >= start + 2
            && Path.GetFileNameWithoutExtension(tokens[start].Value).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && tokens[start + 1].Value.Equals("test", StringComparison.OrdinalIgnoreCase)
            ? start
            : -1;
    }

    private static string Rewrite(string segment, Func<string?, string> filter)
    {
        var tokens = CheckPaths.Tokenize(segment);
        var start = DotnetTest(tokens);
        if (start < 0)
        {
            return segment;
        }
        // Each token's raw text, found again in the segment in order, gives where it sits.
        var where = new int[tokens.Count];
        var cursor = 0;
        for (var t = 0; t < tokens.Count; t++)
        {
            var found = segment.IndexOf(tokens[t].Raw, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                return segment;
            }
            where[t] = found;
            cursor = found + tokens[t].Raw.Length;
        }
        for (var i = start + 2; i < tokens.Count && tokens[i].Value != "--"; i++)
        {
            string? expression = null;
            var prefix = "";
            var index = i;
            if (tokens[i].Value.Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                expression = tokens[i + 1].Value;
                index = i + 1;
            }
            else if (tokens[i].Value.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "--filter=";
                expression = tokens[i].Value[prefix.Length..];
            }
            if (expression is null)
            {
                continue;
            }
            if (expression.Contains('\'') || expression.Length == 0)
            {
                return segment;
            }
            var replacement = $"{prefix}'{filter(expression)}'";
            return string.Concat(segment.AsSpan(0, where[index]), replacement, segment.AsSpan(where[index] + tokens[index].Raw.Length));
        }
        // No filter: one goes in right after "dotnet test".
        var after = where[start + 1] + tokens[start + 1].Raw.Length;
        return string.Concat(segment.AsSpan(0, after), $" --filter '{filter(null)}'", segment.AsSpan(after));
    }

    [GeneratedRegex(@"(?<key>FullyQualifiedName|ClassName|DisplayName|Name)\s*(?<op>!?[~=])\s*(?<value>[^|&()'""]+)")]
    private static partial Regex FilterTerm();

    // An attribute line (not a doc comment that quotes one) carrying the trait.
    [GeneratedRegex(@"(?m)^[ \t]*\[[^\n]*?Trait\(\s*""Category""\s*,\s*(?:""Invariant""|[\w.]*Invariant[\w.]*)\s*\)")]
    private static partial Regex InvariantTrait();

    [GeneratedRegex(@"\bclass\s+(?<name>\w+)")]
    private static partial Regex ClassName();
}
