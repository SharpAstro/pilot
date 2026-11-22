using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The follow-up of a PR no flight owns keys its points by the PR, as a flight's are keyed by its code (#1243 R3.4): a flight made on the issue ticks its own
/// lines, never a bare R1.2 another flight's report names too, and the body says how a point is reported.
/// </summary>
public sealed class FollowUpPrKeyTests
{
    private static PrReviewRow Review(string repo, int number) => new()
    {
        Repo = repo, Number = number, Url = $"https://github.com/{repo}/pull/{number}", Base = "main", Head = "abc", ClonePath = "/c", Worktree = "/w",
        Reviewer = "r", State = PrReviewState.Reviewed, StartedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void APrsFollowUpKeysItsPointsByTheRepoAndNumberAndSaysHowToReportThem()
    {
        var body = ReviewFollowUp.Body(Review("SharpAstro/pilot", 1246), [new ReviewFollowUp.Item("N2.1", "found it", "left it", "settle it")]);

        body.ShouldContain("- [ ] `pilot1246:N2.1`");
        body.ShouldContain("### pilot1246:N2.1:");
        body.ShouldContain(ReviewFollowUp.ReportLine);
        body.ShouldNotContain("- [ ] `N2.1`");
    }

    [Theory]
    [InlineData("SharpAstro/FITS.Lib", 12, "FITSLib12")]
    [InlineData("tianwen", 2435, "tianwen2435")]
    [InlineData("SharpAstro/42", 7, "PR7")]
    public void TheKeysPrefixIsTheRepoNamesLettersAndThePrsNumber(string repo, int number, string expected) =>
        ReviewFollowUp.KeyOf(Review(repo, number)).ShouldBe(expected);

    [Theory]
    [InlineData("PIL1229", "PIL1229")]
    [InlineData("SharpAstro/pilot#1246", "pilot1246")]
    [InlineData("SharpAstro/FITS.Lib#12", "FITSLib12")]
    public void AnotherReposIssueKeysItsPointsByTheReviewsSubject(string subject, string expected) => ReviewFollowUp.KeyOf(subject).ShouldBe(expected);
}
