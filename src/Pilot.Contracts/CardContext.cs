using System.Text;

namespace Pilot.Contracts;

/// <summary>
/// What a client shows of a card-chat message: the card it is about, a short quote, and the person's words. <see cref="Cut"/> is true
/// when the node cut the card's block (<see cref="CardContext.Clip"/>); the quote then leaves the cut's own line out.
/// </summary>
public sealed record CardChat(string CardId, string Title, string Quote, string Words, bool Cut = false);

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

    /// <summary>The line that stands for what <see cref="Clip"/> left out of a card's block.</summary>
    public const string Cut = Indent + "[...]";

    /// <summary>
    /// A message <see cref="Compose"/> made, cut to <paramref name="limit"/> characters inside the card's block: the block's close and
    /// the person's words after it are kept whole, and a <see cref="Cut"/> line stands for what was left out, so the message still
    /// reads back with <see cref="TryParse"/>. Null for a message that is not a card's, or whose words alone are over the limit: the
    /// caller cuts those as it cuts any text. A long card cut at its end lost the words the person typed under it, so the chat
    /// showed the block without them and kept their echo beside it (DE1, c20738 and "wdwd", 2026-11-12).
    /// </summary>
    public static string? Clip(string message, int limit)
    {
        if (message.Length <= limit)
        {
            return message;
        }
        var close = message.StartsWith(Open, StringComparison.Ordinal) ? message.IndexOf("\n" + Close, StringComparison.Ordinal) : -1;
        if (close < 0)
        {
            return null;
        }
        var tail = message[(close + 1)..];
        var room = limit - tail.Length - Cut.Length - 1;
        // The head ends on a whole line, the card's first line at the least.
        var head = room > 0 ? message.LastIndexOf('\n', Math.Min(room, close) - 1) : -1;
        return head < 0 ? null : string.Concat(message.AsSpan(0, head + 1), Cut, "\n", tail);
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
        var cut = false;
        foreach (var line in lines[1..end])
        {
            if (line == Cut)
            {
                cut = true;
                continue;
            }
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
        chat = new CardChat(id, title ?? "", Shorten(quoted), string.Join('\n', lines[(end + 1)..]).Trim('\n'), cut);
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
