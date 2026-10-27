using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;

namespace Pilot.Node;

/// <summary>
/// What becomes of an assistant's your-only command (#208, see <see cref="Standing"/>): with the autopilot off the node does not
/// run it but raises a For-you card, <c>&lt;session&gt; asks to merge #63 (TIA1): Allow · Deny</c>, whose Allow runs the same
/// command as whoever presses it (the person: answering it is theirs alone) and whose Deny closes it, and either way the
/// asking session is told what the person answered; with it on the command runs at once, recorded with the session and the
/// message it acted on, and a Handled card says what was done.
/// </summary>
public sealed class Autopilot(NodeState state, IServiceProvider services)
{
    public const string AllowLabel = "Allow";
    public const string DenyLabel = "Deny";

    /// <summary>
    /// The card for <paramref name="command"/>, an assistant's your-only command not to be run yet; its result is a refusal
    /// saying it waits on that card. The same command asked again while its card is open is that card; another command about
    /// the same thing (a landing after a merge) replaces it.
    /// </summary>
    public CommandResult Ask(CommandRequest command)
    {
        var subject = Subject(command);
        var open = state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == CardKinds.AssistantAsk && c.Subject == subject);
        if (open is not null && !(open.Choices.FirstOrDefault() is { } allowed && Same(allowed, command)))
        {
            state.CloseCard(open.Id, "replaced by a newer ask", "node");
            open = null;
        }
        if (open is null)
        {
            var why = command.Why?.Trim();
            var card = state.RaiseCard(CardBin.ForYou, CardKinds.AssistantAsk, subject, $"{command.Who} asks to {Describe(command).Ask}",
                why is { Length: > 0 } ? $"Acting on: {why}" : null,
                [new CardChoice { Label = AllowLabel, Verb = command.Verb, Target = command.Target, Args = command.Args }],
                askedBy: command.Who, why: why is { Length: > 0 } ? why : null);
            // Deny needs the card's own id, which is only known now.
            state.UpdateChoices(card.Id, [.. card.Choices, new CardChoice { Label = DenyLabel, Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id), Args = ["denied"] }]);
            open = card;
        }
        else
        {
            // Another session asking the same thing is told what the person answers too, not only the first to ask.
            state.AddAsker(open.Id, command.Who);
        }
        return state.Complete(command, $"{command.Verb} is the person's: it waits on their card {open.Id} ('{open.Title}'), and you are told what they answer");
    }

    /// <summary>Says on a Handled card that <paramref name="command"/> was run on the autopilot for its session, with the message it acted on.</summary>
    public void Handled(CommandRequest command)
    {
        var why = command.Why?.Trim();
        state.RaiseCard(CardBin.Handled, CardKinds.AutopilotRan, Subject(command), $"{command.Who} {Describe(command).Done} on autopilot",
            why is { Length: > 0 } ? $"Acting on: {why}" : "No message given", askedBy: command.Who, why: why is { Length: > 0 } ? why : null);
    }

    /// <summary>
    /// Tells the session that asked what the person answered on <paramref name="card"/>. A session the node no longer
    /// knows (an MCP client with no row, one that ended) cannot be told, and the card says so; a say that fails is said there too.
    /// </summary>
    public async Task TellAsync(CardRow card, bool allowed, string answeredBy, CancellationToken cancellationToken)
    {
        var what = card.Choices.FirstOrDefault(c => c.Label == AllowLabel) is { } allow ? Describe(allow.Verb, allow.Target, allow.Args).Ask : card.Title;
        var text = allowed
            ? $"{answeredBy} allowed your request to {what}, and it is done. Carry on from here."
            : $"{answeredBy} denied your request to {what}. It was not done: do not ask again unless they say so.";
        foreach (var asker in new[] { card.AskedBy }.Concat(card.AlsoAskedBy).OfType<string>().Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await TellOneAsync(card, asker, text, allowed, cancellationToken);
        }
    }

    private async Task TellOneAsync(CardRow card, string asker, string text, bool allowed, CancellationToken cancellationToken)
    {
        var session = state.Sessions()
            .Where(s => s.State != SessionState.Ended && string.Equals(s.Name, asker, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.StartedAt).FirstOrDefault();
        if (session is null)
        {
            state.NoteOutcome(card.Id, $"{(allowed ? "Allow" : "Deny")}; {asker} is no session the node knows, so it was not told");
            return;
        }
        var said = await services.GetRequiredService<SessionMessenger>().SayAsync(
            new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(session.Id), Args = [text], Who = LegWatch.Who }, cancellationToken);
        if (!said.Ok)
        {
            state.NoteOutcome(card.Id, $"{(allowed ? "Allow" : "Deny")}; {asker} could not be told: {said.Error}");
        }
    }

    /// <summary>What a card is about: the flight, PR or card the command is on, so a landing closes the flight's asks; a node-wide command by verb (and a setting by key).</summary>
    private static string Subject(CommandRequest command)
    {
        var target = command.Target;
        if (target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) || target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal)
            || target.StartsWith(Verbs.CardTargetPrefix, StringComparison.Ordinal))
        {
            return target;
        }
        return command.Verb == Verbs.Set && command.Args is [{ } key, ..] ? $"{target}/{command.Verb}/{key.Trim().ToLowerInvariant()}" : $"{target}/{command.Verb}";
    }

    private static bool Same(CardChoice choice, CommandRequest command) =>
        choice.Verb == command.Verb && choice.Target == command.Target && choice.Args.SequenceEqual(command.Args, StringComparer.Ordinal);

    private (string Ask, string Done) Describe(CommandRequest command) => Describe(command.Verb, command.Target, command.Args);

    /// <summary>The command in words, as asked (<c>merge #63 (TIA1)</c>) and as done (<c>merged #63 (TIA1)</c>).</summary>
    private (string Ask, string Done) Describe(string verb, string target, string[] args)
    {
        string Flight()
        {
            var code = target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? target[Verbs.FlightTargetPrefix.Length..] : target;
            return state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { } flight
                ? flight.PullRequest is { } number ? $"#{number} ({flight.Code})" : flight.Code
                : target;
        }

        var words = string.Join(' ', args).Trim();
        return verb switch
        {
            Verbs.Merge when PrRef.Parse(target) is { } pr && target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal) => ($"merge {pr}", $"merged {pr}"),
            Verbs.Merge => ($"merge {Flight()}", $"merged {Flight()}"),
            Verbs.Land => ($"land {Flight()}", $"landed {Flight()}"),
            Verbs.Set => ($"set {words}", $"set {words}"),
            Verbs.Setup => ($"run setup {words}", $"ran setup {words}"),
            Verbs.Update => ($"update pilot{(words.Length > 0 ? " " + words : "")}", $"updated pilot{(words.Length > 0 ? " " + words : "")}"),
            Verbs.RestartNode => ($"restart the node{(words.Length > 0 ? " " + words : "")}", $"restarted the node{(words.Length > 0 ? " " + words : "")}"),
            Verbs.RestartWatchtower => ("restart the watchtower", "restarted the watchtower"),
            Verbs.AnswerGrant => ($"answer a LAN ask ({words})", $"answered a LAN ask ({words})"),
            Verbs.RevokeGrant => ($"revoke the LAN grant {words}", $"revoked the LAN grant {words}"),
            Verbs.Dismiss => ($"dismiss {target}", $"dismissed {target}"),
            Verbs.Answer => ($"answer {target}{(words.Length > 0 ? $" with '{words}'" : "")}", $"answered {target}{(words.Length > 0 ? $" with '{words}'" : "")}"),
            _ => ($"{verb} {target}", $"did {verb} {target}"),
        };
    }
}
