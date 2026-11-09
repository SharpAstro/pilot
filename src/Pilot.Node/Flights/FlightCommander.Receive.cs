using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>
    /// Takes over a flight another node hands this one (<see cref="Verbs.ReceiveFlight"/>, #376): refused unless it comes under a LAN
    /// grant, this node is out of maintenance and willing to take fleet flights, it does not have the code already (but a flight that
    /// moved away from here and comes back replaces its moved-away row, and the same hand-over again, a retry of one this node took, is
    /// answered taken without doing it twice), no leg of the flight is flying, and it has a checkout of the repo's GitHub remote. Then the plane is made from the branch the sender pushed
    /// (in this machine's checkout, at this machine's path), and the flight, its legs and its open cards are put as they were, the
    /// flight saying where it came from. Returns the code, and where it came from as the result's way.
    /// </summary>
    private async Task<(string Code, string? Way)> ReceiveAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.Grant is null)
        {
            throw new FlightException($"{Verbs.ReceiveFlight} is taken from another node of the fleet, under its grant, not from a client of {Machine}");
        }
        if (Models.Maintenance)
        {
            throw new FlightException($"{Machine} is in maintenance: it takes no new flights");
        }
        if (!Models.AcceptsFleetFlights)
        {
            throw new FlightException($"{Machine} does not take a flight another node hands it (accept-fleet-flights is off)");
        }
        if (FlightMove.Read((command.Args ?? []).FirstOrDefault()) is not { } move || move.Flight.Code.Length == 0)
        {
            throw new FlightException($"{Verbs.ReceiveFlight} takes the flight's whole record as JSON, as in {Verbs.ReceiveFlight} flight/<code> <record>");
        }
        var sent = move.Flight;
        var code = sent.Code;
        if (!string.Equals(command.Target, Verbs.FlightTarget(code), StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{command.Target} is not the flight in the record, {code}");
        }
        if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { } here)
        {
            // The same hand-over again (the sender did not hear the answer and asks once more): it was taken, so it is answered taken.
            if (await TookFromAsync(here, move, cancellationToken))
            {
                return (code, $"{code} {here.MovedFrom!.Text}");
            }
            // A flight that left this node earlier and comes back: its row here is the line it left, so it is replaced; any other row is a flight of this node's.
            if (here is not { State: FlightState.Landed, MovedTo: not null })
            {
                throw new FlightException($"{code} is already here ({here.State.ToString().ToLowerInvariant()} on {Machine}): a code is one flight on a node for good, unless the flight moved away from here and comes back");
            }
        }
        if (sent.State == FlightState.Landed)
        {
            throw new FlightException($"{code} is landed: only a flight in the air is handed over");
        }
        if (move.Legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying)
        {
            throw new FlightException($"{code}'s leg {flying.Name} is still flying on {move.From}: it is handed over once the leg has finished");
        }
        if (move.GitHub is not { Length: > 0 } gitHub)
        {
            throw new FlightException($"{code}'s repo has no GitHub remote on {move.From}, so {Machine} cannot tell which of its checkouts is it");
        }
        var checkout = Repos?.Repos().FirstOrDefault(r => string.Equals(r.GitHub, gitHub, StringComparison.OrdinalIgnoreCase))?.Path;
        var repoPath = checkout is null ? null : await planes.MainCheckoutAsync(checkout, cancellationToken);
        if (repoPath is null)
        {
            throw new FlightException($"{Machine} has no checkout of {gitHub}");
        }

        var plane = await planes.MakeFromBranchAsync(repoPath, code, sent.Branch, sent.Base, cancellationToken);
        var moved = new FlightMoved { From = move.From, AfterLeg = move.AfterLeg, At = time.GetUtcNow() };
        var taken = sent with
        {
            Repo = Path.GetFileName(repoPath),
            RepoPath = repoPath,
            Branch = plane.Branch,
            Worktree = plane.Worktree,
            Base = plane.Base,
            MovedFrom = moved,
        };
        // A held flight's waits were sent as waits on nodes (the sender's flights among them): one naming this node is a flight of its own,
        // so the flight is settled here, and flies at once when what it waited for has already landed here.
        state.PutFlight(sent.State == FlightState.Held ? Settle(WaitsHere(taken)) : taken);
        foreach (var leg in move.Legs.Where(l => string.Equals(l.Flight, code, StringComparison.Ordinal)))
        {
            state.PutLeg(leg);
        }
        foreach (var card in move.Cards.Where(c => c is { State: CardState.Open, Bin: not CardBin.Handled } && FlightMove.IsAbout(c, code)))
        {
            state.RaiseCard(card.Bin, card.Kind, card.Subject, card.Title, card.Detail, card.Choices, card.Link, card.AskedBy, card.Why, cause: card.Cause);
        }
        // The item says it flies here too (the sender takes its own label off once it has let go): a refusal is a card, not a lost flight.
        await LabelAsync(repoPath, sent.Item, code, null, cancellationToken, sent.PullRequest);
        var way = $"{code} {moved.Text}";
        state.RaiseCard(CardBin.Handled, ReceivedKind, Verbs.FlightTarget(code), way, $"{move.From} handed {code} over to {Machine}, which made its plane at {plane.Worktree} from {plane.Branch} on origin");
        return (code, way);
    }

    /// <summary>
    /// Whether <paramref name="here"/> is this very hand-over already taken: the flight flies here, it came from the same sender on the same
    /// branch, and its plane has the head the sender pushed in its history (the plane may have flown on since).
    /// </summary>
    private async Task<bool> TookFromAsync(FlightRow here, FlightMove move, CancellationToken cancellationToken) =>
        here is { State: FlightState.Flying, MovedTo: null, MovedFrom: { } from }
        && string.Equals(from.From, move.From, StringComparison.OrdinalIgnoreCase)
        && string.Equals(here.Branch, move.Flight.Branch, StringComparison.Ordinal)
        && (move.Head is not { Length: > 0 } head || await planes.HasCommitAsync(here.Worktree, head, cancellationToken));
}
