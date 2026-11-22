using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>The node's own autopilot press of the cards its rules decide (<see cref="NodePress"/>, pilot #1269).</summary>
    private static void AddNodePress(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services =>
        {
            var models = services.GetRequiredService<RoleModels>();
            var press = new NodePress(services.GetRequiredService<NodeState>(), () => models.Autopilot,
                (command, cancellationToken) => services.GetRequiredService<NodeCommands>().RunAsync(command, cancellationToken), services.GetRequiredService<TimeProvider>())
            {
                MayEndStray = key => services.GetService<StraySweep>()?.NodeMayEnd(key) == true,
                WhyNoFixer = async (pr, cancellationToken) =>
                {
                    var reviews = services.GetRequiredService<PrReviews>();
                    return reviews.Of(pr) is { } review ? await reviews.WhyNoFixerAsync(pr, review, cancellationToken) : "no review is known here";
                },
                Error = logWriter,
            };
            // A card the node presses itself, with the autopilot on, is never sent to the ring judge.
            services.GetRequiredService<NodeState>().NodePresses = kind => models.Autopilot && NodePress.Owns(kind);
            return press;
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<NodePress>());
    }
}
