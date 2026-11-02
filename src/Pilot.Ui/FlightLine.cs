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
            Code = flight.Code,
            Title = flight.Title ?? flight.Item.Noun,
            Where = $"{flight.Repo} {flight.Item.Display}",
            State = flight.State switch { FlightState.Flying => "flying", FlightState.Held => "held", _ => flight.MovedTo is null ? "landed" : "moved" },
            StateDetail = flight.State switch
            {
                FlightState.Held => $"waiting for {FlightWaiting.For(flight)}",
                FlightState.Landed when flight.MovedTo is { } to => to.Text,
                FlightState.Landed when flight.LandedAt is { } at => SessionLine.Ago(now - at),
                // Merged through a queue is not landed yet: it waits for the queue's checks.
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
