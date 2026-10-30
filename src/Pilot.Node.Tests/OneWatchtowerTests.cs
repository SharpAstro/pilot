using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>One watchtower per node (pilot #454): a second live one is stopped, and a compaction's continuation is the same row.</summary>
public sealed class OneWatchtowerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-onetower-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;

    public OneWatchtowerTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = new Watchtower(_state, _workers, (c, _) => Task.FromResult(_state.Complete(c, null)), _options, _time) { Pilot = "pilot" };
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

    private void Row(string id, SessionState state, DateTimeOffset startedAt, SessionKind kind = SessionKind.Background, string name = "du1-watchtower",
        SessionRole role = SessionRole.Watchtower) =>
        _state.PutSession(new SessionRow
        {
            Id = id,
            SessionId = id + "-0000-4000-8000-000000000000",
            Name = name,
            Kind = kind,
            Cwd = _options.WatchtowerFolder,
            Role = role,
            ContextTokens = 10_000,
            ContextWindow = 200_000,
            State = state,
            SpendWeighted = 0,
            LastResponseAt = _time.GetUtcNow(),
            StartedAt = startedAt,
        });

    private void RecordCurrentBrief()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(_tower.BriefFile, Briefs.WatchtowerFingerprint("du1-watchtower", "pilot", _tower.Models.Model(RoleModels.Watchtower), _options.Machine));
    }

    [Fact]
    public async Task StartingOneStopsAnotherLiveWatchtowerRowAndSaysSo()
    {
        // The newest live row is the one replaced; the other live one is the duplicate.
        Row("dup00001", SessionState.Idle, Start.AddMinutes(-5));
        Row("cur00001", SessionState.Idle, Start);

        await _tower.RestartAsync("test", Ct);

        _workers.Started.ShouldHaveSingleItem();
        _workers.Stopped.ShouldBe(["cur00001", "dup00001"]);
        _state.Cards().Single(c => c.Kind == "watchtower-duplicate").Title.ShouldContain("dup00001");
    }

    [Fact]
    public async Task AdoptingOneOnNodeStartWithTwoLiveStopsTheOlder()
    {
        Row("old00001", SessionState.Idle, Start.AddHours(-2));
        Row("new00001", SessionState.Idle, Start.AddHours(-1));
        RecordCurrentBrief();

        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldBe(["old00001"]);
        _workers.Started.ShouldBeEmpty();
        _state.Cards().Single(c => c.Kind == "watchtower-duplicate").Title.ShouldContain("old00001");
    }

    [Fact]
    public async Task ARenamedWatchtowerThenAFreshStartLeavesOneLiveAndNoSayReachesTheOldId()
    {
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);
        var oldId = _tower.Generations().ShouldHaveSingleItem().Job;

        // The node's own watchtower turns up renamed, its role gone, and is live.
        _time.Advance(TimeSpan.FromMinutes(3));
        Row(oldId, SessionState.Idle, Start, name: "de1-pil455-pr498-merge-ready", role: SessionRole.AdHoc);
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(2);
        var freshId = _tower.Generations().Last().Job;
        freshId.ShouldNotBe(oldId);
        Row(freshId, SessionState.Idle, _time.GetUtcNow());

        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        _time.Advance(TimeSpan.FromMinutes(1));
        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldContain(oldId);
        _workers.Stopped.ShouldNotContain(freshId);
        _tower.Current()!.Id.ShouldBe(freshId);
        _tower.TellTarget().Target.ShouldBe(Verbs.SessionTarget(freshId));
        _state.Cards().Count(c => c.Kind == "watchtower-duplicate").ShouldBe(1);
    }

    [Fact]
    public async Task ARenamedOldWatchtowerStillLiveIsStoppedByAPlainLook()
    {
        await _tower.LookAsync(Ct);
        var oldId = _tower.Generations().ShouldHaveSingleItem().Job;
        _time.Advance(TimeSpan.FromMinutes(3));
        Row(oldId, SessionState.Idle, Start, name: "my-own-name", role: SessionRole.AdHoc);
        Row("new00001", SessionState.Idle, _time.GetUtcNow());
        RecordCurrentBrief();
        _tower.Generations();

        // The new one is the node's by name; the old by id.
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldBe([oldId]);
        _state.Cards().Single(c => c.Kind == "watchtower-duplicate").Title.ShouldContain($"my-own-name ({oldId})");
    }

    [Fact]
    public async Task AnInteractiveSessionNamedLikeTheWatchtowerIsNeverStopped()
    {
        Row("mine0001", SessionState.Idle, Start.AddHours(-2), SessionKind.Interactive, role: SessionRole.AdHoc);
        Row("tower001", SessionState.Idle, Start.AddHours(-1));
        RecordCurrentBrief();

        await _tower.LookAsync(Ct);
        await _tower.RestartAsync("test", Ct);

        _workers.Stopped.ShouldNotContain("mine0001");
        _state.Cards().Where(c => c.Kind == "watchtower-duplicate").ShouldBeEmpty();
    }

    [Fact]
    public async Task ACompactedWatchtowerLeavesOneLiveWatchtowerRowThatFollowsTheContinuation()
    {
        using var profile = new TempProfile();
        var agents = new FakeClaudeAgents();
        var tracker = new SessionTracker(_state, agents, profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance);

        agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "du1-watchtower", "running", pid: 4242, status: "idle"));
        await tracker.PollAsync(Ct);

        // The compaction continues under a new session id; the old one is no longer listed.
        agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "du1-watchtower", "running", pid: 4242, status: "idle"));
        await tracker.PollAsync(Ct);
        await tracker.PollAsync(Ct);

        var live = _state.Sessions().Where(s => s.Role == SessionRole.Watchtower && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
        var row = live.ShouldHaveSingleItem();
        row.Id.ShouldBe("a1b2c3d4");
        row.SessionId.ShouldBe(AgentRows.SessionB);
        row.EarlierSessionIds.ShouldBe([AgentRows.SessionA]);
        _state.Sessions().Length.ShouldBe(1);
    }

    [Fact]
    public async Task AFreshWatchtowerBesideARunningOneIsNotFoldedIntoIt()
    {
        using var profile = new TempProfile();
        var agents = new FakeClaudeAgents();
        var tracker = new SessionTracker(_state, agents, profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance);

        agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "du1-watchtower", "running", pid: 4242, status: "idle"),
            AgentRows.Background(AgentRows.SessionB, "du1-watchtower", "running", pid: 4343, status: "idle"));
        await tracker.PollAsync(Ct);

        _state.Sessions().Length.ShouldBe(2);
    }
}
