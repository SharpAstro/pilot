using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>
/// Plain draw calls at an origin and a scale: the one place the cat's shapes become renderer calls. Coordinates are in design pixels (the
/// design's 200 x 160 cat box), <see cref="Scale"/> maps them to device pixels, so the same drawing serves a laptop and a high-DPI screen.
/// Works on any <see cref="Renderer{TSurface}"/>: the CPU <c>RgbaImageRenderer</c> in the tests, the Vulkan one in the window.
/// </summary>
public sealed class Pen<TSurface>(Renderer<TSurface> renderer, string fontPath, float originX, float originY, float scale)
{
    /// <summary>The renderer the shapes go to.</summary>
    public Renderer<TSurface> Renderer { get; } = renderer;

    /// <summary>The UI face's path, as the renderer takes a family.</summary>
    public string FontPath { get; } = fontPath;

    /// <summary>Device pixels per design pixel.</summary>
    public float Scale { get; } = scale;

    /// <summary>This pen's origin (design 0,0) in device pixels.</summary>
    public (float X, float Y) Origin { get; } = (originX, originY);

    /// <summary>The same drawing moved by <paramref name="dx"/>, <paramref name="dy"/> design pixels.</summary>
    public Pen<TSurface> Moved(float dx, float dy) => new(Renderer, FontPath, Origin.X + dx * Scale, Origin.Y + dy * Scale, Scale);

    /// <summary>The same drawing at another scale, same origin.</summary>
    public Pen<TSurface> Scaled(float factor) => new(Renderer, FontPath, Origin.X, Origin.Y, Scale * factor);

    private int X(float x) => (int)MathF.Round(Origin.X + x * Scale);

    private int Y(float y) => (int)MathF.Round(Origin.Y + y * Scale);

    private int D(float d) => Math.Max(1, (int)MathF.Round(d * Scale));

    /// <summary>The device rectangle of a design rectangle.</summary>
    public RectInt Rect(float x, float y, float w, float h) => new(new PointInt(X(x + w), Y(y + h)), new PointInt(X(x), Y(y)));

    /// <summary>A filled, optionally outlined, rounded rectangle: the outline is the same shape one step bigger, so the two share a corner.</summary>
    public void Box(float x, float y, float w, float h, RGBAColor32 fill, float radius = 0, RGBAColor32? border = null, float borderWidth = 0)
    {
        if (border is { } edge && borderWidth > 0)
        {
            Renderer.FillRoundedRectangle(Rect(x, y, w, h), edge, radius * Scale);
            var inset = MathF.Max(1f / Scale, borderWidth);
            Renderer.FillRoundedRectangle(Rect(x + inset, y + inset, w - 2 * inset, h - 2 * inset), fill, MathF.Max(0, radius - inset) * Scale);
            return;
        }
        Renderer.FillRoundedRectangle(Rect(x, y, w, h), fill, radius * Scale);
    }

    /// <summary>A filled, optionally outlined, ellipse inscribed in the rectangle.</summary>
    public void Oval(float x, float y, float w, float h, RGBAColor32 fill, RGBAColor32? border = null, float borderWidth = 0)
    {
        if (border is { } edge && borderWidth > 0)
        {
            Renderer.FillEllipse(Rect(x, y, w, h), edge);
            Renderer.FillEllipse(Rect(x + borderWidth, y + borderWidth, w - 2 * borderWidth, h - 2 * borderWidth), fill);
            return;
        }
        Renderer.FillEllipse(Rect(x, y, w, h), fill);
    }

    /// <summary>A line of <paramref name="width"/> design pixels.</summary>
    public void Line(float x0, float y0, float x1, float y1, RGBAColor32 color, float width = 1)
        => Renderer.DrawLine(X(x0), Y(y0), X(x1), Y(y1), color, D(width));

    /// <summary>A connected line through the points.</summary>
    public void Polyline(RGBAColor32 color, float width, params (float X, float Y)[] points)
    {
        var device = new (float X, float Y)[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            device[i] = (X(points[i].X), Y(points[i].Y));
        }
        Renderer.DrawPolyline(device, color, D(width));
    }

    /// <summary>Text in the rectangle, from its top left; <paramref name="size"/> is in design pixels.</summary>
    public void Text(string text, float x, float y, float w, float h, float size, RGBAColor32 color, TextAlign align = TextAlign.Near, TextAlign vertical = TextAlign.Near)
        => Renderer.DrawText(text, FontPath, size * Scale, color, Rect(x, y, w, h), align, vertical);

    /// <summary>The width and line height of <paramref name="text"/> at <paramref name="size"/>, in design pixels.</summary>
    public (float Width, float Height) Measure(string text, float size)
    {
        var (w, h) = Renderer.MeasureText(text, FontPath, size * Scale);
        return (w / Scale, h / Scale);
    }
}
