using DIR.Lib;
using DIR.Lib.Prototyping;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Designs page, drawn on the CPU from the sample prototype with no node: the list, a design's page with the prototype played live, a click in the
/// player that is the prototype's, a click on the page's own controls that is not, and the sizes the node checks a design at held to the GUI's sweep.
/// </summary>
public sealed class DesignsViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static DesignsScreen Sample(string state = "sample") => PageData.DesignsFor(state, Now);

    private sealed class Rig : IDisposable
    {
        private readonly RectF32 _bounds = new(0, 0, 1400, 900);
        private readonly RgbaImageRenderer _renderer = new(1400, 900);

        public Rig(DesignsScreen screen)
        {
            // The page shows local time: the tests that read a time off it run in UTC unless one sets a zone after its rig.
            PageTime.Zone = TimeZoneInfo.Utc;
            Screen = screen;
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            Chrome.Show(PilotPage.Designs);
            Router = new InputRouter(Chrome.Designs.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
        }

        public DesignsScreen Screen { get; }
        public RgbaImageRenderer Renderer => _renderer;
        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }
        public DesignsView<RgbaImage> View => Chrome.Designs;

        public void Paint()
        {
            var data = PageData.For("sample");
            Chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, _bounds, data.Machines, null, data.Spend, Screen);
        }

        public ClickableRegion Region(IEnumerable<ClickableRegion> regions, Func<HitResult, bool> match) =>
            regions.Single(r => r.Result is { } hit && match(hit));

        public void Click(ClickableRegion region)
        {
            var x = region.X + region.Width / 2f;
            var y = region.Y + region.Height / 2f;
            Router.Handle(new InputEvent.MouseDown(x, y, MouseButton.Left, InputModifier.None, 1));
            Router.Handle(new InputEvent.MouseUp(x, y, MouseButton.Left));
            Paint();
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static List<ClickableRegion> Painted<TView>(TView view) where TView : PixelWidgetBase<RgbaImage>
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions;
    }

    private static List<string> TextsOf(Rig rig) =>
        SentenceRuns.Texts(rig.View.Tree(rig.Screen) is { } tree ? Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 1400, 900), new PixelMeasureContext<RgbaImage>(rig.Renderer, SessionsCapture.FontPath, 1f)).Select(a => a.Node) : []);

    private static (Layout.ArrangedNode<float> Input, Layout.ArrangedNode<float> Button) StartRow(IEnumerable<Layout.ArrangedNode<float>> arranged)
    {
        var all = arranged.ToList();
        var input = all.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput });
        var button = all.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:start" });
        return (input, button);
    }

    private static List<Layout.ArrangedNode<float>> ArrangeAt(Rig rig, float width, float height) =>
        [.. Layout.Engine.Arrange(rig.View.Tree(rig.Screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(rig.Renderer, SessionsCapture.FontPath, 1f))];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBriefBoxAndStartButtonShowOnTheEmptyPageAndAboveTheListOnOneRow(bool empty)
    {
        using var rig = new Rig(empty ? DesignsScreen.Empty("sample", Now) : Sample());
        rig.Paint();

        var texts = TextsOf(rig);
        texts.ShouldContain("Start a design");
        texts.ShouldNotContain(t => t.Contains("pilot design new", StringComparison.Ordinal));
        var (input, button) = StartRow(ArrangeAt(rig, 1400, 900));
        button.Bounds.Height.ShouldBe(input.Bounds.Height, "one height");
        (button.Bounds.Y + button.Bounds.Height / 2f).ShouldBe(input.Bounds.Y + input.Bounds.Height / 2f, 0.5f, "one middle");
        if (!empty)
        {
            texts.ShouldContain("DSN-1  Design 0");
        }
    }

    [Fact]
    public void TheStartButtonIsAsWideAsItsLabelPlusPaddingAndTheInputTakesTheRestOfTheRow()
    {
        using var rig = new Rig(DesignsScreen.Empty("sample", Now));
        var all = ArrangeAt(rig, 1600, 900);
        var (input, button) = StartRow(all);
        var label = all.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value =="Start a design");
        button.Bounds.Width.ShouldBeLessThan(label.Bounds.Width + 40f, "the button is its label plus padding, not half the row");
        button.Bounds.Width.ShouldBeLessThan(250f);
        button.Bounds.Height.ShouldBe(input.Bounds.Height, "one height");
        (button.Bounds.Y + button.Bounds.Height / 2f).ShouldBe(input.Bounds.Y + input.Bounds.Height / 2f, 0.5f, "one middle");
        (input.Bounds.X + input.Bounds.Width + 6f).ShouldBe(button.Bounds.X, 1f, "the input fills the row up to the button");
        (button.Bounds.X + button.Bounds.Width).ShouldBeGreaterThan(1500f);
    }

    [Fact]
    public void TheStartBoxHoldsAtANarrowWidthWithoutCuttingTheButton()
    {
        using var rig = new Rig(DesignsScreen.Empty("sample", Now));
        var (input, button) = StartRow(ArrangeAt(rig, 360, 700));
        button.Bounds.X.ShouldBeGreaterThan(input.Bounds.X);
        (button.Bounds.X + button.Bounds.Width).ShouldBeLessThanOrEqualTo(360f);
        input.Bounds.Width.ShouldBeGreaterThan(40f);
    }

    [Fact]
    public void TypingABriefAndPressingStartAsksTheHostForDesignNewOnTheNodeThenOpensTheMadeDesign()
    {
        using var rig = new Rig(Sample());
        rig.View.Nodes = rig.Chrome.Nodes;
        rig.Chrome.Nodes.Choices = [new NodeMark("a", "a", "DE1", true), new NodeMark("b", "b", "SE1", false)];
        rig.Chrome.Nodes.All.ShouldBeTrue();
        (string Brief, string Node)? asked = null;
        rig.View.OnStart = (brief, node) => asked = (brief, node);
        rig.Paint();
        TextsOf(rig).ShouldContain("Starts on DE1", "All nodes names the node it will use");

        rig.View.BriefInput.Text = "A settings page for the dome";
        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:start" }));

        asked.ShouldBe(("A settings page for the dome", "DE1"));
        // Pressed, the button reads Starting… with no hit until the host answers: a second press is not taken.
        rig.Paint();
        TextsOf(rig).ShouldContain("Starting…");
        Painted(rig.View).Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldNotContain("designs:start");
        rig.View.Started("DSN-9");
        // The answer is applied by the render thread at the next frame, not by the thread that gave it.
        rig.View.OpenId.ShouldBeNull();
        rig.Paint();
        rig.View.OpenId.ShouldBe("DSN-9");
        rig.View.BriefInput.Text.ShouldBe("");
    }

    [Fact]
    public void AnEmptyBriefAsksNothingAndARefusalKeepsTheBriefAndSaysWhy()
    {
        using var rig = new Rig(Sample());
        var asked = 0;
        rig.View.OnStart = (_, _) => asked++;
        rig.View.Start().ShouldBeFalse();
        asked.ShouldBe(0);
        rig.View.StartNote.ShouldBe("Type a brief first.");

        rig.View.BriefInput.Text = "brief";
        rig.View.Start().ShouldBeTrue();
        rig.View.StartFailed("no repo");
        rig.Paint();
        rig.View.BriefInput.Text.ShouldBe("brief");
        rig.Paint();
        TextsOf(rig).ShouldContain("no repo");
    }

    [Fact]
    public void ShowTakesTheWindowToTheDesignsPageAndRenderKeepsIt()
    {
        using var rig = new Rig(Sample());
        rig.Chrome.Page.ShouldBe(PilotPage.Designs, "after Show");
        rig.Paint();
        rig.Chrome.Page.ShouldBe(PilotPage.Designs, "after Render");
    }

    [Fact]
    public void TheListShowsTheDesignsWithTheirVersionRepoAndTime()
    {
        using var rig = new Rig(Sample());
        rig.Paint();

        var texts = TextsOf(rig);
        texts.ShouldContain("DSN-1  Design 0");
        texts.ShouldContain("v2 · SharpAstro/pilot · 2026-11-16 09:00");
        texts.ShouldContain(t => t.StartsWith("3 designs", StringComparison.Ordinal));
    }

    [Fact]
    public void ADesignWithNoVersionSaysSoAndShowsItsBrief()
    {
        var full = Sample();
        var detail = full.Open!.Detail with { Row = full.Open.Detail.Row with { Current = null, Versions = 0 }, Versions = [] };
        using var rig = new Rig(full with { Open = DesignPages.Load(detail) });
        rig.View.Open("DSN-1");
        rig.Paint();

        rig.View.Player.ShouldBeNull();
        var texts = TextsOf(rig);
        texts.ShouldContain(t => t.StartsWith("This design has no version yet", StringComparison.Ordinal));
        texts.ShouldContain(t => t.Contains("Brief", StringComparison.Ordinal));
    }

    private static IEnumerable<Layout.ArrangedNode<float>> ArrangedAt(Rig rig, float width, float height, float dpi = 1f) =>
        Layout.Engine.Arrange(rig.View.Tree(rig.Screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(rig.Renderer, SessionsCapture.FontPath, dpi));

    [Theory]
    [InlineData(360)]
    [InlineData(700)]
    [InlineData(1000)]
    [InlineData(1400)]
    public void ALongTitleAndANoVersionTextAreWrappedOrCutWithTheWholeOnHover(int width)
    {
        var title = "A very long design title that goes on and on about the whole of the observatory control panel";
        var full = Sample();
        var detail = full.Open!.Detail with { Row = full.Open.Detail.Row with { Title = title, Current = null, Versions = 0 }, Versions = [], Brief = string.Join(' ', Enumerable.Repeat("A long brief sentence.", 40)) };
        using var rig = new Rig(full with { Open = DesignPages.Load(detail) });
        rig.View.Open("DSN-1");
        rig.Paint();

        var arranged = ArrangedAt(rig, width, 900).ToList();
        var counts = $"DSN-1 · {title}";
        arranged.ShouldContain(a => a.Node.Tooltip == counts, "the title line carries its whole text on hover");
        arranged.Any(a => a.Node.Tooltip == null && a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == counts).ShouldBeFalse("never one plain leaf that a column clips with no ellipsis");
        arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.StartsWith("This design has no version yet. The brief", StringComparison.Ordinal)).ShouldBeFalse("the no-version text is words that wrap, not one line");
    }

    [Theory]
    [InlineData(2880, 1814, 1.5f)]
    [InlineData(2880, 1814, 1f)]
    [InlineData(1920, 1080, 1f)]
    public void TheIssuesTitleLineIsWholeOrEndsInAnEllipsisAtTheIssuesWindow(int width, int height, float dpi)
    {
        // The title is made from the brief by the store's own rule, as DesignStore.New makes it: the issue's first line ran past 80
        // characters and the store cut it mid-word ("a line sayin") before the page ever drew it.
        var firstLine = "A card for one job: its id j6917 and the command on the first line, a line saying what it is doing and the whole of its output";
        var brief = firstLine + "\nThe card sits on the job's page.";
        var title = DesignTitle.Of(brief);
        title.ShouldBe(firstLine, "a first line this long is kept whole, so the page's one-line cut and its hover have all of it");
        var full = Sample();
        var detail = full.Open!.Detail with { Row = full.Open.Detail.Row with { Title = title }, Brief = brief };
        using var rig = new Rig(full with { Open = DesignPages.Load(detail) });
        rig.View.Open("DSN-1");
        rig.Paint();
        var counts = $"DSN-1 · {title}";
        var arranged = ArrangedAt(rig, width, height, dpi).ToList();
        var line = arranged.Single(a => a.Node.Tooltip == counts);
        var words = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && a.Bounds.Y >= line.Bounds.Y - 0.5f && a.Bounds.Y + a.Bounds.Height <= line.Bounds.Y + line.Bounds.Height + 0.5f
            && a.Bounds.X >= line.Bounds.X - 0.5f && a.Bounds.X <= line.Bounds.X + line.Bounds.Width + 0.5f).Select(a => (Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Select(t => t.Value).ToList();
        var drawn = string.Concat(words).Replace(" ", "");
        (drawn.EndsWith("…", StringComparison.Ordinal) || drawn == counts.Replace(" ", "")).ShouldBeTrue($"the title line is whole or ends in an ellipsis, drew: {string.Join("|", words)}");
    }

    [Fact]
    public void EveryTimeOnADesignsPageIsLocalWithUtcOnHover()
    {
        try
        {
            var full = Sample();
            var brief = $"## 2026-11-16T09:00:00Z{Environment.NewLine}{Environment.NewLine}A piece.";
            var detail = full.Open!.Detail with { Brief = brief };
            using var rig = new Rig(full with { Open = DesignPages.Load(detail) });
            PageTime.Zone = TimeZoneInfo.CreateCustomTimeZone("test+5:30", TimeSpan.FromHours(5.5), "test", "test");
            rig.Paint();
            var list = ArrangedAt(rig, 1400, 900).ToList();
            SentenceRuns.Texts(list.Select(a => a.Node)).ShouldContain("v2 · SharpAstro/pilot · 2026-11-16 14:30");
            list.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(t => t.EndsWith("2026-11-16 09:00:00 UTC", StringComparison.Ordinal));

            rig.View.Open("DSN-1");
            rig.Paint();
            var page = ArrangedAt(rig, 1400, 900).ToList();
            var texts = SentenceRuns.Texts(page.Select(a => a.Node));
            texts.ShouldContain(t => t.Contains("2026-11-16 14:30", StringComparison.Ordinal) && t.StartsWith("v", StringComparison.Ordinal), "the versions list");
            page.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(t => t.StartsWith("Play v", StringComparison.Ordinal) && t.EndsWith("09:00:00 UTC", StringComparison.Ordinal));
            PageTime.LocalHeadings(brief).ShouldStartWith("## 2026-11-16 14:30 (2026-11-16 09:00 UTC)");
            var hover = PageTime.Hover(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
            hover.ShouldContain("+05:30");
            hover.ShouldContain("09:00:00 UTC");
        }
        finally
        {
            PageTime.Zone = TimeZoneInfo.Local;
        }
    }

    [Fact]
    public void PointAtThisIsAsWideAsItsLabelPlusPaddingAndSharesOneHeightAndMiddleWithTheHeading()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        foreach (var width in new[] { 1000f, 1600f })
        {
            var all = ArrangedAt(rig, width, 900).ToList();
            var button = all.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:point" });
            var label = all.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == "Point at this");
            var heading = all.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == "Chat with the designer");
            // The label's width as the renderer measures it, not the leaf's bounds, which stretch with the button.
            var (measured, _) = rig.Renderer.MeasureText("Point at this", SessionsCapture.FontPath, 12f);
            button.Bounds.Width.ShouldBe(measured + 20f, 2f, "the measured label plus 10 either side, not a star share of the column");
            button.Bounds.Height.ShouldBe(heading.Bounds.Height, 0.5f, "one height");
            (button.Bounds.Y + button.Bounds.Height / 2f).ShouldBe(heading.Bounds.Y + heading.Bounds.Height / 2f, 0.5f, "one middle");
            (label.Bounds.Y + label.Bounds.Height / 2f).ShouldBe(heading.Bounds.Y + heading.Bounds.Height / 2f, 1f, "the label's middle is the heading text's middle");
        }
    }

    [Fact]
    public void ADesignsPageShowsThePlayerTheRendersTheVersionsAndTheNotes()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();

        rig.Chrome.Page.ShouldBe(PilotPage.Designs);
        rig.View.OpenId.ShouldBe("DSN-1");
        rig.View.Player.ShouldNotBeNull();
        rig.View.Player.CurrentScreen.Id.ShouldBe("home");
        var texts = TextsOf(rig);
        texts.ShouldContain("Versions");
        texts.ShouldContain(t => t.StartsWith("v2 (current)", StringComparison.Ordinal));
        texts.ShouldContain(t => t.Contains("Brief", StringComparison.Ordinal));
        texts.ShouldContain("Home");
        texts.ShouldContain("Settings");
        // The player is a widget after the page, so the router reaches it as it reaches any.
        rig.Chrome.Widgets.ShouldContain(rig.View.Player);
    }

    [Fact]
    public void AClickOnAGotoButtonInThePlayerChangesTheScreenShown()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        var player = rig.View.Player!;

        // The prototype's "Open settings" is a goto:settings action; the player registers it as a button.
        var open = Painted(player).Where(r => r.Result is HitResult.ButtonHit { Action: "goto:settings" } or HitResult.ButtonHit { Action: "settings" }).ToList();
        open = open.Count > 0 ? open : [.. Painted(player).Where(r => r.Result is HitResult.ButtonHit { Action: { } a } && a.Contains("settings", StringComparison.Ordinal))];
        open.ShouldNotBeEmpty("the prototype's goto button is among the player's regions");
        rig.Click(open[0]);

        rig.View.Player!.CurrentScreen.Id.ShouldBe("settings");
    }

    [Fact]
    public void AClickOnAPageControlIsNotThePrototypesAndLiesOutsideThePlayer()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        var player = rig.View.Player!;
        player.Goto("settings").ShouldBeTrue();
        rig.Paint();
        var screenBefore = player.CurrentScreen.Id;

        var versions = new List<int>();
        rig.View.OnVersion = (_, n) => versions.Add(n);
        var page = Painted(rig.View);
        var playerRects = Painted(player).ToList();

        var control = rig.Region(page, h => h is HitResult.ButtonHit { Action: "designs:version:1" });
        playerRects.ShouldAllBe(p => control.X + control.Width <= p.X || p.X + p.Width <= control.X || control.Y + control.Height <= p.Y || p.Y + p.Height <= control.Y);
        rig.Click(control);

        versions.ShouldBe([1]);
        rig.View.Player!.CurrentScreen.Id.ShouldBe(screenBefore);
    }

    [Fact]
    public void AClickOnAStripTileShowsThatScreenInThePlayer()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        rig.View.Player!.CurrentScreen.Id.ShouldBe("home");

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:screen:settings" }));

        rig.View.Player!.CurrentScreen.Id.ShouldBe("settings");
    }

    [Fact]
    public void AClickOnARowOpensItsDesign()
    {
        using var rig = new Rig(Sample());
        var opened = new List<string>();
        rig.View.OnOpen = opened.Add;
        rig.Paint();

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:open:DSN-2" }));

        opened.ShouldBe(["DSN-2"]);
        rig.View.OpenId.ShouldBe("DSN-2");
    }

    [Fact]
    public void APageReadFromAVersionsFolderLoadsItsPrototypeNotesAndRenders()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-design-" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = Path.Combine(folder, "versions", "v1");
            Directory.CreateDirectory(Path.Combine(version, "renders"));
            File.Copy(PageData.SamplePrototypePath, Path.Combine(version, "prototype.json"));
            File.WriteAllText(Path.Combine(version, "NOTES.md"), "# notes");
            File.WriteAllBytes(Path.Combine(version, "renders", "home.png"), [1]);
            var detail = new DesignDetail { Row = new DesignRow { Id = "DSN-9", Current = 1, Versions = 1 }, Folder = folder };

            var page = DesignPages.Load(detail);

            page.Version.ShouldBe(1);
            page.Document.ShouldNotBeNull();
            page.Notes.ShouldBe("# notes");
            page.Renders.Select(r => r.Screen).ShouldBe(["home", "settings", "confirm"]);
            page.Renders.Single(r => r.Screen == "home").File.ShouldNotBeNull();
            page.Renders.Single(r => r.Screen == "settings").File.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Rig CellsRig(string size)
    {
        var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        rig.View.ShowCells(true);
        rig.View.PickSize(size).ShouldBeTrue();
        rig.Paint();
        return rig;
    }

    private static void ClickAt(Rig rig, float x, float y)
    {
        rig.Router.Handle(new InputEvent.MouseDown(x, y, MouseButton.Left, InputModifier.None, 1));
        rig.Router.Handle(new InputEvent.MouseUp(x, y, MouseButton.Left));
        rig.Paint();
    }

    // The cell where a text of the grid starts: its column and row.
    private static (int Column, int Row) CellOf(Rig rig, string text)
    {
        var rows = rig.View.CellGrid!.Cells!.FrontRowText();
        for (var row = 0; row < rows.Length; row++)
        {
            if (rows[row].IndexOf(text, StringComparison.Ordinal) is var column and >= 0)
            {
                return (column, row);
            }
        }
        throw new ShouldAssertException($"'{text}' is not on the grid:{Environment.NewLine}{string.Join(Environment.NewLine, rows.Select(r => r.TrimEnd()))}");
    }

    [Fact]
    public void PixelsIsTheDefaultAndCellsListsTheGridInsteadOfThePlayer()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();

        rig.View.CellsShown.ShouldBeFalse();
        rig.Chrome.Widgets.ShouldContain(rig.View.Player);
        rig.View.ShowCells(true);
        rig.Paint();
        rig.Chrome.Widgets.ShouldContain(rig.View.CellGrid);
        rig.Chrome.Widgets.ShouldNotContain(rig.View.Player);
    }

    [Theory]
    [InlineData("80x24", 80, 24)]
    [InlineData("160x44", 160, 44)]
    public void CellsArrangesTheSampleScreenAtATerminalSizeAndFitsItAtOneToTwo(string size, int columns, int rows)
    {
        using var rig = CellsRig(size);
        var grid = rig.View.CellGrid!;

        grid.Columns.ShouldBe(columns);
        grid.Rows.ShouldBe(rows);
        grid.Cells!.Columns.ShouldBe(columns);
        grid.Cells.Rows.ShouldBe(rows);
        var text = grid.Cells.Text();
        text.ShouldContain("Fleet");
        text.ShouldContain("Open settings");
        // A cell is twice as tall as it is wide, and the grid lies inside the rectangle the page gave it.
        (grid.GridRect.Height / rows / (grid.GridRect.Width / columns)).ShouldBe(2f, 0.01f);
        grid.GridRect.Width.ShouldBeGreaterThan(0f);
    }

    [Fact]
    public void AFrameDoesNotRebuildTheArrangementUntilSomethingChanges()
    {
        using var rig = CellsRig("80x24");
        var grid = rig.View.CellGrid!;
        var builds = grid.Builds;

        rig.Paint();
        rig.Paint();
        grid.Builds.ShouldBe(builds);

        rig.View.PickSize("100x30");
        rig.Paint();
        grid.Builds.ShouldBe(builds + 1);
    }

    [Fact]
    public void AClickOnTheCellsOfAGotoTextMovesToItsScreen()
    {
        using var rig = CellsRig("80x24");
        var (column, row) = CellOf(rig, "Open settings");

        var (x, y) = rig.View.CellGrid!.CellCentre(column + 3, row);
        ClickAt(rig, x, y);

        rig.View.Player!.CurrentScreen.Id.ShouldBe("settings");
        rig.View.CellGrid!.Cells!.Text().ShouldContain("Delete this node");
    }

    [Fact]
    public void AKeyTypedIntoTheFocusedTextInputShowsInTheGrid()
    {
        using var rig = CellsRig("80x24");
        rig.View.Player!.Goto("settings").ShouldBeTrue();
        rig.Paint();
        var (column, row) = CellOf(rig, "Node name");

        var (x, y) = rig.View.CellGrid!.CellCentre(column + 2, row);
        ClickAt(rig, x, y);
        rig.Router.Handle(new InputEvent.TextInput("Z"));
        rig.Router.Handle(new InputEvent.TextInput("7"));
        rig.Paint();

        rig.View.CellGrid!.Cells!.Text().ShouldContain("Z7");
    }

    [Fact]
    public void SwitchingBackToPixelsKeepsTheScreenAndTheTypedValue()
    {
        using var rig = CellsRig("80x24");
        rig.View.Player!.Goto("settings").ShouldBeTrue();
        rig.Paint();
        var (column, row) = CellOf(rig, "Node name");
        var (x, y) = rig.View.CellGrid!.CellCentre(column + 2, row);
        ClickAt(rig, x, y);
        rig.Router.Handle(new InputEvent.TextInput("Q"));
        rig.Paint();

        rig.View.ShowCells(false);
        rig.Paint();
        rig.View.Player!.CurrentScreen.Id.ShouldBe("settings");
        rig.Chrome.Widgets.ShouldContain(rig.View.Player);
        rig.View.ShowCells(true);
        rig.Paint();
        rig.View.CellGrid!.Cells!.Text().ShouldContain("Q");
    }

    [Fact]
    public void ADesignNamingTerminalTargetsOffersThemFirstAndStartsAtTheFirst()
    {
        var full = Sample();
        var detail = full.Open!.Detail with { Row = full.Open.Detail.Row with { Terminals = ["120x36", "80x24"] } };
        using var rig = new Rig(full with { Open = full.Open with { Detail = detail } });
        rig.View.Open("DSN-1");
        rig.Paint();
        rig.View.ShowCells(true);
        rig.Paint();

        rig.View.TerminalSize.ShouldBe("120x36");
        var buttons = Painted(rig.View).Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(h => h.Action).Where(a => a.StartsWith("designs:size:", StringComparison.Ordinal)).ToList();
        buttons.ShouldBe(["designs:size:120x36", "designs:size:80x24", "designs:size:100x30", "designs:size:160x44", "designs:size:250x60"]);
    }

    private static List<string> ActionsOf(Rig rig) =>
        [.. Painted(rig.View).Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(h => h.Action)];

    [Fact]
    public void TheButtonOnAPublishedVersionSendsDesignBuildWithTheVersionAndNoQuestion()
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: true, idea: false, flight: null));
        (string Id, int Version, string? Repo, string? Milestone)? asked = null;
        rig.View.OnBuild = (id, version, repo, milestone) => asked = (id, version, repo, milestone);
        rig.View.Open("DSN-1");
        rig.Paint();
        TextsOf(rig).ShouldContain(DesignsView<RgbaImage>.BuildLabel);

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:build" }));

        asked.ShouldBe(("DSN-1", 2, null, null));
        // With the node, the button reads Building… and takes no second press; the answer is applied at the next frame.
        rig.Paint();
        TextsOf(rig).ShouldContain("Building…");
        ActionsOf(rig).ShouldNotContain("designs:build");
        rig.View.Built("DSN-1", 2, "PIL1153", null);
        rig.Paint();
        rig.View.BuildNote.ShouldBe("Built as PIL1153.");
        ActionsOf(rig).ShouldContain("designs:build");
    }

    [Fact]
    public void ARefusedBuildSaysWhyAndKeepsTheButton()
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: true, idea: false, flight: null));
        rig.View.OnBuild = (_, _, _, _) => { };
        rig.View.Open("DSN-1");
        rig.Paint();
        rig.View.Build().ShouldBeTrue();
        rig.View.Built("DSN-1", 2, null, "DSN-1 v2 is being built already");
        rig.Paint();
        TextsOf(rig).ShouldContain("DSN-1 v2 is being built already");
        ActionsOf(rig).ShouldContain("designs:build");
    }

    [Fact]
    public void ADesignWithNoRepoAsksForOneFirstAndSendsTheChoice()
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: false, idea: false, flight: null));
        var sent = new List<(string? Repo, string? Milestone)>();
        rig.View.OnBuild = (_, _, repo, milestone) => sent.Add((repo, milestone));
        rig.View.Open("DSN-1");
        rig.Paint();

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:build" }));

        sent.ShouldBeEmpty("nothing is sent before a repo is chosen");
        rig.View.BuildAsking.ShouldBeTrue();
        TextsOf(rig).ShouldContain("Which repo is it built in?");
        ActionsOf(rig).ShouldContain("designs:build-repo:pilot");
        ActionsOf(rig).ShouldContain("designs:build-repo:tianwen");

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:build-repo:tianwen" }));

        sent.ShouldBe([("tianwen", null)]);
    }

    [Fact]
    public void ADesignFromAnIdeaAsksForTheMilestoneOfItsReposOpenOnesAndSendsIt()
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: true, idea: true, flight: null));
        var sent = new List<(string? Repo, string? Milestone)>();
        rig.View.OnBuild = (_, _, repo, milestone) => sent.Add((repo, milestone));
        rig.View.Open("DSN-1");
        rig.Paint();

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:build" }));

        sent.ShouldBeEmpty();
        TextsOf(rig).ShouldContain("Which milestone is its idea planned into?");
        ActionsOf(rig).ShouldContain("designs:build-milestone:12");
        ActionsOf(rig).ShouldContain("designs:build-milestone:11");
        ActionsOf(rig).ShouldNotContain("designs:build-milestone:3", "another repo's milestone is not offered");

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:build-milestone:12" }));

        sent.ShouldBe([(null, "Designs: build a version")]);
    }

    [Fact]
    public void CancellingTheAskDropsTheChoiceAndSendsNothing()
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: false, idea: false, flight: null));
        var asked = 0;
        rig.View.OnBuild = (_, _, _, _) => asked++;
        rig.View.Open("DSN-1");
        rig.Paint();
        rig.View.Build().ShouldBeFalse();
        rig.Paint();

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:build-cancel" }));

        asked.ShouldBe(0);
        rig.View.BuildAsking.ShouldBeFalse();
        ActionsOf(rig).ShouldContain("designs:build");
    }

    [Theory]
    [InlineData(FlightState.Flying)]
    [InlineData(FlightState.Held)]
    public void AVersionWhoseFlightFliesShowsTheFlightItsStateAndPrAndOffersNoSecondBuild(FlightState state)
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: true, idea: false, flight: state));
        string? flight = null, url = null;
        rig.View.OnFlight = code => flight = code;
        rig.View.OnUrl = address => url = address;
        var asked = 0;
        rig.View.OnBuild = (_, _, _, _) => asked++;
        rig.View.Open("DSN-1");
        rig.Paint();

        var texts = TextsOf(rig);
        texts.ShouldContain("PIL1153");
        texts.ShouldContain(state.ToString().ToLowerInvariant());
        texts.ShouldContain("PR #1170");
        texts.ShouldNotContain("PIL9", "a flight built from another version is not this version's");
        ActionsOf(rig).ShouldNotContain("designs:build");
        rig.View.Build().ShouldBeFalse();
        asked.ShouldBe(0);

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:flight:PIL1153" }));
        flight.ShouldBe("PIL1153");
        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:pr:PIL1153" }));
        url.ShouldBe("https://github.com/SharpAstro/pilot/pull/1170");
    }

    [Fact]
    public void ALandedFlightIsShownAndTheVersionCanBeBuiltAgain()
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: true, idea: false, flight: FlightState.Landed));
        rig.View.OnBuild = (_, _, _, _) => { };
        rig.View.Open("DSN-1");
        rig.Paint();

        TextsOf(rig).ShouldContain("landed");
        ActionsOf(rig).ShouldContain("designs:build");
    }

    [Fact]
    public void TheBuildLineAndButtonShareOneHeightAndOneMiddleWithTheVersionRows()
    {
        using var rig = new Rig(PageData.BuildableDesigns(Now, repo: true, idea: false, flight: FlightState.Landed));
        rig.View.OnBuild = (_, _, _, _) => { };
        rig.View.Open("DSN-1");
        rig.Paint();
        var all = ArrangeAt(rig, 1400, 900);

        var code = all.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:flight:PIL1153" }).Bounds;
        var pr = all.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:pr:PIL1153" }).Bounds;
        var build = all.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:build" }).Bounds;
        code.Height.ShouldBe(26f, "one height");
        pr.Height.ShouldBe(26f, "one height");
        build.Height.ShouldBe(26f, "one height");
        (code.Y + code.Height / 2f).ShouldBe(pr.Y + pr.Height / 2f, 0.5f, "one middle on the built line");
    }

    [Fact]
    public void DesignReadsFindsTheFlightsBuiltFromAVersionByTheSnapshotsDesignAndVersion()
    {
        var screen = PageData.BuildableDesigns(Now, repo: true, idea: false, flight: FlightState.Flying);

        DesignReads.BuiltOf(screen.Flights, "DSN-1", 2).Select(f => f.Code).ShouldBe(["PIL1153"]);
        DesignReads.BuiltOf(screen.Flights, "DSN-1", 1).Select(f => f.Code).ShouldBe(["PIL9"]);
        DesignReads.BuiltOf(screen.Flights, "DSN-2", 2).ShouldBeEmpty();
        DesignReads.BuiltOf(null, "DSN-1", 2).ShouldBeEmpty();
    }

    [Fact]
    public void DesignSizesAreTheDistinctSizesOfTheWireframeSweep()
    {
        var sweep = WireframeSweepTests.LongSizes.Select(s => ((uint)s.Width, (uint)s.Height)).Distinct().OrderBy(s => s).ToArray();

        DesignSizes.All.OrderBy(s => s).ToArray().ShouldBe(sweep);
    }
}
