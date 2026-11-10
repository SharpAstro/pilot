using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The brain's word before a ring (PIL855). A card raised to ring is first filed in For you, silently, and the brain is asked 'does the
/// person have to act, and what is the one action?' as a message on its stdin with a JSON answer. Ring: the card moves to the ring (and
/// is reworded in plain words after, in the background, so the ring waits only for the verdict). Quiet: it stays in For you with the
/// brain's reason in its detail. The brain down, busy with the person, late or unreadable is the old path: it rings, at once for a brain
/// not there and after at most <see cref="VerdictTimeout"/> otherwise. A ring is never lost to a failure of this class.
/// </summary>
public sealed class RingJudge(NodeState state, Func<IBrain?> brain)
{
    /// <summary>How long the brain has to give its verdict before the card rings as it would have.</summary>
    public TimeSpan VerdictTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How long it has to reword a ringing card; the card keeps its words when it is late.</summary>
    public TimeSpan WordingTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Where a verdict, a rewording and a fall back to the old path are noted; null notes none.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The longest title and the longest verdict reason that are kept.</summary>
    private const int TitleLength = 160;
    private const int ReasonLength = 300;
    private const int DetailLength = 1500;

    /// <summary>The JSON object the brain answers a ring-or-quiet request with.</summary>
    public const string VerdictShape = """{"ring": true or false, "action": "<the one thing the person does; empty when quiet>", "reason": "<one plain sentence>"}""";

    /// <summary>The JSON object the brain answers a wording request with.</summary>
    public const string WordingShape = """{"title": "<one plain sentence, under 100 characters>", "summary": "<at most two short plain sentences>"}""";

    /// <summary>What the detail of a card the brain judged quiet starts its reason with.</summary>
    public const string QuietPrefix = "Not a ring: ";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _judged = new(StringComparer.Ordinal);
    private readonly List<Task> _running = [];

    /// <summary>
    /// Whether a card of <paramref name="kind"/> goes to the brain before it rings. Not what is about the brain itself (it is down, or it
    /// asks the person something): those are the person's by definition, and a brain that is down cannot say.
    /// </summary>
    public static bool Wants(string kind) =>
        kind is not (Watchtower.AskKind or Watchtower.Down or Watchtower.Stopped or Watchtower.Waits or Watchtower.RefusedKind or CardKinds.AssistantAsk
            or LegCommander.PrReady or LegCommander.MergeFailed);

    /// <summary>
    /// Whether a card of <paramref name="kind"/> offering <paramref name="choices"/> goes to the brain before it rings. Not one whose choice
    /// is the person's alone (a merge, a landing, any of <see cref="Standing.YourOnlyVerbs"/>): the brain could only hold it back, and a wrong
    /// verdict would leave a decision of theirs unpushed with nothing to say so. Those ring at once, as before PIL855; the brain judges the rest.
    /// </summary>
    public static bool Wants(string kind, IReadOnlyList<CardChoice>? choices) =>
        Wants(kind) && !(choices ?? []).Any(c => Standing.YourOnlyVerbs.Contains(c.Verb));

    /// <summary>
    /// Whether a ring can wait for the brain now: a brain there and able to answer. When it cannot, the card rings at once, as before, with
    /// no moment in For you (a ringing card's rules, such as an assistant's dismiss needing an ask, hold from the start).
    /// </summary>
    public bool CanJudge => brain() is { Unavailable: null };

    /// <summary>Starts the verdict of <paramref name="card"/>, a card just filed in For you on its way to the ring; once per card, whatever the caller does.</summary>
    public void Begin(CardRow card)
    {
        lock (_lock)
        {
            if (!_judged.Add(card.Id))
            {
                return;
            }
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(Task.Run(() => JudgeAsync(card, CancellationToken.None)));
        }
    }

    /// <summary>Completes when every verdict begun so far has been given.</summary>
    public Task SettledAsync()
    {
        lock (_lock)
        {
            return Task.WhenAll(_running);
        }
    }

