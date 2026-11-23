using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1285: with autopilot on, the node wakes the watchtower on a stalled flight, a peer going or coming and a repeat; off, never.</summary>
public sealed class AutopilotWakeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FailureLedger _failures = new();
    private readonly AutopilotWakes _wakes;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-wake-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;
    private bool _on = true;

    public AutopilotWakeTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _wakes = new AutopilotWakes(_state, new RepeatDetector(_state, _failures, _time), _time) { On = () => _on, Stall = () => TimeSpan.FromMinutes(60) };
        _tower = new Watchtower(_state, _workers, Run, _options, _time) { Pilot = "pilot", Wakes = _wakes, Version = "2.0.1" };
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, StartedAt = Start });
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            TestDirs.DeleteTree(_data);
        }
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    /// <summary>A running watchtower that has been told what there was.</summary>
    private async Task RunningTowerAsync()
    {
        await _tower.LookAsync(Ct);
        Row();
        await _tower.LookAsync(Ct);
        _said.Clear();
    }

    private void Row() =>
        _state.PutSession(new SessionRow
        {
            Id = "job00001", SessionId = "job00001-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background, Cwd = _options.WatchtowerFolder,
            Role = SessionRole.Watchtower, ContextTokens = 10_000, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, LastResponseAt = _time.GetUtcNow(), StartedAt = Start,
        });

    private List<string> Says() => [.. _said.Where(c => c.Verb == Verbs.Say).Select(c => string.Join(' ', c.Args))];

    [Fact]
    public async Task AFlightWithNoProgressForTheBoundWakesTheWatchtowerOnceWithTheFlightAndWhatItWaitsOn()
    {
        await RunningTowerAsync();

        _time.Advance(TimeSpan.FromMinutes(59));
        Row();
        await _tower.LookAsync(Ct);
        Says().ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(2));
        Row();
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(10));
        Row();
        await _tower.LookAsync(Ct);

        var said = Says().ShouldHaveSingleItem();
        said.ShouldContain("TIA9");
        said.ShouldContain("no progress");
        said.ShouldContain("leg parser flying");
        _said.Single(c => c.Verb == Verbs.Say).Who.ShouldBe(LegWatch.Who);
        _state.Cards().ShouldNotContain(c => c.Bin != CardBin.Handled);
    }

    [Fact]
    public async Task AJobOfTheFlightMovingItIsNoStall()
    {
        await RunningTowerAsync();
        _time.Advance(TimeSpan.FromMinutes(50));
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = "/r/w", Flight = "TIA9", Leg = "parser", State = JobState.Running, LogPath = "l", Timeout = TimeSpan.FromHours(1),
            StartedAt = _time.GetUtcNow(),
        });
        _time.Advance(TimeSpan.FromMinutes(20));
        Row();

        await _tower.LookAsync(Ct);

        Says().ShouldBeEmpty();
    }

    [Fact]
    public async Task APeerThatStopsAnsweringAndOneThatAnswersAgainEachWakeItOnce()
    {
        await RunningTowerAsync();
        _wakes.NotePeer("SE1", true);
        await _tower.LookAsync(Ct);
        Says().ShouldBeEmpty();

        _wakes.NotePeer("SE1", false);
        _wakes.NotePeer("SE1", false);
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);
        Says().ShouldHaveSingleItem().ShouldContain("SE1 stopped answering");

        _wakes.NotePeer("SE1", true);
        _wakes.NotePeer("SE1", true);
        await _tower.LookAsync(Ct);
        Says().Count.ShouldBe(2);
        Says()[1].ShouldContain("SE1 answers again");
    }

    [Fact]
    public async Task ASecondCardOfTheSameKindIsToldAsFindTheCauseNotAsAFreshCard()
    {
        var first = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "TIA9's PR is red", "run 1");
        await RunningTowerAsync();
        _state.CloseCard(first.Id, "pressed", "owner");
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "TIA9's PR is red", "run 2");
        Row();

        await _tower.LookAsync(Ct);

        var said = Says().ShouldHaveSingleItem();
        said.ShouldContain("Find the cause, do not press again");
        said.ShouldContain(first.Id);
        said.ShouldContain(second.Id);
        said.ShouldContain("run 1");
        said.ShouldContain("run 2");
        _state.Cards().Single(c => c.Kind == CardKinds.RepeatTask).Bin.ShouldBe(CardBin.Handled);
    }

    [Fact]
    public async Task TheSameFailureSignatureTwiceOnAFlightTellsTheWatchtowerToFindTheCause()
    {
        await RunningTowerAsync();
        FailureRecord Of(string head, DateTimeOffset at) => new("tianwen", 3, "TIA9", head, FailureWhere.Ci, 0, "T.Test", "boom", "test:T.Test", "", 0, at);
        _failures.Add(Of("h1", Start));
        _failures.Add(Of("h2", Start.AddMinutes(30)));
        _time.Advance(TimeSpan.FromMinutes(31));
        Row();

        await _tower.LookAsync(Ct);

        var said = Says().ShouldHaveSingleItem();
        said.ShouldContain("Find the cause, do not press again");
        said.ShouldContain("h1");
        said.ShouldContain("h2");
    }

    [Fact]
    public async Task AJobStillRunningPastTheBoundIsWorkGoingOnNotAStall()
    {
        await RunningTowerAsync();
        _state.PutJob(new JobRow
        {
            Id = "j2", Command = "dotnet test", Cwd = "/r/w", Flight = "TIA9", Leg = "parser", State = JobState.Running, LogPath = "l", Timeout = TimeSpan.FromHours(3),
            StartedAt = _time.GetUtcNow(),
        });
        _time.Advance(TimeSpan.FromMinutes(90));
        Row();

        await _tower.LookAsync(Ct);

        Says().ShouldBeEmpty();
    }

    private Watchtower RestartedTower(AutopilotWakes wakes) =>
        new(_state, _workers, Run, _options, _time) { Pilot = "pilot", Wakes = wakes, Version = "2.0.1" };

    [Fact]
    public async Task ATaskNotYetToldIsToldAfterARestartAndOnceToldNotAgainAfterAnother()
    {
        var first = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "TIA9's PR is red", "run 1");
        _state.CloseCard(first.Id, "pressed", "owner");
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "TIA9's PR is red", "run 2");
        // The node found the pair and made the task, then went down before it said anything: its memory of the wake went with it.
        _wakes.Look();
        var task = _state.Cards().Single(c => c.Kind == CardKinds.RepeatTask);
        _wakes.Covered.ShouldContain(second.Id);

        var wakes = new AutopilotWakes(_state, new RepeatDetector(_state, _failures, _time), _time) { On = () => _on, Stall = () => TimeSpan.FromMinutes(60) };
        var tower = RestartedTower(wakes);
        await tower.LookAsync(Ct);
        Row();
        await tower.LookAsync(Ct);

        var said = Says().ShouldHaveSingleItem();
        said.ShouldContain("Find the cause, do not press again");
        said.ShouldContain(task.Id);
        _said.Clear();

        // The next node's wake is not made again: the task was told, and that is kept with the tells.
        var third = new AutopilotWakes(_state, new RepeatDetector(_state, _failures, _time), _time) { On = () => _on, Stall = () => TimeSpan.FromMinutes(60) };
        var after = RestartedTower(third);
        await after.LookAsync(Ct);
        Row();
        await after.LookAsync(Ct);
        third.Pending.ShouldBeEmpty();
        Says().ShouldBeEmpty();
    }

    [Fact]
    public async Task AStallToldBeforeARestartIsNotToldAgainAfterItButANewStallIs()
    {
        await RunningTowerAsync();
        _time.Advance(TimeSpan.FromMinutes(61));
        Row();
        await _tower.LookAsync(Ct);
        Says().ShouldHaveSingleItem().ShouldContain("no progress");
        _said.Clear();

        var wakes = new AutopilotWakes(_state, new RepeatDetector(_state, _failures, _time), _time) { On = () => _on, Stall = () => TimeSpan.FromMinutes(60) };
        var tower = RestartedTower(wakes);
        _time.Advance(TimeSpan.FromMinutes(5));
        Row();
        await tower.LookAsync(Ct);
        wakes.Pending.ShouldBeEmpty();
        Says().ShouldBeEmpty();

        // The flight moves, then stalls again: a new stall, told once more.
        _state.PutLeg(_state.Legs().Single() with { StartedAt = _time.GetUtcNow() });
        _time.Advance(TimeSpan.FromMinutes(61));
        Row();
        await tower.LookAsync(Ct);
        Says().ShouldHaveSingleItem().ShouldContain("no progress");
    }

    [Fact]
    public void ASecondCardWhoseWakeWasNeverSaidIsToldAsAnyCardOnceTheSwitchIsOff()
    {
        var first = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "TIA9's PR is red", "run 1");
        _state.CloseCard(first.Id, "pressed", "owner");
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "TIA9's PR is red", "run 2");
        _wakes.Look();
        _wakes.Covered.ShouldContain(second.Id);
        _on = false;

        _tower.Untold().ShouldContain(c => c.Id == second.Id);
    }

    [Fact]
    public async Task WithAutopilotOffNothingIsWokenNothingIsRaisedAndTheSecondCardIsToldAsAnyCard()
    {
        _on = false;
        await RunningTowerAsync();
        _wakes.NotePeer("SE1", true);
        _wakes.NotePeer("SE1", false);
        _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "red");
        _state.RaiseCard(CardBin.Ring, "pr-red", "flight/TIA9", "red");
        _time.Advance(TimeSpan.FromHours(3));
        Row();

        await _tower.LookAsync(Ct);

        _wakes.Pending.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == CardKinds.RepeatTask);
        Says().ShouldHaveSingleItem().ShouldNotContain("no progress");
    }
}
