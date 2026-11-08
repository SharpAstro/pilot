using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>One flight as the Flights page shows it, every field already in words, as <see cref="SessionLine"/> is.</summary>
internal sealed record FlightLine
{
    public required FlightRow Row { get; init; }

    /// <summary>Its code, <c>TIA1611</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Its title, or the issue when it has none.</summary>
    public required string Title { get; init; }

    /// <summary><c>tianwen #1611</c>.</summary>
    public required string Where { get; init; }

    /// <summary>flying, held, landed.</summary>
    public required string State { get; init; }

    /// <summary>What holds it (<c>waiting for PIL5</c>), or when it landed; null otherwise.</summary>
    public string? StateDetail { get; init; }

    /// <summary>Why it waits for admission (<c>waiting for an Opus slot: 3 of 3 flying</c>); null while it waits for nothing. Also the head of <see cref="StateDetail"/>, as <c>queued: ...</c>.</summary>
    public string? Queued { get; init; }

    /// <summary>The mark on a priority flight's row and page (<see cref="FlightMarks.PriorityMark"/>); null for any other flight.</summary>
    public string? PriorityMark { get; init; }

    /// <summary>The level of the issue's Priority field as a word (urgent, high, medium, low); null when unset or the repo has no field.</summary>
    public string? LevelMark { get; init; }

    /// <summary>What a row, card, page and queue line say: the level where there is one, else the priority mark.</summary>
    public string? ShownPriority => LevelMark ?? PriorityMark;

    /// <summary>The plane's branch.</summary>
    public required string Branch { get; init; }

    /// <summary>Its legs: <c>3 legs · 1 done · render flying</c>, or <c>no legs yet</c>.</summary>
    public string Legs { get; init; } = "no legs yet";

    /// <summary>Its legs themselves, in their order, for the selected flight's details.</summary>
    public IReadOnlyList<LegRow> LegRows { get; init; } = [];

    /// <summary>Its sessions: <c>2 sessions · 1 live</c>, or <c>no session</c>.</summary>
    public required string Sessions { get; init; }

    /// <summary>
    /// The sessions working on it now, or waiting on you for it (its planner, a leg's worker or validator, its reviewer), the
    /// one to watch first. An idle one is not: a planner long done and a validator between runs keep their process, and
    /// listed they read as three sessions at it (PIL208's page, 2026-10-25).
    /// </summary>
    public IReadOnlyList<SessionRow> Working { get; init; } = [];

    /// <summary>Every session of the flight the node still knows, whatever its state or role (the flight page links its legs' sessions from these).</summary>
    public IReadOnlyList<SessionRow> AllSessions { get; init; } = [];

    /// <summary>The node it flies on, when the page merges several; null on a page of one node, this window's.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>
    /// The name of the node this flight's row comes from while the window cannot reach that node (its last known row, which may
    /// be long out of date and cannot be commanded); null while the node answers. Set by <see cref="FleetPages.Merge"/> from the
    /// feed's own reachability, the state the Fleet page and the panel read (pilot #325).
    /// </summary>
    public string? Stale { get; init; }

    /// <summary>What the node says went stale about the flight (#736), the timeline and cause in a line; null while nothing is stale.</summary>
    /// A flight that landed, folded or moved is done, so a summary left on it is not shown (and no Ask the watchtower is offered).
    public string? StaleSummary =>
        Row is { State: not FlightState.Landed, FoldedInto: null, MovedTo: null, StaleSummary: { Length: > 0 } summary } ? summary : null;

    /// <summary>The node's last priority raise for a stale bound (<c>raised to high: open 19 h</c>), or why it could not be made; null before the first.</summary>
    public string? StaleRaise => Row.StaleRaises is { Length: > 0 } raises ? raises[^1].Note : null;

    /// <summary>
    /// The clock this line was built against. The age below is read from it, not from the snapshot: the page rebuilds its lines with
    /// each frame's clock, so an age ticks while the row does not change. Null for a line built without one.
    /// </summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>
    /// How long the flight has not really moved (<see cref="FlightRow.LastMovedAt"/> against <see cref="Now"/>); null when the node
    /// gave no last move (an older node) or the line has no clock. Never negative.
    /// </summary>
    public TimeSpan? Idle => Row.LastMovedAt is { } at && Now is { } now ? (now - at is { Ticks: >= 0 } span ? span : TimeSpan.Zero) : null;

