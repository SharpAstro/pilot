using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A push of a flight's branch takes its PR out of GitHub's merge queue first (PIL836): the queue locks the branch (GH006) and merges
/// the head it was queued at, so work made while the PR is queued would be refused, or left out of the squash merge.
/// </summary>
[Trait("Category", "Slow")]
public sealed class QueuedPushTests : LegCommanderTestsBase
{
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";

    private string Origin => Path.Combine(_root, "origin.git");

    // Parser started, every step ticked, its PR open and passed at a head the leg then moves past, and the PR queued after the leg began
    // (as PIL819's review-notes leg was). Returns the flight, with one commit made in its plane.
    private async Task<FlightRow> QueuedWithACommit()
    {
        var flight = await FlightWithTwoLegs();
        _legs.Models = new RoleModels(null);
        Git(_root, "init", "-q", "--bare", Origin);
        Git(Repo, "remote", "add", "origin", Origin);
        Git(Repo, "push", "-q", "origin", "main");
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        if (L("parser").State == LegState.Planned)
        {
            await Leg(Verbs.ValidateLeg, "flight/TIA1/parser", "go");
        }
        foreach (var i in Enumerable.Range(1, L("parser").Checklist.Length))
        {
            await Leg(Verbs.Check, "flight/TIA1/parser", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // The PR was pushed and reviewed at this head; the other leg is done, so the parser is the last.
        var old = Git(flight.Worktree, "rev-parse", "HEAD");
        Git(flight.Worktree, "push", "-q", "origin", "HEAD:" + flight.Branch);
        MarkDone("render");
        _state.PutFlight(_state.Flights().Single() with
        {
            PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", ReviewedHead = old, StartedAt = DateTimeOffset.UtcNow },
        });
        _github.Pr = new PullRequest(63, "OPEN", old, PrUrl);
        _github.Queue = new MergeQueueState("OPEN", old, Queued: true, Position: 1);

        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the parser, after it was queued");
        return _state.Flights().Single();
    }

    private void MarkDone(string leg) => _state.PutLeg(L(leg) with { State = LegState.Done });

    private string RefusedMarker => Path.Combine(_root, "gh006-refused").Replace('\\', '/');

    private string HookPath => Path.Combine(Origin, "hooks", "pre-receive");

    // Origin refuses a push as GitHub does a queued branch: the GH006 line on the hook's stderr (git relays it as "remote: ..."), exit 1.
    // The marker file says a push was refused; with onlyOnce, the next push passes. With progressFirst, the hook prints a progress line
    // before the GH006 line, as GitHub often does, so the refusal's first line is not GitHub's error. On the model the hook is its
    // PreReceive; on real git (PILOT_TEST_REAL_GIT=1) the same hook is a script in origin.git/hooks.
    private void RefuseWithGh006(FlightRow flight, bool onlyOnce = false, bool progressFirst = false)
    {
        var lines = new List<string>();
        if (progressFirst)
        {
            lines.Add("Resolving deltas: 100% (3/3), completed with 2 local objects.");
        }
        lines.Add($"error: GH006: Protected branch update failed for refs/heads/{flight.Branch}.");
        lines.Add("error: Cannot change this locked branch because it is in a merge queue.");
        if (RepoModel is { } fake)
        {
            fake.PreReceive = _ =>
            {
                if (onlyOnce && File.Exists(RefusedMarker))
                {
                    return null;
                }
                File.WriteAllText(RefusedMarker, "");
                return lines;
            };
            return;
        }
        var refuse = $"touch '{RefusedMarker}'\n{string.Concat(lines.Select(l => $"echo '{l}' >&2\n"))}exit 1";
        File.WriteAllText(HookPath, $"#!/bin/sh\n{(onlyOnce ? $"if [ -f '{RefusedMarker}' ]; then exit 0; fi\n" : "")}{refuse}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(HookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Origin lets pushes through again: the hook goes.
    private void LetPushesThrough()
    {
        if (RepoModel is { } fake)
        {
            fake.PreReceive = null;
            return;
        }
        File.Delete(HookPath);
    }

    // The PR is merged on GitHub from the moment a push was refused: what the node reads after the refusal.
    private void MergeOnceRefused(string head) =>
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && File.Exists(RefusedMarker))
            {
                _github.Pr = new PullRequest(63, "MERGED", head, PrUrl);
            }
            return Task.CompletedTask;
        };

    private IEnumerable<CardRow> Rings() => _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring);

    [Fact]
    public async Task AGh006PushOnAPrThatMergedMeanwhileMarksTheFlightAndTheSweepOpensTheFollowUpWithTheCommit()
    {
        var flight = await QueuedWithACommit();
        _github.Queue = null;
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        var old = Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}");
        RefuseWithGh006(flight);
        MergeOnceRefused(old);

        var ex = await Should.ThrowAsync<LegCommander.PushRefusedException>(() => _legs.PushAsync(flight, Ct));

        // Handed on, said in the refusal's way, which the sweep reads; the node's own words carry no GH006.
        ex.Way.ShouldBe(RefusalWays.HandedOn);
        ex.Message.ShouldNotContain("GH006");
        ex.Message.ShouldContain("merged meanwhile");
        ex.Message.ShouldContain("follow-up PR");
        var marked = _state.Flights().Single();
        (marked.PullRequest, marked.MergedPullRequest, marked.MergedHead).ShouldBe((null, 63, old));
        _github.Dequeued.ShouldBeEmpty();
        Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(old);
        // Nothing rings: no reviewer start is offered that would be refused again, and no push is retried by force.
        Rings().ShouldBeEmpty();

        // The last leg is done, and origin lets the branch go as it is no longer locked: the sweep's own step opens the follow-up PR.
        MarkDone("parser");
        LetPushesThrough();
        _github.OnCall = null;
        await _legs.AdvanceFlightAsync("TIA1", Ct);

        var created = _github.Created.ShouldHaveSingleItem();
        created.Branch.ShouldBe(flight.Branch);
        Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(head);
        Git(Origin, "log", "-1", "--format=%s", $"refs/heads/{flight.Branch}").ShouldBe("the parser, after it was queued");
        var opened = _state.Flights().Single();
        (opened.PullRequest, opened.MergedHead).ShouldBe((71, null));
        var card = _state.Cards().Single(c => c.Kind == LegCommander.FollowUpPrKind);
        (card.Bin, card.Link).ShouldBe((CardBin.Handled, "https://github.com/x/tianwen/pull/71"));
        card.Title.ShouldContain("#63's merge: PR #71");
        Rings().ShouldBeEmpty();

        // The follow-up merges, and the flight lands.
        _github.Pr = new PullRequest(71, "MERGED", head, "https://github.com/x/tianwen/pull/71");
        await _flights.LandAfterMergeAsync("TIA1", head, Ct);
        _state.Flights().Single().State.ShouldBe(FlightState.Landed);
    }

