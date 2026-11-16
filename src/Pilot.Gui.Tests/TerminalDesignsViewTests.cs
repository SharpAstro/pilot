using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Designs page as pilot-tui plays it (PIL1098): the prototype is a node of the page's own tree, so a click on its cells is the terminal's ordinary hit test.</summary>
public sealed class TerminalDesignsViewTests
{
    private const int Columns = 100;
    private const int Rows = 30;

    private static System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Frame(PilotChrome<RgbaImage> chrome, DesignsScreen screen)
    {
        var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, PageData.Now, _ => { }, null, Columns);
        return TerminalFrame.Build(rail, chrome.Designs.Tree(screen), null, Columns, Rows, CellMeasureContext.PixelAuthored);
    }

    private static bool IsText(Layout.ArrangedNode<int> a, string text) =>
        a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == text;

    [Fact]
    public void AClickOnAGotosCellsInTheTerminalPageChangesTheScreen()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        var screen = PageData.DesignsFor("sample", PageData.Now);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (Columns, Rows);
        chrome.Designs.Open(screen.Open!.Detail.Row.Id);

        var arranged = Frame(chrome, screen);
        chrome.Designs.Player!.CurrentScreen.Id.ShouldBe("home");
        var link = arranged.First(a => IsText(a, "Open settings"));
        CellLayout.HitTest(arranged, link.Bounds.X + 1, link.Bounds.Y).ShouldNotBeNull();

        chrome.Designs.Player.CurrentScreen.Id.ShouldBe("settings");
        // The next frame shows the new screen's own text; the screens are named buttons, not a strip of renders.
        var next = Frame(chrome, screen);
        next.ShouldContain(a => IsText(a, "Delete this node"));
        next.ShouldContain(a => a.Node.Hit is HitResult.ButtonHit && ((HitResult.ButtonHit)a.Node.Hit).Action == "designs:screen:confirm");
    }
}