    /// <summary>
    /// The short mark under a stale flight's code: <c>stale · no progress for 10 h · raised to high</c> (just <c>stale</c> when the node
    /// gave no last move). The age is <see cref="Waited.Coarse"/>, not <see cref="SessionLine.Ago"/> as the issue named: Ago says
    /// seconds, minutes and an "ago" (<c>10h 5m ago</c>), which reads wrongly in "no progress for ..." and ticks every second and minute,
    /// and it would drift from the number the node's digest and stale lines print for the same flight, which are <see cref="Waited.Coarse"/>
    /// too (whole hours under a day, no "ago"). One format, so they never disagree.
    /// </summary>
    public string? StaleWorkMark => StaleSummary is null ? null
        : string.Join(" · ", new[]
        {
            Idle is { } idle ? $"stale · no progress for {Waited.Coarse(idle)}" : "stale",
            Row.StaleRaises?.LastOrDefault(r => r.Level is not null) is { Level: { } level } ? $"raised to {IssuePriorities.Name(level).ToLowerInvariant()}" : null,
        }.OfType<string>());

    /// <summary>The last thing that moved the flight with its local time, <c>last: review passed 12:09</c>; null when the node gave none.</summary>
    public string? LastMoveText => Row is { LastMovedAt: { } at, LastMove: { Length: > 0 } move }
        ? $"last: {move} {at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}" : null;

    /// <summary>The whole text of the stale mark on hover: the mark, the last move with its time, and the stale timeline, one to a line.</summary>
    public string? StaleWorkTip => StaleWorkMark is { } mark
        ? string.Join('\n', new[] { mark, LastMoveText, StaleSummary }.OfType<string>()) : null;

    /// <summary>
    /// How long a flight in the air has been still, for its detail page only (a table row stays clean): <c>last activity 25 m ago</c>,
    /// and for a stale flight the mark's words with the last move, <c>no progress for 10 h · last: review passed 12:09</c>. Null for a
    /// landed, folded or moved flight, or one with no last move.
    /// </summary>
    public string? ActivityText => Row is { State: not FlightState.Landed, FoldedInto: null, MovedTo: null } && Idle is { } idle
        ? StaleSummary is null ? $"last activity {Waited.Coarse(idle)} ago"
            : string.Join(" · ", new[] { $"no progress for {Waited.Coarse(idle)}", LastMoveText }.OfType<string>())
        : null;

    /// <summary>What says the row is a stale copy, <c>stale (DE1 unreachable)</c>, in words so colour never carries it alone; null while not stale.</summary>
    public string? StaleMark => Stale is { } node ? $"stale ({node} unreachable)" : null;

    /// <summary>
    /// What says the last attempt to move the flight to another node failed, in words so the red it is drawn in never carries it
    /// alone (#376); null while none failed. The reason is <see cref="MoveFailure"/>. A stale row keeps its stale mark and no red: it
    /// is the last row the node told, and says nothing of what it is doing now.
    /// </summary>
    public string? MoveMark => Row.MoveFailure is { Length: > 0 } && Stale is null ? "move failed" : null;

    /// <summary>Why the move failed, as the node worded it (what happened, what to do); null when <see cref="MoveMark"/> is.</summary>
    public string? MoveFailure => MoveMark is null ? null : Row.MoveFailure;

    /// <summary>Where a flight another node handed over came from, <c>moved from SE1 after leg parser</c>; null for a flight made here.</summary>
    public string? Provenance => Row.MovedFrom?.Text;

    /// <summary>
    /// The state's detail as a row's State column shows it (its stale mark is under its code): the move mark, then what holds the
    /// flight or when it landed, or where it came from when it has nothing else to say.
    /// </summary>
    public string? RowDetail => string.Join(" · ", new[] { MoveMark, StateDetail ?? Provenance }.OfType<string>()) is { Length: > 0 } detail ? detail : null;

    /// <summary>The state's detail as every page shows it: the stale mark first, then <see cref="RowDetail"/>.</summary>
    public string? ShownDetail => StaleMark is { } mark ? (RowDetail is null ? mark : $"{mark} · {RowDetail}") : RowDetail;

    /// <summary>What its sessions have spent so far, <c>1,234,567 tokens so far</c>; null when the node has no spend for it.</summary>
    public string? TokensText => Row.Tokens is > 0 and var tokens ? FlightTokens.Text(tokens) : null;

    /// <summary>The line as a dump writes it.</summary>
    public string ToDumpLine()
    {
        var line = string.Join(" | ", Code, Title, Where, ShownDetail is null ? State : $"{State} ({ShownDetail})", Legs, Branch, Sessions);
        return Node is { } node ? $"{node.Name} | {line}" : line;
    }

    /// <summary>The flights in drawing order: flying, then held, then landed, newest first within each.</summary>
    public static IReadOnlyList<FlightLine> Build(IEnumerable<FlightRow> flights, IEnumerable<SessionRow> sessions, DateTimeOffset now, IEnumerable<LegRow>? legs = null, AdmissionRow? admission = null)
    {
        var flightRows = flights as IReadOnlyCollection<FlightRow> ?? [.. flights];
        var legRows = legs as IReadOnlyCollection<LegRow> ?? [.. legs ?? []];
        var queued = QueuedFlight.Of(flightRows, legRows, admission).ToDictionary(q => q.Code, q => q.Why, StringComparer.Ordinal);
        legs = legRows;
        flights = flightRows;
        var byFlight = sessions.Where(s => s.Flight is not null).ToLookup(s => s.Flight!, StringComparer.Ordinal);
        var legsOf = (legs ?? []).ToLookup(l => l.Flight, StringComparer.Ordinal);
        return Order(flights.Select(f => (From(f, byFlight[f.Code].ToList(), now) with { Legs = LegsText([.. legsOf[f.Code].OrderBy(l => l.Order)]), LegRows = [.. legsOf[f.Code].OrderBy(l => l.Order)] }).Admitted(queued.GetValueOrDefault(f.Code), admission)));
    }

