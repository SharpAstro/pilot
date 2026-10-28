using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A frame's text cost is bounded whatever the cards say: a card of 2,000 characters costs a small fixed number of measurements.</summary>
public sealed class RenderCostTests
{
    private sealed class CountingRenderer(uint width, uint height) : RgbaImageRenderer(width, height)
    {
        public int Calls;
        public long Chars;
        public Dictionary<string, int> Texts { get; } = [];

        public override (float Width, float Height) MeasureText(ReadOnlySpan<char> text, string fontPath, float fontSize)
        {
            Calls++;
            Chars += text.Length;
            var key = text.Length > 40 ? string.Concat(text[..40], "...(", text.Length.ToString(), ")") : text.ToString();
            Texts[key] = Texts.GetValueOrDefault(key) + 1;
            return base.MeasureText(text, fontPath, fontSize);
        }
    }

    private static string Prose(int length)
    {
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel" };
        var sb = new System.Text.StringBuilder();
        for (var i = 0; sb.Length < length; i++)
        {
            sb.Append(words[i % words.Length]).Append(' ');
        }
        return sb.ToString(0, length);
    }

    private static CardRow Card(string id, string? summary, string? detail) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "leg-failed", Subject = "flight/PIL1/leg", Title = "PIL1/leg failed", Summary = summary, Detail = detail,
        At = DateTimeOffset.UnixEpoch, State = CardState.Open,
    };

    private static (int Calls, long Chars, int FirstCalls) SecondFrame(params CardRow[] cards)
    {
        using var renderer = new CountingRenderer(1280, 800);
        var memo = new MemoMeasureRenderer<RgbaImage>(renderer);
        var chrome = new PilotChrome<RgbaImage>(memo, new SessionsView<RgbaImage>(memo)) { FontPath = SessionsCapture.FontPath };
        var sessions = new SessionsScreen([], "test", Live.At);
        var screen = CardsScreen.From(cards);
        chrome.Show(PilotPage.Home);
        chrome.Render(sessions, null, screen, new RectF32(0, 0, 1280, 800));
        var firstCalls = renderer.Calls;
        var firstFrame = renderer.Texts.Keys.ToHashSet();
        renderer.Calls = 0;
        renderer.Chars = 0;
        renderer.Texts.Clear();
        chrome.Render(sessions, null, screen, new RectF32(0, 0, 1280, 800));
        chrome.ShowsCards.ShouldBeTrue();
        var top = string.Join("\n", renderer.Texts.OrderByDescending(t => t.Value).Take(15).Select(t => $"{t.Value} x {t.Key}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"first={firstCalls} calls={renderer.Calls} chars={renderer.Chars}\n{top}");
        renderer.Texts.Keys.Where(firstFrame.Contains).ShouldBeEmpty("a leaf measured in the first frame is measured again in the second");
        return (renderer.Calls, renderer.Chars, firstCalls);
    }

    [Fact]
    public void ALongDetailCostsAFixedFewMeasurements()
    {
        var shortOne = SecondFrame(Card("c1", null, "short detail"));
        var longOne = SecondFrame(Card("c1", null, Prose(2000)));

        AssertBounded(shortOne, longOne);
    }

    [Fact]
    public void ALongSummaryCostsAFixedFewMeasurements()
    {
        var shortOne = SecondFrame(Card("c1", "short summary", null));
        var longOne = SecondFrame(Card("c1", Prose(2000), null));

        AssertBounded(shortOne, longOne);
    }

    // Measured at the leaf: 41 first-frame measurements for a short text, 47 for 2,000 characters (about 6 more words that fit
    // the capped lines), and none in the second frame, because the memoised renderer answers a leaf it has measured.
    private static void AssertBounded((int Calls, long Chars, int FirstCalls) shortOne, (int Calls, long Chars, int FirstCalls) longOne)
    {
        shortOne.Calls.ShouldBe(0);
        longOne.Calls.ShouldBe(0);
        longOne.FirstCalls.ShouldBeLessThanOrEqualTo(shortOne.FirstCalls + 10);
        longOne.FirstCalls.ShouldBeLessThan(80);
    }
}
