using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// While a merge command runs for a flight, its PR-ready card shows the Merge choice as Merging with what it waits for, a second
/// press is answered as in progress (not refused) while the command drives the entry, and the card ends armed, queued or as it was.
/// </summary>
public sealed class MergingCardTests : ReviewTestsBase
{
    private static readonly TimeSpan Stuck = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan PastStuck = TimeSpan.FromMilliseconds(700);

    private LegCommander Commander()
    {
        var git = new GitCli();
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        return new LegCommander(_state, _workers, new FakeChecks(), _github, git, new Planes(git), _flights, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            StuckAfter = Stuck,
        };
    }

    /// <summary>A flight whose legs are done and review passed, its PR open, and the PR-ready card offering Merge (as checks were last seen: <paramref name="status"/>).</summary>
    private (FlightRow Flight, string Head, CardRow Card) Ready(string code, int pr, string? status = ChoiceStatus.Passing)
    {
        var head = $"cafe{pr:0000}aaaabbbbccccdddd";
        var url = $"https://github.com/x/tianwen/pull/{pr}";
        var flight = new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = Repo, Issue = pr, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = Path.Combine(_root, "plane-" + code),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = pr, PullRequestUrl = url,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = $"du1-{code.ToLowerInvariant()}-review", StartedAt = DateTimeOffset.UtcNow },
        };
        _state.PutFlight(flight);
        _state.PutLeg(new LegRow { Flight = code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        _github.PrOf[flight.Branch] = new PullRequest(pr, "OPEN", head, url);
        var card = _state.RaiseCard(CardBin.Ring, LegCommander.PrReady, Verbs.FlightTarget(code), $"{code}'s PR #{pr} is ready to merge", "Reviewed: fine.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = [head], Status = status }], link: url);
        return (flight, head, card);
    }

    private CardChoice MergeChoice(CardRow card) => _state.Cards().Single(c => c.Id == card.Id).Choices.Single(c => c.Verb == Verbs.Merge);

    private static Task<CommandResult> Press(LegCommander legs, string code, string head) =>
        legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = [head], Who = "controller" }, Ct);

    private (TaskCompletionSource Open, TaskCompletionSource Reached) Hold(string name)
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.OnCall = call =>
        {
            if (call != name)
            {
                return Task.CompletedTask;
            }
            reached.TrySetResult();
            return Task.WhenAny(open.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        };
        return (open, reached);
    }

    private static async Task Reaches(TaskCompletionSource reached) =>
        (await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(20), Ct))).ShouldBe(reached.Task, "the call was not reached");

    private async Task<CardChoice> ShowsMerging(CardRow card)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (MergeChoice(card) is { Status: ChoiceStatus.Merging } choice)
            {
                return choice;
            }
            await Task.Delay(10, Ct);
        }
        throw new ShouldAssertException($"the card never showed Merging: {MergeChoice(card).Status}");
    }

    [Fact]
    public async Task AMergeWaitingItsTurnBehindAnotherFlightShowsMergingAndAnotherPressIsInProgressPastTheStuckLimit()
    {
        var (a, headA, _) = Ready("TIA1", 63);
        var (b, headB, cardB) = Ready("TIA2", 64);
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        var legs = Commander();
        var (open, reached) = Hold(nameof(IGitHub.MergeAsync));
        var first = Press(legs, a.Code, headA);
        await Reaches(reached);
        var waiting = Press(legs, b.Code, headB);

        var shown = await ShowsMerging(cardB);
        shown.Note.ShouldBe("waiting its turn behind TIA1");
        var before = _state.Cards().Single(c => c.Id == cardB.Id);
        await Task.Delay(PastStuck, Ct);

        var again = await Press(legs, b.Code, headB);

        again.Ok.ShouldBeTrue(again.Error);
        again.Way.ShouldBe(MergeWays.InProgress);
        _state.Cards().Single(c => c.Id == cardB.Id).ShouldBe(before);
        open.SetResult();
        (await first).Ok.ShouldBeTrue();
        (await waiting).Ok.ShouldBeTrue();
        _github.Merged.Select(m => m.Number).ShouldBe([63, 64]);
    }

    [Fact]
    public async Task AMergeWaitingForChecksShowsHowManyArePendingAndEndsArmed()
    {
        var (flight, head, card) = Ready("TIA1", 63, ChoiceStatus.Pending);
        _github.SummaryOf[63] = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        var legs = Commander();
        var (open, reached) = Hold(nameof(IGitHub.MergeQueueAsync));
        var merge = Press(legs, flight.Code, head);
        await Reaches(reached);

        (await ShowsMerging(card)).Note.ShouldBe("waiting for checks (2 pending)");
        await Task.Delay(PastStuck, Ct);
        var again = await Press(legs, flight.Code, head);

        again.Ok.ShouldBeTrue(again.Error);
        again.Way.ShouldBe(MergeWays.InProgress);
        MergeChoice(card).Status.ShouldBe(ChoiceStatus.Merging);
        open.SetResult();
        (await merge).Way.ShouldBe(MergeWays.Armed);
        MergeChoice(card).Status.ShouldBe(ChoiceStatus.Armed);
        _state.Cards(openOnly: true).ShouldContain(c => c.Id == card.Id);
    }

    [Fact]
    public async Task AMergeThatJoinsTheQueueLeavesTheCardQueued()
    {
        var (flight, head, card) = Ready("TIA1", 63);
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        _github.Queues = true;

        var result = await Press(Commander(), flight.Code, head);

        result.Way.ShouldBe(MergeWays.Queued);
        MergeChoice(card).Status.ShouldBe(ChoiceStatus.Queued);
    }

    [Fact]
    public async Task ARefusedMergePutsBackTheMergeTheCardOfferedAndSaysTheRefusalOnce()
    {
        var (flight, head, card) = Ready("TIA1", 63, ChoiceStatus.Passing);
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", "https://github.com/x/tianwen/actions/runs/1");

        var result = await Press(Commander(), flight.Code, head);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("checks fail");
        var choice = MergeChoice(card);
        (choice.Status, choice.Note).ShouldBe((ChoiceStatus.Passing, null));
        _state.Cards(openOnly: true).Count(c => c.Id == card.Id).ShouldBe(1);
    }

    [Fact]
    public async Task AnEntryThatNothingDrivesAnyMoreIsRefusedAsBeingMergedAlready()
    {
        // Held in the merge call itself, unmoved past the limit: a call that never returns is stuck, not waiting for a turn or checks.
        var (flight, head, _) = Ready("TIA1", 63);
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        var legs = Commander();
        var (open, reached) = Hold(nameof(IGitHub.MergeAsync));
        var merge = Press(legs, flight.Code, head);
        await Reaches(reached);
        await Task.Delay(PastStuck, Ct);

        var again = await Press(legs, flight.Code, head);

        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain("is being merged already");
        open.SetResult();
        await merge;
    }
}
