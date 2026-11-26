using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// #1357's notification rule needs the buddy to learn whether another client shows the cards: <see cref="PageViews"/> is the one source and
/// <see cref="EventHub"/> sends each client an <see cref="OthersShowCardsEvent"/> when the answer changes, so the buddy never polls or guesses.
/// </summary>
public sealed class PageViewsOthersShowCardsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void OthersShowCardsCountsEveryClientButTheAskingOne()
    {
        var views = new PageViews();
        var buddy = new object();
        var gui = new object();
        views.OthersShowCards(buddy).ShouldBeFalse();

        views.SayCards(buddy, true);
        views.OthersShowCards(buddy).ShouldBeFalse("the buddy alone is not another client");
        views.OthersShowCards(gui).ShouldBeTrue("the GUI sees the buddy as one that shows the cards");

        views.SayCards(gui, true);
        views.OthersShowCards(buddy).ShouldBeTrue();

        views.Forget(gui);
        views.OthersShowCards(buddy).ShouldBeFalse();
    }

    [Fact]
    public void CardsChangedIsRaisedOnlyWhenTheSetChanges()
    {
        var views = new PageViews();
        var client = new object();
        var raised = 0;
        views.CardsChanged += () => raised++;

        views.SayCards(client, false);
        raised.ShouldBe(0);
        views.SayCards(client, true);
        views.SayCards(client, true);
        raised.ShouldBe(1);
        views.Forget(client);
        raised.ShouldBe(2);
    }

    private static async Task<bool> NextOthers(NodeEventStream stream)
    {
        // Bounded by the node's answering: the harness's own stream ends the read when the test's token is cancelled.
        while (await stream.ReadAsync(Ct) is { } next)
        {
            if (next is OthersShowCardsEvent others)
            {
                return others.Shown;
            }
        }
        throw new InvalidOperationException("the stream ended before the node said whether others show the cards");
    }

    [Fact]
    public async Task TheNodeTellsEachClientWhetherAnotherClientShowsTheCards()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var views = node.App.Services.GetRequiredService<PageViews>();

        await using var buddy = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        await buddy.SayPageAsync(null, Ct, showsCards: true);
        // Alone: it is told nothing (false is what it assumes), then a GUI joins and shows the cards.
        await using var gui = await node.Client.OpenEventsAsync(since: null, stream: null, Ct);
        await gui.SayPageAsync(null, Ct, showsCards: true);

        (await NextOthers(buddy)).ShouldBeTrue("the GUI shows the cards");
        (await NextOthers(gui)).ShouldBeTrue("the GUI is told the buddy shows them too");

        await gui.SayPageAsync(null, Ct, showsCards: false);
        (await NextOthers(buddy)).ShouldBeFalse("the GUI stopped showing the cards");
        views.AnyShowsCards.ShouldBeTrue("the buddy itself still shows them");
    }
}
