using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The Notifications page (PIL1184): DIR.Lib's list frame over the For you bar's feed, so the window and the terminal draw one
/// declaration. Needs you comes first (every open card, with the card's own buttons), then History, newest first, each with its severity, time
/// and node; Clear empties the history and leaves what needs you. How long the history is kept is a setting on the Settings page.
/// </summary>
internal sealed class NotificationsView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    /// <summary>The bar whose feed the page lists.</summary>
    public required NotificationsHost Host { get; init; }

    /// <summary>The list the page scrolls.</summary>
    protected override IEnumerable<ListScrollController> PageScrolls => [Host.PageScroll];

    /// <summary>The window's one attachment gallery: a card's thumbnail is painted and opened through it.</summary>
    public AttachmentGallery Gallery { get; set; } = new();

    /// <summary>The header's ? card.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The header's Night switch.</summary>
    public NightSwitch? Night { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>Whether this host can draw the grid: pilot-tui sets this false, keeps the list and shows no switch.</summary>
    public bool GridAvailable { get; set; } = true;

    /// <summary>The chosen view, kept for the window's life (the view is not rebuilt on a page switch).</summary>
    public NotificationsViewMode Mode { get; set; } = NotificationsViewMode.Grid;

    /// <summary>Whether the page is drawing the grid now.</summary>
    public bool GridShown => GridAvailable && Mode == NotificationsViewMode.Grid;

    // Grid and List as the marks DIR.Lib draws, each with its word as the tooltip, on the one ViewSwitch Flights and Ideas share.
    private static readonly ViewChoice<NotificationsViewMode>[] ViewChoices =
    [
        new(NotificationsViewMode.Grid, "notifications:view:grid", "Grid", Icon: Layout.IconKind.Grid),
        new(NotificationsViewMode.List, "notifications:view:list", "List", Icon: Layout.IconKind.List),
    ];

    /// <summary>The cards' gap in the grid, both ways, and the narrowest a card is drawn there (the library's toast width, or this where that is narrower).</summary>
    internal const float GridGap = 14f, GridCardMin = 440f;

    /// <summary>How many columns a grid <paramref name="width"/> design units wide holds: as many cards of at least <see cref="GridCardWidth"/> as fit, and at least one.</summary>
    internal static int GridColumns(float width)
    {
        var inner = MathF.Max(1f, width - 2f * Gutter);
        return Math.Max(1, (int)MathF.Floor((inner + GridGap) / (GridCardWidth + GridGap)));
    }

    /// <summary>The narrowest a grid card is drawn.</summary>
    internal static float GridCardWidth => MathF.Max(GridCardMin, NotificationsHost.Style.ToastWidth);

    public void Render(RectF32 bounds, string source, DateTimeOffset asOf)
    {
        Palette.Use(Night);
        BeginFrame();
        RenderLayout(Tree(source, asOf, bounds.Width / Scale.X), bounds, drawFill: Gallery.DrawFill);
    }

    /// <summary>The page as a tree, <paramref name="width"/> design units wide.</summary>
    internal Layout.Node Tree(string source, DateTimeOffset asOf, float width)
    {
        Palette.Use(Night);
        var needs = Host.Feed.NeedsYou.Count;
        var history = Host.History.Count;
        var header = TopBar.Build(new PageHeader("notifications")
        {
            Counts = $"{needs} need{(needs == 1 ? "s" : "")} you · {history} in the history",
            Buttons = GridAvailable ? [Pilot.Ui.ViewSwitch.Build(ViewChoices, Mode, mode => Mode = mode, Palette.Current)] : [],
            Source = PageHeader.SourceLine(source, asOf),
        }, new TopBarParts(Nodes, null, Autopilot, About, Night));
        var page = Layout.Builder.VStack(header, List(width).Stretch()).Bg(Palette.Current.Ground).Stretch();
        return About?.Over(page) ?? page;
    }

    private const float Gutter = 20f, CardGap = 8f;

    /// <summary>
    /// The list: Needs you, then History newest first, each notification the one card component (<c>CardsView.Card</c>) at page fit, a row across
    /// the page less its gutters (DSN-5 v1: no column cap) with one gap between rows; notifications alike in title, node and text are one row
    /// marked ×N. One declaration for the window and the terminal.
    /// </summary>
    private Layout.Node List(float width)
    {
        Host.PageWidth = width;
        Host.PageGrid = GridShown;
        var column = MathF.Max(1f, width - 2f * Gutter);
        var blocks = new List<Layout.Node>();
        var colors = NotificationsHost.Colors();
        Layout.Node Section(string title, int count, Layout.Node? action = null)
        {
            var label = Layout.Builder.Text($"{title} · {count}", GridShown ? 18f : 13f, GridShown ? Palette.Current.Bright : colors.Dim).Selectable().WStar();
            return (action is null ? Layout.Builder.HStack(label) : Layout.Builder.HStack(label, action)).CrossCenter().RowH(30f).WFixed(column);
        }
        var columns = GridShown ? GridColumns(width) : 0;
        Layout.Node Placed(Layout.Node card) => Layout.Builder.HStack(card.WFixed(column), Layout.Builder.Spacer().WStar()).HAuto();

        var needs = Host.Feed.NeedsYou;
        var history = Host.History;
        if (needs.Count == 0 && history.Count == 0)
        {
            blocks.Add(Layout.Builder.Text("Nothing has happened yet", 13f, colors.Dim).Selectable().RowH(30f));
        }
        if (needs.Count > 0)
        {
            blocks.Add(Section(GridShown ? "Need you" : "Needs you", needs.Count));
            AddCards(blocks, needs, Placed, columns);
        }
        if (history.Count > 0)
        {
            blocks.Add(Section("History", history.Count, CardsView<TSurface>.HeaderPill("Clear", "notifications:clear", Host.Clear)));
            AddCards(blocks, history, Placed, columns);
        }
        return Layout.Builder.VStack([.. blocks]).WithGap(CardGap).WithScroll(Host.PageScroll).Pad(Gutter, 10f).Stretch();
    }

    private void AddCards(List<Layout.Node> blocks, IReadOnlyList<DIR.Lib.Notifications.Notification> notifications, Func<Layout.Node, Layout.Node> place, int columns)
    {
        var cells = new List<Layout.Node>();
        foreach (var (n, count) in Folded(notifications))
        {
            Host.PageRepeats = count;
            try
            {
                var card = n.Row is { } row
                    ? row()
                    : Host.EventCard?.Invoke(n.Title, n.Text, n.Source, n.Posted, n.Posted.ToLocalTime().ToString("HH:mm"), n.Id.ToString(), n.Severity) ?? Layout.Builder.Text(n.Title, 12f, Palette.Current.Bright).Selectable();
                if (columns > 0)
                {
                    cells.Add(card.WStar());
                }
                else
                {
                    blocks.Add(place(card));
                }
            }
            finally
            {
                Host.PageRepeats = 1;
            }
        }
        // The grid: rows of equal star columns, the last row's empty columns kept so its cards are the width of the rest.
        for (var i = 0; i < cells.Count; i += columns)
        {
            var row = cells.Skip(i).Take(columns).ToList();
            while (row.Count < columns)
            {
                row.Add(Layout.Builder.Spacer().WStar());
            }
            blocks.Add(Layout.Builder.HStack([.. row]).WithGap(GridGap).WStar().HAuto());
        }
    }

    /// <summary>
    /// <paramref name="notifications"/> with those of one title, node and text folded into one, the newest of them kept (at the place of the first), with how many it stands for.
    /// </summary>
    internal static List<(DIR.Lib.Notifications.Notification Notification, int Count)> Folded(IReadOnlyList<DIR.Lib.Notifications.Notification> notifications)
    {
        var rows = new List<(DIR.Lib.Notifications.Notification Notification, int Count)>();
        var at = new Dictionary<(string, string?, string), int>();
        foreach (var n in notifications)
        {
            var key = (n.Title, n.Source, n.Text);
            if (at.TryGetValue(key, out var i))
            {
                rows[i] = (rows[i].Notification.Posted >= n.Posted ? rows[i].Notification : n, rows[i].Count + 1);
            }
            else
            {
                at[key] = rows.Count;
                rows.Add((n, 1));
            }
        }
        return rows;
    }

    /// <summary>A keep time as a person reads it: <c>24 hours</c>, <c>3 days</c>.</summary>
    internal static string KeepText(TimeSpan keep) =>
        NotificationsHost.KeepChoices.FirstOrDefault(c => c.Keep == keep).Label
        ?? (keep.TotalHours < 48 ? $"{(int)keep.TotalHours} hours" : $"{(int)keep.TotalDays} days");
}
