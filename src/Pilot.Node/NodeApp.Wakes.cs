using Pilot.Node.Flights;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>
    /// The watchtower's wakes besides a new card (PIL1285): a stalled flight, a peer gone or back, a repeat. One per node; with the
    /// switch off it reads and keeps nothing.
    /// </summary>
    private static void AddWakes(WebApplicationBuilder builder) => builder.Services.AddSingleton(AutopilotWakes.For);

    /// <summary>The node's wakes are told of the peers a flight commander sees come and go.</summary>
    private static FlightCommander WithWakes(FlightCommander flights, IServiceProvider services)
    {
        flights.Wakes = services.GetRequiredService<AutopilotWakes>();
        return flights;
    }
}
