using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The For you panel's Clean up (#706): one press tells the watchtower the standard message once, says it, and says a refusal in words.</summary>
public sealed class CleanUpTests
{
    private const string Hit = "cards:cleanup";
    private const string Refusal = "this node keeps no watchtower (--no-watchtower), so there is nobody to tidy up";

    private static SessionRow Tower() => new()
    {
        Id = "bbbb0002", SessionId = "bbbb0002-0000-4000-8000-000000000000", Name = "se1-watchtower", Kind = SessionKind.Background, Cwd = "/w",
        Role = SessionRole.Watchtower, State = SessionState.Idle, ContextTokens = 50_000, ContextWindow = 200_000, SpendWeighted = 0, StartedAt = Live.At,
    };

    private static CardsScreen Screen() => CardsScreen.From(
        [new CardRow { Id = "c1", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/X", Title = "card c1", At = Live.At, State = CardState.Open }], [Tower()]);

    private static CardsView<RgbaImage> NewView(RgbaImageRenderer renderer, out PageButtonFeedbackTests.LateNode node, List<string>? sent = null)
    {
        var late = node = new PageButtonFeedbackTests.LateNode();
        return new CardsView<RgbaImage>(renderer)
        {
            FontPath = SessionsCapture.FontPath,
            CanTell = true,
            OnCleanUp = () =>
            {
                sent?.Add("sent");
                return late.Ask(WatchtowerAsks.CleanUp);
            },
        };
    }

    [Fact]
    public async Task OnePressSendsExactlyTheConstantOnceAndASecondWhilePendingSendsNothing()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = NewView(renderer, out var node);
        var screen = Screen();

        PageButtonFeedbackTests.Click(view.Tree(screen), Hit);
        node.Asked.ShouldBe([WatchtowerAsks.CleanUp]);
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldContain("Sent to the watchtower");
        PageButtonFeedbackTests.Has(view.Tree(screen), Hit).ShouldBeFalse();
        view.CleanUpPending.ShouldBeTrue();

        node.Answer(null);
        await PageButtonFeedbackTests.Until(() => !view.CleanUpPending);
        view.CleanUpRefused.ShouldBeNull();
        node.Asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ARefusalIsShownInWordsOnThePanelAndTheButtonIsBackToPress()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = NewView(renderer, out var node);
        var screen = Screen();

        PageButtonFeedbackTests.Click(view.Tree(screen), Hit);
        node.Answer(Refusal);
        await PageButtonFeedbackTests.Until(() => !view.CleanUpPending);

        view.CleanUpRefused.ShouldBe(Refusal);
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldContain("nobody to tidy up");
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("pending")]
    [InlineData("refused")]
    public async Task TheFootHasNoTextOverTextAt240PxInEachState(string state)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = NewView(renderer, out var node);
        var screen = Screen();
        if (state != "idle")
        {
            PageButtonFeedbackTests.Click(view.Tree(screen), Hit);
        }
        if (state == "refused")
        {
            node.Answer(Refusal);
            await PageButtonFeedbackTests.Until(() => !view.CleanUpPending);
        }

        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 240, 600), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        foreach (var a in arranged)
        {
            (a.Bounds.X + a.Bounds.Width).ShouldBeLessThanOrEqualTo(240.5f);
        }
    }
}
