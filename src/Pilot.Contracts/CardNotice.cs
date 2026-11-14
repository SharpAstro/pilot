using System.Text.RegularExpressions;

namespace Pilot.Contracts;

/// <summary>One card of a <see cref="CardNotice"/>: what the node said of it. Detail is null when it had none; choices are their labels, in order.</summary>
public sealed record CardNoticeCard(string Id, string Bin, string Subject, string Title, string? Detail, string[] Choices);

/// <summary>A node's message about cards it opened, read back: the header line, each card, and the trailing instruction (null when absent).</summary>
public sealed record CardNoticeMessage(string Header, CardNoticeCard[] Cards, string? Instruction);

/// <summary>
/// The message the node says to the watchtower when cards open ("A card opened:" or "N cards opened:", one line per card, then what
/// to do), in one place: the node writes it with <see cref="Compose"/> and a client reads it back with <see cref="TryParse"/> to show
/// each card as a quote. A card's line is <c>- id [bin] about subject: title (detail). Choices: 1 label, 2 label.</c>; the detail and
/// the choices are left out when there are none. A title ending in a parenthesis and no detail is written with an empty <c>()</c>
/// after it, so the parse never takes the title's own parenthesis for the detail.
/// </summary>
public static partial class CardNotice
{
    /// <summary>The sentence under the cards.</summary>
    public const string Instruction = "These are yours to see through: act on each (resolve a conflict, make a review's small fixes, redo a leg only for real work).";

    /// <summary>The header line for <paramref name="count"/> cards.</summary>
    public static string Header(int count) => count == 1 ? "A card opened:" : $"{count} cards opened:";

    /// <summary>The whole message for <paramref name="cards"/>.</summary>
    public static string Compose(IReadOnlyList<CardRow> cards) =>
        Header(cards.Count) + "\n" + string.Join("\n", cards.Select(Line)) + "\n" + Instruction;

    /// <summary>One card as the watchtower reads it.</summary>
    public static string Line(CardRow card)
    {
        var bin = card.Bin switch { CardBin.Ring => "ring", CardBin.ForYou => "for you", _ => "handled" };
        var title = card.Title.ReplaceLineEndings(" ");
        var detail = card.Detail is { Length: > 0 } d
            ? $" ({d.ReplaceLineEndings(" ")})"
            : title.EndsWith(')') ? " ()" : "";
        var choices = card.Choices.Length == 0 ? "" : $" Choices: {string.Join(", ", card.Choices.Select((c, i) => $"{i + 1} {c.Label}"))}.";
        return $"- {card.Id} [{bin}] about {card.Subject}: {title}{detail}.{choices}";
    }

    [GeneratedRegex(@"^(?:A card|\d+ cards) opened:$")]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"^- (?<id>\S+) \[(?<bin>[^\]]+)\] about (?<subject>\S+): (?<rest>.*)$")]
    private static partial Regex CardLine();

    [GeneratedRegex(@" Choices: (?<list>\d+ .*)\.$")]
    private static partial Regex ChoicesTail();

    [GeneratedRegex(@", (?=\d+ )")]
    private static partial Regex ChoiceSplit();

    /// <summary>Reads a message <see cref="Compose"/> made; false for any other, including one with a card line it cannot read.</summary>
    public static bool TryParse(string message, out CardNoticeMessage notice)
    {
        notice = null!;
        var lines = message.ReplaceLineEndings("\n").Trim().Split('\n');
        if (lines.Length < 2 || !HeaderLine().IsMatch(lines[0].Trim()))
        {
            return false;
        }
        var cards = new List<CardNoticeCard>();
        string? instruction = null;
        foreach (var line in lines[1..])
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (instruction is not null || Card(line) is not { } card)
                {
                    return false;
                }
                cards.Add(card);
            }
            else if (line.Trim().Length > 0)
            {
                if (cards.Count == 0 || instruction is not null)
                {
                    return false;
                }
                instruction = line.Trim();
            }
        }
        if (cards.Count == 0)
        {
            return false;
        }
        notice = new CardNoticeMessage(lines[0].Trim(), [.. cards], instruction);
        return true;
    }

    private static CardNoticeCard? Card(string line)
    {
        var m = CardLine().Match(line);
        if (!m.Success)
        {
            return null;
        }
        var rest = m.Groups["rest"].Value;
        string[] choices = [];
        if (ChoicesTail().Match(rest) is { Success: true } tail)
        {
            choices = [.. ChoiceSplit().Split(tail.Groups["list"].Value).Select(c => c[(c.IndexOf(' ') + 1)..])];
            // The line's own full stop comes before " Choices:", so it stays.
            rest = rest[..tail.Index];
        }
        if (!rest.EndsWith('.'))
        {
            return null;
        }
        rest = rest[..^1];
        var title = rest;
        string? detail = null;
        if (rest.EndsWith(')') && OpeningOf(rest) is var open and > 0 && rest[open - 1] == ' ')
        {
            title = rest[..(open - 1)];
            detail = rest[(open + 1)..^1];
            if (detail.Length == 0)
            {
                detail = null;
            }
        }
        return new CardNoticeCard(m.Groups["id"].Value, m.Groups["bin"].Value, m.Groups["subject"].Value, title, detail, choices);
    }

    // The index of the parenthesis that closes at the end of text, or -1.
    private static int OpeningOf(string text)
    {
        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == ')')
            {
                depth++;
            }
            else if (text[i] == '(' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }
}
