using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1190: a flight's planning-stall clock starts with its planner, never while its plan waits in admission.</summary>
public sealed class PlanQueueStallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly LegWatch _watch;
    private readonly List<CommandRequest> _ran = [];
    private string? _refuse;

    public PlanQueueStallTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _watch = new LegWatch(_state, Run, _time)
        {
            Pilot = "pilot --node-socket \"/tmp/n.sock\"",
            StartedPlanner = _ => null,
            StopPlanner = (_, _) => Task.CompletedTask,
            RestartPlanner = (_, _) => Task.CompletedTask,
        };
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _ran.Add(command);
        return Task.FromResult(_state.Complete(command, _refuse));
    }

    private void Flight(string? queued = null, DateTimeOffset? admitted = null) => _state.PutFlight(new FlightRow
    {
        Code = "PIL9", Repo = "pilot", RepoPath = "/r", Issue = 9, Branch = "flight/pil9", Worktree = "/r/pil9", Base = "origin/main",
        State = FlightState.Flying, CreatedAt = Start, Queued = queued, AdmittedAt = admitted,
    });

    private void Admit() => _state.UpdateFlight("PIL9", f => f with { Queued = null, AdmittedAt = _time.GetUtcNow() });

    private void Planner(SessionState state, DateTimeOffset? lastResponse) => _state.PutSession(new SessionRow
    {
        Id = "e5f6a7b8", SessionId = "e5f6a7b8-0000-4000-8000-000000000000", Name = "du1-pil9-plan", Kind = SessionKind.Background,
        Cwd = "/r/pil9", Role = SessionRole.Planner, Flight = "PIL9", ContextTokens = 0, ContextWindow = 200_000,
        State = state, SpendWeighted = 0, LastResponseAt = lastResponse, StartedAt = Start,
    });

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private CardRow[] Handled() => [.. _state.Cards().Where(c => c.Bin == CardBin.Handled)];

    private CardRow[] AnyPlanStall() => [.. _state.Cards().Where(c => c.Kind == LegWatch.PlanStalled)];

    private async Task NothingAtLooks()
    {
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(7));
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _watch.LookAsync(Ct);
        AnyPlanStall().ShouldBeEmpty();
        _ran.Where(c => c.Verb == Verbs.PlanFlight).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightAdmittedAfterThreeHoursBehindAnotherPlannerIsNotCardedBeforeItsPlannerIsListed()
    {
        Flight("waiting for an Opus slot: 1 of 1 flying: du1-pil8-plan");
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromHours(3));
        await _watch.LookAsync(Ct);
        Admit();
        await NothingAtLooks();
    }

    [Fact]
    public async Task AFlightAdmittedBehindAnotherFlightOnItsRepoIsNotCardedBeforeItsPlannerIsListed()
    {
        Flight("waiting for the repo tianwen: TIA2507 holds it");
        _time.Advance(TimeSpan.FromHours(2));
        await _watch.LookAsync(Ct);
        Admit();
        await NothingAtLooks();
    }

    [Fact]
    public async Task TheNoPlannerClockStartsAtAdmissionAndTheFreshPlannerIsStartedAfterThePlanGrace()
    {
        Flight("waiting for an Opus slot: 1 of 1 flying: du1-pil8-plan");
        _time.Advance(TimeSpan.FromHours(3));
        Admit();
        _time.Advance(_watch.PlanGrace - TimeSpan.FromSeconds(1));
        await _watch.LookAsync(Ct);
        AnyPlanStall().ShouldBeEmpty();
        _ran.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(2));
        await _watch.LookAsync(Ct);
        _ran.Single().Verb.ShouldBe(Verbs.PlanFlight);
        var handled = Handled().ShouldHaveSingleItem();
        (handled.Kind, handled.Title).ShouldBe((LegWatch.PlanStalled, "PIL9's planner started afresh"));
    }

    [Fact]
    public async Task AnIdlePlannerWithNoLegsIsStillToldThenRung()
    {
        Flight();
        Planner(SessionState.Idle, Start);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        Handled().ShouldHaveSingleItem().Title.ShouldBe("PIL9's planner was told to hand in its legs");

        _time.Advance(TimeSpan.FromMinutes(4));
        await _watch.LookAsync(Ct);
        var ring = Open().Where(c => c.Bin == CardBin.Ring).ShouldHaveSingleItem();
        ring.Detail.ShouldNotBeNull().ShouldContain("told the idle planner");
    }

    [Fact]
    public async Task AnOpenPlanStalledRingIsClosedOnTheNextLookOnceThePlannerIsWorking()
    {
        Flight();
        _refuse = "PIL9 is planning already";
        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);
        Open().Where(c => c.Kind == LegWatch.PlanStalled && c.Bin == CardBin.Ring).ShouldHaveSingleItem();

        Planner(SessionState.Working, Start);
        await _watch.LookAsync(Ct);
        Open().Where(c => c.Kind == LegWatch.PlanStalled).ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedFreshPlannerNamesTheRefusalAndNeverClaimsTheNodeToldOrReplacedOne()
    {
        Flight();
        _refuse = "PIL9 is planning already";
        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);

        var ring = Open().Where(c => c.Bin == CardBin.Ring).ShouldHaveSingleItem();
        ring.Detail.ShouldNotBeNull().ShouldContain("asked for a fresh planner and was refused: PIL9 is planning already");
        AnyPlanStall().ShouldAllBe(c => !(c.Detail ?? "").Contains("told or replaced"));
    }
}
