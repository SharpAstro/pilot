using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The overlap verbs: a planner finds its flight overlaps another (<c>overlap</c>, with the files both change), the controller
/// answers on a card (<c>fold</c>, <c>connect-overlap</c>, <c>separate</c>), and the answer is kept on the flight per pair, so the same
/// pair is never asked twice. The other flight may fly on another node: it can be connected to from here, never folded into.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The card an overlap rings: the flight overlaps another, to fold into, connect to or plan in parallel with.</summary>
    public const string OverlapKind = CardKinds.Overlap;

    /// <summary>The prefix of an argument of <c>overlap</c> that names a file both flights change.</summary>
    public const string OverlapFileArg = Verbs.OverlapFileArg;

    /// <summary>The most shared files a card's title names before it says how many more there are.</summary>
    private const int OverlapNamed = 4;

    /// <summary>
    /// <c>overlap flight/&lt;code&gt; &lt;other&gt; [file=&lt;path&gt;…] &lt;why…&gt;</c>: returns what the planner is told. A new pair is recorded
    /// pending and rings one card; a pair asked before answers with what is recorded, and rings nothing. The other is a flight of this
    /// node or of a reachable peer.
    /// </summary>
    private async Task<string> AskOverlapAsync(CommandRequest command, CancellationToken cancellationToken)
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
        string? node = null;
        if (other is null && args.Length > 0)
        {
            // Not of this node: asked of the peers, with the lock let go. A pair asked before is answered from the record when no peer can say.
            var (found, unasked) = await OffLockAsync(null, () => flights.FindOnPeersAsync(flight.RepoPath, args[0], cancellationToken));
            flight = FlightOf(command.Target);
            if (found.FirstOrDefault() is { } peer)
            {
                (other, node) = (peer.Flight, peer.Node);
            }
            else if (flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, args[0], StringComparison.OrdinalIgnoreCase)) is { } recorded)
            {
                return KnownAnswer(flight, recorded);
            }
            else
            {
                throw new FlightException($"There is no flight {args[0]}{(unasked.Count > 0 ? $" on this node, and a node could not be asked: {string.Join("; ", unasked)}" : "")}");
            }
        }
        if (other is null)
        {
            throw new FlightException($"overlap needs the code of the flight {flight.Code} overlaps, and why, as in overlap flight/{flight.Code} TIA1611 file=src/Parser.cs <why>");
        }
        if (other.Code == flight.Code)
        {
            throw new FlightException($"{flight.Code} cannot overlap itself");
        }
        if (other.State == FlightState.Landed)
        {
            if (other is { MovedTo: null, FoldedInto: null })
            {
                // Answered at once, no row and no card (PIL824): its work is in, so there is nothing to wait for or to ask.
                return $"{other.Code} has landed{(node is null ? "" : $" on {node}")}: its work is in, so there is nothing to wait for and no card rings; plan {flight.Code} on top of it";
            }
            throw new FlightException($"{other.Code} has landed: its work is in, so build on it instead of folding into it");
        }
        var named = args.Skip(1).TakeWhile(a => a.StartsWith(OverlapFileArg, StringComparison.Ordinal) || a.StartsWith(Verbs.OverlapPickArg, StringComparison.Ordinal)).ToArray();
        var files = named.Where(a => a.StartsWith(OverlapFileArg, StringComparison.Ordinal)).Select(a => a[OverlapFileArg.Length..].Trim()).Where(a => a.Length > 0).ToArray();
        var picked = named.Where(a => a.StartsWith(Verbs.OverlapPickArg, StringComparison.Ordinal)).Select(a => a[Verbs.OverlapPickArg.Length..].Trim().ToLowerInvariant()).LastOrDefault();
        if (picked is not null && picked is not (Verbs.OverlapPickFold or Verbs.OverlapPickConnect))
        {
            throw new FlightException($"pick= is {Verbs.OverlapPickFold} or {Verbs.OverlapPickConnect}, not '{picked}'");
        }
        // A flight of another node is never folded into: its card says connect whatever touches leaned to.
        var fold = picked == Verbs.OverlapPickFold && node is null;
        var why = string.Join(' ', args.Skip(1 + named.Length)).Trim();

        if (flight.Overlaps.FirstOrDefault(o => o.Other == other.Code) is { } known)
        {
            return KnownAnswer(flight, known);
        }
        if (node is null && AnswerOnPair(flight, other.Code, null) is { Reverse: true } reverse)
        {
            // Answered on the other's card (PIL589): mirrored onto this flight so the next ask finds it on its own side. Connecting back would cycle.
            var mirrored = new OverlapRow
            {
                Other = other.Code, Node = null, Files = files.Length > 0 ? files : reverse.Row.Files, Pick = Verbs.OverlapPickConnect,
                Why = $"answered from {other.Code}'s card ({reverse.Answer.ToString().ToLowerInvariant()})", Answer = OverlapAnswer.Separate, At = time.GetUtcNow(),
            };
            state.PutFlight(flight with { Overlaps = [.. flight.Overlaps, mirrored] });
            var how = reverse.Answer == OverlapAnswer.Connect
                ? $"{other.Code} was connected to {flight.Code} on its card, so {flight.Code} cannot connect back"
                : $"{flight.Code} and {other.Code} were answered on {other.Code}'s card";
            return $"{how}: plan {flight.Code} in parallel with {other.Code}, and connect it (flight connect) only if it still depends on it; no card rings again";
        }
        if (why.Length == 0)
        {
            throw new FlightException($"overlap needs why {flight.Code} overlaps {other.Code}, as in overlap flight/{flight.Code} {other.Code} file=src/Parser.cs <why>");
        }
        if (node is null && other.Overlaps.FirstOrDefault(o => IsPendingAbout(o, flight.Code)) is { } theirs)
        {
            // The other holds a card on this flight (PIL664): answered parallel by the node on both sides, so the planner plans on.
            var (_, released) = AnswerMutualParallel(flight, null, other, theirs, files);
            try
            {
                await GoOnAfterParallelAnswerAsync(released, theirs, $"{released.Code} is planned in parallel with {flight.Code} by the node", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Error.WriteLineAsync($"overlap: {released.Code} could not go on after the mutual parallel answer: {ex.Message}");
            }
            return $"{other.Code} held a card on {flight.Code} as well, so the node answered it: plan {flight.Code} in parallel with {other.Code}, and connect it (flight connect) only if it still depends on it; no card rings";
        }
        if (flight.Overlaps.FirstOrDefault(o => o.Answer == OverlapAnswer.Pending) is { } waiting)
        {
            throw new FlightException($"{flight.Code} waits for the answer on {waiting.Other}{CardSaid(flight)}; ask about {other.Code} once that is answered");
        }

        state.PutFlight(flight with
        {
            Overlaps = [.. flight.Overlaps, new OverlapRow { Other = other.Code, Node = node, Files = files.Length > 0 ? files : null, Pick = fold ? Verbs.OverlapPickFold : Verbs.OverlapPickConnect, Why = why, Answer = OverlapAnswer.Pending, At = time.GetUtcNow() }],
        });
        var target = Verbs.FlightTarget(flight.Code);
        var at = node is null ? other.Code : $"{other.Code} on {node}";
        var names = ShortNames(files);
        var shared = names.Length switch
        {
            0 => why,
            > OverlapNamed => $"both change {string.Join(", ", names.Take(OverlapNamed))} and {names.Length - OverlapNamed} more",
            _ => $"both change {string.Join(", ", names)}",
        };
        List<CardChoice> choices = [];
        if (node is null)
        {
            choices.Add(new CardChoice { Label = $"Fold into {other.Code}", Verb = Verbs.Fold, Target = target, Args = [other.Code] });
        }
        choices.Add(new CardChoice { Label = $"Connect to {other.Code}", Verb = Verbs.ConnectOverlap, Target = target, Args = [other.Code] });
        choices.Add(new CardChoice { Label = $"Plan {flight.Code} in parallel with {other.Code}", Verb = Verbs.Separate, Target = target, Args = [other.Code] });

        // The title names the choice the planner leans to: a fold reads "Fold PIL365 into PIL294", a connect "Connect PIL365 to PIL294".
        var title = fold ? $"Fold {flight.Code} into {at} ({shared})" : $"Connect {flight.Code} to {at} ({shared})";
        var card = state.RaiseCard(CardBin.Ring, OverlapKind, target, title, OverlapDetail(flight, other, node, files, why), choices);
        return $"asked the controller on card {card.Id}: {flight.Code} overlaps {at}. Plan no leg and say no planned: end your turn now, you are started again with the answer";
    }

    /// <summary>
    /// The files as a card names them: by file name (<c>DetachedShell.cs</c>), where two share one by their path, so a long list fits the
    /// panel. The row keeps the paths whole.
    /// </summary>
    private static string[] ShortNames(string[] files)
    {
        var shared = files.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. files.Select(f => shared.Contains(Path.GetFileName(f)) ? f : Path.GetFileName(f))];
    }

    /// <summary>What the overlap card says: both flights, the files, and what each choice does.</summary>
    private static string OverlapDetail(FlightRow flight, FlightRow other, string? node, string[] files, string why)
    {
        static string Named(FlightRow row) => row.Title is { Length: > 0 } title ? $"{row.Code} ({title})" : row.Code;
        var text = new System.Text.StringBuilder();
        text.Append($"{Named(flight)} overlaps {Named(other)}{(node is null ? "" : $", which flies on {node}")}.");
        text.Append($" The planner says: {why}.");
        text.Append($" Connect holds {flight.Code} until {other.Code} has landed (a card raised at a leg's end holds the next leg, never the one finished).");
        text.Append(" Plan in parallel flies both; the second to land resolves the conflicts.");
        text.Append(node is null
            ? $" Fold takes {flight.Code}'s issue into {other.Code}."
            : $" {flight.Code} cannot be folded into it from here.");
        text.Append(" Its planner waits for your answer.");
        // Last: the card's detail is cut at a few rows (its whole text on hover), and a long file list must not cut what the choices do.
        if (files.Length > 0)
        {
            text.Append($" Both change {files.Length} file{(files.Length == 1 ? "" : "s")}: {string.Join(", ", ShortNames(files))}.");
        }
        return text.ToString();
    }

    private string KnownAnswer(FlightRow flight, OverlapRow known)
    {
        var at = known.Node is null ? known.Other : $"{known.Other} on {known.Node}";
        return known.Answer switch
        {
            OverlapAnswer.Pending => $"{flight.Code} and {known.Other} are asked about already{CardSaid(flight)}: end your turn, you are started again with the answer",
            OverlapAnswer.Separate => $"{flight.Code} and {known.Other} were answered before: plan {flight.Code} in parallel with {at}, and connect it (flight connect) only if it still depends on it; no card rings again",
            OverlapAnswer.Connect => $"{flight.Code} and {known.Other} were answered before: {flight.Code} is connected to {at} and waits for it to land; plan its legs meanwhile; no card rings again",
            _ => $"{flight.Code} and {known.Other} were answered before: fold {flight.Code} into {known.Other}; no card rings again",
        };
    }

    /// <summary>
    /// <c>connect-overlap flight/&lt;code&gt; &lt;other&gt;</c>: records the answer, connects the flight to the other (a flight of this node or
    /// of the peer the overlap found it on), closes the card, stops the planner that waits and starts a fresh one, whose brief says what was
    /// answered. Nothing is recorded when the connect is refused (it would cycle), and the card stays open. Returns what the caller is told.
    /// </summary>
    private async Task<string> ConnectAnswerAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        var known = args.Length > 0 ? flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, args[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (known is null)
        {
            throw new FlightException($"{flight.Code} has no overlap with {(args.Length > 0 ? args[0] : "another flight")} to answer: the planner asks with overlap first");
        }
        var at = known.Node is null ? known.Other : $"{known.Other} on {known.Node}";
        if (known.Answer == OverlapAnswer.Connect)
        {
            return $"{flight.Code} is connected to {at} already";
        }
        if (known.Answer != OverlapAnswer.Pending)
        {
            throw new FlightException($"{flight.Code} and {known.Other} were answered {known.Answer.ToString().ToLowerInvariant()} already");
        }
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }

        // The connect first: it can refuse (the other waits for this flight), and then nothing is recorded and the card stays open.
        var told = await OffLockAsync(null, () => flights.ConnectToAsync(flight.Code, known.Other, known.Node, cancellationToken));
        flight = FlightOf(command.Target);
        if (flight.Overlaps.FirstOrDefault(o => o.Other == known.Other)?.Answer != OverlapAnswer.Pending)
        {
            throw new FlightException($"{flight.Code} and {known.Other} were answered while it was connected: look again");
        }

        flight = flight with { Overlaps = [.. flight.Overlaps.Select(o => o.Other == known.Other ? o with { Answer = OverlapAnswer.Connect, At = time.GetUtcNow() } : o)] };
        state.PutFlight(flight);
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"connect {flight.Code} to {at}", OverlapKind);

        // A question raised at a leg's end (#564) has no planner waiting: the flight goes on at the node's next sweep, no session is started.
        if (known.AfterLeg is not null)
        {
            return await GoOnAfterLegEndAnswerAsync(flight, $"{told ?? $"{flight.Code} is connected to {at}: it waits for it to land"}; no planner is started, as {known.AfterLeg} had finished and stays done; the next leg is held until {at} lands", cancellationToken);
        }
        await StopPlannerAsync(flight, cancellationToken);
        StartPlanner(flight, replacing: true);
        // What the connect said stands: for a flight that landed already it is "nothing to wait for", not "waits".
        return $"{told ?? $"{flight.Code} is connected to {at}: it waits for it to land"}; a fresh planner is started with the answer";
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
            return $"{flight.Code} is planned in parallel with {known.Other} already";
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
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"plan in parallel with {known.Other}", OverlapKind);

        // The planner that asked is waiting for its turn to end: stopped, so a fresh one is told the answer. Its start is not
        // refused for the stopped one, which the tracker may list for a poll yet.
        return await GoOnAfterParallelAnswerAsync(flight, known,
            known.AfterLeg is not null
                ? $"{flight.Code} is planned in parallel with {known.Other}; no planner is started, as {known.AfterLeg} had finished, and the flight goes on"
                : $"{flight.Code} is planned in parallel with {known.Other}; a fresh planner is started with the answer", cancellationToken);
    }

    /// <summary>
    /// After a parallel answer on <paramref name="answered"/>: a leg-end row goes on (<see cref="GoOnAfterLegEndAnswerAsync"/>), a
    /// planner-asked row has its planner stopped and a fresh one started. Called under the lock.
    /// </summary>
    private async Task<string> GoOnAfterParallelAnswerAsync(FlightRow flight, OverlapRow answered, string told, CancellationToken cancellationToken)
    {
        if (answered.AfterLeg is not null)
        {
            return await GoOnAfterLegEndAnswerAsync(flight, told, cancellationToken);
        }
        await StopPlannerAsync(flight, cancellationToken);
        StartPlanner(flight, replacing: true);
        return told;
    }

    /// <summary>
    /// After the answer to a card raised at a leg's end: the check runs again (a second other flight with unforeseen files rings its own card,
    /// one at a time) and the flight advances, off the lock as the finish path does. A connect leaves the next leg held by <see cref="Due"/>
    /// until the other lands; a parallel answer lets it start. No planner is stopped or started.
    /// </summary>
    private async Task<string> GoOnAfterLegEndAnswerAsync(FlightRow flight, string told, CancellationToken cancellationToken)
    {
        // Marked from here, under the lock, so a sweep in the gap starts nothing before the re-check has rung or decided not to.
        MarkLegEndChecking(flight.Code);
        await OffLockAsync(null, async () =>
        {
            var last = state.Legs(flight.Code).Where(l => l.State == LegState.Done).OrderByDescending(l => l.Order).FirstOrDefault()?.Name ?? "the last leg";
            try
            {
                await CheckOverlapAsync(flight.Code, last, cancellationToken);
            }
            finally
            {
                ClearLegEndChecking(flight.Code);
            }
            await AdvanceAsync(FlightOf(Verbs.FlightTarget(flight.Code)), cancellationToken);
        });
        return told;
    }

    /// <summary>The planner this node started for the flight and when, if it still holds one.</summary>
    internal (string Job, DateTimeOffset At)? StartedPlanner(string code) =>
        _plannerJobs.TryGetValue(code, out var job) && _plannersStarted.TryGetValue(code, out var at) ? (job, at) : null;

    /// <summary>Stops the flight's planner and starts a fresh one in its place.</summary>
    internal async Task RestartPlannerAsync(string code, CancellationToken cancellationToken)
    {
        var flight = FlightOf(Verbs.FlightTarget(code));
        await StopPlannerAsync(flight, cancellationToken);
        StartPlanner(flight, replacing: true);
    }

    /// <summary>
    /// Stops a flying leg's worker that took no first turn and starts a fresh one in its place. Not a go-around: the leg's count,
    /// brief and model are as they were, as nothing was done by the first.
    /// </summary>
    internal async Task RestartWorkerAsync(string code, string legName, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(Verbs.LegTarget(code, legName));
        if (leg.WorkerJob is { } job)
        {
            DropQueuedJobsOf(job, "its worker was restarted");
            await StopQuietly(job, cancellationToken);
        }
        // A ticker of the ticking pass (PIL804) must not outlive the worker it was started for: it could tick or finish the new worker's leg.
        // TickerJob is cleared as it is stopped, so a restart whose start fails does not leave the leg pointing at it.
        // The watch calls this off the lock, so the clearing of the stored row takes the lock itself.
        leg = await StopTickerAsync(leg, "its worker was restarted", cancellationToken, locked: false);
        StartWorker(flight, leg, state.Legs(flight.Code));
    }

    /// <summary>
    /// Stops the leg's ticker of the ticking pass (PIL804), if it has one, and clears <see cref="LegRow.TickerJob"/> on the stored row
    /// at once; returns the leg as it is now. Left set, a replacement worker whose start fails would leave the leg looking as if the
    /// ticker we just stopped still worked, and the watch would ring for it ending. <paramref name="locked"/> says the caller holds
    /// the commander's lock (a go-around does); one that does not (the watch's restart) has the row written under it here, so a
    /// finish or a tick meanwhile is not overwritten by a stale copy.
    /// </summary>
    private async Task<LegRow> StopTickerAsync(LegRow leg, string why, CancellationToken cancellationToken, bool locked = true)
    {
        if (leg.TickerJob is not { } tickerJob)
        {
            return leg;
        }
        DropQueuedJobsOf(tickerJob, why);
        await StopQuietly(tickerJob, cancellationToken);
        if (!locked)
        {
            await _one.WaitAsync(cancellationToken);
        }
        try
        {
            // Read fresh, and only this ticker cleared: a row that moved on while it was stopped keeps what it has.
            if (state.Legs(leg.Flight).FirstOrDefault(l => l.Name == leg.Name) is { } stored && string.Equals(stored.TickerJob, tickerJob, StringComparison.Ordinal))
            {
                state.PutLeg(stored with { TickerJob = null });
            }
        }
        finally
        {
            if (!locked)
            {
                _one.Release();
            }
        }
        return leg with { TickerJob = null };
    }

    /// <summary>Stops a leg's validator that took no first turn and starts a fresh one in its place.</summary>
    internal async Task RestartValidatorAsync(string code, string legName, CancellationToken cancellationToken)
    {
        var (flight, leg) = LegOf(Verbs.LegTarget(code, legName));
        if (leg.ValidatorJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        StartValidator(flight, leg, state.Legs(flight.Code));
    }

    /// <summary>Stops a session by its job, whatever it is (claude stop); one already gone is no error.</summary>
    internal Task StopSessionAsync(string job, CancellationToken cancellationToken) => StopQuietly(job, cancellationToken);

    /// <summary>Stops the flight's planner and forgets it.</summary>
    internal Task StopPlannerAsync(string code, CancellationToken cancellationToken) =>
        StopPlannerAsync(FlightOf(Verbs.FlightTarget(code)), cancellationToken);

    /// <summary>Stops the flight's planner, by its row or the job this node started, and forgets it was started.</summary>
    private async Task StopPlannerAsync(FlightRow flight, CancellationToken cancellationToken)
    {
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
    }

    /// <summary>
    /// <c>fold flight/&lt;code&gt; &lt;other&gt;</c>: the flight's issue becomes the other flight's. The other carries the issue
    /// (its planner plans the issue's points as legs after its own, its PR closes both), and this flight is retired as landed with
    /// nothing of its own, marked folded into the other. Returns what the caller is told.
    /// </summary>
    private async Task<string> FoldAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        var known = args.Length > 0 ? flight.Overlaps.FirstOrDefault(o => string.Equals(o.Other, args[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (known is null)
        {
            throw new FlightException($"{flight.Code} has no overlap with {(args.Length > 0 ? args[0] : "another flight")} to answer: the planner asks with overlap first");
        }
        if (known.Node is not null)
        {
            throw new FlightException($"{known.Other} flies on {known.Node}, and a flight folds only into one of this node: its issue and PR are that node's to close; connect {flight.Code} to {known.Other} or plan it in parallel instead");
        }
        if (known.Answer == OverlapAnswer.Fold)
        {
            return $"{flight.Code} is folded into {known.Other} already";
        }
        if (known.Answer != OverlapAnswer.Pending)
        {
            throw new FlightException($"{flight.Code} and {known.Other} were answered {known.Answer.ToString().ToLowerInvariant()} already");
        }
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        var other = FlightOf(Verbs.FlightTarget(known.Other));
        if (other.State == FlightState.Landed)
        {
            throw new FlightException($"{other.Code} has landed: its work is in, so it cannot take {flight.Code}'s issue; plan {flight.Code} separately");
        }
        if (other.PullRequest is { } open)
        {
            throw new FlightException($"{other.Code}'s PR #{open} is open already and would not close #{flight.Issue}; plan {flight.Code} separately");
        }
        if (other.Tracker != flight.Tracker)
        {
            throw new FlightException($"{flight.Code} is on a {flight.Tracker} item ({flight.Item.Display}) and {other.Code} on a {other.Tracker} one ({other.Item.Display}): an item folds only into a flight on the same tracker; plan {flight.Code} separately");
        }
        if (other.Repo != flight.Repo)
        {
            throw new FlightException($"{flight.Code} is in {flight.Repo} and {other.Code} in {other.Repo}: an issue folds only within one repo");
        }

        // Its planner first: it holds the plane open. The retirement can refuse (a plane with work on it), and then nothing
        // is recorded and the card stays open; the planner, stopped, is started again by the controller's next answer.
        await StopPlannerAsync(flight, cancellationToken);
        var answered = flight with { Overlaps = [.. flight.Overlaps.Select(o => o.Other == known.Other ? o with { Answer = OverlapAnswer.Fold, At = time.GetUtcNow() } : o)] };
        // The retirement takes the plane down (its sessions, its worktree): with this lock let go, the flight held meanwhile (PIL338).
        using (Calling(flight.Code, "being folded"))
        {
            await OffLockAsync(null, () => flights.RetireFoldedAsync(answered, other.Code, cancellationToken));
        }

        other = FlightOf(Verbs.FlightTarget(other.Code));
        if (!other.FoldedIn.Any(f => f.Number == flight.Issue))
        {
            other = other with { FoldedIn = [.. other.FoldedIn, new FoldedIssue { Number = flight.Issue, From = flight.Code }] };
            state.PutFlight(other);
        }

        // One planner a flight: one planning the other already is stopped, and the fresh one is briefed with the folded issue.
        await StopPlannerAsync(other, cancellationToken);
        StartPlanner(other, replacing: true);
        return $"{flight.Code} is folded into {other.Code}: its issue #{flight.Issue} is {other.Code}'s now, and a planner is started on {other.Code} to plan its points";
    }

    /// <summary>Refuses what plans the flight's legs while a question about an overlap waits for its answer.</summary>
    private void HoldForOverlap(FlightRow flight)
    {
        if (flight.Overlaps.FirstOrDefault(o => o.Answer == OverlapAnswer.Pending) is { } waiting)
        {
            throw new FlightException($"{flight.Code} overlaps {waiting.Other} and waits for the controller's answer{CardSaid(flight)}: plan no leg until a fresh planner is started with it; end your turn");
        }
    }

    private string CardSaid(FlightRow flight) =>
        state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == OverlapKind && c.Subject == Verbs.FlightTarget(flight.Code)) is { } card
            ? $" (card {card.Id})"
            : "";
}
