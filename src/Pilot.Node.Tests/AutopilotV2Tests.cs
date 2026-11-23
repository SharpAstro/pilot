using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1285's last acceptance criterion, end to end on the in-memory node (fake launcher, fake clock, no real process): with autopilot on, a
/// stalled flight is moved, a repeated failure is filed as an issue plus a flight, and only a decision that is truly the person's rings,
/// once, saying what was tried; the digest lists the three. With it off (the default) none of it happens.
/// </summary>
public sealed class AutopilotV2Tests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly RoleModels _models;
    private readonly Investigator _investigator;

    public AutopilotV2Tests() : this(on: true)
    {
    }

    private AutopilotV2Tests(bool on)
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        var settings = new NodeSettings();
        if (on)
        {
            settings = NodeSettings.With(settings, NodeSettings.AutopilotKey, ["on"]).Settings.ShouldNotBeNull();
        }
        _models = new RoleModels(settings);
        _investigator = new Investigator(_state, _workers, _models, new StartGate(_state, _time), new FailureLedger(), _time)
        {
            Machine = "DE1", DefaultFolder = "C:/watchtower", Pilot = "pilot", RepoOf = _ => "C:/r/pilot",
        };
        _state.Investigations = _investigator;
    }

    private static FlightRow Flight(string code, TimeSpan age) => new()
    {
        Code = code, Repo = "pilot", RepoPath = "C:/r/pilot", Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"C:/r/pilot/.claude/worktrees/{code}",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = Start - age,
    };

    private int Rings() => _state.Cards().Count(c => c.Bin == CardBin.Ring);

    [Fact]
    public async Task AStalledFlightIsMovedARepeatIsFiledAndOnlyThePersonsDecisionRingsOnce()
    {
        // 1. A stalled flight: an investigator starts on it and moves it (a go-around), recorded with its why. No ring.
        _state.PutFlight(Flight("PIL1", TimeSpan.FromDays(4)));
        _investigator.Look();
        await _investigator.SettledAsync();
        _workers.Started.Single(s => s.Name == "de1-investigate-pil1").Model.ShouldBe("opus");
        _investigator.Outcome("de1-investigate-pil1", "PIL1", Investigator.ActionOutcome, "sent a go-around: job j962 shows the worker hung on a prompt").ShouldBeNull();
        Rings().ShouldBe(0);

        // 2. A failure that came twice: a find-the-cause task starts an investigator that files an issue plus a flight. No ring.
        _state.RaiseCard(CardBin.Handled, CardKinds.RepeatTask, "flight/PIL2", "Find the cause: pr-red on flight/PIL2 happened twice", "[pair: c1 + c2]");
        _investigator.Look();
        await _investigator.SettledAsync();
        _investigator.Outcome("de1-investigate-pil2", "PIL2", Investigator.IssueOutcome, "filed #1300 and flight PIL1300: the leg check filters a removed class (j970)").ShouldBeNull();
        Rings().ShouldBe(0);

        // 3. A decision truly the person's: a card bound for the ring is held, and the run's ring outcome lets it through, once, with what was tried.
        var card = _state.RaiseCard(CardBin.Ring, "worker-idle", "flight/PIL3/node", "PIL3 needs a token", "the GitHub token expired (job j971)",
            [new CardChoice { Label = "Renew", Verb = Verbs.Say, Target = "session/abc" }]);
        _state.Cards().Single(c => c.Id == card.Id).Bin.ShouldBe(CardBin.ForYou);
        Rings().ShouldBe(0);
        await _investigator.SettledAsync();
        _investigator.Outcome("de1-investigate-pil3", "PIL3", Investigator.RingOutcome, "Tried: a rerun of checks (j971). Needed: only you can renew the GitHub token.").ShouldBeNull();

        Rings().ShouldBe(1);
        _state.Cards().Single(c => c.Bin == CardBin.Ring).Detail.ShouldNotBeNull().ShouldContain("Tried: a rerun of checks");

        // The digest says what was done, filed and escalated.
        var detail = new Digest(_state, _time) { Autopilot = () => _models.Autopilot }.Compose(Start.AddHours(-1), Start.AddHours(1)).Detail;
        detail.ShouldContain("flight/PIL1: action");
        detail.ShouldContain("flight/PIL2: filed #1300");
        detail.ShouldContain("flight/PIL3: Tried: a rerun of checks");
    }

    [Fact]
    public async Task WithAutopilotOffByDefaultNothingStartsNothingIsHeldAndTheDigestHasNoPart()
    {
        var off = new AutopilotV2Tests(on: false);
        off._models.Autopilot.ShouldBeFalse();
        off._state.PutFlight(Flight("PIL1", TimeSpan.FromDays(4)));
        off._state.RaiseCard(CardBin.Handled, CardKinds.RepeatTask, "flight/PIL2", "Find the cause: pr-red happened twice", "[pair: c1 + c2]");
        var card = off._state.RaiseCard(CardBin.Ring, "worker-idle", "flight/PIL3/node", "PIL3 needs a token", "expired",
            [new CardChoice { Label = "Renew", Verb = Verbs.Say, Target = "session/abc" }]);

        off._investigator.Look();
        await off._investigator.SettledAsync();

        off._workers.Started.ShouldBeEmpty();
        off._state.Cards().Single(c => c.Id == card.Id).Bin.ShouldBe(CardBin.Ring);
        new Digest(off._state, off._time) { Autopilot = () => off._models.Autopilot }.Compose(Start.AddHours(-1), Start.AddHours(1)).Detail.ShouldNotContain("Autopilot:");
    }
}
