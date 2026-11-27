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

    /// <summary>
    /// A filled, optionally outlined, convex polygon (counter-clockwise or clockwise). The outline is the same polygon pushed out by
    /// <paramref name="borderWidth"/> at every edge, drawn first, so the fill sits inside it like <see cref="Box"/>'s.
    /// </summary>
    public void Polygon((float X, float Y)[] points, RGBAColor32 fill, RGBAColor32? border = null, float borderWidth = 0)
    {
        if (border is { } edge && borderWidth > 0)
        {
            FillConvex(Grow(points, borderWidth), edge);
        }
        FillConvex(points, fill);
    }

    private void FillConvex((float X, float Y)[] points, RGBAColor32 color)
    {
        // The pinned renderer has no polygon or triangle fill, so a convex shape is filled one device row at a time: each row's span is where
        // the row's middle meets the polygon's edges.
        var n = points.Length;
        var dev = new (float X, float Y)[n];
        var top = float.MaxValue;
        var bottom = float.MinValue;
        for (var i = 0; i < n; i++)
        {
            dev[i] = (Origin.X + points[i].X * Scale, Origin.Y + points[i].Y * Scale);
            top = MathF.Min(top, dev[i].Y);
            bottom = MathF.Max(bottom, dev[i].Y);
        }
        for (var row = (int)MathF.Floor(top); row < (int)MathF.Ceiling(bottom); row++)
        {
            var yMid = row + 0.5f;
            var left = float.MaxValue;
            var right = float.MinValue;
            for (var i = 0; i < n; i++)
            {
                var (a, b) = (dev[i], dev[(i + 1) % n]);
                if ((a.Y <= yMid) == (b.Y <= yMid))
                {
                    continue;
                }
                var x = a.X + (yMid - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                left = MathF.Min(left, x);
                right = MathF.Max(right, x);
            }
            if (right > left)
            {
                var x0 = (int)MathF.Round(left);
                var x1 = Math.Max(x0 + 1, (int)MathF.Round(right));
                Renderer.FillRectangle(new RectInt(new PointInt(x1, row + 1), new PointInt(x0, row)), color);
            }
        }
    }

    /// <summary>The polygon with each edge moved out by <paramref name="by"/> (mitred corners).</summary>
    private static (float X, float Y)[] Grow((float X, float Y)[] points, float by)
    {
        var n = points.Length;
        var area = 0f;
        for (var i = 0; i < n; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % n]);
            area += a.X * b.Y - b.X * a.Y;
        }
        var sign = area > 0 ? 1f : -1f;
        var grown = new (float X, float Y)[n];
        for (var i = 0; i < n; i++)
        {
            var prev = points[(i + n - 1) % n];
            var cur = points[i];
            var next = points[(i + 1) % n];
            var n1 = Normal(prev, cur, sign);
            var n2 = Normal(cur, next, sign);
            var mx = n1.X + n2.X;
            var my = n1.Y + n2.Y;
            // Miter: the offset along the bisector is by / cos(half the turn), capped so a sharp tip does not run away.
            var dot = MathF.Max(0.35f, 1 + n1.X * n2.X + n1.Y * n2.Y);
            grown[i] = (cur.X + mx * by / dot, cur.Y + my * by / dot);
        }
        return grown;
    }

    private static (float X, float Y) Normal((float X, float Y) a, (float X, float Y) b, float sign)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        return (sign * dy / len, -sign * dx / len);
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

    /// <summary>
    /// Told every text a pen draws on this thread, while set: the window sets it around its CPU pass so the glyphs the scene is about to draw on the GPU are known
    /// and warmed before that frame is presented (<see cref="BuddyGlyphWarmer"/>). Thread-static, so the host's one draw thread and a parallel test do not meet.
    /// </summary>
    [ThreadStatic]
    public static Action<string>? TextSeen;

    /// <summary>Text in the rectangle, from its top left; <paramref name="size"/> is in design pixels.</summary>
    public void Text(string text, float x, float y, float w, float h, float size, RGBAColor32 color, TextAlign align = TextAlign.Near, TextAlign vertical = TextAlign.Near)
        => DrawText(text, size, color, Rect(x, y, w, h), align, vertical);

    private void DrawText(string text, float size, RGBAColor32 color, RectInt rect, TextAlign align, TextAlign vertical)
    {
        TextSeen?.Invoke(text);
        Renderer.DrawText(text, FontPath, size * Scale, color, rect, align, vertical);
    }

    /// <summary>The width and line height of <paramref name="text"/> at <paramref name="size"/>, in design pixels.</summary>
    public (float Width, float Height) Measure(string text, float size)
    {
        var (w, h) = Renderer.MeasureText(text, FontPath, size * Scale);
        return (w / Scale, h / Scale);
    }
}
