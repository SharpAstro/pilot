using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

// #984: the review of PIL823's notes. The Azure deletes of a lost node's benches run beside the dispatcher, a stop mid-delete leaves the
// row for the next start, a cloud set up by any one key says why it is off, and only a bench that asked for a VM is billed for one.
public sealed partial class CloudBenchJobTests
{
    [Fact]
    public async Task RecoveredDoesNotWaitForALostBenchsAzureDeleteWhichEndsTheRowWhenItIsDone()
    {
        SeedLostBench();
        var gate = Gate();
        var asked = Gate();
        _cloud.CleanupAsync = async (_, ct) =>
        {
            asked.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return new CloudCleanupResult();
        };

        // StartedRunner waits on Recovered: it must come while the delete is still held (the delete starts beside it, so it is awaited too).
        var jobs = await StartedRunner(Settings());
        await asked.Task.WaitAsync(WaitCeiling, Ct);
        _cloud.Cleanups.ShouldHaveSingleItem().JobId.ShouldBe("j7");
        Row("j7").State.ShouldBe(JobState.Running, "the delete is still running");
        jobs.LostCloud.IsCompleted.ShouldBeFalse();

        gate.SetResult();
        await jobs.LostCloud.WaitAsync(WaitCeiling, Ct);
        Row("j7").State.ShouldBe(JobState.Failed);
        Row("j7").Error.ShouldNotBeNull().ShouldContain("deleted the Azure resources named pilot-bench-j7-");
    }

    [Fact]
    public async Task ANodeThatStopsWhileItDeletesALostBenchLeavesTheRowRunningForTheNextStart()
    {
        SeedLostBench();
        var asked = Gate();
        _cloud.CleanupAsync = async (_, ct) =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new CloudCleanupResult();
        };
        var jobs = await StartedRunner(Settings());
        await asked.Task.WaitAsync(WaitCeiling, Ct);

        await jobs.StopAsync(CancellationToken.None);
        await jobs.LostCloud.WaitAsync(WaitCeiling, Ct);

        // Not "may still be running" for a delete that never finished: the next start deletes them again (a delete by name repeats safely).
        var row = Row("j7");
        row.State.ShouldBe(JobState.Running);
        row.Error.ShouldBeNull();
        File.ReadAllText(Path.Combine(_root, "j7.log")).ShouldContain("this node stopped while it deleted the Azure resources named pilot-bench-j7-");
    }

    [Theory]
    [InlineData(NodeSettings.BenchRegionKey, "westeurope")]
    [InlineData(NodeSettings.BenchAuthKey, "service-principal")]
    [InlineData(NodeSettings.BenchSpotPriceKey, "0.3")]
    [InlineData(NodeSettings.BenchSubscriptionKey, "00000000-0000-0000-0000-000000000001")]
    public void ACloudSetUpByAnyOneBenchKeySaysWhyItIsOffOnTheBenchsRow(string key, string value)
    {
        var settings = NodeSettings.With(new NodeSettings(), key, [value]).Settings.ShouldNotBeNull();
        var (jobs, _, _) = Runner(settings);

        var bench = HandCloudBench(jobs);

        _cloud.Requests.ShouldBeEmpty();
        Row(bench).BenchVm.ShouldNotBeNull().LocalBecause.ShouldNotBeNullOrWhiteSpace($"{key} alone is a cloud begun, so the row says why it is off");
    }

    [Fact]
    public async Task ARunnerThatThrowsBillsTheVmsMinutesSinceTheStartAsAnUpperBound()
    {
        _cloud.Behavior = (_, _, _) => throw new InvalidOperationException("az crashed");
        var (jobs, _, _) = Runner(Settings());

        var ended = await Until(HandCloudBench(jobs), j => j.EndedAt is not null);

        ended.State.ShouldBe(JobState.Failed);
        ended.Error.ShouldNotBeNull().ShouldContain("az crashed");
        var vm = ended.BenchVm.ShouldNotBeNull();
        vm.PriceIsUpperBound.ShouldBeTrue("the runner gave no price, so the settings' most is billed");
        vm.PricePerHour.ShouldBe(0.3);
        vm.Minutes.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0);
        vm.Cost.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ABenchThatEndedBeforeItAskedForAVmCostsNothing()
    {
        var vm = new BenchVm { Size = "Standard_F8s_v2", Image = Image, Region = "westeurope" };

        var unbilled = JobRunner.Unbilled(vm).ShouldNotBeNull();

        (unbilled.Minutes, unbilled.Cost, unbilled.PriceIsUpperBound).ShouldBe((0, 0, false));
        unbilled.Size.ShouldBe("Standard_F8s_v2", "the row still says what it would have run on");
        // A VM the runner already priced is left as it was.
        JobRunner.Unbilled(vm with { Cost = 0.4, Minutes = 30 }).ShouldBe(vm with { Cost = 0.4, Minutes = 30 });
    }
}
