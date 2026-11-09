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
    private readonly string _root = TestTemp.Create();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
    private readonly NodeState _state;
    private readonly JobRunner _jobs;
    private readonly List<string> _said = [];
    private bool _endedForGood;

    public JobRetellTests()
    {
        _state = new NodeState(new InMemoryJournal(), _clock);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), _clock)
        {
            RetellAfter = After,
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
            TestTemp.Delete(_root);
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
}
