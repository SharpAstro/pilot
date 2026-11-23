using Pilot.Contracts;

namespace Pilot.Gui.Tests;

/// <summary>A snapshot with a flight in each phase, long leg names included: the map tests' and the captures' one world.</summary>
internal static class FlightMapWorld
{
    internal static FlightRow Flight(string code, FlightState state = FlightState.Flying, string? queued = null, FlightOnHold? hold = null,
        ReviewRow? review = null, bool pr = true) => new()
    {
        Code = code,
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1,
        Title = $"Flight {code}",
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(),
        Base = "origin/main",
        State = state,
        Queued = queued,
        OnHold = hold,
        Review = review,
        Connections = state == FlightState.Held ? ["PIL0"] : [],
        PullRequest = pr ? 7 : null,
        PullRequestUrl = pr ? "https://github.com/x/y/pull/7" : null,
        CreatedAt = Live.At,
    };

    internal static LegRow Leg(string flight, LegState state, int order = 1, string? queued = null, string? name = null) => new()
    {
        Flight = flight, Name = name ?? $"node-merge-queue-leg{order}", Order = order, Goal = "g", DoneWhen = "d", State = state, Queued = queued,
    };

    internal static ReviewRow Review(ReviewState state, string? over = null) => new() { State = state, Reviewer = "r", StartedAt = Live.At, LabelOverride = over };

    internal const string Why = "waiting for DIR.Lib flight PIL1234 to land";

    /// <summary>The queue reason of a flight with no legs yet, queued for its planner (the DIR316 case of pilot #1298).</summary>
    internal const string PlannerWhy = "waiting for a planner slot (2 of 2 busy)";

    /// <summary>The queue reason of a flight whose legs are all done, queued for its review.</summary>
    internal const string ReviewWhy = "waiting for a review slot";

    internal static (FlightRow[] Flights, LegRow[] Legs) World()
    {
        var landed = Flight("LND", FlightState.Landed) with { LandedAt = Live.At };
        return
        ([
            landed,
            Flight("HLD", hold: new FlightOnHold { Reason = "wait", Since = Live.At, By = "seb" }),
            Flight("HLT", FlightState.Held),
            Flight("QRW", queued: Why, pr: false),
            Flight("FLY", pr: false),
            Flight("FAL"),
            Flight("CNF", review: Review(ReviewState.Passed, "pilot: conflicts")),
            Flight("CHG", review: Review(ReviewState.Changes)),
            Flight("BTW", pr: false),
            Flight("QNL", queued: PlannerWhy, pr: false),
            Flight("QDN", queued: ReviewWhy),
            // In conflict, its fix leg waiting for a slot: Conflicts wins over the queue, so it counts as flying and is red.
            Flight("CNQ", review: Review(ReviewState.Passed, "pilot: conflicts")),
        ],
        [
            Leg("LND", LegState.Done),
            Leg("HLD", LegState.Planned), Leg("HLT", LegState.Planned),
            Leg("QRW", LegState.Planned), Leg("QRW", LegState.Planned, 2),
            Leg("FLY", LegState.Done), Leg("FLY", LegState.Flying, 2), Leg("FLY", LegState.Planned, 3),
            Leg("FAL", LegState.Done), Leg("FAL", LegState.Failed, 2),
            Leg("CNF", LegState.Done),
            Leg("CHG", LegState.Done), Leg("CHG", LegState.Flying, 2),
            Leg("BTW", LegState.Done), Leg("BTW", LegState.Planned, 2),
            Leg("QDN", LegState.Done), Leg("QDN", LegState.Done, 2),
            Leg("CNQ", LegState.Done), Leg("CNQ", LegState.Planned, 2, queued: "waiting for an Opus slot", name: "conflict-fix"),
        ]);
    }

    internal static FlightLine[] Lines(FlightRow[] flights, LegRow[] legs) => [.. FlightLine.Build(flights, [], Live.At, legs, null)];
}
