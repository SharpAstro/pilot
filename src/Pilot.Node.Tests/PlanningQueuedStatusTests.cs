using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1190: a flight whose planner waits for admission is said to be planning queued behind whom since when; a flight queued for a leg keeps "queued: why".</summary>
public class PlanningQueuedStatusTests
{
    private static readonly DateTimeOffset Since = new(2026, 11, 28, 9, 1, 0, TimeSpan.Zero);

    private static FlightRow Flight(string? behind, string? queued = "waiting for the repo tianwen: TIA2507 holds it (a leg is flying); one flight per repo") => new()
    {
        Code = "TIA2508", Repo = "o/r", RepoPath = "r", Issue = 1, Branch = "flight/tia2508", Worktree = "w", Base = "main",
        State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch, Queued = queued, QueuedSince = Since, QueuedBehind = behind,
    };

    private static SnapshotEvent Snapshot(FlightRow flight, params LegRow[] legs) => new()
    {
        Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Cards = [], Sessions = [], Flights = [flight], Legs = legs,
    };

    private static string[] Texts(SnapshotEvent snapshot) =>
        [PilotCommand.Status(snapshot, zone: TimeZoneInfo.Utc), StatusMarkdown.Build(snapshot, zone: TimeZoneInfo.Utc)];

    [Fact]
    public void AFlightWithNoLegsWhosePlannerWaitsIsPlanningQueuedBehindWhoAndSince()
    {
        foreach (var text in Texts(Snapshot(Flight("TIA2507"))))
        {
            text.ShouldContain("planning queued behind TIA2507 since 09:01");
            text.ShouldNotContain("queued: waiting");
        }
    }

    [Fact]
    public void TheTimeIsShownInTheZoneGiven()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        PilotCommand.Status(Snapshot(Flight("TIA2507")), zone: zone).ShouldContain("planning queued behind TIA2507 since 11:01");
    }

    [Fact]
    public void AWaitThatNamesNoOneSaysItsReasonAfterTheTime()
    {
        foreach (var text in Texts(Snapshot(Flight(null, "waiting for memory: 0.5 GB free"))))
        {
            text.ShouldContain("planning queued since 09:01: waiting for memory: 0.5 GB free");
        }
    }

    [Fact]
    public void AFlightQueuedForALegKeepsQueuedWhy()
    {
        var leg = new LegRow { Flight = "TIA2508", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned, Queued = "waiting for an Opus slot: 1 of 1 flying: x" };
        foreach (var text in Texts(Snapshot(Flight(null, queued: null), leg)))
        {
            text.ShouldContain("queued: waiting for an Opus slot: 1 of 1 flying: x");
            text.ShouldNotContain("planning queued");
        }
    }
}
