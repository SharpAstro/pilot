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
    public static Layout.Node Of(string text, float size, RGBAColor32 color, int maxLines = 0, Func<string, Layout.Node?>? link = null, bool selectable = false)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(word => link?.Invoke(word) is { } linked ? [linked] : Pieces(word).Select(piece => Layout.Builder.Text(piece, size, color).WAuto().Selectable(selectable)))
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
        var pieces = Break(word).ToArray();
        for (var i = 0; i < pieces.Length; i++)
        {
            yield return i == pieces.Length - 1 ? pieces[i] + " " : pieces[i];
        }
    }

    /// <summary>
    /// <paramref name="word"/> cut after each of its separators (<c>, + / \ . : _ -</c>), a separator that ends it left with it;
    /// the pieces joined are the word, so the copy of a run of them has no space the text did not. A piece still longer than
    /// <see cref="LongWord"/> (a type's name, a long identifier) is cut again at its humps (<c>fooBar</c> into <c>foo</c> and
    /// <c>Bar</c>), and one with none every <see cref="LongWord"/> characters: a piece wider than the page is a line cut by the clip.
    /// </summary>
    internal static IEnumerable<string> Break(string word)
    {
        var start = 0;
        for (var i = 0; i + 1 < word.Length; i++)
        {
            if (word[i] is ',' or '+' or '/' or '\\' or '.' or ':' or '_' or '-')
            {
                foreach (var piece in Shorter(word[start..(i + 1)]))
                {
                    yield return piece;
                }
                start = i + 1;
            }
        }
        foreach (var piece in Shorter(word[start..]))
        {
            yield return piece;
        }
    }

    // A piece cut at its humps once it has run half a LongWord, then by length.
    private static IEnumerable<string> Shorter(string piece)
    {
        if (piece.Length <= LongWord)
        {
            yield return piece;
            yield break;
        }
        var start = 0;
        for (var i = 1; i < piece.Length; i++)
        {
            var hump = char.IsUpper(piece[i]) && char.IsLower(piece[i - 1]) && i - start >= LongWord / 2;
            if (hump || i - start >= LongWord)
            {
                yield return piece[start..i];
                start = i;
            }
        }
        yield return piece[start..];
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
