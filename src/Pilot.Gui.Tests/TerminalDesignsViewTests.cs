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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTerminalPageShowsTheBriefBoxAndStartButtonOnOneRow(bool empty)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        var screen = empty ? DesignsScreen.Empty("sample", PageData.Now) : PageData.DesignsFor("sample", PageData.Now);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (Columns, Rows);
        (string Brief, string Node)? asked = null;
        chrome.Designs.OnStart = (brief, node) => asked = (brief, node);

        var arranged = Frame(chrome, screen);
        arranged.ShouldContain(a => IsText(a, "Start a design"));
        arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.Contains("pilot design new", StringComparison.Ordinal)).ShouldBeFalse();
        var input = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput });
        var button = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:start" });
        button.Bounds.Height.ShouldBe(input.Bounds.Height, "one height");
        button.Bounds.Y.ShouldBe(input.Bounds.Y, "one middle");

        chrome.Designs.BriefInput.InsertText("a terminal brief");
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y).ShouldNotBeNull();
        chrome.Designs.Start().ShouldBeTrue();
        asked.ShouldNotBeNull().Brief.ShouldBe("a terminal brief");
        chrome.Designs.Started("DSN-7");
        chrome.Designs.OpenId.ShouldBe("DSN-7");
    }
}
