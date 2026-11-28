using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Every push the node makes of a flight's branch records the head pushed and the time taken just before it (#1407), so a merge-queue
/// removal older than the PR's current head can be told from a drop of that head.
/// </summary>
// Slow: the cases commit and push through the real git CLI helpers (Git, Touch), a second clone of origin and a lease push, which the
// in-memory git is not asked to do here. The GH006 refusal itself is answered in memory too (QueuedPushTests, RepoModel.PreReceive);
// this case is real only because it shares those helpers, and what it adds is the push record after the retry.
[Trait("Category", "Slow")]
public sealed class PushedHeadTests() : ReviewTestsBase(realGit: true)
{
    private static void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-a", "-m", message);

    private static void Touch(string plane, string text)
    {
        File.WriteAllText(Path.Combine(plane, "p.txt"), text);
        Git(plane, "add", ".");
        Commit(plane, text);
    }

    [Fact]
    public async Task APlainPushRecordsThePlaneHeadAndATimeAroundThePush()
    {
        var (flight, _) = await Passed("1");
        Touch(flight.Worktree, "one\n");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");

        var before = _clock.GetUtcNow();
        await _legs.PushAsync(F(flight.Code), Ct);
        var after = _clock.GetUtcNow();

        var row = F(flight.Code);
        row.PushedHead.ShouldBe(head);
        row.PushedAt.ShouldNotBeNull().ShouldBeInRange(before, after);
    }

    [Fact]
    public async Task APushOfAHeadAlreadyRecordedKeepsTheTimeItFirstWentUp()
    {
        var (flight, _) = await Passed("1");
        Touch(flight.Worktree, "one\n");
        await _legs.PushAsync(F(flight.Code), Ct);
        var first = F(flight.Code);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await _legs.PushAsync(F(flight.Code), Ct);

        var row = F(flight.Code);
        row.PushedHead.ShouldBe(first.PushedHead);
        row.PushedAt.ShouldBe(first.PushedAt);
    }

    [Fact]
    public async Task APushOfADifferentHeadMovesThePushedTimeToIt()
    {
        var (flight, _) = await Passed("1");
        Touch(flight.Worktree, "one\n");
        await _legs.PushAsync(F(flight.Code), Ct);
        var first = F(flight.Code);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Touch(flight.Worktree, "two\n");
        var c = Git(flight.Worktree, "rev-parse", "HEAD");
        await _legs.PushAsync(F(flight.Code), Ct);

        var row = F(flight.Code);
        row.PushedHead.ShouldBe(c);
        row.PushedAt.ShouldNotBeNull().ShouldBeGreaterThan(first.PushedAt.ShouldNotBeNull());
    }

    [Fact]
    public async Task ARemovalBetweenTwoPushesOfTheSameHeadIsNotTakenForAnEarlierHeads()
    {
        var (flight, _) = await Passed("1");
        Touch(flight.Worktree, "one\n");
        var b = Git(flight.Worktree, "rev-parse", "HEAD");
        await _legs.PushAsync(F(flight.Code), Ct);
        var t0 = F(flight.Code).PushedAt.ShouldNotBeNull();

        _clock.Advance(TimeSpan.FromMinutes(1));
        var t1 = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _legs.PushAsync(F(flight.Code), Ct);

        t1.ShouldBeGreaterThan(t0);
        _legs.RemovalPredatesHead(flight.Code, b, t1).ShouldBeFalse();
    }

    [Fact]
    public async Task APushGitRefusesLeavesTheEarlierRecordAsItWas()
    {
        var (flight, _) = await Passed("1");
        var recorded = F(flight.Code);
        recorded.PushedHead.ShouldNotBeNull();
        // Origin gains a commit the plane lacks, so the node's own guard refuses the push before git runs.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", Git(flight.Worktree, "remote", "get-url", "origin"), other);
        Git(other, "checkout", "-q", flight.Branch);
        Touch(other, "elsewhere\n");
        Git(other, "push", "-q", "origin", flight.Branch);
        Git(flight.Worktree, "fetch", "-q", "origin");
        Touch(flight.Worktree, "mine\n");

        await Should.ThrowAsync<FlightException>(() => _legs.PushAsync(F(flight.Code), Ct));

        var row = F(flight.Code);
        (row.PushedHead, row.PushedAt).ShouldBe((recorded.PushedHead, recorded.PushedAt));
    }

    [Fact]
    public async Task AGh006PushAnsweredByDequeueAndRetryRecordsATimeNotBeforeTheDequeue()
    {
        var (flight, _) = await Passed("1");
        Touch(flight.Worktree, "queued\n");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        var origin = Git(flight.Worktree, "remote", "get-url", "origin");
        var marker = Path.Combine(_root, "gh006-refused").Replace('\\', '/');
        var hook = Path.Combine(origin, "hooks", "pre-receive");
        File.WriteAllText(hook, $"#!/bin/sh\nif [ -f '{marker}' ]; then exit 0; fi\ntouch '{marker}'\necho 'error: GH006: Protected branch update failed for refs/heads/{flight.Branch}.' >&2\nexit 1\n");
        var queued = GhAnswers.MergeQueue("queued", _github.Pr!.Head, 1);
        _github.Queue = null;
        var looks = 0;
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.MergeQueueAsync) && ++looks == 2)
            {
                _github.Queue = queued;
            }
            return Task.CompletedTask;
        };
        DateTimeOffset? dequeuedAt = null;
        _github.OnDequeued = () => dequeuedAt = _clock.GetUtcNow();

        await _legs.PushAsync(F(flight.Code), Ct);

        dequeuedAt.ShouldNotBeNull();
        var row = F(flight.Code);
        row.PushedHead.ShouldBe(head);
        row.PushedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(dequeuedAt.Value);
    }

    [Fact]
    public async Task ALeasePushRecordsTheLaidHead()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        var (flight, old) = await KeepOursFlight();

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        var laid = Git(flight.Worktree, "rev-parse", "HEAD");
        laid.ShouldNotBe(old);
        F(flight.Code).PushedHead.ShouldBe(laid);
    }

    // As RebaseLayFlightTests' flight: main gains f.txt twice, the branch keeps its side in a keep-ours merge, so a rebase would conflict.
    private async Task<(FlightRow Flight, string Old)> KeepOursFlight()
    {
        var (flight, _) = await Passed("1");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "x\n");
        Git(Repo, "add", "f.txt");
        Commit(Repo, "f");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "side\n");
        Commit(Repo, "side");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        Git(plane, "fetch", "-q", "origin");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine1\n");
        Git(plane, "add", ".");
        Commit(plane, "mine 1");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "-s", "ours", "-m", "keep ours", "origin/main");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine2\n");
        Commit(plane, "mine 2");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var old = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = old } : null });
        return (F(flight.Code), old);
    }
}
