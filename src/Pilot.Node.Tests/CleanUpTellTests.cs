using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The panel's Clean up (#706): the standard tidy-up message goes to the watchtower through the node's tell, as the person's turn.</summary>
public sealed class CleanUpTellTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CommandRequest CleanUp(string who) =>
        new() { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = [WatchtowerAsks.CleanUp], Who = who };

    [Fact]
    public async Task APersonsCleanUpTellReachesTheWatchtowerUnchangedAndIsNotedAsTheirs()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var name = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, name, status: "busy"));
        for (var i = 0; i < 100 && !node.State.Sessions().Any(s => s.Name == name); i++)
        {
            await Task.Delay(50, Ct);
        }

        var told = (await node.Client.SendAsync(CleanUp("gui"), Ct)).Response.ShouldNotBeNull();

        told.Error.ShouldBeNull();
        node.Delivery.Relays.ShouldHaveSingleItem().Text.ShouldBe(WatchtowerAsks.CleanUp);
        node.App.Services.GetService(typeof(Watchtower)).ShouldBeOfType<Watchtower>().ActingForPerson.ShouldBeTrue();
    }

    [Fact]
    public async Task ACleanUpTellWithNoWatchtowerIsRefusedInWordsAndSaysNothing()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var told = (await node.Client.SendAsync(CleanUp("gui"), Ct)).Response.ShouldNotBeNull();

        told.Error.ShouldNotBeNullOrWhiteSpace();
        node.Delivery.Relays.ShouldBeEmpty();
    }
}
