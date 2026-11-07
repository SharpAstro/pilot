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
