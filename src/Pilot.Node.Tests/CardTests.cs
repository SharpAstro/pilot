using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Cards on the node: one event, one card, closed when their thing is done, and kept across a restart.</summary>
public sealed class CardStateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2k-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ACardIsRaisedOnceForItsEventAndAHandledOneIsBornClosed()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);

        var first = state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge");
        var again = state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge");
        var handled = state.RaiseCard(CardBin.Handled, "resumed", "session/a1b2c3d4", "Resumed a1b2c3d4 after the usage window reset");

        again.Id.ShouldBe(first.Id);
        (first.Id, handled.Id).ShouldBe(("c1", "c2"));
        handled.State.ShouldBe(CardState.Closed);
        state.Cards(openOnly: true).ShouldHaveSingleItem().Id.ShouldBe("c1");
    }

    [Fact]
    public void AFlightsCardsCloseWithItIncludingItsLegsButNotAnotherFlights()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "a");
        state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "b");
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA10", "c");

        state.CloseCardsAbout("flight/TIA1", "TIA1 landed");

        state.Cards(openOnly: true).Select(c => c.Subject).ShouldBe(["flight/TIA10"]);
        state.Cards().Where(c => c.Subject.StartsWith("flight/TIA1/", StringComparison.Ordinal) || c.Subject == "flight/TIA1")
            .ShouldAllBe(c => c.Outcome == "TIA1 landed" && c.ClosedBy == "node");
        state.CloseCard("c1", "again", "cli").ShouldBeFalse();
    }

    [Fact]
    public void CardsSurviveARestartAndNumberingCarriesOn()
    {
        using (var journal = new FileJournal(_folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "a", "detail",
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["abc1234"] }]);
            state.CloseCard("c1", "Merge", "gui");
        }

        using var again = new FileJournal(_folder);
        var restored = new NodeState(again, TimeProvider.System);
        var card = restored.Cards().ShouldHaveSingleItem();
        (card.State, card.Outcome, card.ClosedBy, card.Choices.Single().Args.Single()).ShouldBe((CardState.Closed, "Merge", "gui", "abc1234"));
        restored.RaiseCard(CardBin.ForYou, "landed", "flight/TIA1", "TIA1 landed").Id.ShouldBe("c2");
    }
}

/// <summary>Cards through the command route on a real node: answered by a choice, dismissed, refused.</summary>
public sealed class CardRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CommandRequest Command(string verb, string target, params string[] args) =>
        new() { Verb = verb, Target = target, Args = args, Who = "tests" };

    [Fact]
    public async Task AnAnswerRunsItsChoiceAndClosesTheCardARefusedOneLeavesItOpen()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.RaiseCard(CardBin.ForYou, "question", "node", "Keep a note?", choices:
        [
            new CardChoice { Label = "Note it", Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["noted from a card"] },
            new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = "flight/TIA9" },
        ]);

        var refused = await node.Client.SendAsync(Command(Verbs.Answer, "card/c1", "land it"), Ct);
        refused.Response.ShouldNotBeNull().Error.ShouldBe("Land it was refused: There is no flight TIA9");
        node.State.Cards(openOnly: true).ShouldHaveSingleItem();

        var unknown = await node.Client.SendAsync(Command(Verbs.Answer, "card/c1", "7"), Ct);
        unknown.Response.ShouldNotBeNull().Error.ShouldBe("card c1 offers 1 Note it, 2 Land it, not '7'");

        (await node.Client.SendAsync(Command(Verbs.Answer, "card/c1", "1"), Ct)).Success.ShouldBeTrue();
        var card = node.State.Cards().Single();
        (card.State, card.Outcome, card.ClosedBy).ShouldBe((CardState.Closed, "Note it", "tests"));
        node.State.Snapshot().Notes.ShouldHaveSingleItem().Text.ShouldBe("noted from a card");

        (await node.Client.SendAsync(Command(Verbs.Dismiss, "card/c1"), Ct)).Response.ShouldNotBeNull().Error.ShouldBe("card c1 is closed already (Note it)");
        (await node.Client.SendAsync(Command(Verbs.Dismiss, "card/c9"), Ct)).Response.ShouldNotBeNull().Error.ShouldBe("There is no card c9");
    }

    [Fact]
    public async Task ADismissClosesTheCardWithWhy()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");

        (await node.Client.SendAsync(Command(Verbs.Dismiss, "card/c1", "known", "flake"), Ct)).Success.ShouldBeTrue();

        node.State.Cards().Single().Outcome.ShouldBe("dismissed: known flake");
    }
}
