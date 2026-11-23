using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// One flight state model (pilot #1298): <see cref="FlightPhases.Of"/> tells each phase apart, and the counts, the Flights header and
/// <c>pilot status</c> all read it, so a queued flight is counted as queued and never as flying.
/// </summary>
public sealed class FlightPhaseTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(1);

    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, string? queued = null, FlightOnHold? hold = null, ReviewRow? review = null) => new()
    {
        Code = code, Repo = "pilot", RepoPath = "/r/pilot", Issue = 1, Title = $"Flight {code}", Branch = $"flight/{code}",
        Worktree = $"/w/{code}", Base = "origin/main", State = state, CreatedAt = Live.At, Queued = queued, OnHold = hold, Review = review,
        Connections = state == FlightState.Held ? ["PIL0"] : [],
    };

    private static LegRow Leg(string flight, LegState state, int order = 1, string? queued = null) => new()
    {
        Flight = flight, Name = $"leg{order}", Order = order, Goal = "g", DoneWhen = "d", State = state, Queued = queued,
    };

    private static ReviewRow Review(ReviewState state, string? over = null) => new()
    {
        State = state, Reviewer = "r", StartedAt = Live.At, LabelOverride = over,
    };

    private static (FlightRow[] Flights, LegRow[] Legs) World() =>
    ([
        Flight("LND", FlightState.Landed),
        Flight("HLD", hold: new FlightOnHold { Reason = "wait", Since = Live.At, By = "seb" }),
        Flight("HLT", FlightState.Held),
        Flight("QRW", queued: "waiting for an Opus slot"),
        Flight("QLG"),
        Flight("FLY"),
        Flight("FAL"),
        Flight("CNF", review: Review(ReviewState.Passed, "pilot: conflicts")),
        Flight("CHG", review: Review(ReviewState.Changes)),
        Flight("BTW"),
    ],
    [
        Leg("QLG", LegState.Planned, queued: "waiting for an Opus slot"),
        Leg("FLY", LegState.Flying),
        Leg("FAL", LegState.Failed),
        Leg("CHG", LegState.Done), Leg("CHG", LegState.Flying, 2),
        Leg("BTW", LegState.Done), Leg("BTW", LegState.Planned, 2),
    ]);

    [Theory]
    [InlineData("LND", FlightPhase.Landed)]
    [InlineData("HLD", FlightPhase.OnHold)]
    [InlineData("HLT", FlightPhase.Held)]
    [InlineData("QRW", FlightPhase.Queued)]
    [InlineData("QLG", FlightPhase.Queued)]
    [InlineData("FLY", FlightPhase.Flying)]
    [InlineData("FAL", FlightPhase.Failed)]
    [InlineData("CNF", FlightPhase.Conflicts)]
    [InlineData("CHG", FlightPhase.MakingChanges)]
    [InlineData("BTW", FlightPhase.BetweenLegs)]
    public void EachFlightHasItsOwnPhase(string code, FlightPhase expected)
    {
        var (flights, legs) = World();
        FlightPhases.Of(flights.Single(f => f.Code == code), legs).ShouldBe(expected);
    }

    [Fact]
    public void ChangesWithAPlannedFixLegAndNoneFlyingIsMakingChanges()
    {
        FlightPhases.Of(Flight("X", review: Review(ReviewState.Changes)), [Leg("X", LegState.Planned)]).ShouldBe(FlightPhase.MakingChanges);
    }

    [Fact]
    public void ALegFlyingMakesAQueuedRowFlying()
    {
        FlightPhases.Of(Flight("X", queued: "why"), [Leg("X", LegState.Flying)]).ShouldBe(FlightPhase.Flying);
    }

    [Fact]
    public void CountsHeaderAndStatusAgreeAndQueuedIsNotFlying()
    {
        var (flights, legs) = World();
        var counts = FlightCounts.Of(flights, legs);
        // Flying: FLY, FAL, CNF, CHG, BTW. Queued: QRW, QLG.
        counts.ShouldBe(new FlightCounts(5, 1, 1, 2));

        // Without legs only the row's own queue shows: QRW.
        FlightCounts.Of(flights).Queued.ShouldBe(1);

        var snapshot = new SnapshotEvent { Seq = 1, At = Now, Stream = "s", Sessions = [], Notes = [], Flights = flights, Legs = legs };
        var screen = new FlightsScreen(FlightLine.Build(flights, [], Now, legs, null), "", Now);
        screen.Counts.ShouldContain("5 flying · 2 queued · 1 held · 1 on hold");

        var status = PilotCommand.Status(snapshot);
        status.ShouldContain("queued: waiting for an Opus slot");
        status.Split('\n').Count(l => l.Contains(", flying")).ShouldBe(counts.Flying);
        status.Split('\n').Count(l => l.Contains(", queued: ")).ShouldBe(counts.Queued);

        StatusMarkdown.Build(snapshot).ShouldContain("queued: waiting for an Opus slot");
    }
}
