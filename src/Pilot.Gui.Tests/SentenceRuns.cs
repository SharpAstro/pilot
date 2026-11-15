using DIR.Lib;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// The text a page says, a sentence at a time: a line that wraps is a leaf a word, each ending in its space (WrappedProse), so a test that
/// asks what the page says reads the run of words as the one sentence it is. A run ends at a leaf that does not end in a space, and at any
/// node that is not a text leaf (the wrap's own parent comes between two sentences).
/// </summary>
internal static class SentenceRuns
{
    public static List<(string Text, RGBAColor32 Color)> Of(IEnumerable<Layout.Node> nodes)
    {
        var found = new List<(string, RGBAColor32)>();
        var run = new System.Text.StringBuilder();
        RGBAColor32 color = default;
        void Flush()
        {
            if (run.Length > 0)
            {
                found.Add((run.ToString().Trim(), color));
                run.Clear();
            }
        }
        foreach (var node in nodes)
        {
            if (node is Layout.Node.Leaf { Content: Layout.Content.Text text })
            {
                if (run.Length == 0)
                {
                    color = text.Color;
                }
                run.Append(text.Value);
                if (!text.Value.EndsWith(' '))
                {
                    Flush();
                }
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return found;
    }

    public static List<string> Texts(IEnumerable<Layout.Node> nodes) => [.. Of(nodes).Select(r => r.Text)];
}
