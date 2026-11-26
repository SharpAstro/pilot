using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>One laid-out line of text of a card, in the card's own design pixels (the tests' overlap check reads these).</summary>
public sealed record CardLine(string Text, float X, float Y, float Width, float Height, float Size);

/// <summary>A pressable place of a card, in the card's own design pixels. <paramref name="Id"/> says what it is (<c>row:2</c>, <c>wakeAsk</c>, <c>choice:0</c>, <c>input</c>).</summary>
public sealed record CardHit(string Id, float X, float Y, float Width, float Height);

/// <summary>What a drawn part of a card is.</summary>
public enum CardItemKind
{
    /// <summary>Text in one of the palette's colours.</summary>
    Text,

    /// <summary>A one-pixel rule.</summary>
    Rule,

    /// <summary>A rounded box (a button, the input, a dropdown, a segment); <see cref="CardItem.On"/> marks the chosen segment.</summary>
    Box,

    /// <summary>A check box.</summary>
    Check,

    /// <summary>A switch.</summary>
    Switch,

    /// <summary>The chevron of a dropdown.</summary>
    Chevron,

    /// <summary>The warning triangle of an ask.</summary>
    WarnIcon,

    /// <summary>The ring of a working session.</summary>
    InfoIcon,

    /// <summary>The check of an idle, fine session.</summary>
    OkIcon,

    /// <summary>The arrow that says a link leaves the card for the GUI.</summary>
    Arrow,

    /// <summary>The text cursor of the input.</summary>
    Cursor,
}

/// <summary>Which palette colour a card's text or item takes.</summary>
public enum CardTone
{
    /// <summary>Header text.</summary>
    Header,

    /// <summary>Body text.</summary>
    Body,

    /// <summary>Muted text.</summary>
    Muted,

    /// <summary>The amber of an ask.</summary>
    Warn,

    /// <summary>The red of a refusal.</summary>
    Bad,

    /// <summary>The accent, for links.</summary>
    Accent,

    /// <summary>A button's text.</summary>
    Button,
}

/// <summary>A part of a card to draw.</summary>
public sealed record CardItem(CardItemKind Kind, float X, float Y, float Width, float Height, string Text = "", float Size = 13, CardTone Tone = CardTone.Body, bool On = false);

/// <summary>
/// What the peek, settings and chat cards share: a panel of a fixed width, as tall as its content, laid out once into <see cref="Items"/>,
/// <see cref="Lines"/> and <see cref="Hits"/> that a draw and a test both read, as <see cref="SpeechBubble"/> does. Text that does not fit ends in
/// an ellipsis and its whole is kept in <see cref="Tooltips"/> (by the hit's id) for the window's hover, so nothing is cut silently.
/// </summary>
public abstract class BuddyCard
{
    /// <summary>Padding, the gap between rows, the corner radius and the border width.</summary>
    public const float Padding = 12, Gap = 8, Radius = 12, BorderWidth = 1;

    /// <summary>The panel's width.</summary>
    public float Width { get; protected init; }

    /// <summary>The panel's height.</summary>
    public float Height { get; protected set; }

    /// <summary>The border colour: the amber of an ask, else the panel's own edge. Null for the palette's CardBorder.</summary>
    protected virtual bool AmberBorder => false;

    /// <summary>The parts to draw, in order.</summary>
    public List<CardItem> Items { get; } = [];

    /// <summary>The text lines.</summary>
    public List<CardLine> Lines { get; } = [];

    /// <summary>The pressable places.</summary>
    public List<CardHit> Hits { get; } = [];

    /// <summary>Whole texts of what ended in an ellipsis, by hit id (or the text's own for a line that is no hit).</summary>
    public Dictionary<string, string> Tooltips { get; } = [];

    /// <summary>The hit under a point in the card's own pixels, or null.</summary>
    public CardHit? HitAt(float x, float y) => Hits.LastOrDefault(h => x >= h.X && x < h.X + h.Width && y >= h.Y && y < h.Y + h.Height);

    /// <summary>Where the card's top left goes so it sits above the cat, right aligned with the bubble's edge (device pixels).</summary>
    public (float X, float Y) PlaceAboveCat<TSurface>(Renderer<TSurface> renderer, float catY, float scale = 1f)
        => (renderer.Width - (BuddyScene.Margin + 4 + Width) * scale, catY - (BuddyScene.BubbleGap + Height) * scale);

    /// <summary>The height of one line of <paramref name="size"/> text: the font's, or the shared 1.35 times the size, the larger.</summary>
    protected static float LineHeight<TSurface>(Pen<TSurface> pen, float size)
        => MathF.Ceiling(MathF.Max(pen.Measure("Ag", size).Height, size * SpeechBubble.LineSpacing));

    /// <summary>Fits <paramref name="text"/> on one line of <paramref name="width"/>: itself, or cut with an ellipsis. Reports the cut.</summary>
    protected static (string Text, bool Cut) Fit<TSurface>(Pen<TSurface> pen, string text, float size, float width)
    {
        var result = TextWrap.Lines(t => pen.Measure(t, size).Width, text, width, 1);
        return (result.Lines.Count == 0 ? string.Empty : result.Lines[0], result.Truncated);
    }

    /// <summary>Adds a one-line text, fitted, and returns the drawn width.</summary>
    protected float AddLine<TSurface>(Pen<TSurface> pen, string text, float x, float y, float width, float size, CardTone tone, string? tooltipId = null)
    {
        var h = LineHeight(pen, size);
        var (fit, cut) = Fit(pen, text, size, width);
        Items.Add(new CardItem(CardItemKind.Text, x, y, width, h, fit, size, tone));
        Lines.Add(new CardLine(fit, x, y, width, h, size));
        if (cut && tooltipId is not null)
        {
            Tooltips[tooltipId] = text;
        }
        return pen.Measure(fit, size).Width;
    }

