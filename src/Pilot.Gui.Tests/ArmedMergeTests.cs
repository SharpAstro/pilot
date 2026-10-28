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
/// A merge answered while its checks run is armed, and its card says so: a Merge that reads <c>Merging when checks pass · 6 of 7</c>
/// in a tint of its own and is not a button (a second press would only arm it again); a card that was disarmed says which
/// check failed, links to it and offers Go around, Re-run checks and Merge again (PIL244, pilot#244).
/// </summary>
public sealed class ArmedMergeTests
{
    private const string LongCheck = "build-and-test (windows-11-arm, net10.0, Release, ExtendedIntegrationTests+LongRunningSnapshotComparisons)";

    private const string ArmedDetail = "merging when checks pass: 6 of 7 done. GitHub merges it, with the node up or not.";

    private const string NodeWaitsDetail = "merging when checks pass: 6 of 7 done. The node waits for them. "
        + "Switch on GitHub's auto-merge to let GitHub wait instead: gh repo edit --enable-auto-merge, or the repo's Settings > General > Allow auto-merge. GitHub waits only for required checks, so make them required too (a branch protection rule or a ruleset).";

    private static readonly string FailedDetail = $"The merge waited for the checks to pass, and {LongCheck} failed on 77018f1c. "
        + "Redo the leg to fix it, re-run the checks if it was a flake, or merge again. GitHub's auto-merge is turned off.";

    private static CardChoice Merge(string? status, string label = "Merge") =>
        new() { Label = label, Verb = Verbs.Merge, Target = "flight/PIL244", Args = ["77018f1"], Status = status };

    private static CardRow Armed(string detail = ArmedDetail, CardBin bin = CardBin.Ring) => new()
    {
        Id = "c1", Bin = bin, Kind = "pr-ready", Subject = "flight/PIL244", Title = "PIL244's PR #251 merges when its checks pass", Detail = detail,
        Choices = [Merge(ChoiceStatus.Armed)], Link = "https://github.com/SharpAstro/pilot/pull/251", At = Live.At, State = CardState.Open,
    };

    private static CardRow Disarmed() => new()
    {
        Id = "c2", Bin = CardBin.Ring, Kind = "merge-failed", Subject = "flight/PIL244", Title = $"PIL244's PR #251: check {LongCheck} failed", Detail = FailedDetail,
        Choices =
        [
            new CardChoice { Label = "Redo leg armed-in-gui", Verb = Verbs.GoAround, Target = "flight/PIL244/armed-in-gui", Args = ["the PR's checks failed"] },
            new CardChoice { Label = "Re-run checks", Verb = Verbs.RerunChecks, Target = "flight/PIL244", Args = ["77018f1", "rebase"] },
            Merge(null, "Merge again"),
        ],
        Link = "https://github.com/SharpAstro/pilot/actions/runs/1/job/2", At = Live.At, State = CardState.Open,
    };

    // WCAG 2.x relative luminance and contrast ratio.
    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardRow card, float width, out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, width, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static string[] Words(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];

    [Theory]
    [InlineData(ArmedDetail, "Merging when checks pass · 6 of 7")]
    [InlineData(NodeWaitsDetail, "Merging when checks pass · 6 of 7")]
    [InlineData("merging when checks pass: 2 failed runs running again. GitHub merges it, with the node up or not.", "Merging when checks pass")]
    [InlineData(null, "Merging when checks pass")]
    public void AnArmedMergeSaysWhatItDoesAndHowFarItsChecksAre(string? detail, string label)
    {
        CardsView<RgbaImage>.StatusLabel(Merge(ChoiceStatus.Armed), detail).ShouldBe(label);
    }

