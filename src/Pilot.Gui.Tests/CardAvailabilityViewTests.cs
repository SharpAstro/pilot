using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A card whose choice the node would refuse right now (#792 point 4: its flight is held, its session is gone) draws that choice dead,
/// with the reason beside it, before anyone presses it; the card's other choices stay as they were. CPU-rendered, as the other card
/// tests are, with the text-over-text check on each page drawn. A live window is the controller's to look at.
/// </summary>
public sealed class CardAvailabilityViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 9, 41, 9, TimeSpan.Zero);

    private const string HeldReason = "TIA1 is held: waiting for TIA0";

    // A short label (beside its reason), and the go-around the card also offers, which nothing refuses.
    private static CardRow Card(string label = "Merge", string? note = HeldReason, string status = ChoiceStatus.Unavailable) => new()
    {
        Id = "c9", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/TIA1", Title = "TIA1's PR #7 is ready to merge", At = Now, State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = label, Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["abc1234"], Status = status, Note = note },
            new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = "flight/TIA1" },
        ],
    };

    private static ImmutableArray<Layout.ArrangedNode<float>> Render(CardRow card, float width = 340f)
    {
        var renderer = new RgbaImageRenderer(1, 1);
        using var _ = renderer;
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From([card], null, Now)), new Rect<float>(0, 0, width, 1400),
            new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static string[] Actions(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action)];

    private static Layout.ArrangedNode<float> Leaf(ImmutableArray<Layout.ArrangedNode<float>> arranged, Func<string, bool> text) =>
        arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && text(t.Value));

    [Theory]
    [InlineData(340f)]
    [InlineData(520f)]
    public void AnUnavailableChoiceIsDeadAndItsReasonIsBesideIt(float width)
    {
        var arranged = Render(Card(), width);

        // Dead: nothing of it answers a click. The card's other choice and Dismiss still do.
        var actions = Actions(arranged);
        actions.ShouldNotContain("card:c9:1");
        actions.ShouldContain("card:c9:2");
        actions.ShouldContain("card:c9:dismiss");
        // The reason is on the card, in the words the node refuses with.
        ArrangedText.Concat(arranged).ShouldContain(HeldReason);
        // Beside the button, on its row: the first word of the reason starts to the right of the label, level with it.
        var label = Leaf(arranged, t => t.Trim() == "Merge");
        var reason = Leaf(arranged, t => t.StartsWith("TIA1 ", StringComparison.Ordinal) && t != "TIA1's PR #7 is ready to merge");
        (reason.Bounds.X > label.Bounds.X + label.Bounds.Width).ShouldBeTrue($"the reason ({reason.Bounds.X}) starts after the label ends ({label.Bounds.X + label.Bounds.Width})");
        Math.Abs((reason.Bounds.Y + reason.Bounds.Height / 2) - (label.Bounds.Y + label.Bounds.Height / 2)).ShouldBeLessThan(label.Bounds.Height, "on the label's row");
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width}");
    }

    [Fact]
    public void ACardWhoseChoicesAreAllAvailableDrawsThemAsBefore()
    {
        var arranged = Render(Card(note: null, status: ChoiceStatus.Passing));

        var actions = Actions(arranged);
        actions.ShouldContain("card:c9:1");
        actions.ShouldContain("card:c9:2");
        ArrangedText.Concat(arranged).ShouldNotContain("is held");
    }

    [Theory]
    [InlineData(340f)]
    [InlineData(520f)]
    public void ALongLabelTakesItsRowAndItsReasonSitsUnderItAndStaysDead(float width)
    {
        const string label = "Resolve the conflict (round 3, on Opus at xhigh)";
        var arranged = Render(Card(label), width);

        Actions(arranged).ShouldNotContain("card:c9:1");
        ArrangedText.Concat(arranged).ShouldContain(label);
        ArrangedText.Concat(arranged).ShouldContain(HeldReason);
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width}");
    }

    [Fact]
    public void ASessionThatIsGoneIsSaidTheSameWay()
    {
        var arranged = Render(Card("Resume", "session a1b2c3d4 no longer exists"));

        Actions(arranged).ShouldNotContain("card:c9:1");
        ArrangedText.Concat(arranged).ShouldContain("session a1b2c3d4 no longer exists");
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void AnUnavailableChoiceWithNoNoteIsStillDead()
    {
        var arranged = Render(Card(note: null));

        var actions = Actions(arranged);
        actions.ShouldNotContain("card:c9:1");
        actions.ShouldContain("card:c9:2");
        ArrangedText.Runs(arranged).Select(r => r.Trim()).ShouldContain("Merge");
    }
}
