using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>Which bubble: an ask (amber border), trouble (red border, error icon), good news (the accent, no buttons) or the one-line summary a hover shows (no border, no icon).</summary>
public enum BubbleStyle
{
    /// <summary>A session waits on the person: amber border, "Open the chat" and "Later".</summary>
    Ask,

    /// <summary>CI failed or a leg is stuck: red border, the error icon, "Open the run" and "Later".</summary>
    Trouble,

    /// <summary>A PR merged or a flight landed: the accent border, a check icon, no buttons.</summary>
    GoodNews,

    /// <summary>The watchtower's one-line summary, shown while the cat hears the pointer: a plain panel.</summary>
    Summary,
}

/// <summary>What a laid-out line of text is, so a test can tell the title from the body.</summary>
public enum BubbleText
{
    /// <summary>The bubble's title, next to the icon.</summary>
    Title,

    /// <summary>The body under it.</summary>
    Body,

    /// <summary>A button's label.</summary>
    Button,
}

/// <summary>One line of text of a laid-out bubble, in the bubble's own design pixels.</summary>
public sealed record BubbleLine(BubbleText Role, string Text, float X, float Y, float Width, float Height, float Size);

/// <summary>A button of a laid-out bubble, in the bubble's own design pixels. <paramref name="Primary"/> is the one that opens the thing.</summary>
public sealed record BubbleButton(string Label, float X, float Y, float Width, float Height, bool Primary);

/// <summary>
/// The speech bubble as one shared component: icon, title, body and up to two buttons, one layout that a draw and a test both read.
/// The text wraps at the bubble's width, a word wider than the line is broken, and what still does not fit ends in an ellipsis with the
/// whole text in <see cref="Tooltip"/> (the window shows it on hover): nothing is cut silently. The border and the icon carry the kind, and the
/// words carry the meaning, so colour never does alone.
/// </summary>
public sealed class SpeechBubble
{
    /// <summary>The widths: a bubble with buttons, and the plainer ones.</summary>
    public const float AskWidth = 336, PlainWidth = 300;

    /// <summary>Padding, the gap between rows, the corner radius, the border, and the icon's size and the gap after it.</summary>
    public const float Padding = 12, Gap = 8, Radius = 12, BorderWidth = 1.5f, IconSize = 16, IconGap = 6;

    /// <summary>Font sizes, and the most lines a title and a body take before the ellipsis.</summary>
    public const float TitleSize = 14, BodySize = 13, LineSpacing = 1.35f;

    /// <summary>The most lines of the title and of the body.</summary>
    public const int MaxTitleLines = 2, MaxBodyLines = 5;

    /// <summary>A button's height and the gap between two.</summary>
    public const float ButtonHeight = 28, ButtonGap = 6;

    private SpeechBubble(BubbleStyle style, float width, float height, IReadOnlyList<BubbleLine> lines, IReadOnlyList<BubbleButton> buttons, string? tooltip)
    {
        Style = style;
        Width = width;
        Height = height;
        Lines = lines;
        Buttons = buttons;
        Tooltip = tooltip;
    }

    /// <summary>The kind of bubble.</summary>
    public BubbleStyle Style { get; }

    /// <summary>The bubble's size in design pixels.</summary>
    public float Width { get; }

    /// <summary>The bubble's height, as tall as its text needs.</summary>
    public float Height { get; }

    /// <summary>Every line of text, the buttons' labels included.</summary>
    public IReadOnlyList<BubbleLine> Lines { get; }

    /// <summary>The buttons (none for good news and the summary).</summary>
    public IReadOnlyList<BubbleButton> Buttons { get; }

    /// <summary>The whole title and body when either ended in an ellipsis, else null: what the window shows on hover.</summary>
    public string? Tooltip { get; }

    /// <summary>The bubble for <paramref name="notice"/>.</summary>
    public static SpeechBubble ForNotice<TSurface>(Pen<TSurface> pen, BuddyNotice notice) => Build(pen, notice.Kind switch
    {
        NoticeKind.Ask => BubbleStyle.Ask,
        NoticeKind.Trouble => BubbleStyle.Trouble,
        _ => BubbleStyle.GoodNews,
    }, notice.Title, notice.Body);

