using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Sessions and Jobs pages' tables: their columns over DIR.Lib's <c>TableHeader</c> and <c>TableRow</c>, a drag on the
/// divider between two titles resizing a column in the header and in every row, and the dragged widths kept by column title
/// in the GUI's own settings file. They assert where things are arranged (the engine's own answer at 1280x800), not pixels.
/// </summary>
public sealed class ListTableTests : IDisposable
{
    private const float PageWidth = 1280f;
    private const float PageHeight = 800f;

    // Where each table's text cells are: the header's titles and the first row's values, by the text they say.
    private sealed record Cells(IReadOnlyList<(string Text, Rect<float> Bounds)> Texts)
    {
        public float HeaderX(string title) => Texts.First(t => t.Text == title && t.Bounds.Y < HeaderBottom).Bounds.X;

        public bool HasHeader(string title) => Texts.Any(t => t.Text == title && t.Bounds.Y < HeaderBottom);

        public float RowX(string value) => Texts.Where(t => t.Text == value && t.Bounds.Y > HeaderBottom).OrderBy(t => t.Bounds.Y).First().Bounds.X;

        public bool HasRow(string value) => Texts.Any(t => t.Text == value && t.Bounds.Y > HeaderBottom);

        // The page's own header is 64 tall and the table's 28 under it, with the page's 8 padding: rows start at 100.
        private float HeaderBottom => 100f;
    }

    private readonly RgbaImageRenderer _renderer = new((uint)PageWidth, (uint)PageHeight);

