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
            foreach (Match m in Regex.Matches(source, @"new CardChoice\b"))
            {
                if (Initializer(source, m.Index + m.Length) is not { } text)
                {
                    continue;
                }
                if (EmptyTarget.IsMatch(text) && !Regex.IsMatch(text, @"Verb\s*=\s*Verbs\.Dismiss\b"))
                {
                    var line = source.Take(m.Index).Count(c => c == '\n') + 1;
                    bad.Add($"{file}:{line}: {Regex.Replace(text, @"\s+", " ")}");
                }
            }
        }
        bad.ShouldBeEmpty("a CardChoice with an empty Target must be a Verbs.Dismiss, which the answer path aims at its own card:\n" + string.Join('\n', bad));
    }

    [Fact]
    public void TheScanReadsAnInitializerToItsMatchingBrace()
    {
        // An interpolation, a brace in a literal or a nested collection before the Target must not end the choice early (review R1.7).
        const string source = """x = new CardChoice { Label = $"Run {name}", Args = [$"{a}", "}"], Verb = Verbs.Run, Target = string.Empty }; y = 1;""";
        var text = Initializer(source, source.IndexOf("new CardChoice", StringComparison.Ordinal) + "new CardChoice".Length);
        text.ShouldNotBeNull();
        text.ShouldEndWith("Target = string.Empty }");
        EmptyTarget.IsMatch(text).ShouldBeTrue();
        EmptyTarget.IsMatch("""{ Target = "" }""").ShouldBeTrue();
        EmptyTarget.IsMatch("{ Target = card.Subject }").ShouldBeFalse();
        Initializer("new CardChoice(a, b);", "new CardChoice".Length).ShouldBeNull("a constructor call has no initializer to read");
    }

    private static readonly Regex EmptyTarget = new(@"\bTarget\s*=\s*(""""|string\.Empty\b)");

    /// <summary>
    /// The object initializer that opens at or after <paramref name="from"/>, read to its matching brace: braces inside string and char
    /// literals do not count, and an interpolation hole is skipped to its own closing brace. Null where a <c>;</c> or <c>(</c> comes
    /// before any <c>{</c> (a constructor call, not an initializer).
    /// </summary>
    internal static string? Initializer(string source, int from)
    {
        var open = from;
        while (open < source.Length && source[open] != '{')
        {
            if (source[open] is ';' or '(')
            {
                return null;
            }
            open++;
        }
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            var c = source[i];
            if (c is '"' or '\'')
            {
                i = LiteralEnd(source, i);
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return source[from..(i + 1)];
            }
        }
        return null;
    }

    /// <summary>The index of the quote closing the literal opened at <paramref name="quote"/>, its interpolation holes skipped whole.</summary>
    private static int LiteralEnd(string source, int quote)
    {
        var q = source[quote];
        var prefix = source[Math.Max(0, quote - 2)..quote];
        var interpolated = prefix.EndsWith('$') || prefix == "$@";
        var verbatim = prefix.EndsWith('@') || prefix == "@$";
        for (var i = quote + 1; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\' && !verbatim)
            {
                i++;
            }
            else if (c == q)
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == q)
                {
                    i++;
                    continue;
                }
                return i;
            }
            else if (interpolated && c == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{')
                {
                    i++;
                    continue;
                }
                var hole = 1;
                while (++i < source.Length && hole > 0)
                {
                    if (source[i] is '"' or '\'')
                    {
                        i = LiteralEnd(source, i);
                    }
                    else
                    {
                        hole += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
                    }
                }
                i--;
            }
        }
        return source.Length;
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
