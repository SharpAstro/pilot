using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// Paints the For you bar's frame over the page (PIL1184): DIR.Lib's strip with its bell, the toasts while the bar is collapsed, and
/// the open bar's collapse row. The window's pages and the cards panel are the chrome's own leaves, so they paint nothing here; the
/// panel is drawn after this layer, over it. The layer registers the strip's and the row's regions with the router, and is listed
/// among the widgets after the page and before the cards panel, so the strip and the open bar take the input over the page beneath.
/// </summary>
internal sealed class NotificationsLayer<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>Paints <paramref name="frame"/> in <paramref name="area"/>, the content right of the rail.</summary>
    public void Render(Layout.Node frame, RectF32 area)
    {
        BeginFrame();
        RenderLayout(frame, area);
    }
}
