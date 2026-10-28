using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A card's chat (#400): <c>tell card/&lt;id&gt;</c> says the card's whole context to the watchtower, as the person's turn.</summary>
public sealed class CardChatTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Lookout(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static CommandRequest Tell(string target, string who, params string[] words) =>
        new() { Verb = Verbs.Tell, Target = target, Args = words, Who = who };

    private static CardRow Raise(NodeHarness node) =>
        node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready to merge", "Checks pass.\nOne review comment is open.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["cafe0000"] }, new CardChoice { Label = "Leave it", Verb = Verbs.Dismiss, Target = "card/c1" }],
            "https://github.com/SharpAstro/tianwen/pull/63");

    private static void Fly(NodeHarness node)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/TIA1", Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Title = "Sky map", PullRequestUrl = "https://github.com/SharpAstro/tianwen/pull/63",
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = "w" });
    }

    private static async Task<SessionRow> Watchtower(NodeHarness node)
    {
        var name = Lookout(node);
        node.Agents.Json = AgentRows.List(AgentRows.Interactive(AgentRows.SessionA, 4242, name, status: "busy"));
        for (var i = 0; i < 100 && !node.State.Sessions().Any(s => s.Name == name); i++)
        {
            await Task.Delay(50, Ct);
        }
        return node.State.Sessions().Single(s => s.Name == name);
    }

    [Fact]
    public async Task TheTellCarriesTheCardsWholeContextAndTheWordsAsThePersonsTurn()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await Watchtower(node);
        Fly(node);
        var card = Raise(node);

        var told = (await node.Client.SendAsync(Tell(Verbs.CardTarget(card.Id), "gui", "should", "I", "merge", "this?"), Ct)).Response.ShouldNotBeNull();

        told.Error.ShouldBeNull();
        var text = node.Delivery.Relays.ShouldHaveSingleItem().Text;
        text.ShouldContain($"[pilot card {card.Id}]");
        text.ShouldContain($"node: {node.Options.Machine}");
        text.ShouldContain("kind: pr-ready");
        text.ShouldContain("subject: flight/TIA1");
        text.ShouldContain("title: TIA1's PR #63 is ready to merge");
        text.ShouldContain("  Checks pass.");
        text.ShouldContain("  One review comment is open.");
        text.ShouldContain("1. Merge: merge flight/TIA1 cafe0000");
        text.ShouldContain("2. Leave it: dismiss card/c1");
        text.ShouldContain("link: https://github.com/SharpAstro/tianwen/pull/63");
        text.ShouldContain("TIA1: Sky map (tianwen");
        text.ShouldContain("1. parser:");
        text.ShouldEndWith("should I merge this?");
        text.Any(c => c != '\n' && c != '\r' && (c < ' ' || c > '~')).ShouldBeFalse();
        // The person's turn: what the watchtower then asks for in their name is an assistant's.
        node.App.Services.GetService(typeof(Watchtower)).ShouldBeOfType<Watchtower>().ActingForPerson.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnknownOrClosedCardIsRefusedWithAReason()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await Watchtower(node);
        var card = Raise(node);

        (await node.Client.SendAsync(Tell("card/c99", "gui", "hello"), Ct)).Response.ShouldNotBeNull().Error.ShouldBe("There is no card c99");
        (await node.Client.SendAsync(Tell(Verbs.CardTarget(card.Id), "gui"), Ct)).Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain("words");

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id), Who = "gui" }, Ct)).Response.ShouldNotBeNull().Error.ShouldBeNull();
        (await node.Client.SendAsync(Tell(Verbs.CardTarget(card.Id), "gui", "hello"), Ct)).Response.ShouldNotBeNull().Error.ShouldBe($"Card {card.Id} is closed");
        node.Delivery.Relays.ShouldBeEmpty();
    }

    [Fact]
    public void TheParseGivesBackTheCardsIdAndTitleAQuoteAndTheWords()
    {
        var card = new CardRow
        {
            Id = "c7", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/TIA1/parser", Title = "parser failed", Detail = new string('x', 300), At = DateTimeOffset.UtcNow, State = CardState.Open,
            Choices = [new CardChoice { Label = "Retry", Verb = Verbs.Answer, Target = "card/c7", Args = ["Retry"] }],
        };

        var message = CardContext.Compose(card, "DU1", null, "why did it fail?\nand when?");

        CardContext.TryParse(message, out var chat).ShouldBeTrue();
        chat.CardId.ShouldBe("c7");
        chat.Title.ShouldBe("parser failed");
        chat.Quote.Length.ShouldBeLessThanOrEqualTo(120);
        chat.Quote.ShouldStartWith("xxx");
        chat.Words.ShouldBe("why did it fail?\nand when?");
        CardContext.TryParse("just a plain message", out _).ShouldBeFalse();
    }
}
