using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The worker brief says where a look-only capture goes and that a scratch test is deleted, not committed (pilot #1306).</summary>
public sealed class ScratchCaptureBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static string Brief()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };
        return Briefs.For(Flight, leg, [leg]);
    }

    [Fact]
    public void ACaptureToLookAtGoesToTheArtifactsOrTmpFolderThroughAnExistingCaptureTest()
    {
        var brief = Brief();

        brief.ShouldContain("PILOT_JOB_ARTIFACTS");
        brief.ShouldContain("or its tmp folder, never into the repo");
        brief.ShouldContain("PILOT_TEST_CAPTURE");
    }

    [Fact]
    public void NoScratchTestIsAddedToTheTestProject()
    {
        Brief().ShouldContain("add no scratch test to the repo's test project");
    }

    [Fact]
    public void AScratchFileMadeInThePlaneIsDeletedBeforeFinishing()
    {
        Brief().ShouldContain("is deleted before you finish, and your report says so");
    }

    [Fact]
    public void TheRuleHasNoEmDash()
    {
        Brief().Contains('—').ShouldBeFalse();
    }
}
