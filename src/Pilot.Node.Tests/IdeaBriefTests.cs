using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What the planner and the watchtower are told about ideas.</summary>
public class IdeaBriefTests
{
    private static FlightRow Flight(string? fromIdea) => new()
    {
        Code = "PIL381",
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 381,
        Branch = "flight/pil381",
        Worktree = "/w/PIL381",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
        FromIdea = fromIdea,
    };

    [Fact]
    public void AFlightMadeFromAnIdeaTellsThePlannerItsPlanGoesInLegsAndThePrAndAdditionsUnderTheIdeasWords()
    {
        var brief = Briefs.Plan(Flight("I381"), [], "pilot");

        brief.ShouldContain("made from the idea I381");
        brief.ShouldContain("Your plan goes in the");
        brief.ShouldContain("legs and the PR");
        brief.ShouldContain("add a section under the idea's words");
        brief.ShouldContain("never replace, shorten or reword");
    }

    [Fact]
    public void AFlightWithNoIdeaSaysNothingOfOne()
    {
        Briefs.Plan(Flight(null), [], "pilot").ShouldNotContain("made from the idea");
    }

    [Fact]
    public void TheWatchtowerIsTaughtTheIdeaCommandsAndNeverToPlanOneWithoutThePerson()
    {
        var brief = Briefs.Watchtower("wt", [], [], [], "pilot");

        brief.ShouldContain("pilot idea I381 plan");
        brief.ShouldContain("I381 becomes PIL381");
        brief.ShouldContain("pilot idea I384 fold PIL294");
        brief.ShouldContain("drop I382, done by #377");
        brief.ShouldContain("pilot idea I381 tidy");
        brief.ShouldContain("pilot idea I381 move tianwen");
        brief.ShouldContain("pilot ideas");
        brief.ShouldContain("pilot idea \"<their words");
        brief.ShouldContain("NEVER plan an idea on your own");
        brief.ShouldContain("`Plan I381: <its title>`");
    }
}
