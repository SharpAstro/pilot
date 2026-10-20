using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The watchtower: kept running with Remote Control on, fed the cards, sent on rounds that keep its cache warm, and rung for when it cannot run.</summary>
public sealed class WatchtowerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly NodeOptions _options = new("/tmp/n.sock", "/tmp/pilot2", MachinePrefix: "DU1");
    private readonly Watchtower _tower;

    public WatchtowerTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = new Watchtower(_state, _workers, Run, _options, _time) { Pilot = "pilot" };
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    /// <summary>The watchtower's row, as the tracker would put it; its id is the job the fake workers handed out.</summary>
    private void Row(string id, SessionState state, DateTimeOffset? lastResponse = null, long context = 10_000, string? apiError = null, DateTimeOffset? resetsAt = null) =>
        _state.PutSession(new SessionRow
        {
            Id = id,
            SessionId = id + "-0000-4000-8000-000000000000",
            Name = "du1-watchtower",
            Kind = SessionKind.Background,
            Cwd = _options.WatchtowerFolder,
            Role = SessionRole.Watchtower,
            ContextTokens = context,
            ContextWindow = 200_000,
            State = state,
            SpendWeighted = 0,
            ApiError = apiError,
            ApiErrorResetsAt = resetsAt,
            LastResponseAt = lastResponse ?? _time.GetUtcNow(),
            StartedAt = Start,
        });

    private CardRow[] Open() => _state.Cards(openOnly: true);

    [Fact]
    public async Task OneIsStartedWhenNoneRunsWithRemoteControlInItsOwnFolderBriefedFromTheState()
    {
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");

        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        var start = _workers.Started.ShouldHaveSingleItem();
        (start.Name, start.RepoPath, start.Plane, start.RemoteControl).ShouldBe(("du1-watchtower", _options.WatchtowerFolder, (string?)null, true));
        start.Brief.ShouldContain("You are du1-watchtower");
        start.Brief.ShouldContain("c1 [ring] about flight/TIA1/parser: TIA1/parser failed");
        _state.Cards().Single(c => c.Bin == CardBin.Handled).Title.ShouldBe("Started a fresh watchtower: none was running");

        // In its brief already: the card is not said to it again once it runs.
        Row("job00001", SessionState.Idle);
        await _tower.LookAsync(Ct);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task NewCardsAreSaidToItOnceButNotItsOwnNorHandledLines()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Working);

        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge", choices:
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["cafe"] }]);
        _state.RaiseCard(CardBin.Handled, "worker-nudged", "flight/TIA1/parser", "nudged");
        _state.RaiseCard(CardBin.ForYou, Watchtower.RaisedKind, "flight/TIA1", "raised by the watchtower");
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/job00001", "node"));
        say.Args[0].ShouldContain("A card opened:");
        say.Args[0].ShouldContain("TIA1's PR #7 is ready to merge. Choices: 1 Merge.");
        say.Args[0].ShouldNotContain("raised by the watchtower");
    }

    [Fact]
    public async Task IdleForFiftyMinutesItIsSentOnARoundThatKeepsItsCacheWarm()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle, lastResponse: Start);

        _time.Advance(TimeSpan.FromMinutes(49));
        await _tower.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));
        await _tower.LookAsync(Ct);
        _said.ShouldHaveSingleItem().Args.ShouldBe([Watchtower.RoundText]);

        // Its answer to the round is a response like any other: the next round is fifty minutes after it.
        await _tower.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        Row("job00001", SessionState.Idle, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(50));
        await _tower.LookAsync(Ct);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PastItsContextLimitAnIdleOneIsReplacedByAFreshOne()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Working, context: 150_000);
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);

        Row("job00001", SessionState.Idle, context: 150_000);
        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldBe(["job00001"]);
        _workers.Started.Count.ShouldBe(2);
        _state.Cards().First(c => c.Bin == CardBin.Handled).Title.ShouldBe("Started a fresh watchtower: its context reached 75% of its window");
    }

    [Fact]
    public async Task OneThatEndedIsStartedAgainButNotWhileAFreshOneIsStillShowingUp()
    {
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00001", SessionState.Ended);
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(2);
        _state.Cards().First(c => c.Bin == CardBin.Handled).Title.ShouldBe("Started a fresh watchtower: the last one ended");
    }

    [Fact]
    public async Task AnUntrustedFolderRingsWithTheFixAndIsTriedAgainLater()
    {
        _workers.Fail = new FlightException("claude --bg did not start du1-watchtower (1): Workspace not trusted. Run `claude` in /tmp/pilot2/watchtower once");

        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, Watchtower.Down, Verbs.WatchtowerTarget));
        card.Detail.ShouldNotBeNull().ShouldContain($"Run `claude` in {_options.WatchtowerFolder} once");
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.RestartWatchtower);

        _workers.Fail = null;
        _time.Advance(TimeSpan.FromMinutes(10));
        await _tower.LookAsync(Ct);
        _workers.Started.ShouldHaveSingleItem();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task OneTheUsageLimitStoppedIsResumedAfterTheReset()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle, apiError: "rate_limit", resetsAt: Start.AddHours(1));

        await _tower.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(Watchtower.Stopped);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2));
        await _tower.LookAsync(Ct);

        _said.ShouldHaveSingleItem().Args[0].ShouldContain("limit that stopped you has reset");
        Open().ShouldBeEmpty();
        _workers.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OneAlreadyRunningWhenTheNodeStartsIsKeptAndNotToldTheCardsAgain()
    {
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");
        Row("a1b2c3d4", SessionState.Idle);

        await _tower.LookAsync(Ct);

        (_workers.Started.Count, _said.Count).ShouldBe((0, 0));
        _tower.TellTarget().ShouldBe(("session/a1b2c3d4", (string?)null));
    }
}

/// <summary>The watchtower's verbs on the node's route, and the controller's gates it cannot pass.</summary>
public sealed class WatchtowerRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMergeOrALandingFromTheWatchtowerIsRefusedACardsIncluded()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var watchtower = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge", choices:
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["cafe0000"] }]);

        var answered = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = "card/c1", Args = ["1"], Who = watchtower }, Ct);
        var landed = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Land, Target = "flight/TIA1", Who = watchtower }, Ct);

        answered.Response.ShouldNotBeNull().Error.ShouldBe("Merge was refused: merge is the controller's, not the watchtower's: leave its card open for them");
        landed.Response.ShouldNotBeNull().Error.ShouldStartWith("land is the controller's");
        node.State.Cards(openOnly: true).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ARaiseIsACardFromWhoeverRaisedItAndATellNeedsAWatchtower()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["ring", "TIA1 forks: SQLite or JSON?", "both work"], Who = "du1-watchtower" }, Ct))
            .Success.ShouldBeTrue();
        var bad = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = "node", Args = ["loud", "x"], Who = "tests" }, Ct);
        var tell = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = ["hello"], Who = "tests" }, Ct);

        var card = node.State.Cards().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Title, card.Detail).ShouldBe((CardBin.Ring, Watchtower.RaisedKind, "TIA1 forks: SQLite or JSON?", "both work (from du1-watchtower)"));
        bad.Response.ShouldNotBeNull().Error.ShouldStartWith("raise takes ring, for-you or handled");
        tell.Response.ShouldNotBeNull().Error.ShouldBe("this node keeps no watchtower (--no-watchtower)");
    }
}
