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
    /// <summary>How long a command's request id is remembered after it ran: a client that timed out retries within minutes.</summary>
    internal static readonly TimeSpan RequestIdRetention = TimeSpan.FromMinutes(15);

    private const int MaxRemembered = 2000;

    /// <summary>How long the command route waits for a command before it answers queued (Waiting); under the CLI's own deadline, so its client hears the node, not its own timeout.</summary>
    public TimeSpan ReplyDeadline { get; set; } = TimeSpan.FromSeconds(90);

    private readonly Lock _seenGate = new Lock();
    private readonly Dictionary<string, Seen> _seen = [];

    private sealed record Seen(string Verb, string Target, TaskCompletionSource<CommandResult> Result, DateTimeOffset? DoneAt);

    /// <summary>
    /// Runs <paramref name="command"/>; its result is on the stream too. Long work (a say, git, a merge) runs off the state's lock.
    /// A command with a <see cref="CommandRequest.RequestId"/> the node has seen runs once: the repeat waits for the first's
    /// result, or is handed it, so a client that gave up and retried cannot do the work twice.
    /// </summary>
    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.RequestId is not { Length: > 0 } id)
        {
            return await RunOnceAsync(command, cancellationToken);
        }

        var now = Get<TimeProvider>().GetUtcNow();
        TaskCompletionSource<CommandResult>? mine = null;
        Seen? earlier;
        lock (_seenGate)
        {
            Forget(now);
            if (!_seen.TryGetValue(id, out earlier))
            {
                mine = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _seen[id] = new Seen(command.Verb, command.Target, mine, null);
            }
        }
        if (mine is null)
        {
            // The id names another command: that is a client's mistake, not a retry, and must not return the other's result.
            return earlier!.Verb != command.Verb || earlier.Target != command.Target
                ? state.Complete(command, $"request id {id} was used for {earlier.Verb} {earlier.Target}; a retry sends the same command")
                : await earlier.Result.Task.WaitAsync(cancellationToken);
        }

        try
        {
            var result = await RunOnceAsync(command, cancellationToken);
            if (result.Later is { } later)
            {
                // Queued behind a lock: the command is still to run, once. A retry by this id waits for what it ends with, and is handed that.
                _ = SettleLaterAsync(id, command, mine, later);
                return result;
            }
            lock (_seenGate)
            {
                if (result.Ok)
                {
                    _seen[id] = new Seen(command.Verb, command.Target, mine, now);
                }
                else
                {
                    // Refused, so nothing was done: a retry runs again rather than being told the same no.
                    _seen.Remove(id);
                }
            }
            mine.SetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            lock (_seenGate)
            {
                _seen.Remove(id);
            }
            mine.SetException(ex);
            throw;
        }
    }

    // A queued command's request id is settled as a command's is, by what the command ends with, once it has run.
    private async Task SettleLaterAsync(string id, CommandRequest command, TaskCompletionSource<CommandResult> mine, Task<CommandResult> later)
    {
        try
        {
            var result = await later;
            lock (_seenGate)
            {
                if (result.Ok)
                {
                    _seen[id] = new Seen(command.Verb, command.Target, mine, Get<TimeProvider>().GetUtcNow());
                }
                else
                {
                    _seen.Remove(id);
                }
            }
            mine.SetResult(result);
        }
        catch (Exception ex)
        {
            lock (_seenGate)
            {
                _seen.Remove(id);
            }
            mine.SetException(ex);
        }
    }

    // Under _seenGate: ids of commands that finished long ago go, and the oldest finished ones if the node has been sent very many.
    private void Forget(DateTimeOffset now)
    {
        foreach (var id in _seen.Where(p => p.Value.DoneAt is { } at && now - at > RequestIdRetention).Select(p => p.Key).ToList())
        {
            _seen.Remove(id);
        }
        if (_seen.Count >= MaxRemembered)
        {
            foreach (var id in _seen.Where(p => p.Value.DoneAt is not null).OrderBy(p => p.Value.DoneAt).Take(_seen.Count - MaxRemembered + 1).Select(p => p.Key).ToList())
            {
                _seen.Remove(id);
            }
        }
    }

    private async Task<CommandResult> RunOnceAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        // Who sends it decides what it may do (Standing): the lookout and a flight's session are fenced from what is the
        // person's alone (a merge or a landing by any route, a card's included; the node's settings and setup; a review posted to
        // GitHub in the person's name; the answer to an overlap), a flight's session from other flights too, and the autopilot
        // setting is the person's to change.
        // The watchtower is the lookout on its own, and an assistant while it acts on a message the person sent it in its chat.
        var watchtower = Get<Watchtower>();
        // The person's own word in the watchtower's chat, on a card it answers or dismisses, is theirs to have done: the node
        // sorts it as the person's and carries that on the command (PersonsWord, never from the wire), so the choice the answer
        // runs passes the checks keyed on the watchtower's name too. Without that word the watchtower is fenced as ever.
        var wordRecord = default((CardRow Card, string Chosen, string Words)?);
        if (command.PersonsWord is null && command.Who == watchtower.Name && command.Verb is Verbs.Answer or Verbs.Dismiss
            && command.Target.StartsWith(Verbs.CardTargetPrefix, StringComparison.Ordinal)
            && state.Cards().FirstOrDefault(c => c.Id == command.Target[Verbs.CardTargetPrefix.Length..]) is { State: CardState.Open } named
            && watchtower.PersonsWord(command) is { } words)
        {
            command = command with { PersonsWord = words };
            wordRecord = (named, ChosenOf(command, named), words);
        }
        var standing = command.PersonsWord is not null ? Standing.Person : Standing.Of(command, state, watchtower.Name, watchtower.ActingForPerson);
        // On autopilot the watchtower sees a flight's work in as the person's assistant would, whoever woke it: the person on
        // Remote Control talks to it past the node, so its turn never read as theirs and it was refused the merge they asked for
        // (#344, DE1, 2026-10-28). Its own merge and landing are then run and recorded; an ask's Allow stays the person's.
        if (standing.Kind == StandingKind.Lookout && Standing.LookoutOnAutopilot.Contains(command.Verb) && Get<RoleModels>().Autopilot)
        {
            standing = new Standing(StandingKind.Assistant);
            // Its fixer is marked as the autopilot's, so the node's own guards on fixers hold for it (PrReviews.FixAsync).
            if (command.Verb == Verbs.FixPr)
            {
                command = command with { Autopilot = true };
            }
        }
        if (standing.Refusal(command, state) is { } refused)
        {
            return state.Complete(command, refused);
        }
        // What the watchtower is told starts its next turn: the person's words make it theirs, the node's cards and nudges do not.
        if (command.Verb == Verbs.Tell || command.Verb == Verbs.Say && watchtower.Current() is { } row && command.Target == Verbs.SessionTarget(row.Id))
        {
            watchtower.NoteMessage(fromPerson: standing.Kind == StandingKind.Person && command.Who != LegWatch.Who, string.Join(' ', command.Args));
        }
        // An assistant's your-only command follows the autopilot rule: off, it waits on the person's card (Allow runs it as
        // them, Deny closes it); on, it runs at once, recorded with its session and message, and a Handled card says so.
        var onAutopilot = standing.Kind == StandingKind.Assistant && Standing.YourOnly(command, state);
        if (onAutopilot)
        {
            // A repo set to no-auto-merge never has its flight merged by an assistant on its own, autopilot or not: the merge waits on the person's card.
            if (!Get<RoleModels>().Autopilot || command.Verb == Verbs.Merge && NoAutoMerge(command))
            {
                return Get<Autopilot>().Ask(command);
            }
            command = command with { Autopilot = true };
        }
        var result = await RouteAsync(command, cancellationToken);
        if (wordRecord is var (answered, chosen, said) && result.Ok)
        {
            state.RaiseCard(CardBin.Handled, CardKinds.PersonsWord, Verbs.CardTarget(answered.Id),
                $"{command.Who} answered {answered.Id} ({chosen}) at the person's word in the chat", said);
        }
        if (onAutopilot && result.Ok)
        {
            // A command queued behind a lock is no done command yet: its Handled line follows once it has run.
            if (result.Later is { } later)
            {
                _ = HandledWhenDoneAsync(command, later);
            }
            else
            {
                Get<Autopilot>().Handled(command);
            }
        }
        return result;
    }

    // The label of the choice an answer names (by number or label), or "dismissed" for a dismissal; what it was, as the person's word is recorded.
    private static string ChosenOf(CommandRequest command, CardRow card)
    {
        var which = string.Join(' ', command.Args ?? []).Trim();
        return command.Verb == Verbs.Dismiss ? "dismissed"
            : (int.TryParse(which, out var number) && number >= 1 && number <= card.Choices.Length
                ? card.Choices[number - 1]
                : card.Choices.FirstOrDefault(c => string.Equals(c.Label, which, StringComparison.OrdinalIgnoreCase)))?.Label ?? which;
    }

    private async Task HandledWhenDoneAsync(CommandRequest command, Task<CommandResult> later)
    {
        try
        {
            if ((await later).Ok)
            {
                Get<Autopilot>().Handled(command);
            }
        }
        catch (Exception)
        {
            // Its failure is on a Handled card already (LockWait): no line for a command that did not run.
        }
    }

    // Whether a merge's flight or PR is in a repo whose PRs the node never merges by itself (no-auto-merge-repos).
    private bool NoAutoMerge(CommandRequest command)
    {
        if (command.Target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal))
        {
            return PrRef.Parse(command.Target) is { } pr
                && (Get<RoleModels>().NoAutoMerge(pr.Name)
                    || Get<PrReviews>().Of(pr) is { } review && Get<RoleModels>().NoAutoMerge(Path.GetFileName(review.ClonePath.TrimEnd('\\', '/'))));
        }
        return command.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal)
            && state.Flights().FirstOrDefault(f => f.Code == command.Target[Verbs.FlightTargetPrefix.Length..]) is { } flight
            && Get<RoleModels>().NoAutoMerge(flight.Repo);
    }

    private async Task<CommandResult> RouteAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.Verb == Verbs.Tell)
        {
            if (command.Target.StartsWith(Verbs.CardTargetPrefix, StringComparison.Ordinal))
            {
                var id = command.Target[Verbs.CardTargetPrefix.Length..];
                var card = state.Cards().FirstOrDefault(c => c.Id == id);
                if (card is null)
                {
                    return state.Complete(command, $"There is no card {id}");
                }
                if (card.State != CardState.Open)
                {
                    return state.Complete(command, $"Card {id} is closed");
                }
                var words = string.Join(' ', command.Args ?? []).Trim();
                if (words.Length == 0)
                {
                    return state.Complete(command, "tell takes the words to say about the card");
                }
                var subject = card.Subject.Split('/');
                var flight = subject is ["flight", { } code, ..] ? state.Flights().FirstOrDefault(f => f.Code == code) : null;
                var message = CardContext.Compose(card, Get<NodeOptions>().Machine, flight is null ? null : Briefs.FlightStatus(flight, state.Legs()), words);
                command = command with { Args = [message] };
            }
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
        if (command.Verb == Verbs.PauseJob)
        {
            return Get<JobRunner>().Pause(command);
        }
        if (command.Verb == Verbs.ResumeJob)
        {
            return Get<JobRunner>().Resume(command);
        }
        if (command.Verb == Verbs.Digest)
        {
            Get<Digest>().Raise(Get<TimeProvider>().GetUtcNow());
            return state.Complete(command, null);
        }
        if (command.Verb == Verbs.Summarise)
        {
            return await Get<Summaries>().RunAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.Records)
        {
            return Get<Summaries>().Records(command);
        }
        if (command.Verb == Verbs.Stop)
        {
            return await Get<SessionMessenger>().StopAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.Archive)
        {
            return Get<SessionTracker>().Archive(command);
        }
        if (command.Verb == Verbs.Set)
        {
            return Get<NodeSettingsCommands>().Set(command);
        }
        if (command.Verb == Verbs.Setup)
        {
            return await Get<NodeSettingsCommands>().SetupAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.Update)
        {
            return await Get<NodeUpdater>().RunAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.RestartNode)
        {
            return Get<NodeSettingsCommands>().RestartNode(command);
        }
        if (command.Verb == Verbs.Say)
        {
            return await Get<SessionMessenger>().SayAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.Compact)
        {
            return await Get<SessionMessenger>().CompactAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.Merge && command.Target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal))
        {
            return await Get<PrReviews>().RunAsync(command, cancellationToken);
        }
        if (FlightCommander.Handles(command.Verb))
        {
            return await Get<FlightCommander>().RunAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.RefreshPullRequests)
        {
            var row = await Get<PullRequestWatch>().RefreshAsync(cancellationToken);
            return state.Complete(command, null, made: (row?.PullRequests.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (RepoCommander.Handles(command.Verb))
        {
            return await Get<RepoCommander>().RunAsync(command, cancellationToken);
        }
        if (IdeaCommander.Handles(command.Verb))
        {
            return await Get<IdeaCommander>().RunAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.AddArtifact && command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal))
        {
            return AddSessionArtifact(command);
        }
        if (LegCommander.Handles(command.Verb))
        {
            return await Get<LegCommander>().RunAsync(command, cancellationToken);
        }
        if (PrReviews.Handles(command.Verb))
        {
            return await Get<PrReviews>().RunAsync(command, cancellationToken);
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
            // A PR review's card closed: its worktree goes now, not at the next sweep.
            if (card.Kind == PrReviews.ReadyKind)
            {
                await Get<PrReviews>().SweepAsync(cancellationToken);
            }
            // An assistant's ask dismissed is denied: the session that asked is told, as with Deny.
            if (card.Kind == CardKinds.AssistantAsk)
            {
                await Get<Autopilot>().TellAsync(card, allowed: false, AnsweredBy(command), cancellationToken);
            }
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

        // An assistant's ask denied: closed with who said so, and the session that asked is told it was not done. Deny is the
        // choice that dismisses the ask card itself; an Allow can be a dismissal too (of a ring card), so the verb alone cannot tell them apart.
        if (card.Kind == CardKinds.AssistantAsk && choice.Verb == Verbs.Dismiss && choice.Target == Verbs.CardTarget(card.Id))
        {
            state.CloseCard(card.Id, choice.Label, command.Who);
            await Get<Autopilot>().TellAsync(card, allowed: false, AnsweredBy(command), cancellationToken);
            return state.Complete(command, null);
        }

        // A merge (or a re-run of checks) pressed on a PR card is in progress while it runs, which is seconds of gh calls: the
        // card stays open throughout, on every client, and closes when the thing is done (landing closes it as landed) or turns
        // to say it is armed or queued. A refusal leaves it as it was.
        // A go-around from such a card stays open as it runs for another reason: a done leg goes around under a passed review only
        // while an open PR-ready or merge-failed card offers it, so closing this card first would refuse its own answer. The
        // go-around closes the flight's cards itself once it has started the worker.
        // A move again from the move-failed card stays open as it runs too, so a failure rewrites that card rather than doubling it.
        var inProgress = choice.Verb is Verbs.Merge or Verbs.RerunChecks or Verbs.GoAround && card.Kind is LegCommander.PrReady or LegCommander.MergeFailed
            || choice.Verb == Verbs.MoveFlight;
        // Any other choice closes the card by the answer before it runs: it lands its flight, and landing closes the flight's
        // cards, this one included, as landed rather than as the answer it was. Refused, it opens again.
        if (!inProgress)
        {
            state.CloseCard(card.Id, choice.Label, command.Who);
        }
        CommandResult result;
        try
        {
            result = await RunAsync(command with { Verb = choice.Verb, Target = choice.Target, Args = choice.Args, RequestId = null }, cancellationToken);
        }
        catch
        {
            // Cut off rather than refused (the node stopping for an update mid-merge): the choice never ran, so the card
            // is open again, never closed on a merge that did not happen (PIL32's #142, 2026-10-23).
            state.ReopenCard(card.Id);
            throw;
        }
        if (!result.Ok)
        {
            state.ReopenCard(card.Id);
            return state.Complete(command, $"{choice.Label} was refused: {result.Error}");
        }
        if (inProgress)
        {
            // A merge armed (its checks still run) or queued is not done: the card says so and stays, and the node closes it as
            // merged once it is, or turns it to what failed. Done (merged and landed), the landing has closed it already.
            if (result.Way is MergeWays.Armed or MergeWays.Queued)
            {
                Get<LegCommander>().ShowMerge(choice.Target, result.Made, result.Way);
            }
            else if (!state.CloseCard(card.Id, choice.Label, command.Who))
            {
                // Closed by the landing already, as landed: it says too which answer it was.
                state.NoteOutcome(card.Id, choice.Label);
            }
            return state.Complete(command, null);
        }
        if (card.Kind == PrReviews.ReadyKind)
        {
            await Get<PrReviews>().SweepAsync(cancellationToken);
        }
        // An assistant's ask allowed and done: the session that asked is told.
        if (card.Kind == CardKinds.AssistantAsk)
        {
            await Get<Autopilot>().TellAsync(card, allowed: true, AnsweredBy(command), cancellationToken);
        }
        return state.Complete(command, null);
    }

    // Who an asking session is told answered its ask: the person when the watchtower acted at their word in its chat.
    private static string AnsweredBy(CommandRequest command) =>
        command.PersonsWord is not null ? "the person (by their word in the watchtower's chat)" : command.Who;

    /// <summary><c>add-artifact session/&lt;id&gt; &lt;path or URL&gt; [title…]</c>: something to look at on a session in no flight.</summary>
    private CommandResult AddSessionArtifact(CommandRequest command)
    {
        var args = command.Args ?? [];
        if (args.Length == 0 || args[0].Trim().Length == 0)
        {
            return state.Complete(command, "add-artifact needs the path or URL of what to look at");
        }
        var location = args[0].Trim();
        var title = string.Join(' ', args.Skip(1)).Trim();
        if (title.Length == 0)
        {
            title = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? location : Path.GetFileName(location);
        }
        try
        {
            Get<SessionTracker>().AddArtifact(command.Target[Verbs.SessionTargetPrefix.Length..],
                new Artifact { Title = title, Location = location, At = Get<TimeProvider>().GetUtcNow() });
        }
        catch (FlightException ex)
        {
            return state.Complete(command, ex.Message);
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
