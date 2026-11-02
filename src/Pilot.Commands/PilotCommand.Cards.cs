using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>The cards, rings first then for-you, newest first within each; with <paramref name="all"/> the closed ones and Handled too.</summary>
    internal static string Cards(SnapshotEvent snapshot, bool all = false)
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
        var text = new System.Text.StringBuilder();
        foreach (var card in cards)
        {
            var bin = card.Bin switch { CardBin.Ring => "RING", CardBin.ForYou => "for you", _ => "handled" };
            var closed = card.State == CardState.Closed ? $"  [closed: {card.Outcome}{(card.ClosedBy is { } by ? $", by {by}" : "")}]" : "";
            text.AppendLine($"{card.Id}  {bin}  {card.Title}{closed}");
            if (CardLeg.Of(card) is { } leg)
            {
                text.AppendLine($"      leg {leg}");
            }
            if (card.Summary is { Length: > 0 } summary)
            {
                text.AppendLine($"      {summary}");
            }
            if (card.Detail is { Length: > 0 } detail)
            {
                foreach (var line in detail.Split('\n'))
                {
                    text.AppendLine($"      {line}");
                }
            }
            if (card.Link is { } link)
            {
                text.AppendLine($"      {link}");
            }
            if (card.State == CardState.Open)
            {
                for (var i = 0; i < card.Choices.Length; i++)
                {
                    var status = card.Choices[i].Status is { } s ? $" (checks {s})" : "";
                    text.AppendLine($"      {i + 1}. {card.Choices[i].Label}{status}: pilot answer {card.Id} {i + 1}");
                }
                text.AppendLine($"      or: pilot dismiss {card.Id}");
            }
        }
        return text.ToString();
    }
    private sealed partial class BuildContext
    {
        /// <summary>Makes the cards, answer, dismiss, and the commands that raise a card (ring, flag, handled).</summary>
        internal (Command, Command, Command, Command, Command, Command) CardCommands()
        {
            // cards
            var all = new Option<bool>("--all") { Description = "Closed cards and Handled lines too" };
            var cards = Action(new Command("cards", "Open cards, rings first") { Options = { all } }, async (p, client, ct) =>
            {
                var snapshot = await SnapshotAsync(client, ct);
                var everything = p.GetValue(all);
                await WriteAsync(p.GetValue(outputFormat), () => Cards(snapshot, all: everything), () => CardsMarkdown.Build(snapshot, everything));
                return 0;
            });
            var card = Arg("card", "The card's id, as c3");
            var choice = Words("choice", "The choice's number or label");
            // --why is the message acted on, as for the person's own commands: the watchtower quotes the person's words in its chat
            // with it, and the node takes such an answer or dismissal as theirs. A dismissal's reason stays its own words.
            var answerWords = new Option<string[]>("--words") { Description = "The words a choice that asks for them needs, such as the design card's Answer", Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = true };
            var answer = Action(new Command("answer", "Run the card's choice; the card closes when it is done") { Arguments = { card, choice }, Options = { actingOn, answerWords } },
                (p, client, ct) => Send(client, Verbs.Answer, Verbs.CardTarget(p.GetValue(card)!), [Join(p, choice), .. p.GetValue(answerWords) ?? []], who, ct, p.GetValue(actingOn)));
            var dismissReason = Words("reason", "Why it needs nothing", required: false);
            var dismiss = Action(new Command("dismiss", "Close the card with nothing done") { Arguments = { card, dismissReason }, Options = { actingOn } },
                (p, client, ct) => Send(client, Verbs.Dismiss, Verbs.CardTarget(p.GetValue(card)!), p.GetValue(dismissReason) ?? [], who, ct, p.GetValue(actingOn)));

            var subject = Arg("subject", "What it is about: flight/<code>, flight/<code>/<leg>, session/<id> or node");
            var cardTitle = Arg("title", "The card in a line");
            var detail = new Option<string?>("--detail") { Description = "What else is needed to decide" };
            Command Raise(string verbName, string bin, string description) =>
                Action(new Command(verbName, description) { Arguments = { subject, cardTitle }, Options = { detail } },
                    (p, client, ct) => Send(client, Verbs.Raise, p.GetValue(subject)!,
                        p.GetValue(detail) is { } d ? [bin, p.GetValue(cardTitle)!, d] : [bin, p.GetValue(cardTitle)!], who, ct));
            var ring = Raise("ring", "ring", "Raise a card that needs the controller and pushes to them");
            var flag = Raise("flag", "for-you", "Raise a card for the controller, without a push");
            var handled = Raise("handled", "handled", "Log a line on what was handled, born closed");

            return (cards, answer, dismiss, ring, flag, handled);
        }
    }
}
