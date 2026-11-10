using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The two ringing cards of a refused watchtower press (pilot#988), worded as the node words them: COD70's review card whose press was
/// refused (c9919, 2026-11-10), and the card of a refused pilot command with its one button. Drawn at the panel's 420 and a wide 900 with
/// nothing over text and no word past the panel; the captures (for the PR) are written beside PILOT_TEST_CAPTURE when it is set.
/// </summary>
public sealed class RefusedCardCaptureTests
{
    // The cards rang a minute ago, as the person sees a fresh ring: the views' clock is a minute past it.
    private static readonly DateTimeOffset Fresh = Live.At;

    private static DateTimeOffset Now() => Fresh.AddMinutes(1);

    private static readonly CardRow[] Cards =
    [
        new()
        {
            Id = "c9919", Bin = CardBin.Ring, Kind = "review-changes", Subject = "flight/COD70", State = CardState.Open, At = Fresh,
            Title = "Press refused: COD70's review found things to fix",
            Summary = "The watchtower's press was refused by Claude Code's permission check, so nothing moves. Press \"Fix the review's findings\" yourself.",
            Detail = "What to do: press \"Fix the review's findings\" yourself. The watchtower pressed it at 23:37 and auto mode: [Auto-Mode Bypass] refused it, so nothing moves until you do.\n\n## Blocking\n\n- The \"map with no transforms inherits the base's orientation\" rule is applied to ISO `tmap` files too, and it turns a case round 2 reconstructed as HDR into a silent SDR fallback.",
            Choices = [new CardChoice { Label = "Fix the review's findings", Verb = Verbs.GoAround, Target = "flight/COD70/review-notes-2" }],
        },
        new()
        {
            Id = "c9930", Bin = CardBin.Ring, Kind = "watchtower-refused", Subject = "watchtower/refused/flight-merge/PIL1", State = CardState.Open, At = Fresh,
            Title = "The watchtower could not run pilot flight merge",
            Summary = "Claude Code refused a pilot command the watchtower ran, so what it was for has not happened. Run it yourself, then press the button so the watchtower goes on.",
            Detail = "What to do: run it yourself, or do what it was for another way, then press \"Done: tell the watchtower\". It was refused by auto mode: [Auto-Mode Bypass], so nothing it was meant to move moves until then.\n\nIt tried, at 23:41:\nC:/Users/SebastianGodelet/AppData/Local/pilot2/bin/pilot.exe flight merge PIL1 c834afcc0",
            Choices = [new CardChoice { Label = "Done: tell the watchtower", Verb = Verbs.Tell, Target = "watchtower" }],
        },
    ];

    [Theory]
    [InlineData(420)]
    [InlineData(900)]
    public void TheRefusedCardsDrawNothingOverTextAndNoWordPastThePanel(int width)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Clock = Now };
        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From([.. Cards])), new Rect<float>(0, 0, width, 3000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).ShouldAllBe(a => a.Bounds.X + a.Bounds.Width <= width + 0.5f);

        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        const int height = 700;
        using var shot = new RgbaImageRenderer((uint)width, height);
        new CardsView<RgbaImage>(shot) { FontPath = SessionsCapture.FontPath, Clock = Now }.Render(CardsScreen.From([.. Cards]), new RectF32(0, 0, width, height));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(capture) ?? ".", $"refused-cards-{width}.png"), PngWriter.Encode(shot.Surface.Pixels, width, height));
    }
}
