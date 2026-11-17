using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The idle-time maintenance sweep (PIL1102): when it starts, when it does not, how it stops, and the line it reports. All in memory, on a clock the test moves and fake steps.</summary>
public sealed class IdleMaintenanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);
    private const long Gib = 1L << 30;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TimerTime _time = new(Start);
    private readonly NodeState _state;

    public IdleMaintenanceTests() => _state = new NodeState(new InMemoryJournal(), _time);

    /// <summary>A step that frees a fixed amount, or waits on its token until cancelled, and notes what it was asked.</summary>
    private sealed class FakeStep(string name, long freed, bool hang = false) : IMaintenanceStep
    {
        public string Name => name;

        public int Measured { get; private set; }

        public int Ran { get; private set; }

        /// <summary>Set once the step's token was cancelled: the fake of its process tree ending.</summary>
        public bool TreeEnded { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken)
        {
            Measured++;
            return Task.FromResult(new MaintenanceMeasure(freed, 3));
        }

        public async Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken)
        {
            Ran++;
            Started.TrySetResult();
            if (hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    TreeEnded = true;
                    throw;
                }
            }
            return new MaintenanceResult(freed);
        }
    }

    private IdleMaintenance Sweep(TimeSpan? every, params IMaintenanceStep[] steps) => new(_state, _time, steps)
    {
        IdleFor = () => Idle,
        Every = () => every,
        DrivePath = () => "C:/repos",
        FreeSpace = _ => (7 * Gib, 100 * Gib, "C:"),
    };

    private void Fly() => _state.PutLeg(new LegRow { Flight = "PIL1", Name = "l1", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });

    private void Land() => _state.PutLeg(new LegRow { Flight = "PIL1", Name = "l1", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done, FinishedAt = Start });

    private void Job(JobState state) => _state.PutJob(new JobRow { Id = "j1", Command = "dotnet test", Cwd = "/r", State = state, LogPath = "x.log", Timeout = TimeSpan.FromMinutes(5), StartedAt = Start });

    private void Review(PrReviewState state) => _state.PutPrReview(new PrReviewRow
    {
        Repo = "x/pilot", Number = 3, Url = "https://example.invalid/pr/3", Base = "main", Head = "abc", ClonePath = "/r", Worktree = "/w",
        Reviewer = "r", State = state, StartedAt = Start,
    });

    [Fact]
    public async Task ItDoesNotStartWhileALegFliesAJobIsQueuedOrAReviewRoundIsLive()
    {
        var step = new FakeStep("packages", Gib);
        var sweep = Sweep(Every, step);

        foreach (var busy in new Action[] { Fly, () => Job(JobState.Queued), () => Job(JobState.Running), () => Review(PrReviewState.Reviewing) })
        {
            busy();
            (await sweep.TickAsync(Ct)).ShouldBeNull();
            _time.Advance(Idle + TimeSpan.FromMinutes(1));
            (await sweep.TickAsync(Ct)).ShouldBeNull();
            step.Ran.ShouldBe(0);
            Land();
            Job(JobState.Succeeded);
            Review(PrReviewState.Closed);
        }
    }

    [Fact]
    public async Task ItStartsOnceTheNodeHasBeenIdleForTheIdleTimeAndNotBefore()
    {
        var step = new FakeStep("packages", Gib);
        var sweep = Sweep(Every, step);

        (await sweep.TickAsync(Ct)).ShouldBeNull();
        _time.Advance(Idle - TimeSpan.FromSeconds(1));
        (await sweep.TickAsync(Ct)).ShouldBeNull();
        _time.Advance(TimeSpan.FromSeconds(1));
        var run = await sweep.TickAsync(Ct);

        run.ShouldNotBeNull();
        step.Ran.ShouldBe(1);
        sweep.Last.ShouldBe(run);
    }

    [Fact]
    public async Task BusyTimeRestartsTheIdleClock()
    {
        var step = new FakeStep("packages", Gib);
        var sweep = Sweep(Every, step);
        await sweep.TickAsync(Ct);
        _time.Advance(Idle - TimeSpan.FromMinutes(1));
        Fly();
        await sweep.TickAsync(Ct);
        Land();
        _time.Advance(TimeSpan.FromMinutes(5));

        (await sweep.TickAsync(Ct)).ShouldBeNull();
        _time.Advance(Idle);
        (await sweep.TickAsync(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ItDoesNotRunASecondTimeInsideTheMaintenanceEveryPeriodAndRunsAgainAfterIt()
    {
        var step = new FakeStep("packages", Gib);
        var sweep = Sweep(Every, step);
        await sweep.TickAsync(Ct);
        _time.Advance(Idle);
        (await sweep.TickAsync(Ct)).ShouldNotBeNull();

        _time.Advance(Every - TimeSpan.FromMinutes(1));
        (await sweep.TickAsync(Ct)).ShouldBeNull();
        step.Ran.ShouldBe(1);
        _time.Advance(TimeSpan.FromMinutes(2));
        (await sweep.TickAsync(Ct)).ShouldNotBeNull();
        step.Ran.ShouldBe(2);
    }

    [Fact]
    public async Task OffNeverRuns()
    {
        var step = new FakeStep("packages", Gib);
        var sweep = Sweep(null, step);
        await sweep.TickAsync(Ct);

        _time.Advance(TimeSpan.FromDays(30));

        (await sweep.TickAsync(Ct)).ShouldBeNull();
        step.Ran.ShouldBe(0);
    }

    [Fact]
    public async Task AJobArrivingMidStepCancelsItWithinASecondAndItsProcessTreeIsReportedEnded()
    {
        var hanging = new FakeStep("packages", Gib, hang: true);
        var later = new FakeStep("git", Gib);
        var sweep = Sweep(Every, hanging, later);
        await sweep.TickAsync(Ct);
        _time.Advance(Idle);

        var running = sweep.TickAsync(Ct);
        await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        hanging.TreeEnded.ShouldBeFalse();
        Job(JobState.Queued);
        _time.Advance(TimeSpan.FromSeconds(1));
        var run = await running.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        hanging.TreeEnded.ShouldBeTrue();
        later.Ran.ShouldBe(0);
        run.ShouldNotBeNull();
        run.Cancelled.ShouldBeTrue();
        run.Steps.Single().Cancelled.ShouldBeTrue();
        run.Line().ShouldStartWith("maintenance (stopped: job j1 started): freed");
        run.Steps.Single().Untouched.ShouldBe("stopped: job j1 started");
        _state.Cards().Single().Title.ShouldStartWith("maintenance (stopped: job j1 started)");
    }

    [Fact]
    public async Task ARealRunCancelledByALegTakingOffNamesTheLeg()
    {
        var hanging = new FakeStep("packages", Gib, hang: true);
        var sweep = Sweep(Every, hanging);
        var running = sweep.RunNowAsync(dryRun: false, Ct);
        await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Fly();
        _time.Advance(TimeSpan.FromSeconds(1));
        var (run, _) = await running.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        run.ShouldNotBeNull().Line().ShouldStartWith("maintenance (stopped: a leg took off (PIL1/l1)): freed");
    }

    [Fact]
    public async Task ADryRunStartedWhileALegFliesRunsEveryStepEvenWhenAJobStartsMidRun()
    {
        Fly();
        var gate = new GatedStep(new FakeStep("packages", 2 * Gib));
        var two = new FakeStep("git", Gib);
        var sweep = Sweep(Every, gate, two);

        var running = sweep.RunNowAsync(dryRun: true, Ct);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Job(JobState.Running);
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
        }
        running.IsCompleted.ShouldBeFalse();
        gate.Release.SetResult();
        var (run, refused) = await running.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        refused.ShouldBeNull();
        run.ShouldNotBeNull().Cancelled.ShouldBeFalse();
        run.Steps.Select(s => s.Name).ShouldBe(["packages", "git"]);
        run.Steps.ShouldAllBe(s => !s.Cancelled);
        two.Measured.ShouldBe(1);
        run.Line().ShouldStartWith("maintenance (dry run): would free 3.0 GB");
    }

    [Fact]
    public async Task ARealRunInTheSameStateIsRefused()
    {
        Fly();
        Job(JobState.Running);
        var step = new FakeStep("packages", Gib);

        var (run, refused) = await Sweep(Every, step).RunNowAsync(dryRun: false, Ct);

        run.ShouldBeNull();
        refused.ShouldNotBeNull().ShouldStartWith("not now:");
        step.Ran.ShouldBe(0);
    }

    /// <summary>Measures like its inner step, but only once the test releases it, and says when it was entered: a step that is still running while time moves on.</summary>
    private sealed class GatedStep(IMaintenanceStep inner) : IMaintenanceStep
    {
        public string Name => inner.Name;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.MeasureAsync(cancellationToken);
        }

        public Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken) => inner.RunAsync(cancellationToken);
    }

    [Fact]
    public async Task ADryRunCallsOnlyTheMeasurePathAndRaisesNoCard()
    {
        var one = new FakeStep("packages", 2 * Gib);
        var two = new FakeStep("git", Gib);
        var sweep = Sweep(Every, one, two);

        var (run, refused) = await sweep.RunNowAsync(dryRun: true, Ct);

        refused.ShouldBeNull();
        (one.Measured, one.Ran, two.Measured, two.Ran).ShouldBe((1, 0, 1, 0));
        run.ShouldNotBeNull().Bytes.ShouldBe(3 * Gib);
        run.Line().ShouldBe("maintenance (dry run): would free 3.0 GB (packages 2.0 GB, git 1.0 GB); C: 7.0 GB free");
        _state.Cards().ShouldBeEmpty();
        sweep.Last.ShouldBeNull();
    }

    [Fact]
    public async Task RunningItByHandIgnoresTheIdleWaitButIsRefusedWhileALegFliesOrAJobRuns()
    {
        var step = new FakeStep("packages", Gib);
        var sweep = Sweep(Every, step);

        Fly();
        var (none, flying) = await sweep.RunNowAsync(dryRun: false, Ct);
        Land();
        Job(JobState.Running);
        var (_, running) = await sweep.RunNowAsync(dryRun: false, Ct);
        Job(JobState.Succeeded);
        var (run, refused) = await sweep.RunNowAsync(dryRun: false, Ct);

        none.ShouldBeNull();
        flying.ShouldBe("not now: a leg is flying (PIL1/l1)");
        running.ShouldBe("not now: job j1 is running");
        refused.ShouldBeNull();
        run.ShouldNotBeNull();
    }

    [Fact]
    public async Task TheHandledLineIsPinnedExactly()
    {
        var steps = new IMaintenanceStep[]
        {
            new FakeStep("packages", (long)(8.1 * Gib)), new FakeStep("git", (long)(0.3 * Gib)),
            new FakeStep("lfs", (long)(0.2 * Gib)), new FakeStep("jobs", (long)(0.6 * Gib)),
        };
        var free = new Queue<long>([(long)(7.0 * Gib), (long)(16.2 * Gib)]);
        var sweep = new IdleMaintenance(_state, _time, steps)
        {
            IdleFor = () => Idle, Every = () => Every, DrivePath = () => "C:/repos",
            FreeSpace = _ => (free.Dequeue(), 100 * Gib, "C:"),
        };

        var (run, _) = await sweep.RunNowAsync(dryRun: false, Ct);

        const string line = "maintenance: freed 9.2 GB (packages 8.1 GB, git 0.3 GB, lfs 0.2 GB, jobs 0.6 GB); C: 7.0 -> 16.2 GB free";
        run.ShouldNotBeNull().Line().ShouldBe(line);
        var card = _state.Cards().Single();
        card.Kind.ShouldBe(IdleMaintenance.Kind);
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldBe(line);
    }

    [Fact]
    public async Task AStepThatCouldNotTouchSomethingSaysSoForTheDiskAlert()
    {
        var sweep = Sweep(Every, new UntouchedStep());

        var (run, _) = await sweep.RunNowAsync(dryRun: false, Ct);

        run.ShouldNotBeNull().Untouched.ShouldBe("git: 2 repos with a plane running");
        sweep.Last.ShouldBe(run);
    }

    private sealed class UntouchedStep : IMaintenanceStep
    {
        public string Name => "git";

        public Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken) => Task.FromResult(new MaintenanceMeasure(0, 0));

        public Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken) => Task.FromResult(new MaintenanceResult(0, "2 repos with a plane running"));
    }

    [Fact]
    public async Task TheNodeVerbAnswersWithTheReportAndAdryRunRemovesNothing()
    {
        var step = new FakeStep("packages", Gib);
        var sweep = Sweep(Every, step);
        var options = new NodeOptions(Path.Combine(Path.GetTempPath(), "maintenance-node.sock"), Path.Combine(Path.GetTempPath(), "maintenance-data"), MachinePrefix: "DU1");
        var tower = new Watchtower(_state, new FakeWorkers(), (command, _) => Task.FromResult(_state.Complete(command, null)), options, TimeProvider.System);
        var commands = new NodeCommands(new ServiceCollection().AddSingleton<TimeProvider>(_time).AddSingleton(sweep).AddSingleton(tower).AddSingleton(new RoleModels(null)).BuildServiceProvider(), _state);

        var dry = await commands.RunAsync(new CommandRequest { Verb = Verbs.Maintenance, Target = Verbs.NodeTarget, Args = ["--dry-run"], Who = "person" }, Ct);
        var real = await commands.RunAsync(new CommandRequest { Verb = Verbs.Maintenance, Target = Verbs.NodeTarget, Args = [], Who = "person" }, Ct);

        dry.Ok.ShouldBeTrue(dry.Error);
        dry.Way.ShouldBe("maintenance (dry run): would free 1.0 GB (packages 1.0 GB); C: 7.0 GB free");
        real.Ok.ShouldBeTrue(real.Error);
        real.Way.ShouldBe("maintenance: freed 1.0 GB (packages 1.0 GB); C: 7.0 -> 7.0 GB free");
        (step.Measured, step.Ran).ShouldBe((1, 1));
    }

    [Fact]
    public void TheMaintenanceVerbIsThePersonsAlone() => Standing.YourOnlyVerbs.ShouldContain(Verbs.Maintenance);
}
