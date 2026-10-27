using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The window's one tooltip, painted last, over everything: DIR.Lib's router says which hover text is due and where it is
/// anchored, and this layer paints it in DIR.Lib's tooltip box (<see cref="Layout.Builder.Tooltip(RectF32, Layout.Node, Layout.TooltipStyle, Layout.DockSide)"/>),
/// its text broken into lines here. Without it every <c>WithTooltip</c> in the window was dead: a card's whole title, the
/// rail's page names, the view switch's words (SE1, 2026-10-25). It registers no region, so painting it changes nothing
/// the router resolves.
/// </summary>
internal sealed class TooltipLayer<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private static readonly Layout.TooltipStyle Style = new(
        Background: new RGBAColor32(0x24, 0x26, 0x31, 0xf4),
        Text: new RGBAColor32(0xe6, 0xe7, 0xee, 0xff),
        Border: new RGBAColor32(0x3a, 0x3d, 0x4c, 0xff),
        FontSize: 12f);

    /// <summary>How wide a tooltip's text may grow, in design units, before it breaks onto another line.</summary>
    internal const float MaxWidth = 420f;

    /// <summary>Paints <paramref name="request"/> in <paramref name="window"/>, or nothing when none is due.</summary>
    public void Render(TooltipRequest? request, RectF32 window)
    {
        BeginFrame();
        if (request is { } due)
        {
            RenderLayout(Tree(due, FontPath is { Length: > 0 } font ? text => Renderer.MeasureText(text.AsSpan(), font, Style.FontSize).Width : null), window);
        }
    }

    /// <summary>
    /// The tooltip as a tree: a text that fits <see cref="MaxWidth"/> as one line, a longer one broken at words into lines
    /// that each fit, as <paramref name="width"/> measures them in design units (by characters when it is null, as a test
    /// without a font has it). Laid out as one line, a card's whole detail was a box as wide as the window, its end cut off
    /// by the edge; wrapped by the engine, the box took its height before the words wrapped and the last lines hung out
    /// under it (SE1, 2026-10-25). Lines broken here are each one text leaf, so the box is as tall as they are; a blank line
    /// stays a gap between paragraphs.
    /// </summary>
    internal static Layout.Node Tree(TooltipRequest due, Func<string, float>? width = null)
    {
        width ??= ByChars;
        var content = due.Text.Contains('\n') || width(due.Text) > MaxWidth
            ? Layout.Builder.VStack([.. Lines(due.Text, width).Select(l => l.Length == 0
                    ? Layout.Builder.Spacer().HFixed(ParagraphGap)
                    : Layout.Builder.Text(l, Style.FontSize, Style.Text))])
                .WithGap(2f)
            : Layout.Builder.Text(due.Text, Style.FontSize, Style.Text);
        return Layout.Builder.Tooltip(due.Anchor, content, Style, due.Side);
    }

    // The height of a blank line between paragraphs: half a line at the tooltip's 12 units.
    private const float ParagraphGap = 6f;

    // A line's width without a font: an average glyph's at the tooltip's size, so 58 characters fill a line.
    private static float ByChars(string text) => text.Length * (MaxWidth / 58f);

    // Past this a word is cut before it is measured, after a path's separators where it has them: a word longer than a
    // line would stand out of the box, and a draft's path did.
    private const int LongWord = 40;

    /// <summary>
    /// The text's lines: each of its own lines broken at words so that every line fits <see cref="MaxWidth"/> by
    /// <paramref name="width"/>, a long word in pieces; a blank line between paragraphs kept as an empty one, runs of them
    /// and those at either end dropped.
    /// </summary>
    internal static IEnumerable<string> Lines(string text, Func<string, float> width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (paragraph.Trim().Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                {
                    lines.Add("");
                }
                continue;
            }
            var line = "";
            foreach (var (piece, joined) in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).SelectMany(w => Pieces(w, width).Select((c, i) => (c, i > 0))))
            {
                var next = line.Length == 0 ? piece : joined ? line + piece : $"{line} {piece}";
                if (line.Length > 0 && width(next) > MaxWidth)
                {
                    lines.Add(line);
                    next = piece;
                }
                line = next;
            }
            lines.Add(line);
        }
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    // A word in pieces that each fit a line: cut by Chunks, and a piece of wide glyphs still too wide halved until it fits.
    private static IEnumerable<string> Pieces(string word, Func<string, float> width) =>
        Chunks(word).SelectMany(chunk => Halve(chunk, width));

    // A piece that fits whole, else its two halves each measured and halved in turn; a single glyph is never cut, and
    // a cut never falls inside a surrogate pair (it moves off the low surrogate), so no piece holds half of one.
    private static IEnumerable<string> Halve(string piece, Func<string, float> width)
    {
        if (piece.Length <= 1 || width(piece) <= MaxWidth)
        {
            yield return piece;
            yield break;
        }
        var half = piece.Length / 2;
        if (char.IsLowSurrogate(piece[half]))
        {
            half = half > 1 ? half - 1 : half + 1;
        }
        if (half >= piece.Length)
        {
            // Only a lone pair is left: one glyph, as wide as it is.
            yield return piece;
            yield break;
        }
        foreach (var part in Halve(piece[..half], width).Concat(Halve(piece[half..], width)))
        {
            yield return part;
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
            if (char.IsLowSurrogate(word[end]))
            {
                end--;
            }
            yield return word[at..end];
            at = end;
        }
        yield return word[at..];
    }

    public override bool HandleInput(InputEvent evt) => false;
}
