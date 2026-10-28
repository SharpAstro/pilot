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

    private static (int Calls, long Chars, CountingRenderer Renderer) SecondFrame(params CardRow[] cards)
    {
        using var renderer = new CountingRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sessions = new SessionsScreen([], "test", Live.At);
        var screen = CardsScreen.From(cards);
        chrome.Show(PilotPage.Home);
        chrome.Render(sessions, null, screen, new RectF32(0, 0, 1280, 800));
        renderer.Calls = 0;
        renderer.Chars = 0;
        renderer.Texts.Clear();
        chrome.Render(sessions, null, screen, new RectF32(0, 0, 1280, 800));
        chrome.ShowsCards.ShouldBeTrue();
        var top = string.Join("\n", renderer.Texts.OrderByDescending(t => t.Value).Take(15).Select(t => $"{t.Value} x {t.Key}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"calls={renderer.Calls} chars={renderer.Chars}\n{top}");
        return (renderer.Calls, renderer.Chars, renderer);
    }

    [Fact]
    public void ALongDetailCostsAFixedFewMeasurements()
    {
        var shortOne = SecondFrame(Card("c1", null, "short detail"));
        var longOne = SecondFrame(Card("c1", null, Prose(2000)));

        longOne.Calls.ShouldBeLessThan(600);
        longOne.Chars.ShouldBeLessThan(20_000);
        // Measured: 98 calls for a short detail, 431 for 2,000 characters (about 40 words, each leaf measured 6 to 8 times by DIR.Lib's
        // layout passes; the fit inside DIR.Lib's painter is DIR.Lib's to memoise, a per-run TextFit cache). The delta is fixed
        // whatever the length: 20,000 characters measure the same as 2,000.
        longOne.Calls.ShouldBeLessThanOrEqualTo(shortOne.Calls + 380);
    }

    [Fact]
    public void ALongSummaryCostsAFixedFewMeasurements()
    {
        var shortOne = SecondFrame(Card("c1", "short summary", null));
        var longOne = SecondFrame(Card("c1", Prose(2000), null));

        longOne.Calls.ShouldBeLessThan(600);
        longOne.Chars.ShouldBeLessThan(20_000);
        longOne.Calls.ShouldBeLessThanOrEqualTo(shortOne.Calls + 250);
    }
}
