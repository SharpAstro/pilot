using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The key prefix of a review's points (#1243 R3.4): a PR's is its repo's letters and number, a flight's its code, so the issues another repo is given
/// carry keys a flight there ticks, never a bare R1.2 another flight's report names too. (A PR no flight owns files no follow-up issue: its points are
/// made by a fixer or put on the PR, #1257.)
/// </summary>
public sealed class FollowUpPrKeyTests
{
    private static PrReviewRow Review(string repo, int number) => new()
    {
        Repo = repo, Number = number, Url = $"https://github.com/{repo}/pull/{number}", Base = "main", Head = "abc", ClonePath = "/c", Worktree = "/w",
        Reviewer = "r", State = PrReviewState.Reviewed, StartedAt = DateTimeOffset.UnixEpoch,
    };

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
