using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>Marks the text of a tree built elsewhere (a Markdown document) selectable, as <see cref="Layout.Node.Selectable"/> does for one leaf.</summary>
internal static class SelectableTree
{
    /// <summary>
    /// <paramref name="node"/> with every text leaf in it marked selectable, and the rest of it as it was: DIR.Lib's Markdown builds
    /// its paragraphs, list items and code blocks from plain text leaves and has no switch for it, so the page marks them after.
    /// <paramref name="breakLongWords"/> also cuts a long word of a paragraph (a path, a check's name, a code span) after its
    /// separators, as <see cref="WrappedProse"/> does: Markdown wraps at white space only, so such a word would run past the box.
    /// <paramref name="selectable"/> false leaves the text as it was marked, for a page whose selection is its own and wants only the cut.
    /// </summary>
    public static Layout.Node Of(Layout.Node node, bool breakLongWords = false, bool selectable = true) => node switch
    {
        Layout.Node.Leaf { Content: Layout.Content.Text } leaf => selectable ? leaf.Selectable() : leaf,
        Layout.Node.Stack stack => stack with { Children = Each(stack.Children, breakLongWords, selectable) },
        Layout.Node.Wrap wrap => wrap with { Children = breakLongWords ? BreakLong(Each(wrap.Children, breakLongWords, selectable)) : Each(wrap.Children, breakLongWords, selectable) },
        Layout.Node.Grid grid => grid with { Cells = Each(grid.Cells, breakLongWords, selectable) },
        Layout.Node.Dock dock => dock with
        {
            Docked = [.. dock.Docked.Select(d => d with { Child = Of(d.Child, breakLongWords, selectable) })],
            Fill = Of(dock.Fill, breakLongWords, selectable),
        },
        Layout.Node.Overlay overlay => overlay with { Base = Of(overlay.Base, breakLongWords, selectable), Top = Of(overlay.Top, breakLongWords, selectable) },
        Layout.Node.Split split => split with { First = Of(split.First, breakLongWords, selectable), Second = Of(split.Second, breakLongWords, selectable) },
        Layout.Node.Anchored anchored => anchored with { Child = Of(anchored.Child, breakLongWords, selectable) },
        _ => node,
    };

    private static ImmutableArray<Layout.Node> Each(ImmutableArray<Layout.Node> nodes, bool breakLongWords, bool selectable) => [.. nodes.Select(n => Of(n, breakLongWords, selectable))];

    /// <summary>
    /// The words of a paragraph's line, a long one (<see cref="WrappedProse.LongWord"/> characters or more, its styled parts
    /// together) in pieces the wrap can break between: a word is one leaf, or a row of leaves for parts styled differently, and
    /// each leaf is cut into one leaf per piece with its colour, size and link kept, so the pieces run on with no space the word
    /// did not have and the copy of them is the word.
    /// </summary>
    private static ImmutableArray<Layout.Node> BreakLong(ImmutableArray<Layout.Node> words)
    {
        var result = ImmutableArray.CreateBuilder<Layout.Node>(words.Length);
        foreach (var word in words)
        {
            ImmutableArray<Layout.Node> parts = word switch
            {
                Layout.Node.Leaf { Content: Layout.Content.Text } leaf => [leaf],
                Layout.Node.Stack { Axis: Layout.Axis.Horizontal } row when row.Children.All(c => c is Layout.Node.Leaf { Content: Layout.Content.Text }) => row.Children,
                _ => default,
            };
            if (parts.IsDefaultOrEmpty || parts.Sum(p => ((Layout.Content.Text)((Layout.Node.Leaf)p).Content).Value.TrimEnd().Length) < WrappedProse.LongWord)
            {
                result.Add(word);
                continue;
            }
            foreach (var part in parts)
            {
                var leaf = (Layout.Node.Leaf)part;
                var run = (Layout.Content.Text)leaf.Content;
                foreach (var piece in WrappedProse.Break(run.Value))
                {
                    result.Add(leaf with { Content = run with { Value = piece } });
                }
            }
        }
        return result.ToImmutable();
    }
}
