using System.Diagnostics;
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
    [Trait("Category", "Slow")]
    public async Task UnreadHeadAfterARestartEndedAFinishCheckGetsItsReviewAtTheNewHeadWhenTheFinishRunsAgain()
    {
        var (flight, read) = await ReviewedWithChanges("401");
        // The finish's check was ended by a restart: the leg is left flying with its recheck set, the commits in the plane, never pushed, never reviewed.
        var leg = _state.Legs(flight.Code).Single();
        CommitInPlane(flight, "after-restart.txt");
        _state.PutLeg(leg with
        {
            State = LegState.Flying,
            Recheck = new LegRecheck { Check = "dotnet test", Since = DateTimeOffset.UtcNow.AddMinutes(-5) },
        });
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(read);
        var started = _workers.Started.Count;

        // The finish runs again and its check passes; the sweep after it takes what the finish left.
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, leg.Name), "controller", "done", "parser in")).Error.ShouldBeNull();
        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Done);
        await _legs.AdvanceAllAsync(Ct);
        await Started(_legs, flight.Code);

        _workers.Started.Count.ShouldBe(started + 1);
        F(flight.Code).Review.ShouldNotBeNull().ReviewedHead.ShouldBe(head);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Reviewing);
        Git(flight.Worktree, "ls-remote", "origin", $"flight/{flight.Code.ToLowerInvariant()}").ShouldStartWith(head);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreadHeadOnTheSweepOfACommitAfterAChangesReviewStartsOneReviewer()
    {
        var (flight, read) = await ReviewedWithChanges("405");
        CommitInPlane(flight, "later.txt");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(read);
        var started = _workers.Started.Count;

        await _legs.AdvanceAllAsync(Ct);
        await Started(_legs, flight.Code);

        _workers.Started.Count.ShouldBe(started + 1);
        F(flight.Code).Review.ShouldNotBeNull().ReviewedHead.ShouldBe(head);
        Git(flight.Worktree, "ls-remote", "origin", $"flight/{flight.Code.ToLowerInvariant()}").ShouldStartWith(head);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreadHeadCommandFindingTheReviewRunningStartsNoSecondReviewer()
    {
        var flight = await NewFlight("406", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        CommitInPlane(flight, "while-reviewing.txt");
        var review = F(flight.Code).Review.ShouldNotBeNull();
        review.State.ShouldBe(ReviewState.Reviewing);
        var started = _workers.Started.Count;

        // What the sweep saw outside the lock is stale: the review is running by the time its command takes the lock.
        var result = await _legs.RunAsync(new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(flight.Code), Args = [LegCommander.UnreadHeadArg], Who = "node" }, Ct);

        result.Ok.ShouldBeTrue();
        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).Review.ShouldBe(review);
        Open().ShouldNotContain(c => c.Kind == LegCommander.NextStepKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreadHeadCommandFindingTheHeadReadByNowStartsNoReviewer()
    {
        var (flight, _) = await ReviewedWithChanges("407");
        var started = _workers.Started.Count;

        var result = await _legs.RunAsync(new CommandRequest { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(flight.Code), Args = [LegCommander.UnreadHeadArg], Who = "node" }, Ct);

        result.Ok.ShouldBeTrue();
        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreadHeadOfAPlaneWithUncommittedChangesIsLeftToTheNextSweep()
    {
        var (flight, read) = await ReviewedWithChanges("408");
        CommitInPlane(flight, "committed.txt");
        File.WriteAllText(Path.Combine(flight.Worktree, "dirty.txt"), "not committed\n");
        var started = _workers.Started.Count;

        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).Review.ShouldNotBeNull().ReviewedHead.ShouldBe(read);
        Open().ShouldNotContain(c => c.Kind == LegCommander.NextStepKind);

        // Once the plane is clean again the next sweep takes it.
        File.Delete(Path.Combine(flight.Worktree, "dirty.txt"));
        await _legs.AdvanceAllAsync(Ct);
        await Started(_legs, flight.Code);
        _workers.Started.Count.ShouldBe(started + 1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreadHeadIsReviewedByTheFirstSweepAtNodeStartBeforeTheIntervalPasses()
    {
        var (flight, read) = await ReviewedWithChanges("409");
        CommitInPlane(flight, "left-by-an-update.txt");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(read);
        var started = _workers.Started.Count;

        using var advance = new FlightAdvance(_legs, TimeProvider.System) { Interval = TimeSpan.FromHours(1) };
        await advance.StartAsync(Ct);
        try
        {
            var clock = Stopwatch.StartNew();
            while (F(flight.Code).Review?.ReviewedHead != head)
            {
                clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "the first sweep did not review the unread head");
                await Task.Delay(20, Ct);
            }
            await Started(_legs, flight.Code);
        }
        finally
        {
            await advance.StopAsync(CancellationToken.None);
        }

        _workers.Started.Count.ShouldBe(started + 1);
    }

    [Fact]
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
    public async Task UnreadHeadIsNoneWhenTheChangesReviewReadTheCurrentHead()
    {
        var (flight, _) = await ReviewedWithChanges("403");
        var started = _workers.Started.Count;

        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    [Trait("Category", "Slow")]
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

    /// <summary>A flight whose reviewer did not start: its review is Changes with no head read, and its "Review again" card is up.</summary>
    private async Task<FlightRow> ReviewerNotStarted(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        _workers.Fail = new FlightException("claude --bg did not start: folder not trusted");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        _legs.Models = new RoleModels(new NodeSettings());
        var review = F(flight.Code).Review.ShouldNotBeNull();
        review.State.ShouldBe(ReviewState.Changes);
        review.ReviewedHead.ShouldBeNull();
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewChanges);
        return F(flight.Code);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreadHeadOfAReviewerThatDidNotStartIsNotRetriedBySweepsAndKeepsItsCard()
    {
        // A review that read no head is the controller's "Review again" card: a start tried every minute while the cause lasts would override it.
        var flight = await ReviewerNotStarted("410");
        var cards = _state.Cards().Count(c => c.Kind == LegCommander.ReviewChanges);

        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);
        // A start the sweeps tried would now succeed, so none tried leaves the starts as they were.
        _workers.Fail = null;
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldNotContain(s => s.Name == Reviewer(flight.Code));
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        Open().Single(c => c.Kind == LegCommander.ReviewChanges).Title.ShouldBe($"{flight.Code}'s review did not start");
        _state.Cards().Count(c => c.Kind == LegCommander.ReviewChanges).ShouldBe(cards);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreadHeadLeavesReadyAnywayOverAReviewThatDidNotStartPassed()
    {
        var flight = await ReviewerNotStarted("411");
        _workers.Fail = null;
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), "controller", "pass", "ready anyway")).Error.ShouldBeNull();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        var started = _workers.Started.Count;

        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Count.ShouldBe(started);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
    }
}
