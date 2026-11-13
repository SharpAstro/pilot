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

    private sealed class Mark
    {
        public DateTimeOffset? HandlerStarted;
    }

    // Times the command (PIL794): wait is the time before its handler started, run the handler's own time. A command queued
    // behind a lock replies at once with a Later; its timing is written when that settles, its run then including the queue.
    private async Task<CommandResult> RunOnceAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var time = Clock;
        var began = time.GetUtcNow();
        var mark = new Mark();
        // The command's own flow is the foreground of the node's process starts (ProcessGate): a git, gh or claude start made
        // anywhere in what it awaits goes straight to Process.Start, never behind the background readers' starts. An explicit
        // scope, not a bare flag: it is switched off when the command ends (a Later: when it settles), so a Task.Run or a
        // fire-and-forget call that outlives the command finds it off and is background, instead of inheriting the command's
        // priority for as long as it runs. Work started while the command still runs is handed off with ProcessGate.Run or
        // ProcessGate.Background (the background readers and the advances a create or a receive starts off its reply).
        var foreground = ProcessGate.Foreground();
        CommandResult result;
        try
        {
            result = await RunCoreAsync(command, cancellationToken, mark);
        }
        catch
        {
            foreground.Dispose();
            Note(command, began, mark.HandlerStarted, time.GetUtcNow());
            throw;
        }
        if (result.Later is { } later)
        {
            _ = NoteWhenSettledAsync(command, began, time.GetUtcNow(), later, foreground);
        }
        else
        {
            foreground.Dispose();
            Note(command, began, mark.HandlerStarted, time.GetUtcNow());
        }
        return result;
    }

    private void Note(CommandRequest command, DateTimeOffset began, DateTimeOffset? handlerStarted, DateTimeOffset ended)
    {
        var started = handlerStarted ?? ended;
        services.GetService<CommandTimings>()?.Record(command.Verb, command.Target, command.Who, started - began, ended - started);
    }

    // The queued command is still the person's: its flow keeps the foreground mark until it settles, then the scope ends.
    private async Task NoteWhenSettledAsync(CommandRequest command, DateTimeOffset began, DateTimeOffset replied, Task<CommandResult> later, IDisposable foreground)
    {
        try
        {
            await later;
        }
        catch (Exception)
        {
            // Its failure is told elsewhere; it still took the time.
        }
        foreground.Dispose();
        Note(command, began, replied, Clock.GetUtcNow());
    }

    private async Task<CommandResult> RunCoreAsync(CommandRequest command, CancellationToken cancellationToken, Mark mark)
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
        // A gate override (PIL702) needs a reason of the session's own, from anyone but the person and the node: the black box
        // keeps it. A card choice's canned words never count, so a card's answer is held to the same rule through its Why.
        if (standing.Kind != StandingKind.Person && !(command.Who == "node" && command.PersonsWord is null)
            && DecisionSources.IsOverride(command, ReviewerOfTarget(command))
            && DecisionSources.OwnReasonOf(command, _fromCard.Value).Length == 0)
        {
            return state.Complete(command,
                $"{command.Verb} {command.Target} overrides a gate, and the black box keeps why: give --why in your own words, naming the evidence (a job id such as j962, a commit, a card id). A card's canned words do not count");
        }
        // What the watchtower is told starts its next turn: the person's words make it theirs, the node's cards and nudges do not.
        // A say finds its session as the messenger does, by row id or by name, so a say to the watchtower by name is noted too.
        if (command.Verb == Verbs.Tell || command.Verb == Verbs.Say && watchtower.Current() is { } row
            && command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal)
            && SessionMessenger.Find(state.Sessions(), command.Target[Verbs.SessionTargetPrefix.Length..])?.Id == row.Id)
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
        // A choice a card's answer runs is recorded by that answer, once; a decision verb given straight is recorded here. An assistant's
        // your-only command run on autopilot is recorded by Autopilot.Handled.
        var fromCard = _fromCard.Value;
        _fromCard.Value = false;
        mark.HandlerStarted = Clock.GetUtcNow();
        var result = await RouteAsync(command, cancellationToken);
        if (result.Ok && !fromCard && !onAutopilot && DecisionSources.IsDirect(command.Verb, command.Args))
        {
            var kind = DecisionSources.KindOf(command.Verb, command.Args)!;
            state.RecordDecision(command.Target, kind, DecisionSources.QuestionOf(command), [], DecisionSources.ChoiceOf(command),
                command.Who, ByKindOf(command, standing), DecisionSources.ReasonOf(command));
        }
        if (wordRecord is var (answered, chosen, said) && result.Ok)
        {
            state.RaiseCard(CardBin.Handled, CardKinds.PersonsWord, Verbs.CardTarget(answered.Id),
                $"{command.Who} answered {answered.Id} ({chosen}) at the person's word in the chat", said);
        }
        if (onAutopilot && result.Ok)
        {
            // A command queued behind a lock is no done command yet: its Handled line follows once it has run.
            // A card's answer, an Answer or a Dismiss is recorded as the card's own row, which reads (autopilot).
            var record = !fromCard && command.Verb is not (Verbs.Answer or Verbs.Dismiss);
            if (result.Later is { } later)
            {
                // Not started by the person's command: what it does once the queued one has run is the node's own.
                using (ProcessGate.Background())
                {
                    _ = HandledWhenDoneAsync(command, later, record);
                }
            }
            else
            {
                Get<Autopilot>().Handled(command, record);
            }
        }
        return result;
    }

    // Set around the command a card's answer runs, so that command's own run does not record a second decision (it flows with the call).
    private static readonly AsyncLocal<bool> _fromCard = new();

    // As what a command's sender chose: the autopilot (its flag, or the node's own path with autopilot on), the watchtower on its own,
    // the person (their word in the chat, or a client of theirs), else the node.
    private string ByKindOf(CommandRequest command, Standing standing)
    {
        if (command.Autopilot || command.Who == "node" && Get<RoleModels>().Autopilot)
        {
            return DecisionByKinds.Autopilot;
        }
        if (command.PersonsWord is null && command.Who == "node")
        {
            return DecisionByKinds.Node;
        }
        if (command.PersonsWord is not null || standing.Kind == StandingKind.Person)
        {
            return DecisionByKinds.Person;
        }
        return command.Who == Get<Watchtower>().Name ? DecisionByKinds.Watchtower : DecisionByKinds.Node;
    }

    // The reviewer of the flight a command targets, or null when the target is no flight or it has no review.
    private string? ReviewerOfTarget(CommandRequest command) =>
        command.Verb == Verbs.Review
            ? state.Flights().FirstOrDefault(f => Verbs.FlightTarget(f.Code) == command.Target)?.Review?.Reviewer
            : null;

    private string ByKindOf(CommandRequest command) =>
        ByKindOf(command, command.PersonsWord is not null ? Standing.Person : Standing.Of(command, state, Get<Watchtower>().Name, Get<Watchtower>().ActingForPerson));

    // The label of the choice an answer names (by number or label), or "dismissed" for a dismissal; what it was, as the person's word is recorded.
    private static string ChosenOf(CommandRequest command, CardRow card)
    {
        var which = string.Join(' ', command.Args ?? []).Trim();
        // A choice that asks for words carries them after its label: the choice is the first word.
        var first = (command.Args ?? []).FirstOrDefault()?.Trim() ?? "";
        if (command.Verb != Verbs.Dismiss && first.Length > 0 && first != which
            && card.Choices.FirstOrDefault(c => c.AsksFor is not null && string.Equals(c.Label, first, StringComparison.OrdinalIgnoreCase)) is { } asking)
        {
            return asking.Label;
        }
        return command.Verb == Verbs.Dismiss ? "dismissed"
            : (int.TryParse(which, out var number) && number >= 1 && number <= card.Choices.Length
                ? card.Choices[number - 1]
                : card.Choices.FirstOrDefault(c => string.Equals(c.Label, which, StringComparison.OrdinalIgnoreCase)))?.Label ?? which;
    }

    private async Task HandledWhenDoneAsync(CommandRequest command, Task<CommandResult> later, bool record)
    {
        try
        {
            if ((await later).Ok)
            {
                Get<Autopilot>().Handled(command, record);
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
        if (command.Verb == Verbs.ChatNow)
        {
            var sendId = (command.Args ?? []).FirstOrDefault()?.Trim() ?? "";
            if (command.Target != Verbs.WatchtowerTarget || sendId.Length == 0)
            {
                return state.Complete(command, $"{Verbs.ChatNow} takes the watchtower and the send id of the queued message to answer now (the chat page's queued ids, or pilot chat now <send id>)");
            }
            return state.Complete(command, await Get<WatchtowerChat>().AnswerNowAsync(sendId, cancellationToken));
        }
        if (command.Verb == Verbs.AnswerAsk)
        {
            if (command.Target != Verbs.WatchtowerTarget)
            {
                return state.Complete(command, $"{Verbs.AnswerAsk} takes the watchtower as its target, not {command.Target}");
            }
            return state.Complete(command, await Get<Watchtower>().AnswerAskAsync(command.Args ?? [], cancellationToken));
        }
        if (command.Verb == Verbs.RestartWatchtower)
        {
            var why = string.Join(' ', command.Args ?? []).Trim();
            var restarted = await Get<Watchtower>().RestartWithWayAsync(why.Length > 0 ? $"restarted by {command.Who}: {why}" : $"restarted by {command.Who}", cancellationToken);
            return state.Complete(command, restarted.Error, restarted.Way);
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
        if (command.Verb == Verbs.Bench)
        {
            return await Get<BenchRun>().RunAsync(command, cancellationToken);
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
        if (command.Verb == Verbs.ReviewCost)
        {
            if (command.Target != Verbs.NodeTarget)
            {
                return state.Complete(command, $"review-cost takes the target {Verbs.NodeTarget}, not {command.Target}");
            }
            var (report, error) = ReviewCost.Run(command.Args ?? [], state, Get<SessionTrackerOptions>().Profile);
            return state.Complete(command, error, made: report);
        }
        if (command.Verb == Verbs.Stop)
        {
            return await Get<SessionMessenger>().StopAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.EndSession)
        {
            return await Get<SessionMessenger>().EndAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.ReportState)
        {
            return ReportState(command);
        }
        if (command.Verb == Verbs.Archive)
        {
            return Get<SessionTracker>().Archive(command);
        }
        if (Verbs.IsAdmission(command.Verb))
        {
            // Another node's claim for an Opus slot, its list of sessions, or a setting held here for the fleet: only under a grant.
            return command.Grant is null
                ? state.Complete(command, $"{command.Verb} is taken from another node of the fleet, under its grant")
                : Get<AdmissionGate>().Handle(command);
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
        if (command.Verb == Verbs.Reboot)
        {
            return await Get<RebootPrep>().RunAsync(command, cancellationToken);
        }
        if (command.Verb == Verbs.EndStray)
        {
            return services.GetService<StraySweep>() is { } sweep
                ? sweep.EndOnWord(command)
                : state.Complete(command, "this node does not look for strays (machine sampling is off), so it has none to end");
        }
        if (command.Verb == Verbs.EndPoller)
        {
            return services.GetService<PollerSweep>() is { } poller
                ? poller.EndOnWord(command)
                : state.Complete(command, "this node does not look for pollers, so it has none to end");
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
    /// <c>report-state</c>: stores a session's own report (args: state, optional seq, optional message). The session is the target's
    /// when it names one (<c>session/&lt;id&gt;</c>), else the caller's own (<see cref="CommandRequest.SessionId"/>).
    /// </summary>
    private CommandResult ReportState(CommandRequest command)
    {
        var args = command.Args ?? [];
        if (args.Length == 0 || !ReportBook.IsState(args[0]))
        {
            return state.Complete(command, "report-state takes idle, working or blocked, then an optional seq and message");
        }
        long? seq = null;
        if (args.Length > 1 && args[1].Length > 0)
        {
            if (!long.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return state.Complete(command, $"the seq must be a whole number, not '{args[1]}'");
            }
            seq = parsed;
        }
        var wanted = command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.SessionTargetPrefix.Length..] : command.SessionId;
        if (wanted is not { Length: > 0 })
        {
            return state.Complete(command, "report-state needs the session: run it inside the session (CLAUDE_CODE_SESSION_ID) or pass --session <id>");
        }
        var row = state.Sessions().FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.SessionId, wanted, StringComparison.OrdinalIgnoreCase)
            || s.EarlierSessionIds?.Contains(wanted, StringComparer.OrdinalIgnoreCase) == true);
        if (row is null)
        {
            return state.Complete(command, $"no session '{wanted}' on this node");
        }
        Get<ReportBook>().Accept(row.SessionId, args[0], args.Length > 2 ? args[2] : null, seq, Get<TimeProvider>().GetUtcNow());
        Get<SessionTracker>().Kick();
        return state.Complete(command, null);
    }

    private static CardChoice? FindChoice(CardRow card, string which) =>
        int.TryParse(which, out var number) && number >= 1 && number <= card.Choices.Length
            ? card.Choices[number - 1]
            : card.Choices.FirstOrDefault(c => string.Equals(c.Label, which, StringComparison.OrdinalIgnoreCase));

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
            return ClosedAlready(command, card, args);
        }

        if (command.Verb == Verbs.Dismiss)
        {
            // An answer underway owns the card: closing it from under the answer would lose what the answer comes to (#792).
            if (card.Answering is { } underway && Get<TimeProvider>().GetUtcNow() - underway.At < Get<LegCommander>().StuckAfter)
            {
                return state.Complete(command, $"card {card.Id} is being answered ({underway.Choice}, pressed by {underway.By} at {LocalTime(underway.At)}): wait for its answer, then dismiss");
            }
            var why = string.Join(' ', args).Trim();
            state.CloseCard(card.Id, why.Length > 0 ? $"dismissed: {why}" : "dismissed", command.Who);
            RecordCardDecision(command, card, DecisionKinds.Dismiss, "dismissed", why);
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
            // PIL1023: a dismissed CI stop card keeps the stop (the budget guards Actions spend); the answer says so plainly.
            if (card.Kind == CiBudgets.CapKind && Verbs.FlightCodeOf(card.Subject) is { } cappedCode
                && state.Flights().FirstOrDefault(f => f.Code == cappedCode)?.Ci is { CappedWhy: not null })
            {
                return state.Complete(command, null, made: CiBudgets.DismissedStays(cappedCode));
            }
            return state.Complete(command, null);
        }

        var which = string.Join(' ', args).Trim();
        var choice = FindChoice(card, which);
        string? given = null;
        // A choice that asks for words: the first arg is the choice, the rest are its words.
        if (choice is null && args.Length > 1 && FindChoice(card, args[0].Trim()) is { AsksFor: not null } asking)
        {
            choice = asking;
            var words = string.Join(' ', args.Skip(1)).Trim();
            if (words.Length == 0)
            {
                return state.Complete(command, $"{choice.Label} needs words ({choice.AsksFor}); the card stays open");
            }
            choice = choice with { Args = [.. choice.Args, words] };
            given = words;
        }
        else if (choice is { AsksFor: not null })
        {
            return state.Complete(command, $"{choice.Label} needs words ({choice.AsksFor}): pilot answer {card.Id} {choice.Label} --words <text>; the card stays open");
        }
        if (choice is null)
        {
            return state.Complete(command, card.Choices.Length == 0
                ? $"card {card.Id} has no choices; dismiss it"
                : $"card {card.Id} offers {string.Join(", ", card.Choices.Select((c, i) => $"{i + 1} {c.Label}"))}, not '{which}'");
        }

        // A choice the node would refuse now (its flight is held, its session is gone) is refused here, before anything is taken, closed or run,
        // with the reason its card shows on it (ChoiceAvailability): a client that pressed before it saw the mark runs nothing (#792).
        if (ChoiceAvailability.WhyNot(choice, state.Flights(), state.Sessions()) is { } unavailable)
        {
            return state.Complete(command, $"{choice.Label} was refused: {unavailable}");
        }

        // An assistant's ask denied: closed with who said so, and the session that asked is told it was not done. Deny is the
        // choice that dismisses the ask card itself; an Allow can be a dismissal too (of a ring card), so the verb alone cannot tell them apart.
        if (card.Kind == CardKinds.AssistantAsk && choice.Verb == Verbs.Dismiss && choice.Target == Verbs.CardTarget(card.Id))
        {
            state.CloseCard(card.Id, choice.Label, command.Who);
            RecordCardDecision(command, card, DecisionKinds.Dismiss, choice.Label, "");
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
        // An in-progress choice is taken before it runs, so the card says on every client that it is underway, by whom and since when,
        // instead of offering its buttons again: a second press of the same choice joins it with an ok answer (the controller's CLI press
        // that sat queued, and the owner's press of the same button two minutes later, was told "refused", #792), and another choice
        // is refused saying which answer is underway. The mark ends with the answer, whatever it comes to (see RunChoiceAsync).
        CardAnswering? answering = null;
        if (inProgress)
        {
            var legs = Get<LegCommander>();
            var (taken, underway) = state.BeginAnswering(card.Id, choice.Label, PresserOf(command), legs.CallingOn(choice.Target), legs.StuckAfter);
            if (taken is null)
            {
                return underway is null
                    ? ClosedAlready(command, state.Cards().First(c => c.Id == card.Id), args)
                    : string.Equals(underway.Choice, choice.Label, StringComparison.Ordinal)
                        ? state.Complete(command, null, made: $"{CardAnswering.JoinedUnderway}, started by {underway.By} at {LocalTime(underway.At)}")
                        : state.Complete(command, $"{choice.Label} was refused: {underway.Choice} is underway on card {card.Id} (pressed by {underway.By} at {LocalTime(underway.At)}): wait for its answer, then ask again");
            }
            answering = taken;
        }
        // Any other choice closes the card by the answer before it runs: it lands its flight, and landing closes the flight's
        // cards, this one included, as landed rather than as the answer it was. Refused, it opens again. A press that lost the race to
        // another's close finds the card closed: it is that answer's repeat, and runs nothing twice.
        else if (!state.CloseCard(card.Id, choice.Label, command.Who))
        {
            return ClosedAlready(command, state.Cards().First(c => c.Id == card.Id), args);
        }
        try
        {
            return await RunChoiceAsync(command, card, choice, given, inProgress, cancellationToken);
        }
        finally
        {
            // Whatever the answer came to (closed, refused, armed, cut off), none is underway now: the card closed has cleared the mark
            // already, an open one stops saying so and offers its buttons again.
            if (answering is not null)
            {
                state.ClearAnswering(card.Id, answering);
            }
        }
    }

    /// <summary>How long after a choice closed a card a repeat of that choice is told it is done already, rather than that the card is closed.</summary>
    internal static readonly TimeSpan RepeatAnswerWindow = TimeSpan.FromMinutes(5);

    // The choice an answer's args name, as the answer reads them, or null: a closed card keeps its choices to tell a repeat from another press.
    private static CardChoice? ChoiceOf(CardRow card, string[] args) =>
        FindChoice(card, string.Join(' ', args).Trim()) ?? (args.Length > 1 && FindChoice(card, args[0].Trim()) is { AsksFor: not null } asking ? asking : null);

    // A card closed already: the same choice a moment ago is that answer's repeat (ok, saying who answered and when, like a join of an
    // answer underway), anything else is refused as before.
    internal CommandResult ClosedAlready(CommandRequest command, CardRow card, string[] args) =>
        command.Verb == Verbs.Answer && ChoiceOf(card, args) is { } same && string.Equals(card.Outcome, same.Label, StringComparison.Ordinal)
            && card.ClosedBy is { } by && card.ClosedAt is { } at && Get<TimeProvider>().GetUtcNow() - at <= RepeatAnswerWindow
            ? state.Complete(command, null, made: $"{CardAnswering.JoinedClosed} by {by} at {LocalTime(at)}")
            : state.Complete(command, $"card {card.Id} is closed already ({card.Outcome})");

    private static string LocalTime(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    // Who pressed, as a card says it: the person when the watchtower acts on their word in its chat.
    private static string PresserOf(CommandRequest command) => command.PersonsWord is not null ? "the person" : command.Who;

    // Runs the chosen button's command as the one answering and settles the card by what it came to; the answer underway (if any) is ended by the caller.
    private async Task<CommandResult> RunChoiceAsync(CommandRequest command, CardRow card, CardChoice choice, string? given, bool inProgress, CancellationToken cancellationToken)
    {
        CommandResult result;
        try
        {
            _fromCard.Value = true;
            result = await RunAsync(command with { Verb = choice.Verb, Target = choice.Target, Args = choice.Args, RequestId = null }, cancellationToken);
        }
        catch
        {
            // Cut off rather than refused (the node stopping for an update mid-merge): the choice never ran, so the card
            // is open again, never closed on a merge that did not happen (PIL32's #142, 2026-10-23).
            state.ReopenCard(card.Id);
            throw;
        }
        finally
        {
            _fromCard.Value = false;
        }
        if (!result.Ok)
        {
            state.ReopenCard(card.Id);
            return state.Complete(command, $"{choice.Label} was refused: {result.Error}");
        }
        // One decision per answer: a design answer is recorded by the review fix it runs, which knows the question and the options. A
        // fix offer that carries the answer the node decided on (its Fix it has args) is that answer's too.
        if (choice.Verb != Verbs.FixPr || card.Kind != PrReviews.DesignKind && !(card.Kind == PrReviews.FixOfferKind && choice.Args is { Length: > 0 }))
        {
            // A Fix it off the design card (a fix offer, a fixer to start again) answers no question of design: it is the card's own choice.
            var kind = choice.Verb == Verbs.FixPr ? DecisionKinds.Card : DecisionSources.KindOf(choice.Verb, choice.Args) ?? DecisionKinds.Card;
            RecordCardDecision(command, card, kind, choice.Label, given ?? "");
        }
        if (inProgress)
        {
            // A merge armed (its checks still run) or queued is not done: the card says so and stays, and the node closes it as
            // merged once it is, or turns it to what failed. Done (merged and landed), the landing has closed it already.
            if (result.Way is MergeWays.Armed or MergeWays.Queued)
            {
                Get<LegCommander>().ShowMerge(choice.Target, result.Made, result.Way);
            }
            else if (result.Way == MergeWays.InProgress)
            {
                // A repeat of a merge already running: the card stays open, and the first merge's result updates or closes it.
            }
            else if (!state.CloseCard(card.Id, choice.Label, command.Who))
            {
                // Closed by the landing already, as landed: it says too which answer it was, and whose, so a repeat is told who answered.
                state.NoteOutcome(card.Id, choice.Label, closedBy: command.Who);
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

    // A card answered or dismissed: what it asked (its title), what it offered (its choices' labels), what was chosen and why.
    private void RecordCardDecision(CommandRequest command, CardRow card, string kind, string choice, string words)
    {
        var byKind = ByKindOf(command);
        var reason = words.Trim().Length > 0 ? words.Trim() : command.Why?.Trim() is { Length: > 0 } why ? why
            : byKind == DecisionByKinds.Person && kind is DecisionKinds.AcceptLeg or DecisionKinds.PassWithNotes or DecisionKinds.Merge or DecisionKinds.CreateAnyway
                ? $"pressed by {command.Who} on card {card.Id}"
                : $"answered card {card.Id}";
        state.RecordDecision(card.Subject, kind, card.Title, [.. card.Choices.Select(c => c.Label)],
            words.Trim().Length > 0 && kind != DecisionKinds.Dismiss ? $"{choice}: {words.Trim()}" : choice,
            command.Who, ByKindOf(command), reason);
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
        string? attachment = null, attachmentExt = null;
        if (command.Attachment is { Length: > 0 } named && KeepCardImage(named, out attachment, out attachmentExt) is { } refused)
        {
            return state.Complete(command, refused);
        }
        state.RaiseCard(bin.Value, Watchtower.RaisedKind, command.Target, title, (detail.Length > 0 ? detail : null) is { } d ? d + from : from.Length > 0 ? from.Trim() : null,
            attachment: attachment, attachmentExt: attachmentExt);
        return state.Complete(command, null);
    }

    /// <summary>
    /// The copy a card is raised with: <paramref name="named"/> is <c>&lt;id&gt;.&lt;ext&gt;</c> of an image the node holds. One uploaded for a card
    /// (or an idea) lives in a folder no archive empties and is kept as it is; a leg artifact's copy goes when its flight lands, so a card
    /// gets a copy of its own. Null when it is kept, else why not.
    /// </summary>
    private string? KeepCardImage(string named, out string? id, out string? ext)
    {
        id = ext = null;
        var attachments = Get<Attachments>();
        var dot = named.LastIndexOf('.');
        if (dot <= 0 || Attachments.IdeaImageExtension(named[(dot + 1)..]) is not { } kind)
        {
            return $"the attachment is <id>.<ext> of an image the node holds (png, jpg, jpeg, gif, webp or bmp), not {named}";
        }
        var wanted = named[..dot];
        if (attachments.FindIdeaImage(wanted) is { } held)
        {
            (id, ext) = (wanted, held.Ext);
            return null;
        }
        var owned = state.Flights().SelectMany(f => state.Legs(f.Code).SelectMany(l => l.Artifacts).Select(a => (Owner: f.Code, Artifact: a)))
            .Concat(state.Sessions().SelectMany(s => (s.Artifacts ?? []).Select(a => (Owner: s.Id, Artifact: a))))
            .FirstOrDefault(x => x.Artifact?.Attachment == wanted && x.Artifact.AttachmentExt is not null);
        if (owned.Artifact is { AttachmentExt: { } ownedExt } && attachments.PathOf(owned.Owner, wanted, ownedExt) is var path && File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > IdeaImages.MaxBytes)
                {
                    return $"a card's image is at most {IdeaImages.MaxBytes / (1024 * 1024)} MB";
                }
                (id, ext) = (attachments.AddIdeaImage(bytes, ownedExt), ownedExt);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FlightException)
            {
                return $"could not keep a copy of {named}: {ex.Message}";
            }
        }
        return $"the node holds no image {wanted}.{kind}: upload it first (pilot ring --attachment <file>)";
    }

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    // The timing every command gets is incidental to it: a host that registered no clock or timings (a test's) still runs it.
    private TimeProvider Clock => services.GetService<TimeProvider>() ?? TimeProvider.System;
}
