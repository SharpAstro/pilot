using System.Text.RegularExpressions;

namespace Pilot.Gui.Tests;

/// <summary>
/// Text a page wraps is a leaf a word (<c>WrappedProse</c>): a test that asks whether the page says a sentence reads it as the
/// page does, the words run together, and not as one leaf.
/// </summary>
internal static partial class TestText
{
    /// <summary>The most leaves of a stretch <see cref="WithRuns"/> reads as one text: a sentence's words, a long word's pieces.</summary>
    private const int MostLeaves = 60;

    /// <summary>
    /// <paramref name="leaves"/> and, after them, every stretch of up to <see cref="MostLeaves"/> leaves run together as one text, its
    /// end trimmed: a sentence is found whole whether the page wrapped it word by word (and a long word piece by piece) or not.
    /// </summary>
    public static List<string> WithRuns(IEnumerable<string> leaves)
    {
        var all = leaves.ToList();
        var runs = new List<string>();
        for (var from = 0; from < all.Count; from++)
        {
            var text = all[from];
            for (var to = from + 1; to < all.Count && to < from + MostLeaves; to++)
            {
                text += all[to];
                runs.Add(text.TrimEnd());
            }
        }
        return [.. all, .. runs];
    }

    /// <summary>
    /// A cell dump's texts, in order and run together (<see cref="Console.Lib.CellLayout"/> describes a node a line, each text quoted):
    /// a sentence the page wrapped word by word is found whole.
    /// </summary>
    public static string Flat(string described) =>
        string.Concat(Quoted().Matches(described).Select(m => m.Groups[1].Value));

    [GeneratedRegex("\"([^\"\\r\\n]*)\"")]
    private static partial Regex Quoted();
}
