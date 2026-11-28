using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A merge-queue removal at or before the node's recorded push of the PR's current head belongs to an earlier head (#1407): nothing is
/// done about it at the new head, it does not hold the PR-ready restore, and Merge queues the new head. A removal after that push is
/// handled as ever. The push record is on the flight's row, so the rule holds across a node restart.
/// </summary>
// Slow: real git over the seed's bare origin, as the push and the conflict handling need (see RebaseLayFlightTests).
[Trait("Category", "Slow")]
public sealed class StaleQueueRemovalTests() : ReviewTestsBase(realGit: true)
{
    private static void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-a", "-m", message);

    private PrChecks Checks(LegCommander? legs = null) => new(_state, _github, TimeProvider.System, legs: legs ?? _legs);

    // The flight is passed at head A; the queue dropped A at a time just before now. Then a leg's commit B is pushed by the node (PushAsync,
    // not a lay), the review and the PR move to B, and GitHub still shows A's removal as the last queue event, now against B.
    private async Task<(FlightRow Flight, string A, string B, DateTimeOffset RemovedAt)> RemovedThenPushed(string reason)
    {
        var (flight, _) = await Passed("1");
        var a = Git(flight.Worktree, "rev-parse", "HEAD");
        var removedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        File.WriteAllText(Path.Combine(flight.Worktree, "p.txt"), "b\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "b");
        var b = Git(flight.Worktree, "rev-parse", "HEAD");
        await _legs.PushAsync(F(flight.Code), Ct);
        F(flight.Code).PushedHead.ShouldBe(b);
        _github.Pr = _github.Pr! with { Head = b };
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = b } : null });
        _state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "test", LegCommander.PrReady);
        _github.Queue = GhAnswers.MergeQueue("dropped", b, removedAt: removedAt, removedReason: reason);
        // Checks still running: the looks restore a PR-ready card at B but nothing merges it by itself, so the card and the plane stay for the asserts.
        _github.Merge = GhAnswers.MergeState(MergeKinds.Pending, 1, 3);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Pending, 1, 3);
        return (F(flight.Code), a, b, removedAt);
    }

    private void NothingDoneAt(FlightRow flight, string head, string planeHead)
    {
        _legs.DroppedForConflict(flight.Code, head).ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.DropUnexplainedKind);
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.QueueDropped);
        _legs.RebaseProbes.ShouldBe(0);
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(planeHead);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AConflictRemovalOlderThanThePushedHeadIsDoneNothingAboutAndMergeQueuesTheHead()
    {
        var (flight, _, b, _) = await RemovedThenPushed("MERGE_CONFLICT");
        var checks = Checks();
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        NothingDoneAt(flight, b, b);
        _github.Merge = GhAnswers.MergeState(MergeKinds.Ready, 3, 3);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing, 3, 3);
        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", b);
        merge.Error.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(b);
    }

    [Fact]
    public async Task TheSameHoldsForAFreshCommanderAndChecksOverTheSameStateAfterARestart()
    {
        var (flight, _, b, _) = await RemovedThenPushed("MERGE_CONFLICT");
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        var restarted = new LegCommander(_state, _workers, new FakeChecks(), _github, _git, new Planes(_git), _flights, options, _clock)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
        var checks = Checks(restarted);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        restarted.DroppedForConflict(flight.Code, b).ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.DropUnexplainedKind);
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.QueueDropped);
        restarted.RebaseProbes.ShouldBe(0);
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(b);
        _github.Merge = GhAnswers.MergeState(MergeKinds.Ready, 3, 3);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing, 3, 3);
        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", b);
        merge.Error.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(b);
    }

    [Fact]
    public async Task ARemovalAfterThePushOfTheHeadIsStillMarkedAndHandled()
    {
        var (flight, _, b, _) = await RemovedThenPushed("MERGE_CONFLICT");
        var pushedAt = F(flight.Code).PushedAt.ShouldNotBeNull();
        _github.Queue = GhAnswers.MergeQueue("dropped", b, removedAt: pushedAt.AddSeconds(5), removedReason: "MERGE_CONFLICT");

        await Checks().LookAsync(Ct);

        _legs.DroppedForConflict(flight.Code, b).ShouldBeTrue();
    }

    [Fact]
    public async Task AFailedChecksRemovalOlderThanThePushedHeadStartsNoInvestigationAndRaisesNoQueueCard()
    {
        var (flight, _, b, _) = await RemovedThenPushed("FAILED_CHECKS");
        var checks = Checks();
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        NothingDoneAt(flight, b, b);
        _state.Cards().ShouldNotContain(c => c.Kind == FailureAlerts.AlertKind);
    }

    [Fact]
    public async Task AFailedChecksRemovalAfterThePushIsStillCarded()
    {
        var (flight, _, b, _) = await RemovedThenPushed("FAILED_CHECKS");
        var pushedAt = F(flight.Code).PushedAt.ShouldNotBeNull();
        _github.Queue = GhAnswers.MergeQueue("dropped", b, removedAt: pushedAt.AddSeconds(5), removedReason: "FAILED_CHECKS");

        await Checks().LookAsync(Ct);

        _state.Cards().ShouldContain(c => c.Kind == PrChecks.QueueDropped);
    }

    [Fact]
    public async Task ThePrReadyCardIsRestoredAtTheNewHeadWhileTheOldRemovalIsTheLastEvent()
    {
        var (flight, _, b, _) = await RemovedThenPushed("MERGE_CONFLICT");

        await Checks().LookAsync(Ct);

        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegCommander.PrReady && c.Subject == Verbs.FlightTarget(flight.Code)
            && c.Choices.Any(ch => ch.Args.SequenceEqual(new[] { b })));
    }
}
