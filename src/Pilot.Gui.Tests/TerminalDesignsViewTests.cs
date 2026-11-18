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
        var sample = PageData.DesignsFor("sample", PageData.Now);
        // One cell whose frame is a terminal of 80 x 8, so the goto is within the rows the page has above the chat.
        var screen = sample with { Open = sample.Open! with { Doc = sample.Open.Doc! with { Cells = [new DesignCell("Home", "Shows the home.", [new DesignFrame("home", "80x8")], "")] } } };
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (Columns, Rows);
        chrome.Designs.Open(screen.Open!.Detail.Row.Id);

        var arranged = Frame(chrome, screen);
        chrome.Designs.PlayerOf()!.CurrentScreen.Id.ShouldBe("home");
        var link = arranged.First(a => IsText(a, "Open settings"));
        CellLayout.HitTest(arranged, link.Bounds.X + 1, link.Bounds.Y).ShouldNotBeNull();

        chrome.Designs.PlayerOf()!.CurrentScreen.Id.ShouldBe("settings");
        // The next frame shows the new screen's own text, under the caption.
        var next = Frame(chrome, screen);
        next.ShouldContain(a => IsText(a, "Delete this node"));
        next.ShouldContain(a => IsText(a, "Proposed design · Home · Terminal 80 x 8"));
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

        chrome.Designs.BriefInput.Text = "a terminal brief";
        // A click in the terminal runs the button's handler (HitTest invokes OnClick), so it is the press.
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y).ShouldNotBeNull();
        asked.ShouldNotBeNull().Brief.ShouldBe("a terminal brief");
        chrome.Designs.Started("DSN-7");
        chrome.Designs.OpenId.ShouldBeNull("the answer waits for the render thread's next frame");
        Frame(chrome, screen);
        chrome.Designs.OpenId.ShouldBe("DSN-7");
    }

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Vessel) Opened(DesignsScreen screen, int columns = Columns, int rows = Rows)
    {
        var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (columns, rows);
        chrome.Designs.Open(screen.Open!.Detail.Row.Id);
        return (chrome, vessel);
    }

    private static Layout.ArrangedNode<int> Button(System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> arranged, string action) =>
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit hit && hit.Action == action);

    [Fact]
    public void TheTerminalPageSendsDesignBuildForTheVersionOnShowWithNoQuestionWhenTheDesignHasARepo()
    {
        var screen = PageData.BuildableDesigns(PageData.Now, repo: true, idea: false, flight: null);
        var (chrome, vessel) = Opened(screen);
        using var vesselScope = vessel;
        (string Id, int Version, string? Repo, string? Milestone)? asked = null;
        chrome.Designs.OnBuild = (id, version, repo, milestone) => asked = (id, version, repo, milestone);

        var arranged = Frame(chrome, screen);
        arranged.ShouldContain(a => IsText(a, "Build this version"));
        var button = Button(arranged, "designs:build");
        button.Bounds.Height.ShouldBe(1, "a row of cells");
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y).ShouldNotBeNull();

        asked.ShouldBe(("DSN-1", 2, null, null));
    }

    [Fact]
    public void TheTerminalPageAsksForTheRepoOfADesignWithNoneAndThenSends()
    {
        var screen = PageData.BuildableDesigns(PageData.Now, repo: false, idea: false, flight: null);
        var (chrome, vessel) = Opened(screen);
        using var vesselScope = vessel;
        var sent = new List<string?>();
        chrome.Designs.OnBuild = (_, _, repo, _) => sent.Add(repo);

        var arranged = Frame(chrome, screen);
        var button = Button(arranged, "designs:build");
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y).ShouldNotBeNull();
        sent.ShouldBeEmpty("a repo is asked for before anything is sent");

        arranged = Frame(chrome, screen);
        arranged.ShouldContain(a => IsText(a, "Which repo is it built in?"));
        var chip = Button(arranged, "designs:build-repo:tianwen");
        CellLayout.HitTest(arranged, chip.Bounds.X + 1, chip.Bounds.Y).ShouldNotBeNull();

        sent.ShouldBe(["tianwen"]);
    }

    [Fact]
    public void TheTerminalPageAsksForTheMilestoneOfADesignFromAnIdea()
    {
        var screen = PageData.BuildableDesigns(PageData.Now, repo: true, idea: true, flight: null);
        var (chrome, vessel) = Opened(screen);
        using var vesselScope = vessel;
        var sent = new List<string?>();
        chrome.Designs.OnBuild = (_, _, _, milestone) => sent.Add(milestone);

        var arranged = Frame(chrome, screen);
        var button = Button(arranged, "designs:build");
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y).ShouldNotBeNull();
        arranged = Frame(chrome, screen);
        arranged.ShouldContain(a => IsText(a, "Which milestone is its idea planned into?"));
        var chip = Button(arranged, "designs:build-milestone:11");
        CellLayout.HitTest(arranged, chip.Bounds.X + 1, chip.Bounds.Y).ShouldNotBeNull();

        sent.ShouldBe(["Ideas"]);
    }

    [Fact]
    public void TheTerminalPageShowsABuiltVersionsFlightStateAndPrAndOffersNoSecondBuildWhileItFlies()
    {
        var screen = PageData.BuildableDesigns(PageData.Now, repo: true, idea: false, flight: FlightState.Flying);
        var (chrome, vessel) = Opened(screen);
        using var vesselScope = vessel;
        string? flight = null;
        chrome.Designs.OnFlight = code => flight = code;

        var arranged = Frame(chrome, screen);
        arranged.ShouldContain(a => IsText(a, "PIL1153"));
        arranged.ShouldContain(a => IsText(a, "flying"));
        arranged.ShouldContain(a => IsText(a, "PR #1170"));
        arranged.Any(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:build" }).ShouldBeFalse();
        var link = Button(arranged, "designs:flight:PIL1153");
        CellLayout.HitTest(arranged, link.Bounds.X + 1, link.Bounds.Y).ShouldNotBeNull();
        flight.ShouldBe("PIL1153");
    }

    /// <summary>
    /// The issue's design (#1160) in cells: its title made from the brief by the store's rule, a long brief, and no version. At every terminal
    /// sweep size the title line carries the whole title on hover and its cells hold it whole or end in an ellipsis, and the no-version text is
    /// words that wrap, never one line a column clips.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(100, 30)]
    [InlineData(160, 44)]
    [InlineData(250, 60)]
    public void TheNoVersionPagesTitleIsWholeOrEndsInAnEllipsisWithTheWholeOnHoverInCells(int columns, int rows)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        var screen = PageData.NoVersionDesigns(PageData.Now);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (columns, rows);
        chrome.Designs.Open(screen.Open!.Detail.Row.Id);
        var title = screen.Open.Detail.Row.Title!;
        var counts = $"{screen.Open.Detail.Row.Id} · {title}";

        var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, PageData.Now, _ => { }, null, columns);
        var arranged = TerminalFrame.Build(rail, chrome.Designs.Tree(screen), null, columns, rows, CellMeasureContext.PixelAuthored);
        var line = arranged.Single(a => a.Node.Tooltip == counts);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        TerminalWireframeSweepTests.Record(arranged, columns, rows, buffer);
        var drawn = string.Concat(Enumerable.Range(line.Bounds.X, Math.Min(line.Bounds.Width, columns - line.Bounds.X)).Select(x => buffer.BackAt(x, line.Bounds.Y).Glyph)).TrimEnd();
        (drawn.EndsWith('…') || drawn == counts).ShouldBeTrue($"{columns}x{rows}: the title line is whole or ends in an ellipsis, drew '{drawn}'");
        arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.StartsWith("This design has no version yet. The brief", StringComparison.Ordinal))
            .ShouldBeFalse($"{columns}x{rows}: the no-version text is words that wrap, not one line");
    }
}
