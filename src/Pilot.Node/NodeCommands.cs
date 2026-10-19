using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;

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

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();
}
