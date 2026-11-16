namespace Pilot.Node.Flights;

/// <summary>
/// A leg's check, widened to run the repo's invariant tests too. A leg's check is usually a narrow filter
/// (<c>--filter FullyQualifiedName~Foo</c>), and the repo-wide guards (the card catalog, the file-size limit, the fixtures
/// checked against real transcripts) are tagged <c>Category=Invariant</c> so one filter runs them all in seconds. No leg filter
/// named them, so PIL700, PIL715 and PIL697 (flights) went green in the plane and red in CI after the push. The node now widens
/// each filtered <c>dotnet test</c> when the leg is planned and again when it reruns the check, so such a break fails the leg.
/// </summary>
public static class InvariantChecks
{
    /// <summary>The trait value the guard tests carry (<c>[Trait("Category", "Invariant")]</c>).</summary>
    public const string Category = "Invariant";

    /// <summary>
    /// The check with each <c>dotnet test ... --filter &lt;expr&gt;</c> segment rewritten to <c>--filter '(&lt;expr&gt;)|Category=Invariant'</c>,
    /// single-quoted so pwsh and sh read it alike. A segment is left alone when it has no filter, when its filter already names the
    /// category (so widening twice changes nothing), or when the expression holds a single quote (no quoting both shells agree on).
    /// A Microsoft.Testing.Platform segment (<c>--filter-class</c> and the like) gets <c>--filter-trait Category=Invariant</c> added only
    /// when every filter it carries is a <c>--filter-trait</c>: xunit v3 ORs the filters of one kind and ANDs the kinds, so a trait added
    /// beside a class or method filter would narrow the run instead of widening it. Any other MTP segment is left unchanged.
    /// </summary>
    public static string With(string check)
    {
        var result = check;
        var cursor = 0;
        foreach (var segment in CheckPaths.Segments(check))
        {
            var at = result.IndexOf(segment, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            var widened = Widen(segment);
            result = string.Concat(result.AsSpan(0, at), widened, result.AsSpan(at + segment.Length));
            cursor = at + widened.Length;
        }
        return result;
    }

    private static string Widen(string segment)
    {
        var tokens = CheckPaths.Tokenize(segment);
        var start = tokens.Count > 0 && tokens[0].Value == "&" ? 1 : 0;
        if (tokens.Count < start + 2
            || !Path.GetFileNameWithoutExtension(tokens[start].Value).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || !tokens[start + 1].Value.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            return segment;
        }

        var filters = TestFilters.Read(tokens.Skip(start + 2).Select(t => t.Value));
        if (filters.Any(f => TestFilters.IsPlatform(f.Option)))
        {
            var traitOnly = filters.All(f => f.Option == TestFilters.Trait);
            var named = filters.Any(f => f.Value.Equals("Category=" + Category, StringComparison.OrdinalIgnoreCase));
            return traitOnly && !named ? TestFilters.Append(segment, TestFilters.Trait + " Category=" + Category) : segment;
        }

        for (var i = start + 2; i < tokens.Count; i++)
        {
            var value = tokens[i].Value;
            if (value == "--")
            {
                break;
            }
            string? expression = null;
            var prefix = "";
            var index = i;
            if (TestFilters.IsVsTest(value) && !value.Contains('=') && i + 1 < tokens.Count)
            {
                expression = tokens[i + 1].Value;
                index = i + 1;
            }
            else if (TestFilters.IsVsTest(value) && value.Contains('='))
            {
                prefix = TestFilters.VsTest + "=";
                expression = value[prefix.Length..];
            }
            if (expression is null)
            {
                continue;
            }
            if (expression.Contains("Category=" + Category, StringComparison.OrdinalIgnoreCase) || expression.Contains('\'') || expression.Length == 0)
            {
                return segment;
            }

            // The token's raw text, found again in the segment in token order, is what gets swapped.
            var cursor = 0;
            for (var t = 0; t < index; t++)
            {
                var found = segment.IndexOf(tokens[t].Raw, cursor, StringComparison.Ordinal);
                if (found < 0)
                {
                    return segment;
                }
                cursor = found + tokens[t].Raw.Length;
            }
            var raw = tokens[index].Raw;
            var where = segment.IndexOf(raw, cursor, StringComparison.Ordinal);
            if (where < 0)
            {
                return segment;
            }
            var replacement = $"{prefix}'({expression})|Category={Category}'";
            return string.Concat(segment.AsSpan(0, where), replacement, segment.AsSpan(where + raw.Length));
        }
        return segment;
    }
}
