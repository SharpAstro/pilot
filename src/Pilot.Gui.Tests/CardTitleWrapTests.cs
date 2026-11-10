using System.Collections.Immutable;
using System.Diagnostics;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The For you panel wraps a card's title (up to three lines) and the label of a choice button
/// (up to two lines inside the button) instead of cutting either to one line at a fixed number of characters (pilot#677).
/// </summary>
public sealed class CardTitleWrapTests
{
    private static CardChoice Choice(string label, string verb = Verbs.GoAround, string target = "flight/PIL670/emit-and-show") =>
        new() { Label = label, Verb = verb, Target = target };

    /// <summary>The four cards of the SE1 screenshot of 2026-11-03 in the issue.</summary>
    internal static CardRow[] IssueCards() =>
    [
        new()
        {
            Id = "c1", Bin = CardBin.Ring, Kind = "session-stopped", Subject = "flight/COD54",
            Title = "COD54: a session stopped with 3 uncommitted files",
            Summary = "The worker stopped with changes in the plane that are not committed. Commit them or drop them.",
            Choices = [Choice("Tell the session to commit them", Verbs.GoAround, "flight/COD54/parser"), Choice("Drop the changes and redo the leg", Verbs.GoAround, "flight/COD54/parser")],
            At = Live.At, State = CardState.Open,
        },
        new()
        {
            Id = "c2", Bin = CardBin.Ring, Kind = "landing-refused", Subject = "flight/COD54",
            Title = "COD54's PR #59 merged on GitHub but the node refused to land it",
            Summary = "GitHub merged the PR, but the plane holds files that are not committed, so the node did not land the flight.",
            Choices = [Choice("Land the flight without them", Verbs.Land, "flight/COD54"), Choice("Dismiss this and land it later", Verbs.Land, "flight/COD54")],
            At = Live.At, State = CardState.Open,
        },
        new()
        {
            Id = "c3", Bin = CardBin.ForYou, Kind = "merge-failed", Subject = "flight/PIL670",
            Title = "PIL670's merge did not go through: the checks are red",
            Summary = "The merge was refused because a required check failed on the head commit.",
            Choices = [Choice("Start a fresh worker on emit-and-show"), Choice("Merge PIL670's PR again as it is", Verbs.Merge, "flight/PIL670")],
            At = Live.At, State = CardState.Open,
        },
        new()
        {
            Id = "c4", Bin = CardBin.ForYou, Kind = "review-passed", Subject = "flight/COD54",
            Title = "COD54's review passed with notes on the parser and the writer",
            Summary = "The reviewer passed the PR and left two notes you may want to act on first.",
            Choices = [Choice("Merge", Verbs.Merge, "flight/COD54"), Choice("Redo the parser with the notes", Verbs.GoAround, "flight/COD54/parser")],
            At = Live.At, State = CardState.Open,
        },
    ];

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardRow[] cards, float width, out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From(cards)), new Rect<float>(0, 0, width, 6000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static IEnumerable<Layout.ArrangedNode<float>> TextOf(ImmutableArray<Layout.ArrangedNode<float>> arranged, float size) =>
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.FontSize == size);

    private static List<Layout.ArrangedNode<float>> Words(ImmutableArray<Layout.ArrangedNode<float>> arranged, string action) =>
        [.. arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action)];

    private static int Rows(IEnumerable<Layout.ArrangedNode<float>> nodes) => nodes.Select(n => n.Bounds.Y).Distinct().Count();

    [Theory]
    [InlineData(240f)]
    [InlineData(300f)]
    public void ALongPlainTitleWrapsOverSeveralRowsInsteadOfBeingCutToOne(float width)
    {
        var arranged = Arrange(IssueCards(), width, out var renderer);
        using var _ = renderer;

        // 14pt is the title's size: the four titles together take more rows than the four cards have, each whole.
        var titles = TextOf(arranged, 14f).ToList();
        Rows(titles).ShouldBeGreaterThan(IssueCards().Length);
        var text = ArrangedText.Concat(arranged);
        foreach (var card in IssueCards())
        {
            text.ShouldContain(card.Title);
        }
        text.ShouldNotContain("…");
        // No title takes more rows than the cap.
        foreach (var card in IssueCards())
        {
            var first = card.Title.Split(' ')[0];
            var start = titles.First(t => t.Node is Layout.Node.Leaf { Content: Layout.Content.Text x } && x.Value.StartsWith(first, StringComparison.Ordinal));
            titles.Where(t => t.Bounds.Y >= start.Bounds.Y && t.Bounds.Y < start.Bounds.Y + CardsView<RgbaImage>.TitleLines * 22f).Select(t => t.Bounds.Y).Distinct().Count()
                .ShouldBeLessThanOrEqualTo(CardsView<RgbaImage>.TitleLines);
        }
    }

    [Fact]
    public void ATitleLongerThanItsThreeLinesIsCutAtThemAndCarriesTheWholeOnHover()
    {
        var title = string.Join(' ', Enumerable.Repeat("a session stopped with uncommitted files", 8));
        var card = IssueCards()[0] with { Title = title, Summary = "short" };
        var arranged = Arrange([card], CardsView<RgbaImage>.MinWidth, out var renderer);
        using var _ = renderer;

        Rows(TextOf(arranged, 14f)).ShouldBe(CardsView<RgbaImage>.TitleLines);
        arranged.Select(a => a.Node.Tooltip).ShouldContain(title);
    }

    [Theory]
    [InlineData(CardsView<RgbaImage>.MinWidth)]
    [InlineData(CardsView<RgbaImage>.Width)]
    public void ATitleCutByItsCapEndsItsLastLineInAnEllipsis(float width)
    {
        // #923: the third line ended with no mark, so a capture (which has no hover) showed the title cut silently.
        var title = string.Join(' ', Enumerable.Repeat("a session stopped with uncommitted files in its plane", 12));
        var card = IssueCards()[0] with { Title = title, Summary = "short" };
        var arranged = Arrange([card], width, out var renderer);
        using var _ = renderer;

        var lines = TextOf(arranged, 14f).GroupBy(n => n.Bounds.Y).OrderBy(g => g.Key).ToList();
        lines.Count.ShouldBe(CardsView<RgbaImage>.TitleLines);
        var last = string.Concat(lines[^1].OrderBy(n => n.Bounds.X).Select(n => n.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : "")).TrimEnd();
        last.ShouldEndWith("…");
    }

    [Fact]
    public void ALinkTitleWrapsWithEveryWordTheLinkAndTheArrowOnTheLastWord()
    {
        var card = IssueCards()[1] with { Link = "https://github.com/SharpAstro/pilot/actions/runs/1" };
        var arranged = Arrange([card], CardsView<RgbaImage>.MinWidth, out var renderer);
        using var _ = renderer;

        var words = Words(arranged, "card:c2:title");
        Rows(words).ShouldBeInRange(2, CardsView<RgbaImage>.TitleLines);
        var texts = words.Select(w => ((Layout.Content.Text)((Layout.Node.Leaf)w.Node).Content).Value).ToList();
        string.Concat(texts).ShouldBe(card.Title + Followable.Away + " ");
        texts.Count(t => t.Contains('↗')).ShouldBe(1);
        texts[^1].ShouldContain("↗");
        words.ShouldAllBe(w => w.Bounds.X + w.Bounds.Width <= CardsView<RgbaImage>.MinWidth + 0.5f);
    }

    [Theory]
    [InlineData(240f)]
    [InlineData(300f)]
    public void ALongChoiceLabelIsNotCutAt28CharactersAndWrapsInsideItsButton(float width)
    {
        var arranged = Arrange(IssueCards(), width, out var renderer);
        using var _ = renderer;
        var text = ArrangedText.Concat(arranged);

        text.ShouldContain("Start a fresh worker on emit-and-show");
        text.ShouldContain("Merge PIL670's PR again as it is");
        text.ShouldContain("Drop the changes and redo the leg");
        // A button with a long label is one pill the width of the panel, its words inside it, whole; no label is cut with an ellipsis.
        var button = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "card:c3:1" });
        var inside = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.FontSize == 12f
            && a.Bounds.Y >= button.Bounds.Y && a.Bounds.Y + a.Bounds.Height <= button.Bounds.Y + button.Bounds.Height + 0.5f
            && a.Bounds.X >= button.Bounds.X && a.Bounds.X + a.Bounds.Width <= button.Bounds.X + button.Bounds.Width + 0.5f).ToList();
        string.Concat(inside.Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value)).ShouldBe("Start a fresh worker on emit-and-show ");
        if (width == CardsView<RgbaImage>.MinWidth)
        {
            Rows(inside).ShouldBe(2, "two lines at the narrowest panel");
        }
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void ALabelPastTwoLinesIsCutAtThemAndCarriesTheWholeOnHover()
    {
        var label = "Start a fresh worker on emit-and-show, then merge the pull request again as it is, and tell the planner about it";
        var card = IssueCards()[2] with { Choices = [Choice(label)] };
        var arranged = Arrange([card], CardsView<RgbaImage>.MinWidth, out var renderer);
        using var _ = renderer;

        var button = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "card:c3:1" });
        button.Node.Tooltip.ShouldBe(label);
        var inside = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.FontSize == 12f && a.Bounds.Y >= button.Bounds.Y
            && a.Bounds.Y + a.Bounds.Height <= button.Bounds.Y + button.Bounds.Height + 0.5f);
        Rows(inside).ShouldBeLessThanOrEqualTo(CardsView<RgbaImage>.ButtonLines);
    }

    [Theory]
    [InlineData("Start a fresh worker on emit-and-show")]
    [InlineData("Merge PIL670's PR again as it is, whatever the checks say")]
    [InlineData("Go")]
    [InlineData("Merging when checks pass · 6 of 7")]
    public void ADeadAndAWorkingButtonAreTheSizeOfTheLiveOneSoNothingMovesOnAPress(string label)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        Layout.ArrangedNode<float> Root(Layout.Node node) =>
            Layout.Engine.Arrange(node, new Rect<float>(0, 0, 217f, 400f), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))[0];

        var live = Root(CardsView<RgbaImage>.ChoicePill(label, false, "x", () => { }, null, Palette.Current.Bright, wrap: true));
        var pressed = Root(CardsView<RgbaImage>.ChoicePill(label, true, null, null, null, Palette.Current.Dim, wrap: true));
        var dead = Root(CardsView<RgbaImage>.ChoicePill(label, false, null, null, null, Palette.Current.Dim, wrap: true));

        dead.Bounds.Height.ShouldBe(live.Bounds.Height, 0.5f);
        pressed.Bounds.Height.ShouldBe(live.Bounds.Height, 0.5f);
        if (CardsView<RgbaImage>.Wraps(label))
        {
            // A wrapped one is the panel's width live and dead alike; a short one grows by its "…", as it always did.
            dead.Bounds.Width.ShouldBe(live.Bounds.Width, 0.5f);
            pressed.Bounds.Width.ShouldBe(live.Bounds.Width, 0.5f);
        }
    }

    [Fact]
    public void ChatAboutThisAndDismissShareARowAndEveryButtonOnItHasOneHeight()
    {
        var card = IssueCards()[3];
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, OnChatAbout = _ => { } };
        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, CardsView<RgbaImage>.MinWidth, 3000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

        var chat = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "card:c4:chat" }).Bounds;
        var dismiss = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "card:c4:dismiss" }).Bounds;
        var merge = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "card:c4:1" }).Bounds;
        chat.Y.ShouldBe(dismiss.Y, 0.5f);
        chat.Height.ShouldBe(dismiss.Height, 0.5f);
        // The short Merge is a row of its own ahead of the long choice, which has the next row; one height for every short button.
        var tall = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "card:c4:2" }).Bounds;
        merge.Y.ShouldBeLessThan(tall.Y);
        tall.Y.ShouldBeLessThan(chat.Y);
        merge.Height.ShouldBe(chat.Height, 0.5f);
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void FortyCardsWithLongTitlesAndLongChoicesDrawInABoundedFrame()
    {
        var cards = Enumerable.Range(1, 40).Select(i => IssueCards()[i % 4] with
        {
            Id = $"c{i}", Title = IssueCards()[i % 4].Title + " and a long reason that runs on well past what three lines of the panel could ever hold, to be cut",
        }).ToArray();
        using var renderer = new RgbaImageRenderer(300, 4000);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = CardsScreen.From(cards);

        var times = new List<double>();
        for (var frame = 0; frame < 3; frame++)
        {
            var watch = Stopwatch.StartNew();
            view.Render(screen, new RectF32(0, 0, 300, 4000));
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 60000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var texts = arranged.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text });
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"40 cards, long titles and choices, 300 wide: first frame {times[0]:F1} ms, then {times[1]:F1} ms and {times[2]:F1} ms; {texts} text leaves, {arranged.Length} nodes");

        // Bounded: a title is at most the three lines' words (Bound), a label at most two, so a card is a fixed few leaves however long its words.
        texts.ShouldBeLessThan(40 * 120);
        times.Skip(1).Max().ShouldBeLessThan(2000d, "a warm frame of forty cards stays far under two seconds");
    }

    [Fact]
    public void CapturesOfTheIssuesFourCardsAtTheNarrowestAndWidePanel()
    {
        // Where the captures go (docs/captures, for the PR) when a person asks for them; PILOT_CARD_CAPTURE_TAG says "before" or "after".
        if (Environment.GetEnvironmentVariable("PILOT_CARD_CAPTURES") is not { Length: > 0 } folder)
        {
            return;
        }
        var tag = Environment.GetEnvironmentVariable("PILOT_CARD_CAPTURE_TAG") is { Length: > 0 } t ? t : "after";
        Directory.CreateDirectory(folder);
        foreach (var width in new[] { (int)CardsView<RgbaImage>.MinWidth, 300 })
        {
            const int height = 1300;
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
            view.Render(CardsScreen.From(IssueCards()), new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(folder, $"cards-wrap-{tag}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }
}