    // What admission says of the flight: why it queues (a flight that is flying and says nothing else), and the priority mark.
    private FlightLine Admitted(string? queued, AdmissionRow? admission) => this with
    {
        Queued = queued,
        StateDetail = StateDetail ?? (queued is null || Row.State != FlightState.Flying ? null : $"queued: {queued}"),
        PriorityMark = FlightMarks.IsPriority(Row, admission) ? FlightMarks.PriorityMark : null,
        LevelMark = FlightMarks.LevelMark(Row),
    };

    /// <summary>Lines in drawing order (<see cref="Build"/>'s), several nodes' merged.</summary>
    public static IReadOnlyList<FlightLine> Order(IEnumerable<FlightLine> lines) =>
        lines.OrderBy(l => l.Row.State switch { FlightState.Flying => 0, FlightState.Held => 1, _ => 2 })
            .ThenByDescending(l => l.Row.CreatedAt)
            .ToList();

    /// <summary>A flight's legs in a line: how many, how many done, and the one flying or failed.</summary>
    internal static string LegsText(IReadOnlyList<LegRow> legs)
    {
        if (legs.Count == 0)
        {
            return "no legs yet";
        }
        var text = $"{legs.Count} {(legs.Count == 1 ? "leg" : "legs")} · {legs.Count(l => l.State == LegState.Done)} done";
        return legs.FirstOrDefault(l => l.State == LegState.Failed) is { } failed ? $"{text} · {failed.Name} failed"
            : legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying ? $"{text} · {flying.Name} {(flying.Recheck is { Outcome: null } ? FlightWaiting.LegState(flying) : flying.Wait is { } wait ? FlightWaiting.WaitText(wait) : "flying")}{(flying.GoArounds > 0 ? $" (go-around {flying.GoArounds})" : "")}"
            : legs.FirstOrDefault(l => l.Hold is not null) is { } held ? $"{text} · {held.Name} {FlightWaiting.LegState(held)}"
            : text;
    }

    public static FlightLine From(FlightRow flight, IReadOnlyList<SessionRow> sessions, DateTimeOffset now)
    {
        var live = sessions.Count(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle);
        return new FlightLine
        {
            Row = flight,
            Now = now,
            Code = flight.Code,
            Title = flight.Title ?? flight.Item.Noun,
            Where = $"{flight.Repo} {flight.Item.Display}",
            // The owner's hold is the state a person sees (#771): the flight still flies as far as the row goes, but nothing is done for it.
            State = flight is { OnHold: not null, State: not FlightState.Landed } ? "on hold"
                : flight.State switch { FlightState.Flying => "flying", FlightState.Held => "held", _ => flight.MovedTo is null ? "landed" : "moved" },
            StateDetail = flight.State switch
            {
                // The owner's hold comes ahead of every other stage (#771).
                // The state says "on hold"; the detail says who and why, without saying it twice.
                not FlightState.Landed when flight.OnHold is { } onHold => $"by {onHold.By}: {onHold.Reason}",
                FlightState.Held => $"waiting for {FlightWaiting.For(flight)}",
                FlightState.Landed when flight.MovedTo is { } to => to.Text,
                FlightState.Landed when flight.LandedAt is { } at => SessionLine.Ago(now - at),
                // Merged through a queue is not landed yet: it waits for the queue's checks.
                FlightState.Flying when FlightWaiting.HoldingText(flight) is { } holding => holding,
                FlightState.Flying when flight.MergeQueue is { } queue => queue,
                _ => null,
            },
            Branch = flight.Branch,
            Sessions = sessions.Count == 0 ? "no session" : $"{sessions.Count} {(sessions.Count == 1 ? "session" : "sessions")} · {live} live",
            AllSessions = [.. sessions],
            Working = [.. sessions
                .Where(s => s.Role is SessionRole.Planner or SessionRole.Worker or SessionRole.Reviewer
                    && s.State is SessionState.Working or SessionState.Waiting)
                .OrderBy(s => s.State == SessionState.Waiting)
                .ThenByDescending(s => s.LastResponseAt)],
        };
    }
}

/// <summary>A flight's tokens so far in words, one wording for the status line, the Flights row and the Home line.</summary>
internal static class FlightTokens
{
    public static string Text(long tokens) => $"{tokens.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} tokens so far";
}
