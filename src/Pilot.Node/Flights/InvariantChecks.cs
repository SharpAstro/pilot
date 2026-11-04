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

    private const string FilterOption = "--filter";

    /// <summary>
    /// The check with each <c>dotnet test ... --filter &lt;expr&gt;</c> segment rewritten to <c>--filter '(&lt;expr&gt;)|Category=Invariant'</c>,
    /// single-quoted so pwsh and sh read it alike. A segment is left alone when it has no filter, when its filter already names the
    /// category (so widening twice changes nothing), or when the expression holds a single quote (no quoting both shells agree on).
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
            if (value.Equals(FilterOption, StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                expression = tokens[i + 1].Value;
                index = i + 1;
            }
            else if (value.StartsWith(FilterOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                prefix = FilterOption + "=";
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
