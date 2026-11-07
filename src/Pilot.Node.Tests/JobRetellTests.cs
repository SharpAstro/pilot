using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A told job whose owner sits idle without acting is told again once, then rings (#756).</summary>
public sealed class JobRetellTests : IDisposable
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly TimeSpan After = TimeSpan.FromMinutes(4);
    private readonly string _root = Directory.CreateTempSubdirectory("retell-").FullName;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
    private readonly NodeState _state;
    private readonly JobRunner _jobs;
    private readonly TurnOutage _outage;
    private readonly List<string> _said = [];
    private bool _endedForGood;

    public JobRetellTests()
    {
        _state = new NodeState(new InMemoryJournal(), _clock);
        _outage = new TurnOutage(_state);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), _clock)
        {
            RetellAfter = After,
            Outage = _outage,
            Say = (command, _) =>
            {
                _said.Add(command.Args[0]);
                return Task.FromResult(_state.Complete(command, null, way: SayWays.Relayed));
            },
            EndedForGood = _ => _endedForGood,
        };
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
    }

    public void Dispose()
    {
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Session(SessionState sessionState, DateTimeOffset? lastResponse) =>
        _state.PutSession(new SessionRow
        {
            Id = "w0000001", SessionId = "w0000001-0000-4000-8000-000000000000", Name = "de1-retell-w1", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.Worker, Flight = "RTL1", Leg = "one", ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0, State = sessionState,
            LastResponseAt = lastResponse, StartedAt = _clock.GetUtcNow().AddHours(-2),
        });

    private async Task<JobRow> TellNowAsync(bool whileWorking = false)
    {
        var now = _clock.GetUtcNow();
        var row = new JobRow
        {
            Id = "j1", Command = "echo hi", Cwd = _root, Owner = "w0000001", State = JobState.Succeeded, ExitCode = 0,
            LogPath = Path.Combine(_root, "j1.log"), Timeout = TimeSpan.FromMinutes(1), StartedAt = now.AddMinutes(-2), EndedAt = now.AddMinutes(-1),
        };
        _state.PutJob(row);
        if (whileWorking)
        {
            Session(SessionState.Working, now.AddMinutes(-10));
        }
        await _jobs.TellAsync(row, TestContext.Current.CancellationToken);
        _said.Clear();
        return _state.Jobs().Single();
    }

    private Task<bool> Sweep() => _jobs.RetellSilentAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task IdleAndSilentIsRetoldOnceThenRings()
    {
        await TellNowAsync();

        _clock.Advance(After - TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _state.Jobs().Single().RetoldAt.ShouldNotBeNull();
        _state.Cards().ShouldBeEmpty();

        // Never a third tell; one ring once the second also goes unanswered.
        _clock.Advance(After);
        (await Sweep()).ShouldBeFalse();
        await Sweep();
        _said.Count.ShouldBe(1);
        var card = _state.Cards().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, JobRunner.UnansweredKind, Verbs.JobTarget("j1")));
    }

    [Fact]
    public async Task ActedAfterTheTellIsNotRetold()
    {
        await TellNowAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());

        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task RelayedIntoAWorkingSessionThatThenIdlesWithoutActingIsRetold()
    {
        await TellNowAsync(whileWorking: true);
        _state.Jobs().Single().ToldWhileWorking.ShouldBe(true);

        // The busy turn ends: a response after the tell, but not one that acted on it.
        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());
        _clock.Advance(After - TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RelayedIntoAWorkingSessionThatThenActsIsNotRetold()
    {
        await TellNowAsync(whileWorking: true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());
        (await Sweep()).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());
        _clock.Advance(After * 2);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOwnerThatEndedForGoodIsNotRetold()
    {
        await TellNowAsync();
        _endedForGood = true;

        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    // PIL791: sessions that are not taking turns cannot act on a tell, so tells and retells wait for them and are redone once.

    private JobRow Seed(string id, DateTimeOffset? toldAt = null)
    {
        var now = _clock.GetUtcNow();
        var row = new JobRow
        {
            Id = id, Command = "echo hi", Cwd = _root, Owner = "w0000001", State = JobState.Succeeded, ExitCode = 0,
            LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(1), StartedAt = now.AddMinutes(-2), EndedAt = now.AddMinutes(-1),
            Told = toldAt is not null, ToldAt = toldAt, ToldWay = toldAt is null ? null : SayWays.Relayed,
        };
        _state.PutJob(row);
        return row;
    }

    /// <summary>The session takes a turn now, which ends an open outage; its window is (when it began, now).</summary>
    private (DateTimeOffset Since, DateTimeOffset Until) EndOutage()
    {
        Session(SessionState.Idle, _clock.GetUtcNow());
        _outage.Look(_state.Sessions(), _clock.GetUtcNow());
        return _outage.LastWindow!.Value;
    }

    private Task<bool> TellSweep() => _jobs.TellUntoldAsync(null, TestContext.Current.CancellationToken);

    private CardRow[] Cards(string kind) => [.. _state.Cards().Where(c => c.Kind == kind)];

    [Fact]
    public async Task ATellAndARetellInsideAnOutageAreToldOnceMoreWhenItEndsAndTheRetellStartsFromThere()
    {
        // The PIL771 timeline: told 19:54, the login changes 19:55, retold 19:59 (before anything knew), the sessions take turns again 20:05.
        await TellNowAsync();
        var told = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(5));
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _said.Clear();
        var retold = _clock.GetUtcNow();

        // The node sees the account change a minute later; the outage began when the login did.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var since = told.AddMinutes(1);
        _outage.AccountChanged(since);

        // The second silence would ring at retold + 4 minutes; with no session able to answer, nothing is said and nothing rings.
        _clock.Advance(TimeSpan.FromMinutes(4));
        (await Sweep()).ShouldBeFalse();
        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        Cards(JobRunner.UnansweredKind).ShouldBeEmpty();
        _state.Jobs().Single().RetoldAt.ShouldBe(retold);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var window = EndOutage();
        window.ShouldBe((since, _clock.GetUtcNow()));
        _said.ShouldBeEmpty();

        // Just after it ended: the tell is made once more, saying why, and the retell ladder starts from it.
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        var redone = _clock.GetUtcNow();
        var said = _said.ShouldHaveSingleItem();
        said.ShouldStartWith("Redo:");
        said.ShouldContain("not taking turns");
        said.ShouldContain("Your job j1");
        var row = _state.Jobs().Single();
        (row.Told, row.ToldAt, row.RetoldAt, row.RetoldWay, row.RetellRung).ShouldBe((true, redone, null, null, null));
        Cards(JobRunner.UnansweredKind).ShouldBeEmpty();

        // Once only, by either sweep.
        (await Sweep()).ShouldBeFalse();
        (await TellSweep()).ShouldBeFalse();
        _said.Count.ShouldBe(1);

        // The ladder from the redo: a repeat after the usual silence, then one ring for the second.
        _clock.Advance(After - TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        _said.Count.ShouldBe(2);
        _said[1].ShouldStartWith("Repeat:");
        Cards(JobRunner.UnansweredKind).ShouldBeEmpty();
        _clock.Advance(After);
        await Sweep();
        _said.Count.ShouldBe(2);
        Cards(JobRunner.UnansweredKind).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ATellAskedDuringAnOutageIsNotSentAndGoesOutWhenItEnds()
    {
        _outage.AccountChanged(_clock.GetUtcNow());
        var row = Seed("j1");

        await _jobs.TellAsync(row, TestContext.Current.CancellationToken);
        (await TellSweep()).ShouldBeFalse();
        _clock.Advance(TimeSpan.FromMinutes(2));
        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Jobs().Single().Told.ShouldBeFalse();
        Cards(JobRunner.UntoldKind).ShouldBeEmpty();

        // Its one try was not used up by the holds: the sweep after the outage sends it, as a first tell and not a redo.
        EndOutage();
        (await TellSweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Your job j1");
        _state.Jobs().Single().Told.ShouldBeTrue();
        (await TellSweep()).ShouldBeFalse();
        (await Sweep()).ShouldBeFalse();
        _said.Count.ShouldBe(1);
        Cards(JobRunner.UntoldKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyRowsToldInsideTheWindowAreRedone()
    {
        var before = Seed("j0", _clock.GetUtcNow().AddMinutes(-30));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var since = _clock.GetUtcNow();
        _outage.AccountChanged(since);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Seed("j1", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(2));
        EndOutage();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var after = Seed("j2", _clock.GetUtcNow());

        (await TellSweep()).ShouldBeTrue();

        _said.ShouldHaveSingleItem().ShouldContain("Your job j1");
        var jobs = _state.Jobs().ToDictionary(j => j.Id);
        jobs["j0"].ToldAt.ShouldBe(before.ToldAt);
        jobs["j2"].ToldAt.ShouldBe(after.ToldAt);
        jobs["j1"].ToldAt.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public async Task ARingRaisedInsideTheWindowIsClosedAndItsRowStartsTheLadderAgain()
    {
        var toldAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var since = _clock.GetUtcNow();
        _outage.AccountChanged(since);
        var row = Seed("j1", toldAt) with { RetoldAt = since.AddMinutes(1), RetoldWay = SayWays.Relayed, RetellRung = true };
        _state.PutJob(row);
        _state.RaiseCard(CardBin.Ring, JobRunner.UnansweredKind, Verbs.JobTarget("j1"), "j1 ended and w1 was told twice but has not acted", "two tells, no turn");
        _clock.Advance(TimeSpan.FromMinutes(3));
        EndOutage();
        _clock.Advance(TimeSpan.FromSeconds(1));

        (await TellSweep()).ShouldBeTrue();

        var redone = _state.Jobs().Single();
        (redone.RetoldAt, redone.RetellRung).ShouldBe((null, null));
        redone.ToldAt.ShouldBe(_clock.GetUtcNow());
        Cards(JobRunner.UnansweredKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ATellToNobodyAndARowTheOwnerEndedForAreNotRedone()
    {
        var since = _clock.GetUtcNow();
        _outage.AccountChanged(since);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Seed("j1", _clock.GetUtcNow());
        _state.PutJob(_state.Jobs().Single() with { ToldWay = "owner-ended" });
        _clock.Advance(TimeSpan.FromMinutes(1));
        EndOutage();
        _clock.Advance(TimeSpan.FromSeconds(1));

        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARedoWaitsWhileAnotherOutageIsOpenAndStillCoversTheFirstWindow()
    {
        _outage.AccountChanged(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Seed("j1", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        EndOutage();
        _clock.Advance(TimeSpan.FromMinutes(1));

        // A second outage opens before the first one's redo went: it is held, not lost.
        _outage.AccountChanged(_clock.GetUtcNow());
        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromMinutes(1));
        EndOutage();
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await TellSweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldContain("Your job j1");
        (await TellSweep()).ShouldBeFalse();
        _said.Count.ShouldBe(1);
    }
}
