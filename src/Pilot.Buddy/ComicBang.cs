using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>Which stroke the comic "!" uses.</summary>
public enum BangStroke
{
    /// <summary>Wide at the top, narrower at the bottom, with a flat end.</summary>
    Trapezium,

    /// <summary>Wide at the top, a point at the bottom.</summary>
    Triangle,
}

/// <summary>
/// The comic-strip exclamation mark drawn as shapes, not a font glyph: a tapering stroke over a round dot, both filled with the alert yellow and
/// outlined in the cat's ink, like Grump's own shapes. The yellow carries on a dark desktop and the ink outline on a light one, so it reads at 3:1
/// on both. One shape, shared (alert Grump uses it; the bubble's small ask icon keeps its amber triangle, which reads at 16 px where this would not).
/// </summary>
public static class ComicBang
{
    /// <summary>The alert yellow, the same on a light and a dark desktop: the outline is what reads on a light one.</summary>
    public static readonly RGBAColor32 Fill = new(0xff, 0xc2, 0x33, 0xff);

    /// <summary>The outline: the cat's ink.</summary>
    public static RGBAColor32 Outline => GrumpCat.Ink;

    /// <summary>Outline width in design pixels.</summary>
    public const float OutlineWidth = 2.5f;

    /// <summary>The design size of the mark: it fits this box, outline included.</summary>
    public const float Width = 24, Height = 36;

    /// <summary>
    /// Draws the mark inside the box whose top left is (<paramref name="x"/>, <paramref name="y"/>) and which is <see cref="Width"/> x <see cref="Height"/>,
    /// outline included, so nothing leaves the box.
    /// </summary>
    public static void Draw<TSurface>(Pen<TSurface> pen, float x, float y, BangStroke stroke = BangStroke.Trapezium)
    {
        var o = OutlineWidth;
        var cx = x + Width / 2;
        var top = y + o;
        var strokeH = 19f;
        var topHalf = 8f;
        (float X, float Y)[] shape = stroke == BangStroke.Trapezium
            ? [(cx - topHalf, top), (cx + topHalf, top), (cx + 4f, top + strokeH), (cx - 4f, top + strokeH)]
            : [(cx - topHalf, top), (cx + topHalf, top), (cx, top + strokeH)];
        pen.Polygon(shape, Fill, Outline, o);
        var d = 8f;
        pen.Oval(cx - d / 2, top + strokeH + 3.5f, d, d, Fill, Outline, o);
    }
}
