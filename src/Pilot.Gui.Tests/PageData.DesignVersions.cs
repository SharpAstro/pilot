using Pilot.Contracts;
using Pilot.Ui;

namespace Pilot.Gui.Tests;

internal sealed partial class PageData
{
    /// <summary>
    /// A flight behind its designs' current versions (PIL1185): PIL1185 flies on the idea I381 and builds DSN-1 at v1 and DSN-3 at v2, while the sample's designs are both at v2.
    /// So DSN-1 is newer than the flight's (the notice and its Re-plan button), DSN-3 is the one it builds (no notice), and DSN-2 is built by no flight.
    /// </summary>
    internal static FlightRow BehindFlight(FlightState state = FlightState.Flying) => new()
    {
        Code = "PIL1185",
        Repo = "pilot",
        RepoPath = "C:/repos/pilot",
        Issue = 1185,
        Title = "Designs and ideas are linked many to many",
        Branch = "flight/pil1185",
        Worktree = "C:/repos/pilot/.claude/worktrees/pil1185",
        Base = "origin/main",
        State = state,
        CreatedAt = Now.AddHours(-3),
        FromIdea = IdeaIds.Id(381),
        Designs = [new FlightDesignRow { Id = "DSN-1", Version = 1, Sha256 = "ab" }, new FlightDesignRow { Id = "DSN-3", Version = 2, Sha256 = "cd" }],
    };

    /// <summary>The Flights page with <see cref="BehindFlight"/> as its only flight.</summary>
    internal static FlightsScreen BehindFlights(FlightState state = FlightState.Flying) => new(FlightLine.Build([BehindFlight(state)], [], Now), "test", Now);

    /// <summary>The Ideas sample with <see cref="BehindFlight"/> in its flights, so the idea I381 (linked to DSN-1 and DSN-3) has a flight that builds DSN-1's older version.</summary>
    internal static IdeasScreen BehindIdeas(FlightState state = FlightState.Flying) => DesignIdeas() with { Flights = [BehindFlight(state)] };

    /// <summary>The Designs sample with <see cref="BehindFlight"/> among its flights: DSN-1 built in v1 (v2 not built yet), DSN-3 built in v2, DSN-2 built by none.</summary>
    internal static DesignsScreen BehindDesigns() => DesignsFor("sample", Now) with { Flights = [BehindFlight()] };
}
