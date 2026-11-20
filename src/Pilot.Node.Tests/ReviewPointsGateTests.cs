using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The verdict gate of a re-review (pilot #1107): the table answers every earlier point or the verdict is refused, and a new blocking
/// finding in text the fix round did not touch is left for the follow-up unless it is marked must-block. In-memory node, FakeRepoGit.
/// </summary>
public sealed class ReviewPointsGateTests : ReviewTestsBase
{
    private const string FirstReport = "## Blocking\n- A is wrong in Parser.cs:10\n- D is missing from Parser.cs:20\n";

    private const string Table = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n";

    private async Task<string> SecondRound()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", FirstReport)).Error.ShouldBeNull();
        File.WriteAllText(Path.Combine(F(flight.Code).Worktree, "fix.txt"), "more\n");
        Git(F(flight.Code).Worktree, "add", ".");
        Git(F(flight.Code).Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix A");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Reviewing);
        return flight.Code;
    }

    private async Task<CommandResult> Verdict(string code, string verdict, string report) =>
        await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), verdict, report);

    [Fact]
    public async Task APointAnsweredStillOpenWithNoReasonIsRefusedAndNothingMerges()
    {
        var code = await SecondRound();

        var result = await Verdict(code, "pass", Table + "| R1.2 | still open | |\n");

        result.Error.ShouldNotBeNull().ShouldContain("R1.2");
        F(code).Review!.State.ShouldBe(ReviewState.Reviewing);
        F(code).ReviewRounds!.Length.ShouldBe(1);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.PrReady);
    }

    [Fact]
    public async Task APointNotMentionedIsRefusedNamingIt()
    {
        var code = await SecondRound();

        var result = await Verdict(code, "pass", Table);

        result.Error.ShouldNotBeNull().ShouldContain("R1.2 is not mentioned");
        F(code).Review!.State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    public async Task APassWithANotAddressedPointIsRefused()
    {
        var code = await SecondRound();

        var result = await Verdict(code, "pass", Table + "| R1.2 | not addressed | |\n");

        result.Error.ShouldNotBeNull().ShouldContain("not addressed");
        F(code).Review!.State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    public async Task ANotAddressedPointBlocksAndTheAnswersAreKeptOnTheRound()
    {
        var code = await SecondRound();

        (await Verdict(code, "changes", Table + "| R1.2 | not addressed | |\n")).Error.ShouldBeNull();

        F(code).Review!.State.ShouldBe(ReviewState.Changes);
        var round = F(code).ReviewRounds!.Last();
        round.Answers.ShouldNotBeNull().Select(a => (a.Id, a.Answer)).ShouldBe([("R1.1", "addressed"), ("R1.2", "not addressed")]);
    }

    private const string Settled = Table + "| R1.2 | deferred: owner decision | Parser.cs is out of this flight's scope, filed for later |\n";

    [Fact]
    public async Task ANewFindingInAnUntouchedFileThatIsNotMustBlockIsLeftAndThePassGoesOn()
    {
        var code = await SecondRound();

        (await Verdict(code, "changes", Settled + "\n## Blocking\n- E: the Other.cs:5 loop is slow\n")).Error.ShouldBeNull();

        F(code).Review!.State.ShouldBe(ReviewState.Passed);
        var round = F(code).ReviewRounds!.Last();
        round.Verdict.ShouldBe(ReviewState.Passed);
        round.LeftPoints.ShouldNotBeNull().ShouldHaveSingleItem().ShouldContain("Other.cs:5");
        ReviewFindings.Read(round.Findings).HasBlocking.ShouldBeFalse();
    }

    [Fact]
    public async Task ANewFindingInAChangedLineBlocks()
    {
        var code = await SecondRound();

        (await Verdict(code, "changes", Settled + "\n## Blocking\n- E: fix.txt:1 breaks the build\n")).Error.ShouldBeNull();

        F(code).Review!.State.ShouldBe(ReviewState.Changes);
        F(code).ReviewRounds!.Last().LeftPoints.ShouldBeNull();
    }

    [Fact]
    public async Task AMustBlockFindingInAnUntouchedFileBlocks()
    {
        var code = await SecondRound();

        (await Verdict(code, "changes", Settled + "\n## Blocking\n- must-block: build or test break in Other.cs:5\n")).Error.ShouldBeNull();

        F(code).Review!.State.ShouldBe(ReviewState.Changes);
        F(code).ReviewRounds!.Last().LeftPoints.ShouldBeNull();
    }

    [Fact]
    public void TheDiffLinesTellAChangedLineFromAnUnchangedOneInAChangedFile()
    {
        var patch = "diff --git a/src/A.cs b/src/A.cs\nindex 1..2 100644\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -8,4 +8,5 @@\n c\n c\n+new\n c\n c\n";

        var lines = ReviewDiffLines.Parse(patch);

        lines.IsOutside("A.cs:3 is wrong").ShouldBeTrue();
        lines.IsOutside("A.cs:10 is wrong").ShouldBeFalse();
        lines.IsOutside("A.cs is wrong").ShouldBeFalse();
        lines.IsOutside("B.cs:10 is wrong").ShouldBeTrue();
        lines.IsOutside("nothing names a file").ShouldBeFalse();
        // A partial or absolute path names the same file as the diff's repo-relative one.
        lines.IsOutside("Pilot/src/A.cs:10 is wrong").ShouldBeFalse();
        lines.IsOutside("src/A.cs:10 is wrong").ShouldBeFalse();
        lines.IsOutside(@"C:\work\plane\src\A.cs:10 is wrong").ShouldBeFalse();
        lines.IsOutside("rc/A.cs:10 is wrong").ShouldBeTrue();
    }
}
