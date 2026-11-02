using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>
    /// The flight whose plane holds <paramref name="cwd"/>, and who is asking: its reviewer while a review is on, as a
    /// worker is its leg's in <see cref="LegHere"/>.
    /// </summary>
    internal static (string Target, string Who) FlightHere(SnapshotEvent snapshot, string cwd, string fallback = "cli")
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the flight with --flight <code>");
        return (Verbs.FlightTarget(flight.Code), flight.Review is { State: ReviewState.Reviewing } review ? review.Reviewer : fallback);
    }
}