    [Fact]
    public async Task AGh006PushOnAPrThatIsQueuedNowDequeuesItAndPushesAgain()
    {
        var flight = await QueuedWithACommit();
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        // The queue took the PR after the node had looked (so the first look found it free), and refuses the first push; the next passes.
        var queued = _github.Queue;
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
        RefuseWithGh006(flight, onlyOnce: true);

        await _legs.PushAsync(flight, Ct);

        _github.Dequeued.ShouldBe([63]);
        Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(head);
        _state.Cards().Single(c => c.Kind == LegCommander.MergeTakenBackKind).Bin.ShouldBe(CardBin.Handled);
        Rings().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGh006RefusalThePushCannotAnswerIsRefusedWithItsTextAndTheSweepRingsOnceWithNoStartToOffer(bool progressFirst)
    {
        var flight = await QueuedWithACommit();
        _github.Queue = null;
        MarkDone("parser");
        RefuseWithGh006(flight, progressFirst: progressFirst);

        // Said in the refusal's way, with GitHub's own line wherever git printed it, never the progress line before it.
        var ex = await Should.ThrowAsync<LegCommander.PushRefusedException>(() => _legs.PushAsync(flight, Ct));
        ex.Way.ShouldBe(RefusalWays.ProtectedBranch);
        ex.Message.ShouldContain("GH006: Protected branch update failed");
        ex.Message.ShouldNotContain("Resolving deltas");

        // The sweep's unread-head review start meets the same refusal; it rings one card saying why, with no reviewer start that
        // would be refused again, and asks no more.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await _legs.AdvanceFlightAsync("TIA1", Ct);
        }
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.NextStepKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldBe($"TIA1's new head cannot be pushed: GitHub locks {flight.Branch}");
        card.Choices.ShouldBeEmpty();
        card.Detail.ShouldNotBeNull().ShouldContain("take the PR out of the queue");
        _github.Dequeued.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADequeueGitHubRefusesDuringTheSweepsPushLeavesACardThatSaysWhy()
    {
        var flight = await QueuedWithACommit();
        MarkDone("parser");
        _github.DequeueFails = "gh: not allowed";

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await _legs.AdvanceFlightAsync("TIA1", Ct);
        }

        // The refusal is the node's own, not a GH006 push it already answered: it is not skipped, and the card says why.
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.NextStepKind).ShouldHaveSingleItem();
        card.Title.ShouldContain("could not start its reviewer");
        card.Detail.ShouldNotBeNull().ShouldContain("#63 is in the merge queue");
        card.Detail.ShouldNotBeNull().ShouldContain("could not be taken out (gh: not allowed)");
        Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}").ShouldNotBe(Git(flight.Worktree, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task TheNodesOwnDequeueRaisesNoLeftTheMergeQueueCardWhenTheQueuesAreLookedAt()
    {
        var flight = await QueuedWithACommit();

        var result = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done");
        result.Error.ShouldBeNull();
        _github.Dequeued.ShouldBe([63]);

        // GitHub now reads the PR as open and out of the queue with a removal that was not a merge, as the node's own dequeue leaves it,
        // and at the pushed head, as real GitHub's headRefOid has it by the look (the head the PR was queued at is gone).
        var pushed = Git(flight.Worktree, "rev-parse", "HEAD");
        Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(pushed);
        _github.Queue = _github.Queue.ShouldNotBeNull() with { Head = pushed };
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.QueueDropped);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.MergeTakenBackKind);

        // Queued again and then let go by GitHub: that removal is GitHub's, and is carded.
        _github.Queue = _github.Queue with { Queued = true, Position = 1, RemovedAt = null, RemovedReason = null };
        await checks.LookAsync(Ct);
        _github.Queue = _github.Queue with { Queued = false, Position = null, RemovedAt = DateTimeOffset.UtcNow, RemovedReason = "checks failed" };
        await checks.LookAsync(Ct);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrChecks.QueueDropped);
    }

    // The window of DequeuedByNode (review note 1): a removal within two minutes of the node's own dequeue, either way, is the node's
    // (clock skew and the call's own time); a later or earlier one is GitHub's to card. Pinned at one minute inside and one minute
    // outside the window, around the span in which the node stamped its dequeue.
    [Fact]
    public async Task ARemovalWithinTwoMinutesOfTheNodesOwnDequeueIsTheNodesAndOneOutsideThatWindowIsGitHubs()
    {
        await QueuedWithACommit();
        var notBefore = _clock.GetUtcNow();
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done")).Error.ShouldBeNull();
        var notAfter = _clock.GetUtcNow();
        _github.Dequeued.ShouldBe([63]);

        // Inside the window, on both sides of the stamp: the node's own removal.
        _legs.DequeuedByNode("TIA1", 63, notBefore - TimeSpan.FromMinutes(1)).ShouldBeTrue();
        _legs.DequeuedByNode("TIA1", 63, notAfter + TimeSpan.FromMinutes(1)).ShouldBeTrue();
        // A GitHub drop that falls inside the window still reads as the node's (accepted: a re-queue needs a fresh review of the new
        // head first, which takes longer than the window, and QueuedAgain lets the entry go once the PR is queued again).
        _legs.DequeuedByNode("TIA1", 63, notAfter).ShouldBeTrue();
        // Outside it, either way: GitHub's.
        _legs.DequeuedByNode("TIA1", 63, notBefore - TimeSpan.FromMinutes(3)).ShouldBeFalse();
        _legs.DequeuedByNode("TIA1", 63, notAfter + TimeSpan.FromMinutes(3)).ShouldBeFalse();
        // Another PR or another flight is never the node's.
        _legs.DequeuedByNode("TIA1", 64, notAfter).ShouldBeFalse();
        _legs.DequeuedByNode("TIA2", 63, notAfter).ShouldBeFalse();

        // Queued again: the entry is let go, so a removal after that is GitHub's whatever the time.
        _legs.QueuedAgain("TIA1");
        _legs.DequeuedByNode("TIA1", 63, notAfter).ShouldBeFalse();
    }

    [Fact]
    public async Task ALegThatCommitsWhileItsPrIsQueuedTakesItOutOfTheQueueBeforeThePushAndTheCardSaysWhy()
    {
        var flight = await QueuedWithACommit();
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        var pushedWhenDequeued = new List<string>();
        _github.OnDequeued = () => pushedWhenDequeued.Add(Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}"));

        var result = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done");

        result.Error.ShouldBeNull();
        _github.Dequeued.ShouldBe([63]);
        // Out of the queue first: when GitHub was asked, origin did not hold the new commit yet.
        pushedWhenDequeued.ShouldHaveSingleItem().ShouldNotBe(head);
        Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(head);
        _github.DraftCalls.ShouldContain((63, true));
        _state.Flights().Single().Review!.State.ShouldNotBe(ReviewState.Passed);
        var card = _state.Cards().Single(c => c.Kind == LegCommander.MergeTakenBackKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.Handled, Verbs.FlightTarget("TIA1")));
        card.Title.ShouldBe("TIA1's PR #63 was taken out of the merge queue for new commits");
        card.Detail.ShouldNotBeNull().ShouldContain("merge queue locks");
    }

    [Fact]
    public async Task ADequeueGitHubRefusesRefusesThePushAndNothingIsPushed()
    {
        var flight = await QueuedWithACommit();
        var pushed = Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}");
        _github.DequeueFails = "gh: not allowed";

        var result = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done");

        var error = result.Error.ShouldNotBeNull();
        error.ShouldContain("#63 is in the merge queue");
        error.ShouldContain("could not be taken out (gh: not allowed)");
        error.ShouldContain("remove it from the queue on the PR page");
        Git(Origin, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(pushed);
        L("parser").State.ShouldBe(LegState.Flying);
        _state.Flights().Single().Review!.State.ShouldBe(ReviewState.Passed);
    }
}

