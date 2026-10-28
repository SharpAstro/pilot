using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A card shows its summary as its body; its raw detail is folded under a toggle, closed until it is clicked, per card.</summary>
public sealed class CardDetailFoldTests
{
    private const string Said = "The leg failed its check; redo it or dismiss the card.";
    private const string Raw = "exit code 1 from dotnet test, 3 failing tests in WidgetTests";

    private static CardRow Card(string id, string? summary, string? detail) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/PIL1/leg", Title = "PIL1/leg failed", Summary = summary, Detail = detail,
        At = DateTimeOffset.UnixEpoch, State = CardState.Open,
    };

    private static CardsView<RgbaImage> NewView(RgbaImageRenderer renderer) => new(renderer) { FontPath = SessionsCapture.FontPath };

    private static string Words(CardsView<RgbaImage> view, RgbaImageRenderer renderer, CardsScreen screen) =>
        string.Join(" ", Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 400, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()));

    private static List<ClickableRegion> Regions(CardsView<RgbaImage> view, CardsScreen screen)
    {
        view.Render(screen, new RectF32(0, 0, 400, 1200));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions;
    }

    private static bool HasToggle(CardsView<RgbaImage> view, CardsScreen screen, string id) =>
        Regions(view, screen).Any(r => r.Result is HitResult.ButtonHit b && b.Action == $"card:{id}:detail");

    private static void Toggle(CardsView<RgbaImage> view, CardsScreen screen, string id) =>
        Regions(view, screen).Single(r => r.Result is HitResult.ButtonHit b && b.Action == $"card:{id}:detail").OnClick!.Invoke(InputModifier.None);

    [Fact]
    public void ASummaryShowsBeforeTheToggleAndTheDetailWaitsForTheClickThenOpensAndShutsAgain()
    {
        using var renderer = new RgbaImageRenderer(1280, 1200);
        var view = NewView(renderer);
        var screen = CardsScreen.From([Card("c1", Said, Raw)]);

        var closed = Words(view, renderer, screen);
        closed.ShouldContain("redo it or dismiss");
        closed.ShouldNotContain("failing tests in WidgetTests");
        closed.IndexOf("redo it or dismiss", StringComparison.Ordinal).ShouldBeLessThan(closed.IndexOf("Detail", StringComparison.Ordinal));

        Toggle(view, screen, "c1");
        view.DetailOpenOn("c1").ShouldBeTrue();
        Words(view, renderer, screen).ShouldContain("failing tests in WidgetTests");

        Toggle(view, screen, "c1");
        Words(view, renderer, screen).ShouldNotContain("failing tests in WidgetTests");
    }

    [Fact]
    public void TheFoldIsPerCard()
    {
        using var renderer = new RgbaImageRenderer(1280, 1200);
        var view = NewView(renderer);
        var screen = CardsScreen.From([Card("c1", Said, Raw), Card("c2", Said, Raw)]);

        Toggle(view, screen, "c1");

        view.DetailOpenOn("c1").ShouldBeTrue();
        view.DetailOpenOn("c2").ShouldBeFalse();
    }

    [Fact]
    public void ACardWithNoDetailHasNoToggle()
    {
        using var renderer = new RgbaImageRenderer(1280, 1200);
        var view = NewView(renderer);

        var screen = CardsScreen.From([Card("c1", Said, null)]);

        HasToggle(view, screen, "c1").ShouldBeFalse();
        Words(view, renderer, screen).ShouldContain("redo it or dismiss");
    }

    [Fact]
    public void ACardFromAnOldNodeWithNoSummaryShowsItsDetailAsBefore()
    {
        using var renderer = new RgbaImageRenderer(1280, 1200);
        var view = NewView(renderer);

        var screen = CardsScreen.From([Card("c1", null, Raw)]);

        HasToggle(view, screen, "c1").ShouldBeFalse();
        Words(view, renderer, screen).ShouldContain("failing tests in WidgetTests");
    }
}
