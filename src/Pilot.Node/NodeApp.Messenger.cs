using Pilot.Node.Flights;
using Pilot.Node.Sessions;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>
    /// The node's own path to a session (say, resume, end): SessionMessenger and its hooks. In a part of its own, one statement per line,
    /// so NodeApp.Services.cs stays under its size limit (#1206).
    /// </summary>
    private static void AddSessionMessenger(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services => new SessionMessenger(
            services.GetRequiredService<NodeState>(),
            services.GetRequiredService<SessionTracker>(),
            services.GetRequiredService<ISessionDelivery>(),
            logWriter,
            services.GetRequiredService<StartOptionsBook>(),
            services.GetRequiredService<SessionLedger>())
        {
            Outage = services.GetRequiredService<TurnOutage>(),
            Clock = services.GetRequiredService<TimeProvider>(),
            // Ending a session cancels the jobs it owns.
            CancelJobs = (owners, who) => services.GetRequiredService<JobRunner>().CancelOwnedBy(owners, who),
            Sends = services.GetRequiredService<SendLog>(),
            Timings = services.GetRequiredService<SendTimings>(),
            Removal = services.GetRequiredService<ClaudeJobRemoval>(),
            WaitGone = SessionMessenger.WaitProcessGoneAsync,
            Watchers = new SessionWatchers(OsProcessTable.Instance),
            Streams = () => services.GetRequiredService<Watchtower>(),
        });
    }
}
