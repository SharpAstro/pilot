using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An <see cref="ICloudBench"/> a test scripts: what it answers per call (the call's number counts from 1), every request it was given, and the
/// lines it writes to the request's log as the real runner writes the VM's output.
/// </summary>
internal sealed class ScriptedCloudBench : ICloudBench
{
    private readonly Lock _gate = new();
    private readonly List<CloudBenchRequest> _requests = [];

    public Func<CloudBenchRequest, CancellationToken, int, Task<CloudBenchResult>> Behavior { get; set; } = (_, _, _) => Task.FromResult(Result());

    public IReadOnlyList<CloudBenchRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task<CloudBenchResult> RunAsync(CloudBenchRequest request, CancellationToken cancellationToken)
    {
        int call;
        lock (_gate)
        {
            _requests.Add(request);
            call = _requests.Count;
        }
        return Behavior(request, cancellationToken, call);
    }

    public static CloudBenchResult Result(CloudBenchOutcome outcome = CloudBenchOutcome.Succeeded, double minutes = 42.5, double? cost = 0.0496, int evictions = 0, int? exit = 0,
        string? error = null, CloudBenchStage stage = CloudBenchStage.Done, IReadOnlyList<string>? leaked = null) => new()
    {
        Outcome = outcome,
        Stage = stage,
        ExitCode = exit,
        Error = error,
        Vm = new BenchVm
        {
            Size = "Standard_F8s_v2", Image = CloudBenchJobTests.Image, Region = "westeurope", PricePerHour = 0.07, Minutes = minutes, Cost = cost, Evictions = evictions,
        },
        ResultsFolder = "results",
        Leaked = leaked ?? [],
    };
}

/// <summary>
/// A bench job with the cloud settings saved runs on a spot VM through the cloud runner (a fake here), holding no slot and nothing else, and one
/// without them, or with <c>--local</c>, waits and holds as it always did (#823). The live run on a real VM is the controller's.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed partial class CloudBenchJobTests : JobTestsBase, IAsyncDisposable
{
    internal const string Image = "Canonical:ubuntu-24_04-lts:server:latest";

    private readonly ScriptedCloudBench _cloud = new();
    private readonly List<JobRunner> _started = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var jobs in _started)
        {
            await jobs.StopAsync(CancellationToken.None);
        }
        GC.SuppressFinalize(this);
    }

    private static NodeSettings Settings(double cap = 20) => new()
    {
        BenchSubscription = "00000000-0000-0000-0000-000000000001",
        BenchResourceGroup = "rg-bench",
        BenchRegion = "westeurope",
        BenchVmSize = "Standard_F8s_v2",
        BenchImage = Image,
        BenchSpotPrice = 0.3,
        BenchMonthlyCap = cap,
    };

    private static CloudBenchPlan Plan(string repo) => new()
    {
        RepoPath = repo,
        Refs = [new CloudBenchRef("ref", "main", new string('a', 40))],
        Command = ["dotnet", "run", "-c", "Release", "--project", "bench/B"],
    };

    // The runner is not started: the test is the dispatcher, so nothing else touches its queue, and the clock and the CPU are the test's.
    private (JobRunner Jobs, Clock Clock, Func<string, Task> Gone) Runner(NodeSettings? settings, Func<double?>? machineCpu = null, Func<double?>? outsideCpu = null, IGit? git = null, ICloudBench? cloud = null)
    {
        var clock = new Clock();
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), clock)
        {
            FreeMemory = () => null,
            MachineCpu = machineCpu,
            MachineOutsideCpu = outsideCpu,
            CloudBench = cloud ?? _cloud,
            BenchSettings = () => settings ?? new NodeSettings(),
            Git = git ?? new GitCli(),
        };
        return (jobs, clock, async id =>
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        });
    }

    // A runner whose dispatcher loop runs, as the node's does: for the tests that stop it, or hand a bench through BenchRun.
    private async Task<JobRunner> StartedRunner(NodeSettings? settings, ICloudBench? cloud = null, IGit? git = null)
    {
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            FreeMemory = () => null,
            MachineOutsideCpu = () => 0,
            MachineCpu = () => 0,
            CloudBench = cloud ?? _cloud,
            BenchSettings = () => settings ?? new NodeSettings(),
            Git = git ?? new GitCli(),
        };
        _started.Add(jobs);
        await jobs.StartAsync(CancellationToken.None);
        await jobs.Recovered.WaitAsync(WaitCeiling, Ct);
        return jobs;
    }

    private string HandCloudBench(JobRunner jobs, bool local = false, CloudBenchPlan? plan = null, TimeSpan? timeout = null)
    {
        var (id, _) = jobs.QueueBench(Echo("benched"), _root, timeout ?? TimeSpan.FromMinutes(30), owner: null, local, plan ?? Plan(_root));
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    private string LogOf(string id) => ReadLog(Row(id).LogPath);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ABenchWithCloudSettingsRunsOnTheVmTakingNoSlotAndOtherJobsStartAndWaitAsIfItWereNotThere()
    {
        var proceed = Gate();
        _cloud.Behavior = async (request, token, _) =>
        {
            request.Log("=== bench ref: main (aaaaaaaaa) ===");
            request.Log("| Mean | 1.00 ms |");
            await proceed.Task.WaitAsync(token);
            return ScriptedCloudBench.Result();
        };
        // A machine that is never quiet: a bench for this machine would wait for good.
        var (jobs, _, gone) = Runner(Settings(), () => 95, () => 90);
        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);

        var bench = HandCloudBench(jobs);
        var running = await Until(bench, j => j.Step == "=== bench ref: main (aaaaaaaaa) ===");

        // Running at once beside a job that holds the node's one slot: no slot, no wait for the CPU, and no process of the node's.
        running.ShouldSatisfyAllConditions(
            r => r.Kind.ShouldBe(JobKind.Bench),
            r => r.State.ShouldBe(JobState.Running),
            r => r.Pid.ShouldBeNull(),
            r => r.BenchVm.ShouldNotBeNull().ShouldSatisfyAllConditions(vm => vm.Size.ShouldBe("Standard_F8s_v2"), vm => vm.Image.ShouldBe(Image), vm => vm.Region.ShouldBe("westeurope")));
        jobs.IsRunning(bench).ShouldBeFalse();

        // Another job queues for the slot the first holds, and the reason is that job, not a benchmark that holds the node.
        var later = Hand(jobs, Echo("later"));
        Row(later).State.ShouldBe(JobState.Queued);
        Row(later).Step.ShouldNotBeNull().ShouldContain("another job is running");
        Row(later).Step.ShouldNotBeNull().ShouldNotContain("benchmark");
        // With room it starts beside the VM bench.
        jobs.MaxRunning = 4;
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(later, j => j.State is JobState.Running or JobState.Succeeded);
        Row(bench).State.ShouldBe(JobState.Running);

        // A VM cannot be paused, and the answer says so.
        var paused = jobs.Pause(new CommandRequest { Verb = Verbs.PauseJob, Target = Verbs.JobTarget(bench), Who = "controller" });
        paused.Error.ShouldNotBeNull().ShouldContain("runs on a VM");

        proceed.SetResult();
        var ended = await Until(bench, j => j.State == JobState.Succeeded);
        ended.ShouldSatisfyAllConditions(
            e => e.ExitCode.ShouldBe(0),
            e => e.EndedAt.ShouldNotBeNull(),
            e => e.BenchVm.ShouldNotBeNull().ShouldSatisfyAllConditions(
                vm => vm.Minutes.ShouldBe(42.5),
                vm => vm.Cost.ShouldBe(0.0496),
                vm => vm.PricePerHour.ShouldBe(0.07),
                vm => vm.Evictions.ShouldBe(0)));
        // The VM's own output is in the job's log, and the runner was asked for the refs and the command the plan gave.
        LogOf(bench).ShouldContain("=== bench ref: main (aaaaaaaaa) ===");
        LogOf(bench).ShouldContain("| Mean | 1.00 ms |");
        var request = _cloud.Requests.ShouldHaveSingleItem();
        (request.JobId, request.RepoPath, request.Refs.Single().Label, request.Command[0]).ShouldBe((bench, _root, "ref", "dotnet"));
        request.Cloud.VmSize.ShouldBe("Standard_F8s_v2");
        request.TmpFolder.ShouldBe(JobRunner.CloudTmpOf(Path.Combine(_root, "data"), bench));
        await gone(first);
    }

    [Fact]
    public async Task AFirstEvictionRunsTheBenchOnceMoreSayingSoInTheStepAndTheLogAndTheVmRecordAddsUpBothAttempts()
    {
        var proceed = Gate();
        _cloud.Behavior = async (request, token, call) =>
        {
            if (call == 1)
            {
                request.Log("=== bench ref: main (aaaaaaaaa) ===");
                return ScriptedCloudBench.Result(CloudBenchOutcome.Evicted, minutes: 12, cost: 0.014, evictions: 1, exit: null, error: "Azure took the VM back", stage: CloudBenchStage.Run);
            }
            await proceed.Task.WaitAsync(token);
            return ScriptedCloudBench.Result(minutes: 30, cost: 0.035);
        };
        var (jobs, _, _) = Runner(Settings());

        var bench = HandCloudBench(jobs);
        var requeued = await Until(bench, j => j.Step is { } step && step.Contains("evicted by Azure", StringComparison.Ordinal));

        requeued.State.ShouldBe(JobState.Running);
        requeued.Step.ShouldNotBeNull().ShouldContain("re-queued once");
        LogOf(bench).ShouldContain(CloudBenches.EvictedPrefix);

        proceed.SetResult();
        var ended = await Until(bench, j => j.State == JobState.Succeeded);
        _cloud.Requests.Count.ShouldBe(2);
        ended.BenchVm.ShouldNotBeNull().ShouldSatisfyAllConditions(
            vm => vm.Evictions.ShouldBe(1),
            vm => vm.Minutes.ShouldBe(42),
            vm => vm.Cost.ShouldNotBeNull().ShouldBe(0.049, 1e-9));
    }

    [Fact]
    public async Task ASecondEvictionEndsTheBenchFailedSayingItWasEvictedTwiceAndItIsNotRunAThirdTime()
    {
        _cloud.Behavior = (_, _, _) => Task.FromResult(ScriptedCloudBench.Result(CloudBenchOutcome.Evicted, minutes: 10, cost: 0.01, evictions: 1, exit: null, error: "Azure took the VM back", stage: CloudBenchStage.Run));
        var (jobs, _, _) = Runner(Settings());

        var bench = HandCloudBench(jobs);
        var ended = await Until(bench, j => j.EndedAt is not null);

        _cloud.Requests.Count.ShouldBe(2);
        ended.State.ShouldBe(JobState.Failed);
        ended.Error.ShouldNotBeNull().ShouldContain("evicted by Azure twice");
        ended.Error.ShouldContain("re-queued once");
        ended.BenchVm.ShouldNotBeNull().ShouldSatisfyAllConditions(vm => vm.Evictions.ShouldBe(2), vm => vm.Minutes.ShouldBe(20), vm => vm.Cost.ShouldNotBeNull().ShouldBe(0.02, 1e-9));
    }

    [Fact]
    public async Task ACancelStopsTheRunnerWhichDeletesTheVmBeforeTheRowEndsCancelled()
    {
        var cleaned = Gate();
        _cloud.Behavior = async (request, token, _) =>
        {
            request.Log("=== bench ref: main (aaaaaaaaa) ===");
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // The runner's own cleanup: it deletes the VM when its token is cancelled, then answers.
            }
            cleaned.SetResult();
            return ScriptedCloudBench.Result(CloudBenchOutcome.Cancelled, minutes: 3, cost: 0.0035, exit: null, stage: CloudBenchStage.Run);
        };
        var (jobs, _, gone) = Runner(Settings());
        var bench = HandCloudBench(jobs);
        await Until(bench, j => j.Step == "=== bench ref: main (aaaaaaaaa) ===");

        await gone(bench);

        cleaned.Task.IsCompletedSuccessfully.ShouldBeTrue();
        var ended = Row(bench);
        (ended.State, ended.Error).ShouldBe((JobState.Cancelled, "cancelled by controller"));
        ended.BenchVm.ShouldNotBeNull().Cost.ShouldBe(0.0035);
    }

    [Fact]
    public async Task ABenchThatRunsPastItsTimeoutEndsFailedAfterTheRunnerHasDeletedTheVm()
    {
        _cloud.Behavior = async (_, token, _) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }
            return ScriptedCloudBench.Result(CloudBenchOutcome.Cancelled, minutes: 1, cost: 0.001, exit: null, stage: CloudBenchStage.Run);
        };
        var (jobs, _, _) = Runner(Settings());

        var bench = HandCloudBench(jobs, timeout: TimeSpan.FromMilliseconds(300));
        var ended = await Until(bench, j => j.EndedAt is not null);

        ended.State.ShouldBe(JobState.Failed);
        ended.Error.ShouldNotBeNull().ShouldStartWith("stopped after its timeout of ");
    }

    [Fact]
    public async Task AMonthWhoseCloudBenchesReachedTheCapRunsTheNextOneOnThisMachineWithTheReasonOnItsRowAndStep()
    {
        var (jobs, clock, gone) = Runner(Settings(cap: 1.5));
        // This month's: one cost 1.0 and one 0.5, together the cap. An earlier month's is not counted, whatever it cost.
        SeedCloudBench("j90", clock.GetUtcNow().AddDays(-3), 1.0);
        SeedCloudBench("j91", clock.GetUtcNow().AddDays(-1), 0.5);
        SeedCloudBench("j92", clock.GetUtcNow().AddMonths(-1), 100);
        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);

        var bench = HandCloudBench(jobs);

        _cloud.Requests.ShouldBeEmpty();
        var row = Row(bench);
        row.State.ShouldBe(JobState.Queued);
        row.BenchVm.ShouldNotBeNull().Size.ShouldBeNull();
        row.BenchVm.LocalBecause.ShouldNotBeNull().ShouldContain("$1.50 of the $1.50 cap");
        row.BenchVm.LocalBecause.ShouldContain("October 2026");
        row.BenchVm.LocalBecause.ShouldContain("runs on this machine");
        row.Step.ShouldNotBeNull().ShouldContain("another job is running");
        row.Step.ShouldContain("it runs on this machine: the cloud benches of October 2026 have cost $1.50");
        await gone(first);
    }

    [Fact]
    public async Task AMonthBelowTheCapStillSendsTheBenchToTheVm()
    {
        var (jobs, clock, _) = Runner(Settings(cap: 1.5));
        SeedCloudBench("j90", clock.GetUtcNow().AddDays(-3), 1.49);
        SeedCloudBench("j91", clock.GetUtcNow().AddMonths(-2), 500);

        var bench = HandCloudBench(jobs);

        var ended = await Until(bench, j => j.State == JobState.Succeeded);
        _cloud.Requests.ShouldHaveSingleItem();
        ended.BenchVm.ShouldNotBeNull().Size.ShouldBe("Standard_F8s_v2");
    }

    [Fact]
    public async Task ABenchStillRunningOnAVmCountsItsHoursSoFarAgainstTheCapSoAnotherOneStaysOnThisMachine()
    {
        var proceed = Gate();
        _cloud.Behavior = async (_, token, _) =>
        {
            await proceed.Task.WaitAsync(token);
            return ScriptedCloudBench.Result();
        };
        // 0.3 an hour at most, for half an hour: 0.15 spent so far against a cap of 0.1.
        var (jobs, clock, gone) = Runner(Settings(cap: 0.1));
        var first = HandCloudBench(jobs);
        await Until(first, j => j.State == JobState.Running);
        clock.Advance(TimeSpan.FromMinutes(30));
        var blocker = Hand(jobs, Sleep(30));
        await Until(blocker, j => j.State == JobState.Running);

        var second = HandCloudBench(jobs);

        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).BenchVm.ShouldNotBeNull().LocalBecause.ShouldNotBeNull().ShouldContain("cap");
        _cloud.Requests.ShouldHaveSingleItem();
        proceed.SetResult();
        await Until(first, j => j.State == JobState.Succeeded);
        await gone(blocker);
    }

    [Fact]
    public async Task ALocalBenchWaitsForTheNodeAndHoldsItsOtherJobsAsToday()
    {
        var (jobs, _, gone) = Runner(Settings());
        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);

        var bench = HandCloudBench(jobs, local: true);

        _cloud.Requests.ShouldBeEmpty();
        Row(bench).State.ShouldBe(JobState.Queued);
        Row(bench).Local.ShouldBeTrue();
        Row(bench).BenchVm.ShouldNotBeNull().LocalBecause.ShouldBe(JobRunner.AskedLocalBecause);
        Row(bench).Step.ShouldNotBeNull().ShouldContain("a benchmark runs alone: another job is running");
        Row(bench).Step.ShouldNotBeNull().ShouldNotContain("runs on this machine:");
        // It heads the queue, so a job handed over behind it is held by it.
        var later = Hand(jobs, Echo("later"));
        Row(later).Step.ShouldNotBeNull().ShouldContain($"a benchmark ({bench}) holds the node");

        await gone(first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(bench, j => j.State is JobState.Running or JobState.Succeeded);
        _cloud.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ABenchWithNoCloudSettingsWaitsAndHoldsAsTodayAndSaysWhyItIsNotInTheCloud()
    {
        var (jobs, _, gone) = Runner(new NodeSettings());
        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);

        var bench = HandCloudBench(jobs);

        _cloud.Requests.ShouldBeEmpty();
        Row(bench).State.ShouldBe(JobState.Queued);
        Row(bench).Local.ShouldBeFalse();
        Row(bench).BenchVm.ShouldNotBeNull().LocalBecause.ShouldNotBeNull().ShouldStartWith("cloud benches are off: bench-subscription and bench-resource-group are not set");
        // The step is as it was before cloud benches: the reason is on the row, not in every queued bench's step.
        Row(bench).Step.ShouldBe("next in line: a benchmark runs alone: another job is running");
        var later = Hand(jobs, Echo("later"));
        Row(later).Step.ShouldNotBeNull().ShouldContain($"a benchmark ({bench}) holds the node");

        await gone(first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(bench, j => j.State is JobState.Running or JobState.Succeeded);
    }

    [Fact]
    public async Task AFailedBenchEndsFailedWithItsExitCodeAndOneThatNeverStartedSaysWhereTheCloudStopped()
    {
        _cloud.Behavior = (_, _, call) => Task.FromResult(call == 1
            ? ScriptedCloudBench.Result(CloudBenchOutcome.Failed, minutes: 20, cost: 0.023, exit: 2, error: "the benchmark exited 2", stage: CloudBenchStage.Run)
            : ScriptedCloudBench.Result(CloudBenchOutcome.Failed, minutes: 0, cost: null, exit: null, error: "az vm create: QuotaExceeded", stage: CloudBenchStage.Create));
        var (jobs, _, _) = Runner(Settings());

        var ran = await Until(HandCloudBench(jobs), j => j.EndedAt is not null);
        var never = await Until(HandCloudBench(jobs), j => j.EndedAt is not null);

        (ran.State, ran.ExitCode, ran.Error).ShouldBe((JobState.Failed, 2, "the benchmark exited 2"));
        (never.State, never.ExitCode).ShouldBe((JobState.Failed, null));
        never.Error.ShouldBe("the benchmark never started: the cloud bench stopped at create: az vm create: QuotaExceeded");
        ran.BenchVm.ShouldNotBeNull().Cost.ShouldBe(0.023);
    }

    [Fact]
    public async Task AnAzureResourceThatCouldNotBeDeletedIsNamedOnTheRowAndInTheLog()
    {
        _cloud.Behavior = (_, _, _) => Task.FromResult(ScriptedCloudBench.Result(leaked: ["pilot-bench-j1-disk"]));
        var (jobs, _, _) = Runner(Settings());

        var bench = HandCloudBench(jobs);
        var ended = await Until(bench, j => j.EndedAt is not null);

        ended.State.ShouldBe(JobState.Succeeded);
        ended.Error.ShouldNotBeNull().ShouldContain("1 Azure resource could not be deleted and cost money until a person deletes it: pilot-bench-j1-disk");
        LogOf(bench).ShouldContain("pilot-bench-j1-disk");
    }

    [Fact]
    public async Task ANodeThatStopsWhileABenchRunsOnItsVmHasTheVmDeletedAndEndsTheRowSayingSo()
    {
        var cleaned = Gate();
        _cloud.Behavior = async (_, token, _) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
            }
            cleaned.SetResult();
            return ScriptedCloudBench.Result(CloudBenchOutcome.Cancelled, minutes: 2, cost: 0.002, exit: null, stage: CloudBenchStage.Run);
        };
        var jobs = await StartedRunner(Settings());
        var bench = HandCloudBenchStarted(jobs);
        await Until(bench, j => j.State == JobState.Running);

        await jobs.StopAsync(CancellationToken.None);

        cleaned.Task.IsCompletedSuccessfully.ShouldBeTrue();
        var ended = Row(bench);
        ended.State.ShouldBe(JobState.Failed);
        ended.Error.ShouldNotBeNull().ShouldContain("the node stopped while the bench ran on its VM, so the VM was deleted");
    }

    [Fact]
    public async Task ACloudBenchRowALostNodeLeftRunningIsEndedNamingTheResourcesToDelete()
    {
        var jobs = await StartedRunner(Settings());
        _state.PutJob(new JobRow
        {
            Id = "j7", Command = "bench", Cwd = _root, Kind = JobKind.Bench, State = JobState.Running, LogPath = Path.Combine(_root, "j7.log"), Timeout = TimeSpan.FromHours(1),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20), BenchVm = new BenchVm { Size = "Standard_F8s_v2", Image = Image, Region = "westeurope" },
        });

        await jobs.RecoverAsync(Ct);

        var ended = Row("j7");
        ended.State.ShouldBe(JobState.Failed);
        ended.Error.ShouldNotBeNull().ShouldContain("pilot-bench-j7-");
        ended.Error.ShouldContain("rg-bench");
        ended.Error.ShouldContain("cost money");
        _cloud.Requests.ShouldBeEmpty();
    }

    private string HandCloudBenchStarted(JobRunner jobs)
    {
        // The dispatcher of a started runner reads the hand-over from its channel: QueueBench writes it there.
        var (id, _) = jobs.QueueBench(Echo("benched"), _root, TimeSpan.FromMinutes(30), owner: null, local: false, Plan(_root));
        return id;
    }

    private void SeedCloudBench(string id, DateTimeOffset ended, double cost) =>
        _state.PutJob(new JobRow
        {
            Id = id, Command = "bench", Cwd = _root, Kind = JobKind.Bench, State = JobState.Succeeded, ExitCode = 0, LogPath = Path.Combine(_root, id + ".log"), Timeout = JobRunner.DefaultTimeout,
            StartedAt = ended.AddMinutes(-40), EndedAt = ended, Told = true,
            BenchVm = new BenchVm { Size = "Standard_F8s_v2", Image = Image, Region = "westeurope", PricePerHour = 0.07, Minutes = 40, Cost = cost },
        });
}
