using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The first sweep on a node waits for a look (PIL1114, #1114): with maintenance-every never set by a person the idle sweep removes nothing
/// and raises one For-you card holding a dry run's totals; answering it saves the setting through the card's own verb. All in memory, on fake
/// steps and a clock the test moves.
/// </summary>
public sealed class MaintenanceFirstRunTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);
    private const long Gib = 1L << 30;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TimerTime _time = new(Start);
    private readonly NodeState _state;
    private RoleModels _roles = new(new NodeSettings());

    public MaintenanceFirstRunTests() => _state = new NodeState(new InMemoryJournal(), _time);

    /// <summary>A step with seven items of 1 GB down to 7 GB, so the largest five are known, that notes what it was asked.</summary>
    private sealed class FakeStep(string name) : IMaintenanceStep
    {
        public string Name => name;

        public int Measured { get; private set; }

        public int Ran { get; private set; }

        public Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken)
        {
            Measured++;
            return Task.FromResult(new MaintenanceMeasure([.. Enumerable.Range(1, 7).Select(i => new MaintenanceItem($"{name}-item{i}", i * Gib, $"older than {i} days"))]));
        }

        public Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken)
        {
            Ran++;
            return Task.FromResult(new MaintenanceResult(Gib));
        }
    }

    /// <summary>A service as the node wires it: Every and EverySet read the settings the test holds, so an answer is seen at once.</summary>
    private IdleMaintenance Sweep(params IMaintenanceStep[] steps) => new(_state, _time, steps)
    {
        IdleFor = () => Idle,
        Every = () => _roles.MaintenanceEvery,
        EverySet = () => _roles.MaintenanceEverySet,
    };

    private void Set(string value) => _roles = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.MaintenanceEveryKey, [value]).Settings!);

    /// <summary>Presses a choice: runs the card's own verb on the settings, as the node does, and closes the card.</summary>
    private void Press(CardRow card, string label)
    {
        var choice = card.Choices.Single(c => c.Label == label);
        choice.Verb.ShouldBe(Verbs.Set);
        choice.Target.ShouldBe(Verbs.NodeTarget);
        Set(choice.Args[1]);
        _state.CloseCard(card.Id, label, "gui");
    }

    private CardRow[] Open() => [.. _state.Cards(openOnly: true).Where(c => c.Kind == IdleMaintenance.FirstRunKind)];

    private async Task<MaintenanceRun?> IdleTickAsync(IdleMaintenance sweep)
    {
        await sweep.TickAsync(Ct);
        _time.Advance(Idle);
        return await sweep.TickAsync(Ct);
    }

    [Fact]
    public async Task AnUnsetSettingRaisesOneCardWithTheTotalsAndTheTopFivePerStepAndRunsNoRealStep()
    {
        var packages = new FakeStep("packages");
        var git = new FakeStep("git");
        var sweep = Sweep(packages, git);

        (await IdleTickAsync(sweep)).ShouldBeNull();

        packages.Ran.ShouldBe(0);
        git.Ran.ShouldBe(0);
        var card = Open().ShouldHaveSingleItem();
        card.Kind.ShouldBe(IdleMaintenance.FirstRunKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe(Verbs.NodeTarget);
        card.Title.ShouldContain("Run the first idle disk sweep");
        card.Title.ShouldContain("56.0 GB");
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("packages: 28.0 GB in 7 item(s)");
        detail.ShouldContain("git: 28.0 GB in 7 item(s)");
        foreach (var step in new[] { "packages", "git" })
        {
            foreach (var i in new[] { 7, 6, 5, 4, 3 })
            {
                detail.ShouldContain($"{step}-item{i}: {i}.0 GB");
            }
            detail.ShouldNotContain($"{step}-item2");
            detail.ShouldNotContain($"{step}-item1:");
        }
        detail.ShouldContain("and 2 more");
        card.Choices.Select(c => (c.Label, c.Verb, c.Target)).ShouldBe([("Run on schedule", Verbs.Set, Verbs.NodeTarget), ("Keep off", Verbs.Set, Verbs.NodeTarget)]);
        card.Choices[0].Args.ShouldBe([NodeSettings.MaintenanceEveryKey, "1d"]);
        card.Choices[1].Args.ShouldBe([NodeSettings.MaintenanceEveryKey, "off"]);
    }

    /// <summary>A step whose items are 340 KB, 12 MB and 900 bytes, so sizes under a gigabyte show their own unit and not 0.0 GB.</summary>
    private sealed class SmallStep : IMaintenanceStep
    {
        public string Name => "git";

        public Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MaintenanceMeasure([new MaintenanceItem("big", 12L << 20, "r1"), new MaintenanceItem("mid", 340L << 10, "r2"), new MaintenanceItem("tiny", 900, "r3")]));

        public Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken) => Task.FromResult(new MaintenanceResult(0));
    }

    [Fact]
    public async Task ItemSizesOnTheCardScaleToTheirOwnUnitNotGigabytes()
    {
        var sweep = Sweep(new SmallStep());

        await IdleTickAsync(sweep);

        var detail = Open().ShouldHaveSingleItem().Detail.ShouldNotBeNull();
        detail.ShouldContain("big: 12.0 MB (r1)");
        detail.ShouldContain("mid: 340.0 KB (r2)");
        detail.ShouldContain("tiny: 900 B (r3)");
        detail.ShouldNotContain("big: 0.0 GB");
    }

    [Fact]
    public void TheCardAndTheCliListingShareOneSizeFormatter()
    {
        foreach (var bytes in new long[] { 0, 900, 1024, 340L << 10, 12L << 20, 3L << 30, 5L << 40 })
        {
            IdleMaintenance.Scaled(bytes).ShouldBe(Pilot.Commands.MaintenanceListing.Size(bytes));
        }
        IdleMaintenance.Scaled(1536).ShouldBe("1.5 KB");
    }

    [Fact]
    public async Task LaterTicksAndARestartedServiceWhileTheCardIsOpenRaiseNoSecond()
    {
        var step = new FakeStep("packages");
        var sweep = Sweep(step);
        await IdleTickAsync(sweep);
        var measured = step.Measured;

        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            await sweep.TickAsync(Ct);
        }
        var restarted = Sweep(step);
        await IdleTickAsync(restarted);

        Open().ShouldHaveSingleItem();
        step.Measured.ShouldBe(measured);
        step.Ran.ShouldBe(0);
    }

    [Fact]
    public async Task AnswerRunSavesTheDefaultAndTheNextIdleWindowRunsTheRealSweep()
    {
        var step = new FakeStep("packages");
        var sweep = Sweep(step);
        await IdleTickAsync(sweep);

        Press(Open().Single(), IdleMaintenance.RunLabel);

        _roles.MaintenanceEverySet.ShouldBeTrue();
        _roles.MaintenanceEvery.ShouldBe(TimeSpan.FromHours(24));
        Open().ShouldBeEmpty();
        (await sweep.TickAsync(Ct)).ShouldNotBeNull();
        step.Ran.ShouldBe(1);
    }

    [Fact]
    public async Task AnswerKeepOffSavesOffAndNothingRunsAndNoSecondCardComes()
    {
        var step = new FakeStep("packages");
        var sweep = Sweep(step);
        await IdleTickAsync(sweep);

        Press(Open().Single(), IdleMaintenance.KeepOffLabel);

        _roles.MaintenanceEverySet.ShouldBeTrue();
        _roles.MaintenanceEvery.ShouldBeNull();
        _time.Advance(TimeSpan.FromDays(3));
        (await sweep.TickAsync(Ct)).ShouldBeNull();
        step.Ran.ShouldBe(0);
        _state.Cards().Count(c => c.Kind == IdleMaintenance.FirstRunKind).ShouldBe(1);
    }

    [Theory]
    [InlineData("1d")]
    [InlineData("12h")]
    [InlineData("off")]
    public async Task ASettingSetByHandRaisesNoCardAndTheScheduleAppliesAsToday(string value)
    {
        Set(value);
        var step = new FakeStep("packages");
        var sweep = Sweep(step);

        var run = await IdleTickAsync(sweep);

        Open().ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == IdleMaintenance.FirstRunKind).ShouldBe(0);
        step.Measured.ShouldBe(0);
        if (value == "off")
        {
            run.ShouldBeNull();
            step.Ran.ShouldBe(0);
        }
        else
        {
            run.ShouldNotBeNull();
            step.Ran.ShouldBe(1);
        }
    }

    [Fact]
    public async Task NoTickOfTheSweepAnswersTheCardForThePersonEvenWithTheSettingForTheAutopilotOn()
    {
        _roles = new RoleModels(new NodeSettings());
        var step = new FakeStep("packages");
        var sweep = Sweep(step);

        await IdleTickAsync(sweep);
        _time.Advance(TimeSpan.FromDays(2));
        await sweep.TickAsync(Ct);

        Open().ShouldHaveSingleItem().State.ShouldBe(CardState.Open);
        _roles.MaintenanceEverySet.ShouldBeFalse();
        step.Ran.ShouldBe(0);
    }

    [Fact]
    public async Task BusyTimeRaisesNoCardUntilTheNodeHasBeenIdleAgain()
    {
        var sweep = Sweep(new FakeStep("packages"));
        _state.PutLeg(new LegRow { Flight = "PIL1", Name = "l1", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });

        await sweep.TickAsync(Ct);
        _time.Advance(Idle * 2);
        await sweep.TickAsync(Ct);

        Open().ShouldBeEmpty();
    }
}
