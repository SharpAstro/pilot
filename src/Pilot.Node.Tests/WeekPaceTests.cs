using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The week-pace guard: off unless set; set, a start waits while the 7-day window is spent faster than an even pace plus a
/// margin, saying used %, elapsed % and the margin on one card, except for a priority repo and the watchtower, and it lifts once
/// the pace has caught up. Fake usage figures and a manual clock.
/// </summary>
public sealed class WeekPaceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly StartGate _gate;

    public WeekPaceTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _gate = new StartGate(_state, _time);
    }

    // The window ends in three days from Start (a fixed reset), so at Start four of seven days (57.1 %) have elapsed.
    private void Usage(double week, double fiveHour = 5) =>
        _state.PutUsage(new UsageRow
        {
            ReadAt = _time.GetUtcNow(),
            FiveHour = new UsageWindow { UsedPercent = fiveHour, ResetsAt = Start + TimeSpan.FromHours(2) },
            SevenDay = new UsageWindow { UsedPercent = week, ResetsAt = Start + TimeSpan.FromDays(3) },
        });

    private static RoleModels Models(double? pace, params string[] priority) =>
        new(new NodeSettings { WeekPace = pace, PriorityRepos = priority.Length > 0 ? priority : null });

    private bool Hold(RoleModels models, string repo = "tianwen") =>
        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "TIA9 waits to start parser", models.WeekPaceFor(repo));

    [Fact]
    public void WithWeekPaceUnsetNothingWaitsHoweverFastTheWeekIsSpent()
    {
        Usage(week: 80);

        Hold(Models(null)).ShouldBeFalse();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public void ASetPaceHoldsAStartAheadOfAnEvenPaceWithOneCardGivingTheNumbersAndLiftsWhenItCatchesUp()
    {
        var models = Models(10);
        Usage(week: 70);

        Hold(models).ShouldBeTrue();
        Hold(models).ShouldBeTrue();

        var card = _state.Cards().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldStartWith("TIA9 waits to start parser: the 7-day window is 70 % spent but only 57 % elapsed (the pace margin is 10 points)");

        // No more spend: 70 - 10 = 60 % of the week is 4.2 days in, so a day on (5 days in, 71 %) the pace has caught up.
        _time.Advance(TimeSpan.FromDays(1));
        Usage(week: 70);
        Hold(models).ShouldBeFalse();
    }

    [Fact]
    public void SpendWhileHeldDoesNotRaiseAnotherCard()
    {
        var models = Models(10);
        Usage(week: 70);
        Hold(models).ShouldBeTrue();

        // The others keep spending while the start waits: the figure moves, the card is the same.
        _time.Advance(TimeSpan.FromMinutes(1));
        Usage(week: 71);
        Hold(models).ShouldBeTrue();

        _state.Cards().ShouldHaveSingleItem();
    }

    [Fact]
    public void WithinTheMarginAStartGoesAhead()
    {
        Usage(week: 67);
        Hold(Models(10)).ShouldBeFalse();
        Hold(Models(0)).ShouldBeTrue();
    }

    [Fact]
    public void APriorityRepoAndTheWatchtowerStartAnyway()
    {
        Usage(week: 90);
        var models = Models(10, "tianwen");

        Hold(models, "tianwen").ShouldBeFalse();
        Hold(models, "other").ShouldBeTrue();
        // The watchtower's start passes no margin at all.
        _gate.Hold("watchtower", 90, 100, "kind", Verbs.WatchtowerTarget, "The watchtower waits to start").ShouldBeFalse();
    }

    [Fact]
    public void TheFiveHourRulesStayAsTheyAreAndStaleFiguresHoldNothing()
    {
        Usage(week: 10, fiveHour: 95);
        _gate.Hold("k", 90, 90, "kind", "flight/TIA9", "x", Models(10).WeekPaceFor("tianwen")).ShouldBeTrue();
        _state.Cards().ShouldHaveSingleItem().Title.ShouldContain("the 5-hour window is 95 % spent");

        Usage(week: 90);
        _time.Advance(StartGate.FreshFor + TimeSpan.FromMinutes(1));
        _gate.CheckPace(10).ShouldBeNull();
    }

    [Fact]
    public void WeekPaceIsASettingThatTakesAMarginOrOnOrOffAndOffByDefault()
    {
        new RoleModels(null).WeekPaceFor("tianwen").ShouldBeNull();
        NodeSettings.With(new NodeSettings(), "week-pace", ["on"]).Settings!.WeekPace.ShouldBe(10);
        NodeSettings.With(new NodeSettings(), "week-pace", ["5"]).Settings!.WeekPace.ShouldBe(5);
        NodeSettings.With(new NodeSettings { WeekPace = 5 }, "week-pace", ["off"]).Settings!.WeekPace.ShouldBeNull();
        NodeSettings.With(new NodeSettings(), "week-pace", ["x"]).Error.ShouldNotBeNull();
        NodeSettings.Keys.ShouldContain("week-pace");
        NodeSettings.Keys.ShouldContain("spend-alert");
    }
}

/// <summary>The week-pace guard at the commander's own call sites: the sweep's start of a leg, for a repo and for a priority repo.</summary>
public sealed class WeekPaceLegTests : LegHoldTestsBase
{
    // At Start four of seven days have elapsed (57 %), so 70 % spent is 13 points ahead.
    private void Usage(double week) =>
        _state.PutUsage(new UsageRow
        {
            ReadAt = _time.GetUtcNow(),
            FiveHour = new UsageWindow { UsedPercent = 5, ResetsAt = Start + TimeSpan.FromHours(2) },
            SevenDay = new UsageWindow { UsedPercent = week, ResetsAt = Start + TimeSpan.FromDays(3) },
        });

    private async Task ParserDone()
    {
        await FlyingParser();
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parsed")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AheadOfPaceTheSweepKeepsTheNextLegPlannedWithACardAndAPriorityRepoStarts()
    {
        await ParserDone();
        Usage(week: 70);
        var started = _workers.Started.Count;
        _legs.Models = new RoleModels(new NodeSettings { WeekPace = 10 });

        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);

        L("render").State.ShouldBe(LegState.Planned);
        _workers.Started.Count.ShouldBe(started);
        var card = _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind).ShouldHaveSingleItem();
        card.Title.ShouldContain("waits to start render: the 7-day window is 70 % spent but only 57 % elapsed (the pace margin is 10 points)");

        // Unset, nothing waits; and a priority repo is exempt.
        _legs.Models = new RoleModels(new NodeSettings { WeekPace = 10, PriorityRepos = ["tianwen"] });
        await _legs.AdvanceAllAsync(Ct);
        // The leg's start is a session (a validator before its worker), not necessarily Flying yet.
        _workers.Started.Count.ShouldBeGreaterThan(started);
    }

    [Fact]
    public async Task WithWeekPaceUnsetTheSweepStartsTheNextLegHoweverFastTheWeekIsSpent()
    {
        await ParserDone();
        Usage(week: 95);
        var started = _workers.Started.Count;
        _legs.Models = new RoleModels(new NodeSettings());

        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.Count.ShouldBeGreaterThan(started);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NextStepKind);
    }
}
