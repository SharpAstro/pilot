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
            Idea(381, "Ideas pane", "Show **ideas** in a pane.\n\n- a list\n- a box", true, "DSN-1", 2,
                [new IdeaDesignLink("DSN-1", "Design 0", 2), new IdeaDesignLink("DSN-3", "Design 2", 2)]),
            Idea(382, "Rail count", "", true),
            Idea(383, "Night mode for the rail", "Keep the red.", false, designs: [new IdeaDesignLink("DSN-1", "Design 0", 2)]),
            Idea(384, "Export the flights page", "As a table.", true, "DSN-2"),
        ], [], ["pilot", "tianwen"], "live from the node", Now);
    }

    /// <summary>
    /// An Ideas page of ideas, open issues in two milestones, a flight that flies idea I381, and an ask card; one issue's readiness was checked
    /// <paramref name="checked"/> before <see cref="Now"/> (30 s by default: its "checked 30s ago" turns over each second).
    /// </summary>
    internal static IdeasScreen FingerprintIdeas(DateTimeOffset at, TimeSpan? @checked = null, string title = "Ideas pane", IdeaState state = IdeaState.Noted,
        string? milestone = "Next", string? flightIdea = "I381", FlightState flight = FlightState.Flying, string ask = "Which repo?", int open = 2)
    {
        IdeaRow Idea(int number, string t, IdeaState s) => new()
        {
            Id = IdeaIds.Id(number), Repo = "pilot", Title = t, Body = "Body of " + number, State = s, UpdatedAt = Now.AddMinutes(number), Age = "3h",
            Origin = "ada", Url = $"https://github.com/SharpAstro/pilot/issues/{number}", Designs = [new IdeaDesignLink("DSN-1", "Design", 2)],
        };
        IssueRow Issue(int number, string t, string? m, IssueReadiness? readiness = null) => new()
        {
            Repo = "pilot", Number = number, Title = t, Body = "Issue " + number, Milestone = m, Age = "2d", Author = "ada",
            Url = $"https://github.com/SharpAstro/pilot/issues/{number}", Readiness = readiness,
        };
        var readiness = new IssueReadiness(false, ["No tests named."], Now - (@checked ?? TimeSpan.FromSeconds(30)));
        return new IdeasScreen([Idea(381, title, state), Idea(382, "Rail count", IdeaState.Noted)],
            [new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = CardKinds.IdeaRepo, Subject = "idea/pilot/I382", Title = ask, At = Now, State = CardState.Open,
                Choices = [new CardChoice { Label = "pilot", Verb = "idea-move", Target = "idea/pilot/I382" }] }],
            ["pilot", "tianwen"], "live from the node", at)
        {
            Milestones = [new MilestoneRow { Repo = "pilot", Number = 11, Title = "Ideas", OpenIssues = open }, new MilestoneRow { Repo = "pilot", Number = 12, Title = "Next", OpenIssues = 1 }],
            Issues = [Issue(381, "Ideas pane", "Ideas", readiness), Issue(390, "Other work", milestone), Issue(391, "More work", "Next")],
            Flights = [new FlightRow
            {
                Code = "PIL381", Repo = "pilot", RepoPath = "C:/repos/pilot", Issue = 381, FromIdea = flightIdea, Branch = "flight/pil381", Worktree = "C:/repos/pilot/.claude/worktrees/pil381",
                Base = "origin/main", State = flight, CreatedAt = Now.AddHours(-1),
            }],
        };
    }
}
