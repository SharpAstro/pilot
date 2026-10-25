using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// <c>pilot cards</c> as Markdown, for <c>--output-format markdown</c> and <c>rich</c>: the same cards in the same order as
/// <see cref="PilotCommand.Cards"/>, each a heading with its detail, its link as a link and its choices as a numbered list
/// with the commands that answer or dismiss it in code spans.
/// </summary>
internal static class CardsMarkdown
{
    internal static string Build(SnapshotEvent snapshot, bool all = false)
    {
        var cards = snapshot.Cards
            .Where(c => all || c.State == CardState.Open)
            .OrderBy(c => c.State)
            .ThenBy(c => c.Bin)
            .ThenByDescending(c => c.At)
            .ToList();
        if (cards.Count == 0)
        {
            return all ? "No cards.\n" : "No open cards.\n";
        }
        var text = new StringBuilder();
        text.AppendLine("# pilot cards").AppendLine();
        foreach (var card in cards)
        {
            var bin = card.Bin switch { CardBin.Ring => "RING", CardBin.ForYou => "for you", _ => "handled" };
            text.AppendLine($"## {card.Id} · {bin} · {Markdown.Text(card.Title)}").AppendLine();
            if (card.State == CardState.Closed)
            {
                var by = card.ClosedBy is { } closedBy ? $", by {closedBy}" : "";
                text.AppendLine($"**closed**: {Markdown.Text($"{card.Outcome}{by}")}").AppendLine();
            }
            if (card.Detail is { Length: > 0 } detail)
            {
                // A hard line break per line, as the plain output keeps the detail's lines.
                text.AppendLine(string.Join("\\\n", detail.Split('\n').Select(l => Markdown.Text(l.TrimEnd('\r'))))).AppendLine();
            }
            if (card.Link is { } link)
            {
                text.AppendLine($"[{Markdown.Text(link)}]({(link.Contains(' ') ? $"<{link}>" : link)})").AppendLine();
            }
            if (card.State == CardState.Open)
            {
                for (var i = 0; i < card.Choices.Length; i++)
                {
                    var status = card.Choices[i].Status is { } s ? $" (checks {Markdown.Text(s)})" : "";
                    text.AppendLine($"{i + 1}. {Markdown.Text(card.Choices[i].Label)}{status}: `pilot answer {card.Id} {i + 1}`");
                }
                text.AppendLine().AppendLine($"or: `pilot dismiss {card.Id}`").AppendLine();
            }
        }
        return text.ToString();
    }
}
