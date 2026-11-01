using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A re-review reads what changed since the head last reviewed; a head that only merged the base keeps the pass.</summary>
public sealed class ReReviewTests : ReviewTestsBase
{
    private async Task<FlightRow> Opened(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return flight;
    }

    private static void Commit(string worktree, string name, string message)
    {
        File.WriteAllText(Path.Combine(worktree, name), "more\n");
        Git(worktree, "add", ".");
        Git(worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }

    private async Task<(string Code, string Head)> Round(string issue, string verdict, string report)
    {
        var flight = await Opened(issue);
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), verdict, report)).Error.ShouldBeNull();
        return (flight.Code, F(flight.Code).Review!.ReviewedHead.ShouldNotBeNull());
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondRoundsBriefCarriesTheRangeAndTheFirstRoundsFindings()
    {
        var (code, first) = await Round("1", "changes", "- the null check in Parse is gone");
        var flight = F(code);
        flight.ReviewRounds.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Round.ShouldBe(1), r => r.Head.ShouldBe(first), r => r.Verdict.ShouldBe(ReviewState.Changes), r => r.Findings.ShouldContain("null check"));

        Commit(flight.Worktree, "fix.txt", "restore the null check");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);

        var brief = _workers.Started.Last().Brief;
        brief.ShouldContain("round 2");
        brief.ShouldContain($"git diff {first}..HEAD");
        brief.ShouldContain("restore the null check");
        brief.ShouldContain("fix.txt");
        brief.ShouldContain("the null check in Parse is gone");
        brief.ShouldContain("not the whole PR");
        brief.ShouldNotContain("Read the whole diff");
        F(code).ReviewRounds.ShouldNotBeNull().Length.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFirstReviewReadsTheWholeDiff()
    {
        var start = await Review("1", lines: 1, fiveHour: 10);

        start.Brief.ShouldContain("Read the whole diff");
        start.Brief.ShouldNotContain("not the whole PR");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHeadThatOnlyMergedTheBaseKeepsThePassWithNoReviewerStarted()
    {
        var (code, first) = await Round("1", "pass", "fine");
        var flight = F(code);
        Commit(Repo, "base-change.txt", "the base moves on");
        Git(Repo, "push", "-q", "origin", "main");
        Git(flight.Worktree, "fetch", "-q", "origin");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "--no-edit", flight.Base);
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(first);
        var workers = _workers.Started.Count;

        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();

        _workers.Started.Count.ShouldBe(workers);
        var review = F(code).Review.ShouldNotBeNull();
        (review.State, review.ReviewedHead, review.StatusState).ShouldBe((ReviewState.Passed, head, "success"));
        _github.Statuses.Last().Sha.ShouldBe(head);
        _state.Cards().ShouldContain(c => c.Kind == "review-kept" && c.Bin == CardBin.Handled && c.Title.Contains("review kept"));
        // The kept round moves to the merged head, so the next re-review does not range over the base's commits.
        F(code).ReviewRounds.ShouldNotBeNull().Last().Head.ShouldBe(head);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHeadWithARealChangeStartsAReviewer()
    {
        var (code, _) = await Round("1", "pass", "fine");
        Commit(F(code).Worktree, "extra.txt", "one more thing");
        var workers = _workers.Started.Count;

        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);

        _workers.Started.Count.ShouldBe(workers + 1);
        F(code).Review!.State.ShouldBe(ReviewState.Reviewing);
    }
}
