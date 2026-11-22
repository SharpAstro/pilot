using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    // A flight made and a flight received both claim a code, so they take turns on the one lock.
    private async Task<CommandResult> CreateOneAtATimeAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        CommandResult result;
        await _creating.WaitAsync(cancellationToken);
        try
        {
            string? error = null;
            string? made = null;
            string? way = null;
            try
            {
                (made, way) = command.Verb == Verbs.ReceiveFlight
                    ? await ReceiveAsync(command, cancellationToken)
                    : await CreateAsync(command, cancellationToken);
            }
            catch (FlightException ex)
            {
                error = ex.Message;
            }
            result = state.Complete(command, error, way: way, made: made);
        }
        finally
        {
            _creating.Release();
        }

        // A flight handed over goes on by the node's normal advance (its next leg, or its PR), off this lock and off the reply: the sender
        // is told it was taken as soon as it is, not after a validator or a worker has started (a slow reply is a sender that cannot tell).
        if (result.Ok && command.Verb == Verbs.ReceiveFlight && result.Made is { } received && Advance is { } advance)
        {
            AdvanceOffReply(received, advance, cancellationToken);
        }
        // A flight made here is planned off the reply too; one routed to another node (Way set) is planned there.
        if (result.Ok && command.Verb == Verbs.CreateFlight && result.Way is null && result.Made is { } created && Plan is { } plan)
        {
            PlanOffReply(created, command.Who, plan, cancellationToken);
        }
        return result;
    }

    private void PlanOffReply(string code, string who, Func<string, string, CancellationToken, Task<CommandResult>> plan, CancellationToken cancellationToken)
    {
        // Background work, not the command's: what it started to answer the person does not inherit the command's priority for its process starts (ProcessGate).
        var task = ProcessGate.Run(async () =>
        {
            try
            {
                var planned = await plan(code, who, cancellationToken);
                if (!planned.Ok)
                {
                    RaisePlanRefused(code, planned.Error ?? "refused");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                RaisePlanRefused(code, ex.Message);
            }
        });
        _advancing[task] = true;
        _ = task.ContinueWith(t => _advancing.TryRemove(t, out _), TaskScheduler.Default);
    }

    private void RaisePlanRefused(string code, string why) =>
        state.RaiseCard(CardBin.Ring, LegCommander.NextStepKind, Verbs.FlightTarget(code), $"{code} was made, but its planner was refused: {LegCommander.OneLine(why)}", why,
            [new CardChoice { Label = $"Plan {code}", Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(code) }]);

    // The advances running after their receive-flight was answered; a test awaits them (WhenAdvancedAsync).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, bool> _advancing = [];

    private void AdvanceOffReply(string code, Func<string, CancellationToken, Task> advance, CancellationToken cancellationToken)
    {
        var task = ProcessGate.Run(async () =>
        {
            try
            {
                await advance(code, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The sweep takes the step again every minute; the flight is taken, so this is a card, not a failed receive.
                state.RaiseCard(CardBin.Ring, LegCommander.NextStepKind, Verbs.FlightTarget(code), $"{code} came here, but its next step was refused: {LegCommander.OneLine(ex.Message)}", ex.Message);
            }
        });
        _advancing[task] = true;
        _ = task.ContinueWith(t => _advancing.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Completes when the advances that follow the receive-flights answered so far have finished.</summary>
    public Task WhenAdvancedAsync() => Task.WhenAll(_advancing.Keys);

    /// <summary>
    /// Runs <paramref name="call"/> (git, GitHub, a launcher: each takes as long as it takes) with the lock let go, and takes it
    /// again before it returns, whatever came of it. Called under the lock.
    /// </summary>
    private async Task<T> OffLockAsync<T>(Func<Task<T>> call)
    {
        _one.Release();
        try
        {
            return await call();
        }
        finally
        {
            await _one.WaitAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Makes the flight; returns its code, which the result carries as what it made, and where it went when it was made
    /// on another node (<see cref="CommandResult.Way"/>), else null.
    /// </summary>
    private async Task<(string Code, string? Way)> CreateAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (!command.Target.StartsWith(Verbs.RepoTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.RepoTargetPrefix.Length)
        {
            throw new FlightException($"create-flight takes a repo target, {Verbs.RepoTargetPrefix}<path to its checkout>, not {command.Target}");
        }
        var args = command.Args ?? [];
        var anyway = args.Contains(Verbs.AnywayFlag, StringComparer.Ordinal);
        args = [.. args.Where(a => a != Verbs.AnywayFlag)];
        if (args.Length == 0)
        {
            throw new FlightException("create-flight needs the work item first (an issue number, sc-N or a Shortcut story URL), as in create-flight repo/<path> 1611 [title]");
        }
        // A bare number means the repo's tracker default (repo-trackers), keyed by the checkout's folder name.
        var givenPath = command.Target[Verbs.RepoTargetPrefix.Length..];
        var named = Path.GetFileName((await planes.MainCheckoutAsync(givenPath, cancellationToken) ?? givenPath).TrimEnd('/', '\\'));
        var (parsed, parseError) = WorkItem.Parse(args[0], Models.TrackerOf(named));
        if (parsed is not { } item)
        {
            throw new FlightException($"create-flight: {parseError}");
        }

        // Only a new flight is refused here: flying and held flights, legs, reviews and landings never come through here. In
        // maintenance a node takes none of its own, and sends this one to a node of the fleet that takes it.
        if (Models.Maintenance)
        {
            return await RouteAsync(command, item, cancellationToken);
        }

        var path = command.Target[Verbs.RepoTargetPrefix.Length..];
        var repoPath = await planes.MainCheckoutAsync(path, cancellationToken)
            ?? throw new FlightException($"{path} is no git checkout");
        var repo = Path.GetFileName(repoPath);
        var flights = state.Flights();
        var archived = state.ArchivedFlightRows();
        var prefix = FlightCodes.PrefixFor(repo, flights, archived);
        var code = FlightCodes.Next(prefix, item.Id, flights, archived)
            ?? throw new FlightException($"{prefix}{item.Id} and its suffixes {FlightCodes.Suffixes} are all taken");

        // One flight per issue: a flight of this repo on this item that has not landed answers for it, so a second is refused naming
        // that one (the caller steers it). --anyway with a why of the caller's own makes one more, and the why is kept on a Handled card.
        var live = flights.FirstOrDefault(f => f.State != FlightState.Landed && f.Issue == item.Id && f.Tracker == item.Tracker
            && string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase));
        var duplicateWhy = command.Why?.Trim() ?? "";
        if (live is not null)
        {
            var liveState = FlightWaiting.OnHoldText(live) is { } onHold ? $"on hold by {live.OnHold!.By}"
                : live.State == FlightState.Held ? "held" : "flying";
            var said = $"{item.Noun} already has {live.Code}, {liveState}: steer that one";
            if (!anyway)
            {
                throw new FlightException($"{said}, or give --anyway with --why to make a second");
            }
            if (duplicateWhy.Length == 0)
            {
                throw new FlightException($"{said}; a second flight on it needs --why, naming the reason it is not that one");
            }
        }

        // A flight is flown by one node: a peer that flies this item, or the branch this flight would get, answers for it, and
        // --anyway does not override a flight that is live there (it only overrides a label, which can be stale).
        if (await FlownByPeerAsync(repoPath, item, Planes.BranchOf(code, item), cancellationToken) is { } peerFlight)
        {
            throw new FlightException($"{peerFlight.Code} flies {item.Display} on {peerFlight.Node}: fly it there");
        }
        var otherNode = await OtherNodeOfAsync(repoPath, item, cancellationToken);
        if (otherNode is not null && !anyway)
        {
            throw new FlightException($"{item.Noun} carries the label {PilotMarks.NodeLabel(otherNode)}: it is flying on {otherNode}, not on {Machine}. Fly it there, or give --anyway to create the flight here over {otherNode}'s label");
        }

        var title = string.Join(' ', args.Skip(1)).Trim();
        if (title.Length == 0 && Trackers?.Of(item.Tracker) is { } tracker)
        {
            // No title given: the item's own, so the flight reads as what it is about. Asked before the plane is made, so a
            // refusal for want of one leaves nothing behind.
            title = (await tracker.TitleAsync(repoPath, item.Id, cancellationToken))?.Trim() ?? "";
            if (title.Length == 0 && tracker.MissingTitle(item) is { } missing)
            {
                throw new FlightException(missing);
            }
        }
        if (title.Length == 0 && item.Tracker != TrackerKind.GitHub && Trackers?.Of(item.Tracker) is null)
        {
            throw new FlightException($"{item.Display} needs a title: this node cannot read a {item.Tracker} item's, so give one as in create-flight repo/<path> {item.Display} <title>");
        }

        var plane = await planes.MakeAsync(repoPath, code, cancellationToken, item);
        state.PutFlight(new FlightRow
        {
            Code = code,
            Repo = repo,
            RepoPath = plane.RepoPath,
            Issue = item.Id,
            Tracker = item.Tracker,
            Title = title.Length > 0 ? title : null,
            Branch = plane.Branch,
            Worktree = plane.Worktree,
            Base = plane.Base,
            State = FlightState.Flying,
            CreatedAt = time.GetUtcNow(),
        });
        if (live is not null)
        {
            state.RaiseCard(CardBin.Handled, "second-flight-made", $"flight/{code}", $"{code} was made on {item.Display} beside {live.Code}, with a reason: {LegCommander.OneLine(duplicateWhy)}",
                $"{command.Who} overrode the one-flight rule: {live.Code} was not landed ({live.State}) when {code} was created.\n\nWhy: {duplicateWhy}");
            // Two live flights on one issue: one card offers to withdraw the newer, beside steering the other (a duplicate is withdrawn
            // only by this button or a person's word, never by the node or the watchtower on its own).
            RaiseDuplicate(code, live, item, $"made anyway: {duplicateWhy}");
        }
        await LabelAsync(repoPath, item, code, otherNode, cancellationToken);
        return (code, null);
    }

    /// <summary>
    /// Says on the item that this node flies it, and with <paramref name="overridden"/> (a create given --anyway over another
    /// node's label) takes that label off and comments why. The flight is made by now, so a refusal is a For-you card, not a lost flight.
    /// </summary>
    private async Task LabelAsync(string repoPath, WorkItem item, string code, string? overridden, CancellationToken cancellationToken, int? pullRequest = null)
    {
        if (Trackers?.Of(item.Tracker) is not { } tracker)
        {
            return;
        }
        await _setup.EnsureAsync(tracker, repoPath, code, cancellationToken);
        try
        {
            await tracker.AddLabelAsync(repoPath, item.Id, PilotMarks.NodeLabel(Machine), cancellationToken);
            if (overridden is not null)
            {
                await tracker.RemoveLabelAsync(repoPath, item.Id, PilotMarks.NodeLabel(overridden), cancellationToken);
                await tracker.CommentAsync(repoPath, item.Id,
                    $"The controller created flight {code} on {Machine} anyway, over the label {PilotMarks.NodeLabel(overridden)}: this item was flying on {overridden}.", cancellationToken);
            }
        }
        catch (FlightException ex)
        {
            state.RaiseCard(CardBin.Handled, "node-label-refused", $"flight/{code}", $"{code} is flying, but {item.Noun} has no {PilotMarks.NodeLabel(Machine)} label", ex.Message);
        }
        // The flight's PR (a hand-over has one already) says which node flies it as its item does; OpenPr gives it the label for a new PR.
        if (pullRequest is { } pr && github is not null)
        {
            try
            {
                await github.AddIssueLabelAsync(repoPath, pr, PilotMarks.NodeLabel(Machine), cancellationToken);
            }
            catch (FlightException ex)
            {
                state.RaiseCard(CardBin.Handled, "node-label-refused", $"flight/{code}", $"{code} is flying, but its PR #{pr} has no {PilotMarks.NodeLabel(Machine)} label: {LegCommander.OneLine(ex.Message)}", ex.Message);
            }
        }
    }

    private readonly RepoSetup _setup = new(state);

    /// <summary>
    /// The For you card of two live flights on one work item of one repo: withdraw <paramref name="code"/> (the one that came second) or keep
    /// both. <paramref name="how"/> says how it came (made anyway, adopted, received).
    /// </summary>
    private void RaiseDuplicate(string code, FlightRow live, WorkItem item, string how) =>
        state.RaiseCard(CardBin.ForYou, DuplicateFlightsKind, $"flight/{code}", $"{code} and {live.Code} both fly {item.Display}",
            $"{live.Code} ({live.State}) and {code} are live on {item.Noun}. {code} was {how}. If it was a mistake and has done no work, withdraw it; otherwise steer {live.Code} and keep both.",
            [
                new CardChoice { Label = $"Withdraw {code}", Verb = Verbs.WithdrawFlight, Target = $"flight/{code}", Args = [$"a duplicate of {live.Code}, made by mistake"] },
                new CardChoice { Label = "Keep both", Verb = Verbs.Dismiss, Target = "" },
            ]);

    /// <summary>
    /// A flight that came here by adopt-flight or receive-flight on an item a live flight of this repo already flies here gets the same
    /// duplicate card as create --anyway (#1130: only the create path raised it, and a second flight reached here no other way said nothing).
    /// </summary>
    private void RaiseDuplicateIfTwin(FlightRow arrived, string how)
    {
        if (state.Flights().FirstOrDefault(f => f.Code != arrived.Code && f.State != FlightState.Landed && f.Issue == arrived.Issue && f.Tracker == arrived.Tracker
                && string.Equals(f.Repo, arrived.Repo, StringComparison.OrdinalIgnoreCase)) is { } live)
        {
            RaiseDuplicate(arrived.Code, live, arrived.Item, how);
        }
    }
}
