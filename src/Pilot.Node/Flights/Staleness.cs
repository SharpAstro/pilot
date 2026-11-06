using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>The five bounds past which work counts as stale (#736); each a node setting with a default (<see cref="NodeSettings.StaleKeys"/>).</summary>
public sealed record StaleBounds
{
    /// <summary>Hours a flight may stay open.</summary>
    public int FlightHours { get; init; } = NodeSettings.DefaultStaleFlightHours;

    /// <summary>Hours a flight's PR may sit open with green CI.</summary>
    public int PrHours { get; init; } = NodeSettings.DefaultStalePrHours;

    /// <summary>Hours a leg may sit rechecking.</summary>
    public int RecheckHours { get; init; } = NodeSettings.DefaultStaleRecheckHours;

    /// <summary>Hours a ringing card may stay open.</summary>
    public int CardHours { get; init; } = NodeSettings.DefaultStaleCardHours;

    /// <summary>How many holding-timeout cards a flight may have raised before its hold counts as repeated.</summary>
    public int Holds { get; init; } = NodeSettings.DefaultStaleHolds;

    /// <summary>Hours a flight may be held on a flight of a node that cannot be asked (#753).</summary>
    public int UnreachableHours { get; init; } = NodeSettings.DefaultStaleUnreachableHours;

    /// <summary>The value of one of <see cref="NodeSettings.StaleKeys"/>; 0 for another key.</summary>
    public int Value(string key) => key switch
    {
        NodeSettings.StaleFlightHoursKey => FlightHours,
        NodeSettings.StalePrHoursKey => PrHours,
        NodeSettings.StaleRecheckHoursKey => RecheckHours,
        NodeSettings.StaleCardHoursKey => CardHours,
        NodeSettings.StaleHoldsKey => Holds,
        NodeSettings.StaleUnreachableHoursKey => UnreachableHours,
        _ => 0,
    };
}

/// <summary>What kind of work a <see cref="StaleItem"/> is.</summary>
public enum StaleKind
{
    Flight,
    PullRequest,
    Leg,
    Card,
    Hold,

    /// <summary>A flight held on a flight of a node that cannot be asked (#753).</summary>
    Unreachable,
}

/// <summary>One stale (or nearing) thing: what it is, how long, what it waits on now, the one action that moves it, and what happened since it last moved.</summary>
public sealed record StaleItem
{
    public required StaleKind Kind { get; init; }

    /// <summary>What it is about: a flight code, <c>PIL736 PR #12</c>, <c>PIL736/leg</c>, or a card id.</summary>
    public required string Subject { get; init; }

    /// <summary>The flight it belongs to; null for a card about nothing in a flight.</summary>
    public string? Flight { get; init; }

    public required TimeSpan Age { get; init; }

    /// <summary>The age in words: <c>19 h</c>, <c>2 d 4 h</c>.</summary>
    public required string AgeText { get; init; }

    /// <summary>What it waits on now, in a line (<c>job j3178, 2nd in the queue</c>).</summary>
    public required string WaitsOn { get; init; }

    /// <summary>The one action that moves it.</summary>
    public required string Action { get; init; }

    /// <summary>What happened since it last moved, oldest first, each with its local time.</summary>
    public required IReadOnlyList<string> Timeline { get; init; }

    /// <summary>Why it is stale, in one line.</summary>
    public required string Cause { get; init; }

    /// <summary>The timeline and the cause as one text.</summary>
    public string Summary => Timeline.Count == 0 ? Cause : $"{string.Join("; ", Timeline)}. {Cause}";

    /// <summary>The bound it passes, or is half way to, in words (<c>12 h</c>, <c>2 holds</c>).</summary>
    public required string Bound { get; init; }

    /// <summary>How long its flight has not moved (now minus its last move); null for an item that is not about a flight's progress (a card).</summary>
    public TimeSpan? Idle { get; init; }

    /// <summary>The line the digest and the brief use.</summary>
    public string Line => $"{Subject} {Kind.ToString().ToLowerInvariant()} {AgeText} (bound {Bound}): waits on {WaitsOn}. Next: {Action}.";
}

/// <summary>What <see cref="Staleness.Read"/> found: what is past a bound (oldest first) and what is past half of one.</summary>
public sealed record StalenessReading(IReadOnlyList<StaleItem> Stale, IReadOnlyList<StaleItem> Nearing);

/// <summary>
/// A pure reading of what has gone stale (#736): no I/O, the time passed in. A flight open too long, a PR with green CI open too long, a
/// leg rechecking too long, a card ringing too long, a hold that passed its timeout more than once.
/// </summary>
public static class Staleness
{
    public static StalenessReading Read(
        IReadOnlyList<FlightRow> flights, IReadOnlyList<LegRow> legs, IReadOnlyList<CardRow> cards, IReadOnlyList<JobRow> jobs,
        PullRequestsRow? pullRequests, DateTimeOffset now, StaleBounds bounds, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var stale = new List<StaleItem>();
        var nearing = new List<StaleItem>();

        void Add(StaleItem item, TimeSpan bound)
        {
            if (item.Age >= bound)
            {
                stale.Add(item);
            }
            else if (item.Age >= bound / 2)
            {
                nearing.Add(item);
            }
        }

        foreach (var flight in flights.Where(f => f is { State: not FlightState.Landed, FoldedInto: null, MovedTo: null }))
        {
            var mine = legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order).ToList();
            var flightCards = cards.Where(c => c.Subject == Verbs.FlightTarget(flight.Code) || c.Subject.StartsWith(Verbs.FlightTarget(flight.Code) + "/", StringComparison.Ordinal)).ToList();
            var (events, last) = Events(flight, mine, flightCards, pullRequests, zone);
            var lastMove = last.At;
            var idle = now - lastMove;
            string[] Timeline() => [.. events.Where(e => e.At >= lastMove).OrderBy(e => e.At).Select(e => $"{Local(e.At, zone)} {e.Text}")];

            // The flight open too long.
            var openFor = now - flight.CreatedAt;
            var (waits, action) = Waits(flight, mine, flightCards, jobs, pullRequests);
            Add(new StaleItem
            {
                Kind = StaleKind.Flight, Subject = flight.Code, Flight = flight.Code, Age = openFor, AgeText = Say(openFor), WaitsOn = waits, Action = action,
                Timeline = Timeline(), Cause = $"open {Say(openFor)}, waiting on {waits}", Bound = Hours(bounds.FlightHours), Idle = idle,
            }, TimeSpan.FromHours(bounds.FlightHours));

            // Its PR open with green CI.
            if (flight.PullRequest is { } number && PullRequestFor(flight, pullRequests) is var pr && IsGreen(pr) && OpenSince(flight, pr, flightCards) is { } opened)
            {
                var prAge = now - opened;
                Add(new StaleItem
                {
                    Kind = StaleKind.PullRequest, Subject = $"{flight.Code} PR #{number}", Flight = flight.Code, Age = prAge, AgeText = Say(prAge), WaitsOn = waits, Action = action,
                    Timeline = Timeline(), Cause = $"PR #{number} has been open {Say(prAge)} with green CI, waiting on {waits}", Bound = Hours(bounds.PrHours), Idle = idle,
                }, TimeSpan.FromHours(bounds.PrHours));
            }

            // A repeated hold: holding-timeout cards raised for the flight since its current hold began. A hold that has ended
            // is no longer stale, however often it timed out before.
            var timeouts = flight.Holding is { } holding
                ? flightCards.Where(c => c.Kind == LegCommander.HoldingTimeoutKind && c.At >= holding.Since).OrderBy(c => c.At).ToList()
                : [];
            if (timeouts.Count > 0 && bounds.Holds > 0)
            {
                var since = flight.Holding!.Since;
                var holdAge = now - since;
                var half = (bounds.Holds + 1) / 2;
                var item = new StaleItem
                {
                    Kind = StaleKind.Hold, Subject = flight.Code, Flight = flight.Code, Age = holdAge, AgeText = Say(holdAge), WaitsOn = waits, Action = action,
                    Timeline = Timeline(),
                    Cause = $"its hold passed the timeout {timeouts.Count} {(timeouts.Count == 1 ? "time" : "times")}, waiting on {waits}",
                    Bound = bounds.Holds == 1 ? "1 hold" : $"{bounds.Holds} holds",
                    Idle = idle,
                };
                if (timeouts.Count >= bounds.Holds)
                {
                    stale.Add(item);
                }
                else if (timeouts.Count >= half)
                {
                    nearing.Add(item);
                }
            }

            // A hold on a flight of a node that has not answered for more than the bound (#753): it names the node and says how long,
            // and the way out is the disconnect.
            foreach (var peer in flight.PeerConnections.Where(c => c.UnreachableSince is not null).OrderBy(c => c.UnreachableSince))
            {
                var off = now - peer.UnreachableSince!.Value;
                var disconnect = $"disconnect {flight.Code} from {peer.Code} (pilot flight disconnect {flight.Code} {peer.Code}), or bring {peer.Node} back onto the LAN";
                Add(new StaleItem
                {
                    Kind = StaleKind.Unreachable, Subject = flight.Code, Flight = flight.Code, Age = off, AgeText = Say(off),
                    WaitsOn = $"{peer.Code} on {peer.Node}, which has not been on the LAN since {Local(peer.UnreachableSince.Value, zone)}", Action = disconnect,
                    Timeline = Timeline(), Cause = $"{peer.Node} has been unreachable for {Say(off)}, so {flight.Code} is held on {peer.Code}", Idle = idle,
                    Bound = Hours(bounds.UnreachableHours),
                }, TimeSpan.FromHours(bounds.UnreachableHours));
                break;
            }

            // A leg past the go-around or flying limits (#752): the leg-stale card's table of check runs is its cause, so the digest
            // and the brief show why without the job logs. Read from the card, or built without logs while no card is raised yet.
            var goneStale = new HashSet<string>(StringComparer.Ordinal);
            foreach (var leg in mine.Where(l => l.State == LegState.Flying))
            {
                var checks = jobs.Where(j => j.Kind == JobKind.Check
                    && string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (StaleLegs.Why(leg, checks, now) is not { } why)
                {
                    continue;
                }
                goneStale.Add(leg.Name);
                var subject = $"{flight.Code}/{leg.Name}";
                var card = cards.FirstOrDefault(c => c.Kind == LegWatch.LegStale && c.Subject == $"{Verbs.FlightTarget(flight.Code)}/{leg.Name}");
                var table = card?.Detail ?? (checks.Length == 0 ? "no check has run for this leg yet" : StaleLegs.Table(checks, null, leg.Check ?? "", readLogs: false));
                var first = checks.Select(j => (DateTimeOffset?)j.StartedAt).Append(leg.StartedAt).Min() ?? flight.CreatedAt;
                var legAge = now - first;
                stale.Add(new StaleItem
                {
                    Kind = StaleKind.Leg, Subject = subject, Flight = flight.Code, Age = legAge, AgeText = Say(legAge), WaitsOn = waits, Action = action,
                    Timeline = Timeline(), Cause = $"{subject} is stale: {why}{(card is null ? "" : $" (card {card.Id})")}. Its check runs:\n{table}",
                    Bound = $"{StaleLegs.MostGoArounds.ToString(CultureInfo.InvariantCulture)} go-arounds or {Say(StaleLegs.MostFlying)}",
                });
            }

            // A leg rechecking too long (a leg already stale above is listed once, with its check runs).
            foreach (var leg in mine.Where(l => l is { State: LegState.Flying, Recheck: { Outcome: null } } && !goneStale.Contains(l.Name)))
            {
                var age = now - leg.Recheck!.Since;
                var (legWaits, legAction) = RecheckWaits(flight, leg, jobs);
                Add(new StaleItem
                {
                    Kind = StaleKind.Leg, Subject = $"{flight.Code}/{leg.Name}", Flight = flight.Code, Age = age, AgeText = Say(age), WaitsOn = legWaits, Action = legAction,
                    Timeline = Timeline(), Cause = $"rechecking `{leg.Recheck.Check}` for {Say(age)}, waiting on {legWaits}", Bound = Hours(bounds.RecheckHours), Idle = idle,
                }, TimeSpan.FromHours(bounds.RecheckHours));
            }
        }

        // A card ringing too long.
        foreach (var card in cards.Where(c => c is { Bin: CardBin.Ring, State: CardState.Open }))
        {
            var age = now - card.At;
            var choice = card.Choices.FirstOrDefault()?.Label;
            Add(new StaleItem
            {
                Kind = StaleKind.Card, Subject = card.Id, Flight = FlightOf(card.Subject), Age = age, AgeText = Say(age),
                WaitsOn = $"you: card {card.Id} rings, {card.Title}",
                Action = choice is null ? $"answer or dismiss card {card.Id} (pilot cards)" : $"press \"{choice}\" on card {card.Id}, or dismiss it",
                Timeline = [$"{Local(card.At, zone)} card {card.Id} raised: {card.Title}"],
                Cause = $"card {card.Id} ({card.Kind}) has rung for {Say(age)} with no answer", Bound = Hours(bounds.CardHours),
            }, TimeSpan.FromHours(bounds.CardHours));
        }

        return new StalenessReading([.. stale.OrderByDescending(i => i.Age)], [.. nearing.OrderByDescending(i => i.Age)]);
    }

    /// <summary>An age in words: <c>35 m</c>, <c>19 h</c>, <c>2 d 4 h</c>.</summary>
    public static string Say(TimeSpan span) => Waited.Coarse(span);

    private static string Hours(int hours) => $"{hours.ToString(CultureInfo.InvariantCulture)} h";

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? FlightOf(string subject) => subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal)
        ? subject[Verbs.FlightTargetPrefix.Length..].Split('/')[0] : null;

    private static PullRequestRow? PullRequestFor(FlightRow flight, PullRequestsRow? read) =>
        read?.PullRequests.FirstOrDefault(p => p.Flight == flight.Code || (p.Number == flight.PullRequest && p.Repo == flight.Repo));

    private static bool IsGreen(PullRequestRow? pr) => pr is not null
        && (pr.Kind is PullRequestKind.Ready or PullRequestKind.Armed or PullRequestKind.InMergeQueue
            || (pr.ChecksTotal > 0 && pr.ChecksDone >= pr.ChecksTotal && pr.FailedCheck is null && pr.Kind is not (PullRequestKind.ChecksFailed or PullRequestKind.Conflicts)));

    private static DateTimeOffset? OpenSince(FlightRow flight, PullRequestRow? pr, List<CardRow> cards) =>
        pr?.CreatedAt ?? cards.Where(c => c.Kind == LegCommander.PrReady).Select(c => (DateTimeOffset?)c.At).Min();

    // Who closes a card that no one answered: the node (a sweep, a replacement) and GitHub (the PR was merged or closed there).
    private const string ClosedByNode = "node";
    private const string ClosedByGitHub = "github";

    private sealed record Event(DateTimeOffset At, string Text);

    /// <summary>
    /// A flight's last real activity: when it happened and its words (<c>review passed</c>, <c>leg parser finished</c>). It is the same last
    /// move the stale items measure from, so a row and a stale line never disagree. Null for a flight that landed, folded or moved.
    /// </summary>
    public static (DateTimeOffset At, string Text)? LastMove(
        FlightRow flight, IReadOnlyList<LegRow> legs, IReadOnlyList<CardRow> cards, PullRequestsRow? pullRequests, TimeZoneInfo? zone = null)
    {
        if (flight is not { State: not FlightState.Landed, FoldedInto: null, MovedTo: null })
        {
            return null;
        }
        var target = Verbs.FlightTarget(flight.Code);
        var mine = legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order).ToList();
        var flightCards = cards.Where(c => c.Subject == target || c.Subject.StartsWith(target + "/", StringComparison.Ordinal)).ToList();
        var (_, last) = Events(flight, mine, flightCards, pullRequests, zone ?? TimeZoneInfo.Local);
        return (last.At, last.Text);
    }

    // The flight's happenings, and the last one that moved it (a leg start or finish, a card answered, a review verdict, the PR opening, its
    // creation). A push and a check finishing are deliberately not moves here:
    //  - no row holds the time of a push (the PR's UpdatedAt also moves on a comment, a label or a bot, so it would hide a still flight);
    //  - a check finishing says nothing about progress: a leg whose check fails again and again finishes a check every time, and that
    //    loop is the stale case (#752), not a flight that moves. A check that passes ends its leg, and the leg's finish is the move;
    //  - a leg's recheck finishing is the same (a recheck keeps only when it began).
    private static (List<Event> Events, Event LastMove) Events(FlightRow flight, List<LegRow> legs, List<CardRow> cards, PullRequestsRow? pullRequests, TimeZoneInfo zone)
    {
        var created = new Event(flight.CreatedAt, "flight created");
        var events = new List<Event> { created };
        var moves = new List<Event> { created };
        void Move(DateTimeOffset at, string text)
        {
            var e = new Event(at, text);
            events.Add(e);
            moves.Add(e);
        }
        foreach (var leg in legs)
        {
            if (leg.StartedAt is { } started)
            {
                Move(started, $"leg {leg.Name} started");
            }
            if (leg.FinishedAt is { } finished)
            {
                Move(finished, $"leg {leg.Name} {leg.State.ToString().ToLowerInvariant()}");
            }
            if (leg.Recheck is { } recheck)
            {
                events.Add(new Event(recheck.Since, $"leg {leg.Name} began rechecking `{recheck.Check}`"));
            }
        }
        if (PullRequestFor(flight, pullRequests)?.CreatedAt is { } opened)
        {
            Move(opened, $"PR #{flight.PullRequest} opened");
        }
        if (flight.Review is { } review)
        {
            Move(review.FinishedAt ?? review.StartedAt, review.FinishedAt is null ? "review started" : $"review {(review.State == ReviewState.Passed ? "passed" : "asked for changes")}");
        }
        if (flight.Holding is { } holding)
        {
            events.Add(new Event(holding.Since, $"hold began: {holding.Reason}"));
        }
        // A card a person or a session answered counts as a move; one the node closed on its own, one GitHub closed (a PR merged or
        // closed there: nobody answered it), or a handled record, does not.
        foreach (var card in cards.Where(c => c is { State: CardState.Closed, ClosedAt: not null } && c.Bin != CardBin.Handled && c.ClosedBy is not (ClosedByNode or ClosedByGitHub)))
        {
            Move(card.ClosedAt!.Value, $"card {card.Id} answered");
        }
        foreach (var card in cards.Where(c => c.Kind == LegCommander.HoldingTimeoutKind))
        {
            events.Add(new Event(card.At, $"holding-timeout card {card.Id} raised, the hold passed its timeout"));
        }
        foreach (var card in cards.Where(c => c.Bin == CardBin.Ring && c.State == CardState.Open && c.Kind != LegCommander.HoldingTimeoutKind))
        {
            events.Add(new Event(card.At, $"card {card.Id} rang: {card.Title}"));
        }
        // The latest move; of equal times the one added last (a verdict after the leg that asked for it).
        return (events, moves.Select((e, i) => (e, i)).OrderBy(x => x.e.At).ThenBy(x => x.i).Last().e);
    }

    // What a flight waits on now, and the one action that moves it, in the order of what is nearest to hand.
    private static (string Waits, string Action) Waits(FlightRow flight, List<LegRow> legs, List<CardRow> cards, IReadOnlyList<JobRow> jobs, PullRequestsRow? pullRequests)
    {
        if (legs.FirstOrDefault(l => l is { State: LegState.Flying, Recheck: { Outcome: null } }) is { } rechecking)
        {
            return RecheckWaits(flight, rechecking, jobs);
        }
        if (flight.Holding is { } holding)
        {
            var card = cards.FirstOrDefault(c => c.Kind == LegCommander.HoldingTimeoutKind && c.State == CardState.Open);
            return ($"a hold ({holding.Reason}), since {holding.Since.ToUniversalTime():HH:mm} UTC",
                card is null ? $"clear the hold (pilot flight clear {flight.Code}) or merge it as it is" : $"press \"Merge as it is\" on card {card.Id}");
        }
        if (flight.Review is { State: ReviewState.Reviewing } reviewing)
        {
            return ($"a review round (reviewer {reviewing.Reviewer})", $"wait for {reviewing.Reviewer}, or look at its session (pilot sessions)");
        }
        if (flight.State == FlightState.Held)
        {
            var waiting = FlightWaiting.For(flight);
            return ($"a connected flight ({(waiting.Length > 0 ? waiting : "none named")})", "land the flight it connects from, or disconnect it (pilot flight disconnect)");
        }
        if (cards.FirstOrDefault(c => c is { Bin: CardBin.Ring, State: CardState.Open }) is { } ringing)
        {
            return ($"a ringing card ({ringing.Id}: {ringing.Title})", $"answer card {ringing.Id}");
        }
        if (legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying)
        {
            return ($"leg {flying.Name} flying", $"ask its worker how it goes (pilot say {flight.Code}/{flying.Name})");
        }
        if (legs.FirstOrDefault(l => l.State == LegState.Planned) is { } planned)
        {
            return ($"leg {planned.Name} {(planned.Queued is { } queued ? $"queued: {queued}" : "to start")}", "start the next leg (pilot leg start), or see why it is queued");
        }
        if (flight.PullRequest is { } pr)
        {
            return ($"PR #{pr} to be merged", $"merge PR #{pr}");
        }
        return ("nothing named", $"look at the flight (pilot status {flight.Code})");
    }

    // A rechecking leg waits on the job that reruns its check: its place in the queue when it is queued.
    private static (string Waits, string Action) RecheckWaits(FlightRow flight, LegRow leg, IReadOnlyList<JobRow> jobs)
    {
        var mine = jobs.Where(j => j.Flight == flight.Code && (j.Leg is null || j.Leg == leg.Name) && j.State is JobState.Queued or JobState.Running)
            .OrderBy(j => j.State == JobState.Running ? 0 : 1).FirstOrDefault();
        if (mine is { State: JobState.Queued } queued)
        {
            var line = jobs.Where(j => j.State == JobState.Queued).OrderBy(j => j.QueuedAt ?? j.StartedAt).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
            var place = line.FindIndex(j => j.Id == queued.Id) + 1;
            return ($"job {queued.Id}, {Ordinal(place)} in the queue", $"wait for the queue ahead of job {queued.Id} (pilot jobs), or cancel a job ahead of it");
        }
        if (mine is { State: JobState.Running } running)
        {
            return ($"job {running.Id}, running", $"wait for job {running.Id} to end (pilot job {running.Id})");
        }
        return ("its recheck job, which the node does not list", $"finish the leg again (pilot leg finish done) or go around {flight.Code}/{leg.Name}");
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };
}

