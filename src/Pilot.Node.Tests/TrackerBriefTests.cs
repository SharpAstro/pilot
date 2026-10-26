using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class TrackerBriefTests
{
    private static FlightRow Row(TrackerKind tracker, FoldedIssue[]? folded = null) => new()
    {
        Code = "BUL101645",
        Repo = "bullclip",
        RepoPath = "/r/bullclip",
        Issue = 101645,
        Tracker = tracker,
        Branch = "flight/bul101645",
        Worktree = "/w/BUL101645",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
        FoldedIn = folded ?? [],
    };

    private static LegRow Leg() => new()
    {
        Flight = "BUL101645",
        Name = "one",
        Order = 1,
        Goal = "g",
        DoneWhen = "d",
        State = LegState.Planned,
    };

    private static readonly Briefs.PlaneState Plane = new("", "");

    private static string[] Briefs_(FlightRow flight) =>
    [
        Briefs.Plan(flight, [], "pilot"),
        Briefs.Validate(flight, Leg(), [Leg()], "pilot"),
        Briefs.Review(flight, [Leg()], Plane, "pilot"),
    ];

    [Fact]
    public void AShortcutFlightsBriefsNameTheMcpToolAndTheCurlFallbackAndNeverGhIssueView()
    {
        foreach (var brief in Briefs_(Row(TrackerKind.Shortcut, [new FoldedIssue { Number = 7, From = "BUL7" }])))
        {
            brief.ShouldContain("stories-get-by-id");
            brief.ShouldContain("curl -s -H \"Shortcut-Token: $SHORTCUT_API_TOKEN\" https://api.app.shortcut.com/api/v3/stories/101645");
            brief.ShouldNotContain("gh issue view");
        }
    }

    [Fact]
    public void AShortcutPlannerReadsTheFoldedStoryAndListsOtherFlightsByTheirOwnTracker()
    {
        var plan = Briefs.Plan(Row(TrackerKind.Shortcut, [new FoldedIssue { Number = 7, From = "BUL7" }]), [], "pilot");

        plan.ShouldContain("- sc-7, from flight BUL7: read it (the Shortcut MCP tool `stories-get-by-id` with id 7");
        plan.ShouldContain("read its story (a Shortcut story (shown as sc-N)");
        plan.ShouldContain("a GitHub issue (shown as #N)");
    }

    [Fact]
    public void AGitHubFlightsPlannerReadsTheIssueWithGhAsItAlwaysDid()
    {
        var plan = Briefs.Plan(Row(TrackerKind.GitHub, [new FoldedIssue { Number = 7, From = "BUL7" }]), [], "pilot");

        plan.ShouldContain("Read the issue\n(`gh issue view 101645 --comments`), the repo's CLAUDE.md".ReplaceLineEndings());
        plan.ShouldContain("- #7, from flight BUL7: read it (`gh issue view 7 --comments`) and plan its points");
        plan.ShouldContain("read its issue (`gh issue view <number> -R <repo>`), its legs' goals, and the");
        plan.ShouldNotContain("stories-get-by-id");
    }

    [Fact]
    public void AGitHubFlightsValidatorAndReviewerSayNothingOfAnyTracker()
    {
        var flight = Row(TrackerKind.GitHub);

        Briefs.Validate(flight, Leg(), [Leg()], "pilot").ShouldNotContain("stories-get-by-id");
        Briefs.Review(flight, [Leg()], Plane, "pilot").ShouldNotContain("stories-get-by-id");
    }
}
