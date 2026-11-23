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

    /// <summary>The header's Night switch.</summary>
    public NightSwitch? Night { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

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
            Counts = $"{needs} need{(needs == 1 ? "s" : "")} you, {history} in the history (kept {KeepText(Host.KeepFor)})",
            CountsFit = true,
            Source = PageHeader.SourceLine(source, asOf),
        }, new TopBarParts(Nodes, null, About, Night));
        var page = Layout.Builder.VStack(header, List(width).Stretch()).Bg(Palette.Current.Ground).Stretch();
        return About?.Over(page) ?? page;
    }

    /// <summary>
    /// The widest a card is drawn on the page, in design units: near the toast's width (<see cref="NotificationsHost.Style"/>'s ToastWidth) and no
    /// wider than the panel's, so a card reads as it does in a toast and the lines stay short on a wide window.
    /// </summary>
    internal static float ColumnWidth => MathF.Round(NotificationsHost.Style.ToastWidth * 1.375f);

    private const float Gutter = 14f, CardGap = 10f;

    /// <summary>
    /// The list: Needs you, then History newest first, each notification the one card component (<c>CardsView.Card</c>) at page fit, in a
    /// left-aligned column of <see cref="ColumnWidth"/> at most with one gap between cards. One declaration for the window and the terminal.
    /// </summary>
    private Layout.Node List(float width)
    {
        var column = MathF.Max(1f, MathF.Min(ColumnWidth, width - 2f * Gutter));
        var blocks = new List<Layout.Node>();
        var colors = NotificationsHost.Colors();
        Layout.Node Section(string title, int count, Layout.Node? action = null)
        {
            var label = Layout.Builder.Text($"{title} · {count}", 13f, colors.Dim).Selectable().WStar();
            return (action is null ? Layout.Builder.HStack(label) : Layout.Builder.HStack(label, action)).CrossCenter().RowH(30f).WFixed(column);
        }
        Layout.Node Placed(Layout.Node card) => Layout.Builder.HStack(card.WFixed(column), Layout.Builder.Spacer().WStar()).HAuto();

        var needs = Host.Feed.NeedsYou;
        var history = Host.History;
        if (needs.Count == 0 && history.Count == 0)
        {
            blocks.Add(Layout.Builder.Text("Nothing has happened yet", 13f, colors.Dim).Selectable().RowH(30f));
        }
        if (needs.Count > 0)
        {
            blocks.Add(Section("Needs you", needs.Count));
            AddCards(blocks, needs, Placed);
        }
        if (history.Count > 0)
        {
            blocks.Add(Section("History", history.Count, CardsView<TSurface>.HeaderPill("Clear", "notifications:clear", Host.Clear)));
            AddCards(blocks, history, Placed);
        }
        return Layout.Builder.VStack([.. blocks]).WithGap(CardGap).WithScroll(Host.PageScroll).Pad(Gutter, 10f).Stretch();
    }

    private void AddCards(List<Layout.Node> blocks, IReadOnlyList<DIR.Lib.Notifications.Notification> notifications, Func<Layout.Node, Layout.Node> place)
    {
        foreach (var n in notifications)
        {
            var card = n.Row is { } row
                ? row()
                : Host.EventCard?.Invoke(n.Title, n.Text, n.Source, n.Posted, n.Posted.ToLocalTime().ToString("HH:mm"), n.Id, n.Severity) ?? Layout.Builder.Text(n.Title, 12f, Palette.Current.Bright).Selectable();
            blocks.Add(place(card));
        }
    }

    /// <summary>A keep time as a person reads it: <c>24 hours</c>, <c>3 days</c>.</summary>
    internal static string KeepText(TimeSpan keep) =>
        NotificationsHost.KeepChoices.FirstOrDefault(c => c.Keep == keep).Label
        ?? (keep.TotalHours < 48 ? $"{(int)keep.TotalHours} hours" : $"{(int)keep.TotalDays} days");
}