/// <summary>The 'Gone stale' section of the digest, the watchtower brief and the summary's records (#736 point 1).</summary>
public static class StaleSection
{
    public const string Heading = "Gone stale";

    /// <summary>What the node holds, read against <paramref name="bounds"/>.</summary>
    public static IReadOnlyList<StaleItem> Read(NodeState state, DateTimeOffset now, StaleBounds bounds, TimeZoneInfo? zone = null) =>
        Staleness.Read(state.Flights(), state.Legs(), state.Cards(), state.Jobs(), state.PullRequests(), now, bounds, zone).Stale;

    /// <summary>One item in a line: its age, what it waits on, the action that moves it and the quoted summary.</summary>
    public static string Line(StaleItem item, IReadOnlyList<FlightRow>? flights = null)
    {
        var word = item.Kind switch
        {
            StaleKind.Leg => "rechecking",
            StaleKind.Card => "ringing",
            StaleKind.Hold => "holding",
            StaleKind.Unreachable => "held on an unreachable node",
            _ => "open",
        };
        var summary = item.Summary.ReplaceLineEndings(" ").Replace('"', '\'');
        var raise = flights?.FirstOrDefault(f => f.Code == item.Flight)?.StaleRaises?.LastOrDefault(r => r.Bound == StaleWatch.BoundKey(item.Kind));
        var raised = raise is null ? "" : $" ({raise.Note})";
        var still = item.Idle is { } idle ? $" no progress for {Staleness.Say(idle)}," : "";
        return $"- {item.Subject}, {word} {item.AgeText}{raised}:{still} waiting on {item.WaitsOn}. Next: {item.Action}. \"{summary}\"";
    }

    /// <summary>The heading and a line per item, oldest first, as lines; empty when nothing is stale.</summary>
    public static IEnumerable<string> Lines(IReadOnlyList<StaleItem> items, IReadOnlyList<FlightRow>? flights = null)
    {
        if (items.Count == 0)
        {
            yield break;
        }
        yield return $"{Heading}:";
        foreach (var item in items)
        {
            yield return Line(item, flights);
        }
    }
}
