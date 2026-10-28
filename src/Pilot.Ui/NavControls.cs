using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The window's Back and Forward, a strip at the top of the rail: each steps the chrome's navigation history, and is
/// disabled (dimmed, its press swallowed) when there is no place to go to. The strip itself is DIR.Lib's
/// (<see cref="Layout.Builder.NavigationStrip"/>), the same one tianwen draws; this only gives it pilot's palette.
/// </summary>
internal sealed class NavControls<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>Design units, as the rail's.</summary>
    public const float Height = 34f;

    // From the palette, so Night recolours it as the rest of the chrome (PIL406).
    private static Layout.NavigationStripColors Colors => new(
        Fill: Palette.Current.RowAltBg,
        HoverFill: Palette.Current.RowHoverBg,
        Ink: Palette.Current.Bright,
        DisabledInk: Palette.Current.NavControlsFaint);

    public Func<bool>? CanGoBack { get; set; }

    public Func<bool>? CanGoForward { get; set; }

    public Action? OnBack { get; set; }

    public Action? OnForward { get; set; }

    public void Render(RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(), bounds);
    }

    internal Layout.Node Tree() =>
        Layout.Builder.NavigationStrip(CanGoBack?.Invoke() ?? false, CanGoForward?.Invoke() ?? false, OnBack, OnForward, Colors);
}
