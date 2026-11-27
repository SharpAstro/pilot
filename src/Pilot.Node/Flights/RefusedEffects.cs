using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What the node saw of a refused command's effect (pilot #1353). <see cref="There"/> is true when the effect is there, false when it is
/// not, and null when the node holds no fact that says (the command cannot be checked, or the flight or session is not here).
/// <see cref="Seen"/> says what was seen, in words for a Handled line, the watchtower and a refusal.
/// </summary>
public sealed record EffectSeen(bool? There, string Seen);

/// <summary>
/// Whether a refused pilot command's effect is there, read from the node's state, and for a flight's PR from GitHub's state. The node can
/// then close a refused command's card only when the thing the command was meant to do is done, whoever did it (the person's own Run, a
/// terminal, the review loop card's own button), and never on a bare "done" it cannot check.
/// </summary>
public sealed class RefusedEffects(NodeState state, Func<PrRef, CancellationToken, Task<string?>> prState)
{
    // The verbs of RefusedCommand's table with a fact in the node's state. The rest (say, tell, a go-around, a CI budget answer) leave no
    // trace the node can read as that command's own.
    private static readonly HashSet<string> Checkable =
    [
        Verbs.AllowRound, Verbs.Merge, Verbs.Land, Verbs.FinishLeg, Verbs.Clear, Verbs.Ground, Verbs.Unground, Verbs.Connect, Verbs.Disconnect, Verbs.EndSession,
    ];

    /// <summary>Whether the node can say if this command's effect is there.</summary>
    public static bool CanCheck(PressedCommand command) => Checkable.Contains(command.Verb);

    /// <summary>The words a checked button carries as its args: the command's verb, its target, then its own args.</summary>
    public static string[] Encode(PressedCommand command) => [command.Verb, command.Target, .. command.Args];

    /// <summary>The command a checked button's args carry; null when they are too few.</summary>
    public static PressedCommand? Decode(IReadOnlyList<string>? args) =>
        args is { Count: >= 2 } ? new PressedCommand(args[0], args[1], [.. args.Skip(2)], null) : null;

    /// <summary>Whether this command's effect is there now.</summary>
    public async Task<EffectSeen> SeeAsync(PressedCommand command, CancellationToken cancellationToken)
    {
        if (!CanCheck(command))
        {
            return new EffectSeen(null, $"the node holds no fact that shows {RefusedCommand.PathOf(command.Verb)} was done, so it cannot check it");
        }
        if (command.Verb == Verbs.EndSession)
        {
            return SessionEnded(command);
        }
        var parts = (Verbs.FlightCodeOf(command.Target) ?? "").Split('/');
        var code = parts[0];
        var flight = state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));
        if (flight is null)
        {
            return new EffectSeen(null, $"this node does not hold flight {code}, so it cannot see what became of it");
        }
        var label = flight.Code;
        switch (command.Verb)
        {
            case Verbs.AllowRound:
                return LegCommander.ReviewLoopHeld(flight)
                    ? new EffectSeen(false, $"{label}'s review is still held: {LegCommander.ReviewLoopHold(flight)?.Detail}")
                    : new EffectSeen(true, $"{label}'s review is no longer held");
            case Verbs.Land:
                return flight.State == FlightState.Landed
                    ? new EffectSeen(true, $"{label} has landed")
                    : new EffectSeen(false, $"{label} has not landed");
            case Verbs.Clear:
                return flight.Holding is null
                    ? new EffectSeen(true, $"{label} is no longer held for clearance")
                    : new EffectSeen(false, $"{label} is still held for clearance");
            case Verbs.Ground:
                return flight.OnHold is not null
                    ? new EffectSeen(true, $"{label} is grounded")
                    : new EffectSeen(false, $"{label} is not grounded");
            case Verbs.Unground:
                return flight.OnHold is null
                    ? new EffectSeen(true, $"{label} is not grounded")
                    : new EffectSeen(false, $"{label} is still grounded");
            case Verbs.Connect or Verbs.Disconnect:
                var other = command.Args.FirstOrDefault() ?? "";
                var connected = flight.Connections.Any(c => string.Equals(c, other, StringComparison.OrdinalIgnoreCase))
                    || flight.PeerConnections.Any(p => string.Equals(p.Code, other, StringComparison.OrdinalIgnoreCase));
                return command.Verb == Verbs.Connect
                    ? new EffectSeen(connected, connected ? $"{label} is connected to {other}" : $"{label} is not connected to {other}")
                    : new EffectSeen(!connected, connected ? $"{label} is still connected to {other}" : $"{label} is no longer connected to {other}");
            case Verbs.FinishLeg:
                return LegFinished(flight, parts);
            case Verbs.Merge:
                return await MergedAsync(flight, cancellationToken);
            default:
                return new EffectSeen(null, "the node cannot check it");
        }
    }

    private EffectSeen LegFinished(FlightRow flight, string[] parts)
    {
        if (parts.Length < 2 || state.Legs(flight.Code).FirstOrDefault(l => string.Equals(l.Name, parts[1], StringComparison.OrdinalIgnoreCase)) is not { } leg)
        {
            return new EffectSeen(null, $"{flight.Code} has no leg {(parts.Length < 2 ? "named" : parts[1])} the node can look at");
        }
        return leg.State == LegState.Flying
            ? new EffectSeen(false, $"{flight.Code}/{leg.Name} is still flying")
            : new EffectSeen(true, $"{flight.Code}/{leg.Name} is {leg.State.ToString().ToLowerInvariant()}, no longer flying");
    }

    private async Task<EffectSeen> MergedAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.State == FlightState.Landed)
        {
            return new EffectSeen(true, $"{flight.Code} has landed");
        }
        if (flight.MergeQueue is { Length: > 0 } queue)
        {
            return new EffectSeen(true, $"{flight.Code}'s PR is {queue}");
        }
        if (PrRef.Parse(flight.PullRequestUrl) is { } pr)
        {
            string? said = null;
            try
            {
                said = await prState(pr, cancellationToken);
            }
            catch (FlightException)
            {
                // GitHub not answering leaves the card open: the next look asks again.
            }
            if (said == "MERGED")
            {
                return new EffectSeen(true, $"{pr} is merged");
            }
        }
        return new EffectSeen(false, $"{flight.Code}'s PR is not merged or queued");
    }

    private EffectSeen SessionEnded(PressedCommand command)
    {
        var id = command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.SessionTargetPrefix.Length..] : command.Target;
        if (SessionNames.Find(state.Sessions(), id) is not { } row)
        {
            return new EffectSeen(null, $"this node holds no session {id}, so it cannot see whether it ended");
        }
        return row.State is SessionState.Ended or SessionState.Failed
            ? new EffectSeen(true, $"session {id} has ended")
            : new EffectSeen(false, $"session {id} is still {row.State.ToString().ToLowerInvariant()}");
    }
}
