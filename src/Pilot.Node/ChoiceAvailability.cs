using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

/// <summary>
/// Which of a card's choices the node would refuse right now, and why (#792 point 4): one rule, read two ways. The node marks such a
/// choice <see cref="ChoiceStatus.Unavailable"/> (its <see cref="CardChoice.Note"/> the reason) before anyone presses it, so every client
/// draws it dead with the reason beside it; and an answer of it is refused with the same words at answer time, for a client that pressed
/// before it saw the mark. What refuses: a flight or leg verb its command's precondition (<see cref="VerbPreconditions"/>) refuses, the owner's hold for
/// the verbs <see cref="LegCommander"/> gates (<see cref="LegCommander.Handles"/>), and a choice that names a session the node no longer has.
/// </summary>
internal static class ChoiceAvailability
{
    /// <summary>The verbs whose node-side command refuses a flight that is not flying (<c>StartAsync</c>, <c>GoAroundAsync</c>, <c>Mergeable</c>).</summary>
    internal static bool NeedsFlyingFlight(string verb) => verb is Verbs.StartLeg or Verbs.GoAround or Verbs.Merge or Verbs.RerunChecks;

    /// <summary>
    /// What the marks are read from, taken once per pass (a raise, a sync of every open card, an answer): the flights by code, the legs by
    /// flight, the open cards and the sessions. A pass over many choices reads these rather than copying and filtering the node's state
    /// per choice (#1325's review, R1.5).
    /// </summary>
    internal sealed class Facts
    {
        private readonly Dictionary<string, FlightRow> _flights;
        private readonly Dictionary<string, LegRow[]> _legs;

        internal Facts(IEnumerable<FlightRow> flights, IEnumerable<LegRow> legs, IEnumerable<CardRow> cards, IReadOnlyCollection<SessionRow> sessions)
        {
            _flights = new Dictionary<string, FlightRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var flight in flights)
            {
                _flights[flight.Code] = flight;
            }
            _legs = legs.GroupBy(l => l.Flight, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            OpenCards = [.. cards.Where(c => c.State == CardState.Open)];
            Sessions = sessions;
        }

        internal IReadOnlyCollection<CardRow> OpenCards { get; }

        internal IReadOnlyCollection<SessionRow> Sessions { get; }

        internal FlightRow? Flight(string code) => _flights.GetValueOrDefault(code);

        internal IReadOnlyCollection<LegRow> LegsOf(string code) => _legs.GetValueOrDefault(code) ?? [];
    }

    /// <summary>Why the node would refuse <paramref name="choice"/> now, in the words the card and the refusal share; null when nothing stops it.</summary>
    internal static string? WhyNot(CardChoice choice, Facts facts, string? cardKind = null) =>
        Refusal(choice, facts, cardKind)?.Why;

    /// <summary>
    /// Like <see cref="WhyNot"/> but with the refusal whole: whether its cause clears by itself. A flight or leg target is judged by
    /// <see cref="VerbPreconditions"/> by verb, the very predicate its command calls first; a card press is judged as the person's.
    /// </summary>
    internal static VerbRefusal? Refusal(CardChoice choice, Facts facts, string? cardKind = null)
    {
        if (choice.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            var parts = choice.Target[Verbs.FlightTargetPrefix.Length..].Split('/', 2);
            if (facts.Flight(parts[0]) is { } flight)
            {
                if (NeedsFlyingFlight(choice.Verb) && flight.State == FlightState.Held)
                {
                    return new VerbRefusal(HeldReason(flight));
                }
                if (LegCommander.Handles(choice.Verb) && !LegCommander.TakenOnHold(choice.Verb, choice.Args) && VerbPreconditions.OnHold(flight) is { } onHold)
                {
                    return new VerbRefusal(onHold);
                }
                var ofFlight = facts.LegsOf(flight.Code);
                var leg = parts.Length > 1 ? ofFlight.FirstOrDefault(l => l.Name == parts[1]) : null;
                if (VerbPreconditions.For(choice.Verb, flight, leg, ofFlight, facts.OpenCards, doneLegGoesRound: cardKind is LegCommander.PrReady or LegCommander.MergeFailed) is { } refused)
                {
                    return refused;
                }
            }
        }
        if (choice.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal))
        {
            var id = choice.Target[Verbs.SessionTargetPrefix.Length..];
            if (id.Length > 0 && SessionNames.Find(facts.Sessions, id) is null)
            {
                return new VerbRefusal($"session {id} no longer exists");
            }
        }
        return null;
    }

    /// <summary>What a held flight says it is held for: <c>PIL501 is held: waiting for PIL500</c>, or just that it is held when no wait is named.</summary>
    internal static string HeldReason(FlightRow flight) =>
        FlightWaiting.For(flight) is { Length: > 0 } waiting ? $"{flight.Code} is held: waiting for {waiting}" : $"{flight.Code} is held";

    /// <summary>
    /// <paramref name="choices"/> as they should read now: a choice the node would refuse is marked unavailable with its reason, one marked
    /// that no longer would be is cleared. A choice that already says what it should is kept as it is (the same instance), so a card
    /// whose choices are all right is not rewritten. A choice that is a chip for another reason (<see cref="ChoiceStatus.IsChip"/>: a merge
    /// armed, queued, merging, merged or waiting for its legs, one GitHub blocks) keeps that status: it is not a button, and what it says is
    /// the more telling. A choice marked unavailable over another status (a Merge's Passing or Pending checks) is cleared to no status when
    /// the mark goes, so it reads blank until the next look at the PR writes its checks again; the mark does not keep what it covered.
    /// </summary>
    internal static CardChoice[] Apply(IReadOnlyList<CardChoice> choices, Facts facts, string? cardKind = null)
    {
        var result = new CardChoice[choices.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var choice = choices[i];
            result[i] = choice;
            if (WhyNot(choice, facts, cardKind) is { } why)
            {
                if (!ChoiceStatus.IsChip(choice.Status) && !(choice.Status == ChoiceStatus.Unavailable && choice.Note == why))
                {
                    result[i] = choice with { Status = ChoiceStatus.Unavailable, Note = why };
                }
            }
            else if (choice.Status == ChoiceStatus.Unavailable)
            {
                result[i] = choice with { Status = null, Note = null };
            }
        }
        return result;
    }
}
