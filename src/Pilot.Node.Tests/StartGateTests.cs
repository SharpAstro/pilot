using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The gate every session the node starts by itself goes through: held while the 5-hour window is spent, said once with
/// until when, fail-open without fresh figures, and a priority repo's own limit. The compactor and the watchtower, whose
/// starts it also holds, are tried here; the planner's and the reviewer's are in the leg and review tests, on real planes.
/// </summary>
public sealed class StartGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly StartGate _gate;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    private readonly NodeOptions _options = new("/tmp/n.sock", "/tmp/pilot2", MachinePrefix: "DU1");

    public StartGateTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _gate = new StartGate(_state, _time);
        // Something to watch: a flight in the air with a leg flying.
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = "w0000001" });
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    private void Usage(double fiveHour, TimeSpan? resetsIn = null, double? week = null, TimeSpan? age = null) =>
        _state.PutUsage(new UsageRow
        {
            ReadAt = _time.GetUtcNow() - (age ?? TimeSpan.Zero),
            FiveHour = new UsageWindow { UsedPercent = fiveHour, ResetsAt = _time.GetUtcNow() + (resetsIn ?? TimeSpan.FromHours(2)) },
            SevenDay = week is { } w ? new UsageWindow { UsedPercent = w, ResetsAt = _time.GetUtcNow() + TimeSpan.FromDays(3) } : null,
        });

    private CardRow[] Cards(string kind) => [.. _state.Cards().Where(c => c.Kind == kind)];

    [Fact]
    public void ASpentWindowHoldsAStartAndSaysUntilWhenButNoFiguresOrStaleOnesHoldNothing()
    {
        _gate.Check(90).ShouldBeNull();

        Usage(89, resetsIn: TimeSpan.FromHours(2));
        _gate.Check(90).ShouldBeNull();

        Usage(95, resetsIn: TimeSpan.FromHours(2));
        var held = _gate.Check(90).ShouldNotBeNull();
        held.Resets.ShouldBe(_time.GetUtcNow().AddHours(2));
        held.Say.ShouldStartWith("the 5-hour window is 95 % spent (the limit is 90 %); it goes on after ");
        _gate.Check(98).ShouldBeNull();

        // The figures are old: nothing says the window is still spent, so the start goes ahead.
        _time.Advance(StartGate.FreshFor + TimeSpan.FromMinutes(1));
        _gate.Check(90).ShouldBeNull();
    }

    [Fact]
    public void AHeldStartSaysSoOncePerWindowAndOnceItHasResetItIsLetGo()
    {
        Usage(95, resetsIn: TimeSpan.FromHours(2));

        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "TIA9 waits to start its reviewer").ShouldBeTrue();
        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "TIA9 waits to start its reviewer").ShouldBeTrue();
        var card = Cards("kind").ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldStartWith("TIA9 waits to start its reviewer: the 5-hour window is 95 % spent");

        _time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        Usage(3, resetsIn: TimeSpan.FromHours(5));
        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "TIA9 waits to start its reviewer").ShouldBeFalse();

        // Spent again in the next window: said again, as a new wait.
        Usage(96, resetsIn: TimeSpan.FromHours(5));
        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "TIA9 waits to start its reviewer").ShouldBeTrue();
        Cards("kind").Length.ShouldBe(2);
    }

    [Fact]
    public void TheWeekRingsOncePerWindowAndHoldsNothing()
    {
        Usage(10, week: 99);

        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "x").ShouldBeFalse();
        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "x").ShouldBeFalse();

        var ring = Cards(StartGate.WeekKind).ShouldHaveSingleItem();
        (ring.Bin, ring.Title).ShouldBe((CardBin.Ring, $"The 7-day window is 99 % spent; it resets {_time.GetUtcNow().AddDays(3).ToLocalTime():ddd HH:mm}"));

        // Under its limit, it rings nothing.
        _gate.RingWeek(100);
        Cards(StartGate.WeekKind).Length.ShouldBe(1);
    }

    [Fact]
    public void TheWeekLimitIsASettingThatTakesPercentAndDefaultsToNinety()
    {
        new RoleModels(null).WeekLimit.ShouldBe(90);
        var models = new RoleModels(NodeSettings.With(new NodeSettings(), "week-limit", ["75"]).Settings);
        models.WeekLimit.ShouldBe(75);
        NodeSettings.With(new NodeSettings(), "week-limit", ["101"]).Error.ShouldBe("week-limit takes the 7-day window's share in percent, above 0 and up to 100");
        NodeSettings.With(new NodeSettings { WeekLimit = 75 }, "week-limit", ["default"]).Settings!.WeekLimit.ShouldBeNull();
    }

    [Fact]
    public async Task ACompactionHeldByTheWindowSaysUntilWhenAndGoesByItselfOnceItHasReset()
    {
        var compactor = new Compactor(_state, new Watchtower(_state, _workers, Run, _options, _time), Run, _time) { Models = new RoleModels(null) };
        Session("w0000001", "du1-tia9-parser", flight: "TIA9", leg: "parser");
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = "/r/w", Owner = "w0000001", State = JobState.Running, LogPath = "/tmp/j1.log", Timeout = TimeSpan.FromHours(2), StartedAt = Start,
        });
        _time.Advance(TimeSpan.FromMinutes(50));
        Usage(95, resetsIn: TimeSpan.FromHours(1));

        await compactor.LookAsync(Ct);
        await compactor.LookAsync(Ct);

        _said.ShouldBeEmpty();
        var wait = Cards(Compactor.Kind).ShouldHaveSingleItem();
        wait.Title.ShouldStartWith("Compaction of du1-tia9-parser waits (idle 50 min with 180k of context, before its prompt cache went cold): the 5-hour window is 95 % spent");

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Usage(2, resetsIn: TimeSpan.FromHours(5));
        await compactor.LookAsync(Ct);

        _said.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Compact);
        Cards(Compactor.Kind).Select(c => c.Title).ShouldContain(t => t.StartsWith("Compacted du1-tia9-parser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACompactionOfAPriorityReposFlightGoesOnPastTheStartLimitAndOneWithNoFiguresGoesToo()
    {
        var settings = NodeSettings.With(new NodeSettings(), "priority-repos", ["tianwen"]).Settings;
        var compactor = new Compactor(_state, new Watchtower(_state, _workers, Run, _options, _time), Run, _time) { Models = new RoleModels(settings) };
        Session("w0000001", "du1-tia9-parser", flight: "TIA9", leg: "parser");
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = "/r/w", Owner = "w0000001", State = JobState.Running, LogPath = "/tmp/j1.log", Timeout = TimeSpan.FromHours(2), StartedAt = Start,
        });
        _time.Advance(TimeSpan.FromMinutes(50));

        // 95 % is past the start limit (90) and under the priority limit (98).
        Usage(95, resetsIn: TimeSpan.FromHours(1));
        await compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem();

        // A session of no flight takes the plain limit.
        Session("w0000002", "du1-solo", flight: null, leg: null);
        _state.PutJob(new JobRow
        {
            Id = "j2", Command = "make", Cwd = "/r/w", Owner = "w0000002", State = JobState.Running, LogPath = "/tmp/j2.log", Timeout = TimeSpan.FromHours(2), StartedAt = Start,
        });
        await compactor.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        Cards(Compactor.Kind).ShouldContain(c => c.Title.StartsWith("Compaction of du1-solo waits", StringComparison.Ordinal));

        // No figures at all: it goes ahead.
        var open = new NodeState(new InMemoryJournal(), _time);
        _gate.Check(0.1).ShouldNotBeNull();
        new StartGate(open, _time).Check(0.1).ShouldBeNull();
    }

    [Fact]
    public async Task TheWatchtowerKeptByTheNodeWaitsForTheWindowAndStartsOnceItHasResetButARestartAskedForGoesAhead()
    {
        var tower = new Watchtower(_state, _workers, Run, _options, _time) { Pilot = "pilot" };
        Usage(95, resetsIn: TimeSpan.FromHours(1));

        await tower.LookAsync(Ct);
        await tower.LookAsync(Ct);

        _workers.Started.ShouldBeEmpty();
        var wait = Cards(Watchtower.Waits).ShouldHaveSingleItem();
        (wait.Bin, wait.Subject).ShouldBe((CardBin.Handled, Verbs.WatchtowerTarget));
        wait.Title.ShouldStartWith("The watchtower waits to start: the 5-hour window is 95 % spent");

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Usage(2, resetsIn: TimeSpan.FromHours(5));
        await tower.LookAsync(Ct);
        _workers.Started.ShouldHaveSingleItem();

        // A person's restart is theirs: it goes ahead over a spent window.
        Usage(99, resetsIn: TimeSpan.FromHours(5));
        (await tower.RestartAsync("by hand", Ct)).ShouldBeNull();
        _workers.Started.Count.ShouldBe(2);
    }

    private void Session(string id, string name, string? flight, string? leg) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/r/w", Role = SessionRole.Worker,
            Flight = flight, Leg = leg, ContextTokens = 180_000, ContextWindow = 1_000_000, State = SessionState.Idle, SpendWeighted = 0, LastResponseAt = Start, StartedAt = Start,
        });
}
