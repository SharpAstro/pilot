using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The compactor: the background sessions the node will wake again (the watchtower while it watches, a session waiting on
/// its job) compacted before their prompt cache goes cold, once per stretch of idleness, and nothing else touched.
/// </summary>
public sealed class CompactorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-tower-" + Guid.NewGuid().ToString("N"));
    private readonly NodeState _state;
    private readonly List<CommandRequest> _said = [];
    private readonly Compactor _compactor;
    private readonly FakeWorkers _workers = new();
    private int _persons;
    private readonly Pilot.Node.Sessions.SessionLedger _ledger = new();
    private string? _refuse;

    public CompactorTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        var tower = new Watchtower(_state, _workers, Run, new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1"), _time);
        _compactor = new Compactor(_state, tower, Run, _time) { PersonMessages = _ => _persons, Ledger = _ledger };
        // Something to watch: a flight in the air with a leg flying.
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = "w0000001" });
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
        return Task.FromResult(_state.Complete(command, _refuse));
    }

    private void Row(string id, string name, SessionState state = SessionState.Idle, long context = 180_000, DateTimeOffset? lastResponse = null,
        SessionKind kind = SessionKind.Background, string? flight = null, string? leg = null) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = kind, Cwd = "/r/w",
            Role = name.EndsWith("watchtower", StringComparison.Ordinal) ? SessionRole.Watchtower : SessionRole.Worker, Flight = flight, Leg = leg,
            ContextTokens = context, ContextWindow = 1_000_000, State = state, SpendWeighted = 0, LastResponseAt = lastResponse ?? Start, StartedAt = Start,
        });

    private void Job(string owner, JobState state = JobState.Running) => _state.PutJob(new JobRow
    {
        Id = "j1", Command = "dotnet test", Cwd = "/r/w", Owner = owner, State = state, LogPath = "/tmp/j1.log", Timeout = TimeSpan.FromHours(2), StartedAt = Start,
    });

    private string[] Handled() => [.. _state.Cards().Where(c => c.Kind == Compactor.Kind).Select(c => c.Title)];

    [Fact]
    public async Task TheWatchtowerIdleFiftyMinutesIsCompactedBeforeItsCacheGoesColdOnce()
    {
        Row("t0000001", "du1-watchtower");

        _time.Advance(TimeSpan.FromMinutes(49));
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));
        await _compactor.LookAsync(Ct);
        var compact = _said.ShouldHaveSingleItem();
        (compact.Verb, compact.Target, compact.Who).ShouldBe((Verbs.Compact, "session/t0000001", "node"));
        compact.Args.ShouldBe([Compactor.WatchtowerFocus]);
        Handled().ShouldBe(["Compacted du1-watchtower"]);

        // Not again until it has answered something since: the compaction is no response, and its context reads as before.
        _time.Advance(TimeSpan.FromMinutes(60));
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(1);

        // Answered, and idle fifty minutes more with a big context again: compacted again.
        Row("t0000001", "du1-watchtower", lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(50));
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ALiveWatchtowerIdleAt70PercentIsCompactedOnceAndNotAgainUntilItGrowsPastTheShare()
    {
        Row("t0000001", "du1-watchtower", context: 700_000);
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem();
        Handled().ShouldBe(["Compacted du1-watchtower"]);

        // The compaction answered and left it small: nothing more, however long it sits (under the idle floor of 100k).
        Row("t0000001", "du1-watchtower", context: 60_000, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(5));
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(1);

        // Grown past the share again: compacted again.
        Row("t0000001", "du1-watchtower", context: 650_000, lastResponse: _time.GetUtcNow());
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AWatchtowerUnderTheShareIsNotCompacted()
    {
        Row("t0000001", "du1-watchtower", context: 550_000);
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALongChatIsCompactedOnceIdleAndTheCountStartsOverAfterwards()
    {
        Row("t0000001", "du1-watchtower", context: 90_000);
        // First sight seeds the count (a node restart does not owe a compaction for the transcript's history).
        _persons = 0;
        await _compactor.LookAsync(Ct);
        _persons = 39;
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _persons = 40;
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem();
        Handled().ShouldBe(["Compacted du1-watchtower"]);

        // Answered and settled; thirty-nine more messages is under the count, forty is not.
        _time.Advance(_compactor.Settle + TimeSpan.FromMinutes(1));
        Row("t0000001", "du1-watchtower", context: 60_000, lastResponse: _time.GetUtcNow());
        _persons = 79;
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        _persons = 80;
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ATranscriptAlreadyPastTheChatCountAtFirstSightIsNotCompactedForItsHistory()
    {
        // A node restart sees a watchtower whose transcript already holds 45 person messages: that history owes no compaction.
        Row("t0000001", "du1-watchtower", context: 90_000);
        _persons = 45;
        await _compactor.LookAsync(Ct);
        _persons = 84;
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _persons = 85;
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ACompactionIsLoggedAndTheSessionRowCarriesWhenAndWhy()
    {
        var log = new StringWriter();
        _compactor.Error = log;
        _ledger.Started("t0000001", "du1-watchtower", Pilot.Node.Sessions.SessionRoles.Resolve("du1-watchtower", background: true), Pilot.Contracts.SessionStartReasons.WatchtowerStart, null);
        Row("t0000001", "du1-watchtower", context: 700_000);

        await _compactor.LookAsync(Ct);

        log.ToString().ShouldContain("compactor: compacted du1-watchtower (t0000001): its context reached 70% of its window");
        var row = _state.Sessions().Single(r => r.Id == "t0000001");
        row.LastCompactedAt.ShouldBe(Start);
        row.CompactedBecause.ShouldBe("its context reached 70% of its window");
    }

    [Fact]
    public async Task AWatchtowerStillPastTheShareAfterCompactingIsReplacedNotCompactedAgain()
    {
        Row("t0000001", "du1-watchtower", context: 800_000);
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem();

        // Answered, and once it has settled still at 75%: a fresh watchtower in its place, through the watchtower's own start.
        _time.Advance(_compactor.Settle + TimeSpan.FromMinutes(1));
        Row("t0000001", "du1-watchtower", context: 750_000, lastResponse: _time.GetUtcNow());
        await _compactor.LookAsync(Ct);
        _said.Count(c => c.Verb == Verbs.Compact).ShouldBe(1);
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-watchtower");
        Handled().ShouldContain(t => t.StartsWith("Replaced du1-watchtower", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWatchtowerIsCompactedOnceThenLeftToSettleAndAStaleReadingNeverLoopsIt()
    {
        // SE1, 2026-11-10: compacted and "replaced" every 30 s for four minutes, each time on the reading from before the compaction.
        Row("t0000001", "du1-watchtower", context: 800_000);
        await _compactor.LookAsync(Ct);
        _said.Count(c => c.Verb == Verbs.Compact).ShouldBe(1);

        // The compaction answers, and the reading stays the old one: looks every 30 s through the settle window act on nothing.
        for (var look = 0; look < 8; look++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            Row("t0000001", "du1-watchtower", context: 800_000, lastResponse: _time.GetUtcNow());
            await _compactor.LookAsync(Ct);
        }
        _said.Count(c => c.Verb == Verbs.Compact).ShouldBe(1);
        _workers.Started.ShouldBeEmpty();

        // Settled and read again under its share: nothing more to do, and nothing was said twice.
        _time.Advance(_compactor.Settle);
        Row("t0000001", "du1-watchtower", context: 250_000, lastResponse: _time.GetUtcNow());
        await _compactor.LookAsync(Ct);
        _said.Count(c => c.Verb == Verbs.Compact).ShouldBe(1);
        _workers.Started.ShouldBeEmpty();
        Handled().ShouldBe(["Compacted du1-watchtower"]);
    }

    [Fact]
    public async Task AWatchtowerStillFullAfterACompactionAndARestartIsSaidOnceAndLeftAlone()
    {
        var current = "t0000001";
        Row(current, "du1-watchtower", context: 800_000);
        await _compactor.LookAsync(Ct);
        // The replacement comes back as a fresh row of the role, and it too reads past its share.
        _workers.OnStart = _ =>
        {
            current = "t0000002";
            Row(current, "du1-watchtower", context: 800_000, lastResponse: _time.GetUtcNow());
        };

        // An hour of looks every 30 s with the reading stuck past its share. SE1 made eight compactions and eight "replacements" in
        // four minutes; here it is one compaction of each row, one replacement, and one card.
        for (var look = 0; look < 120; look++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            Row(current, "du1-watchtower", context: 800_000, lastResponse: _time.GetUtcNow());
            await _compactor.LookAsync(Ct);
        }
        _said.Count(c => c.Verb == Verbs.Compact).ShouldBe(2);
        _workers.Started.ShouldHaveSingleItem();
        Handled().Count(t => t.StartsWith("Replaced", StringComparison.Ordinal)).ShouldBe(1);
        var stuck = _state.Cards(openOnly: true).Where(c => c.Kind == "watchtower-still-full").ShouldHaveSingleItem();
        // It says what the replacement came to, not a restart it cannot vouch for, and offers the restart by hand.
        stuck.Detail.ShouldContain("a fresh one started");
        stuck.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.RestartWatchtower);

        // Back under its share: the card closes, and a later episode may be replaced again.
        Row(current, "du1-watchtower", context: 200_000, lastResponse: _time.GetUtcNow());
        await _compactor.LookAsync(Ct);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == "watchtower-still-full");
    }

    [Fact]
    public async Task ASmallContextIsLeftToGoColdAndAFullOneIsCompactedAtOnce()
    {
        Row("t0000001", "du1-watchtower", context: 60_000);
        _time.Advance(TimeSpan.FromMinutes(55));
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        Row("t0000001", "du1-watchtower", context: 750_000, lastResponse: _time.GetUtcNow());
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem();
        Handled().ShouldBe(["Compacted du1-watchtower"]);
    }

    [Fact]
    public async Task AWorkerWaitingOnItsJobIsCompactedWithWhatItWaitsOnAndOneNotWaitingIsNot()
    {
        Row("w0000001", "du1-tia9-parser", flight: "TIA9", leg: "parser");
        _time.Advance(TimeSpan.FromMinutes(50));

        // Idle with no job: the leg watch nudges it, so it is not the compactor's.
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        Job("w0000001");
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem().Args.ShouldBe([Compactor.JobFocus]);
        _state.Cards().Single(c => c.Kind == Compactor.Kind).Subject.ShouldBe("flight/TIA9/parser");
    }

    [Fact]
    public async Task AWatchtowerWithNothingToWatchIsLeftAlone()
    {
        // Its watchtower stops it once it has been quiet a while; a compaction would only wake it.
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed, LandedAt = Start });
        Row("t0000001", "du1-watchtower");
        _time.Advance(TimeSpan.FromHours(2));

        await _compactor.LookAsync(Ct);

        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASessionMidTurnAndAnInteractiveOneAreLeftAloneThoughTheyWaitOnJobs()
    {
        Row("w0000002", "busy-worker", state: SessionState.Working);
        Row("i0000003", "someones-terminal", kind: SessionKind.Interactive);
        Job("w0000002");
        _state.PutJob(new JobRow
        {
            Id = "j2", Command = "make", Cwd = "/r", Owner = "i0000003", State = JobState.Running, LogPath = "/tmp/j2.log", Timeout = TimeSpan.FromHours(1), StartedAt = Start,
        });
        _time.Advance(TimeSpan.FromHours(2));

        await _compactor.LookAsync(Ct);

        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedCompactionSaysWhyAndIsTriedAgainLater()
    {
        Row("t0000001", "du1-watchtower");
        _time.Advance(TimeSpan.FromMinutes(50));
        _refuse = "du1-watchtower is working, not idle";

        await _compactor.LookAsync(Ct);
        await _compactor.LookAsync(Ct);

        _said.Count.ShouldBe(1);
        Handled().ShouldHaveSingleItem().ShouldStartWith("Could not compact du1-watchtower; trying again in ");
        _refuse = null;
        _time.Advance(TimeSpan.FromMinutes(10));
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        Handled().ShouldContain(t => t.StartsWith("Compacted du1-watchtower", StringComparison.Ordinal));
    }
}
