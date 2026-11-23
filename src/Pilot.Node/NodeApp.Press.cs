using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>The node's own press of the cards its rules decide (<see cref="NodePress"/>, pilot #1269).</summary>
    private static void AddNodePress(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton(services =>
        {
            var press = new NodePress(services.GetRequiredService<NodeState>(),
                (command, cancellationToken) => services.GetRequiredService<NodeCommands>().RunAsync(command, cancellationToken), services.GetRequiredService<TimeProvider>())
            {
                MayEndStray = key => services.GetService<StraySweep>()?.NodeMayEnd(key) == true,
                WhyNoFixer = async (pr, cancellationToken) =>
                {
                    var reviews = services.GetRequiredService<PrReviews>();
                    return reviews.Of(pr) is { } review ? await reviews.WhyNoFixerAsync(pr, review, cancellationToken) : "no review is known here";
                },
                WhyNoReview = (pr, cancellationToken) => services.GetRequiredService<PrReviews>().WhyNoReviewAsync(pr, cancellationToken),
                Stale = new StaleRefused(services.GetRequiredService<NodeState>())
                {
                    Time = services.GetRequiredService<TimeProvider>(),
                    PrState = async (pr, cancellationToken) => (await services.GetRequiredService<IGitHub>().PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken)).State,
                    StrayGone = key =>
                    {
                        var state = services.GetRequiredService<NodeState>();
                        return services.GetService<StraySweep>() is not null && !state.Strays().Any(s => StraySweep.KeyOf(s.Pid, s.StartedAt) == key);
                    },
                },
                Error = logWriter,
            };
            // A card the node presses itself, is never sent to the ring judge.
            services.GetRequiredService<NodeState>().NodePresses = (kind, subject, detail, choices) => press.WouldPress(kind, subject, detail, choices);
            return press;
        });
        builder.Services.AddHostedService(services => services.GetRequiredService<NodePress>());
    }
}
