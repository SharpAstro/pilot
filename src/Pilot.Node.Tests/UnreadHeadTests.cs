using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The sweep's invariant: every leg done, the PR open and no review running, a head the last review did not read gets one (PIL485).</summary>
public sealed class UnreadHeadTests : ReviewTestsBase
{
    /// <summary>A flight with its PR open and a Changes review that read the head the PR was opened at.</summary>
    private async Task<(FlightRow Flight, string ReadHead)> ReviewedWithChanges(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        var code = flight.Code;
        (await Run(Verbs.Review, $"flight/{code}", Reviewer(code), "changes", "fix it")).Ok.ShouldBeTrue();
        _legs.Models = new RoleModels(new NodeSettings());
        var read = Git(flight.Worktree, "rev-parse", "HEAD");
        _state.PutFlight(F(code) with { Review = F(code).Review! with { ReviewedHead = read } });
        return (F(code), read);
    }

    private static void CommitInPlane(FlightRow flight, string name)
    {
        File.WriteAllText(Path.Combine(flight.Worktree, name), "more\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", name);
    }

    [Fact]
    public async Task UnreadHeadAfterARestartEndedAFinishCheckGetsItsReviewAtTheNewHeadOnTheSweep()
    {
        var (flight, read) = await ReviewedWithChanges("401");
        // The finish's check was ended by a restart after the leg was recorded done: the commits are in the plane, never pushed, never reviewed.
        CommitInPlane(flight, "after-restart.txt");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(read);
        var started = _workers.Started.Count;

        await _legs.AdvanceAllAsync(Ct);
        await Started(_legs, flight.Code);

        _workers.Started.Count.ShouldBe(started + 1);
        F(flight.Code).Review.ShouldNotBeNull().ReviewedHead.ShouldBe(head);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Reviewing);
        Git(flight.Worktree, "ls-remote", "origin", $"flight/{flight.Code.ToLowerInvariant()}").ShouldStartWith(head);
    }

    [Fact]
    public async Task UnreadHeadOfAFailedLegMarkedDoneAnywayIsPushedAndReviewedBySweep()
    {
        var (flight, read) = await ReviewedWithChanges("402");
        var leg = _state.Legs(flight.Code).Single();
        CommitInPlane(flight, "accepted.txt");
        _state.PutLeg(leg with { State = LegState.Failed });
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, leg.Name), "controller", "accepted", "good enough")).Error.ShouldBeNull();
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(read);

        await _legs.AdvanceAllAsync(Ct);
        await Started(_legs, flight.Code);

        Git(flight.Worktree, "ls-remote", "origin", $"flight/{flight.Code.ToLowerInvariant()}").ShouldStartWith(head);
        F(flight.Code).Review.ShouldNotBeNull().ReviewedHead.ShouldBe(head);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    public async Task UnreadHeadIsNoneWhenTheChangesReviewReadTheCurrentHead()
    {
        var (flight, _) = await ReviewedWithChanges("403");
        var started = _workers.Started.Count;

        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    public async Task UnreadHeadNeverReplacesAReviewStillRunning()
    {
        var flight = await NewFlight("404", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        _legs.Models = new RoleModels(new NodeSettings());
        CommitInPlane(flight, "while-reviewing.txt");
        var review = F(flight.Code).Review.ShouldNotBeNull();
        review.State.ShouldBe(ReviewState.Reviewing);
        var started = _workers.Started.Count;

        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).Review.ShouldBe(review);
    }
}
