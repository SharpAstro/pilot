using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>A page as drawn: RGBA rows, top to bottom, <paramref name="Width"/> by <paramref name="Height"/> pixels.</summary>
internal sealed record PageImage(byte[] Rgba, int Width, int Height);

/// <summary>
/// Draws a page's arranged nodes again, off screen on the CPU, for the card a live text-over-text fault raises (#423): the same nodes
/// the check found the fault in, painted at the same DPI with the same font, so the capture shows what the window drew. The page's
/// rect is cut out of the window's frame, so the rail and the bar beside it are not in the picture.
/// </summary>
internal sealed class PageCapture : PixelWidgetBase<RgbaImage>
{
    /// <summary>The largest side drawn, in pixels: a page larger than this (a 4K window at DPI 2) is not captured rather than drawn huge.</summary>
    public const int MaxSide = 4096;

    private PageCapture(RgbaImageRenderer renderer) : base(renderer) { }

    /// <summary>The page <paramref name="arranged"/> lays out, painted on <paramref name="ground"/>; null for an empty or oversized page.</summary>
    public static PageImage? Of(ImmutableArray<Layout.ArrangedNode<float>> arranged, string? fontPath, float dpi, RGBAColor32 ground)
    {
        if (arranged.IsDefaultOrEmpty)
        {
            return null;
        }
        var root = arranged[0].Bounds;
        var (left, top) = ((int)MathF.Floor(root.X), (int)MathF.Floor(root.Y));
        var (right, bottom) = ((int)MathF.Ceiling(root.X + root.Width), (int)MathF.Ceiling(root.Y + root.Height));
        var (width, height) = (right - left, bottom - top);
        if (width <= 0 || height <= 0 || right > MaxSide || bottom > MaxSide)
        {
            return null;
        }
        // The frame is the window's from its origin to the page's far corner, as the arranged rects are in window pixels; the page is cut out after.
        using var renderer = new RgbaImageRenderer((uint)right, (uint)bottom);
        var widget = new PageCapture(renderer) { DpiScale = dpi };
        if (fontPath is { Length: > 0 })
        {
            widget.FontPath = fontPath;
        }
        renderer.FillRectangle(new RectInt(new PointInt(right, bottom), new PointInt(0, 0)), ground);
        widget.PaintLayout(arranged);
        var frame = renderer.Surface.Pixels;
        var page = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(frame, ((top + y) * right + left) * 4, page, y * width * 4, width * 4);
        }
        return new PageImage(page, width, height);
    }
}
