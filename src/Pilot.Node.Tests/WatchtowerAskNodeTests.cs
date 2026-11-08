using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The watchtower's permission prompts and questions on a real node (PIL855): the verb that answers them is the person's alone, and the card's
/// button reaches it as them.
/// </summary>
public sealed class WatchtowerAskNodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoSessionButThePersonMayAnswerAnAskAndTheCardsButtonReachesTheWatchtowersAnswerAsRunAsThem()
    {
        // The node keeps no watchtower here (a real one would start a real claude), so there is nothing to answer: what is pinned is who may send
        // the verb and that the card's button reaches it with the args the card was raised with. The answer itself is pinned on a fake process
        // (WatchtowerChatTests, StreamSessionTests).
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = node.State.RaiseCard(CardBin.ForYou, Watchtower.AskKind, Watchtower.AskSubject("p1"), "The watchtower asks leave to run: git push origin main (Bash)", null,
            [new CardChoice { Label = "Allow", Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = ["p1", "allow"] }]);

        // Neither the watchtower nor an assistant may answer its own ask: the verb is the person's.
        var tower = node.App.Services.GetRequiredService<Watchtower>();
        foreach (var (who, agent) in new[] { (tower.Name, false), ("some-assistant", true) })
        {
            var refused = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = ["p1", "allow"], Who = who, Agent = agent }, Ct);
            refused.Response.ShouldNotBeNull().Ok.ShouldBeFalse();
            refused.Response.Error.ShouldNotBeNull().ShouldContain("the person's");
        }

        // The person's own command reaches the watchtower, which says there is nothing running to answer.
        var chat = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = ["p1", "deny", "no"], Who = "gui" }, Ct);
        chat.Response.ShouldNotBeNull().Ok.ShouldBeFalse();
        chat.Response.Error.ShouldNotBeNull().ShouldContain("no running process");

        // So does the card's button, run as the person; refused, the card is open again for them.
        var pressed = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Allow"], Who = "gui" }, Ct);
        pressed.Response.ShouldNotBeNull().Ok.ShouldBeFalse();
        pressed.Response.Error.ShouldNotBeNull().ShouldContain("no running process");
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Open);

        // A wrong target or words that answer nothing are refused with what the verb takes.
        var wrong = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.AnswerAsk, Target = Verbs.NodeTarget, Args = ["p1", "allow"], Who = "gui" }, Ct);
        wrong.Response.ShouldNotBeNull().Ok.ShouldBeFalse();
        var words = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.AnswerAsk, Target = Verbs.WatchtowerTarget, Args = ["p1", "maybe"], Who = "gui" }, Ct);
        words.Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain("allow, deny or answer");
    }
}
