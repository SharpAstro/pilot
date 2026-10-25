using DIR.Lib;

namespace Pilot.Ui;

/// <summary>Text that wraps at the width it is given, for the views whose sentences must follow the window and the terminal alike.</summary>
internal static class WrappedProse
{
    /// <summary>
    /// A sentence that wraps at the width it is given, each word a text leaf ending in its space as Markdown's paragraphs
    /// are: a text leaf is one line, and wrapping the words is what lets it follow the window and the terminal alike.
    /// <paramref name="maxLines"/> cuts it after that many lines; 0 is no cap.
    /// </summary>
    public static Layout.Node Of(string text, float size, RGBAColor32 color, int maxLines = 0)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(Pieces)
            .Select(piece => Layout.Builder.Text(piece, size, color).WAuto())
            .ToArray();
        var wrap = Layout.Builder.WrapH(words).WithLineGap(size * 0.2f);
        return (maxLines > 0 ? wrap.WithMaxLines(maxLines) : wrap).WStar();
    }

    /// <summary>A word this long may not fit a line of its own: a check's name, a path, a URL.</summary>
    internal const int LongWord = 24;

    /// <summary>
    /// A word as the leaves that draw it, each ending in its space as Markdown's paragraphs are, except that a long one is
    /// broken after its separators (<c>, + / \ . : _ -</c>), the leaves of a piece running on without a space, so a long check
    /// name or path wraps instead of running past the box (ui-changes.md section 3).
    /// </summary>
    private static IEnumerable<string> Pieces(string word)
    {
        if (word.Length < LongWord)
        {
            yield return word + " ";
            yield break;
        }
        var start = 0;
        for (var i = 0; i < word.Length; i++)
        {
            if (word[i] is ',' or '+' or '/' or '\\' or '.' or ':' or '_' or '-' && i + 1 < word.Length)
            {
                yield return word[start..(i + 1)];
                start = i + 1;
            }
        }
        yield return word[start..] + " ";
    }

    /// <summary>
    /// Lines of text, each its own wrapped paragraph (an explicit newline stays a break), together at most
    /// <paramref name="maxLines"/> lines tall: the first paragraph gets what the others leave of the cap, the others one each.
    /// </summary>
    public static Layout.Node Paragraphs(string text, float size, RGBAColor32 color, int maxLines)
    {
        var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(maxLines).ToArray();
        var rows = lines.Select((l, i) => Of(l, size, color, i == 0 ? maxLines - (lines.Length - 1) : 1)).ToArray();
        // Gap of 2 design units between paragraphs, a literal the way the views' other fixed gaps are.
        return Layout.Builder.VStack(rows).WithGap(2f).WStar().HAuto();
    }
}