    public void Dispose() => _renderer.Dispose();

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"pilot-gui-settings-{Guid.NewGuid():N}", "gui-settings.json");

    private static SessionsScreen SessionsScreenOf(bool several = false)
    {
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        return several
            ? screen with { Lines = [.. screen.Lines.Select((l, i) => l with { Node = new NodeMark(i % 2 == 0 ? "s" : "s9", "node.sock", i % 2 == 0 ? "SE1" : "LE1", Here: i % 2 == 0) })] }
            : screen;
    }

    private static JobsScreen JobsScreenOf(bool several = false)
    {
        JobRow Job(string id, JobState state, DateTimeOffset started, DateTimeOffset? ended = null) => new()
        {
            Id = id,
            Command = $"dotnet test {id}",
            Cwd = "/r/orbit",
            Owner = "c3d4e5f6",
            OwnerName = "xy1-orb204-parser",
            Flight = "ORB204",
            Leg = "parser",
            State = state,
            LogPath = $"/data/jobs/{id}.log",
            Timeout = TimeSpan.FromHours(1),
            StartedAt = started,
            EndedAt = ended,
            Told = true,
        };

        var lines = JobLine.Build(
        [
            Job("j14", JobState.Running, Live.At.AddMinutes(-11)),
            Job("j13", JobState.Succeeded, Live.At.AddMinutes(-40), Live.At.AddMinutes(-35)),
        ], Live.At);
        if (several)
        {
            lines = [.. lines.Select((l, i) => l with { Node = new NodeMark(i == 0 ? "s" : "s9", "node.sock", i == 0 ? "SE1" : "LE1", Here: i == 0) })];
        }
        return new JobsScreen(lines, "live", Live.At);
    }

    private SessionsView<RgbaImage> Sessions() => new(_renderer) { FontPath = SessionsCapture.FontPath };

    private JobsView<RgbaImage> Jobs() => new(_renderer) { FontPath = SessionsCapture.FontPath };

    private Cells Arranged(Layout.Node tree, float width = PageWidth)
    {
        var context = new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f);
        var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0f, 0f, width, PageHeight), context);
        return new Cells([.. arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).Select(a => (((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, a.Bounds))]);
    }

    /// <summary>Presses on the divider <paramref name="hit"/> names, moves <paramref name="by"/> to the right and lets go, as the window's router does.</summary>
    private static void Drag<TView>(TView view, Action render, string hit, float by) where TView : PixelWidgetBase<RgbaImage>
    {
        render();
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var divider = regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == hit);
        var x = divider.X + (divider.Width / 2f);
        var y = divider.Y + (divider.Height / 2f);
        var router = new InputRouter(view.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => [view] };
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseMove(x + (by / 2f), y));
        router.Handle(new InputEvent.MouseMove(x + by, y));
        router.Handle(new InputEvent.MouseUp(x + by, y));
        render();
    }

    private static List<ClickableRegion> Painted<TView>(TView view) where TView : PixelWidgetBase<RgbaImage>
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions;
    }

    // Sessions' values for the first row, which name each column's cell.
    private static string[] SessionTitles => ["Session", "Model", "Context", "Last response", "State", "Spend"];

    private static (string Title, string Value)[] SessionColumns(SessionLine line) =>
    [
        ("Session", line.Role), ("Model", line.Model), ("Context", line.Context), ("Last response", line.LastResponse), ("State", line.State), ("Spend", line.Spend),
    ];

    private static (string Title, string Value)[] JobColumns(JobLine line) =>
    [
        ("Job", line.Id), ("Command", line.Command), ("For", line.For), ("State", line.State), ("Started", line.Started),
    ];

    [Fact]
    public void SessionsWithNoDragLayOutAsTheyAlwaysDid()
    {
        var view = Sessions();
        var screen = SessionsScreenOf();
        var cells = Arranged(view.Tree(screen));
        var first = screen.Lines[0];

        // Each title sits over its values, and the fixed columns have the widths the page declares: the next column starts a gap after.
        foreach (var (title, value) in SessionColumns(first))
        {
            cells.HeaderX(title).ShouldBe(cells.RowX(value), $"{title} sits over its values");
        }
        (cells.HeaderX("Last response") - cells.HeaderX("Context") - ListTable.ColumnGap).ShouldBe(160f, 0.5f);
        (cells.HeaderX("Spend") - cells.HeaderX("State") - ListTable.ColumnGap).ShouldBe(160f, 0.5f);
        (cells.HeaderX("Context") - cells.HeaderX("Model") - ListTable.ColumnGap).ShouldBe(140f, 0.5f);
        cells.HasHeader("Node").ShouldBeFalse("the Node column shows only with several nodes known");
        view.Table.Saved.ShouldBeEmpty();
    }

    [Fact]
    public void SessionsShowTheNodeColumnFirstOnlyWithSeveralNodes()
    {
        var view = Sessions();
        var screen = SessionsScreenOf(several: true);
        var cells = Arranged(view.Tree(screen));

        cells.HasHeader("Node").ShouldBeTrue();
        cells.HeaderX("Node").ShouldBeLessThan(cells.HeaderX("Session"));
        cells.HeaderX("Node").ShouldBe(cells.RowX("SE1"));
        (cells.HeaderX("Session") - cells.HeaderX("Node") - ListTable.ColumnGap).ShouldBe(64f, 0.5f);
    }

    [Fact]
    public void ADragOnASessionsDividerResizesTheColumnInTheHeaderAndInEveryRow()
    {
        var view = Sessions();
        var screen = SessionsScreenOf();
        void Render() => view.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));
        var before = Arranged(view.Tree(screen));

        // The divider after the State column (State is fixed, Spend takes up to its width): State grows by what was dragged.
        Drag(view, Render, "sessions:table:divider:4", 40f);

        var after = Arranged(view.Tree(screen));
        var first = screen.Lines[0];
        (after.HeaderX("Spend") - after.HeaderX("State") - ListTable.ColumnGap).ShouldBe(200f, 0.5f);
        foreach (var (title, value) in SessionColumns(first))
        {
            after.HeaderX(title).ShouldBe(after.RowX(value), $"{title} still sits over its values");
        }
        (before.HeaderX("Spend") - before.HeaderX("State") - ListTable.ColumnGap).ShouldBe(160f, 0.5f);
        view.Table.Saved.ShouldContainKeyAndValue("State", 200f);
    }

    [Fact]
    public void ADragOnAJobsDividerResizesTheColumnInTheHeaderAndInEveryRow()
    {
        var view = Jobs();
        var screen = JobsScreenOf();
        void Render() => view.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));

        Drag(view, Render, "jobs:table:divider:0", 30f);

        var after = Arranged(view.Tree(screen));
        (after.HeaderX("Command") - after.HeaderX("Job") - ListTable.ColumnGap).ShouldBe(100f, 0.5f);
        foreach (var (title, value) in JobColumns(screen.Lines[0]))
        {
            after.HeaderX(title).ShouldBe(after.RowX(value), $"{title} still sits over its values");
        }
        after.RowX("j13").ShouldBe(after.HeaderX("Job"));
        view.Table.Saved.ShouldContainKeyAndValue("Job", 100f);
    }

    [Fact]
    public void JobsWithNoDragLayOutAsTheyAlwaysDid()
    {
        var view = Jobs();
        var screen = JobsScreenOf();
        var cells = Arranged(view.Tree(screen));

        (cells.HeaderX("Command") - cells.HeaderX("Job") - ListTable.ColumnGap).ShouldBe(70f, 0.5f);
        (cells.HeaderX("State") - cells.HeaderX("For") - ListTable.ColumnGap).ShouldBe(170f, 0.5f);
        (cells.HeaderX("Started") - cells.HeaderX("State") - ListTable.ColumnGap).ShouldBe(220f, 0.5f);
        (cells.HeaderX("Took") - cells.HeaderX("Started") - ListTable.ColumnGap).ShouldBe(110f, 0.5f);
        foreach (var (title, value) in JobColumns(screen.Lines[0]))
        {
            cells.HeaderX(title).ShouldBe(cells.RowX(value), $"{title} sits over its values");
        }
        cells.HasHeader("Node").ShouldBeFalse();
        var merged = Arranged(view.Tree(JobsScreenOf(several: true)));
        merged.HeaderX("Node").ShouldBeLessThan(merged.HeaderX("Job"));
        merged.HeaderX("Node").ShouldBe(merged.RowX("SE1"));
        (merged.HeaderX("Job") - merged.HeaderX("Node") - ListTable.ColumnGap).ShouldBe(64f, 0.5f);
    }

    [Fact]
    public void SavedWidthsAreArrangedByAViewGivenThem()
    {
        var screen = JobsScreenOf();
        var view = Jobs();
        view.Table.Restore(new Dictionary<string, float> { ["For"] = 250f, ["Job"] = 90f });

        var cells = Arranged(view.Tree(screen));

        (cells.HeaderX("State") - cells.HeaderX("For") - ListTable.ColumnGap).ShouldBe(250f, 0.5f);
        (cells.HeaderX("Command") - cells.HeaderX("Job") - ListTable.ColumnGap).ShouldBe(90f, 0.5f);
        cells.RowX("ORB204/parser").ShouldBe(cells.HeaderX("For"));
    }

    [Fact]
    public void SavedWidthsFollowTheirTitleWhenTheNodeColumnComesAndGoes()
    {
        var view = Jobs();
        view.Table.Restore(new Dictionary<string, float> { ["State"] = 300f });

        foreach (var several in new[] { false, true, false })
        {
            var cells = Arranged(view.Tree(JobsScreenOf(several)));
            (cells.HeaderX("Started") - cells.HeaderX("State") - ListTable.ColumnGap).ShouldBe(300f, 0.5f);
            (cells.HeaderX("State") - cells.HeaderX("For") - ListTable.ColumnGap).ShouldBe(170f, 0.5f);
        }
    }

    [Fact]
    public void ADraggedWidthIsKeptInTheFileAndReadBackByANewWindow()
    {
        var path = TempFile();
        try
        {
            var screen = JobsScreenOf();
            var view = Jobs();
            using (var file = GuiSettingsFile.Open(path))
            {
                file.Attach(view.Table);
                var redraws = 0;
                view.Table.Changed += () => redraws++;
                Drag(view, () => view.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight)), "jobs:table:divider:2", 25f);
                redraws.ShouldBeGreaterThan(0, "a drag asks for a frame");
                file.Flush();
            }

            File.Exists(path).ShouldBeTrue();
            File.ReadAllText(path).ShouldContain("\"For\": 195");

            var again = Jobs();
            using var reopened = GuiSettingsFile.Open(path);
            reopened.Attach(again.Table);
            var cells = Arranged(again.Tree(screen));
            (cells.HeaderX("State") - cells.HeaderX("For") - ListTable.ColumnGap).ShouldBe(195f, 0.5f);
            again.Table.Saved["For"].ShouldBe(195f);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all {")]
    [InlineData("{\"tables\": 7}")]
    [InlineData("{\"tables\": {\"jobs\": {\"For\": \"wide\"}}}")]
    [InlineData("{\"tables\": {\"jobs\": {\"For\": -5, \"State\": 1e999}}}")]
    public void ASettingsFileThatCannotBeReadMeansTheDefaults(string content)
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, content);
            var view = Jobs();
            using var file = GuiSettingsFile.Open(path);
            file.Attach(view.Table);

            var cells = Arranged(view.Tree(JobsScreenOf()));
            (cells.HeaderX("State") - cells.HeaderX("For") - ListTable.ColumnGap).ShouldBe(170f, 0.5f);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AMissingSettingsFileMeansTheDefaultsAndItsFolderIsMadeOnTheFirstWrite()
    {
        var path = TempFile();
        try
        {
            var view = Jobs();
            using var file = GuiSettingsFile.Open(path);
            file.Attach(view.Table);
            Arranged(view.Tree(JobsScreenOf())).HasHeader("Job").ShouldBeTrue();
            File.Exists(path).ShouldBeFalse("nothing changed, nothing is written");

            Drag(view, () => view.Render(JobsScreenOf(), new RectF32(0f, 0f, PageWidth, PageHeight)), "jobs:table:divider:0", 10f);
            file.Flush();

            File.Exists(path).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void OptionalColumnsDropTogetherInTheHeaderAndTheRowsAndTheDividersStayUnderTheGaps()
    {
        var view = Sessions();
        var screen = SessionsScreenOf();

        // Wide enough for everything, then too narrow for the optional columns: they go from the header and the rows alike.
        var wide = Arranged(view.Tree(screen), 1280f);
        var narrow = Arranged(view.Tree(screen), 760f);
        var first = screen.Lines[0];

        wide.HasHeader("Model").ShouldBeTrue();
        wide.HasRow(first.Model).ShouldBeTrue();
        narrow.HasHeader("Model").ShouldBe(narrow.HasRow(first.Model));
        narrow.HasHeader("Last response").ShouldBe(narrow.HasRow(first.LastResponse));
        narrow.HasHeader("Spend").ShouldBe(narrow.HasRow(first.Spend));
        (narrow.HasHeader("Model") && narrow.HasHeader("Last response") && narrow.HasHeader("Spend")).ShouldBeFalse("at 760 not every optional column fits");
        foreach (var (title, value) in SessionColumns(first).Where(c => narrow.HasHeader(c.Title)))
        {
            narrow.HeaderX(title).ShouldBe(narrow.RowX(value), $"{title} sits over its values at 760 wide");
        }
    }

    [Theory]
    [InlineData(1280f)]
    [InlineData(1000f)]
    [InlineData(800f)]
    [InlineData(640f)]
    public void EachDividerStandsInTheGapBeforeTheNextShownTitleAtAnyWidth(float width)
    {
        var view = Sessions();
        var screen = SessionsScreenOf();
        view.Render(screen, new RectF32(0f, 0f, width, PageHeight));
        var dividers = Painted(view)
            .Where(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("sessions:table:divider:", StringComparison.Ordinal))
            .OrderBy(r => r.X)
            .ToList();
        var cells = Arranged(view.Tree(screen), width);
        var shown = SessionTitles.Where(cells.HasHeader).Select(cells.HeaderX).Order().ToList();

        // Between every two shown columns exactly one divider, ending where the next column begins.
        dividers.Count.ShouldBeGreaterThanOrEqualTo(shown.Count - 1);
        foreach (var next in shown.Skip(1))
        {
            dividers.ShouldContain(d => Math.Abs(d.X + d.Width - next) < 1f, $"a divider ends at {next}");
        }
    }
}
