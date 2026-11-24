using DIR.Lib;
using DIR.Lib.Notifications;

namespace Pilot.Ui;

/// <summary>
/// The toast's frame, composed from DIR.Lib's public tokens (DIR.Lib's own toast builder is private): the severity's tinted fill and edge, the
/// corner radius and the shadow, the severity icon on the left, a Close cross on the right of an open row. One source: the toast over the page and
/// the Notifications page's rows take their colours, radius and sizes from <see cref="NotificationsHost.Colors"/> and
/// <see cref="NotificationsHost.Style"/>, and the page's text sizes are the constants here (DSN-5 v1).
/// </summary>
internal static class NotificationToast
{
    /// <summary>An open row's title, in design units.</summary>
    internal const float OpenTitleSize = 18f;

    /// <summary>A closed row's title.</summary>
    internal const float ClosedTitleSize = 17f;

    /// <summary>A row's summary, open or closed, and its folded detail.</summary>
    internal const float SummarySize = 15f;

    /// <summary>A row's meta (node, time, repeat count) and the Detail fold's label.</summary>
    internal const float MetaSize = 14f;

    /// <summary>The widest the right-hand cluster (meta, buttons, Close) of an open row is given; the buttons wrap within it.</summary>
    internal const float ActionsWidth = 460f;

    /// <summary>Below this page width, in design units, a row stacks its actions under its body (and a closed row its summary and meta under its title) instead of in columns.</summary>
    internal const float NarrowPage = 900f;

    /// <summary>The severity's icon, as DIR.Lib's toast draws it.</summary>
    internal static Layout.IconKind IconOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => Layout.IconKind.Error,
        NotificationSeverity.Warn => Layout.IconKind.Warning,
        NotificationSeverity.Success => Layout.IconKind.Success,
        _ => Layout.IconKind.Info,
    };

    /// <summary>The icon cell of a row: the severity icon at the toast's size, in the severity's colour.</summary>
    internal static Layout.Node Icon(NotificationSeverity severity, RGBAColor32 ink) =>
        Layout.Builder.Icon(IconOf(severity), NotificationsLayout.ToastIconSize, ink);

    /// <summary>The Close cross of an open row: the toast's click box with the cross in it; a null <paramref name="hit"/> draws it dead (a press is with the node).</summary>
    internal static Layout.Node Close(RGBAColor32 ink, string? hit, Action? run)
    {
        var cross = Layout.Builder.HStack(Layout.Builder.Icon(Layout.IconKind.Close, 12f, ink))
            .Pad((NotificationsLayout.CloseBoxSize - 12f) / 2f)
            .CrossCenter()
            .WAuto();
        return hit is null || run is null
            ? cross
            : cross.Follows(new HitResult.ButtonHit(hit), _ => run()).WithTooltip("Close");
    }

    /// <summary>
    /// The frame round <paramref name="cells"/> (one per column of <paramref name="columns"/>): the fill and edge given (the severity's tint, toned
    /// for a closed row), the library's corner radius, the toast's padding, and the shadow on an open row.
    /// </summary>
    internal static Layout.Node Frame(RGBAColor32 fill, RGBAColor32 edge, bool shadow, Layout.Sizing[] columns, params Layout.Node[] cells)
    {
        var tokens = NotificationsHost.Colors();
        var style = NotificationsHost.Style;
        var inset = style.Padding - 1f;
        var framed = new List<Layout.Node> { Layout.Builder.Spacer() };
        framed.AddRange(cells);
        framed.Add(Layout.Builder.Spacer());
        var sizes = new List<Layout.Sizing> { Layout.Sizing.Fixed(inset) };
        sizes.AddRange(columns);
        sizes.Add(Layout.Sizing.Fixed(inset));
        var grid = Layout.Builder.Grid(sizes.Count, [.. framed])
            .WithColumns([.. sizes])
            .WithAutoRows()
            .Bg(fill)
            .Border(edge, 1f, tokens.CornerRadius)
            .Radius(tokens.CornerRadius)
            .WStar()
            .HAuto();
        return shadow ? grid.Shadow(tokens.ToastShadow) : grid;
    }
}
