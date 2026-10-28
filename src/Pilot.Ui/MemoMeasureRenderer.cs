using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// Wraps a renderer and remembers what <see cref="MeasureText"/> answered for a text, font and size, so a leaf the layout asks
/// about several times in a frame (and every frame it is unchanged) costs a lookup, whichever DIR.Lib version fits it. Drawing and
/// clipping pass straight through to the wrapped renderer. Measurements are cleared when the table grows past <see cref="Limit"/>.
/// </summary>
public sealed class MemoMeasureRenderer<TSurface> : Renderer<TSurface>
{
    /// <summary>How many measured texts are kept before the table starts over.</summary>
    internal const int Limit = 8192;

    private readonly Renderer<TSurface> _inner;
    private readonly Dictionary<(string Font, float Size), Dictionary<string, (float Width, float Height)>> _measured = [];
    private int _count;

    public MemoMeasureRenderer(Renderer<TSurface> inner) : base(inner.Surface)
    {
        _inner = inner;
        TextShaper = inner.TextShaper;
        HostRendersSelectableText = inner.HostRendersSelectableText;
    }

    public override uint Width => _inner.Width;

    public override uint Height => _inner.Height;

    public override ContentTransform ContentTransform
    {
        get => _inner.ContentTransform;
        set => _inner.ContentTransform = value;
    }

    public override (float Width, float Height) MeasureText(ReadOnlySpan<char> text, string fontFamily, float fontSize)
    {
        if (!_measured.TryGetValue((fontFamily, fontSize), out var table))
        {
            _measured[(fontFamily, fontSize)] = table = [];
        }

        var lookup = table.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out var size))
        {
            return size;
        }

        size = _inner.MeasureText(text, fontFamily, fontSize);
        if (++_count > Limit)
        {
            _measured.Clear();
            _count = 0;
            _measured[(fontFamily, fontSize)] = table = [];
            lookup = table.GetAlternateLookup<ReadOnlySpan<char>>();
        }
        lookup[text] = size;
        return size;
    }

    // A measure is a function of (font, size, text) alone, so a resize or DPI change (which arrives as a different size key)
    // never makes the inner renderer answer differently for the same key: the memo needs no clearing on resize. The window's
    // loop resizes the bare VkRenderer anyway, so this override never runs there; it only forwards.
    public override void Resize(uint width, uint height) => _inner.Resize(width, height);

    public override void DrawRectangle(in RectInt rect, RGBAColor32 strokeColor, int strokeWidth) => _inner.DrawRectangle(in rect, strokeColor, strokeWidth);

    public override void FillRectangle(in RectInt rect, RGBAColor32 fillColor) => _inner.FillRectangle(in rect, fillColor);

    public override void FillEllipse(in RectInt rect, RGBAColor32 fillColor) => _inner.FillEllipse(in rect, fillColor);

    public override void DrawTriangles(ReadOnlySpan<float> vertices, RGBAColor32 color) => _inner.DrawTriangles(vertices, color);

    protected override void ApplyClip(in RectInt rect)
    {
        ClearInner();
        _inner.PushClip(in rect);
    }

    protected override void ClearClip() => ClearInner();

    private void ClearInner()
    {
        while (_inner.ClipDepth > 0)
        {
            _inner.PopClip();
        }
    }

    public override void DrawEllipse(in RectInt rect, RGBAColor32 strokeColor, float strokeWidth) => _inner.DrawEllipse(in rect, strokeColor, strokeWidth);

    public override void FillEllipse((float, float) c00, (float, float) c10, (float, float) c11, (float, float) c01, RGBAColor32 fillColor) =>
        _inner.FillEllipse(c00, c10, c11, c01, fillColor);

    public override void DrawEllipse((float, float) c00, (float, float) c10, (float, float) c11, (float, float) c01, RGBAColor32 strokeColor, float strokeWidth) =>
        _inner.DrawEllipse(c00, c10, c11, c01, strokeColor, strokeWidth);

    public override void DrawLine(float x0, float y0, float x1, float y1, RGBAColor32 color, int thickness) => _inner.DrawLine(x0, y0, x1, y1, color, thickness);

    public override void DrawLineDashed(float x0, float y0, float x1, float y1, RGBAColor32 color, float dashLength, float gapLength, int thickness) =>
        _inner.DrawLineDashed(x0, y0, x1, y1, color, dashLength, gapLength, thickness);

    public override void DrawPolyline(ReadOnlySpan<(float, float)> points, RGBAColor32 color, int thickness) => _inner.DrawPolyline(points, color, thickness);

    public override void DrawPolylineDashed(ReadOnlySpan<(float, float)> points, RGBAColor32 color, float dashLength, float gapLength, int thickness) =>
        _inner.DrawPolylineDashed(points, color, dashLength, gapLength, thickness);

    public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize, RGBAColor32 fontColor, in RectInt layout, TextAlign horizAlignment, TextAlign vertAlignment) =>
        _inner.DrawText(text, fontFamily, fontSize, fontColor, in layout, horizAlignment, vertAlignment);

    public override void FillRoundedRectangle(in RectInt rect, RGBAColor32 fillColor, float cornerRadius) => _inner.FillRoundedRectangle(in rect, fillColor, cornerRadius);

    public override void FillRectangles(ReadOnlySpan<(RectInt, RGBAColor32)> rectangles) => _inner.FillRectangles(rectangles);

    public override void DrawScrim(in RectInt bounds, RGBAColor32 scrimColor) => _inner.DrawScrim(in bounds, scrimColor);

    /// <summary>The wrapped renderer is the caller's to dispose.</summary>
    public override void Dispose()
    {
    }
}
