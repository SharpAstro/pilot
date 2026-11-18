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
/// The Designs page's "Ideas waiting for a design" list (PIL1152) and a design's link to the idea it came from, in the pixel and the terminal host from one
/// declaration. Drawn on the CPU from PageData's sample: I382 is marked and has no design (so it waits), I381 and I384 are marked and have one, I383 is not marked;
/// the first design was started from I381.
/// </summary>
public sealed class DesignsWaitingIdeasTests
{
    private const int Columns = 120;
    private const int Rows = 40;
    private static readonly DateTimeOffset Now = PageData.Now;

    private sealed class Host(bool terminal) : IDisposable
    {
        private readonly RgbaImageRenderer _renderer = new(1280, 900);

        public bool Terminal { get; } = terminal;

        public PilotChrome<RgbaImage> Make()
        {
            PageTime.Zone = TimeZoneInfo.Utc;
            var chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.WithoutScrollBars();
            chrome.Show(PilotPage.Designs);
            if (Terminal)
            {
                chrome.Designs.Terminal = (Columns, Rows);
            }
            return chrome;
        }

        /// <summary>Every node of the page arranged, in the room the host gives it.</summary>
        public List<Layout.Node> Nodes(PilotChrome<RgbaImage> chrome, DesignsScreen screen, float width = 1280f)
        {
            var tree = chrome.Designs.Tree(screen);
            if (Terminal)
            {
                var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, Now, _ => { }, null, Columns);
                return [.. TerminalFrame.Build(rail, tree, null, Columns, Rows, CellMeasureContext.PixelAuthored).Select(a => a.Node)];
            }
            return [.. Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, 900f), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).Select(a => a.Node)];
        }

        public IEnumerable<ArrangedHit> Hits(PilotChrome<RgbaImage> chrome, DesignsScreen screen)
        {
            if (Terminal)
            {
                var rail = TerminalRail.Tree(PilotPage.Designs, GlyphMode.Emoji, null, null, Now, _ => { }, null, Columns);
                var cells = TerminalFrame.Build(rail, chrome.Designs.Tree(screen), null, Columns, Rows, CellMeasureContext.PixelAuthored);
                return cells.Where(a => a.Node.Hit is HitResult.ButtonHit).Select(a => new ArrangedHit(((HitResult.ButtonHit)a.Node.Hit!).Action, () => CellLayout.HitTest(cells, a.Bounds.X + 1, a.Bounds.Y)));
            }
            // The regions a frame paints, as a click reaches them.
            chrome.Render(new SessionsScreen([], "test", Now), null, null, null, null, null, null, new RectF32(0, 0, 1280, 900), null, null, null, screen);
            var regions = new List<ClickableRegion>();
            chrome.Designs.CollectPaintedRegions(regions);
            return regions.Where(r => r.Result is HitResult.ButtonHit).Select(r => new ArrangedHit(((HitResult.ButtonHit)r.Result!).Action, () => r.OnClick!.Invoke(InputModifier.None)));
        }

        public void Dispose() => _renderer.Dispose();
    }

    private sealed record ArrangedHit(string Action, Action Press);

    private static List<string> Said(IEnumerable<Layout.Node> nodes) =>
        SentenceRuns.Texts(nodes);

    private static DesignsScreen Sample() => PageData.DesignsFor("sample", Now);

    public static TheoryData<bool> Hosts => new() { false, true };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void TheListShowsOnlyTheMarkedIdeaWithNoDesign(bool terminal)
    {
        using var host = new Host(terminal);
        var chrome = host.Make();
        var said = Said(host.Nodes(chrome, Sample()));

        said.ShouldContain("Ideas waiting for a design");
        said.ShouldContain(t => t.Contains("I382", StringComparison.Ordinal) && t.Contains("Rail count", StringComparison.Ordinal));
        // I381 and I384 have a design, I383 is not marked: none of them waits.
        said.ShouldNotContain(t => t.Contains("Ideas pane", StringComparison.Ordinal));
        said.ShouldNotContain(t => t.Contains("Night mode", StringComparison.Ordinal));
        said.ShouldNotContain(t => t.Contains("Export the flights", StringComparison.Ordinal));
        host.Hits(chrome, Sample()).Count(h => h.Action.StartsWith("designs:idea-start:", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void TheSectionIsNotThereWhenNoIdeaWaits(bool terminal)
    {
        using var host = new Host(terminal);
        var chrome = host.Make();
        var none = Sample() with { Ideas = [.. Sample().Ideas!.Where(i => i.Id != "I382")] };
        Said(host.Nodes(chrome, none)).ShouldNotContain("Ideas waiting for a design");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ItsButtonAsksForTheDesignOnTheNodeThePageShowsAndTheMadeDesignOpens(bool terminal)
    {
        using var host = new Host(terminal);
        var chrome = host.Make();
        chrome.Nodes.Choices = [new NodeMark("a", "a", "DE1", true), new NodeMark("b", "b", "SE1", false)];
        chrome.Nodes.All.ShouldBeTrue();
        (IdeaRow Idea, string Node)? asked = null;
        chrome.Designs.OnStartIdea = (idea, node) => asked = (idea, node);
        var screen = Sample();

        host.Hits(chrome, screen).Single(h => h.Action == "designs:idea-start:pilot I382").Press();

        var (idea, node) = asked.ShouldNotBeNull();
        idea.Qualified.ShouldBe("pilot I382");
        node.ShouldBe("DE1", "under All nodes the design is made on this window's node, as Start a design is");
        // The target the host sends the verb to is the idea's own.
        IdeaIds.Target(idea.Repo, idea.Number).ShouldBe(IdeaIds.Target("pilot", 382));
        // Pressed, the button reads Starting… and takes no second press.
        Said(host.Nodes(chrome, screen)).ShouldContain("Starting…");
        host.Hits(chrome, screen).Select(h => h.Action).ShouldNotContain("designs:idea-start:pilot I382");

        chrome.Designs.StartedFromIdea("pilot I382", "DSN-9");
        chrome.Designs.OpenId.ShouldBeNull("the answer waits for the render thread's next frame");
        host.Nodes(chrome, screen);
        chrome.Designs.OpenId.ShouldBe("DSN-9");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ARefusalIsSaidUnderTheIdeaAndTheButtonComesBack(bool terminal)
    {
        using var host = new Host(terminal);
        var chrome = host.Make();
        chrome.Designs.OnStartIdea = (_, _) => { };
        var screen = Sample();
        host.Hits(chrome, screen).Single(h => h.Action == "designs:idea-start:pilot I382").Press();
        chrome.Designs.StartFailedFromIdea("pilot I382", "I382 is not marked as needing a design");

        var said = Said(host.Nodes(chrome, screen));
        said.ShouldContain("I382 is not marked as needing a design");
        chrome.Designs.OpenId.ShouldBeNull();
        host.Hits(chrome, screen).Select(h => h.Action).ShouldContain("designs:idea-start:pilot I382");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ADesignMadeFromAnIdeaShowsItAsALinkThatOpensTheIdeasPage(bool terminal)
    {
        using var host = new Host(terminal);
        var chrome = host.Make();
        var screen = Sample();
        chrome.Designs.Open(screen.Open!.Detail.Row.Id);
        screen.Open.Detail.Row.FromIdea.ShouldBe("pilot I381");

        Said(host.Nodes(chrome, screen)).ShouldContain("I381");
        host.Hits(chrome, screen).Single(h => h.Action == "designs:for-idea:pilot:I381").Press();

        chrome.Page.ShouldBe(PilotPage.Ideas);
        chrome.Ideas.ShowingIdea.ShouldBe("pilot I381");
        chrome.Ideas.ViewMode.ShouldBe(IdeasViewMode.Tree);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ADroppedIdeaStillReadsByItsIdOnTheDesign(bool terminal)
    {
        using var host = new Host(terminal);
        var chrome = host.Make();
        var screen = Sample();
        // The idea is gone from the snapshot (dropped or folded away): the design remembers it, and its id is still there.
        var gone = screen with { Ideas = [], Open = screen.Open! with { Detail = screen.Open.Detail with { Row = screen.Open.Detail.Row with { FromIdea = "pilot I999" } } } };
        chrome.Designs.Open(gone.Open!.Detail.Row.Id);

        Said(host.Nodes(chrome, gone)).ShouldContain("pilot I999");
    }

    [Fact]
    public void TheIdeasPageSelectsTheIdeaTheDesignCameFrom()
    {
        using var host = new Host(false);
        var chrome = host.Make();
        chrome.Ideas.Send = (_, _, _, _) => Task.FromResult(new IdeaReply(true));
        var screen = PageData.DesignIdeas();
        chrome.Designs.OnIdea!("pilot I381");
        chrome.Page.ShouldBe(PilotPage.Ideas);
        chrome.Render(new SessionsScreen([], "test", Now), null, null, null, null, null, new RectF32(0, 0, 1280, 900), null, screen);
        chrome.Ideas.Selected.ShouldBe("pilot#381");
        chrome.Ideas.ShowingIdea.ShouldBeNull();
    }

    [Fact]
    public void ABriefFromAnIdeaLeavesTheTypedBriefAlone()
    {
        using var host = new Host(false);
        var chrome = host.Make();
        chrome.Designs.OnStartIdea = (_, _) => { };
        chrome.Designs.BriefInput.Text = "half typed";
        var screen = Sample();
        host.Hits(chrome, screen).Single(h => h.Action == "designs:idea-start:pilot I382").Press();
        chrome.Designs.StartedFromIdea("pilot I382", "DSN-9");
        host.Nodes(chrome, screen);
        chrome.Designs.BriefInput.Text.ShouldBe("half typed");
    }

    [Fact]
    public void TheButtonLabelIsTwoToFiveWords() =>
        IdeaDesignMark<RgbaImage>.StartLabel.Split(' ').Length.ShouldBeInRange(2, 5);

    // ---- Captures ----

    private static void Keep(string name, byte[] png)
    {
        if (Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            File.WriteAllBytes(Path.Combine(artifacts, name), png);
        }
    }

    [Theory]
    [InlineData("narrow", 480, 900)]
    [InlineData("wide", 1280, 900)]
    public void CapturesOfTheDesignsPageInPilotGui(string size, int width, int height)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        var folder = Path.Combine(Path.GetTempPath(), $"pilot-waiting-capture-{Guid.NewGuid():N}");
        try
        {
            foreach (var (name, open) in new[] { ("designs-waiting-ideas", (string?)null), ("design-from-idea", "DSN-1") })
            {
                var png = Path.Combine(folder, $"{name}-{size}.png");
                // The dump lists the designs, not the ideas section; what the page says is held by the tests above, and the picture is for the eye.
                SessionsCapture.Write(new SessionsScreen([], "test", Now), png, width, height, page: PilotPage.Designs, designs: PageData.DesignsFor("long", Now), openDesign: open);
                File.Exists(png).ShouldBeTrue();
                Keep($"after-{name}-pilot-gui-{size}.png", File.ReadAllBytes(png));
            }
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CapturesOfTheDesignsPageInPilotTui()
    {
        // The sweep's own recorder draws the page as Program.cs does, from the sample, which carries the ideas and the design made from one.
        foreach (var (name, page, open) in new[] { ("designs-waiting-ideas", "designs", false), ("design-from-idea", "design page", true) })
        {
            var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            await TerminalWireframeSweepTests.RecordCase(page, "long", Columns, Rows, buffer);
            var lines = Enumerable.Range(0, Rows).Select(y => string.Concat(Enumerable.Range(0, Columns).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();
            string.Join('\n', lines).ShouldContain(open ? "I381" : "Ideas waiting for a design");
            Keep($"after-{name}-pilot-tui.png", Png(lines));
        }
    }

    // The terminal's cells as a picture: one glyph a cell on a dark ground, in the mono face where the build has it, so the columns line up as they do in the terminal.
    internal static byte[] Png(string[] lines)
    {
        const int cw = 9, ch = 18;
        var width = lines.Max(l => l.Length) * cw;
        var height = lines.Length * ch;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var mono = Path.Combine(Path.GetDirectoryName(SessionsCapture.FontPath)!, "DejaVuSansMono.ttf");
        var font = File.Exists(mono) ? mono : SessionsCapture.FontPath;
        renderer.FillRectangle(new RectInt(new PointInt(width, height), new PointInt(0, 0)), new RGBAColor32(24, 24, 28, 255));
        for (var y = 0; y < lines.Length; y++)
        {
            for (var x = 0; x < lines[y].Length; x++)
            {
                if (lines[y][x] != ' ')
                {
                    renderer.DrawText(lines[y][x].ToString(), font, 13f, new RGBAColor32(220, 220, 220, 255),
                        new RectInt(new PointInt((x + 1) * cw, (y + 1) * ch), new PointInt(x * cw, y * ch)), TextAlign.Center, TextAlign.Center);
                }
            }
        }
        return PngWriter.Encode(renderer.Surface.Pixels, width, height);
    }
}
