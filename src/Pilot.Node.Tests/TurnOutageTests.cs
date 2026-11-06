using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>An account switch or a general stall is one outage with one card, and the first-turn ladders hold while it lasts (PIL791).</summary>
public sealed class TurnOutageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 6, 19, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly TurnOutage _outage;
    private readonly LegWatch _watch;
    private readonly List<string> _restarted = [];
    private readonly List<string> _stopped = [];

    public TurnOutageTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _outage = new TurnOutage(_state, _time);
        _watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time)
        {
            Pilot = "pilot",
            Outage = _outage,
            StopSession = (job, _) => { _stopped.Add(job); return Task.CompletedTask; },
            RestartWorker = (flight, leg, _) => { _restarted.Add($"{flight}/{leg}"); return Task.CompletedTask; },
        };
        foreach (var (name, job, order) in new[] { ("parser", "a1b2c3d4", 1), ("solver", "e5f6a7b8", 2) })
        {
            _state.PutLeg(new LegRow
            {
                Flight = "TIA1", Name = name, Order = order, Goal = name, DoneWhen = "it works", Checklist = [new CheckItem { Text = "Do it" }],
                State = LegState.Flying, Worker = "du1-tia1-" + name, WorkerJob = job, StartedAt = Start,
            });
        }
    }

    private void Session(string id, string leg, long context = 0, DateTimeOffset? response = null) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "du1-tia1-" + leg, Kind = SessionKind.Background, Cwd = "/work/tia1",
        Role = SessionRole.Worker, Flight = "TIA1", Leg = leg, ContextTokens = context, ContextWindow = 200_000, State = SessionState.Working,
        SpendWeighted = 0, LastResponseAt = response, StartedAt = Start,
    });

    private CardRow[] Open() => _state.Cards(openOnly: true);

    [Fact]
    public void AnAccountChangeOpensOneOutageWithOneCardAndATurnLaterEndsItAndKeepsTheWindow()
    {
        _outage.IsOpen.ShouldBeFalse();
        _time.Advance(TimeSpan.FromMinutes(55));
        _outage.AccountChanged(_time.GetUtcNow());
        _outage.AccountChanged(_time.GetUtcNow());

        _outage.IsOpen.ShouldBeTrue();
        _outage.Since.ShouldBe(Start.AddMinutes(55));
        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, "turns-outage", Verbs.NodeTarget));
        card.Title.ShouldStartWith("Sessions are not taking turns since ");
        card.Title.ShouldEndWith("the account changed");

        // A session whose last turn was before the outage does not end it.
        Session("a1b2c3d4", "parser", context: 5_000, response: Start.AddMinutes(10));
        _outage.Look(_state.Sessions(), _time.GetUtcNow());
        _outage.IsOpen.ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(3));
        Session("a1b2c3d4", "parser", context: 5_000, response: _time.GetUtcNow());
        _outage.Look(_state.Sessions(), _time.GetUtcNow());

        _outage.IsOpen.ShouldBeFalse();
        Open().ShouldBeEmpty();
        _state.Cards().Single(c => c.Kind == TurnOutage.CardKind).State.ShouldBe(CardState.Closed);
        _outage.LastWindow.ShouldBe((Start.AddMinutes(55), Start.AddMinutes(58)));
    }

    [Fact]
    public void ARealLoginChangeSeenByTheUsageReaderOpensTheOutage()
    {
        var folder = Directory.CreateTempSubdirectory("turn-outage").FullName;
        try
        {
            var account = Path.Combine(folder, ".claude.json");
            File.WriteAllText(account, """{"oauthAccount":{"emailAddress":"a@example.com"}}""");
            var reader = new UsageReader(_state, Path.Combine(folder, "rate-limits.json"), _time) { AccountFile = account, Outage = _outage };
            reader.Read();
            _outage.IsOpen.ShouldBeFalse();

            File.WriteAllText(account, """{"oauthAccount":{"emailAddress":"b@example.com"}}""");
            _time.Advance(TimeSpan.FromMinutes(1));
            reader.Read();

            _outage.IsOpen.ShouldBeTrue();
            _outage.Since.ShouldBe(_time.GetUtcNow());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TwoSubjectsWithNoFirstTurnWithinTenMinutesOpenAnOutageAndOneAloneDoesNot()
    {
        _outage.Observe("flight/TIA1/parser", Start);
        _outage.Observe("flight/TIA1/parser", Start.AddMinutes(1));
        _outage.IsOpen.ShouldBeFalse();
        Open().ShouldBeEmpty();

        _outage.Observe("flight/TIA1/solver", Start.AddMinutes(4));
        _outage.IsOpen.ShouldBeTrue();
        Open().ShouldHaveSingleItem().Title.ShouldContain("2 sessions showed no first turn");
    }

    [Fact]
    public void TwoSubjectsMoreThanTenMinutesApartAreTwoStuckSessionsNotAnOutage()
    {
        _outage.Observe("flight/TIA1/parser", Start);
        _outage.Observe("flight/TIA1/solver", Start.AddMinutes(11));

        _outage.IsOpen.ShouldBeFalse();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task StalledSessionsOpenOneOutageAndGetNoRestartAndNoPerSessionCardWhileItLasts()
    {
        Session("a1b2c3d4", "parser");
        Session("e5f6a7b8", "solver");

        // Past the first-turn grace: both would be restarted one by one; instead the second one's stall opens the outage first.
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);

        _outage.IsOpen.ShouldBeTrue();
        // The first ladder to look already acted (one subject alone is a stuck session); the second is held and one outage card is up.
        _restarted.Count.ShouldBeLessThanOrEqualTo(1);
        Open().Where(c => c.Kind == TurnOutage.CardKind).ShouldHaveSingleItem();
        var restartsBefore = _restarted.Count;

        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(4));
            await _watch.LookAsync(Ct);
        }

        _restarted.Count.ShouldBe(restartsBefore);
        _stopped.ShouldBeEmpty();
        Open().Select(c => c.Kind).ShouldBe([TurnOutage.CardKind]);
        _state.Cards().Count(c => c.Kind == TurnOutage.CardKind).ShouldBe(1);
        _state.Cards().Where(c => c.Kind is LegWatch.NeverTurned or LegWatch.ReviewStalled or LegWatch.PlanStalled && c.Bin == CardBin.Ring).ShouldBeEmpty();
    }

    [Fact]
    public async Task ATurnAfterTheOutageStartedEndsItThroughTheWatchAndClosesTheCard()
    {
        Session("a1b2c3d4", "parser");
        Session("e5f6a7b8", "solver");
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        _outage.IsOpen.ShouldBeTrue();
        var since = _outage.Since!.Value;

        // The first look after opening takes the baseline; a later turn (context grows) ends it.
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _outage.IsOpen.ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(1));
        Session("e5f6a7b8", "solver", context: 12_000);
        await _watch.LookAsync(Ct);

        _outage.IsOpen.ShouldBeFalse();
        Open().Where(c => c.Kind == TurnOutage.CardKind).ShouldBeEmpty();
        _outage.LastWindow.ShouldBe((since, _time.GetUtcNow()));
    }
}
