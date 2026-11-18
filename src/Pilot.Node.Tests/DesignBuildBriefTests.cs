using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What the planner, the worker and the reviewer are told of the design version a flight was built from (PIL1153).</summary>
public class DesignBuildBriefTests
{
    private const string Sha = "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12";
    private static readonly Briefs.DesignRef Design = new(Path.Combine("data", "designs"), ["80x24", "160x44"]);
    private static readonly string Folder = Path.Combine("data", "designs", "DSN-7", "versions", "v3");

    private static FlightRow Flight(bool design) => new()
    {
        Code = "PIL500",
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 500,
        Branch = "flight/pil500",
        Worktree = "/w/PIL500",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
        FromDesign = design ? "DSN-7" : null,
        FromDesignVersion = design ? 3 : null,
        FromDesignSha256 = design ? Sha : null,
    };

    private static readonly LegRow Leg = new()
    {
        Flight = "PIL500",
        Name = "build",
        Order = 1,
        Goal = "Build the screens.",
        DoneWhen = "They exist.",
        State = LegState.Pending,
    };

    private static readonly Briefs.PlaneState Plane = new("", "");

    private static string[] All(bool design, Briefs.DesignRef? where) =>
    [
        Briefs.Plan(Flight(design), [], "pilot", design: where),
        Briefs.For(Flight(design), Leg, [Leg], plane: Plane, design: where),
        Briefs.Review(Flight(design), [Leg], Plane, "pilot", design: where),
    ];

    [Fact]
    public void EveryRoleNamesTheVersionsFolderTheHashAndItsFiles()
    {
        foreach (var brief in All(true, Design))
        {
            brief.ShouldContain(Folder);
            brief.ShouldContain(Sha);
            brief.ShouldContain("prototype.json");
            brief.ShouldContain("NOTES.md");
            brief.ShouldContain("renders/");
        }
    }

    [Fact]
    public void ThePlannerAndTheWorkerAreToldToBuildTheScreensAsTheTreeShowsThem()
    {
        var all = All(true, Design);

        all[0].ShouldContain("Build the screens as the prototype tree shows them");
        all[1].ShouldContain("Build the screens as the prototype tree shows them");
        all[1].ShouldContain("said in the leg's report");
    }

    [Fact]
    public void TheReviewerIsToldTheSizesAndThatADifferenceIsAFindingUnlessThePrSaysWhy()
    {
        var review = All(true, Design)[2];

        foreach (var (width, height) in DesignSizes.All)
        {
            review.ShouldContain($"{width}x{height}");
        }
        review.ShouldContain("80x24, 160x44");
        review.ShouldContain("CPU-rendered Gui test captures");
        review.ShouldContain("A difference from the renders is a finding unless the PR says why");
    }

    [Fact]
    public void WithoutTheNodesDesignsFolderTheBriefStillNamesTheVersionAndTheHash()
    {
        foreach (var brief in All(true, null))
        {
            brief.ShouldContain("DSN-7");
            brief.ShouldContain("v3");
            brief.ShouldContain(Sha);
        }
    }

    [Fact]
    public void AFlightWithNoDesignMentionsNone()
    {
        foreach (var brief in All(false, null))
        {
            brief.ShouldNotContain("DSN-");
            brief.ShouldNotContain("prototype tree");
            brief.ShouldNotContain("Design DSN");
        }
    }
}
