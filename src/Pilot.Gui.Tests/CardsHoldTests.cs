using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The cards panel holds still while the pointer is over it: a card raised meanwhile would push the card under the pointer
/// down, and the click meant for it would land on another's button (SE1, 2026-10-25). The new card waits as a dot on the
/// summary line, a card closed meanwhile stays where it was with its buttons dead, and the panel catches up once the
/// pointer leaves.
/// </summary>
public sealed class CardsHoldTests
{
    private static CardRow Card(string id, CardBin bin, string title, int minute) => new()
    {
        Id = id, Bin = bin, Kind = "leg-failed", Subject = "flight/TIA1818/web-header", Title = title,
        At = DateTimeOffset.UnixEpoch.AddMinutes(minute), State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static readonly CardRow Older = Card("c1", CardBin.ForYou, "TIA1818 compiles against stale siblings", 1);
    private static readonly CardRow Ringing = Card("c2", CardBin.Ring, "TIA1818 could not start web-header", 2);
    private static readonly CardRow Raised = Card("c3", CardBin.Ring, "PIL222 could not open its PR", 3);

    [Fact]
    public void ACardRaisedUnderThePointerWaitsAsADotAndTheCardsStayPut()
    {
        using var renderer = new RgbaImageRenderer(400, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        view.Tree(view.Steady(CardsScreen.From([Older, Ringing]), over: false));

        var held = view.Steady(CardsScreen.From([Older, Ringing, Raised]), over: true);

        held.Open.Select(c => c.Id).ShouldBe(["c2", "c1"]);
        held.Summary.ShouldBe("2 ringing · 1 for you");
        Tooltips(view, held).ShouldContain("new: PIL222 could not open its PR");

        // The pointer leaves: the new card takes its place, the newest ring first.
        view.Steady(CardsScreen.From([Older, Ringing, Raised]), over: false).Open.Select(c => c.Id).ShouldBe(["c3", "c2", "c1"]);
    }

    [Fact]
    public void ACardClosedUnderThePointerStaysWhereItWasWithItsButtonsDead()
    {
        using var renderer = new RgbaImageRenderer(400, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        view.Tree(view.Steady(CardsScreen.From([Older, Ringing]), over: false));

        var held = view.Steady(CardsScreen.From([Older]), over: true);

        held.Open.Select(c => c.Id).ShouldBe(["c2", "c1"]);
        held.Summary.ShouldBe("1 for you");
        var arranged = Arrange(view, held);
        Texts(arranged).ShouldContain("RING · closed");
        Hits(arranged).ShouldNotContain("card:c2:1");
        Hits(arranged).ShouldNotContain("card:c2:dismiss");
        Hits(arranged).ShouldContain("card:c1:1");

        view.Steady(CardsScreen.From([Older]), over: false).Open.Select(c => c.Id).ShouldBe(["c1"]);
    }

    [Fact]
    public void TheFocusedFlightsCardsAndOnlyTheyArePaintedLit()
    {
        using var renderer = new RgbaImageRenderer(400, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Focus = "TIA1818" };
        var other = Raised with { Subject = "flight/PIL222" };

        var lit = Arrange(view, CardsScreen.From([Older, other])).Count(a => a.Node.Background is { } bg && bg == FocusBg);

        // Older is about TIA1818's leg (flight/TIA1818/web-header): lit; PIL222's card is not.
        lit.ShouldBe(1);
        view.Focus = null;
        Arrange(view, CardsScreen.From([Older, other])).Count(a => a.Node.Background is { } bg && bg == FocusBg).ShouldBe(0);
    }

    [Fact]
    public void APressedButtonSaysItIsWorkingAndTheCardTakesNoSecondPressUntilTheNodeAnswers()
    {
        // A Merge on a merge-queue repo is several GitHub calls: seconds in which the button looked dead (SE1, 2026-10-25).
        using var renderer = new RgbaImageRenderer(400, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = CardsScreen.From([Ringing]);

        view.NoteWorking("c2", "card:c2:1");
        var working = Arrange(view, screen);
        Texts(working).ShouldContain("Start web-header …");
        Hits(working).ShouldNotContain("card:c2:1");
        Hits(working).ShouldNotContain("card:c2:dismiss");

        view.NoteWorking("c2", null);
        Hits(Arrange(view, screen)).ShouldContain("card:c2:1");
    }

    [Fact]
    public void TheSessionsPageFocusesTheFlightOfItsOpenTranscriptElseOfItsSelectedRow()
    {
        using var renderer = new RgbaImageRenderer(1280, 400);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = new SessionsScreen(SessionLine.Build(
        [
            Live.Row("a1", "se1-tia1818-web-header", SessionState.Working) with { Flight = "TIA1818", Role = SessionRole.Worker },
            Live.Row("b2", "pilot-33", SessionState.Idle),
        ], Live.At), "test", Live.At);
        view.Render(screen, new RectF32(0, 0, 1280, 400));

        foreach (var i in Enumerable.Range(0, screen.Lines.Count))
        {
            while (view.Selected != i)
            {
                view.HandleInput(new InputEvent.KeyDown(view.Selected < i ? InputKey.Down : InputKey.Up, InputModifier.None)).ShouldBeTrue();
            }
            view.FocusFlight.ShouldBe(screen.Lines[i].Row.Flight);
        }
    }

    private static readonly RGBAColor32 FocusBg = new(0x26, 0x33, 0x4a, 0xff);

    private static Layout.ArrangedNode<float>[] Arrange(CardsView<RgbaImage> view, CardsScreen screen)
    {
        var renderer = new RgbaImageRenderer(1, 1);
        return [.. Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))];
    }

    private static string[] Tooltips(CardsView<RgbaImage> view, CardsScreen screen) =>
        [.. Arrange(view, screen).Select(a => a.Node.Tooltip).OfType<string>()];

    private static string[] Texts(Layout.ArrangedNode<float>[] arranged) =>
        [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];

    private static string[] Hits(Layout.ArrangedNode<float>[] arranged) =>
        [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(h => h.Action)];
}
