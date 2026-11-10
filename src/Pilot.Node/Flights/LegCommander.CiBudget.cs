using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>PIL795: the stop a flight's CI spend puts on its pushes, and the owner's answer to it.</summary>
public sealed partial class LegCommander
{
    /// <summary>
    /// The refusal a push gets while the flight's CI is capped (<see cref="CiBudgets"/>): the numbers, and what answers the card. Null
    /// while it may push. The flight is read from the node's state, not from the row the caller holds, so a stop set meanwhile counts.
    /// </summary>
    private string? CiStop(FlightRow flight) =>
        state.Flights().FirstOrDefault(f => f.Code == flight.Code)?.Ci is { CappedWhy: { } why }
            ? $"{flight.Branch} is not pushed: {why}. The node stops pushing for a flight past its CI budget; the For-you card offers continue (one more budget), land what is green, or split: `pilot flight ci-budget {flight.Code} continue`"
            : null;

    /// <summary>
    /// <c>ci-budget</c>: the person's answer to a flight whose CI is capped. <c>continue</c> raises the cap by one budget and lets
    /// pushes through again (a red stop counts from now); <c>land-green</c> and <c>split</c> are recorded on the flight for the controller, who
    /// lands what is green or splits the flight, and the stop stays. The card closes either way.
    /// </summary>
    private string AnswerCiBudget(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (!IsPerson(flight, command.Who))
        {
            throw new FlightException("only the person may answer a CI budget: the stop is theirs to lift");
        }
        if (flight.Ci is not { CappedWhy: not null } ci)
        {
            throw new FlightException($"{flight.Code}'s CI is not capped: nothing to answer");
        }
        var answer = (command.Args ?? []).FirstOrDefault()?.Trim().ToLowerInvariant();
        var subject = Verbs.FlightTarget(flight.Code);
        string made;
        switch (answer)
        {
            case Verbs.CiContinue:
                Record(flight.Code, f => f with { Ci = ci with { Extra = ci.Extra + 1, CappedWhy = null, CappedAt = null, Answer = null, RedAcknowledgedAt = time.GetUtcNow() } });
                made = $"{flight.Code} may push again: its CI cap is now {Models.FlightCiBudget * (1 + ci.Extra + 1)} runner-hours";
                break;
            case Verbs.CiLandGreen or Verbs.CiSplit:
                Record(flight.Code, f => f with { Ci = ci with { Answer = answer } });
                made = $"{flight.Code}'s owner answered {answer}: recorded for the controller; pushes stay stopped";
                state.RaiseCard(CardBin.Handled, CiBudgets.AnsweredKind, subject, $"{flight.Code}'s CI budget: the owner said {(answer == Verbs.CiSplit ? "split it" : "land what is green")}",
                    $"{CiBudgets.Numbers(ci)}. Pushes stay stopped; the controller {(answer == Verbs.CiSplit ? "splits the flight" : "lands what is green")}.");
                break;
            default:
                throw new FlightException($"ci-budget takes {Verbs.CiContinue}, {Verbs.CiLandGreen} or {Verbs.CiSplit}");
        }
        state.CloseCardsAbout(subject, $"{command.Who} answered {answer}", CiBudgets.CapKind);
        return made;
    }
}
