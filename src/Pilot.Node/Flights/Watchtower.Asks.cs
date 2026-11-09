using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The permission prompts and questions of the stream watchtower (PIL855): each arrives on its stdout as a <c>can_use_tool</c> control
/// request (<see cref="StreamSession.Pending"/>), the chat shows it as a 'Waiting on you' panel, and the same ask is a card in For you
/// whose buttons answer it. Whichever the person uses, the control response is written once; a second answer is refused.
/// </summary>
public sealed partial class Watchtower
{
    /// <summary>The kind of the For-you card of a permission prompt or a question the watchtower waits on.</summary>
    public const string AskKind = CardKinds.WatchtowerAsk;

    /// <summary>The words of a card's button that refuses a call or declines a question.</summary>
    public const string DeclineLabel = "Decline to answer";

    private const int TitleLength = 160;

    /// <summary>The subject of the card of ask <paramref name="requestId"/>: one card per ask, so two open asks never share one.</summary>
    public static string AskSubject(string requestId) => $"{Verbs.WatchtowerTarget}/ask/{requestId}";

    private void RaiseAskCard(PendingAsk ask)
    {
        string title;
        string? detail;
        List<CardChoice> choices = [];
        if (ask.IsQuestion)
        {
            var first = ask.Questions is { Count: > 0 } questions ? questions[0] : null;
            title = $"The watchtower asks you: {Clip(first?.Question ?? "a question", TitleLength)}";
            detail = ask.Questions is { Count: > 0 } all
                ? string.Join("\n\n", all.Select(q => q.Question + "\n" + string.Join("\n", q.Options.Select(o => o.Description is { Length: > 0 } d ? $"- {o.Label}: {d}" : $"- {o.Label}")))
                    + (all.Any(q => q.MultiSelect) || all.Count > 1 ? "\n\nThis question takes several answers or boxes: answer it in the chat." : ""))
                : null;
            // One single-choice question is answered by a button per option; anything with more to it is answered in the chat's panel.
            if (ask.Questions is [{ MultiSelect: false } only] && only.Options.Count > 0)
            {
                choices.AddRange(only.Options.Select(o => new CardChoice
                {
                    Label = o.Label, Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget,
                    Args = [ask.Id, "answer", new JsonObject { [only.Question] = o.Label }.ToJsonString()],
                }));
            }
            choices.Add(new CardChoice { Label = DeclineLabel, Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = [ask.Id, "deny"] });
        }
        else
        {
            title = $"The watchtower asks leave to {PermissionAsks.Describe(ask.Tool, ask.Summary)}";
            // Whole, never cut: Allow runs the whole call, so the card shows all of what it allows (PIL855's review).
            detail = ask.Input.ValueKind == JsonValueKind.Object ? ask.Input.GetRawText().Trim() : "";
            choices.Add(new CardChoice { Label = "Allow it", Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = [ask.Id, "allow"] });
            choices.Add(new CardChoice { Label = "Deny it", Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = [ask.Id, "deny"] });
        }
        state.RaiseCard(CardBin.ForYou, AskKind, AskSubject(ask.Id), title, detail is { Length: > 0 } ? detail : null, choices);
    }

    /// <summary>
    /// How long an ask waits in For you before its card rings: as long as any session waiting on the person waits before it rings
    /// (<see cref="LegWatch.RingAfter"/>). While the person is at the chat they answer it in the panel long before; past it nobody is, and
    /// the watchtower is blocked with nothing else to show it (PIL855's review).
    /// </summary>
    public TimeSpan AskRingAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Rings the card of every ask the stream watchtower has waited on for longer than <see cref="AskRingAfter"/>, once each.</summary>
    private void RingLongAsks(DateTimeOffset now)
    {
        if (CurrentStream() is not { Pending.Count: > 0 } stream)
        {
            return;
        }
        var open = state.Cards(openOnly: true).Where(c => c.Kind == AskKind && c.Bin == CardBin.ForYou).ToDictionary(c => c.Subject, StringComparer.Ordinal);
        foreach (var ask in stream.Pending.Where(a => now - a.At > AskRingAfter))
        {
            if (open.TryGetValue(AskSubject(ask.Id), out var card))
            {
                state.UpdateCard(card.Id, card.Title, $"It has waited on you for over {Waited.Say(AskRingAfter)}, and the watchtower does nothing else until it is answered."
                    + (card.Detail is { Length: > 0 } detail ? "\n\n" + detail : ""), card.Choices, card.Link, bin: CardBin.Ring);
            }
        }
    }

    private void CloseAskCard(PendingAsk ask, string outcome)
    {
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == AskKind && c.Subject == AskSubject(ask.Id)) is { } card)
        {
            state.CloseCard(card.Id, outcome, "node");
        }
    }

    private static string Clip(string text, int length)
    {
        var line = text.Trim();
        return line.Length <= length ? line : line[..(length - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Answers a permission prompt or a question the watchtower waits on (<see cref="Verbs.AnswerAsk"/>): <paramref name="args"/> is the request
    /// id, then <c>allow</c>, <c>deny [reason…]</c> or <c>answer &lt;json&gt;</c>. Null when the control response was written; otherwise why it was
    /// not (nothing is waiting under that id, it was answered already, the words are not an answer). Never writes a second response.
    /// </summary>
    public async Task<string?> AnswerAskAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2 || args[0].Trim().Length == 0)
        {
            return $"{Verbs.AnswerAsk} takes the ask's id (the chat page's asks), then allow, deny [why] or answer <json of question to answer>";
        }
        var id = args[0].Trim();
        AskAnswer answer;
        switch (args[1].Trim().ToLowerInvariant())
        {
            case "allow":
                answer = AskAnswer.Allowed;
                break;
            case "deny":
                answer = AskAnswer.Denied(string.Join(' ', args.Skip(2)).Trim());
                break;
            case "answer":
                if (args.Count < 3 || AnswersOf(string.Join(' ', args.Skip(2))) is not { } answers)
                {
                    return $"{Verbs.AnswerAsk} {id} answer takes a JSON object of each question's text to its answer";
                }
                answer = new AskAnswer(true, Answers: answers);
                break;
            default:
                return $"{Verbs.AnswerAsk} {id} takes allow, deny or answer, not '{args[1]}'";
        }
        if (CurrentStream() is not { } stream)
        {
            return "the watchtower has no running process to answer: whatever it asked was called off when it ended";
        }
        return await stream.AnswerAsync(id, answer, cancellationToken);
    }

    /// <summary>A question's answers from a JSON object (each value a label, or an array of labels joined by a comma); null when it is no such object.</summary>
    private static Dictionary<string, string>? AnswersOf(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject given)
            {
                return null;
            }
            var answers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (question, value) in given)
            {
                answers[question] = value switch
                {
                    JsonArray many => string.Join(", ", many.Select(v => v?.GetValue<string>() ?? "")),
                    JsonValue one => one.GetValue<string>(),
                    _ => throw new JsonException("an answer is text"),
                };
            }
            return answers;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
