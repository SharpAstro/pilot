using System.Globalization;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>One call the watchtower's MCP server relays to the running buddy: the tool (<c>say</c>, <c>point</c>, <c>state</c>, <c>sleep</c>) and its arguments.</summary>
public sealed record BuddyCall(string Tool, string? Text = null, string? Urgency = null, string? Card = null)
{
    public const string Say = "say";
    public const string Point = "point";
    public const string State = "state";
    public const string Sleep = "sleep";

    /// <summary>Opens the settings card: sent by <c>pilot-buddy settings</c>, not offered to the watchtower.</summary>
    public const string Settings = "settings";
}

/// <summary>What the buddy answers a call with; <paramref name="Ok"/> false is a refusal the tool reports as an error.</summary>
public sealed record BuddyAnswer(bool Ok, string Text)
{
    /// <summary>What every tool answers when no buddy listens on this desktop.</summary>
    public const string NoBuddyText = "no buddy on this desktop";

    /// <summary>The answer when no buddy listens: said at once, nothing waits.</summary>
    public static BuddyAnswer NoBuddy { get; } = new(true, NoBuddyText);
}

/// <summary>The road from the MCP server to the buddy: a named pipe in production, the relay itself in a test.</summary>
public interface IBuddyLink
{
    /// <summary>Relays <paramref name="call"/> and returns the buddy's answer, or <see cref="BuddyAnswer.NoBuddy"/> when none listens.</summary>
    Task<BuddyAnswer> CallAsync(BuddyCall call, CancellationToken cancellationToken);
}

/// <summary>
/// The running buddy's end of the channel: what the watchtower says, points at or settles reaches the <see cref="BuddyModel"/> through here, so
/// it shows in the attention or trouble bubble of DSN-2 v5 by its urgency (no screen of its own). Pure: no thread, no clock but the
/// <see cref="TimeProvider"/>; the pipe server that awaits connections calls <see cref="Handle"/>.
/// <para>
/// <see cref="Gate"/> is the one lock over the model: the host takes it while it draws and the relay while it answers, since a call comes from the
/// pipe's own thread.
/// </para>
/// </summary>
public sealed class BuddyRelay(BuddyModel model, BuddyFeed feed, TimeProvider time, object? gate = null)
{
    private readonly HashSet<string> _told = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>The lock over the model and the feed that the host and this relay both take.</summary>
    public object Gate { get; } = gate ?? new();

    /// <summary>The window is hidden (the settings' "hide the cat"); reported by buddy_state, and the words still queue.</summary>
    public bool Hidden
    {
        get => !feed.WindowVisible;
        // One source: the feed says whether the buddy shows the cards (visible and not snoozed) from the same flag.
        set => feed.WindowVisible = !value;
    }

    /// <summary>The last words the watchtower had the cat say, and when.</summary>
    public string? LastSaid { get; private set; }

    public DateTimeOffset? LastSaidAt { get; private set; }

    /// <summary>Answers one call; an unknown tool is a refusal.</summary>
    public BuddyAnswer Handle(BuddyCall call)
    {
        BuddyAnswer answer;
        lock (Gate)
        {
            answer = call.Tool switch
            {
                BuddyCall.Say => Say(call),
                BuddyCall.Point => Point(call),
                BuddyCall.State => State(),
                BuddyCall.Sleep => Sleep(),
                BuddyCall.Settings => OpenSettingsCard(),
                _ => new BuddyAnswer(false, $"the buddy has no tool '{call.Tool}'"),
            };
        }
        // The window sleeps on its event queue: a call that changed the cat wakes it so it asks whether a frame is due.
        Changed?.Invoke();
        return answer;
    }

    /// <summary>Opens the settings card on the window; set by the host.</summary>
    public Action? OpenSettings { get; set; }

    private BuddyAnswer OpenSettingsCard()
    {
        OpenSettings?.Invoke();
        return new BuddyAnswer(true, "the settings card is open");
    }

    /// <summary>Called after each call, outside the gate: the host wakes its loop.</summary>
    public Action? Changed { get; set; }

    private BuddyAnswer Say(BuddyCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Text))
        {
            return new BuddyAnswer(false, "buddy_say needs text");
        }
        var urgent = string.Equals(call.Urgency, "urgent", StringComparison.OrdinalIgnoreCase);
        var id = "say:" + (++_sequence).ToString(CultureInfo.InvariantCulture);
        _told.Add(id);
        LastSaid = call.Text;
        LastSaidAt = time.GetUtcNow();
        // Urgent is trouble (red bubble); anything else asks for a look (the attention bubble).
        model.Tell(new BuddyNotice(id, urgent ? NoticeKind.Trouble : NoticeKind.Ask, "Watchtower", call.Text));
        return new BuddyAnswer(true, urgent ? "the cat is furious and said it" : "the cat is up and said it");
    }

    private BuddyAnswer Point(BuddyCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Card))
        {
            return new BuddyAnswer(false, "buddy_point needs a card id");
        }
        var id = BuddyFeed.CardNoticeId(call.Card);
        var known = feed.Mirror.Cards.TryGetValue(call.Card, out var card);
        var urgent = known && ToastTier.Of(card!.Kind) == ToastTier.Urgent;
        var title = known ? card!.Title : "Card " + call.Card;
        var body = known ? card!.Summary ?? card.Detail ?? string.Empty : "The watchtower points you to this card.";
        if (!known)
        {
            // A card the stream raised belongs to the stream (its card closing ends it); only what the watchtower pointed at on its own is cleared by buddy_sleep.
            _told.Add(id);
        }
        model.Tell(new BuddyNotice(id, urgent ? NoticeKind.Trouble : NoticeKind.Ask, title, body));
        return new BuddyAnswer(true, known ? $"the cat is up and points at card {call.Card}" : $"the cat is up; card {call.Card} is not one the buddy knows, so it shows the id");
    }

    private BuddyAnswer Sleep()
    {
        // Only what the watchtower woke it with goes; an ask or failure the stream raised stays until its card closes.
        foreach (var id in _told)
        {
            model.Clear(id);
        }
        _told.Clear();
        return new BuddyAnswer(true, model.Mood == BuddyMood.Asleep ? "the cat is asleep" : "the cat still has what the node raised to show; what you told it is cleared");
    }

    private BuddyAnswer State()
    {
        var mood = model.Mood;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("running", true);
            writer.WriteString("state", mood == BuddyMood.Asleep ? "asleep" : "awake");
            writer.WriteString("mood", mood.ToString());
            writer.WriteBoolean("hidden", Hidden);
            if (LastSaid is null)
            {
                writer.WriteNull("lastSaid");
                writer.WriteNull("lastSaidAt");
            }
            else
            {
                writer.WriteString("lastSaid", LastSaid);
                writer.WriteString("lastSaidAt", LastSaidAt?.ToString("o", CultureInfo.InvariantCulture));
            }
            writer.WriteEndObject();
        }
        return new BuddyAnswer(true, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
