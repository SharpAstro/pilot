using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What the right panel shows: the watchtower (its state and Remote Control link, when the node keeps one), the open
/// cards, rings first, and the latest handled lines.
/// </summary>
internal sealed record CardsScreen(IReadOnlyList<CardRow> Open, IReadOnlyList<CardRow> Handled, SessionRow? Watchtower = null)
{
    public static readonly CardsScreen Empty = new([], []);

    /// <summary>The open cards rings first, newest first within each; the five latest handled lines; the watchtower's row, if any.</summary>
    public static CardsScreen From(IEnumerable<CardRow> cards, IEnumerable<SessionRow>? sessions = null)
    {
        var all = cards.ToList();
        // The node's own (a background session; an interactive one named so is someone's terminal), live first.
        var watchtower = (sessions ?? [])
            .Where(s => s.Role == SessionRole.Watchtower && s.Kind == SessionKind.Background)
            .OrderByDescending(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle)
            .ThenByDescending(s => s.StartedAt)
            .FirstOrDefault();
        return new CardsScreen(
            [.. all.Where(c => c.State == CardState.Open).OrderBy(c => c.Bin).ThenByDescending(c => c.At)],
            [.. all.Where(c => c.Bin == CardBin.Handled).OrderByDescending(c => c.At).Take(5)],
            watchtower);
    }

    /// <summary>Whether the panel has anything to show: the watchtower, an open card or a handled line. Empty, it takes no room.</summary>
    public bool HasAny => Watchtower is not null || Open.Count > 0 || Handled.Count > 0;

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

    /// <summary>Whether the watchtower can be told things from here: live, with a node.</summary>
    public bool CanTell { get; set; }

    /// <summary>The box that tells the watchtower something.</summary>
    public TextInputState TellInput { get; } = new();

    /// <summary>Called with what was typed in the tell box.</summary>
    public Func<string, Task>? OnTell { get; set; }

    /// <summary>What the last tell came to.</summary>
    public string? TellStatus { get; set; }

    /// <summary>Opens the watchtower's Remote Control link.</summary>
    public Action<string>? OnOpen { get; set; }

    private static readonly RGBAColor32 Live = new(0x4c, 0xc3, 0x8a, 0xff);

    public void Render(CardsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>The panel as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(CardsScreen screen)
    {
        TellInput.OnCommit = TellAsync;
        TellInput.Placeholder = "tell the watchtower · Enter sends";
        return Build(screen);
    }

    private async Task TellAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || OnTell is not { } tell)
        {
            return;
        }
        TellInput.Text = "";
        TellInput.CursorPos = 0;
        await tell(text.Trim());
    }

    /// <summary>The watchtower's block: who it is, how it is, its Remote Control link, and the box to tell it things.</summary>
    private List<Layout.Node> Watchtower(SessionRow row)
    {
        var live = row.State is SessionState.Working or SessionState.Waiting or SessionState.Idle;
        var blocks = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("Watchtower", 18f, Bright).WAuto(),
                    Layout.Builder.Spacer().WStar(),
                    Layout.Builder.Text(row.State.ToString().ToLowerInvariant(), 12f, live ? Live : Ring, TextAlign.Far).WAuto())
                .RowH(30f),
            Layout.Builder.Text($"{row.Name}{(row.ContextWindow > 0 ? $" · context {100.0 * row.ContextTokens / row.ContextWindow:0}%" : "")}", 11f, Dim).RowH(18f),
        };
        if (row.RemoteControlUrl is { } url)
        {
            blocks.Add(Layout.Builder.Text("Remote Control: open in the browser", 12f, ForYou)
                .Clickable(new HitResult.ButtonHit("watchtower:rc"), _ => OnOpen?.Invoke(url))
                .RowH(20f));
        }
        if (CanTell)
        {
            blocks.Add(Layout.Builder.TextInput(TellInput, 13f).RowH(26f));
            blocks.Add(Layout.Builder.Text(TellStatus ?? "", 10f, Dim).RowH(16f));
        }
        blocks.Add(Layout.Builder.Spacer().RowH(10f));
        return blocks;
    }

    public override bool HandleInput(InputEvent evt) => false;

    internal Layout.Node Build(CardsScreen screen)
    {
        var blocks = screen.Watchtower is { } tower ? Watchtower(tower) : [];
        blocks.Add(Layout.Builder.Text("For you", 18f, Bright).RowH(30f));
        blocks.Add(Layout.Builder.Text(screen.Summary, 12f, Dim).RowH(20f));
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
