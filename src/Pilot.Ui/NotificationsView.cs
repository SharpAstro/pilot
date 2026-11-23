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
        }, new TopBarParts(Nodes, null, Autopilot, About, Night));
        var page = Layout.Builder.VStack(header, Host.ListFrame(width).Stretch()).Bg(Palette.Current.Ground).Stretch();
        return About?.Over(page) ?? page;
    }

    /// <summary>A keep time as a person reads it: <c>24 hours</c>, <c>3 days</c>.</summary>
    internal static string KeepText(TimeSpan keep) =>
        NotificationsHost.KeepChoices.FirstOrDefault(c => c.Keep == keep).Label
        ?? (keep.TotalHours < 48 ? $"{(int)keep.TotalHours} hours" : $"{(int)keep.TotalDays} days");
}
