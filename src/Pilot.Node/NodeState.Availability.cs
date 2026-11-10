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
    private CardChoice[] Available(IReadOnlyList<CardChoice> choices) => ChoiceAvailability.Apply(choices, _flights.Values, _sessions.Values);

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
            var next = Available(card.Choices);
            if (!next.SequenceEqual(card.Choices))
            {
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Choices = next } });
            }
        }
    }

    // Whether what a flight is held for changed between two rows: its state, or what it waits on (the reason a card gives).
    private static bool HoldChanged(FlightRow before, FlightRow after) =>
        before.State != after.State || (after.State == FlightState.Held && FlightWaiting.For(before) != FlightWaiting.For(after));
}
