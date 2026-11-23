using Pilot.Contracts;
using Microsoft.Extensions.DependencyInjection;
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

    private static async Task RaiseAbout(NodeHarness node, string title, string target = "flight/TIA1")
    {
        var response = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = target, Args = ["ring", title], Who = "cli" }, Ct)).Response.ShouldNotBeNull();
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
        await RaiseAbout(node, $"Press {a.Id} which is closed already", "flight/TIA2");

        var rings = node.State.Cards().Where(c => c.Kind == "watchtower").ToList();
        rings.Count.ShouldBe(2);
        rings.ShouldAllBe(c => c.AboutCard == null);
    }

    private static async Task<string?> RingCard(NodeHarness node, string id, string bin, string title, string? detail = null)
    {
        string[] args = detail is null ? [bin, title] : [bin, title, detail];
        return (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = Verbs.CardTarget(id), Args = args, Who = "wt" }, Ct)).Response.ShouldNotBeNull().Error;
    }

    [Fact]
    public async Task RingingACardByItsIdPutsTheWordsOnItMovesItToRingAndRaisesNoOther()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = node.State.RaiseCard(CardBin.ForYou, "pr-ready", "flight/TIA1", "TIA1 needs a round", null,
            [new CardChoice { Label = "Allow one more round", Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["handled", "round allowed"] }]);

        var made = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = Verbs.CardTarget(a.Id), Args = ["ring", "Please press Allow", "It has waited a day."], Who = "wt" }, Ct)).Response.ShouldNotBeNull();
        made.Error.ShouldBeNull();
        made.Made.ShouldBe(a.Id);

        var now = node.State.Cards().Single();
        now.Id.ShouldBe(a.Id);
        now.Bin.ShouldBe(CardBin.Ring);
        now.RungAgainAt.ShouldNotBeNull();
        now.Detail.ShouldNotBeNull().ShouldContain("From wt: Please press Allow. It has waited a day.");
        Open(node).Select(c => c.Id).ShouldBe([a.Id]);

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(a.Id), Args = ["Allow one more round"], Who = "cli" }, Ct))
            .Response.ShouldNotBeNull().Error.ShouldBeNull();
        Open(node).ShouldBeEmpty();
    }

    [Fact]
    public async Task ARingCardRungAgainKeepsItsIdAndAForYouRaiseLeavesItsBin()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ring = RaiseAsk(node);
        var quiet = node.State.RaiseCard(CardBin.ForYou, "pr-ready", "flight/TIA2", "TIA2 is parked", null);

        (await RingCard(node, ring.Id, "ring", "Still waiting")).ShouldBeNull();
        (await RingCard(node, quiet.Id, "for-you", "A note for you")).ShouldBeNull();

        node.State.Cards().Count().ShouldBe(2);
        node.State.Cards().Single(c => c.Id == ring.Id).RungAgainAt.ShouldNotBeNull();
        var kept = node.State.Cards().Single(c => c.Id == quiet.Id);
        kept.Bin.ShouldBe(CardBin.ForYou);
        kept.Detail.ShouldNotBeNull().ShouldContain("From wt: A note for you.");
    }

    [Fact]
    public async Task RingingAClosedOrMissingCardIsRefusedAndAHandledRaiseStaysALine()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = RaiseAsk(node);
        node.State.CloseCard(a.Id, "dismissed", "cli");

        (await RingCard(node, a.Id, "ring", "Too late")).ShouldNotBeNull().ShouldContain("dismissed");
        (await RingCard(node, "c999999", "ring", "Nobody")).ShouldNotBeNull().ShouldContain("no card");
        Open(node).ShouldBeEmpty();

        (await RingCard(node, a.Id, "handled", "A line about it")).ShouldBeNull();
        node.State.Cards().Count(c => c.Bin == CardBin.Handled && c.Subject == Verbs.CardTarget(a.Id)).ShouldBe(1);
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

    [Fact]
    public async Task ReplacingTheCardARingIsAboutHandsOutNoIdTwice()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var failed = node.State.RaiseCard(CardBin.Ring, "merge-failed", "flight/X", "X could not merge", null);
        await RaiseAbout(node, $"Press Rerun checks on {failed.Id}", "flight/X");
        var ring = node.State.Cards().Single(c => c.Kind == "watchtower");
        ring.AboutCard.ShouldBe(failed.Id);

        var ready = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/X", "X is ready", null);

        var all = node.State.Cards().ToList();
        all.Select(c => c.Id).Distinct().Count().ShouldBe(all.Count);
        node.State.Cards().Single(c => c.Id == ready.Id).Kind.ShouldBe("pr-ready");
        node.State.Cards().Single(c => c.Id == failed.Id).Outcome.ShouldBe($"replaced by {ready.Id}");
        node.State.Cards().Single(c => c.Id == ring.Id).Outcome.ShouldNotBeNull().ShouldStartWith($"{failed.Id} closed: ");
        all.Any(c => c.Bin == CardBin.Handled && c.Title.Contains(ring.Id)).ShouldBeTrue();
        Open(node).Select(c => c.Id).ShouldBe([ready.Id]);
    }

    [Fact]
    public async Task TheNodeClosingACardClosesTheRingAboutIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = RaiseAsk(node);
        await RaiseAbout(node, $"Press {a.Id}", "flight/TIA2");

        node.State.CloseCardsAbout("flight/TIA1", "landed");

        Open(node).ShouldBeEmpty();
        node.State.Cards().Single(c => c.Kind == "watchtower").Outcome.ShouldNotBeNull().ShouldStartWith($"{a.Id} closed: landed");
    }

    [Fact]
    public async Task ARingOnTheSameSubjectKeepsItsLinkOutcomeWhenTheNodeClosesBoth()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = RaiseAsk(node);
        await RaiseAbout(node, $"Press {a.Id}");

        node.State.CloseCardsAbout("flight/TIA1", "landed");

        node.State.Cards().Single(c => c.Kind == "watchtower").Outcome.ShouldNotBeNull().ShouldStartWith($"{a.Id} closed: ");
    }

    [Fact]
    public async Task AnAttachmentOnARingAboutACardIsRefusedInWords()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = node.State.RaiseCard(CardBin.ForYou, "pr-ready", "flight/TIA1", "TIA1 needs a round", "The node's words.");
        // An image the node holds, so the attachment passes KeepCardImage and reaches the guard on a ring about an existing card.
        var image = node.App.Services.GetRequiredService<Attachments>().AddIdeaImage([1, 2, 3], "png");

        var response = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = Verbs.CardTarget(a.Id), Args = ["ring", "Look"], Who = "wt", Attachment = $"{image}.png" }, Ct)).Response.ShouldNotBeNull();

        response.Error.ShouldNotBeNull().ShouldContain("existing card");
        var kept = node.State.Cards().Single();
        kept.Detail.ShouldBe("The node's words.");
        kept.Bin.ShouldBe(CardBin.ForYou);
        kept.RungAgainAt.ShouldBeNull();
    }

    [Fact]
    public async Task ARefusedAnswerOpensTheCardAndTheRingItClosedAgain()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // A choice the node refuses: raise takes ring, for-you or handled.
        var a = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1 needs a round", null,
            [new CardChoice { Label = "Refused", Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["nope", "x"] }]);
        await RaiseAbout(node, $"Press Refused on {a.Id}", "flight/TIA2");
        var ring = node.State.Cards().Single(c => c.Kind == "watchtower");

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(a.Id), Args = ["Refused"], Who = "cli" }, Ct))
            .Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain("was refused");

        Open(node).Select(c => c.Id).Order().ShouldBe(new[] { a.Id, ring.Id }.Order());
        node.State.Cards().Single(c => c.Id == ring.Id).Outcome.ShouldBeNull();
        node.State.Cards().Any(c => c.Bin == CardBin.Handled && c.Title == $"{ring.Id} open again with {a.Id}").ShouldBeTrue();
    }

    [Fact]
    public async Task TwoRingsFromOneSenderLeaveOneParagraphAndANodeRewriteKeepsIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var a = node.State.RaiseCard(CardBin.ForYou, "pr-ready", "flight/TIA1", "TIA1 needs a round", "The node's words.");

        (await RingCard(node, a.Id, "ring", "First nudge")).ShouldBeNull();
        (await RingCard(node, a.Id, "ring", "Second nudge", new string('x', 2000))).ShouldBeNull();

        var detail = node.State.Cards().Single().Detail.ShouldNotBeNull();
        detail.ShouldStartWith("The node's words.");
        detail.ShouldNotContain("First nudge");
        detail.ShouldContain("From wt: Second nudge.");
        detail.Length.ShouldBeLessThan("The node's words.".Length + NodeState.RungWordsCap + 4);

        // The node rewrites the card's detail from its own words: the ring's paragraph stays.
        var card = node.State.Cards().Single();
        node.State.UpdateCard(a.Id, card.Title, "The node's new words.", card.Choices, card.Link).ShouldBeTrue();
        var rewritten = node.State.Cards().Single().Detail.ShouldNotBeNull();
        rewritten.ShouldStartWith("The node's new words.");
        rewritten.ShouldContain("From wt: Second nudge.");
    }

    [Fact]
    public async Task ARingAboutACardStillClosesWithItAfterTheNodeRestarts()
    {
        var first = await NodeHarness.StartAsync(Ct);
        var folder = first.Folder;
        string aId, ringId;
        try
        {
            aId = RaiseAsk(first).Id;
            await RaiseAbout(first, $"Press {aId}", "flight/TIA2");
            ringId = first.State.Cards().Single(c => c.Kind == "watchtower").Id;
        }
        finally
        {
            await first.StopKeepingFolderAsync();
        }

        // Few deltas, so no compaction ran: the card comes back by replaying the journal, which the line below proves.
        File.ReadAllText(Path.Combine(folder, "data", "journal.jsonl")).ShouldContain($"\"aboutCard\":\"{aId}\"");
        await using var node = await NodeHarness.StartAsync(Ct, folder: folder);
        node.State.Cards().Single(c => c.Id == ringId).AboutCard.ShouldBe(aId);

        node.State.CloseCard(aId, "dismissed", "cli");

        Open(node).ShouldBeEmpty();
        node.State.Cards().Single(c => c.Id == ringId).Outcome.ShouldBe($"{aId} closed: dismissed");
    }
}
