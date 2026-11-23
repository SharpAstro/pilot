using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>
    /// <paramref name="choices"/> as they read against the flights and sessions the node holds now (<see cref="ChoiceAvailability.Apply"/>):
    /// every write of a card's choices goes through it, so a card raised, turned or refreshed while its flight is held (or its session
    /// gone) says so at once, and a writer that rebuilds its choices from scratch (a merge's checks) never drops the mark. Under the lock.
    /// </summary>
    private CardChoice[] Available(IReadOnlyList<CardChoice> choices, string? cardKind = null) =>
        ChoiceAvailability.Apply(choices, _flights.Values, [.. _legs.Values], [.. _cards.Values], _sessions.Values, cardKind);

    /// <summary>
    /// PIL1325: why a card with these choices needs nothing from a person: every choice is refused by its command for a reason that clears
    /// by itself (<see cref="VerbRefusal.ClearsByItself"/>). Null for a card with no choices, one with a choice that is available or a chip,
    /// or one refused for a reason that needs a person (the owner's hold, a gone session). Under the lock.
    /// </summary>
    private string? SelfClearingReason(IReadOnlyList<CardChoice> choices, string? cardKind)
    {
        if (choices.Count == 0)
        {
            return null;
        }
        var reasons = new List<string>();
        foreach (var choice in choices)
        {
            if (ChoiceStatus.IsChip(choice.Status)
                || ChoiceAvailability.Refusal(choice, _flights.Values, [.. _legs.Values], [.. _cards.Values], _sessions.Values, cardKind) is not { ClearsByItself: true } refusal)
            {
                return null;
            }
            if (!reasons.Contains(refusal.Why))
            {
                reasons.Add(refusal.Why);
            }
        }
        return string.Join("; ", reasons);
    }

    /// <summary>
    /// Brings every open card's choices in line with what the node would refuse now (#792 point 4): a choice whose flight turned held or whose
    /// session is gone is marked <see cref="ChoiceStatus.Unavailable"/> with the reason, one whose flight flies again or whose session is back
    /// is cleared. One <see cref="CardChangedEvent"/> per card changed, none for a card already right. Called where those facts change (a
    /// flight's hold, a session row added or removed), never on a timer. Under the lock.
    /// </summary>
    private void SyncChoiceAvailability()
    {
        foreach (var card in _cards.Values.Where(c => c.State == CardState.Open && c.Choices.Length > 0).ToList())
        {
            var next = Available(card.Choices, card.Kind);
            // PIL1325: a For you or Ring card whose every choice now waits on something that clears by itself needs no person: it closes
            // with the reason, and is not reopened when the reason clears (the node's own step takes over then).
            if (card.Bin is CardBin.ForYou or CardBin.Ring && SelfClearingReason(next, card.Kind) is { } reason)
            {
                var now = _time.GetUtcNow();
                Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { Choices = next, State = CardState.Closed, Outcome = reason, ClosedBy = "node", ClosedAt = now, Answering = null } });
                continue;
            }
            if (!next.SequenceEqual(card.Choices))
            {
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Choices = next } });
            }
        }
    }

    // Whether what a flight is held for changed between two rows: its state, or what it waits on (the reason a card gives).
    // PIL1325: and what the verb predicates read of it: its review state, its owner's hold, its PR and its holding pattern.
    private static bool AvailabilityChanged(FlightRow before, FlightRow after) =>
        before.State != after.State || (after.State == FlightState.Held && FlightWaiting.For(before) != FlightWaiting.For(after))
        || before.Review?.State != after.Review?.State || before.OnHold != after.OnHold || before.PullRequestUrl != after.PullRequestUrl;
}
