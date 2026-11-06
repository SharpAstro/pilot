using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Starting the watchtower is single flight per node (pilot #782): a start asked while one is starting or live joins it.</summary>
public sealed class WatchtowerStartTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-starttower-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;

    public WatchtowerStartTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = Tower();
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private Watchtower Tower(IWorkerLauncher? launcher = null) =>
        new(_state, launcher ?? _workers, _workers.EndingRun(_state), _options, _time) { Pilot = "pilot" };

    private void Row(string id, SessionState state = SessionState.Idle) =>
        _state.PutSession(new SessionRow
        {
            Id = id,
            SessionId = id + "-0000-4000-8000-000000000000",
            Name = "du1-watchtower",
            Kind = SessionKind.Background,
            Cwd = _options.WatchtowerFolder,
            Role = SessionRole.Watchtower,
            ContextTokens = 10_000,
            ContextWindow = 200_000,
            State = state,
            SpendWeighted = 0,
            LastResponseAt = _time.GetUtcNow(),
            StartedAt = _time.GetUtcNow(),
        });

    private int Live() => _state.Sessions().Count(s => s.Role == SessionRole.Watchtower && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle);

    [Fact]
    public async Task ALookAndARestartRacingStartOneWatchtower()
    {
        var release = new TaskCompletionSource();
        _workers.Hold = release.Task;

        var look = _tower.LookAsync(Ct);
        var restart = _tower.RestartWithWayAsync("racing", Ct);
        release.SetResult();
        await look;
        var restarted = await restart;

        restarted.Error.ShouldBeNull();
        restarted.Way.ShouldBe(Watchtower.JoinedWay);
        _workers.Started.ShouldHaveSingleItem();
        Row(_tower.Generations().Single().Job);
        Live().ShouldBe(1);
    }

    [Fact]
    public async Task TwoNodesOverOneDataFolderStartingTogetherLaunchOnce()
    {
        var other = Tower();
        var release = new TaskCompletionSource();
        _workers.Hold = release.Task;

        var first = _tower.LookAsync(Ct);
        // The first holds the start lock while its launcher is slow: the second joins that start.
        await other.LookAsync(Ct);
        release.SetResult();
        await first;

        _workers.Started.ShouldHaveSingleItem();
        Row(_tower.Generations().Single().Job);
        Live().ShouldBe(1);
    }

    [Fact]
    public async Task AnOverlappingNodeThatLooksAfterTheFirstStartedJoinsItInsteadOfDoublingIt()
    {
        await _tower.LookAsync(Ct);
        var other = Tower();
        // It has read the generations file already, as its first look does, and the session is not listed yet.
        other.Generations();
        _time.Advance(TimeSpan.FromSeconds(20));

        await other.LookAsync(Ct);
        await other.RestartAsync("update", Ct);

        _workers.Started.ShouldHaveSingleItem();
        Row(_tower.Generations().Single().Job);
        Live().ShouldBe(1);
    }

    [Fact]
    public async Task ARestartWhileAStartIsInFlightJoinsItAndStartsNothing()
    {
        await _tower.LookAsync(Ct);
        _workers.Started.ShouldHaveSingleItem();
        _time.Advance(TimeSpan.FromSeconds(30));

        var restarted = await _tower.RestartWithWayAsync("by hand", Ct);

        restarted.Error.ShouldBeNull();
        restarted.Way.ShouldNotBeNull().ShouldContain("already in progress");
        _workers.Started.ShouldHaveSingleItem();
        _workers.Stopped.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARestartAfterTheGraceOrOnceListedStartsAFreshOne()
    {
        await _tower.LookAsync(Ct);
        Row(_tower.Generations().Single().Job);

        (await _tower.RestartAsync("listed", Ct)).ShouldBeNull();

        _workers.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ALauncherThatThrewAfterTheSessionCameUpDoesNotLetASecondStartThrough()
    {
        var launcher = new ThrowingAfterUp(Row);

        var tower = Tower(launcher);
        await tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(15));
        await tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(15));
        await tower.LookAsync(Ct);

        launcher.Attempts.ShouldBe(1);
        tower.Current()!.Id.ShouldBe("up000001");
        Live().ShouldBe(1);
    }

    private sealed class ThrowingAfterUp(Action<string, SessionState> row) : IWorkerLauncher
    {
        public int Attempts { get; private set; }

        public Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
        {
            Attempts++;
            row("up000001", SessionState.Idle);
            throw new InvalidOperationException("claude exited after the session started");
        }

        public Task StopAsync(string job, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
