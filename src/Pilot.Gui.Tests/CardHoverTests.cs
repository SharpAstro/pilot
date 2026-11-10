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
        // Past three lines of the panel at its narrowest (a title wraps, pilot#677): the whole is on hover.
        const string longTitle = "TIA1818/details-width compiles against stale siblings of the flight, and the build of the renderer family stays red";
        var tips = Tooltips(Card(longTitle, LongDetail));

        tips.ShouldContain(LongDetail);
        tips.ShouldContain(longTitle);
    }

    [Fact]
    public void AShortCardHasNoHoverTextRepeatingIt()
    {
        var tips = Tooltips(Card("TIA1818 landed", "merged as #1820"));

        tips.ShouldNotContain("TIA1818 landed");
        tips.ShouldNotContain("merged as #1820");
    }

    [Fact]
    public void ACutOptionButtonEndsInAnEllipsisAndCarriesItsWholeOptionOnHover()
    {
        const string whole = "export the variable only when SiblingWatch.ReachedClones(job.Repo) is true, and say so on the job's line in pilot status (jobs in repos with no clones restore packages)";
        var card = Card("the review asks a question of design", "detail") with
        {
            Kind = "pr-review-design",
            Choices =
            [
                new CardChoice { Label = whole[..41] + "...", Verb = Verbs.FixPr, Target = "pr/SharpAstro/pilot#888", Args = [whole], Hint = whole },
                new CardChoice { Label = "Keep a library", Verb = Verbs.FixPr, Target = "pr/SharpAstro/pilot#888", Args = ["Keep a library"] },
            ],
        };

        var tips = Tooltips(card);

        tips.ShouldContain(whole);
        tips.ShouldNotContain("Keep a library");
    }
}
