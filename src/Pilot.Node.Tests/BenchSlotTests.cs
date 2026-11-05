using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>An exclusive (bench) job has the node to itself: it waits for no other job and a quiet CPU, and nothing else starts beside it (PIL675).</summary>
[Collection(JobsCollection.Name)]
public sealed class BenchSlotTests : JobTestsBase
{
    private double? _cpu = 5;

    private string HandBench(JobRunner jobs, string command)
    {
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [Verbs.AloneArg, _root, "0", command], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private static void Look(JobRunner jobs) => jobs.Dispatch(JobRunner.LookAgain.Instance);

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    [Fact]
    public async Task ABenchJobBehindARunningJobStartsOnlyAfterItEndsAndTheCpuIsQuiet()
    {
        var (jobs, _, _, gone) = Idle(() => _cpu);
        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);
        var bench = HandBench(jobs, Echo("bench"));

        var row = Row(bench);
        row.Kind.ShouldBe(JobKind.Bench);
        row.State.ShouldBe(JobState.Queued);
        row.Step.ShouldNotBeNull().ShouldContain("another job is running");

        await gone(first);
        _cpu = 60;
        Look(jobs);
        Row(bench).State.ShouldBe(JobState.Queued);
        Row(bench).Step.ShouldNotBeNull().ShouldContain("quiet CPU");

        _cpu = 5;
        Look(jobs);
        (await Until(bench, j => j.State is JobState.Running or JobState.Succeeded)).Kind.ShouldBe(JobKind.Bench);
    }

    [Fact]
    public async Task ARunAndACheckHandedOverWhileABenchRunsStayQueuedNamingItAndStartAfter()
    {
        var (jobs, _, _, gone) = Idle(() => _cpu);
        jobs.MaxRunning = 4;
        var bench = HandBench(jobs, Sleep(30));
        await Until(bench, j => j.State == JobState.Running);

        var run = Hand(jobs, Sleep(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var check = jobs.RunCheckAsync("TIA1", "parser", Sleep(30), _root, TimeSpan.FromMinutes(5), cts.Token);
        var checkRow = _state.Jobs().Single(j => j.Kind == JobKind.Check);
        jobs.Dispatch(new JobRunner.HandedOver(checkRow));

        Row(run).State.ShouldBe(JobState.Queued);
        Row(run).Step.ShouldNotBeNull().ShouldContain($"a benchmark ({bench}) holds the node");
        Row(checkRow.Id).State.ShouldBe(JobState.Queued);
        Row(checkRow.Id).Step.ShouldNotBeNull().ShouldContain($"a benchmark ({bench}) holds the node");

        await gone(bench);
        Look(jobs);
        await Until(run, j => j.State == JobState.Running);
        await Until(checkRow.Id, j => j.State == JobState.Running);

        await gone(run);
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task OnlyOneBenchJobRunsAtATimeAndItGoesAheadOfOtherQueuedJobs()
    {
        var (jobs, _, _, gone) = Idle(() => _cpu);
        jobs.MaxRunning = 4;
        var first = HandBench(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);
        var plain = Hand(jobs, Echo("plain"));
        var second = HandBench(jobs, Echo("second"));

        // The second bench waits even with the machine idle, and heads the queue ahead of the plain run.
        Row(second).State.ShouldBe(JobState.Queued);
        Row(second).Step.ShouldNotBeNull().ShouldContain("runs alone");
        Row(plain).State.ShouldBe(JobState.Queued);
        Row(plain).Step.ShouldNotBeNull().ShouldContain("queued, 1 ahead");

        await gone(first);
        Look(jobs);
        await Until(second, j => j.State is JobState.Running or JobState.Succeeded);
    }

    [Fact]
    public async Task ABenchWaitingOnOutsideLoadLetsAnOrdinaryJobBehindItStart()
    {
        double? outside = 90;
        _cpu = 95;
        var (jobs, _, _, gone) = Idle(() => _cpu, () => outside);
        jobs.MaxRunning = 4;
        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);
        var bench = HandBench(jobs, Echo("bench"));

        Row(bench).State.ShouldBe(JobState.Queued);
        Row(bench).Step.ShouldNotBeNull().ShouldContain("waits for a quiet CPU (95% now, 90% outside pilot");
        Row(bench).Step.ShouldNotBeNull().ShouldContain("other jobs run meanwhile");

        var run = Hand(jobs, Sleep(30));
        await Until(run, j => j.State == JobState.Running);
        Row(bench).State.ShouldBe(JobState.Queued);

        await gone(run);
        await gone(first);
        Row(bench).State.ShouldBe(JobState.Queued);
    }

    [Fact]
    public async Task ABenchHoldsNewJobsOnceOutsideLoadDropsAndStartsWhenNothingRunsAndTheCpuIsQuiet()
    {
        double? outside = 90;
        _cpu = 95;
        var (jobs, _, _, gone) = Idle(() => _cpu, () => outside);
        jobs.MaxRunning = 4;
        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);
        var bench = HandBench(jobs, Echo("bench"));
        Row(bench).State.ShouldBe(JobState.Queued);

        outside = 2;
        _cpu = 40;
        var later = Hand(jobs, Echo("later"));
        Row(later).State.ShouldBe(JobState.Queued);
        Row(later).Step.ShouldNotBeNull().ShouldContain($"a benchmark ({bench}) holds the node");

        await gone(first);
        _cpu = 5;
        Look(jobs);
        (await Until(bench, j => j.State is JobState.Running or JobState.Succeeded)).Kind.ShouldBe(JobKind.Bench);
    }

    [Fact]
    public async Task ALegWorkerStartIsHeldWhileABenchIsQueuedOrRunningAndAdmittedAfter()
    {
        var (jobs, _, _, gone) = Idle(() => _cpu);
        var roomy = new RoleModels(NodeSettings.With(new NodeSettings(), "opus-cap", ["5"]).Settings!);
        var gate = new AdmissionGate(_state, () => roomy, "DE1");
        gate.Ask(AdmissionRole.Worker, null, null).ShouldBeOfType<AdmissionDecision.Admit>();

        var first = Hand(jobs, Sleep(30));
        await Until(first, j => j.State == JobState.Running);
        var bench = HandBench(jobs, Echo("bench"));
        Row(bench).State.ShouldBe(JobState.Queued);
        gate.Ask(AdmissionRole.Worker, null, null).ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldContain($"a benchmark ({bench}) holds the node");
        gate.Ask(AdmissionRole.Reviewer, null, null).ShouldBeOfType<AdmissionDecision.Wait>();

        await gone(first);
        Look(jobs);
        await Until(bench, j => j.EndedAt is not null);
        gate.Ask(AdmissionRole.Worker, null, null).ShouldBeOfType<AdmissionDecision.Admit>();
    }
}
