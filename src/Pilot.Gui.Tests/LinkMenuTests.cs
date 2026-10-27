using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A link that leaves the app has a right-click menu with one item, Copy URL: it hands the URL to the host's CopyText and
/// shows a short "Copied" notice at the pointer, that goes on its own. The cards panel is the first to use it
/// (SharpAstro/pilot#337).
/// </summary>
public sealed class LinkMenuTests
{
    private const string PrUrl = "https://github.com/SharpAstro/DIR.Lib/pull/138";
    private const string RcUrl = "https://claude.ai/code/session_waiting";
    private static readonly PointerPress Right = new(40f, 90f, MouseButton.Right, InputModifier.None, 1);

    private static CardRow PrCard() => new()
    {
        Id = "c5", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/DIR112", Title = "DIR112 landed", At = Live.At, State = CardState.Open, Link = PrUrl,
    };

    private static CardRow WaitingCard() => new()
    {
        Id = "c7", Bin = CardBin.ForYou, Kind = CardKinds.SessionWaiting, Subject = "session/a1b2c3d4", At = Live.At, State = CardState.Open, Link = RcUrl,
        Title = "pilot-tianwen-2 waits on a permission prompt · since 10 h 0 m", Detail = "Bash: git push origin main",
    };

    private static (CardsView<RgbaImage> View, List<string> Copied) Panel(RgbaImageRenderer renderer)
    {
        var copied = new List<string>();
        return (new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CopyText = copied.Add }, copied);
    }

    private static List<ClickableRegion> Painted(CardsView<RgbaImage> view, CardsScreen screen)
    {
        view.Render(screen, new RectF32(0, 0, CardsView<RgbaImage>.Width, 800));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions;
    }

    private static ClickableRegion Link(List<ClickableRegion> regions, string action) =>
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action);

    [Fact]
    public void ARightClickOnACardsTitleLinkOffersCopyUrlAndNothingElse()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);
        var screen = CardsScreen.From([PrCard()]);

        Link(Painted(view, screen), "card:c5:title").OnContextMenu.ShouldNotBeNull().Invoke(Right);

        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        // The menu is on the page, and its one row is what a click on it chooses.
        Painted(view, screen).ShouldContain(r => r.Result is HitResult.ListItemHit);
        view.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([PrUrl]);
    }

    [Fact]
    public void AWaitingSessionsAnswerInTheBrowserHasTheSameMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);

        Link(Painted(view, CardsScreen.From([WaitingCard()])), "card:c7:answer").OnContextMenu.ShouldNotBeNull().Invoke(Right);

        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        view.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([RcUrl]);
    }

    [Fact]
    public void TheWatchtowersRemoteControlLinkHasTheSameMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);
        var tower = Live.Row("bbbb0002", "se1-watchtower", SessionState.Idle) with { Kind = SessionKind.Background, Role = SessionRole.Watchtower, RemoteControlUrl = RcUrl };

        Link(Painted(view, CardsScreen.From([], [tower])), "watchtower:rc").OnContextMenu.ShouldNotBeNull().Invoke(Right);

        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        view.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([RcUrl]);
    }

    [Fact]
    public void ALeftClickStillOpensTheLinkAndOnlyARightClickOpensTheMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, _) = Panel(renderer);
        var opened = new List<string>();
        view.OnOpen = opened.Add;

        Link(Painted(view, CardsScreen.From([PrCard()])), "card:c5:title").OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

        opened.ShouldBe([PrUrl]);
        view.Links.MenuItems.ShouldBeEmpty();
    }

    [Fact]
    public void AHostWithNoClipboardGivesALinkNoMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        Link(Painted(view, CardsScreen.From([PrCard()])), "card:c5:title").OnContextMenu.ShouldBeNull();
    }

    [Fact]
    public void ChoosingCopyUrlShowsACopiedNoticeThatGoesOnItsOwn()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, _) = Panel(renderer);
        var now = Live.At;
        view.Links.Now = () => now;
        var screen = CardsScreen.From([PrCard()]);
        var regions = Painted(view, screen);
        var plain = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)).Count();

        Link(regions, "card:c5:title").OnContextMenu!.Invoke(Right);
        view.Links.MenuItems[0].OnChoose!();
        view.Links.NoticeShown.ShouldBeTrue();
        Texts(view, screen).ShouldContain(LinkMenu.CopiedText);

        now += LinkMenu.NoticeFor - LinkMenu.FadeFor / 2;
        Texts(view, screen).ShouldContain(LinkMenu.CopiedText);

        now += LinkMenu.FadeFor;
        view.Links.NoticeShown.ShouldBeFalse();
        Texts(view, screen).ShouldNotContain(LinkMenu.CopiedText);
        // Nothing else is left on the screen: the tree is the one it was before the menu.
        Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)).Count().ShouldBe(plain);
    }

    private static IEnumerable<string> Texts(CardsView<RgbaImage> view, CardsScreen screen)
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        return [.. Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : null).OfType<string>()];
    }

    [Fact]
    public void CapturesOfTheMenuOpenOverACardAndOfTheCopiedNotice()
    {
        // Written where PILOT_TEST_CAPTURE says, else beside the temp files; never into the plane.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture
            ? Path.GetDirectoryName(capture) ?? "."
            : Path.Combine(Path.GetTempPath(), "pilot-linkmenu-captures");
        Directory.CreateDirectory(dir);
        var longCard = PrCard() with { Title = "DIR112 landed: the whole of a very long title that runs past the panel's width" };
        foreach (var (width, height) in new[] { (300, 520), (480, 520) })
        {
            foreach (var chosen in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (view, _) = Panel(renderer);
                var screen = CardsScreen.From([longCard, WaitingCard()]);
                view.Links.Now = () => Live.At;
                var title = Link(Painted(view, screen), "card:c5:title");
                title.OnContextMenu!.Invoke(new PointerPress(title.X + 30f, title.Y + 10f, MouseButton.Right, InputModifier.None, 1));
                if (chosen)
                {
                    view.Links.MenuItems[0].OnChoose!();
                }
                view.Render(screen, new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"link-menu-{(chosen ? "copied" : "open")}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }
}