    /// <summary>The one-line summary bubble.</summary>
    public static SpeechBubble ForSummary<TSurface>(Pen<TSurface> pen, string summary) => Build(pen, BubbleStyle.Summary, null, summary);

    /// <summary>The label of the button that opens what the bubble is about.</summary>
    public static string PrimaryLabel(BubbleStyle style) => style == BubbleStyle.Trouble ? "Open the run" : "Open the chat";

    /// <summary>The label of the button that puts it off.</summary>
    public const string LaterLabel = "Later";

    private static SpeechBubble Build<TSurface>(Pen<TSurface> pen, BubbleStyle style, string? title, string body)
    {
        var width = style is BubbleStyle.Ask or BubbleStyle.Trouble ? AskWidth : PlainWidth;
        var lines = new List<BubbleLine>();
        var tooltip = false;
        var y = Padding;
        var inner = width - 2 * Padding;

        if (title is not null)
        {
            var textX = Padding + IconSize + IconGap;
            var wrapped = TextWrap.Lines(text => pen.Measure(text, TitleSize).Width, title, width - Padding - textX, MaxTitleLines);
            var lineH = MathF.Ceiling(MathF.Max(pen.Measure("Ag", TitleSize).Height, TitleSize * LineSpacing));
            var rowH = MathF.Max(IconSize, lineH * wrapped.Lines.Count);
            tooltip |= wrapped.Truncated;
            for (var i = 0; i < wrapped.Lines.Count; i++)
            {
                lines.Add(new BubbleLine(BubbleText.Title, wrapped.Lines[i], textX, y + i * lineH, width - Padding - textX, lineH, TitleSize));
            }
            y += rowH + Gap;
        }

        if (body.Length > 0)
        {
            var wrapped = TextWrap.Lines(text => pen.Measure(text, BodySize).Width, body, inner, MaxBodyLines);
            var lineH = MathF.Ceiling(MathF.Max(pen.Measure("Ag", BodySize).Height, BodySize * LineSpacing));
            tooltip |= wrapped.Truncated;
            for (var i = 0; i < wrapped.Lines.Count; i++)
            {
                lines.Add(new BubbleLine(BubbleText.Body, wrapped.Lines[i], Padding, y + i * lineH, inner, lineH, BodySize));
            }
            y += lineH * wrapped.Lines.Count + Gap;
        }

        var buttons = new List<BubbleButton>();
        if (style is BubbleStyle.Ask or BubbleStyle.Trouble)
        {
            var each = (inner - ButtonGap) / 2;
            buttons.Add(new BubbleButton(PrimaryLabel(style), Padding, y, each, ButtonHeight, true));
            buttons.Add(new BubbleButton(LaterLabel, Padding + each + ButtonGap, y, each, ButtonHeight, false));
            foreach (var button in buttons)
            {
                lines.Add(new BubbleLine(BubbleText.Button, button.Label, button.X, button.Y, button.Width, button.Height, BodySize));
            }
            y += ButtonHeight + Gap;
        }

        // The last row has Padding under it, not Gap.
        var height = y - Gap + Padding;
        return new SpeechBubble(style, width, height, lines, buttons, tooltip ? (title is null ? body : title + "\n" + body) : null);
    }

    /// <summary>Draws the bubble with its top left at the pen's origin.</summary>
    public void Draw<TSurface>(Pen<TSurface> pen, BuddyPalette palette)
    {
        var border = Style switch
        {
            BubbleStyle.Ask => palette.Amber,
            BubbleStyle.Trouble => palette.Red,
            BubbleStyle.GoodNews => palette.Accent,
            _ => (RGBAColor32?)null,
        };
        pen.Box(0, 0, Width, Height, palette.PanelBg, Radius, border, border is null ? 0 : BorderWidth);

        if (Style != BubbleStyle.Summary)
        {
            DrawIcon(pen, palette, Padding, Padding + 1);
        }

        foreach (var line in Lines)
        {
            switch (line.Role)
            {
                case BubbleText.Title:
                    pen.Text(line.Text, line.X, line.Y, line.Width, line.Height, line.Size, palette.HeaderText);
                    break;
                case BubbleText.Body:
                    pen.Text(line.Text, line.X, line.Y, line.Width, line.Height, line.Size, Style == BubbleStyle.GoodNews ? palette.MutedText : palette.BodyText);
                    break;
            }
        }

        foreach (var button in Buttons)
        {
            pen.Box(button.X, button.Y, button.Width, button.Height, palette.ButtonBg, 6);
            pen.Text(button.Label, button.X, button.Y, button.Width, button.Height, BodySize, palette.ButtonText, TextAlign.Center, TextAlign.Center);
        }
    }

