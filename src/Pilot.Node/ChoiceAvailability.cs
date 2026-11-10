using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Which of a card's choices the node would refuse right now, and why (#792 point 4): one rule, read two ways. The node marks such a
/// choice <see cref="ChoiceStatus.Unavailable"/> (its <see cref="CardChoice.Note"/> the reason) before anyone presses it, so every client
/// draws it dead with the reason beside it; and an answer of it is refused with the same words at answer time, for a client that pressed
/// before it saw the mark. Two things refuse today: a choice that needs its flight flying (a go-around, a merge, a re-run of its checks,
/// a leg's start) while the flight is held, and a choice that names a session the node no longer has.
/// </summary>
internal static class ChoiceAvailability
{
    /// <summary>The verbs whose node-side command refuses a flight that is not flying (<c>StartAsync</c>, <c>GoAroundAsync</c>, <c>Mergeable</c>).</summary>
    internal static bool NeedsFlyingFlight(string verb) => verb is Verbs.StartLeg or Verbs.GoAround or Verbs.Merge or Verbs.RerunChecks;

    /// <summary>Why the node would refuse <paramref name="choice"/> now, in the words the card and the refusal share; null when nothing stops it.</summary>
    internal static string? WhyNot(CardChoice choice, IEnumerable<FlightRow> flights, IReadOnlyCollection<SessionRow> sessions)
    {
        if (NeedsFlyingFlight(choice.Verb) && choice.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            var code = choice.Target[Verbs.FlightTargetPrefix.Length..].Split('/')[0];
            if (flights.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { State: FlightState.Held } held)
            {
                return HeldReason(held);
            }
        }
        if (choice.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal))
        {
            var id = choice.Target[Verbs.SessionTargetPrefix.Length..];
            if (id.Length > 0 && SessionNames.Find(sessions, id) is null)
            {
                return $"session {id} no longer exists";
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
    internal static CardChoice[] Apply(IReadOnlyList<CardChoice> choices, IEnumerable<FlightRow> flights, IReadOnlyCollection<SessionRow> sessions)
    {
        var flightList = flights as IReadOnlyCollection<FlightRow> ?? [.. flights];
        var result = new CardChoice[choices.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var choice = choices[i];
            result[i] = choice;
            if (WhyNot(choice, flightList, sessions) is { } why)
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
