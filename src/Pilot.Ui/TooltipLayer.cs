using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The window's one tooltip, painted last, over everything: DIR.Lib's router says which hover text is due and where it is
/// anchored, and <see cref="Layout.Builder.Tooltip(TooltipRequest, Layout.TooltipStyle)"/> is its one painter. Without it
/// every <c>WithTooltip</c> in the window was dead: a card's whole title, the rail's page names, the view switch's words
/// (SE1, 2026-10-25). It registers no region, so painting it changes nothing the router resolves.
/// </summary>
internal sealed class TooltipLayer<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private static readonly Layout.TooltipStyle Style = new(
        Background: new RGBAColor32(0x24, 0x26, 0x31, 0xf4),
        Text: new RGBAColor32(0xe6, 0xe7, 0xee, 0xff),
        Border: new RGBAColor32(0x3a, 0x3d, 0x4c, 0xff),
        FontSize: 12f);

    /// <summary>How wide a tooltip may grow, in design units, before its text wraps.</summary>
    internal const float MaxWidth = 420f;

    // Past this a text is wrapped at MaxWidth rather than laid out as one line.
    private const int OneLine = 60;

    /// <summary>Paints <paramref name="request"/> in <paramref name="window"/>, or nothing when none is due.</summary>
    public void Render(TooltipRequest? request, RectF32 window)
    {
        BeginFrame();
        if (request is { } due)
        {
            RenderLayout(Tree(due), window);
        }
    }

    /// <summary>
    /// The tooltip as a tree: a short text as one line, a long one broken into lines of at most <see cref="LineChars"/>
    /// characters, about <see cref="MaxWidth"/> wide. Laid out as one line, a card's whole detail was a box as wide as the
    /// window, its end cut off by the edge; wrapped by the engine, the box took its height before the words wrapped and
    /// the last lines hung out under it (SE1, 2026-10-25). Lines broken here are each one text leaf, so the box is as tall
    /// as they are.
    /// </summary>
    internal static Layout.Node Tree(TooltipRequest due)
    {
        var content = due.Text.Length > OneLine || due.Text.Contains('\n')
            ? Layout.Builder.VStack([.. Lines(due.Text).Select(l => Layout.Builder.Text(l, Style.FontSize, Style.Text))]).WithGap(2f)
            : Layout.Builder.Text(due.Text, Style.FontSize, Style.Text);
        return Layout.Builder.Tooltip(due.Anchor, content, Style, due.Side);
    }

    /// <summary>How many characters a broken line holds: at the tooltip's 12 units, well inside <see cref="MaxWidth"/>.</summary>
    internal const int LineChars = 58;

    // Past this a word is broken, after a path's separators where it has them: a word longer than a line would stand out
    // of the box, and a draft's path did.
    private const int LongWord = 40;

    /// <summary>The text's lines: each of its own lines broken at words to <see cref="LineChars"/>, a long word in pieces.</summary>
    internal static IEnumerable<string> Lines(string text)
    {
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0))
        {
            var line = new System.Text.StringBuilder();
            foreach (var piece in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).SelectMany(w => Chunks(w).Select((c, i) => (c, Joined: i > 0))))
            {
                var gap = line.Length == 0 || piece.Joined ? "" : " ";
                if (line.Length > 0 && line.Length + gap.Length + piece.c.Length > LineChars)
                {
                    yield return line.ToString();
                    line.Clear();
                    gap = "";
                }
                line.Append(gap).Append(piece.c);
            }
            if (line.Length > 0)
            {
                yield return line.ToString();
            }
        }
    }

    // A word in pieces no longer than LongWord, each cut after a / or \ where one falls in reach, else at the length.
    private static IEnumerable<string> Chunks(string word)
    {
        var at = 0;
        while (word.Length - at > LongWord)
        {
            var cut = word.LastIndexOfAny(['/', '\\'], at + LongWord - 1, LongWord - 1);
            var end = cut > at ? cut + 1 : at + LongWord;
            yield return word[at..end];
            at = end;
        }
        yield return word[at..];
    }

    public override bool HandleInput(InputEvent evt) => false;
}
