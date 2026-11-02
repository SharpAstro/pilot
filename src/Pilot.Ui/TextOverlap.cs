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

    /// <summary>
    /// Every fault in <paramref name="arranged"/>: two non-empty text leaves sharing a pixel, and a text leaf outside the
    /// viewport of its enclosing scroll (<see cref="Layout.Node.Scroll"/>) or, with no scroll around it, outside the page. A scroll
    /// slides its rows, so a row scrolled out of view is neither: only text that shows is held against what it shows beside, and two
    /// rows of one scroll are held apart wherever they are. Only the first <paramref name="maxLeaves"/> text leaves are read (the
    /// live GUI bounds its work by it): the pairs compared grow with the square of them.
    /// </summary>
    public static IReadOnlyList<TextFault> Faults<T>(ImmutableArray<Layout.ArrangedNode<T>> arranged, float tolerance = Tolerance, int maxLeaves = int.MaxValue)
        where T : INumber<T>
    {
        var faults = new List<TextFault>();
        if (arranged.IsDefaultOrEmpty)
        {
            return faults;
        }

        var root = Rectangle(arranged[0].Bounds);
        // The scrolls the walk is inside: the arranged index, the viewport, and what shows of it through the ones around it.
        var scrolls = new Stack<(int Depth, int Index, Rect<float> Viewport, Rect<float> Shown)>();
        var leaves = new List<Leaf>();
        var overlays = new Stack<OverlayWalk>();
        for (var i = 0; i < arranged.Length; i++)
        {
            var (node, bounds) = arranged[i];
            var depth = arranged[i].Depth;
            while (scrolls.Count > 0 && scrolls.Peek().Depth >= depth)
            {
                scrolls.Pop();
            }

            var rect = Rectangle(bounds);

            // The overlays the walk is inside: which of its two children (base, then top) this node starts or sits in.
            while (overlays.Count > 0 && overlays.Peek().Depth >= depth)
            {
                overlays.Pop();
            }

            if (overlays.Count > 0 && overlays.Peek().Depth == depth - 1)
            {
                var ov = overlays.Pop();
                ov = ov with { Children = ov.Children + 1 };
                if (ov.Children == 2)
                {
                    ov = ov with { TopStart = leaves.Count };
                }

                overlays.Push(ov);
            }

            // An opaque node in an overlay's top layer hides the base's text it covers: it is not drawn over, it is not drawn.
            if (node.Background is { Alpha: 255 })
            {
                foreach (var ov in overlays)
                {
                    if (ov.TopStart < 0)
                    {
                        continue;
                    }

                    for (var k = ov.BaseStart; k < ov.TopStart; k++)
                    {
                        leaves[k] = leaves[k] with { Shown = Uncovered(leaves[k].Shown, rect, tolerance) };
                    }
                }
            }

            if (node is Layout.Node.Overlay)
            {
                overlays.Push(new OverlayWalk(depth, leaves.Count, -1, 0));
            }

            if (node is Layout.Node.Leaf { Content: Layout.Content.Text { Value.Length: > 0 } text })
            {
                if (string.IsNullOrWhiteSpace(text.Value) || leaves.Count >= maxLeaves)
                {
                    continue;
                }

                var inScroll = scrolls.Count > 0;
                var inside = inScroll ? scrolls.Peek() : (Depth: -1, Index: -1, Viewport: root, Shown: root);
                var visible = Intersect(rect, inside.Shown);
                // A scroll slides its rows along its axis only: across it the clip cuts what is wider than the viewport. With no
                // scroll around it, the page is the clip, on every side. A row scrolled out of view is not drawn, so it is no fault.
                var pastX = rect.X < inside.Viewport.X - tolerance || rect.X + rect.Width > inside.Viewport.X + inside.Viewport.Width + tolerance;
                var pastY = rect.Y < inside.Viewport.Y - tolerance || rect.Y + rect.Height > inside.Viewport.Y + inside.Viewport.Height + tolerance;
                var shows = !inScroll || visible.Width > 0 && visible.Height > 0;
                var cut = !inScroll ? pastX || pastY : ScrollAxisIsVertical(arranged, inside.Index) ? pastX : pastY;
                if (shows && cut)
                {
                    faults.Add(new TextFault(TextFaultKind.Outside, text.Value, rect, inScroll ? "scroll" : "page", inside.Viewport));
                }

                leaves.Add(new Leaf(text.Value, rect, inside.Index, visible, rect));
                continue;
            }

            if (node.Scroll is not null)
            {
                var shown = scrolls.Count > 0 ? Intersect(rect, scrolls.Peek().Shown) : rect;
                scrolls.Push((depth, i, rect, shown));
            }
        }

        for (var a = 0; a < leaves.Count; a++)
        {
            for (var b = a + 1; b < leaves.Count; b++)
            {
                // Rows of one scroll are held apart wherever they sit; across scrolls, only where both show.
                var same = leaves[a].Scroll == leaves[b].Scroll;
                var (ra, rb) = same ? (leaves[a].Bounds, leaves[b].Bounds) : (leaves[a].Visible, leaves[b].Visible);
                ra = Intersect(ra, leaves[a].Shown);
                rb = Intersect(rb, leaves[b].Shown);
                if (ra.Width <= 0 || ra.Height <= 0 || rb.Width <= 0 || rb.Height <= 0)
                {
                    continue;
                }

                if (ra.X < rb.X + rb.Width - tolerance && rb.X < ra.X + ra.Width - tolerance
                    && ra.Y < rb.Y + rb.Height - tolerance && rb.Y < ra.Y + ra.Height - tolerance)
                {
                    faults.Add(new TextFault(TextFaultKind.Overlap, leaves[a].Text, leaves[a].Bounds, leaves[b].Text, leaves[b].Bounds));
                }
            }
        }

        return faults;
    }

    // Whether the scroll node at arranged[index] stacks its rows down the page (the axis it slides along).
    private static bool ScrollAxisIsVertical<T>(ImmutableArray<Layout.ArrangedNode<T>> arranged, int index)
        where T : INumber<T> =>
        arranged[index].Node is not Layout.Node.Stack { Axis: Layout.Axis.Horizontal };

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
