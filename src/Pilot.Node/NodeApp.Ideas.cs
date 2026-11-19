using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public static partial class NodeApp
{
    /// <summary>The ideas, and the models that merge, tidy and sort them.</summary>
    private static void AddIdeas(WebApplicationBuilder builder, LogWriter logWriter)
    {
        builder.Services.AddSingleton<IIdeaMerger, ClaudeIdeaMerger>();
        builder.Services.AddSingleton<IIdeaTidier, ClaudeIdeaTidier>();
        builder.Services.AddSingleton<IIdeaMilestoner, ClaudeIdeaMilestoner>();
        builder.Services.AddSingleton<IIssueReadinessChecker, ClaudeIssueReadinessChecker>();
        builder.Services.AddSingleton<IMilestoneSuggester, ClaudeMilestoneSuggester>();
        builder.Services.AddSingleton<IIssueSplitter>(services => new ClaudeIssueSplitter(services.GetService<Attachments>()));
        builder.Services.AddSingleton<IIdeaRepoChooser, ClaudeIdeaRepoChooser>();
        builder.Services.AddSingleton(services =>
        {
            var ideas = ActivatorUtilities.CreateInstance<IdeaCommander>(services);
            ideas.Error = logWriter;
            ideas.Attachments = services.GetRequiredService<Attachments>();
            ideas.Designs = services.GetRequiredService<Pilot.Node.Designs.DesignStore>();
            ideas.Designer = services.GetRequiredService<Pilot.Node.Designs.Designers>();
            return ideas;
        });
    }
}
