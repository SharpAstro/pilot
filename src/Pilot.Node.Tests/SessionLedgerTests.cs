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
            TestDirs.DeleteTree(_folder);
        }
    }

    private void Launch(string sessionId, string name, string reason, int? pr = null) =>
        _ledger.Started(sessionId[..8], name, SessionRoles.Resolve(name, background: true), reason, "claude-sonnet-5-5", pr);

    private SessionRow Row(string id) => _state.Sessions().Single(r => r.Id == id);

    [Fact]
    public void StartedRecordsTheAccountTheNodeHeldAndStampPutsItOnTheRow()
    {
        _ledger.Account = () => "a@example.org";
        Launch(AgentRows.SessionA, "PIL5 node", SessionStartReasons.LegLaunch);
        _ledger.Account = () => "b@example.org";

        _ledger.Find([AgentRows.SessionA[..8]])!.Account.ShouldBe("a@example.org");
        var row = new SessionRow
        {
            Id = AgentRows.SessionA[..8], SessionId = AgentRows.SessionA, Name = "PIL5 node", Kind = SessionKind.Background, Cwd = "/r", Role = SessionRole.Worker,
            ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = Start,
        };
        _ledger.Stamp(row, null).Account.ShouldBe("a@example.org");
        // An adopted row's start is not known to be under any login.
        var adopted = row with { Id = "ad000000", SessionId = "ad000000-0000-4000-8000-000000000000" };
        _ledger.Adopt(adopted);
        _ledger.Stamp(adopted, null).Account.ShouldBeNull();
        // It survives a restart.
        new SessionLedger(SessionLedger.FileIn(_folder), _time).Find([AgentRows.SessionA[..8]])!.Account.ShouldBe("a@example.org");
    }

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
    public async Task AFixerAndAPrReviewerCarryTheirPrAndTheFixerItsRound()
    {
        Launch(AgentRows.SessionA, "de1-fix-pilot-7", SessionStartReasons.PrFix, pr: 7);
        Launch(AgentRows.SessionB, "de1-review-pilot-7", SessionStartReasons.PrReview, pr: 7);
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "SharpAstro/pilot", Number = 7, Url = "u", Base = "main", Head = "h", ClonePath = _folder, Worktree = _folder, Reviewer = "de1-review-pilot-7",
            State = default, StartedAt = Start, Fixer = "de1-fix-pilot-7", FixRound = 2,
        });
        _agents.Json = AgentRows.List(
            AgentRows.Background(AgentRows.SessionA, "de1-fix-pilot-7", "working", pid: 1, status: "busy"),
            AgentRows.Background(AgentRows.SessionB, "de1-review-pilot-7", "working", pid: 2, status: "busy"));

        await _tracker.PollAsync(Ct);

        var fixer = Row("a1b2c3d4");
        (fixer.Role, fixer.Flight, fixer.Leg, fixer.Pr, fixer.FixRound).ShouldBe((SessionRole.Fixer, null, null, 7, 2));
        var reviewer = Row("b2c3d4e5");
        (reviewer.Role, reviewer.Pr, reviewer.FixRound).ShouldBe((SessionRole.Reviewer, 7, null));
    }

    /// <summary>PIL1008's review note: every round's fixer has the same name, so a row keeps the round it was first seen in; the PR's later round must not renumber it.</summary>
    [Fact]
    public async Task AFixersRowKeepsItsRoundWhenThePrMovesToTheNext()
    {
        Launch(AgentRows.SessionA, "de1-fix-pilot-7", SessionStartReasons.PrFix, pr: 7);
        var review = new PrReviewRow
        {
            Repo = "SharpAstro/pilot", Number = 7, Url = "u", Base = "main", Head = "h", ClonePath = _folder, Worktree = _folder, Reviewer = "de1-review-pilot-7",
            State = default, StartedAt = Start, Fixer = "de1-fix-pilot-7", FixRound = 2,
        };
        _state.PutPrReview(review);
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "de1-fix-pilot-7", "working", pid: 1, status: "busy"));
        await _tracker.PollAsync(Ct);

        _state.PutPrReview(review with { FixRound = 3 });
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "de1-fix-pilot-7", "working", pid: 1, status: "idle"));
        await _tracker.PollAsync(Ct);

        Row("a1b2c3d4").FixRound.ShouldBe(2);
    }

    /// <summary>An ended fixer row keeps its round once the PR's review no longer names it, through the row it had (the review of #1011).</summary>
    [Fact]
    public async Task AnEndedFixersRowKeepsItsRoundOnceTheReviewNoLongerNamesIt()
    {
        Launch(AgentRows.SessionA, "de1-fix-pilot-7", SessionStartReasons.PrFix, pr: 7);
        var review = new PrReviewRow
        {
            Repo = "SharpAstro/pilot", Number = 7, Url = "u", Base = "main", Head = "h", ClonePath = _folder, Worktree = _folder, Reviewer = "de1-review-pilot-7",
            State = default, StartedAt = Start, Fixer = "de1-fix-pilot-7", FixRound = 2,
        };
        _state.PutPrReview(review);
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "de1-fix-pilot-7", "working", pid: 1, status: "busy"));
        await _tracker.PollAsync(Ct);

        _state.PutPrReview(review with { Fixer = null, FixRound = 3 });
        _agents.Json = "[]";
        await _tracker.PollAsync(Ct);
        await _tracker.PollAsync(Ct);

        var row = Row("a1b2c3d4");
        row.State.ShouldNotBe(SessionState.Working);
        row.FixRound.ShouldBe(2);
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

    // #1365: Find is indexed; it must answer as the linear scan did.

    private static SessionLedgerEntry? Linear(SessionLedgerEntry[] entries, string[] ids)
    {
        foreach (var id in ids)
        {
            var job = id.Length > 8 ? id[..8] : id;
            if (entries.LastOrDefault(e => string.Equals(e.JobId, job, StringComparison.OrdinalIgnoreCase)
                || e.SessionIds.Contains(id, StringComparer.OrdinalIgnoreCase)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private void FindsAsTheScanDid(params string[][] probes)
    {
        var all = _ledger.All();
        foreach (var ids in probes)
        {
            _ledger.Find(ids).ShouldBe(Linear(all, ids), string.Join(",", ids));
        }
    }

    [Fact]
    public void FindAnswersAsTheLinearScanDidAfterEveryKindOfWrite()
    {
        var idA = AgentRows.SessionA;
        var idB = AgentRows.SessionB;
        string[][] probes =
        [
            [idA], [idA[..8]], [idA.ToUpperInvariant()], [idB], [idB[..8]], [SessionC], [SessionC[..8].ToUpperInvariant()], ["zz", idB, idA], [idA, idB], ["nothing"], [],
        ];
        FindsAsTheScanDid(probes);

        Launch(idA, "a", SessionStartReasons.LegLaunch);
        Launch(idB, "b", SessionStartReasons.LegLaunch);
        FindsAsTheScanDid(probes);
        _ledger.Find([idA.ToUpperInvariant()])!.Name.ShouldBe("a");

        // Observe follows a later session id on the entry; the last entry holding an id wins.
        var row = new SessionRow
        {
            Id = idA[..8], SessionId = SessionC, Name = "a", Kind = SessionKind.Background, Cwd = "/r", Role = SessionRole.Worker,
            ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 5, StartedAt = Start,
        };
        _ledger.Observe(row, null);
        FindsAsTheScanDid(probes);
        _ledger.Find([SessionC])!.JobId.ShouldBe(idA[..8]);

        _ledger.Stopped([idB], "stopped", "test");
        FindsAsTheScanDid(probes);
        _ledger.Find([idB])!.EndReason.ShouldBe("stopped");

        _ledger.Compacted([SessionC], Start, "why");
        FindsAsTheScanDid(probes);
        _ledger.Find([idA])!.CompactedBecause.ShouldBe("why");

        _ledger.Resumed([idA], "say");
        FindsAsTheScanDid(probes);
        _ledger.Find([idA])!.Resumes.ShouldBe(["say"]);

        // Adopt takes in a row the ledger does not know, and is a no-op for one it does.
        var adopted = row with { Id = "adopted1", SessionId = "adopted1-0000-4000-8000-000000000000", EarlierSessionIds = ["old-session"] };
        _ledger.Adopt(adopted);
        _ledger.Adopt(adopted);
        FindsAsTheScanDid([.. probes, ["adopted1"], [adopted.SessionId], ["old-session"], ["ADOPTED1"]]);
        _ledger.Find(["old-session"])!.JobId.ShouldBe("adopted1");

        // Started again under the same job id replaces the entry.
        Launch(idA, "a2", SessionStartReasons.LegLaunch);
        FindsAsTheScanDid(probes);
        _ledger.Find([idA])!.Name.ShouldBe("a2");
    }

    private const string Probe = "abcdef12-0000-4000-8000-0000000000aa";

    // A ledger file written by hand, so two entries can hold the same id (through the API Observe and Adopt look ids up first and never leave one).
    private SessionLedger LedgerOf(params (string Job, string[] Ids)[] entries)
    {
        var folder = Path.Combine(_folder, "dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var json = "[" + string.Join(",", entries.Select(e =>
            $"{{\"job\":\"{e.Job}\",\"sessionIds\":[{string.Join(",", e.Ids.Select(i => $"\"{i}\""))}],\"name\":\"{e.Job}\"}}")) + "]";
        File.WriteAllText(SessionLedger.FileIn(folder), json);
        return new SessionLedger(SessionLedger.FileIn(folder), _time);
    }

    private static void FindsAsTheScanDid(SessionLedger ledger, string expectedJob, params string[][] probes)
    {
        var all = ledger.All();
        foreach (var ids in probes)
        {
            var found = ledger.Find(ids);
            found.ShouldBe(Linear(all, ids), string.Join(",", ids));
            found!.JobId.ShouldBe(expectedJob, string.Join(",", ids));
        }
    }

    [Fact]
    public void WhenTwoEntriesHoldTheSameSessionIdTheLastOneWins()
    {
        var ledger = LedgerOf(("11111111", [Probe]), ("22222222", [Probe]));

        FindsAsTheScanDid(ledger, "22222222", [Probe], [Probe.ToUpperInvariant()], ["zz", Probe]);
    }

    [Fact]
    public void AJobIdMatchAndALaterSessionIdMatchGoToTheLaterEntry()
    {
        var ledger = LedgerOf(("abcdef12", []), ("33333333", [Probe]));

        FindsAsTheScanDid(ledger, "33333333", [Probe], [Probe.ToUpperInvariant()]);
        ledger.Find(["ABCDEF12"])!.JobId.ShouldBe("abcdef12");
    }

    [Fact]
    public void ASessionIdMatchAndALaterJobIdMatchGoToTheLaterEntry()
    {
        var ledger = LedgerOf(("33333333", [Probe]), ("abcdef12", []));

        FindsAsTheScanDid(ledger, "abcdef12", [Probe], [Probe.ToUpperInvariant()]);
    }

    [Fact]
    public void FindAnswersAsTheLinearScanDidAfterTheCapacityTrim()
    {
        var ledger = new SessionLedger(null, _time);
        for (var i = 0; i < 2005; i++)
        {
            ledger.Started($"job{i:D5}", "n" + i, SessionRoles.Resolve("n" + i, background: true), SessionStartReasons.LegLaunch, null);
        }

        ledger.All().Length.ShouldBe(2000);
        ledger.Find(["job00000"]).ShouldBeNull();
        ledger.Find(["job00004"]).ShouldBeNull();
        ledger.Find(["JOB00005"])!.Name.ShouldBe("n5");
        ledger.Find(["job02004-extra-long-session-id"])!.Name.ShouldBe("n2004");
    }
}
