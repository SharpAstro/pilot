using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Gui;

/// <summary>What the cards panel shows: the open cards, rings first, and the latest handled lines.</summary>
internal sealed record CardsScreen(IReadOnlyList<CardRow> Open, IReadOnlyList<CardRow> Handled)
{
    public static readonly CardsScreen Empty = new([], []);

    /// <summary>The open cards rings first, newest first within each; the five latest handled lines.</summary>
    public static CardsScreen From(IEnumerable<CardRow> cards)
    {
        var all = cards.ToList();
        return new CardsScreen(
            [.. all.Where(c => c.State == CardState.Open).OrderBy(c => c.Bin).ThenByDescending(c => c.At)],
            [.. all.Where(c => c.Bin == CardBin.Handled).OrderByDescending(c => c.At).Take(5)]);
    }

    /// <summary>Whether the panel has anything to show: an open card or a handled line. Empty, it takes no room.</summary>
    public bool HasAny => Open.Count > 0 || Handled.Count > 0;

    /// <summary>The panel's head: <c>2 ringing · 1 for you</c>, or <c>Nothing waits on you</c>.</summary>
    public string Summary
    {
        get
        {
            var rings = Open.Count(c => c.Bin == CardBin.Ring);
            var forYou = Open.Count(c => c.Bin == CardBin.ForYou);
            return rings + forYou == 0 ? "Nothing waits on you"
                : string.Join(" · ", new[] { rings > 0 ? $"{rings} ringing" : null, forYou > 0 ? $"{forYou} for you" : null }.Where(s => s is not null));
        }
    }
}

/// <summary>
/// The cards panel down the window's right side, as the design's home screen has the watchtower's: what waits on the
/// controller on top, each card with its buttons (its choices, and Dismiss), then what was handled, a line each. A
/// button runs through <see cref="OnAnswer"/> or <see cref="OnDismiss"/>; the node closes the card and the stream says so.
/// </summary>
internal sealed class CardsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>Design units, scaled by DpiScale.</summary>
    public const float Width = 300f;

    private const float Gutter = 14f;

    private static readonly RGBAColor32 PanelBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 CardBg = new(0x1f, 0x21, 0x2b, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Ring = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 ForYou = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly RGBAColor32 Button = new(0x2c, 0x35, 0x4a, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x3a, 0x46, 0x62, 0xff);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>Called with the card and the choice's number from 1 when its button is clicked.</summary>
    public Action<CardRow, int>? OnAnswer { get; set; }

    /// <summary>Called with the card when its Dismiss is clicked.</summary>
    public Action<CardRow>? OnDismiss { get; set; }

    public void Render(CardsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Build(screen), bounds);
    }

    public override bool HandleInput(InputEvent evt) => false;

    internal Layout.Node Build(CardsScreen screen)
    {
        var blocks = new List<Layout.Node>
        {
            Layout.Builder.Text("For you", 18f, Bright).RowH(30f),
            Layout.Builder.Text(screen.Summary, 12f, Dim).RowH(20f),
        };
        foreach (var card in screen.Open)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(8f));
            blocks.Add(Card(card));
        }
        if (screen.Handled.Count > 0)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(16f));
            blocks.Add(Layout.Builder.Text("Handled", 13f, Dim).RowH(22f));
            foreach (var card in screen.Handled)
            {
                blocks.Add(Layout.Builder.Text(card.Title, 12f, Dim).RowH(20f));
            }
        }
        return Layout.Builder.VStack([.. blocks]).WithScroll(_scroll).Pad(Gutter, 10f).Bg(PanelBg).Stretch();
    }

    private Layout.Node Card(CardRow card)
    {
        var accent = card.Bin == CardBin.Ring ? Ring : ForYou;
        var lines = new List<Layout.Node>
        {
            Layout.Builder.Text(card.Bin == CardBin.Ring ? "RING" : "for you", 10f, accent).RowH(16f),
            Layout.Builder.Text(card.Title, 14f, Bright).RowH(22f),
        };
        if (card.Detail is { Length: > 0 } detail)
        {
            foreach (var line in detail.Split('\n').Take(3))
            {
                lines.Add(Layout.Builder.Text(line, 11f, Body).RowH(18f));
            }
        }

        var buttons = new List<Layout.Node>();
        for (var i = 0; i < card.Choices.Length; i++)
        {
            var number = i + 1;
            buttons.Add(Pill(card.Choices[i].Label, $"card:{card.Id}:{number}", () => OnAnswer?.Invoke(card, number)));
        }
        buttons.Add(Pill("Dismiss", $"card:{card.Id}:dismiss", () => OnDismiss?.Invoke(card)));
        lines.Add(Layout.Builder.Spacer().RowH(6f));
        lines.Add(Layout.Builder.HStack([.. buttons, Layout.Builder.Spacer().WStar()]).WithGap(8f).RowH(26f));

        return Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(accent).WFixed(3f).HStar(),
                Layout.Builder.VStack([.. lines]).Pad(10f, 8f).WStar())
            .Bg(CardBg)
            .RowH(30f + 22f + 18f * Math.Min(3, card.Detail?.Split('\n').Length ?? 0) + 6f + 26f + 16f);
    }

    private static Layout.Node Pill(string label, string action, Action onClick) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .Clickable(new HitResult.ButtonHit(action), _ => onClick());
}
