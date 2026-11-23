using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A card choice with no target can only be a Dismiss of its own card (PIL1294): the answer path closes the card by the choice itself.
/// Any other verb written with an empty target would be refused at press time as "this verb takes a target".
/// </summary>
[Trait("Category", "Invariant")]
public sealed class CardChoiceTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void NoCardChoiceHasAnEmptyTargetUnlessItDismissesItsCard()
    {
        var bad = new List<string>();
        foreach (var (file, source) in CardKindTests.Sources.Value)
        {
            foreach (Match m in Regex.Matches(source, @"new CardChoice\b[^;]*?\}", RegexOptions.Singleline))
            {
                var text = m.Value;
                if (Regex.IsMatch(text, "Target\\s*=\\s*\"\"") && !Regex.IsMatch(text, @"Verb\s*=\s*Verbs\.Dismiss\b"))
                {
                    var line = source.Take(m.Index).Count(c => c == '\n') + 1;
                    bad.Add($"{file}:{line}: {Regex.Replace(text, @"\s+", " ")}");
                }
            }
        }
        bad.ShouldBeEmpty("a CardChoice with an empty Target must be a Verbs.Dismiss, which the answer path aims at its own card:\n" + string.Join('\n', bad));
    }

    [Theory]
    [InlineData("")]
    [InlineData("self")]
    public async Task ADismissChoiceAimedAtItsOwnCardClosesItByTheAnswer(string target)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // The card's id is not known before it is raised, so "self" is written afterwards through UpdateChoices.
        var card = node.State.RaiseCard(CardBin.ForYou, "x-kind", Verbs.NodeTarget, "t", "d",
            [new CardChoice { Label = "Mark it read", Verb = Verbs.Dismiss, Target = "" }]);
        if (target == "self")
        {
            node.State.UpdateChoices(card.Id, [new CardChoice { Label = "Mark it read", Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id) }]);
        }
        var commands = node.App.Services.GetRequiredService<NodeCommands>();

        var result = await commands.RunAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Mark it read"], Who = "cli" }, Ct);

        result.Ok.ShouldBeTrue(result.Error);
        node.State.Cards(openOnly: true).ShouldNotContain(c => c.Id == card.Id);
    }
}
