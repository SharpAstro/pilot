using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The watchtower kept live: by a flight in the air, or on a flightless node by a connected UI; else stopped once quiet.</summary>
public sealed class WatchtowerLiveTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly PageViews _views = new();
    private readonly List<CommandRequest> _said = [];
    private readonly List<CommandRequest> _ends = [];
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-watchtower-live-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;

    public WatchtowerLiveTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = new Watchtower(_state, _workers, Run, _options, _time) { Pilot = "pilot", UiConnected = () => _views.Any };
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        (command.Verb == Verbs.EndSession ? _ends : _said).Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    private void Flight(FlightState flight = FlightState.Flying) =>
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = flight, CreatedAt = Start, LandedAt = flight == FlightState.Landed ? Start : null,
        });

    // A live row is one the node started: its brief is recorded, so it is kept and not replaced on a changed brief.
    private void Row(string id, SessionState state)
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(_tower.BriefFile, Briefs.WatchtowerFingerprint("du1-watchtower", "pilot", _tower.Models.Model(RoleModels.Watchtower), _options.Machine));
        PutRow(id, state);
    }

    private void PutRow(string id, SessionState state) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background,
            Cwd = _options.WatchtowerFolder, Role = SessionRole.Watchtower, ContextTokens = 10_000, ContextWindow = 200_000,
            State = state, SpendWeighted = 0, LastResponseAt = _time.GetUtcNow(), StartedAt = Start,
        });

    private async Task LookPast()
    {
        await _tower.LookAsync(Ct);
        _time.Advance(_tower.QuietFor + TimeSpan.FromMinutes(1));
        await _tower.LookAsync(Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFlightInTheAirKeepsAnIdleWatchtowerPastQuietForWithAndWithoutAUi(bool ui)
    {
        Flight();
        Row("job00001", SessionState.Idle);
        var client = new object();
        if (ui)
        {
            _views.Say(client, "cards");
        }

        await LookPast();

        _ends.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightlessNodeWithAUiKeepsItAndStartsOneWhenNoneRuns()
    {
        Flight(FlightState.Landed);
        var client = new object();
        _views.Say(client, "cards");

        await _tower.LookAsync(Ct);
        _workers.Started.ShouldHaveSingleItem();

        Row("job00001", SessionState.Idle);
        await LookPast();
        _ends.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheSameNodeWithNoUiStopsItAfterQuietForWithTheQuietReasonAndTheCountRunsFromTheLastUiLeaving()
    {
        Flight(FlightState.Landed);
        Row("job00001", SessionState.Idle);
        var client = new object();
        _views.Say(client, "cards");
        _time.Advance(TimeSpan.FromMinutes(30));
        await _tower.LookAsync(Ct);
        _ends.ShouldBeEmpty();

        // The UI leaves: the quiet count starts then, not from before.
        _views.Forget(client);
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(14));
        await _tower.LookAsync(Ct);
        _ends.ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMinutes(2));
        await _tower.LookAsync(Ct);

        var end = _ends.ShouldHaveSingleItem();
        end.Args!.Single().ShouldStartWith("quiet:");
        _state.Cards().Single(c => c.Bin == CardBin.Handled).Title.ShouldContain("no UI connected");
    }

    [Fact]
    public async Task ACliOrMcpCallAloneIsNoUi()
    {
        Flight(FlightState.Landed);
        Row("job00001", SessionState.Idle);
        _views.Any.ShouldBeFalse();

        await LookPast();

        _ends.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ASayToAKeptWatchtowerIsRelayedAndNotResumed()
    {
        Flight(FlightState.Landed);
        Row("job00001", SessionState.Idle);
        _views.Say(new object(), "cards");
        await _tower.LookAsync(Ct);

        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);

        _said.ShouldContain(c => c.Verb == Verbs.Say);
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADeadWatchtowerProcessWithAFlightInTheAirIsStartedAgain()
    {
        Flight();
        PutRow("job00001", SessionState.Ended);

        await _tower.LookAsync(Ct);

        _workers.Started.ShouldHaveSingleItem();
    }
}
