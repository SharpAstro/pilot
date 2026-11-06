using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Claude Code ends a <c>claude --bg</c> job's process a minute or two after its turn reaches a terminal state (PIL815), so the
/// watchtower ends after every reply by design. An ended row whose job record reads a finished turn, with nothing to tell, is left
/// ended; a new card resumes it warm and starts one cold; such ends are no churn.
/// </summary>
public sealed class WatchtowerEndsByDesignTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-bydesign-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;
    // Claude Code's state.json of each job, by job id.
    private readonly Dictionary<string, ClaudeJobRecord> _records = [];

    public WatchtowerEndsByDesignTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = new Watchtower(_state, _workers, Run, _options, _time)
        {
            Pilot = "pilot",
            JobRecord = job => _records.TryGetValue(job, out var record) ? record : null,
            Version = "2.0.1",
        };
        // A flight in the air with a leg flying: something keeps the watchtower, but it has no card.
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

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.Verb != Verbs.EndSession)
        {
            _said.Add(command);
        }
        return Task.FromResult(_state.Complete(command, null));
    }

    private void Row(string id, SessionState state, long context = 10_000) =>
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
            LastResponseAt = _time.GetUtcNow(),
            StartedAt = Start,
        });

    private ClaudeJobRecord Finished(string state = "done") => new(state, "replied", _time.GetUtcNow());

    private CardRow[] Open() => _state.Cards(openOnly: true);

    /// <summary>The watchtower starts, replies, and its process is gone: ended, as the job record reads <paramref name="record"/>.</summary>
    private async Task StartedRepliedAndEnded(ClaudeJobRecord? record)
    {
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);
        Row("job00001", SessionState.Idle);
        _time.Advance(TimeSpan.FromSeconds(90));
        if (record is not null)
        {
            _records["job00001"] = record;
        }
        Row("job00001", SessionState.Ended);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("blocked")]
    public async Task AnEndedWatchtowerWhoseTurnFinishedIsLeftEndedWithNoCardToTell(string state)
    {
        await StartedRepliedAndEnded(Finished(state));

        for (var i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await _tower.LookAsync(Ct);
        }

        _workers.Started.Count.ShouldBe(1);
        _said.ShouldBeEmpty();
        _state.Sessions().Single().State.ShouldBe(SessionState.Ended);
        // The node does not say it started a fresh one, and rings nothing about a watchtower that is down.
        Open().ShouldNotContain(c => c.Kind == Watchtower.Down || c.Kind == CardKinds.WatchtowerChurning);
    }

    [Fact]
    public async Task AnEndedWatchtowerThatDidNotFinishATurnIsStartedAgain()
    {
        // No record at all (or one that reads failed or working): a crash, started again on the next look as before.
        await StartedRepliedAndEnded(null);
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(2);
        _workers.Started[1].Reason.ShouldBe(SessionStartReasons.WatchtowerStart);
    }

    [Fact]
    public async Task AnEndedWatchtowerWhoseJobRecordReadsFailedIsNotLeftEnded()
    {
        await StartedRepliedAndEnded(new ClaudeJobRecord("failed", "crashed", _time.GetUtcNow()));
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ANewCardResumesItInPlaceWhileItsCacheIsWarm()
    {
        await StartedRepliedAndEnded(Finished());
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(5));
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);

        // Said to the ended row (the messenger resumes it in place), no fresh start.
        _workers.Started.Count.ShouldBe(1);
        var said = _said.ShouldHaveSingleItem();
        said.Target.ShouldBe(Verbs.SessionTarget("job00001"));
        said.Args[0].ShouldContain("TIA9/parser failed");
    }

    [Fact]
    public async Task ANewCardStartsAFreshOneWhenTheEndedOneIsColdOrFull()
    {
        await StartedRepliedAndEnded(Finished());
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);

        // Cold: its last reply is past the prompt cache's hour.
        _time.Advance(TimeSpan.FromHours(2));
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(2);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ANewCardStartsAFreshOneWhenTheEndedOnesWindowIsPastTheResumeShare()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle, context: 180_000);
        _records["job00001"] = Finished();
        Row("job00001", SessionState.Ended, context: 180_000);

        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(2);
        _said.ShouldBeEmpty();
    }

    /// <summary>
    /// Three cycles inside the hour (the start rate is no churn yet): a card opens, a fresh watchtower starts, replies and ends a
    /// minute later, as the record reads <paramref name="record"/>.
    /// </summary>
    private async Task ThreeShortLivedAsync(Func<ClaudeJobRecord?> record)
    {
        for (var n = 1; n <= 3; n++)
        {
            // Over its window, so the next card starts a fresh one rather than resuming this.
            _state.RaiseCard(CardBin.Ring, "leg-failed", $"flight/TIA{n}/parser", $"TIA{n}/parser failed");
            await _tower.LookAsync(Ct);
            var id = $"job{n:D5}";
            _time.Advance(TimeSpan.FromSeconds(30));
            Row(id, SessionState.Idle, context: 180_000);
            _time.Advance(TimeSpan.FromSeconds(60));
            if (record() is { } read)
            {
                _records[id] = read;
            }
            Row(id, SessionState.Ended, context: 180_000);
            _state.PutSession(_state.Sessions().Single(r => r.Id == id) with { LastResponseAt = _time.GetUtcNow() - TimeSpan.FromSeconds(60) });
        }
        _tower.Health();
        await _tower.LookAsync(Ct);
    }

    [Fact]
    public async Task EndsAfterAFinishedTurnAreNoChurnAndRaiseNoCard()
    {
        await ThreeShortLivedAsync(() => Finished());

        _workers.Started.Count.ShouldBe(3);
        var health = _tower.Health().ShouldNotBeNull();
        health.Churning.ShouldBeFalse();
        health.TypicalLifetimeAfterReply.ShouldBeNull();
        Open().ShouldNotContain(c => c.Kind == CardKinds.WatchtowerChurning);
        _tower.Generations().ShouldAllBe(g => g.AfterFinishedTurn == true);
    }

    [Fact]
    public async Task EndsWithNoFinishedTurnStillRaiseTheChurnCard()
    {
        await ThreeShortLivedAsync(() => null);

        var health = _tower.Health().ShouldNotBeNull();
        health.Churning.ShouldBeTrue();
        health.TypicalLifetimeAfterReply.ShouldBe(TimeSpan.FromSeconds(60));
        var card = Open().Single(c => c.Kind == CardKinds.WatchtowerChurning);
        card.Title.ShouldBe("The watchtower keeps ending");
        _tower.Generations().ShouldAllBe(g => g.AfterFinishedTurn == false);
    }

    [Fact]
    public async Task TheFinishedTurnNoteOfAnEndSurvivesAReload()
    {
        await StartedRepliedAndEnded(Finished());
        _tower.Generations().Single().AfterFinishedTurn.ShouldBe(true);

        new Watchtower(_state, _workers, Run, _options, _time).Generations().Single().AfterFinishedTurn.ShouldBe(true);
    }

    [Fact]
    public void ARowOfAFinishedJobWithNoProcessSaysClaudeCodeEndedItAfterItsTurn()
    {
        var agent = ClaudeAgentsJson.Parse(AgentRows.List(AgentRows.Background(AgentRows.SessionB, "x", "done", pid: null, status: null)))!.Single();

        SessionRowBuilder.Build(agent, TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc, job: Finished()).EndedBecause
            .ShouldBe("Claude Code ended the job after its turn finished (done)");
        SessionRowBuilder.Build(agent, TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc, job: Finished("blocked")).EndedBecause
            .ShouldBe("Claude Code ended the job after its turn finished (blocked)");
        // No record, or one that did not finish a turn: the listing's word, as before.
        SessionRowBuilder.Build(agent, TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc).EndedBecause
            .ShouldBe("Claude Code lists it as done with no process");
        SessionRowBuilder.Build(agent, TranscriptFacts.None, null, null, SessionRoleInfo.AdHoc, job: new ClaudeJobRecord("failed", "x")).EndedBecause
            .ShouldBe("Claude Code lists it as done with no process");
    }

    [Fact]
    public void TheJobRecordReadsItsStateAndLastTerminalTime()
    {
        var jobs = Path.Combine(_data, "jobs");
        Directory.CreateDirectory(Path.Combine(jobs, "d282c08f"));
        File.WriteAllText(Path.Combine(jobs, "d282c08f", "state.json"), """{"state":"done","detail":"d","lastTerminalAt":"2026-11-06T16:40:02.984Z"}""");
        Directory.CreateDirectory(Path.Combine(jobs, "a95fceac"));
        File.WriteAllText(Path.Combine(jobs, "a95fceac", "state.json"), """{"state":"blocked","firstTerminalAt":null}""");
        Directory.CreateDirectory(Path.Combine(jobs, "b1b1b1b1"));
        File.WriteAllText(Path.Combine(jobs, "b1b1b1b1", "state.json"), """{"state":"working"}""");

        var reader = new ClaudeJobState(jobs);

        var done = reader.Read("d282c08f").ShouldNotBeNull();
        (done.TurnFinished, done.LastTerminalAt).ShouldBe((true, new DateTimeOffset(2026, 11, 6, 16, 40, 2, 984, TimeSpan.Zero)));
        var blocked = reader.Read("a95fceac").ShouldNotBeNull();
        (blocked.TurnFinished, blocked.LastTerminalAt).ShouldBe((true, null as DateTimeOffset?));
        reader.Read("b1b1b1b1").ShouldNotBeNull().TurnFinished.ShouldBeFalse();
    }
}
