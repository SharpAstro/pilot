using System.Text.RegularExpressions;

namespace Pilot.Contracts;

/// <summary>
/// A card's summary as the lines a person reads it in. A design card's summary is one line on the wire (the node's summary rule keeps a
/// summary to two lines), but it is a list: the question, then each option by its tag (<c>A: ...</c>, <c>1B: ...</c>), then why it rings.
/// Run together they were a wall of text nobody could scan (the owner, 2026-11-22), so a view breaks it here, before each tag, before a
/// question's number and before <c>It rings because</c>, and the node's <c>PrReviews.DesignSummary</c> writes every tag after a sentence's end so that this finds it.
/// An explicit newline is a break too. Any other summary comes back as its one line.
/// </summary>
public static partial class CardSummaryLines
{
    /// <summary>The lines of <paramref name="summary"/>, none empty, each trimmed.</summary>
    public static IReadOnlyList<string> Of(string summary) =>
        summary.Split('\n')
            .SelectMany(line => Break().Split(line))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    // A tag is one capital letter, after a question's number on a card with several (1A, 2B), and a colon, standing after a sentence's end
    // (., ?, ! or the ellipsis of a cut question). Plain prose rarely puts a lone capital and a colon right after a full stop. A card with
    // several questions numbers them (2. Is a leg done...), and a number so starts a line too.
    [GeneratedRegex(@"(?<=[.?!…])\s+(?=(?:\d+)?[A-Z]: |\d+\. |It rings because )")]
    private static partial Regex Break();
}
