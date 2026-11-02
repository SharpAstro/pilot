using System.Collections.Immutable;
using System.Numerics;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>What is wrong with a piece of text a page draws.</summary>
internal enum TextFaultKind
{
    /// <summary>Two text leaves share a pixel: one is drawn over the other.</summary>
    Overlap,

    /// <summary>A text leaf reaches outside the viewport of the scroll that holds it (or the page, where none does): the clip cuts it.</summary>
    Outside,
}

/// <summary>
/// One fault <see cref="TextOverlap.Faults"/> found: the text, the other text it is drawn over (for <see cref="TextFaultKind.Overlap"/>)
/// or null, and the rects. <see cref="Line"/> is the one line tests and the live GUI both show.
/// </summary>
internal sealed record TextFault(TextFaultKind Kind, string Text, Rect<float> Bounds, string? Other, Rect<float> OtherBounds)
{
    /// <summary>Short and the same everywhere: <c>overlap: 'a' (x,y wxh) is drawn over 'b' (x,y wxh)</c>, or <c>outside: 'a' (x,y wxh) reaches past the scroll's or page's (x,y wxh)</c>.</summary>
    public string Line => Kind == TextFaultKind.Overlap
        ? $"overlap: '{Shorten(Text)}' {Show(Bounds)} is drawn over '{Shorten(Other ?? "")}' {Show(OtherBounds)}"
        : $"outside: '{Shorten(Text)}' {Show(Bounds)} reaches past its {(Other ?? "page")} {Show(OtherBounds)}";

    public override string ToString() => Line;

    private static string Show(Rect<float> r) => $"({r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#})";

    private static string Shorten(string text)
    {
        var one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 40 ? one : one[..39] + "…";
    }
}

/// <summary>
/// The one check that a page's text is not drawn over other text, shared by the tests and the live GUI: it reads the arranged
/// nodes (<see cref="Layout.Engine.Arrange"/>), so it sees what the painter is given and nothing else.
/// <para>
/// A popover hides what it covers: text of an <see cref="Layout.Node.Overlay"/>'s base that lies within a node of its top layer
/// with an opaque <see cref="Layout.Node.Background"/> is not drawn (a cover across the text's whole height or width trims the strip it reaches), so it is neither an overlap nor compared further. Text of the
/// top layer is still held against its own layer and against base text no opaque background covers (a bar's corner text drawn
/// over its header, with no background, is a fault).
/// </para>
/// </summary>
internal static class TextOverlap
{
    /// <summary>Pixels two texts may share without it counting: the rounding of a measure, as the chat's row tests allowed.</summary>
    public const float Tolerance = 0.5f;

    private readonly record struct Leaf(string Text, Rect<float> Bounds, int Scroll, Rect<float> Visible, Rect<float> Shown);

    // An Overlay the walk is inside: its depth, where its base's leaves start, where its top's start (-1 before the top), and children seen.
    private readonly record struct OverlayWalk(int Depth, int BaseStart, int TopStart, int Children);

    // What of `shown` an opaque `cover` leaves: a cover that spans the text's whole height (or width) cuts a strip off the side it
    // reaches, or hides it all; one that only dents it hides nothing (the text is held to its whole box).
    private static Rect<float> Uncovered(Rect<float> shown, Rect<float> cover, float tol)
    {
        if (shown.Width <= 0 || shown.Height <= 0)
        {
            return shown;
        }

        var (sr, sb, cr, cb) = (shown.X + shown.Width, shown.Y + shown.Height, cover.X + cover.Width, cover.Y + cover.Height);
        if (cover.Y <= shown.Y + tol && cb >= sb - tol)
        {
            if (cover.X <= shown.X + tol && cr >= sr - tol) return new Rect<float>(shown.X, shown.Y, 0, 0);
            if (cover.X <= shown.X + tol && cr > shown.X) return new Rect<float>(cr, shown.Y, sr - cr, shown.Height);
            if (cr >= sr - tol && cover.X < sr) return new Rect<float>(shown.X, shown.Y, cover.X - shown.X, shown.Height);
        }
        else if (cover.X <= shown.X + tol && cr >= sr - tol)
        {
            if (cover.Y <= shown.Y + tol && cb > shown.Y) return new Rect<float>(shown.X, cb, shown.Width, sb - cb);
            if (cb >= sb - tol && cover.Y < sb) return new Rect<float>(shown.X, shown.Y, shown.Width, cover.Y - shown.Y);
        }

        return shown;
    }

    private static Rect<float> Rectangle<T>(Rect<T> r) where T : INumber<T> =>
        new(float.CreateTruncating(r.X), float.CreateTruncating(r.Y), float.CreateTruncating(r.Width), float.CreateTruncating(r.Height));

    private static Rect<float> Intersect(Rect<float> a, Rect<float> b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new Rect<float>(x, y, Math.Max(0f, right - x), Math.Max(0f, bottom - y));
    }
}
