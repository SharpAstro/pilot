using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A sha a card's text names is a link to its commit page (#691), as an idea id is a link to its idea.</summary>
public sealed class CardCommitLinkTests
{
    private const string Url = "https://github.com/SharpAstro/Codecs/commit/bc32366a1";

    [Fact]
    public void AShaInACardsDetailIsAFollowableLinkToItsCommitWithCopyUrl()
    {
        var card = new CardRow
        {
            Id = "c9", Bin = CardBin.ForYou, Kind = "pr-ready", Subject = "flight/COD57", Title = "COD57's PR is up to date with master",
            Detail = "master merged in since review, code unchanged (bc32366a1)", Commits = new Dictionary<string, string> { ["bc32366a1"] = Url },
            At = Live.At, State = CardState.Open,
        };
        using var renderer = new RgbaImageRenderer(400, 1200);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var copied = new List<string>();
        view.CopyText = copied.Add;
        string? opened = null;
        view.OnOpen = u => opened = u;

        view.Render(CardsScreen.From([card]), new RectF32(0, 0, 400, 1200));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var link = regions.Where(r => r.Result is HitResult.ButtonHit { Action: "card:c9:commit:bc32366a1" }).ShouldHaveSingleItem();

        link.OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(Url);
        link.OnContextMenu.ShouldNotBeNull().Invoke(new PointerPress(40f, 90f, MouseButton.Right, InputModifier.None, 1));
        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        view.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([Url]);
    }
}
