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

    private static readonly Layout.NavigationStripColors Colors = new(
        Fill: new RGBAColor32(0x16, 0x17, 0x1e, 0xff),
        HoverFill: new RGBAColor32(0x22, 0x24, 0x2e, 0xff),
        Ink: new RGBAColor32(0xe6, 0xe7, 0xee, 0xff),
        DisabledInk: new RGBAColor32(0x4a, 0x4d, 0x5a, 0xff));

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