    /// <summary>Adds wrapped text of at most <paramref name="maxLines"/> lines from <paramref name="y"/> and returns the y after it.</summary>
    protected float AddWrapped<TSurface>(Pen<TSurface> pen, string text, float x, float y, float width, float size, CardTone tone, int maxLines, string tooltipId)
    {
        var h = LineHeight(pen, size);
        var wrapped = TextWrap.Lines(t => pen.Measure(t, size).Width, text, width, maxLines);
        for (var i = 0; i < wrapped.Lines.Count; i++)
        {
            Items.Add(new CardItem(CardItemKind.Text, x, y + i * h, width, h, wrapped.Lines[i], size, tone));
            Lines.Add(new CardLine(wrapped.Lines[i], x, y + i * h, width, h, size));
        }
        if (wrapped.Truncated)
        {
            Tooltips[tooltipId] = text;
        }
        return y + h * wrapped.Lines.Count;
    }

    /// <summary>Draws the card with its top left at the pen's origin.</summary>
    public void Draw<TSurface>(Pen<TSurface> pen, BuddyPalette palette)
    {
        pen.Box(0, 0, Width, Height, palette.PanelBg, Radius, AmberBorder ? palette.Amber : palette.CardBorder, AmberBorder ? 1.5f : BorderWidth);
        foreach (var item in Items)
        {
            DrawItem(pen, palette, item);
        }
    }

    private static RGBAColor32 ToneColor(BuddyPalette palette, CardTone tone) => tone switch
    {
        CardTone.Header => palette.HeaderText,
        CardTone.Muted => palette.MutedText,
        CardTone.Warn => palette.Amber,
        CardTone.Bad => palette.Red,
        CardTone.Accent => palette.Accent,
        CardTone.Button => palette.ButtonText,
        _ => palette.BodyText,
    };

    private static void DrawItem<TSurface>(Pen<TSurface> pen, BuddyPalette palette, CardItem item)
    {
        var (x, y, w, h) = (item.X, item.Y, item.Width, item.Height);
        switch (item.Kind)
        {
            case CardItemKind.Text:
                pen.Text(item.Text, x, y, w, h, item.Size, ToneColor(palette, item.Tone));
                break;
            case CardItemKind.Rule:
                pen.Box(x, y, w, 1, palette.CardBorder);
                break;
            case CardItemKind.Box:
                // A chosen segment has a heavier accent edge: the weight says "chosen", not the colour alone.
                pen.Box(x, y, w, h, palette.ButtonBg, 6, item.On ? palette.Accent : palette.CardBorder, item.On ? 2f : 1f);
                break;
            case CardItemKind.Check:
                pen.Box(x, y, w, h, item.On ? palette.Accent : palette.PanelBg, 4, item.On ? palette.Accent : palette.CardBorder, 1.5f);
                if (item.On)
                {
                    pen.Polyline(palette.PanelBg, 2f, (x + w * 0.24f, y + h * 0.52f), (x + w * 0.44f, y + h * 0.72f), (x + w * 0.78f, y + h * 0.3f));
                }
                break;
            case CardItemKind.Switch:
                pen.Box(x, y, w, h, item.On ? palette.Accent : palette.ButtonBg, h / 2, item.On ? palette.Accent : palette.CardBorder, 1.5f);
                pen.Oval(item.On ? x + w - h + 3 : x + 3, y + 3, h - 6, h - 6, item.On ? palette.PanelBg : palette.CardBorder);
                break;
            case CardItemKind.Chevron:
                pen.Polyline(palette.MutedText, 1.6f, (x, y), (x + w / 2, y + h), (x + w, y));
                break;
            case CardItemKind.WarnIcon:
                pen.Polyline(palette.Amber, 1.6f, (x + w / 2, y + 1), (x + w - 1, y + h - 2), (x + 1, y + h - 2), (x + w / 2, y + 1));
                pen.Line(x + w / 2, y + h * 0.38f, x + w / 2, y + h * 0.62f, palette.Amber, 1.6f);
                pen.Line(x + w / 2, y + h * 0.74f, x + w / 2, y + h * 0.78f, palette.Amber, 1.8f);
                break;
            case CardItemKind.InfoIcon:
                pen.Oval(x, y, w, h, palette.Accent);
                pen.Oval(x + 1.6f, y + 1.6f, w - 3.2f, h - 3.2f, palette.PanelBg);
                pen.Line(x + w / 2, y + h * 0.46f, x + w / 2, y + h * 0.74f, palette.Accent, 1.6f);
                pen.Line(x + w / 2, y + h * 0.28f, x + w / 2, y + h * 0.32f, palette.Accent, 1.8f);
                break;
            case CardItemKind.OkIcon:
                pen.Oval(x, y, w, h, palette.Green);
                pen.Oval(x + 1.6f, y + 1.6f, w - 3.2f, h - 3.2f, palette.PanelBg);
                pen.Polyline(palette.Green, 1.8f, (x + w * 0.28f, y + h * 0.52f), (x + w * 0.44f, y + h * 0.68f), (x + w * 0.74f, y + h * 0.32f));
                break;
            case CardItemKind.Arrow:
                pen.Line(x, y + h, x + w, y, palette.Accent, 1.4f);
                pen.Polyline(palette.Accent, 1.4f, (x + w * 0.2f, y), (x + w, y), (x + w, y + h * 0.8f));
                break;
            case CardItemKind.Cursor:
                pen.Line(x, y, x, y + h, palette.HeaderText, 1.4f);
                break;
        }
    }
}
