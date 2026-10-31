using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node's ledger of the sessions it launched (#542): start reason, end reason and time, cost, and a restart keeping them.</summary>
public sealed class SessionLedgerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-31T10:00:00Z");

    private const string SessionC = "c3d4e5f6-0000-4000-8000-000000000003";

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly ManualTime _time = new ManualTime(Start);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly NodeState _state;
    private readonly SessionLedger _ledger;
    private readonly SessionTracker _tracker;

    public SessionLedgerTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _ledger = new SessionLedger(SessionLedger.FileIn(_folder), _time);
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance) { Launched = _ledger };
    }

    public void Dispose()
    {
        _profile.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private void Launch(string sessionId, string name, string reason, int? pr = null) =>
        _ledger.Started(sessionId[..8], name, SessionRoles.Resolve(name, background: true), reason, "claude-sonnet-5-5", pr);

    private SessionRow Row(string id) => _state.Sessions().Single(r => r.Id == id);

    [Fact]
    public async Task ALegLaunchAReviewAndTheWatchtowerEachCarryTheirStartReason()
    {
        Launch(AgentRows.SessionA, "PIL5 node", SessionStartReasons.LegLaunch);
        Launch(AgentRows.SessionB, "de1-review-pilot-7", SessionStartReasons.PrReview, pr: 7);
        Launch(SessionC, "de1-watchtower", SessionStartReasons.WatchtowerRestart);
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "PIL5 node", "working", pid: 1, status: "busy"),
            AgentRows.Background(AgentRows.SessionB, "de1-review-pilot-7", "working", pid: 2, status: "busy"),
            AgentRows.Background(SessionC, "de1-watchtower", "working", pid: 3, status: "idle"));

        await _tracker.PollAsync(Ct);

        Row("a1b2c3d4").StartReason.ShouldBe(SessionStartReasons.LegLaunch);
        Row("b2c3d4e5").StartReason.ShouldBe(SessionStartReasons.PrReview);
        Row("c3d4e5f6").StartReason.ShouldBe(SessionStartReasons.WatchtowerRestart);
        Row("a1b2c3d4").EndReason.ShouldBeNull();
        _ledger.Find(["b2c3d4e5"])!.Pr.ShouldBe(7);
    }

    [Fact]
    public async Task ASessionTheNodeStoppedCarriesTheEndReasonAndTimeGiven()
    {
        Launch(AgentRows.SessionA, "PIL5 node", SessionStartReasons.LegLaunch);
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "PIL5 node", "working", pid: 1, status: "busy"));
        await _tracker.PollAsync(Ct);

        _time.Advance(TimeSpan.FromMinutes(20));
        _ledger.Stopped([AgentRows.SessionA], SessionEndReasons.Landed, "land");
        _time.Advance(TimeSpan.FromMinutes(1));
        _agents.Json = "[]";
        await _tracker.PollAsync(Ct);

        var row = Row("a1b2c3d4");
        row.State.ShouldBe(SessionState.Ended);
        row.EndReason.ShouldBe(SessionEndReasons.Landed);
        row.EndedAt.ShouldBe(Start.AddMinutes(20));
        _ledger.Find(["a1b2c3d4"])!.EndedBy.ShouldBe("land");
    }

    [Fact]
    public async Task ASessionSeenEndedWithNoNodeStopIsFinishedOrCrashedByItsState()
    {
        Launch(AgentRows.SessionA, "PIL5 node", SessionStartReasons.LegLaunch);
        Launch(AgentRows.SessionB, "PIL6 node", SessionStartReasons.LegLaunch);
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "PIL5 node", "working", pid: 1, status: "busy"),
            AgentRows.Background(AgentRows.SessionB, "PIL6 node", "working", pid: 2, status: "busy"));
        await _tracker.PollAsync(Ct);

        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "PIL6 node", "failed", startedAt: 1793440800000));
        await _tracker.PollAsync(Ct);

        Row("a1b2c3d4").EndReason.ShouldBe(SessionEndReasons.Finished);
        Row("b2c3d4e5").EndReason.ShouldBe(SessionEndReasons.Crashed);
    }

    [Fact]
    public async Task AnEndedSessionKeepsItsTokensAndWallTimeEvenPastTheTrackersRetention()
    {
        Launch(AgentRows.SessionA, "PIL5 node", SessionStartReasons.LegLaunch);
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "PIL5 node", "working", pid: 1, status: "busy"));
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionA), Lines.Assistant("m1", input: 10, output: 100));
        await _tracker.PollAsync(Ct);

        _time.Advance(TimeSpan.FromMinutes(90));
        _agents.Json = "[]";
        await _tracker.PollAsync(Ct);

        var row = Row("a1b2c3d4");
        row.SpendWeighted.ShouldBe(510);
        row.WallSeconds.ShouldBe(5400);

        _time.Advance(TimeSpan.FromHours(25));
        await _tracker.PollAsync(Ct);
        _state.Sessions().ShouldBeEmpty();
        var entry = _ledger.Find(["a1b2c3d4"]).ShouldNotBeNull();
        entry.Tokens.ShouldBe(510);
        entry.WallSeconds.ShouldBe(5400);
        entry.EndReason.ShouldBe(SessionEndReasons.Finished);
    }

    [Fact]
    public void TheLedgerSurvivesANodeRestart()
    {
        Launch(AgentRows.SessionA, "PIL5 node", SessionStartReasons.LegLaunch);
        _ledger.Stopped([AgentRows.SessionA], SessionEndReasons.Watchtower, "watchtower");
        _ledger.Resumed([AgentRows.SessionA], SessionStartReasons.Compaction);

        File.Exists(SessionLedger.FileIn(_folder)).ShouldBeTrue();
        var again = new SessionLedger(SessionLedger.FileIn(_folder), _time);

        var entry = again.Find(["a1b2c3d4"]).ShouldNotBeNull();
        entry.StartReason.ShouldBe(SessionStartReasons.LegLaunch);
        entry.Role.ShouldBe(SessionRole.Worker);
        entry.Flight.ShouldBe("PIL5");
        entry.Leg.ShouldBe("node");
        entry.StartedAt.ShouldBe(Start);
        entry.EndReason.ShouldBe(SessionEndReasons.Watchtower);
        entry.EndedBy.ShouldBe("watchtower");
        entry.Model.ShouldBe("claude-sonnet-5-5");
        entry.Resumes.ShouldBe([SessionStartReasons.Compaction]);
    }

    [Fact]
    public void ThePilotSessionsTextShowsStartEndAndWhenItEnded()
    {
        var ended = new SessionRow
        {
            Id = "a1b2c3d4", SessionId = AgentRows.SessionA, Name = "PIL5 node", Kind = SessionKind.Background, Cwd = "/r", Role = SessionRole.Worker,
            ContextTokens = 0, ContextWindow = 1, State = SessionState.Ended, SpendWeighted = 510, StartedAt = Start,
            StartReason = SessionStartReasons.LegLaunch, EndReason = SessionEndReasons.Landed, EndedAt = Start.AddMinutes(75), WallSeconds = 4500,
        };

        var text = PilotCommand.Sessions([ended, ended with { Id = "b2c3d4e5", StartReason = null, EndReason = null, EndedAt = null, WallSeconds = null, State = SessionState.Idle }]);

        text.ShouldContain("start: leg launch  end: stopped on land  ended: 2026-10-31 11:15Z  wall: 1h15m");
        text.ShouldContain("start: -  end: -  ended: -");
    }
}
