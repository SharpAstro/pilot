using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>A time as typed: a full date and time, or a clock time (<c>14:30</c>), which is the next such time from <paramref name="now"/>.</summary>
    internal static DateTimeOffset ParseUntil(string text, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var at))
        {
            throw new UsageException($"cannot read '{text}' as a time; give 14:30 or 2026-10-27T09:00");
        }
        return TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _) && at <= now ? at.AddDays(1) : at;
    }

    /// <summary>
    /// The leg the folder <paramref name="cwd"/> works on: the flying leg of the flight whose plane holds it, and who is
    /// asking, its worker by name, so the black box says which worker ticked what.
    /// </summary>
    internal static (string Target, string Who) LegHere(SnapshotEvent snapshot, string cwd, string fallback = "cli", bool validating = false)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the leg with --leg <code>/<leg>");
        if (validating)
        {
            var pending = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Planned && l.Validation == LegValidation.Pending)
                ?? throw new UsageException($"{flight.Code} has no leg waiting for a validation; name it with --leg {flight.Code}/<leg>");
            return (Verbs.LegTarget(flight.Code, pending.Name), pending.Validator ?? fallback);
        }
        var leg = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Flying)
            ?? throw new UsageException($"{flight.Code} has no leg flying; name it with --leg {flight.Code}/<leg>");
        return (Verbs.LegTarget(flight.Code, leg.Name), leg.Worker ?? fallback);
    }
}
