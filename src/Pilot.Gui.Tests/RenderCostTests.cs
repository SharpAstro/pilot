using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A frame's text cost is bounded whatever the cards say: a card of 2,000 characters costs a small fixed number of measurements.</summary>
public sealed class RenderCostTests
{
    /// <summary>
    /// Counts what the layout asks of <see cref="MeasureText"/> (<see cref="Asked"/>), and what it would cost the window: the
    /// window's renderer (VkRenderer) answers a text it has measured from its own table by font, size and text, so only a text
    /// not seen before is a fit (<see cref="Fitted"/>). The CPU renderer has no such table; this one keeps it, as the window's does.
    /// </summary>
    private sealed class CountingRenderer(uint width, uint height) : RgbaImageRenderer(width, height)
    {
        private readonly HashSet<(string Font, float Size, string Text)> _seen = [];

        public int Asked;
        public int Fitted;
        public long FittedChars;
        public Dictionary<string, int> Texts { get; } = [];

        public void Reset()
        {
            Asked = Fitted = 0;
            FittedChars = 0;
            Texts.Clear();
        }

        public override (float Width, float Height) MeasureText(ReadOnlySpan<char> text, string fontPath, float fontSize)
        {
            Asked++;
            var key = text.Length > 40 ? string.Concat(text[..40], "...(", text.Length.ToString(), ")") : text.ToString();
            Texts[key] = Texts.GetValueOrDefault(key) + 1;
            if (_seen.Add((fontPath, fontSize, text.ToString())))
            {
                Fitted++;
                FittedChars += text.Length;
            }
            return base.MeasureText(text, fontPath, fontSize);
        }
    }

