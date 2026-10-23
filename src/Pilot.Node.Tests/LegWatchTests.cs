using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The leg watch: no flying leg's worker stalls without the node resuming it, nudging it, or ringing for it.</summary>
public sealed class LegWatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly List<CommandRequest> _said = [];
    private readonly LegWatch _watch;

    /// <summary>Why the next say is refused; null to deliver it.</summary>
    private string? _refuse;

    public LegWatchTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _watch = new LegWatch(_state, Run, _time) { Pilot = "pilot --node-socket \"/tmp/n.sock\"" };
        _state.PutLeg(new LegRow
        {
            Flight = "TIA1",
            Name = "parser",
            Order = 1,
            Goal = "Parse the ephemeris",
            DoneWhen = "The parser tests pass",
            Checklist = [new CheckItem { Text = "Write it", Done = true }, new CheckItem { Text = "Test it" }],
            State = LegState.Flying,
            Worker = "du1-tia1-parser",
            WorkerJob = "a1b2c3d4",
            StartedAt = Start,
        });
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, _refuse));
    }

    private void Worker(SessionState state, DateTimeOffset? lastResponse = null, string? apiError = null, DateTimeOffset? resetsAt = null, string? waitingFor = null) =>
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4",
            SessionId = "a1b2c3d4-0000-4000-8000-000000000000",
            Name = "du1-tia1-parser",
            Kind = SessionKind.Background,
            Cwd = "/work/tia1",
            Role = SessionRole.Worker,
            Flight = "TIA1",
            Leg = "parser",
            ContextTokens = 0,
            ContextWindow = 200_000,
            State = state,
            WaitingFor = waitingFor,
            SpendWeighted = 0,
            ApiError = apiError,
            ApiErrorResetsAt = resetsAt,
            LastResponseAt = lastResponse ?? Start,
            StartedAt = Start,
        });

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private CardRow[] Handled() => [.. _state.Cards().Where(c => c.Bin == CardBin.Handled)];

    [Fact]
    public async Task AWorkerTheUsageLimitStoppedIsResumedOnlyOnceItsLimitHasReset()
    {
        Worker(SessionState.Idle, apiError: "rate_limit", resetsAt: Start.AddHours(2));

        await _watch.LookAsync(Ct);
        _said.ShouldBeEmpty();
        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.ForYou, LegWatch.Stopped, "flight/TIA1/parser"));
        card.Choices.Select(c => c.Label).ShouldBe(["Resume now", "Go around"]);

        _time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3));
        await _watch.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/a1b2c3d4", "node"));
        say.Args[0].ShouldContain("limit that stopped you has reset");
        Open().ShouldBeEmpty();
        Handled().ShouldHaveSingleItem().Kind.ShouldBe("worker-resumed");

        // Still stopped on the same reset a look later: the resume did not take, so it rings rather than resuming again.
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        (Open().ShouldHaveSingleItem().Bin, Open()[0].Kind).ShouldBe((CardBin.Ring, LegWatch.Stopped));
    }

    [Fact]
    public async Task AStopWithNoResetTimeRingsAtOnce()
    {
        Worker(SessionState.Idle, apiError: "overloaded");

        await _watch.LookAsync(Ct);

        _said.ShouldBeEmpty();
        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.Ring, LegWatch.Stopped));
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Resume", Verbs.Say), ("Go around", Verbs.GoAround)]);
    }

    [Fact]
    public async Task AnIdleWorkerIsLeftItsGraceThenNudgedTwiceThenRungFor()
    {
        Worker(SessionState.Idle);

        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(3));
        await _watch.LookAsync(Ct);
        var nudge = _said.ShouldHaveSingleItem().Args[0];
        nudge.ShouldContain("1 of 2 steps are not ticked yet");
        nudge.ShouldContain("pilot --node-socket \"/tmp/n.sock\" leg finish done");

        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        Handled().Length.ShouldBe(2);
        Open().ShouldBeEmpty();

        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.Ring, LegWatch.Idle));
        card.Choices.Select(c => c.Label).ShouldBe(["Nudge", "Go around"]);
    }

    [Fact]
    public async Task ANudgeTheNodeCannotDeliverRings()
    {
        Worker(SessionState.Ended);
        _refuse = "the folder is gone";
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Detail).ShouldBe((LegWatch.Idle, "the folder is gone"));
    }

    [Fact]
    public async Task AWaitingWorkerRingsWithItsQuestionAndTheCardClosesWhenItWorksAgain()
    {
        Worker(SessionState.Waiting, waitingFor: "permission to run rm -rf build");

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Detail).ShouldBe((LegWatch.Waiting, "permission to run rm -rf build"));
        card.Choices.Select(c => c.Label).ShouldBe(["Go around"]);

        Worker(SessionState.Working);
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();
        _state.Cards().Single(c => c.Id == card.Id).Outcome.ShouldBe("working again");
    }

    [Fact]
    public async Task AFailedWorkerAndOneThatNeverShowedUpRing()
    {
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.Missing);

        Worker(SessionState.Failed);
        await _watch.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegWatch.Failed);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReviewerThatStopsWithoutItsVerdictRingsForAFreshReview()
    {
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start,
            Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "du1-tia1-review", ReviewerJob = "a1b2c3d4", StartedAt = Start },
        });
        Worker(SessionState.Working);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Subject, card.Detail).ShouldBe((LegWatch.ReviewStalled, "flight/TIA1", "it stopped 4 min ago without its verdict"));
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.StartReview);
    }

    [Fact]
    public async Task AnyPrsReviewerIsWatchedAsAFlightsIs()
    {
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "4f2c9a01d6e8",
            ClonePath = "/r", Worktree = "/r/.claude/worktrees/review-widget-7", Reviewer = "du1-review-widget-7", ReviewerJob = "a1b2c3d4",
            State = PrReviewState.Reviewing, StartedAt = Start,
        });
        Worker(SessionState.Working);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);
        Open().ShouldBeEmpty();

        Worker(SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Subject, card.Title).ShouldBe((LegWatch.ReviewStalled, "pr/acme/widget#7", "acme/widget#7's review stalled"));
        var again = card.Choices.ShouldHaveSingleItem();
        (again.Verb, again.Target).ShouldBe((Verbs.ReviewPr, "pr/acme/widget#7"));
    }

    [Fact]
    public async Task LegsNotFlyingAreNotWatched()
    {
        var leg = _state.Legs().Single();
        _state.PutLeg(leg with { State = LegState.Done });
        Worker(SessionState.Idle);
        _time.Advance(TimeSpan.FromHours(1));

        await _watch.LookAsync(Ct);

        (_said.Count, Open().Length).ShouldBe((0, 0));
    }
}
