using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>Marks the text of a tree built elsewhere (a Markdown document) selectable, as <see cref="Layout.Node.Selectable"/> does for one leaf.</summary>
internal static class SelectableTree
{
    /// <summary>
    /// <paramref name="node"/> with every text leaf in it marked selectable, and the rest of it as it was: DIR.Lib's Markdown builds
    /// its paragraphs, list items and code blocks from plain text leaves and has no switch for it, so the page marks them after.
    /// </summary>
    public static Layout.Node Of(Layout.Node node) => node switch
    {
        Layout.Node.Leaf { Content: Layout.Content.Text } leaf => leaf.Selectable(),
        Layout.Node.Stack stack => stack with { Children = Each(stack.Children) },
        Layout.Node.Wrap wrap => wrap with { Children = Each(wrap.Children) },
        Layout.Node.Grid grid => grid with { Cells = Each(grid.Cells) },
        Layout.Node.Dock dock => dock with
        {
            Docked = [.. dock.Docked.Select(d => d with { Child = Of(d.Child) })],
            Fill = Of(dock.Fill),
        },
        Layout.Node.Overlay overlay => overlay with { Base = Of(overlay.Base), Top = Of(overlay.Top) },
        Layout.Node.Split split => split with { First = Of(split.First), Second = Of(split.Second) },
        Layout.Node.Anchored anchored => anchored with { Child = Of(anchored.Child) },
        _ => node,
    };

    private static ImmutableArray<Layout.Node> Each(ImmutableArray<Layout.Node> nodes) => [.. nodes.Select(Of)];
}
