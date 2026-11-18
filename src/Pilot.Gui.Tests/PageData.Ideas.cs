using Pilot.Contracts;

namespace Pilot.Gui.Tests;

internal sealed partial class PageData
{
    /// <summary>
    /// The Ideas page's sample for the design mark (PIL1152): I381 is marked and its design DSN-1 has a version (v2), I382 is marked and has no design,
    /// I383 is not marked, and I384 is marked with a design (DSN-2) that has no published version. <paramref name="marked"/> false leaves every idea
    /// unmarked and without a design: the page as it was before the mark.
    /// </summary>
    internal static IdeasScreen DesignIdeas(bool marked = true)
    {
        IdeaRow Idea(int number, string title, string body, bool needs, string? design = null, int? version = null, IdeaDesignLink[]? designs = null) => new()
        {
            Id = IdeaIds.Id(number),
            Repo = "pilot",
            Title = title,
            Body = body,
            State = IdeaState.Noted,
            UpdatedAt = Now.AddMinutes(number),
            Age = "3h",
            Origin = "ada",
            Url = $"https://github.com/SharpAstro/pilot/issues/{number}",
            NeedsDesign = marked && needs,
            Design = marked ? design : null,
            DesignVersion = marked ? version : null,
            // #1185: the designs the idea is linked to, kept whether or not the sample marks ideas.
            Designs = !marked ? [] : designs ?? (design is null ? [] : [new IdeaDesignLink(design, null, version)]),
        };
        return new IdeasScreen(
        [
            Idea(381, "Ideas pane", "Show **ideas** in a pane.\n\n- a list\n- a box", true, "DSN-1", 2),
            Idea(382, "Rail count", "", true),
            Idea(383, "Night mode for the rail", "Keep the red.", false, designs: [new IdeaDesignLink("DSN-1", "Design 0", 2)]),
            Idea(384, "Export the flights page", "As a table.", true, "DSN-2"),
        ], [], ["pilot", "tianwen"], "live from the node", Now);
    }
}