    [Fact]
    public void AnArmedMergeHasATintOfItsOwn()
    {
        var armed = CardsView<RgbaImage>.StatusTint(ChoiceStatus.Armed).ShouldNotBeNull();
        foreach (var other in new[] { ChoiceStatus.Passing, ChoiceStatus.Pending, ChoiceStatus.Failing })
        {
            armed.Bg.ShouldNotBe(CardsView<RgbaImage>.StatusTint(other)!.Value.Bg);
        }
        CardsView<RgbaImage>.StatusTint(null).ShouldBeNull();
    }

    [Fact]
    public void TheArmedMergesWordsMeetTheContrastFloorOnItsFill()
    {
        // The words on the tint; the card's own colours are what the chip sits on, and the chip's fill is told from them by its words, not by colour alone.
        Contrast(CardsView<RgbaImage>.ArmedText, CardsView<RgbaImage>.ArmedBg).ShouldBeGreaterThanOrEqualTo(4.5);
        var tint = CardsView<RgbaImage>.StatusTint(ChoiceStatus.Armed)!.Value;
        Contrast(CardsView<RgbaImage>.ArmedText, tint.Hover).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void AnArmedMergeIsNotAButtonAndTheCardsOtherButtonsAre()
    {
        var arranged = Arrange(Armed(), 340, out var renderer);
        using var _ = renderer;
        var actions = arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();

        // The Merge is drawn, with its words, and answers nothing; Dismiss and the title link still do.
        Words(arranged).ShouldContain("Merging when checks pass · 6 of 7");
        actions.ShouldNotContain("card:c1:1");
        actions.ShouldContain("card:c1:dismiss");
        actions.ShouldContain("card:c1:title");
    }

    [Fact]
    public void ADisarmedCardOffersGoAroundRerunAndMergeAgainAsButtonsAndLinksToTheFailedCheck()
    {
        var arranged = Arrange(Disarmed(), 340, out var renderer);
        using var _ = renderer;
        var actions = arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();

        // The go-around names a leg, so the card also links it (card:c2:leg).
        actions.ShouldBe(["card:c2:title", "card:c2:leg", "card:c2:1", "card:c2:2", "card:c2:3", "card:c2:dismiss"], ignoreOrder: true);
        Words(arranged).ShouldContain("Merge again");
        // A merge that is not armed any more is a button again: pressing it arms it afresh.
        CardsView<RgbaImage>.StatusLabel(Disarmed().Choices[2]).ShouldBe("Merge again");
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(520f)]
    public void AnArmedOrDisarmedCardsDetailIsNeverCutSilently(float width)
    {
        foreach (var (card, detail) in new[] { (Armed(NodeWaitsDetail), NodeWaitsDetail), (Armed(), ArmedDetail), (Disarmed(), FailedDetail) })
        {
            var arranged = Arrange(card, width, out var renderer);
            using var _ = renderer;
            var sourceWords = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // A long word is broken after its separators into pieces that run on without a space, so the pieces read back as the words.
            var drawn = string.Concat(arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value));

            // Every word of the detail is drawn (wrapped, none dropped by the line cap), and none past the panel's edge.
            foreach (var word in sourceWords)
            {
                drawn.Contains(word, StringComparison.Ordinal).ShouldBeTrue($"'{word}' of {card.Id}'s detail at {width}");
            }
            arranged.Where(a => a.Bounds.X + a.Bounds.Width > width + 1).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(320f)]
    [InlineData(340f)]
    [InlineData(520f)]
    public void EveryButtonOfAWrappedChoiceRowIsInsideItsCard(float width)
    {
        // The disarmed card's Go around, Re-run checks, Merge again and Dismiss wrap onto three lines: the card grows to hold the last.
        var arranged = Arrange(Disarmed(), width, out var renderer);
        using var _ = renderer;
        var card = arranged.Where(a => a.Node.Background is not null && a.Bounds.Width > width / 2 && a.Bounds.Width < width - 1).OrderBy(a => a.Depth).First();
        var buttons = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action.StartsWith("card:c2:", StringComparison.Ordinal) && action != "card:c2:title").ToList();

        // The four choice buttons and the card's leg link.
        buttons.Count.ShouldBe(5);
        buttons.ShouldAllBe(b => b.Bounds.Bottom <= card.Bounds.Bottom + 0.5f, $"card bottom {card.Bounds.Bottom}: {string.Join(", ", buttons.Select(b => b.Bounds.Bottom))}");
    }

