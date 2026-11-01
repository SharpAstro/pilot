using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A node in maintenance moves its flights out at a leg boundary (#376): the leg flying finishes, no new leg starts here, and the
/// flight, once nothing of it is flying, is made safe (plane clean, branch pushed fast-forward only, origin's head read back equal
/// to the plane's), sent to the best willing node with <see cref="Verbs.ReceiveFlight"/>, and let go. The same move runs by hand
/// (<see cref="Verbs.MoveFlight"/>). Any failure leaves the flight here with <see cref="FlightRow.MoveFailure"/> and a card offering to
/// move it again, and the flight is flown on one node only: it is the sender's until the receiver has answered that it took it.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The ring a move that failed leaves on the flight, saying why and offering to move it again.</summary>
    public const string MoveFailedKind = "move-failed";

    /// <summary>The line a flight that went to another node leaves, naming the node.</summary>
    public const string MovedAwayKind = "moved-away";

    // The flights being handed over right now: no session of one starts meanwhile, so the flight is never flown on two nodes.
    private readonly ConcurrentDictionary<string, bool> _moving = new(StringComparer.OrdinalIgnoreCase);

    // A flight's session starts while it is being moved are refused (Launching names the flight by the code after its key's colon).
    private void RefuseIfMoving(string key, string what)
    {
        var code = key[(key.IndexOf(':') + 1)..];
        if (_moving.ContainsKey(code))
        {
            throw new FlightException($"{what} does not start: the flight is being moved to another node");
        }
        // Nor while it is not known whether the other node took it: the flight is flown on one node only, so on none until that is known.
        if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { MoveUnknown: { } unknown })
        {
            throw new FlightException($"{what} does not start: it is not known yet whether {unknown.To} took the flight");
        }
    }

    /// <summary>
    /// How long after a hand-over was sent a node that says it does not hold the flight is believed: a receive-flight whose reply was slow
    /// or lost may still be under way on the other node (the node does not cancel it when the sender goes), and a flight not held there
    /// <em>yet</em> is not one it will not hold. Past it, the flight stays here with the failure, as any failed move.
    /// </summary>
    public TimeSpan MoveSettle { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Why the flight is not to move yet, as a refusal says it; null when nothing of it is flying or starting: its legs are done or
    /// planned (the next one is for the node that takes it), and no reviewer, validator, planner or worker is at work on its plane.
    /// </summary>
    private string? NotYetToMove(FlightRow flight)
    {
        if (flight.MoveUnknown is { } unknown)
        {
            return $"{flight.Code} may already be {unknown.To}'s: it is not known yet whether {unknown.To} took it, so it is not moved again until that is known";
        }
        // A held flight moves as readily as a flying one, more so: nothing of it has started. Its waits go with it (HandOverAsync).
        if (flight.State == FlightState.Landed)
        {
            return $"{flight.Code} is landed{(flight.MovedTo is { } to ? $" ({to.Text})" : "")}: only a flight in the air is moved";
        }
        var legs = state.Legs(flight.Code);
        if (legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying)
        {
            return $"{flight.Code}/{flying.Name} is flying: a flight moves once its leg has finished";
        }
        if (legs.FirstOrDefault(l => l.State == LegState.Failed) is { } failed)
        {
            return $"{flight.Code}/{failed.Name} failed: redo it or take it out first, a flight is not moved with a failed leg";
        }
        if (Starting(flight.Code))
        {
            return $"a session of {flight.Code} is being started: wait for it, then move it";
        }
        if (flight.Review is { State: ReviewState.Reviewing })
        {
            return $"{flight.Review.Reviewer} is reading {flight.Code}: a flight moves once its review has said";
        }
        if (state.Sessions().FirstOrDefault(s => s.State == SessionState.Working && string.Equals(s.Flight, flight.Code, StringComparison.Ordinal)) is { } working)
        {
            return $"{working.Name} is still working on {flight.Code}'s plane: a flight moves once it has stopped";
        }
        // A validator reading the next leg, a package the flight holds for, an overlap's answer, or no leg planned yet: left to whoever has it.
        // A held flight's wait is a flight or a package, which it takes with it.
        return flight.State == FlightState.Flying && Due(flight) is null ? $"{flight.Code} waits for something else (a validation, a package, an overlap's answer, or its plan): move it once that is done" : null;
    }

    /// <summary>
    /// The node's own sweep in maintenance: a flight due to move is moved, where a flight otherwise takes its next step. A flight a
    /// move failed on is held here: its next leg does not start, but its PR and its review go on as they do anywhere. Returns
    /// whether the sweep is done with the flight.
    /// </summary>
    private async Task<bool> InMaintenanceAsync(FlightRow current, CancellationToken cancellationToken)
    {
        if (!Models.Maintenance)
        {
            return false;
        }
        if (current.MoveFailure is not null)
        {
            return Due(current) is { Step.Verb: Verbs.StartLeg };
        }
        if (NotYetToMove(current) is null)
        {
            // A failure is on the flight and a card already; a refusal for a flight that changed since the look is the next sweep's to see.
            try
            {
                await MoveAsync(current.Code, cancellationToken);
            }
            catch (FlightException)
            {
            }
        }
        return true;
    }

    /// <summary>
    /// Moves the flight <paramref name="code"/> to another node, as maintenance does at a leg boundary. Throws <see cref="FlightException"/>
    /// when it is not to move yet (<see cref="NotYetToMove"/>), changing nothing. Returns what moved (<c>TIA1 moved to DE1 after leg parser</c>),
    /// or why the move failed, which is then on the flight and on a card as well: the flight stays here, flown from here alone.
    /// </summary>
    public async Task<(string? Moved, string? Failure)> MoveAsync(string code, CancellationToken cancellationToken)
    {
        // A move whose outcome is not known is asked about first: the other node may well have the flight, which is then moved, not moved again.
        if (state.Flights().Any(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase) && f.MoveUnknown is not null)
            && await SettleMoveAsync(code, cancellationToken) is { Moved: { } already })
        {
            return (already, null);
        }
        var call = await EnterAsync(code, "being moved to another node", cancellationToken);
        var moving = false;
        (FlightRow Flight, string Text)? moved = null;
        try
        {
            var flight = state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) ?? throw new FlightException($"there is no flight {code}");
            if (NotYetToMove(flight) is { } why)
            {
                throw new FlightException(why);
            }
            // From here, under the lock, no session of the flight starts until it is moved or the move has failed.
            moving = _moving.TryAdd(flight.Code, true);
            var (to, failure, unknown) = await OffLockAsync(null, () => HandOverAsync(flight, cancellationToken));
            if (to is null)
            {
                RecordMoveFailure(flight, failure!, unknown);
                return (null, failure);
            }
            var text = RecordMoved(flight, to);
            moved = (flight, text);
            return (text, null);
        }
        finally
        {
            if (moving)
            {
                _moving.TryRemove(code, out _);
            }
            call.Dispose();
            _one.Release();
            // The flight is the other node's: what this one still holds of it is let go with the lock released, git and GitHub being slow.
            if (moved is { } let)
            {
                await LetGoOfPlaneAsync(let.Flight, let.Text);
            }
        }
    }

    /// <summary>
    /// The calls of a move, with the lock let go: the best willing node is chosen as create-flight routing chooses it
    /// (<see cref="FleetChoice"/>), the plane made safe, and the flight sent to each willing node in turn until one takes it. The node
    /// that took it, or null and why not.
    /// </summary>
    private async Task<(string? To, string? Failure, FlightMoveUnknown? Unknown)> HandOverAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var gitHub = RepoCatalog.GitHubOf(Path.Combine(flight.RepoPath, ".git"));
        var peers = flights.Fleet?.Peers() ?? [];
        var facts = await Task.WhenAll(peers.Select(p => FlightCommander.ReadPeerAsync(p, gitHub, time.GetUtcNow(), cancellationToken)));
        var verdicts = FleetChoice.Judge(facts.Select(f => f.Facts), gitHub);
        var willing = verdicts.Where(v => v.Reason is null).ToList();
        if (willing.Count == 0)
        {
            return (null, Failed(flight, null, peers.Count == 0
                ? "this node knows no other node of the fleet. Grant it one, or fly the flight on here."
                : $"no node of the fleet takes it ({string.Join("; ", verdicts.Select(v => $"{v.Name}: {v.Reason}"))}). Put one out of maintenance and willing (accept-fleet-flights on), or move it again later."), null);
        }

        // The node it is named for in a failure: the one chosen, when it is the only one willing.
        var named = willing.Count == 1 ? willing[0].Name : null;
        if (await MakeSafeAsync(flight, cancellationToken) is { } unsafePlane)
        {
            return (null, Failed(flight, named ?? willing[0].Name, unsafePlane), null);
        }

        var move = FlightMoves.Gather(state, flight.Code, options.Machine);
        // What is the sender's alone does not go: the failure of an earlier try, and its card. The head is what origin was read back to hold.
        var head = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
        move = move with { Head = head.Ok ? head.Output.Trim() : null, Flight = WaitsFromAfar(move.Flight with { MoveFailure = null, MoveUnknown = null }), Cards = [.. move.Cards.Where(c => c.Kind != MoveFailedKind)] };
        var refused = new List<string>();
        foreach (var verdict in willing)
        {
            var peer = facts.Single(f => ReferenceEquals(f.Facts, verdict.Facts)).Peer;
            var sentAt = time.GetUtcNow();
            string lost;
            try
            {
                var result = await peer.SendAsync(move, $"{options.Machine}/node", cancellationToken);
                if (result is { Ok: true, Waiting: null })
                {
                    return (verdict.Name, null, null);
                }
                if (!result.Ok)
                {
                    refused.Add($"{verdict.Name} refused it ({result.Error ?? "without a reason"})");
                    continue;
                }
                // Past its reply deadline the other node answers "still running": it has not said it took the flight, and may yet.
                lost = $"it was still taking it when its reply deadline passed ({result.Waiting})";
            }
            catch (Exception ex) when (IsNeverDelivered(ex))
            {
                refused.Add($"{verdict.Name} could not be reached ({ex.Message})");
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
            {
                lost = $"its answer did not come ({ex.Message})";
            }

            // The flight may be taken and the answer lost: the other node is asked, and the flight is sent to no other node meanwhile.
            var unknown = new FlightMoveUnknown { To = verdict.Name, Endpoint = peer.Endpoint, Since = sentAt };
            if (await AskPeerAsync(peer, flight.Code) == PeerHolds.Yes)
            {
                return (verdict.Name, null, null);
            }
            return (null, $"{flight.Code} may have moved to {verdict.Name}: {lost}. {flight.Code} stays here and is flown by nobody until {verdict.Name} says whether it holds it (asked again every sweep, and settled {MoveSettle.TotalMinutes:0} min after it was sent), or move it again.", unknown);
        }
        return (null, Failed(flight, named, $"{string.Join("; ", refused)}. Put that right there, or move it again."), null);
    }

    /// <summary>
    /// The flight's waits as another node reads them: a flight of this node it waits for becomes a wait for that flight on this node
    /// (or on the node it moved to), and one that landed here is waited for no longer. The receiver takes a wait naming itself back as its own.
    /// </summary>
    private FlightRow WaitsFromAfar(FlightRow flight)
    {
        if (flight.Connections.Length == 0)
        {
            return flight;
        }
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        var now = time.GetUtcNow();
        var waits = flight.PeerConnections.ToList();
        foreach (var code in flight.Connections)
        {
            var node = !flights.TryGetValue(code, out var other) ? options.Machine
                : other.MovedTo is { } moved ? moved.To
                : other.State != FlightState.Landed ? options.Machine
                : null;
            if (node is not null && !waits.Any(w => string.Equals(w.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                waits.Add(new PeerConnection { Code = code, Node = node, Since = now });
            }
        }
        return flight with { Connections = [], PeerConnections = [.. waits] };
    }

    // A connection that was never made: the request did not reach the node, so it cannot have taken the flight.
    private static bool IsNeverDelivered(Exception ex) =>
        ex is HttpRequestException { InnerException: System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.NetworkUnreachable } };

    private enum PeerHolds { Yes, No, Unreachable }

    /// <summary>Asks <paramref name="peer"/> what it holds: it holds the flight when its snapshot has it, handed over by this node and not itself moved away from there.</summary>
    private async Task<PeerHolds> AskPeerAsync(IFleetPeer peer, string code)
    {
        using var budget = new CancellationTokenSource(HeldTokenFleet.ReadBudget * 2);
        try
        {
            var load = await peer.LoadAsync(budget.Token);
            return load.Flights.Any(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)
                && f.MovedTo is null && f.MovedFrom is { } from && string.Equals(from.From, options.Machine, StringComparison.OrdinalIgnoreCase))
                ? PeerHolds.Yes
                : PeerHolds.No;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            return PeerHolds.Unreachable;
        }
    }

    /// <summary>
    /// Settles a move whose outcome was not known (<see cref="FlightRow.MoveUnknown"/>) by asking the other node: when it holds the flight,
    /// the flight is moved (recorded, let go of here); when it says it does not and the settling time has passed since the hand-over was
    /// sent, the move failed as any failed move; otherwise the flight waits, flown by nobody. Moved is what moved; Unknown, whether it is still not known.
    /// </summary>
    public async Task<(string? Moved, bool Unknown)> SettleMoveAsync(string code, CancellationToken cancellationToken)
    {
        var call = await EnterAsync(code, "being asked where it moved", cancellationToken);
        (FlightRow Flight, string Text)? moved = null;
        try
        {
            if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { MoveUnknown: { } unknown } flight)
            {
                return (null, false);
            }
            var peer = flights.Fleet?.Peers().FirstOrDefault(p => string.Equals(p.Endpoint, unknown.Endpoint, StringComparison.OrdinalIgnoreCase));
            var holds = peer is null ? PeerHolds.Unreachable : await OffLockAsync(null, () => AskPeerAsync(peer, flight.Code));
            // Whatever changed the row meanwhile (a settling by another call) is that call's.
            if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { MoveUnknown: not null } now)
            {
                return (null, false);
            }
            flight = now;
            if (holds == PeerHolds.Yes)
            {
                var text = RecordMoved(flight, unknown.To);
                moved = (flight, text);
                return (text, false);
            }
            if (holds == PeerHolds.No && time.GetUtcNow() - unknown.Since >= MoveSettle)
            {
                RecordMoveFailure(flight, $"{flight.Code} did not move to {unknown.To}: {unknown.To} does not hold it {MoveSettle.TotalMinutes:0} minutes after it was sent, so the flight stays here. Move it again.", null);
                return (null, false);
            }
            return (null, true);
        }
        finally
        {
            call.Dispose();
            _one.Release();
            if (moved is { } let)
            {
                await LetGoOfPlaneAsync(let.Flight, let.Text);
            }
        }
    }

    private static string Failed(FlightRow flight, string? to, string why) =>
        $"{flight.Code} could not move{(to is null ? "" : $" to {to}")}: {why}";

    /// <summary>
    /// Makes the plane safe to hand over: clean, its branch pushed to origin fast-forward only (never forced, never over a lease), and
    /// the head origin has read back equal to the plane's. Null when it is; else what happened and what to do, as a failure says it.
    /// </summary>
    private async Task<string?> MakeSafeAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var plane = flight.Worktree;
        var branch = flight.Branch;
        var remoteRef = $"refs/heads/{branch}";
        if (!Directory.Exists(plane))
        {
            return $"its plane {plane} is gone, so there is nothing to push. Make the plane again, or land the flight.";
        }
        var status = await git.RunAsync(["status", "--porcelain"], plane, cancellationToken);
        if (!status.Ok)
        {
            return $"git could not read the plane ({status.Why}). Look at it, then move it again.";
        }
        var dirty = status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (dirty.Length > 0)
        {
            return $"the plane holds {dirty.Length} change{(dirty.Length == 1 ? "" : "s")} not committed ({string.Join(", ", dirty.Take(3).Select(l => l.Trim()))}{(dirty.Length > 3 ? ", ..." : "")}). Commit or drop {(dirty.Length == 1 ? "it" : "them")}, then move it again.";
        }
        var head = await git.RunAsync(["rev-parse", "HEAD"], plane, cancellationToken);
        if (!head.Ok)
        {
            return $"git could not read the plane's head ({head.Why}). Look at it, then move it again.";
        }
        var planeHead = head.Output.Trim();

        var remote = await RemoteHeadAsync(plane, remoteRef, cancellationToken);
        if (remote.Why is { } unreachable)
        {
            return $"git could not ask origin for {branch} ({unreachable}). Check the network, then move it again.";
        }
        if (remote.Sha != planeHead)
        {
            // Only a head that is a stage of the plane's own history is pushed to: a remote that holds more is never overwritten or forced.
            if (remote.Sha is not null)
            {
                var fetch = await git.RunAsync(["fetch", "--quiet", "origin", $"+{remoteRef}:refs/remotes/origin/{branch}"], plane, cancellationToken);
                if (!fetch.Ok)
                {
                    return $"git could not fetch {branch} from origin ({fetch.Why}). Check the network, then move it again.";
                }
                if (!(await git.RunAsync(["merge-base", "--is-ancestor", remote.Sha, planeHead], plane, cancellationToken)).Ok)
                {
                    return "the push was refused, the remote holds commits the plane lacks. Merge them in, or move it again.";
                }
            }
            try
            {
                // GitHub's auto-merge, armed on the PR, would merge what is pushed unreviewed: the push is not made while it may be.
                await EnsureNotArmedAsync(flight, cancellationToken);
            }
            catch (FlightException ex)
            {
                return ex.Message;
            }
            // No -f and no lease: a push git would have to force is refused, as the remote holding more is.
            var push = await git.RunAsync(["push", "-u", "origin", branch], plane, cancellationToken);
            if (!push.Ok)
            {
                return $"the push was refused ({push.Why}). Look at {branch} on origin, then move it again.";
            }
            remote = await RemoteHeadAsync(plane, remoteRef, cancellationToken);
        }
        // What the receiver makes its plane from is what origin holds, so that is what is compared with the plane's head.
        if (remote.Sha != planeHead)
        {
            return $"origin has {(remote.Sha is null ? "no" : remote.Sha[..Math.Min(8, remote.Sha.Length)])} for {branch} after the push, not the plane's {planeHead[..Math.Min(8, planeHead.Length)]}. Look at {branch} on origin, then move it again.";
        }
        return null;
    }

    /// <summary>The head origin holds for <paramref name="remoteRef"/> now (null when it has none), or why it could not be asked.</summary>
    private async Task<(string? Sha, string? Why)> RemoteHeadAsync(string plane, string remoteRef, CancellationToken cancellationToken)
    {
        var read = await git.RunAsync(["ls-remote", "origin", remoteRef], plane, cancellationToken);
        if (!read.Ok)
        {
            return (null, read.Why);
        }
        var line = read.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.EndsWith(remoteRef, StringComparison.Ordinal));
        return (line?.Split('\t', ' ')[0], null);
    }

    // The flight stays here: its reason on the row and a card that moves it again, which the next failure rewrites rather than doubles.
    private void RecordMoveFailure(FlightRow flight, string failure, FlightMoveUnknown? unknown)
    {
        Record(flight.Code, f => f with { MoveFailure = failure, MoveUnknown = unknown });
        var split = failure.IndexOf(": ", StringComparison.Ordinal);
        var (title, detail) = split > 0 ? (failure[..split], failure[(split + 2)..]) : (failure, null);
        var subject = Verbs.FlightTarget(flight.Code);
        CardChoice[] retry = [new CardChoice { Label = "Retry move", Verb = Verbs.MoveFlight, Target = subject }];
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == MoveFailedKind && c.Subject == subject) is { } open)
        {
            state.UpdateCard(open.Id, title, detail, retry, open.Link);
        }
        else
        {
            state.RaiseCard(CardBin.Ring, MoveFailedKind, subject, title, detail, retry, flight.PullRequestUrl);
        }
    }

    /// <summary>
    /// The other node has the flight: it is no longer this node's. Recorded under the lock at once, before anything else is done, so no
    /// leg or sweep here can take it up again; then its cards close here with a line naming the node; the caller lets go of its sessions and its plane.
    /// </summary>
    private string RecordMoved(FlightRow flight, string to)
    {
        var at = time.GetUtcNow();
        var after = state.Legs(flight.Code).Where(l => l.State == LegState.Done).OrderByDescending(l => l.Order).FirstOrDefault()?.Name;
        var movedTo = new FlightMovedTo { To = to, AfterLeg = after, At = at };
        Record(flight.Code, f => f with { State = FlightState.Landed, LandedAt = at, MovedTo = movedTo, MoveFailure = null, MoveUnknown = null });
        _rung.TryRemove(flight.Code, out _);
        var text = $"{flight.Code} {movedTo.Text}";
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), text);
        state.RaiseCard(CardBin.Handled, MovedAwayKind, Verbs.FlightTarget(flight.Code), text,
            $"{options.Machine} handed {flight.Code} over to {to}{(Models.Maintenance ? $", as {options.Machine} is in maintenance" : "")}: {to} flies it from here, and {options.Machine} no longer does");
        return text;
    }

    // What this node still holds of a flight it no longer flies: its idle sessions, its plane (its branch stays), its label on the item.
    private async Task LetGoOfPlaneAsync(FlightRow flight, string text)
    {
        foreach (var session in state.Sessions().Where(s => s.State != SessionState.Ended && string.Equals(s.Flight, flight.Code, StringComparison.Ordinal)))
        {
            await StopQuietly(session.Id, CancellationToken.None);
        }
        try
        {
            if (await planes.RemoveAsync(flight.RepoPath, flight.Worktree, CancellationToken.None) is { } leftover)
            {
                if (planes.Queue is { } queue)
                {
                    queue.Add(flight.Worktree, RemovalQueue.PlaneKind, flight.Code, leftover);
                }
                state.RaiseCard(CardBin.Handled, "plane-left", Verbs.FlightTarget(flight.Code), text, $"left {flight.Worktree} for the next start: a process holds it");
            }
        }
        catch (FlightException ex)
        {
            state.RaiseCard(CardBin.ForYou, "plane-left", Verbs.FlightTarget(flight.Code), $"{text}, but its plane folder is still here", $"{ex.Message}; the flight is no longer flown here, so delete the folder by hand");
        }
        if (flights.Trackers?.Of(flight.Tracker) is { } tracker)
        {
            try
            {
                await tracker.RemoveLabelAsync(flight.RepoPath, flight.Issue, PilotMarks.NodeLabel(options.Machine), CancellationToken.None);
            }
            catch (FlightException ex)
            {
                state.RaiseCard(CardBin.ForYou, "node-label-refused", Verbs.FlightTarget(flight.Code), $"{PilotMarks.NodeLabel(options.Machine)} is still on {flight.Item.Noun} {flight.Item.Display}", ex.Message);
            }
        }
    }
}
