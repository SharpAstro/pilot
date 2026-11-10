using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The summary's model (#797): Sonnet unless a setting says otherwise, chosen through <see cref="RoleModels"/> as the role ranges are
/// (this node's value over the fleet's), and the summary's <c>claude -p</c> call carries it at each call, with no restart.
/// </summary>
public sealed class SummaryModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RoleModels With(string? own = null, string? fleet = null) => new(new NodeSettings
    {
        Roles = own is null ? null : new Dictionary<string, string> { [NodeSettings.SummaryModelKey] = own },
        FleetRoles = fleet is null ? null : new Dictionary<string, string> { [NodeSettings.SummaryModelKey] = fleet },
    });

    private static string ModelOf(IReadOnlyList<string> args) => args[args.ToList().IndexOf("--model") + 1];

    [Fact]
    public void TheSummaryRunsOnSonnetWhenNothingIsSet()
    {
        new RoleModels(null).SummaryModelWithSource().ShouldBe(("sonnet", SettingSources.BuiltIn));
        RoleModels.Unset.SummaryModel.ShouldBe("sonnet");
    }

    [Fact]
    public void ThisNodesSettingOverridesTheDefaultAndTheFleetsValue()
    {
        With(own: "opus").SummaryModelWithSource().ShouldBe(("opus", SettingSources.ThisNode));
        With(fleet: "haiku").SummaryModelWithSource().ShouldBe(("haiku", SettingSources.Fleet));
        With(own: "opus", fleet: "haiku").SummaryModelWithSource().ShouldBe(("opus", SettingSources.ThisNode));
    }

    [Fact]
    public void TheCallsArgumentsCarryTheModelAtEachCallSoASetNeedsNoRestart()
    {
        var models = new RoleModels(null);
        var call = new ClaudeSummaryModel(Path.GetTempPath(), models);

        ModelOf(call.Arguments).ShouldBe("sonnet");

        models.Apply(new NodeSettings { Roles = new Dictionary<string, string> { [NodeSettings.SummaryModelKey] = "opus" } });
        ModelOf(call.Arguments).ShouldBe("opus");

        models.Apply(new NodeSettings { FleetRoles = new Dictionary<string, string> { [NodeSettings.SummaryModelKey] = "claude-haiku-5-5" } });
        ModelOf(call.Arguments).ShouldBe("claude-haiku-5-5");

        // Nothing but the model varies.
        call.Arguments.Select((a, i) => (a, i)).Where(p => p.i != call.Arguments.ToList().IndexOf("--model") + 1).Select(p => p.a)
            .ShouldBe(ClaudeSummaryModel.ArgumentsFor("sonnet").Select((a, i) => (a, i)).Where(p => p.i != 2).Select(p => p.a));
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("haiku")]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("claude-sonnet-5-5[1m]")]
    public void AModelIsTakenAsAnAliasOrAFullId(string model)
    {
        var (settings, error) = NodeSettings.With(new NodeSettings(), NodeSettings.SummaryModelKey, [model]);

        error.ShouldBeNull();
        settings!.Get(NodeSettings.SummaryModelKey).ShouldBe([model]);
        new RoleModels(settings).SummaryModel.ShouldBe(model);
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("sonnet-high")]
    [InlineData("sonnet..opus")]
    [InlineData("claude-")]
    public void AnythingElseIsRefusedSayingWhatIsTaken(string text)
    {
        var (settings, error) = NodeSettings.With(new NodeSettings(), NodeSettings.SummaryModelKey, [text]);

        settings.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("summary-model takes a model: sonnet, opus or haiku");
    }

    [Fact]
    public void DefaultTakesTheSettingOutAndTheFleetLayerIsKeptApart()
    {
        var set = NodeSettings.With(new NodeSettings(), NodeSettings.SummaryModelKey, ["opus"]).Settings!;
        var fleet = NodeSettings.WithFleet(set, NodeSettings.SummaryModelKey, ["haiku"]).Settings!;

        fleet.Roles![NodeSettings.SummaryModelKey].ShouldBe("opus");
        fleet.FleetRoles![NodeSettings.SummaryModelKey].ShouldBe("haiku");

        var cleared = NodeSettings.With(fleet, NodeSettings.SummaryModelKey, ["default"]).Settings!;
        new RoleModels(cleared).SummaryModelWithSource().ShouldBe(("haiku", SettingSources.Fleet));
    }

    [Fact]
    public void TheSettingSurvivesTheFileForThisNodeAndForTheFleet()
    {
        var file = Path.Combine(Path.GetTempPath(), "pilot-summary-model-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var own = NodeSettings.With(new NodeSettings(), NodeSettings.SummaryModelKey, ["opus"]).Settings!;
            NodeSettings.WithFleet(own, NodeSettings.SummaryModelKey, ["haiku"]).Settings!.Write(file);

            var read = new RoleModels(NodeSettings.Read(file));

            read.SummaryModelWithSource().ShouldBe(("opus", SettingSources.ThisNode));
            NodeSettings.Read(file).FleetRoles![NodeSettings.SummaryModelKey].ShouldBe("haiku");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task TheSettingsPageListsItWithItsBuiltInWhyAndASetIsInForceAtOnce()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var row = (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull().Settings.Single(s => s.Key == NodeSettings.SummaryModelKey);
        (row.Label, row.Value, row.Source).ShouldBe(("Summary model", "sonnet", SettingSources.BuiltIn));
        row.Detail.ShouldContain("Built in: sonnet, " + RoleModels.SummaryModelWhy);
        row.Choices.ShouldBe(["sonnet", "opus", "haiku"]);

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.SummaryModelKey, "opus"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe("summary-model saved as opus, and in force now; the next summary runs on it");
        node.App.Services.GetRequiredService<RoleModels>().SummaryModel.ShouldBe("opus");
        var after = (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == NodeSettings.SummaryModelKey);
        (after.Value, after.Saved, after.Source, after.Pending).ShouldBe(("opus", "opus", SettingSources.ThisNode, false));

        var refused = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.SummaryModelKey, "gpt-5"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        refused.Ok.ShouldBeFalse();
    }
}
