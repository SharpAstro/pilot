using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>The node's wakes are told of the peers a flight commander sees come and go.</summary>
    private static FlightCommander WithWakes(FlightCommander flights, IServiceProvider services)
    {
        flights.Wakes = services.GetRequiredService<AutopilotWakes>();
        return flights;
    }

    /// <summary>
    /// The investigator (<see cref="Investigator"/>, PIL1285): the switch's thinking tier, and the hook that holds a ring for it. The sweep's
    /// start resolves it, so the hook is on the state before the first look; with the switch off it holds and starts nothing.
    /// </summary>
    private static void AddInvestigator(WebApplicationBuilder builder, NodeOptions options, LogWriter logWriter)
    {
        builder.Services.AddSingleton(AutopilotWakes.For);
        builder.Services.AddSingleton(services =>
        {
            var state = services.GetRequiredService<NodeState>();
            var investigator = new Investigator(state, services.GetRequiredService<IWorkerLauncher>(), services.GetRequiredService<RoleModels>(),
                services.GetRequiredService<StartGate>(), services.GetRequiredService<FailureLedger>(), services.GetRequiredService<TimeProvider>())
            {
                Machine = options.Machine,
                DefaultFolder = options.WatchtowerFolder,
                Pilot = Briefs.Run(Briefs.PilotCli(), null),
                RepoOf = code => state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))?.RepoPath,
                Log = line => logWriter.WriteLine(line),
            };
            state.Investigations = investigator;
            return investigator;
        });
        builder.Services.AddHostedService(services => new InvestigatorSweep(services.GetRequiredService<Investigator>()));
    }
}
