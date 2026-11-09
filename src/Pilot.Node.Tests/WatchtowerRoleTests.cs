using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The role has one record of its watchtower (pilot #782): never an ended or ending row, and the live one is adopted.</summary>
public sealed class WatchtowerRoleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly string _data = TestTemp.NewPath();
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;
    private readonly List<string> _says = [];

    public WatchtowerRoleTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        var ending = _workers.EndingRun(_state);
        _tower = new Watchtower(_state, _workers, (command, ct) =>
        {
            if (command.Verb == Verbs.Say)
            {
                _says.Add(command.Target[Verbs.SessionTargetPrefix.Length..]);
            }
            return ending(command, ct);
        }, _options, _time) { Pilot = "pilot" };
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
            TestTemp.Delete(_data);
        }
    }

    private void Row(string id, SessionState state, DateTimeOffset startedAt) =>
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
            StartedAt = startedAt,
        });

    [Fact]
    public async Task ASecondWatchtowerStoppedAsADuplicateLeavesTheRolePointingAtTheFirstAndSaysReachIt()
    {
        await _tower.LookAsync(Ct);
        var first = _tower.Generations().Single().Job;
        Row(first, SessionState.Idle, Start);
        _time.Advance(TimeSpan.FromMinutes(3));

        // A second start made beside it, listed live and newer.
        Row("fb183143", SessionState.Idle, _time.GetUtcNow());
        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldBe(["fb183143"]);
        _state.CurrentWatchtower.ShouldBe(first);
        _tower.Current()!.Id.ShouldBe(first);

        // Still listed live: the newest-row rule alone would call the first replaced; the record says otherwise.
        var rows = _state.Sessions();
        SessionMessenger.ReplacedBy(rows.Single(r => r.Id == first), rows, _state.CurrentWatchtower).ShouldBeNull();
        SessionMessenger.ReplacedBy(rows.Single(r => r.Id == "fb183143"), rows, _state.CurrentWatchtower)!.Id.ShouldBe(first);

        Row("fb183143", SessionState.Ended, _time.GetUtcNow());
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);

        _says.ShouldContain(first);
        _says.ShouldNotContain("fb183143");
    }

    [Fact]
    public async Task ARoleRecordPointingAtAnEndedRowAdoptsTheLiveOneWithNoFreshStart()
    {
        await _tower.LookAsync(Ct);
        var recorded = _tower.Generations().Single().Job;
        _workers.Started.ShouldHaveSingleItem();
        _time.Advance(TimeSpan.FromMinutes(5));
        Row(recorded, SessionState.Ended, Start);
        Row("live0001", SessionState.Idle, _time.GetUtcNow());

        await _tower.LookAsync(Ct);

        _workers.Started.ShouldHaveSingleItem();
        _tower.Current()!.Id.ShouldBe("live0001");
        _state.CurrentWatchtower.ShouldBe("live0001");
        _workers.Stopped.ShouldNotContain("live0001");
    }
}
