using System.Text;

namespace Pilot.Contracts;

/// <summary>What a client shows of a card-chat message: the card it is about, a short quote, and the person's words.</summary>
public sealed record CardChat(string CardId, string Title, string Quote, string Words);

/// <summary>
/// The message that opens a chat about a card: the card's whole context in a marked block, so the watchtower can act without
/// looking anything up, then the person's words. Plain ASCII; a client reads it back with <see cref="TryParse"/>.
/// </summary>
public static class CardContext
{
    private const string Open = "[pilot card ";
    private const string Close = "[end of card]";
    private const string Indent = "  ";
    private const int QuoteLength = 120;

    /// <summary>The block for <paramref name="card"/> on <paramref name="node"/>, then <paramref name="words"/>.</summary>
    /// <param name="flightState">The card's flight as a line or lines (its legs, PR and review); null when the card is not about a flight.</param>
    public static string Compose(CardRow card, string node, string? flightState, string words)
    {
        var text = new StringBuilder();
        text.Append(Open).Append(Ascii(card.Id)).AppendLine("]");
        text.Append("node: ").AppendLine(Ascii(node));
        text.Append("kind: ").AppendLine(Ascii(card.Kind));
        text.Append("subject: ").AppendLine(Ascii(card.Subject));
        text.Append("title: ").AppendLine(Ascii(OneLine(card.Title)));
        if (card.Detail is { Length: > 0 } detail)
        {
            text.AppendLine("detail:");
            Block(text, detail);
        }
        if (card.Choices.Length > 0)
        {
            text.AppendLine("choices:");
            for (var i = 0; i < card.Choices.Length; i++)
            {
                var c = card.Choices[i];
                var command = string.Join(' ', new[] { c.Verb, c.Target }.Concat(c.Args));
                text.Append(Indent).Append(i + 1).Append(". ").Append(Ascii(OneLine(c.Label))).Append(": ").AppendLine(Ascii(OneLine(command)));
            }
        }
        if (card.Link is { Length: > 0 } link)
        {
            text.Append("link: ").AppendLine(Ascii(link));
        }
        if (flightState is { Length: > 0 })
        {
            text.AppendLine("flight:");
            Block(text, flightState);
        }
        text.AppendLine(Close);
        text.AppendLine();
        text.Append(words);
        return text.ToString();
    }

    /// <summary>Reads a message <see cref="Compose"/> made; false for any other message.</summary>
    public static bool TryParse(string message, out CardChat chat)
    {
        chat = null!;
        var lines = message.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith(Open, StringComparison.Ordinal) || !lines[0].EndsWith(']'))
        {
            return false;
        }
        var end = Array.IndexOf(lines, Close);
        if (end < 0)
        {
            return false;
        }
        var id = lines[0][Open.Length..^1];
        string? title = null;
        var detail = new List<string>();
        var inDetail = false;
        foreach (var line in lines[1..end])
        {
            if (inDetail && line.StartsWith(Indent, StringComparison.Ordinal))
            {
                detail.Add(line[Indent.Length..]);
                continue;
            }
            inDetail = line == "detail:";
            if (line.StartsWith("title: ", StringComparison.Ordinal))
            {
                title = line["title: ".Length..];
            }
        }
        var quoted = detail.Count > 0 ? string.Join(' ', detail) : title ?? "";
        chat = new CardChat(id, title ?? "", Shorten(quoted), string.Join('\n', lines[(end + 1)..]).Trim('\n'));
        return true;
    }

    private static void Block(StringBuilder text, string value)
    {
        foreach (var line in value.ReplaceLineEndings("\n").Split('\n'))
        {
            text.Append(Indent).AppendLine(Ascii(line));
        }
    }

    private static string OneLine(string value) => value.ReplaceLineEndings(" ");

    private static string Shorten(string value) => value.Length <= QuoteLength ? value : value[..(QuoteLength - 3)] + "...";

    /// <summary>Printable ASCII and tabs only: a character beyond it becomes <c>?</c>.</summary>
    private static string Ascii(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            text.Append(c is '\t' or >= ' ' and <= '~' ? c : '?');
        }
        return text.ToString();
    }
}