    /// <summary>Words that are each their own text (<c>w0001 w0002 ...</c>), so a renderer's table answers none of them for another.</summary>
    internal static string Prose(int length)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; sb.Length < length; i++)
        {
            sb.Append('w').Append(i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        }
        return sb.ToString(0, length);
    }

    private static CardRow Card(string id, string? summary, string? detail) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "leg-failed", Subject = "flight/PIL1/leg", Title = "PIL1/leg failed", Summary = summary, Detail = detail,
        At = DateTimeOffset.UnixEpoch, State = CardState.Open,
    };

    private sealed record Cost(int FirstAsked, int FirstFitted, int Asked, int Fitted, long FittedChars);

    // The Home page with the cards panel open, 1280 by 800 (the panel 340 wide), drawn twice from the same pages.
    private static Cost TwoFrames(params CardRow[] cards)
    {
        using var renderer = new CountingRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sessions = new SessionsScreen([], "test", Live.At);
        var screen = CardsScreen.From(cards);
        chrome.Show(PilotPage.Home);
        chrome.Render(sessions, null, screen, new RectF32(0, 0, 1280, 800));
        chrome.ShowsCards.ShouldBeTrue();
        var (firstAsked, firstFitted) = (renderer.Asked, renderer.Fitted);
        var top = string.Join("\n", renderer.Texts.OrderByDescending(t => t.Value).Take(15).Select(t => $"{t.Value} x {t.Key}"));
        renderer.Reset();
        chrome.Render(sessions, null, screen, new RectF32(0, 0, 1280, 800));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"first frame: {firstAsked} asked, {firstFitted} fitted; second: {renderer.Asked} asked, {renderer.Fitted} fitted ({renderer.FittedChars} chars)\n{top}");
        return new Cost(firstAsked, firstFitted, renderer.Asked, renderer.Fitted, renderer.FittedChars);
    }

    [Fact]
    public void ALongDetailCostsAFixedFewMeasurements()
    {
        var shortOne = TwoFrames(Card("c1", null, "short detail"));
        var longOne = TwoFrames(Card("c1", null, Prose(2000)));

        AssertBounded(shortOne, longOne);
    }

    [Fact]
    public void ALongSummaryCostsAFixedFewMeasurements()
    {
        var shortOne = TwoFrames(Card("c1", "short summary", null));
        var longOne = TwoFrames(Card("c1", Prose(2000), null));

        AssertBounded(shortOne, longOne);
    }

    // Measured at the leaf, every word its own text (DIR.Lib 11.39): a short card's Home is 43 texts fitted, asked 110 to 112
    // times a frame; with the 2,000-character summary 81 texts, 338 asks, and with the detail 78 texts, 433 asks (the layout asks
    // each word 6 times in the summary and 12 in the detail's stacks, every ask after the first a lookup in the window). Unbounded,
    // the card is 334 words: some 330 more texts fitted and about 4,000 asks a frame, which fails both bounds.
    private static void AssertBounded(Cost shortOne, Cost longOne)
    {
        // A second frame of the same pages fits nothing again: every leaf is the window renderer's lookup.
        shortOne.Fitted.ShouldBe(0);
        longOne.Fitted.ShouldBe(0);
        // What the long card adds over a short one is the words its capped lines could hold, not its length.
        longOne.FirstFitted.ShouldBeLessThanOrEqualTo(shortOne.FirstFitted + LongCardAdds);
        longOne.Asked.ShouldBeLessThanOrEqualTo(SecondFrameAsks);
    }

    /// <summary>
    /// The most texts a long card may add: its text is cut to <see cref="CardsView{TSurface}.SummaryChars"/> (240, the summary)
    /// or three lines of <see cref="CardsView{TSurface}.LineChars"/> (225, the detail), 40 and 37 six-character words; the bound
    /// leaves half again.
    /// </summary>
    private const int LongCardAdds = 60;

    /// <summary>The most a second frame of the Home page with one long card may ask (433 measured), all of it lookups.</summary>
    private const int SecondFrameAsks = 600;

    [Fact]
    public void Bound_cuts_after_the_last_whole_word_within_the_budget()
    {
        WrappedProse.Bound("alpha bravo charlie", 13).ShouldBe("alpha bravo");
        WrappedProse.Bound("alpha bravo charlie", 11).ShouldBe("alpha bravo", "a word ending at the budget is whole");
        WrappedProse.Bound("alpha bravo charlie", 10).ShouldBe("alpha");
    }

    [Fact]
    public void Bound_keeps_a_text_within_the_budget()
    {
        WrappedProse.Bound("alpha bravo", 11).ShouldBe("alpha bravo");
        WrappedProse.Bound("alpha bravo", 40).ShouldBe("alpha bravo");
        WrappedProse.Bound("", 5).ShouldBe("");
    }

    [Fact]
    public void Bound_cuts_a_text_with_no_whitespace_within_the_budget_at_the_budget()
    {
        WrappedProse.Bound("C:/a/very/long/path/with/no/space", 10).ShouldBe("C:/a/very/");
        WrappedProse.Bound(" leading", 4).ShouldBe(" lea", "a space at the very start is no word to cut after");
    }

    [Fact]
    public void Paragraphs_with_line_chars_makes_leaves_only_for_what_the_lines_could_hold()
    {
        static int Leaves(Layout.Node node) => node switch
        {
            Layout.Node.Stack stack => stack.Children.Sum(Leaves),
            Layout.Node.Wrap wrap => wrap.Children.Sum(Leaves),
            Layout.Node.Leaf { Content: Layout.Content.Text } => 1,
            _ => 0,
        };
        var text = Prose(2000);

        // Three lines of 75 characters: 225, the last whole word within them, so 37 six-character words.
        Leaves(WrappedProse.Paragraphs(text, 11f, default, 3, 75)).ShouldBe(37);
        Leaves(WrappedProse.Of(text, 11f, default, 3, lineChars: 75)).ShouldBe(37);
        // No cap on the characters: every word is a leaf.
        Leaves(WrappedProse.Paragraphs(text, 11f, default, 3)).ShouldBe(334);
        // A later paragraph is one line, so 75 characters of its own.
        Leaves(WrappedProse.Paragraphs("short first\n" + text, 11f, default, 3, 75)).ShouldBe(2 + 12);
        // A short text is whole.
        Leaves(WrappedProse.Paragraphs("short detail", 11f, default, 3, 75)).ShouldBe(2);
    }
}
