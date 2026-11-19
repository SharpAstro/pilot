using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Designs and ideas linked many to many (#1185), seen from both pages: an idea shows each design it is linked to and has Link a design; a design's page shows
/// "For: I381 title" for each idea on its first row, with Link an idea and a remove on each; the Designs list names each design's ideas; the Ideas page filters
/// to ideas with a design. One component draws a linked thing both ways (LinkedThing), in the pixel and the terminal host. Drawn on the CPU from PageData's sample:
/// I381 is linked to DSN-1 and DSN-3 (two designs), DSN-1 is for I381 and I383 (two ideas), I384 has DSN-2 with no version, I382 has none.
/// </summary>
public sealed class LinkedDesignsViewTests
{
    private static readonly DateTimeOffset Now = PageData.Now;

    private static (PilotChrome<RgbaImage> Chrome, List<(string Verb, string Target, string[] Args)> Sent, List<Task> Tasks) Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sent = new List<(string, string, string[])>();
        var tasks = new List<Task>();
        chrome.Ideas.Send = (verb, target, args, _) =>
        {
            lock (sent)
            {
                sent.Add((verb, target, args));
            }
            return Task.FromResult(new IdeaReply(true));
        };
        chrome.Ideas.Track = (run, _) => tasks.Add(run());
        return (chrome, sent, tasks);
    }

    private static void DrawIdeas(PilotChrome<RgbaImage> chrome, float width = 1280, float height = 1600)
    {
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, null, null, null, null, null, null, new RectF32(0, 0, width, height), null, PageData.DesignIdeas(), null, PageData.DesignsFor("sample", Now));
    }

    private static void DrawDesign(PilotChrome<RgbaImage> chrome, float width = 1280, float height = 1000)
    {
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, null, null, null, null, null, null, new RectF32(0, 0, width, height), null, null, null, PageData.DesignsFor("sample", Now));
    }

    private static void Click(List<ClickableRegion> regions, string action) =>
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome, bool ideas)
    {
        var regions = new List<ClickableRegion>();
        if (ideas)
        {
            chrome.Ideas.CollectPaintedRegions(regions);
        }
        else
        {
            chrome.Designs.CollectPaintedRegions(regions);
        }
        return regions;
    }

    private static IEnumerable<string> Actions(List<ClickableRegion> regions) =>
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action);

    private static string Spoken(RgbaImageRenderer renderer, Layout.Node tree, float width = 1280)
    {
        var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, 6000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return string.Concat(arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value));
    }

    [Theory]
    [InlineData("List")]
    [InlineData("Canvas")]
    [InlineData("Tree")]
    public void AnIdeaShowsEveryDesignItIsLinkedToAsALinkInEveryView(string view)
    {
        var mode = Enum.Parse<IdeasViewMode>(view);
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Show(PilotPage.Ideas);
        chrome.Ideas.ViewMode = mode;
        DrawIdeas(chrome);
        if (mode == IdeasViewMode.Tree)
        {
            chrome.Ideas.Select(chrome.Ideas.Board.Find("pilot#381"));
            DrawIdeas(chrome);
        }

        var said = Spoken(renderer, chrome.Ideas.Tree(PageData.DesignIdeas()));
        // The tree grid's row has the room for the id and the version; the rows, the cards and the grid's detail add the title.
        said.ShouldContain(mode == IdeasViewMode.Tree ? "DSN-3 · v2" : "DSN-3 Design 2 · v2");
        said.ShouldContain(mode == IdeasViewMode.Tree ? "DSN-1 · v2" : "DSN-1 Design 0 · v2");
        var actions = Actions(Regions(chrome, true)).ToList();
        actions.ShouldContain(mode == IdeasViewMode.Tree ? "ideas:tree-design:pilot I381:DSN-1" : "ideas:design:pilot I381:DSN-1");
        actions.ShouldContain(mode == IdeasViewMode.Tree ? "ideas:tree-design:pilot I381:DSN-3" : "ideas:design:pilot I381:DSN-3");
    }

    [Fact]
    public void ADesignLinkOnTheIdeasPageOpensThatDesignsPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Show(PilotPage.Ideas);
        DrawIdeas(chrome);
        Click(Regions(chrome, true), "ideas:design:pilot I381:DSN-3");
        chrome.Page.ShouldBe(PilotPage.Designs);
        chrome.Designs.OpenId.ShouldBe("DSN-3");
    }

    [Fact]
    public void ADesignsPageShowsForAndEachIdeaOnItsFirstRowAsLinksThatOpenTheIdeasPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        DrawDesign(chrome);

        var said = Spoken(renderer, chrome.Designs.Tree(PageData.DesignsFor("sample", Now)));
        said.ShouldContain("For:");
        said.ShouldContain("I381 Ideas pane · noted");
        said.ShouldContain("I383 Night mode for the rail · noted", Case.Insensitive, said);
        var actions = Actions(Regions(chrome, false)).ToList();
        actions.ShouldContain("designs:for-idea:pilot:I381");
        actions.ShouldContain("designs:for-idea:pilot:I383");

        Click(Regions(chrome, false), "designs:for-idea:pilot:I383");
        chrome.Page.ShouldBe(PilotPage.Ideas);
        chrome.Ideas.ShowingIdea.ShouldBe("pilot I383");
    }

    [Fact]
    public void TheDesignsListNamesEachDesignsIdeas()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Show(PilotPage.Designs);
        var said = Spoken(renderer, chrome.Designs.Tree(PageData.DesignsFor("sample", Now)));
        said.ShouldContain("for I381, I383");
        said.ShouldContain("for I381");
    }

    [Fact]
    public async Task LinkADesignPicksFromTheNodesDesignsAndSendsDesignLink()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, tasks) = Chrome(renderer);
        chrome.Show(PilotPage.Ideas);
        DrawIdeas(chrome);
        Click(Regions(chrome, true), "ideas:linkdesign:pilot I382");
        DrawIdeas(chrome);

        var actions = Actions(Regions(chrome, true)).ToList();
        actions.ShouldContain("ideas:design-link:DSN-1");
        actions.ShouldContain("ideas:design-link:DSN-2");
        actions.ShouldContain("ideas:design-link:DSN-3");
        Click(Regions(chrome, true), "ideas:design-link:DSN-2");
        await Task.WhenAll(tasks);

        var link = sent.ShouldHaveSingleItem();
        link.Verb.ShouldBe(Verbs.DesignLink);
        link.Target.ShouldBe(Verbs.DesignTarget("DSN-2"));
        link.Args.ShouldBe(["pilot I382"]);
    }

    [Fact]
    public void ThePickerOffersOnlyTheDesignsNotLinkedYet()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Show(PilotPage.Ideas);
        DrawIdeas(chrome);
        Click(Regions(chrome, true), "ideas:linkdesign:pilot I381");
        DrawIdeas(chrome);

        var actions = Actions(Regions(chrome, true)).ToList();
        actions.ShouldContain("ideas:design-link:DSN-2");
        actions.ShouldNotContain("ideas:design-link:DSN-1");
        actions.ShouldNotContain("ideas:design-link:DSN-3");
    }

    [Fact]
    public async Task RemovingADesignFromAnIdeaSendsDesignUnlink()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, tasks) = Chrome(renderer);
        chrome.Show(PilotPage.Ideas);
        DrawIdeas(chrome);
        Click(Regions(chrome, true), "ideas:design:pilot I381-remove:DSN-3");
        await Task.WhenAll(tasks);

        var unlink = sent.ShouldHaveSingleItem();
        unlink.Verb.ShouldBe(Verbs.DesignUnlink);
        unlink.Target.ShouldBe(Verbs.DesignTarget("DSN-3"));
        unlink.Args.ShouldBe(["pilot I381"]);
    }

    [Fact]
    public void LinkAnIdeaPicksFromTheIdeasNotLinkedAndSendsDesignLink()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var asked = new List<(string Design, string Idea, bool Link)>();
        chrome.Designs.OnLinkIdea = (design, idea, link) => asked.Add((design, idea, link));
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        DrawDesign(chrome);
        Click(Regions(chrome, false), "designs:link-idea");
        chrome.Designs.PickingIdea.ShouldBeTrue();
        DrawDesign(chrome);

        var actions = Actions(Regions(chrome, false)).ToList();
        actions.ShouldContain("designs:pick-idea:pilot:I382");
        actions.ShouldContain("designs:pick-idea:pilot:I384");
        actions.ShouldNotContain("designs:pick-idea:pilot:I381");
        actions.ShouldNotContain("designs:pick-idea:pilot:I383");
        Click(Regions(chrome, false), "designs:pick-idea:pilot:I382");

        asked.ShouldHaveSingleItem().ShouldBe(("DSN-1", "pilot I382", true));
        chrome.Designs.PickingIdea.ShouldBeFalse();
    }

    [Fact]
    public void RemovingAnIdeaFromADesignSendsDesignUnlink()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var asked = new List<(string Design, string Idea, bool Link)>();
        chrome.Designs.OnLinkIdea = (design, idea, link) => asked.Add((design, idea, link));
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        DrawDesign(chrome);
        Click(Regions(chrome, false), "designs:for-idea:pilot-remove:I383");
        asked.ShouldHaveSingleItem().ShouldBe(("DSN-1", "pilot I383", false));
    }

    [Fact]
    public void ADesignPageWithNoWayToLinkShowsTheIdeasButNoControls()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        DrawDesign(chrome);
        var actions = Actions(Regions(chrome, false)).ToList();
        actions.ShouldContain("designs:for-idea:pilot:I381");
        actions.ShouldNotContain("designs:link-idea");
        actions.ShouldNotContain(a => a.Contains("-remove:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFilterKeepsOnlyTheIdeasThatHaveADesign()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Show(PilotPage.Ideas);
        DrawIdeas(chrome);
        Spoken(renderer, chrome.Ideas.Tree(PageData.DesignIdeas())).ShouldContain("Rail count");

        Click(Regions(chrome, true), "ideas:filter-has-design");
        chrome.Ideas.OnlyHasDesign.ShouldBeTrue();
        DrawIdeas(chrome);
        chrome.Ideas.Board.Unsorted.Items.Select(i => i.Key).ShouldBe(["pilot#381", "pilot#383", "pilot#384"], ignoreOrder: true);
        Spoken(renderer, chrome.Ideas.Tree(PageData.DesignIdeas())).ShouldNotContain("Rail count");

        Click(Regions(chrome, true), "ideas:filter-has-design");
        chrome.Ideas.OnlyHasDesign.ShouldBeFalse();
    }

    // ---- Captures: an idea with two designs and a design with two ideas, in pilot-gui and pilot-tui ----

    private static void Keep(string name, byte[] png)
    {
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(artifacts);
        File.WriteAllBytes(Path.Combine(artifacts, name), png);
    }

    [Theory]
    [InlineData("ideas", 1920, 1080)]
    [InlineData("ideas", 2880, 1620)]
    [InlineData("design", 1920, 1080)]
    [InlineData("design", 2880, 1620)]
    public void TheLinkedPagesAreCapturedInPilotGuiWithNoTextOverlap(string page, int width, int height)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        using var accent = Pilot.Ui.Palette.StandardForCapture(null);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        Layout.Node tree;
        if (page == "ideas")
        {
            chrome.Show(PilotPage.Ideas);
            DrawIdeas(chrome, width, height);
            tree = chrome.Ideas.Tree(PageData.DesignIdeas());
        }
        else
        {
            chrome.Designs.OnLinkIdea = (_, _, _) => { };
            chrome.Show(PilotPage.Designs);
            chrome.Designs.Open("DSN-1");
            DrawDesign(chrome, width, height);
            tree = chrome.Designs.Tree(PageData.DesignsFor("sample", Now));
        }
        var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"{page} at {width}");
        Keep($"after-linked-{page}-pilot-gui-{width}.png", SharpAstro.Png.PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
    }

    [Theory]
    [InlineData("ideas")]
    [InlineData("design page")]
    public async Task TheLinkedPagesAreCapturedInPilotTui(string page)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        const int columns = 160, rows = 44;
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        var frame = page == "ideas"
            ? await TerminalWireframeSweepTests.RecordCase(page, "sample", columns, rows, buffer, chrome =>
            {
                chrome.Ideas.Send = (_, _, _, _) => Task.FromResult(new IdeaReply(true));
                chrome.Ideas.Designs = PageData.DesignsFor("sample", Now).Rows;
            }, PageData.DesignIdeas())
            : await TerminalWireframeSweepTests.RecordCase(page, "sample", columns, rows, buffer, chrome => chrome.Designs.OnLinkIdea = (_, _, _) => { });
        var lines = Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();
        var text = string.Join(Environment.NewLine, lines);
        if (page == "ideas")
        {
            text.ShouldContain("DSN-1 Design 0 · v2");
            text.ShouldContain("DSN-3 Design 2 · v2");
            text.ShouldContain(LinkedThing.LinkDesignLabel);
        }
        else
        {
            text.ShouldContain("For:");
            text.ShouldContain("I381");
            text.ShouldContain("I383");
        }
        // The cell checks of these pages at this size belong to the sweep (TerminalWireframeSweepTests, with its baseline: the cards panel's last column is cut there on main too); this test is the capture and the text.
        frame.Frame.Texts.IsEmpty.ShouldBeFalse();
        Keep($"after-linked-{page.Replace(' ', '-')}-pilot-tui-{columns}x{rows}.png", DesignsWaitingIdeasTests.Png(lines));
    }
}
