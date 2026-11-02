using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg's go-arounds are capped whatever their reason (PIL277 went round six times): past the cap the watchtower and the node's own
/// sends are refused and a Ring card lists every round's reason; the person's go-around and Accept still work.
/// </summary>
[Trait("Category", "Slow")]
public sealed class GoAroundCapTests : LegHoldTestsBase
{
    private const string Tower = "du1-watchtower";

    private Task<CommandResult> As(string who, string verb, string target, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);

    private async Task RoundsAsTower(int rounds)
    {
        for (var round = 1; round <= rounds; round++)
        {
            (await As(Tower, Verbs.GoAround, "flight/TIA1/parser", $"the check failed in round {round}")).Error.ShouldBeNull();
        }
    }

    private CardRow? Cap() => _state.Cards(openOnly: true).SingleOrDefault(c => c.Kind == LegCommander.GoAroundCapKind);

    [Fact]
    public async Task TheSixthGoAroundOfALegIsRefusedAndRaisesTheCardWithEveryRoundsReason()
    {
        await FlyingParser();
        await RoundsAsTower(5);
        L("parser").GoArounds.ShouldBe(5);
        Cap().ShouldBeNull();

        var refused = await As(Tower, Verbs.GoAround, "flight/TIA1/parser", "the check failed in round 6");

        refused.Error.ShouldNotBeNull().ShouldContain("cap is 5");
        L("parser").GoArounds.ShouldBe(5);
        var card = Cap().ShouldNotBeNull();
        card.Kind.ShouldBe(LegCommander.GoAroundCapKind);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Subject.ShouldBe("flight/TIA1/parser");
        for (var round = 1; round <= 5; round++)
        {
            card.Detail.ShouldNotBeNull().ShouldContain($"the check failed in round {round}");
        }
        L("parser").GoAroundReasons.Select(r => r.Why).ShouldBe(Enumerable.Range(1, 5).Select(i => $"the check failed in round {i}"));
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.GoAround, Verbs.FinishLeg]);

        // The node's own send is refused the same way.
        (await As(LegWatch.Who, Verbs.GoAround, "flight/TIA1/parser", "context full")).Error.ShouldNotBeNull().ShouldContain("cap is 5");

        // The person's Go around passes the cap, and closes the card.
        var go = card.Choices[0];
        (await As("gui", go.Verb, go.Target, [.. go.Args])).Error.ShouldBeNull();
        L("parser").GoArounds.ShouldBe(6);
        Cap().ShouldBeNull();
    }

    [Fact]
    public async Task TheCardsAcceptTakesAFlyingLegAsItStandsForThePersonOnly()
    {
        await FlyingParser();
        await RoundsAsTower(5);
        await As(Tower, Verbs.GoAround, "flight/TIA1/parser", "one more");
        var accept = Cap().ShouldNotBeNull().Choices[1];

        (await As(Tower, accept.Verb, accept.Target, [.. accept.Args])).Error.ShouldNotBeNull();
        (await As("gui", accept.Verb, accept.Target, [.. accept.Args])).Error.ShouldBeNull();

        L("parser").State.ShouldBe(LegState.Done);
        Cap().ShouldBeNull();
    }

    [Fact]
    public async Task TheCapIsASettingAndLowersWithIt()
    {
        await FlyingParser();
        _legs.Models = new RoleModels(new NodeSettings { MaxGoArounds = 2 });

        await RoundsAsTower(2);
        (await As(Tower, Verbs.GoAround, "flight/TIA1/parser", "a third")).Error.ShouldNotBeNull().ShouldContain("cap is 2");
        Cap().ShouldNotBeNull();
    }

    [Fact]
    public async Task SetNodeMaxGoAroundsIsInForceAtOnceAndRefusesWhatIsNotANumberFromOneToFifty()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var models = node.App.Services.GetRequiredService<RoleModels>();
        models.MaxGoArounds.ShouldBe(5);

        var set = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["max-go-arounds", "2"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        set.Error.ShouldBeNull();
        set.Way.ShouldBe("max-go-arounds saved as 2, and in force now");
        models.MaxGoArounds.ShouldBe(2);
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == NodeSettings.MaxGoAroundsKey).Value.ShouldBe("2");
        foreach (var bad in new[] { "0", "51", "many" })
        {
            (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["max-go-arounds", bad], Who = "tests" }, Ct)).Response!.Error.ShouldBe("max-go-arounds takes a number, 1 to 50");
        }
    }
}