/// <summary>GitHub's refusal of a push to a protected or queued branch is told by name (PIL836).</summary>
public sealed class PushRefusalTests
{
    [Theory]
    [InlineData("remote: error: GH006: Protected branch update failed for refs/heads/flight/pil819.")]
    [InlineData("remote: error: GH006: Protected branch update failed for refs/heads/flight/pil819.\nremote: error: Cannot change this locked branch because it is in a merge queue.\nTo https://github.com/SharpAstro/pilot.git\n ! [remote rejected] flight/pil819 -> flight/pil819 (protected branch hook declined)\nerror: failed to push some refs to 'https://github.com/SharpAstro/pilot.git'")]
    [InlineData("remote: error: Protected branch update failed for refs/heads/main.")]
    public void GitHubsProtectedBranchRefusalIsRecognised(string text) => PushRefusals.IsProtectedBranch(text).ShouldBeTrue();

    [Fact]
    public void GitHubsLineIsFoundAfterTheProgressGitPrintsFirst() =>
        PushRefusals.ProtectedLine("remote: Resolving deltas: 100% (3/3), completed with 2 local objects.        \r\nremote: error: GH006: Protected branch update failed for refs/heads/flight/pil819.\r\nTo https://github.com/SharpAstro/pilot.git")
            .ShouldBe("remote: error: GH006: Protected branch update failed for refs/heads/flight/pil819.");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("To origin\n ! [rejected] flight/x -> flight/x (non-fast-forward)\nerror: failed to push some refs")]
    [InlineData("fatal: unable to access 'https://github.com/SharpAstro/pilot.git/': Could not resolve host: github.com")]
    [InlineData("remote: error: GH013: Repository rule violations found for refs/heads/main.")]
    public void AnyOtherRefusalIsNot(string? text) => PushRefusals.IsProtectedBranch(text).ShouldBeFalse();
}
