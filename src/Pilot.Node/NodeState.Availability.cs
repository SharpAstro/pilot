using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>What the marks are read from now (<see cref="ChoiceAvailability.Facts"/>): taken once per pass, never per choice. Under the lock.</summary>
    private ChoiceAvailability.Facts AvailabilityFacts() => new(_flights.Values, _legs.Values, _cards.Values, _sessions.Values);

    /// <summary>
    /// <paramref name="choices"/> as they read against the flights and sessions the node holds now (<see cref="ChoiceAvailability.Apply"/>):
    /// every write of a card's choices goes through it, so a card raised, turned or refreshed while its flight is held (or its session
    /// gone) says so at once, and a writer that rebuilds its choices from scratch (a merge's checks) never drops the mark. Under the lock.
    /// <paramref name="facts"/> is the pass's own reading when a pass marks many cards; null reads the state now.
    /// </summary>
    private CardChoice[] Available(IReadOnlyList<CardChoice> choices, string? cardKind = null, ChoiceAvailability.Facts? facts = null) =>
        ChoiceAvailability.Apply(choices, facts ?? AvailabilityFacts(), cardKind);

    /// <summary>
    /// PIL1325: why a card with these choices needs nothing from a person: every choice is refused by its command for a reason that clears
    /// by itself (<see cref="VerbRefusal.ClearsByItself"/>). Null for a card with no choices, one with a choice that is available or a chip,
    /// or one refused for a reason that needs a person (the owner's hold, a gone session). Under the lock.
    /// </summary>
    private string? SelfClearingReason(IReadOnlyList<CardChoice> choices, string? cardKind, ChoiceAvailability.Facts? facts = null)
    {
        if (choices.Count == 0)
        {
            return null;
        }
        facts ??= AvailabilityFacts();
        var reasons = new List<string>();
        foreach (var choice in choices)
        {
            if (ChoiceStatus.IsChip(choice.Status) || ChoiceAvailability.Refusal(choice, facts, cardKind) is not { ClearsByItself: true } refusal)
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
    /// The card kinds whose opening or closing changes what a choice's predicate reads (<see cref="VerbPreconditions"/>): a design question
    /// refuses a Merge on its PR, an open PR-ready or merge-failed card offering a go-around lets a done leg go round. A card of these kinds
    /// that opens or closes re-marks every open card (#1325's review, R1.4).
    /// </summary>
    internal static bool GatesChoices(string kind) => kind is global::Pilot.Node.Flights.PrReviews.DesignKind or LegCommander.PrReady or LegCommander.MergeFailed;

    // A sync under way, and whether something it did (a card it closed) asks for another pass with what that changed.
    private bool _syncingChoices;
    private bool _syncChoicesAgain;

    /// <summary>
    /// Brings every open card's choices in line with what the node would refuse now (#792 point 4): a choice whose flight turned held or whose
    /// session is gone is marked <see cref="ChoiceStatus.Unavailable"/> with the reason, one whose flight flies again or whose session is back
    /// is cleared. One <see cref="CardChangedEvent"/> per card changed, none for a card already right. Called where those facts change (a
    /// flight's hold, a session row added or removed, a gating card opened or closed), never on a timer. Under the lock. A call made while
    /// a pass runs (the pass closed a gating card) is not nested: the pass runs once more with the state that close left.
    /// </summary>
    private void SyncChoiceAvailability()
    {
        if (_syncingChoices)
        {
            _syncChoicesAgain = true;
            return;
        }
        _syncingChoices = true;
        try
        {
            // Each pass reads the state once; one that changed a gating card is followed by one more. A pass only closes or re-marks open
            // cards, so the passes end: the cap is a guard against a bug, not a limit a real state reaches.
            for (var pass = 0; pass < 8; pass++)
            {
                _syncChoicesAgain = false;
                SyncChoiceAvailabilityPass();
                if (!_syncChoicesAgain)
                {
                    break;
                }
            }
        }
        finally
        {
            _syncingChoices = false;
            _syncChoicesAgain = false;
        }
    }

    private void SyncChoiceAvailabilityPass()
    {
        var facts = AvailabilityFacts();
        foreach (var id in facts.OpenCards.Where(c => c.Choices.Length > 0).Select(c => c.Id).ToList())
        {
            // Re-read: a card closed earlier in this pass may have closed this one with it.
            if (_cards[id] is not { State: CardState.Open } card)
            {
                continue;
            }
            var next = Available(card.Choices, card.Kind, facts);
            // PIL1325: a For you or Ring card whose every choice now waits on something that clears by itself needs no person: it closes
            // with the reason, and is not reopened when the reason clears (the node's own step takes over then).
            if (card.Bin is CardBin.ForYou or CardBin.Ring && SelfClearingReason(next, card.Kind, facts) is { } reason)
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
    // PIL1325: and what the verb predicates read of the flight row: its review state, its owner's hold and its PR url.
    private static bool AvailabilityChanged(FlightRow before, FlightRow after) =>
        before.State != after.State || (after.State == FlightState.Held && FlightWaiting.For(before) != FlightWaiting.For(after))
        || before.Review?.State != after.Review?.State || before.OnHold != after.OnHold || before.PullRequestUrl != after.PullRequestUrl;
}
