using System.Globalization;
using Pilot.Contracts;
using Pilot.Node.Designs;

namespace Pilot.Node.Flights;

public sealed partial class LegCommander
{
    /// <summary>
    /// <c>design-replan flight/&lt;code&gt; &lt;design&gt;</c> (PIL1185): a flight stays on the design version it was planned from when a newer one is published; this is the
    /// controller moving it. The flight's entry for the design takes the design's current version and prototype hash, the flight's issue is told, and a fresh planner
    /// (the one waiting, if any, is stopped) plans the flight against the new version. Refused for a landed flight, a design the flight does not build and a design with
    /// no version newer than the flight's. Returns what the caller is told.
    /// </summary>
    private async Task<string> ReplanOnDesignAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed: a landed flight is not re-planned");
        }
        var given = (command.Args ?? []).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(given))
        {
            throw new FlightException($"{Verbs.DesignReplan} takes the design to re-plan on: {flight.Code} builds {(flight.Designs.Length == 0 ? "none" : string.Join(", ", flight.Designs.Select(d => d.Id)))}");
        }
        var id = DesignStore.Normalise(given);
        var recorded = flight.Designs.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlightException($"{flight.Code} does not build {id}: it builds {(flight.Designs.Length == 0 ? "no design" : string.Join(", ", flight.Designs.Select(d => d.Id)))}");
        var design = Designs?.Find(id) ?? throw new FlightException($"There is no design {id} on this node");
        if (design.Current is not { } now || now <= recorded.Version)
        {
            throw new FlightException($"{id} has no version newer than v{recorded.Version.ToString(CultureInfo.InvariantCulture)}, the one {flight.Code} builds");
        }
        RefuseIfOnHold(flight.Code);

        var prototype = Path.Combine(Designs.FolderOf(id), DesignStore.VersionsFolderName, "v" + now.ToString(CultureInfo.InvariantCulture), DesignStore.PrototypeFile);
        var sha = File.Exists(prototype) ? DesignStore.Sha256Of(prototype) : null;
        flight = flight with
        {
            Designs = [.. flight.Designs.Select(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) ? d with { Version = now, Sha256 = sha } : d)],
        };
        state.PutFlight(flight);

        var said = $"{id} is now v{now.ToString(CultureInfo.InvariantCulture)}: {flight.Code} was planned on v{recorded.Version.ToString(CultureInfo.InvariantCulture)} and is re-planned on v{now.ToString(CultureInfo.InvariantCulture)} (prototype sha256 {sha ?? "not found"}), asked for by {command.Who ?? "the controller"}.";
        try
        {
            await OffLockAsync(null, () => github.CommentIssueAsync(flight.RepoPath, flight.Issue, said, cancellationToken));
        }
        catch (FlightException ex)
        {
            await Error.WriteLineAsync($"design-replan: {flight.Code} moved to {id} v{now}, but its issue {flight.Issue} got no comment: {ex.Message}");
        }

        flight = FlightOf(command.Target);
        await StopPlannerAsync(flight, cancellationToken);
        StartPlanner(flight, replacing: true);
        return $"{flight.Code} builds {id} v{now.ToString(CultureInfo.InvariantCulture)} now; a fresh planner is started";
    }
}
