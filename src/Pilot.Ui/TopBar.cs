using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// What a page says of itself in the top bar (#231): its name, its counts, its own buttons and the lines under the bar. A page
/// builds no header of its own; it hands one of these to <see cref="TopBar.Build"/>, which lays it out beside what every page
/// shares (the node and In play pills, Autopilot, the update light and ?).
/// </summary>
/// <param name="Title">The page's name after <c>pilot v2</c>: <c>flights</c>, <c>background tasks</c>.</param>
internal sealed record PageHeader(string Title)
{
    /// <summary>The page's counts line (<c>12 flights · 8 flying</c>); null or empty says none.</summary>
    public string? Counts { get; init; }

    /// <summary>What In play leaves out (<c>29 landed hidden</c>), after the counts, as a click that shows everything; null says nothing is hidden.</summary>
    public string? Hidden { get; init; }

    /// <summary>The action id of the <see cref="Hidden"/> clause.</summary>
    public string HiddenAction { get; init; } = "";

    /// <summary>The page's own buttons, in the middle of the bar after its counts: Sync, Archive ended, the view switch, New flight.</summary>
    public IReadOnlyList<Layout.Node> Buttons { get; init; } = [];

    /// <summary>The source line under the bar: <c>live from the node on … · as of …</c>, or what the page says in its place.</summary>
    public string? Source { get; init; }

    /// <summary>More lines under the source line: a node in maintenance, the LAN's state, the sync's outcome.</summary>
    public IReadOnlyList<Layout.Node> Lines { get; init; } = [];

    /// <summary><c>live from the node on … · as of …</c>, the source line of the pages that list a node's things.</summary>
    public static string SourceLine(string source, DateTimeOffset asOf, string? more = null) =>
        $"{source} · as of {asOf.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC{(more is null ? "" : $" · {more}")}";
}

/// <summary>What a page's bar shares with every other page: the window's own pieces, as the chrome holds them.</summary>
/// <param name="Nodes">The window's node selection, whose pills the bar shows while there is more than one node.</param>
/// <param name="Play">The In play / Everything choice, on the pages that use it; null shows no pills.</param>
/// <param name="Autopilot">The Autopilot switch.</param>
/// <param name="About">The ? card, and the update light before it.</param>
/// <param name="Night">The Night switch, whose state paints the bar on every page.</param>
internal sealed record TopBarParts(NodeFilter? Nodes, PlayFilter? Play, AutopilotSwitch? Autopilot, AboutCard? About, NightSwitch? Night = null);

/// <summary>
/// The one top bar every page shows, Home included (#231): <b>pilot v2</b> and the page's name, then the node pills and the In
/// play pills where the page uses them; the page's counts and buttons in the middle; Autopilot, the update light and ? at the
/// right on every page. Under it, the source line and the page's extra lines. Every page's bar is this one tree, so the heights,
/// the baseline and the order cannot drift.
/// </summary>
internal static class TopBar
{
    /// <summary>The row of the bar itself, design units.</summary>
    public const float RowHeight = 32f;

    /// <summary>The source line's row.</summary>
    public const float SourceHeight = 20f;

    /// <summary>The padding above and below the bar's rows.</summary>
    public const float PadY = 6f;

    /// <summary>The padding either side of the bar.</summary>
    public const float PadX = 16f;

    /// <summary>The bottom of the bar's row of buttons: what the ? card hangs under.</summary>
    public const float ButtonsBottom = PadY + RowHeight;

    /// <summary>The bar with its source line and no extra lines: what the transcript's overlay starts under.</summary>
    public const float Height = RowHeight + SourceHeight + 2 * PadY;

    /// <summary>The bar as one tree, <paramref name="page"/> laid out among <paramref name="parts"/>.</summary>
    public static Layout.Node Build(PageHeader page, TopBarParts parts)
    {
        var night = parts.Night?.Palette;
        var palette = night ?? FlightsPalette.Standard;
        var items = new List<Layout.Node>
        {
            Layout.Builder.Text("pilot v2", 20f, palette.Bright, TextAlign.Near, TextAlign.Far).WAuto(),
            Layout.Builder.Text(page.Title, 20f, palette.Dim, TextAlign.Near, TextAlign.Far).WAuto(),
        };
        items.AddRange(parts.Nodes?.Pills(night) ?? []);
        items.AddRange(parts.Play?.Pills(night) ?? []);
        items.Add(Layout.Builder.Spacer().WStar());
        if (!string.IsNullOrEmpty(page.Counts))
        {
            items.AddRange(PlayFilter.Summary(parts.Play, page.Counts, page.Hidden, page.HiddenAction, palette.Body, palette.RowHoverBg, night));
        }
        items.AddRange(page.Buttons);
        if (parts.Autopilot is { } autopilot)
        {
            items.Add(autopilot.Button(night));
        }
        if (parts.Night is { } nightSwitch)
        {
            items.Add(nightSwitch.Button());
        }
        if (parts.About is { } about)
        {
            items.Add(about.Button(night));
        }

        var rows = new List<Layout.Node> { Layout.Builder.HStack([.. items]).WithGap(12f).RowH(RowHeight) };
        if (page.Source is { } source)
        {
            rows.Add(Layout.Builder.Text(source, 12f, palette.Dim).RowH(SourceHeight));
        }
        rows.AddRange(page.Lines);
        return Layout.Builder.VStack([.. rows]).Pad(PadX, PadY).Bg(palette.HeaderBg).WStar().HAuto();
    }
}
