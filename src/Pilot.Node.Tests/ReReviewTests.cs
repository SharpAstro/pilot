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

    private static readonly ReviewRound Round1 = new() { Round = 1, Head = "aaaa111", Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong\n\n## Not blocking\n- C is a nit\n" };
    private static readonly ReviewRound Round2 = new() { Round = 2, Head = "bbbb222", Verdict = ReviewState.Changes, Findings = "## Blocking\n- E is new\n" };

    private static FlightRow BriefFlight => new()
    {
        Code = "PIL283", Repo = "pilot", RepoPath = "/r/pilot", Issue = 283, Branch = "flight/pil283", Worktree = "/w/PIL283",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void AReReviewBriefListsEveryEarlierRoundsPointsAndAsksForTheTableFirst()
    {
        var again = new Briefs.ReReview(Round2, new Briefs.PlaneState("c1 fix", "x.cs | 2")) { Rounds = [Round1, Round2] };
        var brief = Briefs.Review(BriefFlight, [], new Briefs.PlaneState("", ""), "pilot", again: again);

        brief.ShouldContain("- R1.1 (blocking): A is wrong");
        brief.ShouldContain("- R1.2 (not blocking): C is a nit");
        brief.ShouldContain("- R2.1 (blocking): E is new");
        brief.ShouldContain("## Earlier points");
        brief.ShouldContain("`addressed`");
        brief.ShouldContain("`deferred: owner decision`");
        brief.ShouldContain("`not work: <why>`");
        brief.ShouldContain("`node's own`");
        brief.ShouldContain("`not addressed`");
        brief.ShouldContain("follow-up issue");
        brief.ShouldContain("\"Still open\", \"unchanged\", \"for later\" and \"to do\" with no reason are refused");
        brief.ShouldContain("must-block:");
        brief.ShouldContain("build or test break");
        brief.ShouldContain("not the whole PR");
        brief.ShouldContain("give `changes`, and the node takes the round as a pass and files it");
        brief.ShouldContain("Any other finding in untouched text goes under `## Not blocking`", Case.Sensitive, "a finding is made in the PR, not filed, unless it needs a design of its own");
        brief.ShouldContain("answer it `not addressed`, so the next round makes it");
        brief.IndexOf("## Earlier points", StringComparison.Ordinal).ShouldBeLessThan(brief.IndexOf("new findings", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFirstReviewsBriefSaysItIsTheThoroughOne()
    {
        var brief = Briefs.Review(BriefFlight, [], new Briefs.PlaneState("", ""), "pilot");

        brief.ShouldContain("first review, and the thorough one");
        brief.ShouldNotContain("## Earlier points");
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
        _state.Cards().ShouldContain(c => c.Kind == "review-kept" && c.Bin == CardBin.Handled && c.Title.Contains("passed review still stands"));
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
