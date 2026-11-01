using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The design card's Answer choice (#567): a choice that asks for words draws a text field beside its button, and the card draws
/// without text over text at narrow and wide windows.
/// </summary>
public sealed class DesignAnswerCardTests
{
    private static CardRow Design() => new()
    {
        Id = "d1", Bin = CardBin.Ring, Kind = "pr-review-design", Subject = "pr/acme/widget#7",
        Title = "acme/widget#7: the review asks a question of design",
        Detail = "README.md:1 design: should this be a service at all?\nNo fixer started: this is the author's to decide. Answer posts what you type on the PR and starts the fixer.",
        Choices = [new CardChoice { Label = "Answer", Verb = Verbs.FixPr, Target = "pr/acme/widget#7", AsksFor = "Your answer" }],
        At = Live.At, State = CardState.Open,
    };

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardRow card, float width, out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, width, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    [Theory]
    [InlineData(320f)]
    [InlineData(420f)]
    [InlineData(900f)]
    public void TheDesignCardDrawsItsAnswerFieldAndButtonWithoutTextOverText(float width)
    {
        var arranged = Arrange(Design(), width, out var renderer);
        using (renderer)
        {
            var words = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()).ToArray();
            words.ShouldContain("Answer");
            arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.TextInput>().ShouldHaveSingleItem();
            TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        }
    }

    private static CardRow WithOptions() => new()
    {
        Id = "d2", Bin = CardBin.Ring, Kind = "pr-review-design", Subject = "pr/acme/widget#7",
        Title = "acme/widget#7: the review asks a question of design: should this be a service at all?",
        Detail = "README.md:1 should this be a service at all?\n- Keep it a library, callers link it directly: smaller surface (recommended)\n- Split it into a service behind HTTP\n- Make it a background worker owned by the host process",
        Choices =
        [
            new CardChoice { Label = "Keep it a library, callers link it directly", Verb = Verbs.FixPr, Target = "pr/acme/widget#7", Args = ["Keep it a library"] },
            new CardChoice { Label = "Split it into a service behind HTTP, with its own deployment and versioning", Verb = Verbs.FixPr, Target = "pr/acme/widget#7", Args = ["Split"] },
            new CardChoice { Label = "Make it a background worker owned by the host process", Verb = Verbs.FixPr, Target = "pr/acme/widget#7", Args = ["Worker"] },
            new CardChoice { Label = "Answer in your own words", Verb = Verbs.FixPr, Target = "pr/acme/widget#7", AsksFor = "Your answer" },
        ],
        At = Live.At, State = CardState.Open,
    };

    [Theory]
    [InlineData(320f)]
    [InlineData(420f)]
    [InlineData(900f)]
    public void TheDesignCardDrawsItsQuestionAndEachOptionWithoutTextOverText(float width)
    {
        var card = WithOptions();
        var arranged = Arrange(card, width, out var renderer);
        using (renderer)
        {
            var text = string.Join('\n', arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value));
            text.ShouldContain("should this be a service at all?");
            foreach (var choice in card.Choices)
            {
                // A long button is cut to what the panel holds, with the whole label as its tooltip; a short one shows whole.
                var drawn = arranged.Select(x => x.Node).OfType<Layout.Node.Leaf>().Any(l => l.Content is Layout.Content.Text t && (t.Value.Trim() == choice.Label || l.Tooltip == choice.Label));
                drawn.ShouldBeTrue(choice.Label);
            }
            arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.TextInput>().ShouldHaveSingleItem();
            TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        }
    }
}
