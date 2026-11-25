using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The ringing cards of a refused watchtower press (pilot#988), worded as the node words them: COD70's review card whose press was
/// refused (c9919, 2026-11-10), c30387's refused allow-round with its Run button, and a refused command that cannot be pressed (pilot#1353). Drawn at the panel's 420 and a wide 900 with
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
            // c30387's allow-round as the node words it: the Run button (its label wraps), then the Done press.
            Id = "c30387", Bin = CardBin.Ring, Kind = "watchtower-refused", Subject = "watchtower/refused/flight-allow-round/PIL1347", State = CardState.Open, At = Fresh,
            Title = "The watchtower could not run pilot flight allow-round",
            Summary = "Claude Code refused a pilot command the watchtower ran, so what it was for has not happened. Press Run to run it as yourself, or run the command from a terminal and press Done another way: the node checks the effect is there, closes the card and tells the watchtower. A command whose effect the node cannot see has no Done button: dismiss the card once it is done.",
            Detail = "What to do: press \"Run it: pilot flight allow-round PIL1347\" to run it as yourself, or run it from a terminal and press \"Done another way: check it\". It was refused by auto mode: [Auto-Mode Bypass], so nothing it was meant to move moves until then.\n\nIt tried, at 23:41:\npilot flight allow-round PIL1347 \"Watchtower: one blocking point is left, a regression from round 2's own fix; the other round 3 note is not blocking.\" 2>&1 | head -20",
            Choices =
            [
                new CardChoice { Label = "Run it: pilot flight allow-round PIL1347", Verb = Verbs.AllowRound, Target = "flight/PIL1347", Args = ["Watchtower: one blocking point is left"] },
                new CardChoice { Label = "Done another way: check it", Verb = Verbs.DoneChecked, Target = "watchtower", Args = [Verbs.AllowRound, "flight/PIL1347"] },
            ],
        },
        new()
        {
            // Two pilot commands joined by &&: no Run button, the first command on a line of its own to copy.
            Id = "c30388", Bin = CardBin.Ring, Kind = "watchtower-refused", Subject = "watchtower/refused/flight-merge/PIL1347", State = CardState.Open, At = Fresh,
            Title = "The watchtower could not run pilot flight merge",
            Summary = "Claude Code refused a pilot command the watchtower ran, so what it was for has not happened. Press Run to run it as yourself, or run the command from a terminal and press Done another way: the node checks the effect is there, closes the card and tells the watchtower. A command whose effect the node cannot see has no Done button: dismiss the card once it is done.",
            Detail = "What to do: it cannot be pressed from here: it is more than one pilot command, and a press runs one. Run it from a terminal, then press \"Done another way: check it\". It was refused by auto mode: [Auto-Mode Bypass], so nothing it was meant to move moves until then.\n\nThe command, at 23:41:\npilot flight merge PIL1347 c834afcc0 --why \"j9 is green, the review passed with no blocking point left\"\n\nThe whole line it tried:\npilot flight merge PIL1347 c834afcc0 --why \"j9 is green, the review passed with no blocking point left\" && pilot flight land PIL1347",
            Choices = [new CardChoice { Label = "Done another way: check it", Verb = Verbs.DoneChecked, Target = "watchtower", Args = [Verbs.AllowRound, "flight/PIL1347"] }],
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
        const int height = 1500;
        using var shot = new RgbaImageRenderer((uint)width, height);
        new CardsView<RgbaImage>(shot) { FontPath = SessionsCapture.FontPath, Clock = Now }.Render(CardsScreen.From([.. Cards]), new RectF32(0, 0, width, height));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(capture) ?? ".", $"refused-cards-{width}.png"), PngWriter.Encode(shot.Surface.Pixels, width, height));
    }
}
