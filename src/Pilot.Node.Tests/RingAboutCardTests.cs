using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1290: a ring that names another open card is about it, and closes when that card closes.</summary>
public sealed class RingAboutCardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CardRow RaiseAsk(NodeHarness node) =>
        node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1 needs a round", null,
            [new CardChoice { Label = "Allow one more round", Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["handled", "round allowed"] }]);

    private static async Task RaiseAbout(NodeHarness node, string title)
    {
        var response = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["ring", title], Who = "cli" }, Ct)).Response.ShouldNotBeNull();
        response.Error.ShouldBeNull();
    }

    private static CardRow[] Open(NodeHarness node) => node.State.Cards(openOnly: true).ToArray();

    [Fact]
    public async Task ARingNamingACardClosesWhenThatCardIsAnswered()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = RaiseAsk(node);
        await RaiseAbout(node, $"Press Allow one more round on {a.Id}");
        var ring = node.State.Cards().Single(c => c.Kind == "watchtower");
        ring.AboutCard.ShouldBe(a.Id);

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(a.Id), Args = ["Allow one more round"], Who = "cli" }, Ct))
            .Response.ShouldNotBeNull().Error.ShouldBeNull();

        Open(node).ShouldBeEmpty();
        var closed = node.State.Cards().Single(c => c.Id == ring.Id);
        closed.Outcome.ShouldNotBeNull().ShouldStartWith($"{a.Id} closed: ");
        node.State.Cards().Any(c => c.Bin == CardBin.Handled && c.Title.Contains(ring.Id) && c.Title.Contains(a.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task ARingNamingACardClosesWhenThatCardIsDismissed()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = RaiseAsk(node);
        await RaiseAbout(node, $"Please look at {a.Id}");

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Dismiss, Target = Verbs.CardTarget(a.Id), Who = "cli" }, Ct))
            .Response.ShouldNotBeNull().Error.ShouldBeNull();

        Open(node).ShouldBeEmpty();
        node.State.Cards().Any(c => c.Bin == CardBin.Handled && c.Title.Contains(a.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task ARaiseNamingNoCardOrAClosedCardLinksNothing()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = RaiseAsk(node);
        node.State.CloseCard(a.Id, "dismissed", "cli");
        await RaiseAbout(node, "Nothing here names a card");
        await RaiseAbout(node, $"Press {a.Id} which is closed already");

        node.State.Cards().Where(c => c.Kind == "watchtower").ShouldAllBe(c => c.AboutCard == null);
    }

    [Fact]
    public async Task ClosingTheAskingCardLeavesTheCardItIsAboutOpen()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = RaiseAsk(node);
        await RaiseAbout(node, $"Press {a.Id}");
        var ring = node.State.Cards().Single(c => c.Kind == "watchtower");

        node.State.CloseCard(ring.Id, "dismissed", "cli");

        Open(node).Select(c => c.Id).ShouldBe([a.Id]);
    }
}
