using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A card's title is one line and its detail three: past those they are cut, and the cut drops the rest with no mark
/// (SharpAstro/DIR.Lib#155), so a long title or detail carries its whole text as hover text (TIA1818's ring, SE1 2026-10-25).
/// </summary>
public sealed class CardHoverTests
{
    private const string LongDetail = "Work done and committed (de7bdc0fb, c331aaad0); 30 tests pass when the filter is quoted, but the node's check runs the "
        + "filter unquoted through cmd, which splits it at the pipe and runs no test at all, so the check failed on a leg whose work is done.";

    private static CardRow Card(string title, string? detail) => new()
    {
        Id = "c1", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/TIA1818/details-width", Title = title, Detail = detail,
        At = DateTimeOffset.UnixEpoch, State = CardState.Open,
    };

    private static string[] Tooltips(CardRow card)
    {
        using var renderer = new RgbaImageRenderer(400, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return [.. Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node.Tooltip).OfType<string>()];
    }

    [Fact]
    public void ALongDetailAndALongTitleCarryTheirWholeTextOnHover()
    {
        var tips = Tooltips(Card("TIA1818/details-width compiles against stale siblings", LongDetail));

        tips.ShouldContain(LongDetail);
        tips.ShouldContain("TIA1818/details-width compiles against stale siblings");
    }

    [Fact]
    public void AShortCardHasNoHoverTextRepeatingIt()
    {
        var tips = Tooltips(Card("TIA1818 landed", "merged as #1820"));

        tips.ShouldNotContain("TIA1818 landed");
        tips.ShouldNotContain("merged as #1820");
    }
}
