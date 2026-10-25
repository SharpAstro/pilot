using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The window's one tooltip, painted last, over everything: DIR.Lib's router says which hover text is due and where it is
/// anchored, and <see cref="Layout.Builder.Tooltip(TooltipRequest, Layout.TooltipStyle)"/> is its one painter. Without it
/// every <c>WithTooltip</c> in the window was dead: a card's whole title, the rail's page names, the view switch's words
/// (SE1, 2026-10-25). It registers no region, so painting it changes nothing the router resolves.
/// </summary>
internal sealed class TooltipLayer<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private static readonly Layout.TooltipStyle Style = new(
        Background: new RGBAColor32(0x24, 0x26, 0x31, 0xf4),
        Text: new RGBAColor32(0xe6, 0xe7, 0xee, 0xff),
        Border: new RGBAColor32(0x3a, 0x3d, 0x4c, 0xff),
        FontSize: 12f);

    /// <summary>Paints <paramref name="request"/> in <paramref name="window"/>, or nothing when none is due.</summary>
    public void Render(TooltipRequest? request, RectF32 window)
    {
        BeginFrame();
        if (request is { } due)
        {
            RenderLayout(Layout.Builder.Tooltip(due, Style), window);
        }
    }

    public override bool HandleInput(InputEvent evt) => false;
}
