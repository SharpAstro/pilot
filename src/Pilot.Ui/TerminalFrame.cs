using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// pilot-tui's whole frame: the page bar's row above the page, and the cards panel beside the page when there is one. Program.cs
/// arranges this tree each frame, and the tests arrange the same one, so what a test checks is what the terminal gets.
/// </summary>
internal static class TerminalFrame
{
    /// <summary>The fewest columns at which the cards panel stands beside the page.</summary>
    public const int CardsPanelColumns = 120;

    /// <summary>The columns the page itself has: the terminal's, less the cards panel's when it stands beside the page.</summary>
    public static int PageColumns(int columns, bool cardsShown)
        => cardsShown ? columns - CardsView<RgbaImage>.Width : columns;

    /// <summary>
    /// The frame's tree.<paramref name="cards"/> is the cards panel's tree, or null for none (a narrow terminal, nothing to show).
    /// </summary>
    public static Layout.Node Tree(Layout.Node rail, Layout.Node page, Layout.Node? cards)
    {
        // The panel takes 38 columns (the GUI's 300 units) beside the page.
        var beside = cards is not null
            ? Layout.Builder.HStack(page.WStar(), cards.WFixed(CardsView<RgbaImage>.Width)).Stretch()
            : page;
        // The bar takes the row above the page, as the page needs the width: one row (its RowH), and the page the rest.
        return Layout.Builder.VStack(rail, beside.HStar()).Bg(Palette.Current.Ground).Stretch();
    }

    /// <summary>
    /// The arranged tree with every node cropped, or dropped, to the rect it belongs in: a node under a scroll container stays inside the
    /// container's bounds, and the page and the cards panel stay inside their own (the frame's children, depth 2). Console.Lib's
    /// CellLayout.Paint has no clip, so a transcript taller than its list painted over the bar, the page's header and the cards panel
    /// (#1036). A node wholly outside its clip is dropped with its subtree; a text leaf only partly inside is dropped too, as cropping
    /// its rect would wrap it again; anything else is cropped. The result is what both the painter and the hit test read, so a click on
    /// a cell hits what is painted there. A clip in Console.Lib itself is a follow-up in that sibling.
    /// </summary>
    public static ImmutableArray<Layout.ArrangedNode<int>> Clip(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        var result = ImmutableArray.CreateBuilder<Layout.ArrangedNode<int>>(arranged.Length);
        var clips = new Stack<(int Depth, Rect<int> Clip)>();
        var droppedAt = int.MaxValue;
        foreach (var a in arranged)
        {
            if (a.Depth > droppedAt)
            {
                continue;
            }
            droppedAt = int.MaxValue;
            while (clips.Count > 0 && clips.Peek().Depth >= a.Depth)
            {
                clips.Pop();
            }
            var b = a.Bounds;
            if (clips.Count > 0)
            {
                var c = clips.Peek().Clip;
                var x0 = Math.Max(b.X, c.X);
                var y0 = Math.Max(b.Y, c.Y);
                var x1 = Math.Min(b.X + b.Width, c.X + c.Width);
                var y1 = Math.Min(b.Y + b.Height, c.Y + c.Height);
                var empty = x1 <= x0 || y1 <= y0;
                var cropped = !empty && (x0 != b.X || y0 != b.Y || x1 - x0 != b.Width || y1 - y0 != b.Height);
                if (empty || (cropped && a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }))
                {
                    droppedAt = a.Depth;
                    continue;
                }
                if (cropped)
                {
                    b = new Rect<int>(x0, y0, x1 - x0, y1 - y0);
                }
            }
            result.Add(a with { Bounds = b });
            if (a.Node.Scroll is not null || a.Depth == 2)
            {
                clips.Push((a.Depth, b));
            }
        }
        return result.ToImmutable();
    }
}
