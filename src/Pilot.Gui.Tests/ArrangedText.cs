using System.Collections.Immutable;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// Text a card draws, read back from an arranged tree. A title, a summary and a choice's label that wraps are one leaf per word,
/// each ending in its space (a piece of a long word without), so the words of a sentence read as it was written once concatenated.
/// </summary>
internal static class ArrangedText
{
    /// <summary>Every text leaf's value, in tree order, run together: the words of a wrapped sentence are one sentence again.</summary>
    public static string Concat(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        string.Concat(arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value));

    /// <summary>The same, one entry per text leaf that stands alone (a sentence of words is one entry), trimmed: leaves of a run, a space or not, are joined.</summary>
    public static string[] Runs(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];

    /// <summary>The text of a cell layout's dump (<c>CellLayout.Describe</c>), run together as <see cref="Concat"/> does: a sentence of words is one sentence again.</summary>
    public static string Spoken(string described) =>
        string.Concat(System.Text.RegularExpressions.Regex.Matches(described, "Text \"([^\"]*)\"").Select(m => m.Groups[1].Value));
}
