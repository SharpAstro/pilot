using DIR.Lib;

namespace Pilot.Ui;

/// <summary>Text that wraps at the width it is given, for the views whose sentences must follow the window and the terminal alike.</summary>
internal static class WrappedProse
{
    /// <summary>
    /// A sentence that wraps at the width it is given, each word a text leaf ending in its space as Markdown's paragraphs
    /// are: a text leaf is one line, and wrapping the words is what lets it follow the window and the terminal alike.
    /// <paramref name="maxLines"/> cuts it after that many lines; 0 is no cap. <paramref name="link"/> may answer a word (with its
    /// punctuation) with a node of its own, drawn in the word's place: a link in the middle of the sentence. <paramref name="selectable"/>
    /// marks each word's run selectable, for the page whose host offers a selection (a word is a region; the copy joins them as the page reads).
    /// </summary>
    public static Layout.Node Of(string text, float size, RGBAColor32 color, int maxLines = 0, Func<string, Layout.Node?>? link = null, bool selectable = false, int lineChars = 0)
    {
        var words = (maxLines > 0 && lineChars > 0 ? Bound(text, maxLines * lineChars) : text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(word => link?.Invoke(word) is { } linked ? [linked] : Pieces(word).Select(piece => Layout.Builder.Text(piece, size, color).WAuto().Selectable(selectable)))
            .ToArray();
        var wrap = Layout.Builder.WrapH(words).WithLineGap(size * 0.2f);
        return (maxLines > 0 ? wrap.WithMaxLines(maxLines) : wrap).WStar();
    }

    /// <summary>
    /// <paramref name="text"/> cut after the last whole word within <paramref name="chars"/> characters: the words past what
    /// <paramref name="chars"/> could fill the capped lines with are never made into leaves, so each frame fits a bounded few
    /// however long the text (the whole text is on hover where the caller keeps it). One word longer than the budget is kept whole.
    /// </summary>
    internal static string Bound(string text, int chars)
    {
        if (text.Length <= chars)
        {
            return text;
        }
        var cut = text.LastIndexOfAny([' ', '\t', '\n'], chars);
        return cut > 0 ? text[..cut] : text[..chars];
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
        var pieces = Break(word).ToArray();
        for (var i = 0; i < pieces.Length; i++)
        {
            yield return i == pieces.Length - 1 ? pieces[i] + " " : pieces[i];
        }
    }

    /// <summary>
    /// <paramref name="word"/> cut after each of its separators (<c>, + / \ . : _ -</c>), a separator that ends it left with it;
    /// the pieces joined are the word, so the copy of a run of them has no space the text did not.
    /// </summary>
    internal static IEnumerable<string> Break(string word)
    {
        var start = 0;
        for (var i = 0; i + 1 < word.Length; i++)
        {
            if (word[i] is ',' or '+' or '/' or '\\' or '.' or ':' or '_' or '-')
            {
                yield return word[start..(i + 1)];
                start = i + 1;
            }
        }
        yield return word[start..];
    }

    /// <summary>
    /// Lines of text, each its own wrapped paragraph (an explicit newline stays a break), together at most
    /// <paramref name="maxLines"/> lines tall: the first paragraph gets what the others leave of the cap, the others one each.
    /// </summary>
    public static Layout.Node Paragraphs(string text, float size, RGBAColor32 color, int maxLines, int lineChars = 0)
    {
        var lines = (lineChars > 0 ? Bound(text, maxLines * lineChars * 2) : text).Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(maxLines).ToArray();
        var rows = lines.Select((l, i) => Of(l, size, color, i == 0 ? maxLines - (lines.Length - 1) : 1, lineChars: lineChars)).ToArray();
        // Gap of 2 design units between paragraphs, a literal the way the views' other fixed gaps are.
        return Layout.Builder.VStack(rows).WithGap(2f).WStar().HAuto();
    }
}
