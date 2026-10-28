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
/// </summary>
internal static class TextOverlap
{
    /// <summary>Pixels two texts may share without it counting: the rounding of a measure, as the chat's row tests allowed.</summary>
    public const float Tolerance = 0.5f;

    private readonly record struct Leaf(string Text, Rect<float> Bounds, int Scroll, Rect<float> Visible);

    /// <summary>
    /// Every fault in <paramref name="arranged"/>: two non-empty text leaves sharing a pixel, and a text leaf outside the
    /// viewport of its enclosing scroll (<see cref="Layout.Node.Scroll"/>) or, with no scroll around it, outside the page. A scroll
    /// slides its rows, so a row scrolled out of view is neither: only text that shows is held against what it shows beside, and two
    /// rows of one scroll are held apart wherever they are.
    /// </summary>
    public static IReadOnlyList<TextFault> Faults<T>(ImmutableArray<Layout.ArrangedNode<T>> arranged, float tolerance = Tolerance)
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
        for (var i = 0; i < arranged.Length; i++)
        {
            var (node, bounds) = arranged[i];
            var depth = arranged[i].Depth;
            while (scrolls.Count > 0 && scrolls.Peek().Depth >= depth)
            {
                scrolls.Pop();
            }

            var rect = Rectangle(bounds);
            if (node is Layout.Node.Leaf { Content: Layout.Content.Text { Value.Length: > 0 } text })
            {
                if (string.IsNullOrWhiteSpace(text.Value))
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

                leaves.Add(new Leaf(text.Value, rect, inside.Index, visible));
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
