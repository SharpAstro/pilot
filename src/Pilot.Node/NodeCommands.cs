using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// The one command route: every command, from a client or a card's button, goes through here to the part of the node
/// that does it. A card's choice is a command like any other, so answering a card cannot do anything a client could
/// not do by hand.
/// </summary>
public sealed class NodeCommands(IServiceProvider services, NodeState state)
{
    /// <summary>Runs <paramref name="command"/>; its result is on the stream too. Long work (a say, git, a merge) runs off the state's lock.</summary>
    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        // The watchtower handles what it may; a merge or a landing is the controller's, by any route, a card's included.
        if (command.Verb is Verbs.Merge or Verbs.Land or Verbs.Review && string.Equals(command.Who, Get<Watchtower>().Name, StringComparison.Ordinal))
        {
            return state.Complete(command, $"{command.Verb} is the controller's, not the watchtower's: leave its card open for them");
        }
        if (command.Verb == Verbs.Tell)
        {
            var (target, why) = Get<Watchtower>().TellTarget();
            if (target is null)
            {
                return state.Complete(command, why);
            }
            var said = await Get<SessionMessenger>().SayAsync(command with { Verb = Verbs.Say, Target = target }, cancellationToken);
            return state.Complete(command, said.Error, said.Way, said.SessionId);
        }
        if (command.Verb == Verbs.RestartWatchtower)
        {
            var why = string.Join(' ', command.Args ?? []).Trim();
            return state.Complete(command, await Get<Watchtower>().RestartAsync(why.Length > 0 ? $"restarted by {command.Who}: {why}" : $"restarted by {command.Who}", cancellationToken));
        }
        if (command.Verb == Verbs.Raise)
        {
            return Raise(command);
        }
        if (command.Verb == Verbs.AnswerGrant)
        {
            var args = command.Args ?? [];
            if (args.Length < 2 || args[1] is not ("allow" or "deny"))
            {
                return state.Complete(command, "answer-grant takes the ask's id, then allow or deny");
            }
            return state.Complete(command, await Get<NodeLan>().AnswerAsync(args[0], args[1] == "allow", cancellationToken));
        }
        if (command.Verb == Verbs.RevokeGrant)
        {
            var args = command.Args ?? [];
            return state.Complete(command, args.Length == 0 ? "revoke-grant takes the grant's id (pilot grants lists them)" : await Get<NodeLan>().RevokeAsync(args[0], cancellationToken));
        }
        if (command.Verb == Verbs.Run)
        {
            return Get<JobRunner>().Run(command);
        }
        if (command.Verb == Verbs.CancelJob)
        {
            return Get<JobRunner>().Cancel(command);
        }
        if (command.Verb == Verbs.Digest)
        {
            Get<Digest>().Raise(Get<TimeProvider>().GetUtcNow());
            return state.Complete(command, null);
        }
        if (command.Verb == Verbs.Update)
        {
            return await Get<NodeUpdater>().RunAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.Say)
        {
            return await Get<SessionMessenger>().SayAsync(command, cancellationToken);
        }
        if (FlightCommander.Handles(command.Verb))
        {
            return await Get<FlightCommander>().RunAsync(command, cancellationToken);
        }
        if (LegCommander.Handles(command.Verb))
        {
            return await Get<LegCommander>().RunAsync(command, cancellationToken);
        }
        if (command.Verb is Verbs.Answer or Verbs.Dismiss)
        {
            return await AnswerAsync(command, cancellationToken);
        }
        return state.Execute(command);
    }

    /// <summary>
    /// <c>answer</c> runs the chosen button's command as the one answering, and closes the card when it was done; a
    /// refused command leaves the card open, its reason in the answer's error. <c>dismiss</c> closes it with nothing done.
    /// </summary>
    private async Task<CommandResult> AnswerAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var id = command.Target.StartsWith(Verbs.CardTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.CardTargetPrefix.Length..] : null;
        var card = id is null ? null : state.Cards().FirstOrDefault(c => c.Id == id);
        var args = command.Args ?? [];
        if (card is null)
        {
            return state.Complete(command, id is null ? $"this verb takes a card target, {Verbs.CardTargetPrefix}<id>, not {command.Target}" : $"There is no card {id}");
        }
        if (card.State != CardState.Open)
        {
            return state.Complete(command, $"card {card.Id} is closed already ({card.Outcome})");
        }

        if (command.Verb == Verbs.Dismiss)
        {
            var why = string.Join(' ', args).Trim();
            state.CloseCard(card.Id, why.Length > 0 ? $"dismissed: {why}" : "dismissed", command.Who);
            return state.Complete(command, null);
        }

        var which = string.Join(' ', args).Trim();
        var choice = int.TryParse(which, out var number) && number >= 1 && number <= card.Choices.Length
            ? card.Choices[number - 1]
            : card.Choices.FirstOrDefault(c => string.Equals(c.Label, which, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            return state.Complete(command, card.Choices.Length == 0
                ? $"card {card.Id} has no choices; dismiss it"
                : $"card {card.Id} offers {string.Join(", ", card.Choices.Select((c, i) => $"{i + 1} {c.Label}"))}, not '{which}'");
        }

        // Closed by the answer before the choice runs: a merge lands its flight, and landing closes the flight's cards,
        // this one included, as landed rather than as the answer it was. Refused, it opens again.
        state.CloseCard(card.Id, choice.Label, command.Who);
        var result = await RunAsync(new CommandRequest { Verb = choice.Verb, Target = choice.Target, Args = choice.Args, Who = command.Who }, cancellationToken);
        if (!result.Ok)
        {
            state.ReopenCard(card.Id);
            return state.Complete(command, $"{choice.Label} was refused: {result.Error}");
        }
        return state.Complete(command, null);
    }

    /// <summary><c>raise &lt;subject&gt; ring|for-you|handled &lt;title&gt; [detail…]</c>: a card from whoever raises it.</summary>
    private CommandResult Raise(CommandRequest command)
    {
        var args = command.Args ?? [];
        CardBin? bin = args.Length > 0 ? args[0] switch { "ring" => CardBin.Ring, "for-you" => CardBin.ForYou, "handled" => CardBin.Handled, _ => null } : null;
        if (bin is null || args.Length < 2 || args[1].Trim().Length == 0)
        {
            return state.Complete(command, "raise takes ring, for-you or handled, then a title, then a detail if there is one");
        }
        var detail = string.Join(' ', args.Skip(2)).Trim();
        var title = args[1].Trim();
        var from = string.Equals(command.Who, "cli", StringComparison.Ordinal) ? "" : $" (from {command.Who})";
        state.RaiseCard(bin.Value, Watchtower.RaisedKind, command.Target, title, (detail.Length > 0 ? detail : null) is { } d ? d + from : from.Length > 0 ? from.Trim() : null);
        return state.Complete(command, null);
    }

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
}
