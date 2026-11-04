using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The two settings of the disk sweep (pilot #711): scratch-days and disk-alert-gb.</summary>
public sealed class DiskSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("scratch-days", "0", "scratch-days takes a number, 1 to 365")]
    [InlineData("scratch-days", "366", "scratch-days takes a number, 1 to 365")]
    [InlineData("scratch-days", "x", "scratch-days takes a number, 1 to 365")]
    [InlineData("disk-alert-gb", "0", "disk-alert-gb takes a number, 1 to 10000")]
    [InlineData("disk-alert-gb", "10001", "disk-alert-gb takes a number, 1 to 10000")]
    [InlineData("disk-alert-gb", "x", "disk-alert-gb takes a number, 1 to 10000")]
    public void ABadValueIsRefusedWithTheRange(string key, string value, string error) =>
        NodeSettings.With(new NodeSettings(), key, [value]).Error.ShouldBe(error);

    [Fact]
    public void AGoodValueIsKeptReachesRoleModelsAndAClearReturnsTheDefault()
    {
        var set = NodeSettings.With(new NodeSettings(), NodeSettings.ScratchDaysKey, ["7"]).Settings.ShouldNotBeNull();
        set = NodeSettings.With(set, NodeSettings.DiskAlertGbKey, ["50"]).Settings.ShouldNotBeNull();
        set.ScratchDays.ShouldBe(7);
        set.DiskAlertGb.ShouldBe(50);
        var models = new RoleModels(set);
        models.ScratchDays.ShouldBe(7);
        models.DiskAlertGb.ShouldBe(50);

        var cleared = NodeSettings.With(set, NodeSettings.ScratchDaysKey, ["default"]).Settings.ShouldNotBeNull();
        cleared = NodeSettings.With(cleared, NodeSettings.DiskAlertGbKey, []).Settings.ShouldNotBeNull();
        var defaults = new RoleModels(cleared);
        defaults.ScratchDays.ShouldBe(2);
        defaults.DiskAlertGb.ShouldBe(20);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task SetNodeScratchDaysAndDiskAlertGbAreInForceAtOnceAndRoundTripThroughSettings()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var models = node.App.Services.GetRequiredService<RoleModels>();
        models.ScratchDays.ShouldBe(2);
        models.DiskAlertGb.ShouldBe(20);

        foreach (var (key, value) in new[] { (NodeSettings.ScratchDaysKey, "9"), (NodeSettings.DiskAlertGbKey, "35") })
        {
            var set = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [key, value], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
            set.Error.ShouldBeNull();
            set.Way.ShouldBe($"{key} saved as {value}, and in force now");
            (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == key).Value.ShouldBe(value);
        }
        models.ScratchDays.ShouldBe(9);
        models.DiskAlertGb.ShouldBe(35);

        foreach (var bad in new[] { (NodeSettings.ScratchDaysKey, "0"), (NodeSettings.DiskAlertGbKey, "10001") })
        {
            (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [bad.Item1, bad.Item2], Who = "tests" }, Ct)).Response!.Error.ShouldNotBeNull();
        }
    }
}
