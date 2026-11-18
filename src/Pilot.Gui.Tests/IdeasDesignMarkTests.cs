using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Ideas page's design mark (PIL1152): the 'needs design' mark, the design's id and version as a link, and Mark for design / Unmark design, one
/// declared component for the list, the canvas and the tree grid in the pixel and the terminal host; the filter 'Needs a design'; and the question Plan
/// asks of a marked idea whose design has no version. Drawn on the CPU from PageData's sample (I381 marked with DSN-1 v2, I382 marked with no design,
/// I383 not marked, I384 marked with DSN-2 and no version).
/// </summary>
public sealed class IdeasDesignMarkTests
{
    private static (PilotChrome<RgbaImage> Chrome, List<(string Verb, string Target, string[] Args)> Sent, List<Task> Tasks) Chrome(RgbaImageRenderer renderer, IdeaReply? reply = null)
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
            return Task.FromResult(reply ?? new IdeaReply(true));
        };
        chrome.Ideas.Track = (run, _) => tasks.Add(run());
        chrome.Show(PilotPage.Ideas);
        return (chrome, sent, tasks);
    }

    private static void Draw(PilotChrome<RgbaImage> chrome, IdeasScreen screen, float width = 1280, float height = 1600) =>
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, null, new RectF32(0, 0, width, height), null, screen);

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome, IdeasScreen screen)
    {
        Draw(chrome, screen);
        var regions = new List<ClickableRegion>();
        chrome.Ideas.CollectPaintedRegions(regions);
        return regions;
    }

    private static void Click(List<ClickableRegion> regions, string action) =>
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);

    private static IEnumerable<string> Actions(List<ClickableRegion> regions) =>
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action);

    // What the page says: a sentence is a leaf per word, each ending in its space, so the leaves read together as it was written.
    private static string Spoken(RgbaImageRenderer renderer, PilotChrome<RgbaImage> chrome, IdeasScreen screen, float width = 1280)
    {
        var arranged = Layout.Engine.Arrange(chrome.Ideas.Tree(screen), new Rect<float>(0, 0, width, 6000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return string.Concat(arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value));
    }

    [Theory]
    [InlineData("List")]
    [InlineData("Canvas")]
    [InlineData("Tree")]
    public void TheMarkTheDesignLinkAndTheButtonShowInEveryView(string view)
    {
        var mode = Enum.Parse<IdeasViewMode>(view);
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Ideas.ViewMode = mode;
        var screen = PageData.DesignIdeas();
        if (mode == IdeasViewMode.Tree)
        {
            Draw(chrome, screen);
            chrome.Ideas.Select(chrome.Ideas.Board.Find("pilot#381"));
        }

        var said = Spoken(renderer, chrome, screen);
        said.ShouldContain("◆ needs design");
        // #1185: every design of an idea is a link, with its title where the view has the room for one (not in the tree grid's row).
        said.ShouldContain(mode == IdeasViewMode.Tree ? "DSN-1 · v2" : "DSN-1 Design 0 · v2");
        said.ShouldContain("DSN-2 · no version yet");
        var actions = Actions(Regions(chrome, screen)).ToList();
        actions.ShouldContain(mode == IdeasViewMode.Tree ? "ideas:tree-design:pilot I381:DSN-1" : "ideas:design:pilot I381:DSN-1");
        if (mode == IdeasViewMode.Tree)
        {
            // The grid's detail is of the picked row: I381 is marked, so its button unmarks.
            said.ShouldContain(IdeaDesignMark<RgbaImage>.UnmarkLabel);
            actions.ShouldContain("ideas:design-mark:pilot#381");
            return;
        }
        said.ShouldContain(IdeaDesignMark<RgbaImage>.MarkLabel);
        said.ShouldContain(IdeaDesignMark<RgbaImage>.UnmarkLabel);
        foreach (var key in new[] { "pilot#381", "pilot#382", "pilot#383", "pilot#384" })
        {
            actions.ShouldContain($"ideas:design-mark:{key}");
        }
    }

    [Theory]
    [InlineData("List")]
    [InlineData("Canvas")]
    [InlineData("Tree")]
    public async Task TheTerminalHostDrawsTheSameMarkInEveryView(string view)
    {
        var mode = Enum.Parse<IdeasViewMode>(view);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        await TerminalWireframeSweepTests.RecordCase("ideas", "sample", 200, 60, buffer, chrome =>
        {
            chrome.Ideas.Send = (_, _, _, _) => Task.FromResult(new IdeaReply(true));
            chrome.Ideas.ViewMode = mode;
            if (mode == IdeasViewMode.Tree)
            {
                chrome.Ideas.Tree(PageData.DesignIdeas());
                chrome.Ideas.Select(chrome.Ideas.Board.Find("pilot#381"));
            }
        }, PageData.DesignIdeas());
        var text = string.Join("\n", Enumerable.Range(0, 60).Select(y => string.Concat(Enumerable.Range(0, 200).Select(x => buffer.BackAt(x, y).Glyph)).TrimEnd()));
        text.ShouldContain("needs design");
        text.ShouldContain(mode == IdeasViewMode.Tree ? "DSN-1 · v2" : "DSN-1 Design 0 · v2");
        text.ShouldContain(mode == IdeasViewMode.Tree ? IdeaDesignMark<RgbaImage>.UnmarkLabel : IdeaDesignMark<RgbaImage>.MarkLabel);
    }

    [Fact]
    public async Task PressingTheButtonSendsTheMarkVerbAndOffWhenMarked()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, tasks) = Chrome(renderer);
        var screen = PageData.DesignIdeas();

        Click(Regions(chrome, screen), "ideas:design-mark:pilot#383");
        await Task.WhenAll(tasks);
        Click(Regions(chrome, screen), "ideas:design-mark:pilot#381");
        await Task.WhenAll(tasks);

        sent.Count(s => s.Verb == Verbs.MarkDesign).ShouldBe(2);
        sent.Single(s => s.Verb == Verbs.MarkDesign && s.Target == IdeaIds.Target("pilot", 383)).Args.ShouldBeEmpty();
        sent.Single(s => s.Verb == Verbs.MarkDesign && s.Target == IdeaIds.Target("pilot", 381)).Args.ShouldBe([Verbs.PinOff]);
    }

    [Fact]
    public void TheFilterLeavesOnlyTheIdeasThatNeedADesign()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = PageData.DesignIdeas();
        Spoken(renderer, chrome, screen).ShouldContain("Night mode for the rail");

        Click(Regions(chrome, screen), "ideas:filter-design");
        chrome.Ideas.OnlyNeedsDesign.ShouldBeTrue();
        var filtered = Spoken(renderer, chrome, screen);
        filtered.ShouldNotContain("Night mode for the rail");
        filtered.ShouldContain("Ideas pane");
        filtered.ShouldContain("Rail count");
        filtered.ShouldContain("Export the flights page");
        chrome.Ideas.Board.Unsorted.Items.Select(i => i.Key).ShouldBe(["pilot#384", "pilot#382", "pilot#381"], ignoreOrder: true);

        Click(Regions(chrome, screen), "ideas:filter-design");
        chrome.Ideas.OnlyNeedsDesign.ShouldBeFalse();
        Spoken(renderer, chrome, screen).ShouldContain("Night mode for the rail");
    }

    [Fact]
    public void TheDesignLinkOpensTheDesignsPageOnThatDesign()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        Click(Regions(chrome, PageData.DesignIdeas()), "ideas:design:pilot I381:DSN-1");
        chrome.Page.ShouldBe(PilotPage.Designs);
        chrome.Designs.OpenId.ShouldBe("DSN-1");
    }

    [Theory]
    [InlineData(382)]
    [InlineData(384)]
    public void PlanOnAMarkedIdeaWithNoVersionAsksFirst(int number)
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, _) = Chrome(renderer);
        var screen = PageData.DesignIdeas();
        Click(Regions(chrome, screen), $"ideas:plan:pilot I{number}");

        var said = Spoken(renderer, chrome, screen);
        said.ShouldContain($"I{number} needs a design and has none yet");
        var actions = Actions(Regions(chrome, screen)).ToList();
        actions.ShouldContain($"ideas:design-start:pilot I{number}");
        actions.ShouldContain($"ideas:plan-anyway:pilot I{number}");
        said.ShouldContain(IdeaDesignMark<RgbaImage>.StartLabel);
        said.ShouldContain(IdeaDesignMark<RgbaImage>.PlanAnywayLabel);
        // The milestone list waits for the answer: nothing was asked of the node.
        sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(381)]
    [InlineData(383)]
    public void PlanOnAnIdeaWithAVersionOrNoMarkAsksNothing(int number)
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = PageData.DesignIdeas();
        Click(Regions(chrome, screen), $"ideas:plan:pilot I{number}");
        Spoken(renderer, chrome, screen).ShouldNotContain("needs a design and has none yet");
        Actions(Regions(chrome, screen)).ShouldNotContain($"ideas:plan-anyway:pilot I{number}");
    }

    [Fact]
    public async Task StartItsDesignSendsTheDesignFromIdeaVerbAndOpensTheNewDesign()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, tasks) = Chrome(renderer, new IdeaReply(true, Made: "DSN-9"));
        var screen = PageData.DesignIdeas();
        Click(Regions(chrome, screen), "ideas:plan:pilot I382");
        Click(Regions(chrome, screen), "ideas:design-start:pilot I382");
        await Task.WhenAll(tasks);

        var start = sent.ShouldHaveSingleItem();
        start.Verb.ShouldBe(Verbs.DesignFromIdea);
        start.Target.ShouldBe(IdeaIds.Target("pilot", 382));
        start.Args.ShouldBeEmpty();
        chrome.Page.ShouldBe(PilotPage.Designs);
        chrome.Designs.OpenId.ShouldBe("DSN-9");
    }

    [Fact]
    public async Task PlanAnywayGoesToTheMilestoneListAndPlansWithTheAnywayArgument()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, tasks) = Chrome(renderer);
        var screen = PageData.DesignIdeas();
        Click(Regions(chrome, screen), "ideas:plan:pilot I382");
        Click(Regions(chrome, screen), "ideas:plan-anyway:pilot I382");
        await Task.WhenAll(tasks);
        sent.Select(s => s.Verb).ShouldContain(Verbs.ProposeMilestone);

        chrome.Ideas.RowInput.Text = "Designs";
        Click(Regions(chrome, screen), "ideas:plan-go:pilot I382");
        await Task.WhenAll(tasks);

        var plan = sent.Single(s => s.Verb == Verbs.PlanIdea);
        plan.Target.ShouldBe(IdeaIds.Target("pilot", 382));
        plan.Args.ShouldBe([Verbs.NewMilestoneArg, "Designs", Verbs.AnywayFlag]);
    }

    [Fact]
    public void ThePlanOfAnIdeaThatAskedIsNotSentAnywayAfterItWasCancelled()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = PageData.DesignIdeas();
        Click(Regions(chrome, screen), "ideas:plan:pilot I382");
        Click(Regions(chrome, screen), "ideas:plan-anyway:pilot I382");
        Click(Regions(chrome, screen), "ideas:cancel:pilot I382");
        // Plan again: the question comes back, the earlier anyway is gone.
        Click(Regions(chrome, screen), "ideas:plan:pilot I382");
        Spoken(renderer, chrome, screen).ShouldContain("I382 needs a design and has none yet");
    }

    [Fact]
    public void ButtonLabelsAreTwoToFiveWords()
    {
        foreach (var label in new[] { IdeaDesignMark<RgbaImage>.MarkLabel, IdeaDesignMark<RgbaImage>.UnmarkLabel, IdeaDesignMark<RgbaImage>.StartLabel, IdeaDesignMark<RgbaImage>.PlanAnywayLabel, IdeaDesignMark<RgbaImage>.FilterLabel })
        {
            label.Split(' ').Length.ShouldBeInRange(2, 5, label);
        }
    }

    [Fact]
    public void CapturesOfTheIdeasPageBeforeAndAfterTheMark()
    {
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS");
        foreach (var (size, width, height) in new[] { ("narrow", 480, 1500), ("wide", 1280, 1100) })
        {
            foreach (var (tag, marked) in new[] { ("before", false), ("after", true) })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (chrome, _, _) = Chrome(renderer);
                var screen = PageData.DesignIdeas(marked);
                Draw(chrome, screen, width, height);
                if (artifacts is { Length: > 0 })
                {
                    Directory.CreateDirectory(artifacts);
                    File.WriteAllBytes(Path.Combine(artifacts, $"{tag}-ideas-pilot-gui-{size}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
                }
            }
        }
    }

    [Fact]
    public async Task CapturesOfTheIdeasPageInTheTerminalBeforeAndAfterTheMark()
    {
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS");
        foreach (var (tag, marked) in new[] { ("before", false), ("after", true) })
        {
            var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            await TerminalWireframeSweepTests.RecordCase("ideas", "sample", 140, 44, buffer, chrome => chrome.Ideas.Send = (_, _, _, _) => Task.FromResult(new IdeaReply(true)), PageData.DesignIdeas(marked));
            var lines = Enumerable.Range(0, 44).Select(y => string.Concat(Enumerable.Range(0, 140).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();
            var text = string.Join(Environment.NewLine, lines.Select(l => l.TrimEnd()));
            text.Contains("needs design", StringComparison.Ordinal).ShouldBe(marked);
            if (artifacts is { Length: > 0 })
            {
                Directory.CreateDirectory(artifacts);
                File.WriteAllBytes(Path.Combine(artifacts, $"{tag}-ideas-pilot-tui.png"), DesignsWaitingIdeasTests.Png(lines));
            }
        }
    }
}
