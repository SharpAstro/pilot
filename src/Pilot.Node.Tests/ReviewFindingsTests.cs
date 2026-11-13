using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The files a report's Blocking findings name (pilot #1024), so the node can tell whether a fix round touched any of them.</summary>
public class ReviewFindingsTests
{
    // PIL1020's round-1 report on PR #1021, trimmed to the lines that matter here.
    private const string Round1 = """
        **pilot's review asks for changes** on `5b635acf4`

        ## Blocking

        - The unground test does not exercise the fix it is meant to prove (`src/Pilot.Node.Tests/UngroundWhileRecheckTests.cs`).
          `LegCommanderTestsBase` builds `new FlightCommander(_state, new Planes(_git), TimeProvider.System)` with no `github`, so `FlightCommander.Trackers` is null. The refusal came from `UnholdAsync`, which skips its branch.
          Needed: give the flight a labelled hold in the test, as `LegSelfFinishTests` does.

        - `JobRunner.NoteRecheckQueued` has no test, so the words are proven only against a hand-written note (`src/Pilot.Node/Flights/JobRunner.cs:980`, calls at about lines 1308 and 1507).
          Needed: assert that `L("parser").Recheck!.Queued` contains the job id.

        ## Not blocking

        - `src/README.md:731` still documents `pilot repos sync` with no argument.
        - Other words still say "rerunning" in `src/Pilot.Node/NodeApp.cs:390`.
        """;

    private const string Round2 = """
        ## Blocking

        - `JobRunner.NoteRecheckQueued` still has no test (`src/Pilot.Node/Flights/JobRunner.cs:984`, calls at lines 1308 and 1507).

        ## Not blocking

        - Round 1's README note is done: `src/README.md:731` now documents it.
        """;

    [Fact]
    public void Round1NamesTheTestFileAndJobRunnerAndNotTheReadmeUnderNotBlocking()
    {
        var f = ReviewFindings.Read(Round1);

        f.Blocking.Count.ShouldBe(2);
        f.NotBlocking.Count.ShouldBe(2);
        f.BlockingFiles.ShouldBe(["src/Pilot.Node.Tests/UngroundWhileRecheckTests.cs", "src/Pilot.Node/Flights/JobRunner.cs"]);
    }

    [Fact]
    public void Round1IsTouchedByItsTestFileAndNotByTheReadmeAlone()
    {
        var f = ReviewFindings.Read(Round1);

        f.Touches(["src/README.md"]).ShouldBeFalse();
        f.Touches(["src/README.md", "src/Pilot.Node.Tests/UngroundWhileRecheckTests.cs"]).ShouldBeTrue();
        f.Touches(["SRC/PILOT.NODE/FLIGHTS/JOBRUNNER.CS"]).ShouldBeTrue();
    }

    [Fact]
    public void Round2NamesJobRunnerWithoutItsLineNumber()
    {
        ReviewFindings.Read(Round2).BlockingFiles.ShouldBe(["src/Pilot.Node/Flights/JobRunner.cs"]);
    }

    [Fact]
    public void ABlockingSectionNamingNoFileNamesNone()
    {
        var f = ReviewFindings.Read("## Blocking\n\n- `JobRunner.NoteRecheckQueued` has no test.\n  Needed: call `Foo.Bar()`.\n\n## Not blocking\n\n- `src/README.md` is stale.\n");

        f.Blocking.Count.ShouldBe(1);
        f.BlockingFiles.ShouldBeEmpty();
        f.Touches(["src/README.md"]).ShouldBeFalse();
    }

    [Fact]
    public void AFileNamedOnlyUnderHumanVerificationIsNotBlocking()
    {
        var f = ReviewFindings.Read("## Blocking\n\n- Bad.\n\n## Human verification\n\n- Look at `src/Pilot.Gui/Page.cs`.\n");

        f.BlockingFiles.ShouldBeEmpty();
    }

    [Fact]
    public void APlainReportReadsItsWholeTextAsBlocking()
    {
        var f = ReviewFindings.Read("The change misses src/Pilot.Node/A.cs and `docs/b.md:12:3`.");

        f.Structured.ShouldBeFalse();
        f.BlockingFiles.ShouldBe(["src/Pilot.Node/A.cs", "docs/b.md"]);
    }

    [Fact]
    public void AWindowsPathBecomesForwardSlashedAndIsKeptOnceIgnoringCase()
    {
        var f = ReviewFindings.Read("## Blocking\n\n- See src\\Pilot.Node\\Flights\\Thing.cs and again SRC/pilot.node/flights/thing.cs.\n");

        f.BlockingFiles.ShouldBe(["src/Pilot.Node/Flights/Thing.cs"]);
        f.Touches(["src\\Pilot.Node\\Flights\\Thing.cs"]).ShouldBeTrue();
    }

    [Fact]
    public void ABareFileNameMatchesTheLastSegmentOfAChangedPath()
    {
        var f = ReviewFindings.Read("## Blocking\n\n- Fix `Pilot.slnx` and `ReviewFindings.cs`.\n");

        f.BlockingFiles.ShouldBe(["Pilot.slnx", "ReviewFindings.cs"]);
        f.Touches(["src/Pilot.slnx"]).ShouldBeTrue();
        f.Touches(["src/Pilot.Node/Flights/ReviewFindings.cs"]).ShouldBeTrue();
        f.Touches(["src/Pilot.Node/Flights/OtherReviewFindings.cs"]).ShouldBeFalse();
        f.Touches([]).ShouldBeFalse();
    }

    [Fact]
    public void ANamedPathWithADirectoryDoesNotMatchAMereLastSegment()
    {
        var f = ReviewFindings.Read("## Blocking\n\n- Fix `src/A/Foo.cs`.\n");

        f.Touches(["src/B/Foo.cs"]).ShouldBeFalse();
    }

    [Theory]
    [InlineData("/src/A/Foo.cs")]
    [InlineData("C:/src/A/Foo.cs")]
    [InlineData("../src/A/Foo.cs")]
    public void ANamedPathThatIsNotRepoRelativeNeverMatches(string named)
    {
        var f = ReviewFindings.Read($"## Blocking

- Fix `{named}`.
");

        f.BlockingFiles.ShouldNotBeEmpty();
        f.Touches(["src/A/Foo.cs", "Foo.cs", named.TrimStart('/')]).ShouldBeFalse();
    }
}
