using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Every card about a leg's worker says in its title what is wrong, in its first line why it needs someone, and each choice what it does in
/// this case (PIL474); one that needs nobody is a Handled line, with no card open. The leg-failed, push-blocked, review and merge cards are
/// pinned in <c>LegTests</c>, <c>PushBlockedTests</c>, <c>ReviewTests</c> and <c>MergeArmTests</c>, the budget one in <c>SignalTests</c>.
/// </summary>
public sealed class LegCardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly LegWatch _watch;
    private string? _refuse;

    public LegCardTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _watch = new LegWatch(_state, (command, _) => Task.FromResult(_state.Complete(command, _refuse)), _time) { Pilot = "pilot" };
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

    private void Worker(SessionState state, string? apiError = null, DateTimeOffset? resetsAt = null, string? waitingFor = null, string? asking = null) =>
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
            Asking = asking,
            SpendWeighted = 0,
            ApiError = apiError,
            ApiErrorResetsAt = resetsAt,
            LastResponseAt = Start,
            StartedAt = Start,
        });

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private CardRow[] Handled() => [.. _state.Cards().Where(c => c.Bin == CardBin.Handled)];

    [Fact]
    public async Task AUsageLimitStopTheNodeResumesItselfIsAHandledLineAndNoCard()
    {
        Worker(SessionState.Idle, apiError: "rate_limit", resetsAt: Start.AddHours(2));

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        Open().ShouldBeEmpty();
        var line = Handled().ShouldHaveSingleItem();
        (line.Kind, line.Subject).ShouldBe((LegWatch.LimitWait, "flight/TIA1/parser"));
        line.Title.ShouldStartWith("TIA1/parser's worker hit the usage limit; the node resumes it at ");
    }

    [Fact]
    public async Task AStopWithNoResetTimeNamesTheErrorAndOffersATellAndAFreshWorker()
    {
        Worker(SessionState.Idle, apiError: "overloaded");

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldBe("TIA1/parser's worker was stopped by the API: overloaded");
        card.Detail.ShouldStartWith("the API gave no time the stop clears");
        card.Choices.Select(c => c.Label).ShouldBe(["Tell du1-tia1-parser to carry on with parser", "Start a fresh worker on parser (the last one was stopped by the API)"]);
    }

    [Fact]
    public async Task AnIdleWorkerPastItsNudgesNamesHowLongAndHowManyStepsAreTicked()
    {
        Worker(SessionState.Idle);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldBe("TIA1/parser's worker has been idle 10 min with 1 of 2 steps ticked");
        card.Detail!.ShouldContain("idle with room in its context");
        card.Detail!.ShouldContain("nudged 2 times with no answer, and the node nudges no more");
        card.Choices.Select(c => c.Label).ShouldBe(["Nudge du1-tia1-parser to finish parser or say why not", "Start a fresh worker on parser (the last one went idle with the leg unfinished)"]);
    }

    [Fact]
    public async Task ANudgeThatCannotBeDeliveredNamesTheReasonInItsTitle()
    {
        Worker(SessionState.Ended);
        _refuse = "the folder is gone";
        _time.Advance(TimeSpan.FromMinutes(10));

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldBe("TIA1/parser's worker has been idle 10 min with 1 of 2 steps ticked, and could not be nudged: the folder is gone");
        card.Detail.ShouldEndWith("the node tried to nudge it: the folder is gone");
        card.Detail.ShouldStartWith("idle with room in its context");
    }

    [Fact]
    public async Task AWaitingWorkerNamesWhatItAsksAndItsOnlyChoiceIsAFreshWorker()
    {
        Worker(SessionState.Waiting, waitingFor: "permission to run rm -rf build");

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldBe("TIA1/parser's worker waits on you for permission to run rm -rf build");
        card.Detail.ShouldStartWith("permission to run rm -rf build; the worker cannot go on until it is answered in its own session");
        card.Choices.Select(c => c.Label).ShouldBe(["Start a fresh worker on parser (the last one waited on you)"]);
    }

    [Theory]
    [InlineData("Bash: git push origin main", "TIA1/parser's worker wants to run: git push origin main (Bash)")]
    [InlineData("Edit: src/Parser.cs", "TIA1/parser's worker wants to edit src/Parser.cs (Edit)")]
    [InlineData("Read: src/Parser.cs", "TIA1/parser's worker wants to read src/Parser.cs (Read)")]
    [InlineData("EnterWorktree: /r/other", "TIA1/parser's worker wants to switch its folder to /r/other (EnterWorktree)")]
    [InlineData("EnterWorktree: /r/w", "TIA1/parser's worker wants to switch its folder to its own plane (EnterWorktree)")]
    public async Task AWaitingWorkerWithAKnownPendingCallSaysWhatItWantsAndWhatEachChoiceDoes(string asking, string title)
    {
        Worker(SessionState.Waiting, waitingFor: "permission to use Tool", asking: asking);
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start,
        });

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldBe(title);
        card.Detail.ShouldNotBeNull().ShouldStartWith(title["TIA1/parser's worker ".Length..].Insert(0, "it ") + ";");
        card.Detail.ShouldContain("answered in its own session");
        var summary = card.Summary.ShouldNotBeNull();
        summary.ShouldContain("Answering in its own session lets this worker go on with the call");
        summary.ShouldContain("stops this one and starts another on the leg from the branch");
        summary.ShouldContain(card.Choices.Single().Label);
        $"{card.Title}{card.Detail}{card.Summary}".ShouldNotContain("—");
        $"{card.Title}{card.Detail}{card.Summary}".ShouldNotContain("--");
    }

    [Fact]
    public async Task AWaitingWorkersRealQuestionKeepsItsOwnWordsEvenWithACallPending()
    {
        Worker(SessionState.Waiting, waitingFor: "Which branch should I use?", asking: "Bash: git checkout");

        await _watch.LookAsync(Ct);

        Open().ShouldHaveSingleItem().Title.ShouldBe("TIA1/parser's worker waits on you for Which branch should I use?");
    }

    [Fact]
    public async Task AFailedWorkerNamesTheApiErrorOrTheStepsLeft()
    {
        Worker(SessionState.Failed, apiError: "invalid_request");

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldBe("TIA1/parser's worker failed: the API said invalid_request");
        card.Choices.Select(c => c.Label).ShouldBe(["Tell du1-tia1-parser to carry on with parser", "Start a fresh worker on parser (the last one failed)"]);
    }

    [Fact]
    public async Task AWorkerThatNeverShowedUpSaysHowLongAgoItStartedAndOffersOnlyAFreshWorker()
    {
        // The first look stops and starts it once more (the first-turn ladder); the second finds it still gone.
        _time.Advance(TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(5));

        await _watch.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        card.Title.ShouldBe("TIA1/parser's worker du1-tia1-parser never showed up, 10 min after it started");
        card.Detail.ShouldStartWith("started as job a1b2c3d4");
        card.Choices.Select(c => c.Label).ShouldBe(["Start a fresh worker on parser (the last one never showed up)"]);
    }
}
