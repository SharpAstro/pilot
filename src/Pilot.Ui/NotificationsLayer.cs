using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// Paints the For you bar's frame over the page (PIL1184): the toasts while the bar is collapsed, the edge handle and
/// the open bar's header row (its pin). The window's pages and the cards panel are the chrome's own leaves, so they paint nothing here; the
/// panel is drawn after this layer, over it. The layer registers the handle's and the row's regions with the router, and is listed
/// among the widgets after the page and before the cards panel, so the handle and the open bar take the input over the page beneath.
/// It stands on <see cref="PilotWidgetBase{TSurface}"/> so the live text check sees the toasts as it sees every page (a toast's lines drew
/// over each other on SE1, 2026-11-23, and nothing reported it).
/// </summary>
internal sealed class NotificationsLayer<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    /// <summary>Paints <paramref name="frame"/> in <paramref name="area"/>, the content right of the rail.</summary>
    public void Render(Layout.Node frame, RectF32 area)
    {
        BeginFrame();
        RenderLayout(frame, area);
    }
}
