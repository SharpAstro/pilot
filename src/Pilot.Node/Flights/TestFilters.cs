namespace Pilot.Node.Flights;

/// <summary>
/// What a <c>dotnet test</c> filter is, said once: VSTest's <c>--filter</c> and the Microsoft.Testing.Platform options
/// (<c>--filter-class</c>, <c>--filter-not-class</c>, <c>--filter-method</c>, <c>--filter-not-method</c>, <c>--filter-namespace</c>,
/// <c>--filter-not-namespace</c>, <c>--filter-trait</c>, <c>--filter-not-trait</c>, <c>--filter-query</c>), each as <c>--opt value</c> or
/// <c>--opt=value</c>, repeatable. The whole-suite guard, the command key and every list of options that take a value ask here, so
/// none of them knows only <c>--filter</c> (pilot #1086).
/// </summary>
public static class TestFilters
{
    /// <summary>VSTest's filter option: one expression as its value.</summary>
    public const string VsTest = "--filter";

    /// <summary>The Microsoft.Testing.Platform filter options; each takes one or more values.</summary>
    public static readonly IReadOnlyList<string> Platform =
    [
        "--filter-class", "--filter-not-class", "--filter-method", "--filter-not-method", "--filter-namespace", "--filter-not-namespace",
        "--filter-trait", "--filter-not-trait", "--filter-query",
    ];

    /// <summary>The Platform options the node reads or writes by name.</summary>
    public const string Class = "--filter-class";
    public const string NotClass = "--filter-not-class";
    public const string Method = "--filter-method";
    public const string Namespace = "--filter-namespace";
    public const string Trait = "--filter-trait";
    public const string Query = "--filter-query";

    private static readonly HashSet<string> Names = new([VsTest, .. Platform], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="word"/> is a filter option, as <c>--opt</c> or <c>--opt=value</c>, in any case.</summary>
    public static bool IsFilterOption(string word) => Names.Contains(NameOf(word));

    /// <summary>Whether <paramref name="word"/> is VSTest's <c>--filter</c>, as <c>--filter</c> or <c>--filter=value</c>.</summary>
    public static bool IsVsTest(string word) => NameOf(word).Equals(VsTest, StringComparison.OrdinalIgnoreCase);

    /// <summary>The option name of a word: what is before an <c>=</c>.</summary>
    public static string NameOf(string word)
    {
        var cut = word.IndexOf('=');
        return cut > 0 ? word[..cut] : word;
    }

    /// <summary>
    /// How many words after the one at <paramref name="at"/> that word takes as its value: 0 for a word that is no filter option or
    /// carries its value (<c>--opt=value</c>); 1 for <c>--filter</c> (when a word follows); for a Platform option every following word
    /// that is no option, up to a bare <c>--</c>.
    /// </summary>
    public static int ValueCount(IReadOnlyList<string> words, int at)
    {
        var word = words[at];
        if (!IsFilterOption(word) || word.Contains('='))
        {
            return 0;
        }
        if (IsVsTest(word))
        {
            return at + 1 < words.Count ? 1 : 0;
        }
        var count = 0;
        while (at + 1 + count < words.Count && !words[at + 1 + count].StartsWith('-'))
        {
            count++;
        }
        return count;
    }

    /// <summary>Whether <paramref name="words"/> carry any filter option, up to a bare <c>--</c>.</summary>
    public static bool Any(IEnumerable<string> words) => words.TakeWhile(w => w != "--").Any(IsFilterOption);

    /// <summary>
    /// Every filter the words carry as (option, value) pairs, the option in lower case, in the order given; all values of a
    /// multi-valued Platform option; stopping at a bare <c>--</c>. An option with no value gives an empty value.
    /// </summary>
    public static List<(string Option, string Value)> Read(IEnumerable<string> tokens)
    {
        var words = tokens.ToList();
        var found = new List<(string, string)>();
        for (var i = 0; i < words.Count && words[i] != "--"; i++)
        {
            var word = words[i];
            if (!IsFilterOption(word))
            {
                continue;
            }
            var option = NameOf(word).ToLowerInvariant();
            var cut = word.IndexOf('=');
            if (cut > 0)
            {
                found.Add((option, word[(cut + 1)..]));
                continue;
            }
            var take = ValueCount(words, i);
            if (take == 0)
            {
                found.Add((option, ""));
            }
            for (var k = 1; k <= take; k++)
            {
                found.Add((option, words[i + k]));
            }
            i += take;
        }
        return found;
    }

    /// <summary>Whether <paramref name="option"/> is a Platform option, not VSTest's.</summary>
    public static bool IsPlatform(string option) => !option.Equals(VsTest, StringComparison.OrdinalIgnoreCase) && Names.Contains(option);

    /// <summary>
    /// <paramref name="segment"/> with <paramref name="args"/> added after its last argument, before a bare <c>--</c> when it has one.
    /// </summary>
    public static string Append(string segment, string args)
    {
        var tokens = CheckPaths.Tokenize(segment);
        var cursor = 0;
        var end = 0;
        foreach (var token in tokens)
        {
            var found = segment.IndexOf(token.Raw, cursor, StringComparison.Ordinal);
            if (found < 0)
            {
                return segment;
            }
            if (token.Value == "--")
            {
                break;
            }
            cursor = found + token.Raw.Length;
            end = cursor;
        }
        return string.Concat(segment.AsSpan(0, end), " ", args, segment.AsSpan(end));
    }

    /// <summary>
    /// <paramref name="segment"/> with every Platform filter option and its values taken out (VSTest's <c>--filter</c> too when
    /// <paramref name="includeVsTest"/>), stopping at a bare <c>--</c>.
    /// </summary>
    public static string Remove(string segment, bool includeVsTest)
    {
        var tokens = CheckPaths.Tokenize(segment);
        var words = tokens.Select(t => t.Value).ToList();
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
        var ranges = new List<(int From, int To)>();
        for (var i = 0; i < words.Count && words[i] != "--"; i++)
        {
            if (!IsFilterOption(words[i]) || (IsVsTest(words[i]) && !includeVsTest))
            {
                continue;
            }
            var last = i + ValueCount(words, i);
            ranges.Add((where[i], where[last] + tokens[last].Raw.Length));
            i = last;
        }
        var result = segment;
        for (var r = ranges.Count - 1; r >= 0; r--)
        {
            var from = ranges[r].From;
            while (from > 0 && result[from - 1] == ' ')
            {
                from--;
            }
            result = string.Concat(result.AsSpan(0, from), result.AsSpan(ranges[r].To));
        }
        return result;
    }

    /// <summary>The filters of <paramref name="tokens"/> sorted by option then value (ordinal), so argument order never changes a key.</summary>
    public static List<(string Option, string Value)> Sorted(IEnumerable<string> tokens) =>
        [.. Read(tokens).OrderBy(f => f.Option, StringComparer.Ordinal).ThenBy(f => f.Value, StringComparer.Ordinal)];

    /// <summary>The filters as one stable text (<c>--filter-class=A;--filter-class=B</c>); empty for none.</summary>
    public static string Key(IEnumerable<string> tokens) => string.Join(';', Sorted(tokens).Select(f => $"{f.Option}={f.Value}"));
}