    // The icons are shapes too: a triangle with "!" for an ask, a circled cross for trouble, a circled check for good news.
    private void DrawIcon<TSurface>(Pen<TSurface> pen, BuddyPalette palette, float x, float y)
    {
        var s = IconSize;
        switch (Style)
        {
            case BubbleStyle.Ask:
                pen.Polyline(palette.Amber, 1.6f, (x + s / 2, y + 1), (x + s - 1, y + s - 2), (x + 1, y + s - 2), (x + s / 2, y + 1));
                pen.Line(x + s / 2, y + 5, x + s / 2, y + 9.5f, palette.Amber, 1.6f);
                pen.Line(x + s / 2, y + 11.5f, x + s / 2, y + 12f, palette.Amber, 1.8f);
                break;
            case BubbleStyle.Trouble:
                pen.Oval(x, y, s, s, palette.Red);
                pen.Oval(x + 1.6f, y + 1.6f, s - 3.2f, s - 3.2f, palette.PanelBg);
                pen.Line(x + 5, y + 5, x + s - 5, y + s - 5, palette.Red, 1.6f);
                pen.Line(x + s - 5, y + 5, x + 5, y + s - 5, palette.Red, 1.6f);
                break;
            default:
                pen.Oval(x, y, s, s, palette.Accent);
                pen.Oval(x + 1.6f, y + 1.6f, s - 3.2f, s - 3.2f, palette.PanelBg);
                pen.Polyline(palette.Accent, 1.8f, (x + 4.5f, y + 8.5f), (x + 7, y + 11), (x + 11.5f, y + 5.5f));
                break;
        }
    }

    /// <summary>The button under a point in the bubble's own pixels, or null.</summary>
    public BubbleButton? ButtonAt(float x, float y) => Buttons.FirstOrDefault(b => x >= b.X && x < b.X + b.Width && y >= b.Y && y < b.Y + b.Height);
}

/// <summary>Word wrapping for the bubble: words at spaces, a word wider than the line broken by character, an ellipsis when the lines run out.</summary>
public static class TextWrap
{
    /// <summary>The ellipsis that ends a cut text.</summary>
    public const string Ellipsis = "…";

    /// <summary>The wrapped lines, and whether the text was cut (the last line then ends in <see cref="Ellipsis"/>).</summary>
    public sealed record Result(IReadOnlyList<string> Lines, bool Truncated);

    /// <summary>Wraps <paramref name="text"/> to <paramref name="width"/> (as <paramref name="measure"/> sees it) in at most <paramref name="maxLines"/> lines.</summary>
    public static Result Lines(Func<string, float> measure, string text, float width, int maxLines)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var raw in text.Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw;
            while (true)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (measure(candidate) <= width)
                {
                    current = candidate;
                    break;
                }
                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                    continue;
                }
                // A word wider than the line: the longest prefix that fits goes on this line, the rest on the next.
                var take = 1;
                while (take < word.Length && measure(word[..(take + 1)]) <= width)
                {
                    take++;
                }
                lines.Add(word[..take]);
                word = word[take..];
                if (word.Length == 0)
                {
                    break;
                }
            }
        }
        if (current.Length > 0)
        {
            lines.Add(current);
        }
        if (lines.Count <= maxLines)
        {
            return new Result(lines, false);
        }
        var kept = lines.Take(maxLines).ToList();
        kept[^1] = WithEllipsis(measure, kept[^1] + " " + string.Join(' ', lines.Skip(maxLines)), width);
        return new Result(kept, true);
    }

    private static string WithEllipsis(Func<string, float> measure, string line, float width)
    {
        var end = line.Length;
        while (end > 0 && measure(line[..end].TrimEnd() + Ellipsis) > width)
        {
            end--;
        }
        return line[..end].TrimEnd() + Ellipsis;
    }
}
