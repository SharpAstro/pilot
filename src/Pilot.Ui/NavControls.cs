using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The window's Back and Forward, a strip at the top of the rail: each steps the chrome's navigation history, and is
/// disabled (dimmed, its press swallowed) when there is no place to go to.
/// </summary>
internal sealed class NavControls<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>Design units, as the rail's.</summary>
    public const float Height = 34f;

    private static readonly RGBAColor32 RailBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 Hover = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Faint = new(0x4a, 0x4d, 0x5a, 0xff);

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
        Layout.Builder.HStack(
                Control("←", "nav:back", "Back (Alt+Left)", CanGoBack?.Invoke() ?? false, OnBack),
                Control("→", "nav:forward", "Forward (Alt+Right)", CanGoForward?.Invoke() ?? false, OnForward))
            .Bg(RailBg).Stretch();

    private static Layout.Node Control(string glyph, string action, string tooltip, bool enabled, Action? go)
    {
        var node = Layout.Builder.Text(glyph, 16f, enabled ? Bright : Faint, TextAlign.Center).Pad(0f, 8f).WStar().BgHover(Hover)
            .Follows(new HitResult.ButtonHit(action), _ => go?.Invoke())
            .WithTooltip(tooltip);
        return enabled ? node : node.Disabled(tooltip);
    }
}