    /// <summary>The verdict, the move and the rewording of <paramref name="card"/>. Never throws: whatever goes wrong, the card rings.</summary>
    public async Task JudgeAsync(CardRow card, CancellationToken cancellationToken)
    {
        var rang = false;
        try
        {
            var verdict = await AskVerdictAsync(card, cancellationToken);
            if (verdict is { Ring: false })
            {
                Quiet(card, verdict.Reason);
                rang = true;
                return;
            }
            var ringsNow = Ring(card, verdict is null ? null : verdict.Action);
            rang = true;
            if (!ringsNow)
            {
                return;
            }
            // A design card's words are the reviewer's questions and the options its buttons cannot say (PrReviews.DesignSummary), which is what the person
            // answers: a rewording "of what happened" would replace them, up to 600 characters where the panel draws 240.
            if (verdict is not null && card.Kind != PrReviews.DesignKind && brain() is { } b)
            {
                await RewordAsync(b, card, verdict.Action, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"ring: judging {card.Id} failed ({ex.Message})");
        }
        finally
        {
            if (!rang)
            {
                Ring(card, null);
            }
        }
    }

    private sealed record Verdict(bool Ring, string Action, string Reason);

    private async Task<Verdict?> AskVerdictAsync(CardRow card, CancellationToken cancellationToken)
    {
        if (brain() is not { } b)
        {
            Log?.Invoke($"ring: no brain to ask about {card.Id}; it rings");
            return null;
        }
        try
        {
            var reply = await b.AskAsync("ring", VerdictRequest(card), VerdictTimeout, cancellationToken);
            if (reply.Answer.TryGetProperty("ring", out var ring) && ring.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var verdict = new Verdict(ring.ValueKind == JsonValueKind.True, Text(reply.Answer, "action", ReasonLength), Text(reply.Answer, "reason", ReasonLength));
                Log?.Invoke($"ring: the brain says {card.Id} {(verdict.Ring ? "rings" : "is quiet")} ({verdict.Reason})");
                return verdict;
            }
            Log?.Invoke($"ring: the brain's verdict on {card.Id} has no ring true or false; it rings");
        }
        catch (BrainDownException ex)
        {
            Log?.Invoke($"ring: the brain could not be asked about {card.Id} ({ex.Message}); it rings");
        }
        return null;
    }

    /// <summary>The request about one card: what it says, what it offers, and the two questions.</summary>
    public static string VerdictRequest(CardRow card)
    {
        var text = new StringBuilder();
        text.AppendLine("A card is about to ring the person (a push to their phone and desktop). Judge it before it does.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Kind: {card.Kind}");
        text.AppendLine(CultureInfo.InvariantCulture, $"About: {card.Subject}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Title: {card.Title}");
        if (!string.IsNullOrWhiteSpace(card.Detail))
        {
            text.AppendLine("Detail:").AppendLine(Clip(card.Detail, DetailLength));
        }
        if (card.Choices.Length > 0)
        {
            text.AppendLine("It offers: " + string.Join("; ", card.Choices.Select(c => c.Label)));
        }
        text.AppendLine();
        text.AppendLine("Does the person have to act, or does it settle without them (the node or a worker will handle it, or it is only news)? What is the one action?");
        text.AppendLine("ring is true only when something stays stuck unless the person acts. When it is false, reason says in one plain sentence why nothing is needed.");
        text.Append("Answer as this JSON object: ").Append(VerdictShape);
        return text.ToString();
    }

    /// <summary>The card, as it is now, when it is still open; null when it closed or is gone.</summary>
    private CardRow? Open(string id) => state.Cards(openOnly: true).FirstOrDefault(c => c.Id == id);

    // Whether this verdict rang it: false for a card closed meanwhile, or one that rang while its verdict was pending (a press of it the
    // watchtower was refused), which already leads with what to do and is not reworded either.
    private bool Ring(CardRow card, string? action)
    {
        if (Open(card.Id) is not { Bin: not CardBin.Ring } now)
        {
            return false;
        }
        // The one action goes in front of the detail, so the card says what it rings for.
        var detail = action is { Length: > 0 } ? $"What to do: {action}" + (string.IsNullOrWhiteSpace(now.Detail) ? "" : "\n\n" + now.Detail) : now.Detail;
        state.UpdateCard(now.Id, now.Title, detail, now.Choices, now.Link, bin: CardBin.Ring);
        return true;
    }

    private void Quiet(CardRow card, string reason)
    {
        // A card rung while its verdict was pending (a press of it the watchtower was refused, Watchtower.Refused) stays rung: the verdict
        // was about the card as it was raised, and only the judge files a card in For you on its way to the ring.
        if (Open(card.Id) is not { Bin: not CardBin.Ring } now)
        {
            return;
        }
        var why = reason.Length > 0 ? reason : "the brain judged that nothing is needed from the person";
        state.UpdateCard(now.Id, now.Title, QuietPrefix + why + (string.IsNullOrWhiteSpace(now.Detail) ? "" : "\n\n" + now.Detail), now.Choices, now.Link, bin: CardBin.ForYou);
    }

    private async Task RewordAsync(IBrain b, CardRow card, string action, CancellationToken cancellationToken)
    {
        if (Open(card.Id) is not { } now)
        {
            return;
        }
        try
        {
            var request = $"""
                A card now rings the person. Reword it in plain words for someone who has not followed the work: what happened and what to do, no jargon, no ids or hashes unless the person needs them to act.
                Title now: {now.Title}
                Detail: {Clip(card.Detail ?? "", DetailLength)}
                The one action: {(action.Length > 0 ? action : "not stated")}
                Answer as this JSON object: {WordingShape}
                """;
            var reply = await b.AskAsync("wording", request, WordingTimeout, cancellationToken);
            var title = Text(reply.Answer, "title", TitleLength);
            var summary = Text(reply.Answer, "summary", 600);
            if (title.Length == 0 || title.Contains('\n') || !CardSummaries.Usable(summary) || Open(card.Id) is not { } latest)
            {
                Log?.Invoke($"ring: the wording of {card.Id} was not usable; it keeps its words");
                return;
            }
            if (latest.Title != now.Title)
            {
                // Its title changed while the wording was asked (a refused press of it rang it): a wording of the old card would drop that.
                Log?.Invoke($"ring: {card.Id} changed while it was reworded; it keeps its new words");
                return;
            }
            state.UpdateCard(latest.Id, title, latest.Detail, latest.Choices, latest.Link, summary: summary);
            Log?.Invoke($"ring: {card.Id} reworded");
        }
        catch (BrainDownException ex)
        {
            Log?.Invoke($"ring: {card.Id} keeps its words ({ex.Message})");
        }
    }

    private static string Text(JsonElement answer, string name, int length) =>
        answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Clip(value.GetString() ?? "", length) : "";

    private static string Clip(string text, int length)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= length ? trimmed : trimmed[..(length - 1)].TrimEnd() + "…";
    }
}
