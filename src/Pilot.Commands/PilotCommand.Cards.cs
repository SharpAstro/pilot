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
            .ThenByDescending(c => c.RungAt)
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
            if (card.State == CardState.Open && card.Answering is { } answering)
            {
                // An answer underway (#792): said under the card, as the GUI says it where the buttons are.
                text.AppendLine($"      {answering.Line()}");
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
        internal (Command, Command, Command, Command, Command, Command, Command) CardCommands()
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

            var subject = Arg("subject", "What it is about: flight/<code>, flight/<code>/<leg>, session/<id>, node or card/<id> (an open card: its words are added to that card, which is rung again, and no second card is raised)");
            var cardTitle = Arg("title", "The card in a line");
            var detail = new Option<string?>("--detail") { Description = "What else is needed to decide" };
            var attachment = new Option<string?>("--attachment") { Description = "An image for the card, shown as a thumbnail that opens full size: the <id>.<ext> of a copy the node holds, or the path of an image file (png, jpg, jpeg, gif, webp or bmp, at most 10 MB) that is uploaded first" };
            Command Raise(string verbName, string bin, string description) =>
                Action(new Command(verbName, description) { Arguments = { subject, cardTitle }, Options = { detail, attachment } },
                    async (p, client, ct) =>
                    {
                        string? image = null;
                        if (p.GetValue(attachment) is { Length: > 0 } named)
                        {
                            (image, var problem) = await CardImageAsync(client, named, ct);
                            if (problem is not null)
                            {
                                await error.WriteLineAsync($"pilot: {problem}");
                                return 2;
                            }
                        }
                        return await Send(client, Verbs.Raise, p.GetValue(subject)!,
                            p.GetValue(detail) is { } d ? [bin, p.GetValue(cardTitle)!, d] : [bin, p.GetValue(cardTitle)!], who, ct, attachment: image);
                    });
            var ring = Raise("ring", "ring", "Raise a card that needs the controller and pushes to them");
            var flag = Raise("flag", "for-you", "Raise a card for the controller, without a push");
            var handled = Raise("handled", "handled", "Log a line on what was handled, born closed");

            // PIL1285: an investigator's one outcome. The node records it, rings once for ring, and ends the run.
            var investigatedSubject = Arg("subject", "What the run is about: the flight code, card id or digest its brief names");
            var outcome = Arg("outcome", "issue (an issue and a flight were made), action (one thing done) or ring (the person is needed)");
            var outcomeWhy = Words("why", "Why, naming the evidence; for ring, what was tried and the one thing the person needs to do");
            var investigated = Action(new Command(Verbs.Investigated, "An investigator's one outcome: recorded as a Handled line and a decision, and the run ends") { Arguments = { investigatedSubject, outcome, outcomeWhy } },
                (p, client, ct) => Send(client, Verbs.Investigated, p.GetValue(investigatedSubject)!, [p.GetValue(outcome)!, .. p.GetValue(outcomeWhy) ?? []], who, ct));

            return (cards, answer, dismiss, ring, flag, handled, investigated);
        }

        /// <summary>
        /// What <c>--attachment</c> names, as the <c>&lt;id&gt;.&lt;ext&gt;</c> the node's raise takes: a file (relative to the working
        /// folder) is checked to be an image of at most <see cref="IdeaImages.MaxBytes"/> and uploaded first; anything else must already be
        /// the id and extension of a copy the node holds. A problem is said in words and nothing is sent.
        /// </summary>
        private async Task<(string? Image, string? Problem)> CardImageAsync(NodeClient client, string named, CancellationToken ct)
        {
            var path = Path.GetFullPath(named, cwd);
            if (!File.Exists(path))
            {
                var dot = named.LastIndexOf('.');
                return dot > 0 && named[..dot].All(char.IsAsciiLetterOrDigit) && TellPicture.Extensions.Contains(named[(dot + 1)..].ToLowerInvariant())
                    ? (named, null)
                    : (null, $"--attachment {named}: there is no such file, and it is not the <id>.<ext> of an image the node holds");
            }
            return await TellPicture.UploadFileAsync(client, path, $"--attachment {named}", ct);
        }
    }
}
