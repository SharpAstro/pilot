using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The overlap verbs: a planner finds its flight looks like part of another (<c>overlap</c>), the controller answers on a
/// card (<c>fold</c>, <c>separate</c>), and the answer is kept on the flight per pair, so the same pair is never asked twice.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The card an overlap rings: the flight looks like part of another, to fold in or to plan separately.</summary>
    public const string OverlapKind = "overlap";

    /// <summary>
    /// <c>overlap flight/&lt;code&gt; &lt;other&gt; &lt;why…&gt;</c>: returns what the planner is told. A new pair is recorded
    /// pending and rings one card; a pair asked before answers with what is recorded, and rings nothing.
    /// </summary>
    private string AskOverlap(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; it overlaps nothing now");
        }
        var other = args.Length > 0
            ? state.Flights().FirstOrDefault(f => string.Equals(f.Code, args[0], StringComparison.OrdinalIgnoreCase))
            : null;
        if (other is null)
        {
            throw new FlightException(args.Length > 0
                ? $"There is no flight {args[0]}"
                : $"overlap needs the code of the flight {flight.Code} looks like part of, and why, as in overlap flight/{flight.Code} TIA1611 <why>");
        }
        if (other.Code == flight.Code)
        {
            throw new FlightException($"{flight.Code} cannot overlap itself");
        }
        if (other.State == FlightState.Landed)
        {
            throw new FlightException($"{other.Code} has landed: its work is in, so build on it instead of folding into it");
        }
        var why = string.Join(' ', args.Skip(1)).Trim();

        if (flight.Overlaps.FirstOrDefault(o => o.Other == other.Code) is { } known)
        {
            return known.Answer switch
            {
                OverlapAnswer.Pending => $"{flight.Code} and {other.Code} are asked about already{CardSaid(flight)}: end your turn, you are started again with the answer",
                OverlapAnswer.Separate => $"{flight.Code} and {other.Code} were answered before: plan {flight.Code} separately, and connect it to {other.Code} (flight connect) only if it still depends on it; no card rings again",
                _ => $"{flight.Code} and {other.Code} were answered before: fold {flight.Code} into {other.Code}; no card rings again",
            };
        }
        if (why.Length == 0)
        {
            throw new FlightException($"overlap needs why {flight.Code} looks like part of {other.Code}, as in overlap flight/{flight.Code} {other.Code} <why>");
        }
        if (flight.Overlaps.FirstOrDefault(o => o.Answer == OverlapAnswer.Pending) is { } waiting)
        {
            throw new FlightException($"{flight.Code} waits for the answer on {waiting.Other}{CardSaid(flight)}; ask about {other.Code} once that is answered");
        }

        state.PutFlight(flight with { Overlaps = [.. flight.Overlaps, new OverlapRow { Other = other.Code, Why = why, Answer = OverlapAnswer.Pending, At = time.GetUtcNow() }] });
        var card = state.RaiseCard(CardBin.Ring, OverlapKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} looks like part of {other.Code}: {why}",
            $"{flight.Code}'s planner holds its legs until you answer. Fold takes it into {other.Code}; Plan separately keeps it a flight of its own.",
            [
                new CardChoice { Label = $"Fold into {other.Code}", Verb = Verbs.Fold, Target = Verbs.FlightTarget(flight.Code), Args = [other.Code] },
                new CardChoice { Label = "Plan separately", Verb = Verbs.Separate, Target = Verbs.FlightTarget(flight.Code), Args = [other.Code] },
            ]);
        return $"asked the controller on card {card.Id}: {flight.Code} looks like part of {other.Code}. Plan no leg and say no planned: end your turn now, you are started again with the answer";
    }

    /// <summary>
    /// <c>separate flight/&lt;code&gt; &lt;other&gt;</c>: records the answer, closes the card, stops the planner that waits and
    /// starts a fresh one, whose brief says what was answered. Returns what the caller is told.
    /// </summary>
    private async Task<string> SeparateAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        var known = args.Length > 0 ? flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, args[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (known is null)
        {
            throw new FlightException($"{flight.Code} has no overlap with {(args.Length > 0 ? args[0] : "another flight")} to answer: the planner asks with overlap first");
        }
        if (known.Answer == OverlapAnswer.Separate)
        {
            return $"{flight.Code} is planned separately from {known.Other} already";
        }
        if (known.Answer != OverlapAnswer.Pending)
        {
            throw new FlightException($"{flight.Code} and {known.Other} were answered {known.Answer.ToString().ToLowerInvariant()} already");
        }
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }

        flight = flight with { Overlaps = [.. flight.Overlaps.Select(o => o.Other == known.Other ? o with { Answer = OverlapAnswer.Separate, At = time.GetUtcNow() } : o)] };
        state.PutFlight(flight);
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"plan separately from {known.Other}", OverlapKind);

        // The planner that asked is waiting for its turn to end: stopped, so a fresh one is told the answer. Its start is not
        // refused for the stopped one, which the tracker may list for a poll yet.
        var planner = $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{PlanLeg}";
        var jobs = state.Sessions().Where(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase)
                && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).Select(s => s.Id)
            .Concat(_plannerJobs.TryGetValue(flight.Code, out var job) ? new[] { job } : []).Distinct().ToList();
        foreach (var running in jobs)
        {
            await StopQuietly(running, cancellationToken);
        }
        _plannerJobs.TryRemove(flight.Code, out _);
        _plannersStarted.TryRemove(flight.Code, out _);
        await StartPlannerAsync(flight, cancellationToken, replacing: true);
        return $"{flight.Code} is planned separately from {known.Other}; a fresh planner is started with the answer";
    }

    /// <summary>Refuses what plans the flight's legs while a question about an overlap waits for its answer.</summary>
    private void HoldForOverlap(FlightRow flight)
    {
        if (flight.Overlaps.FirstOrDefault(o => o.Answer == OverlapAnswer.Pending) is { } waiting)
        {
            throw new FlightException($"{flight.Code} looks like part of {waiting.Other} and waits for the controller's answer{CardSaid(flight)}: plan no leg until a fresh planner is started with it; end your turn");
        }
    }

    private string CardSaid(FlightRow flight) =>
        state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == OverlapKind && c.Subject == Verbs.FlightTarget(flight.Code)) is { } card
            ? $" (card {card.Id})"
            : "";
}
