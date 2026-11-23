using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The one attachment viewer of the window: the chrome paints it last, over the rail, the page and the cards panel, so a thumbnail
/// of a card, a flight, a session or an idea opens its picture over the whole window. It registers its click region with the
/// router (listed after every other widget), and paints nothing while no copy is open.
/// </summary>
internal sealed class AttachmentViewer<TSurface>(Renderer<TSurface> renderer, AttachmentGallery gallery) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>Paints the open copy over <paramref name="bounds"/>, the whole window; does nothing with none open.</summary>
    public void Render(RectF32 bounds)
    {
        BeginFrame();
        if (gallery.Viewer() is { } viewer)
        {
            RenderLayout(viewer, bounds, drawFill: gallery.DrawFill);
        }
    }
}