    [Fact]
    public void ADetailPastTheMergeCapStillCarriesItsWholeTextOnHover()
    {
        var long_ = string.Join(' ', Enumerable.Range(0, 120).Select(i => $"word{i}"));
        var arranged = Arrange(Armed(long_), 300, out var renderer);
        using var _ = renderer;

        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(long_);
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(340f)]
    public void ADisarmedDetailOfAFewHundredCharactersInTwoParagraphsCarriesItsWholeTextOnHover(float width)
    {
        // Under the old 320-character threshold, yet at a narrow panel's width the first paragraph wraps past the cap and the
        // second gets one line: both are cut by the line cap with no ellipsis, so the whole text is on hover (ui-changes.md section 3).
        var detail = "GitHub blocks the merge: a required check has yet to report on 77018f1c, or a branch rule keeps it from merging, and the PR page says which one it is.\n"
            + "Redo the leg to fix what it found, re-run the checks if it was a flake, or merge again once that is dealt with.";
        detail.Length.ShouldBeInRange(230, 300);
        var arranged = Arrange(Disarmed() with { Detail = detail }, width, out var renderer);
        using var _ = renderer;

        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(detail);
    }

    [Fact]
    public void AShortOneParagraphMergeDetailNeedsNoHover()
    {
        var arranged = Arrange(Armed("merging when checks pass: 6 of 7 done."), 340, out var renderer);
        using var _ = renderer;

        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldNotContain(t => t.StartsWith("merging when", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePanelDoesNotCountAnArmedMergeAsClearedToLand()
    {
        var cards = CardsScreen.From([Armed(bin: CardBin.ForYou)]);
        var home = new HomeScreen(new FlightsScreen([], "t", Live.At), new SessionsScreen([], "t", Live.At), new JobsScreen([], "t", Live.At), cards);

        home.Waiting.ShouldBe("1 for you");
        home.Waiting.ShouldNotContain("cleared");
        // The passing merge beside it is.
        var passing = Armed(bin: CardBin.ForYou) with { Id = "c3", Choices = [Merge(ChoiceStatus.Passing)] };
        new HomeScreen(new FlightsScreen([], "t", Live.At), new SessionsScreen([], "t", Live.At), new JobsScreen([], "t", Live.At), CardsScreen.From([Armed(bin: CardBin.ForYou), passing]))
            .Waiting.ShouldBe("2 for you · 1 PR cleared to land");
    }

    [Fact]
    public void TheFlightsPageShowsTheArmedMergeAsTheCardDoesAndNotAsAButton()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var tint = CardsView<RgbaImage>.ChoiceControl(Merge(ChoiceStatus.Armed), ArmedDetail, "flight:PIL244:merge-card", () => { });
        var arranged = Layout.Engine.Arrange(tint, new Rect<float>(0, 0, 400, 40), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

        arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().ShouldBeEmpty();
        arranged.Single(a => a.Node.Background is not null).Node.Background.ShouldBe(CardsView<RgbaImage>.ArmedBg);
    }

    [Fact]
    public void CapturesOfAnArmedAndADisarmedCardAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var cases = new (string Name, CardRow[] Cards)[]
        {
            ("armed-github", [Armed()]),
            ("armed-node-waits", [Armed(NodeWaitsDetail)]),
            ("disarmed", [Disarmed()]),
            ("both", [Armed(), Disarmed()]),
        };
        foreach (var (width, height) in new[] { (1280, 800), (2000, 900) })
        {
            foreach (var (name, cards) in cases)
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From(cards), new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"armed-{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }
}
