using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Poll by state (pilot #1293) where a real node's readers run: a <see cref="PrChecks"/> with its flight and leg commanders, so the
/// phases that read a flight's PR before its CI is counted (<c>LandMergedWithoutCardAsync</c>, <c>LookAtMarksAsync</c>) do run. The
/// PR's CI is counted in every look that read its repo, though the read has already moved the PR's next-read time on, and in no look that
/// read nothing.
/// </summary>
public sealed class PrPollBudgetTestsOnANode : ReviewTestsBase
{
    [Fact]
    public async Task ALookThatReadThePrCountsItsCiAndALookThatReadNothingDoesNot()
    {
        var (flight, _) = await Passed("70");
        flight.Review.ShouldNotBeNull("the marks look reads this flight's PR before the CI count");
        _state.Legs(flight.Code).ShouldNotBeEmpty();
        // The batch is live for the repo, as GitHub's would be.
        _github.OpenBranches.GetOrAdd(flight.RepoPath, _ => new()).Enqueue(flight.Branch);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Pending, 1, 3);
        var checks = new PrChecks(_state, _github, _clock, _flights, _legs);

        await checks.LookAsync(Ct);
        var first = _github.CiReads;
        first.ShouldBeGreaterThan(0, "the first look counts the CI");
        var batches = _github.Reads.GetValueOrDefault((flight.RepoPath, nameof(IGitHub.OpenPullRequestsByHeadAsync)));
        batches.ShouldBeGreaterThan(0);

        // Past CiInterval, but the PR is not due (CI running): the look reads nothing and asks for no runs.
        _clock.Advance(checks.CiInterval + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        _github.Reads.GetValueOrDefault((flight.RepoPath, nameof(IGitHub.OpenPullRequestsByHeadAsync))).ShouldBe(batches, "not due: the repo is not read");
        _github.CiReads.ShouldBe(first, "a look that read nothing does not count the CI either");

        // Due now (the node pushed it): the look's first reader reads the repo and moves the PR's next read on before the CI count, which counts all the same.
        _state.PrPolls.MarkDue(flight.RepoPath, flight.Branch, TimeSpan.Zero);
        _clock.Advance(checks.CiInterval + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        _github.Reads.GetValueOrDefault((flight.RepoPath, nameof(IGitHub.OpenPullRequestsByHeadAsync))).ShouldBeGreaterThan(batches, "due: the repo is read");
        _github.CiReads.ShouldBeGreaterThan(first, "the look read the PR, so its CI is counted again");
    }
}
