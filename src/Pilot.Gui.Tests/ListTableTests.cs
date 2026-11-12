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
    private sealed record Cells(IReadOnlyList<(string Text, Rect<float> Bounds)> Texts, float HeaderBottom = 100f)
    {
        public float HeaderX(string title) => Texts.First(t => t.Text == title && t.Bounds.Y < HeaderBottom).Bounds.X;

        public bool HasHeader(string title) => Texts.Any(t => t.Text == title && t.Bounds.Y < HeaderBottom);

        public float RowX(string value) => Texts.Where(t => t.Text == value && t.Bounds.Y > HeaderBottom).OrderBy(t => t.Bounds.Y).First().Bounds.X;

        public bool HasRow(string value) => Texts.Any(t => t.Text == value && t.Bounds.Y > HeaderBottom);
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

    // The page's own header is 64 tall and the table's 28 under it, with the page's 8 padding: rows start at 100. A page
    // whose header is another height names a title, and its rows are what lies below that title's line.
    private Cells Arranged(Layout.Node tree, float width = PageWidth, string? headerTitle = null)
    {
        var context = new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f);
        var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0f, 0f, width, PageHeight), context);
        var texts = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).Select(a => (((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, a.Bounds)).ToList();
        return headerTitle is null ? new Cells(texts) : new Cells(texts, texts.First(t => t.Value == headerTitle).Bounds.Y + 20f);
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
    private static string[] SessionTitles => ["Session", "Model", "Context", "Last response", "State", "Spend", "Age", "Spend/min"];

    private static (string Title, string Value)[] SessionColumns(SessionLine line) =>
    [
        ("Session", line.Role), ("Model", line.Model), ("Context", line.Context), ("Last response", line.LastResponse), ("State", line.State), ("Spend", line.Spend), ("Age", line.Age), ("Spend/min", line.SpendPerMinute),
    ];

    private static (string Title, string Value)[] JobColumns(JobLine line) =>
    [
        ("Job", line.Id), ("Command", line.Command), ("For", line.For), ("State", line.State), ("Started", line.Started),
    ];

    private static NodeSummary NodeSummaryOf(string stream, string account) => new()
    {
        Connected = true, Status = "live", Stream = stream, Account = account, LiveSessions = 3,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream },
    };

    private static FleetScreen FleetScreenOf() => new(
    [
        FleetLine.From("node.sock", here: true, canCommand: true, null, NodeSummaryOf("laptop", "ada@example.org")),
        FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, null, NodeSummaryOf("desktop", "bo@example.org")),
    ], Live.At);

    private static FlightsScreen FlightsScreenOf() => new(FlightLine.Build(
        [.. new[] { "ORB1", "ORB2" }.Select(code => new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = $"{code} title", Branch = "flight/" + code.ToLowerInvariant(),
            Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = FlightState.Flying, Connections = [], CreatedAt = Live.At,
        })], [], Live.At), "test", Live.At);

    private static ReposScreen ReposScreenOf() => new(RepoLine.Build(
        SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath),
        [new FlightRow { Code = "ORB204", Repo = "orbit", RepoPath = "/home/ada/src/example-org/orbit", Issue = 204, Branch = "flight/orb204", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, Connections = [], CreatedAt = Live.At }],
        [Live.Row("c3d4e5f6", "xy1-orb204-parser", SessionState.Working) with
        {
            Cwd = "/home/ada/src/example-org/orbit/.claude/worktrees/orb204/src", Flight = "ORB204", Role = SessionRole.Worker, LastResponseAt = Live.At.AddMinutes(-3),
        }],
        [], [], Live.At), "live", Live.At);

    private PilotChrome<RgbaImage> Chrome() => new(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };

    private static void ClickTitle(SessionsView<RgbaImage> view, SessionsScreen screen, string title, float width = PageWidth)
    {
        view.Render(screen, new RectF32(0f, 0f, width, PageHeight));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == $"sessions:table:{title}").OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
    }

    // The sort carets drawn in the header, of one kind, from the page as the engine arranges it.
    private int Carets(SessionsView<RgbaImage> view, SessionsScreen screen, Layout.IconKind kind)
    {
        var context = new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f);
        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0f, 0f, PageWidth, PageHeight), context);
        return arranged.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon icon } && icon.Kind == kind && a.Bounds.Y < 100f);
    }

    [Fact]
    public void AClickOnATitleMarksTheHeaderASecondClickFlipsItAndTheActionColumnDoesNotSort()
    {
        var screen = SessionsScreenOf();
        var view = Sessions();

        view.Table.Sort.ShouldBeNull();
        Carets(view, screen, Layout.IconKind.CaretUp).ShouldBe(0);

        ClickTitle(view, screen, "Session");
        view.Table.Sort.ShouldBe(new ListTable.SortState("Session", false));
        Carets(view, screen, Layout.IconKind.CaretUp).ShouldBe(1);

        ClickTitle(view, screen, "Session");
        view.Table.Sort.ShouldBe(new ListTable.SortState("Session", true));
        Carets(view, screen, Layout.IconKind.CaretDown).ShouldBe(1);

        // The action column has no title: its press is swallowed, and sorts nothing.
        view.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var blank = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "sessions:table:" });
        blank.OnClick?.Invoke(InputModifier.None);
        view.Table.Sort.ShouldBe(new ListTable.SortState("Session", true));
    }

    [Fact]
    public void ASortComesBackFromTheSettingsFileAndAnUnknownColumnMeansNoSort()
    {
        var path = TempFile();
        try
        {
            var screen = SessionsScreenOf();
            var chrome = Chrome();
            using (var file = GuiSettingsFile.Open(path))
            {
                chrome.KeepColumnWidths(file, () => { });
                // Spend is an optional column: at 1280 it is dropped now Age and Spend/min share the row, so this click needs the wide page.
                ClickTitle(chrome.Sessions, screen, "Spend", 1400f);
                file.Flush();
            }

            var written = File.ReadAllText(path);
            written.ShouldContain("\"sorts\"");
            written.ShouldContain("\"column\": \"Spend\"");
            written.ShouldContain("\"descending\": true");

            var again = Chrome();
            using (var reopened = GuiSettingsFile.Open(path))
            {
                again.KeepColumnWidths(reopened, () => { });
                again.Sessions.Table.Sort.ShouldBe(new ListTable.SortState("Spend", true));
                again.Jobs.Table.Sort.ShouldBeNull();
            }

            File.WriteAllText(path, "{\"sorts\":{\"sessions\":{\"column\":\"\",\"descending\":true}}}");
            var blank = Chrome();
            using var blanked = GuiSettingsFile.Open(path);
            blank.KeepColumnWidths(blanked, () => { });
            blank.Sessions.Table.Sort.ShouldBeNull();

            File.WriteAllText(path, "{\"sorts\":{\"sessions\":{\"column\":\"Nonesuch\",\"descending\":false}}}");
            var unknown = Chrome();
            using var opened = GuiSettingsFile.Open(path);
            unknown.KeepColumnWidths(opened, () => { });
            unknown.Sessions.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));
            var plain = Chrome();
            plain.Sessions.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));
            unknown.Sessions.Shown.Count.ShouldBeGreaterThan(0);
            unknown.Sessions.Shown.Select(l => l.Key).ShouldBe([.. plain.Sessions.Shown.Select(l => l.Key)], "no column of that title: the page's own order");
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)!))
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }
    }

    [Fact]
    public void FleetWithNoDragLaysOutAsItAlwaysDidTheButtonsColumnIncluded()
    {
        var chrome = Chrome();
        chrome.Fleet.OnConnect = _ => Task.FromResult<string?>(null);
        var screen = FleetScreenOf();
        chrome.Fleet.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));
        var cells = Arranged(chrome.Fleet.Tree(screen));

        (cells.HeaderX("Account and usage") - cells.HeaderX("Node") - ListTable.ColumnGap).ShouldBe(190f, 0.5f);
        (cells.HeaderX("Sessions") - cells.HeaderX("Account and usage") - ListTable.ColumnGap).ShouldBe(280f, 0.5f);
        (cells.HeaderX("Flights and jobs") - cells.HeaderX("Sessions") - ListTable.ColumnGap).ShouldBe(170f, 0.5f);
        (cells.HeaderX("Rings") - cells.HeaderX("Flights and jobs") - ListTable.ColumnGap).ShouldBe(170f, 0.5f);
        cells.HeaderX("Node").ShouldBe(cells.RowX("SE2 · DESKTOP-7"));
        cells.HeaderX("Account and usage").ShouldBe(cells.RowX("ada@example.org"));
        cells.HeaderX("Sessions").ShouldBe(cells.RowX("3 live"));
        cells.HeaderX("Rings").ShouldBe(cells.RowX("—"));

        // The buttons column after Rings is the 150 it was, and its button is a click of its own on the second row.
        var connect = Painted(chrome.Fleet).Single(r => r.Result is HitResult.ButtonHit { Action: "fleet:10.0.0.9:47731:connect" });
        (connect.X - cells.HeaderX("Rings") - 80f - ListTable.ColumnGap).ShouldBe(0f, 0.5f);
        chrome.Fleet.Table.Saved.ShouldBeEmpty();
    }

    [Fact]
    public void ADragOnAFleetDividerResizesTheColumnInTheHeaderAndInEveryRowAndKeepsTheButtonsWhereTheyClick()
    {
        var chrome = Chrome();
        var fleet = chrome.Fleet;
        fleet.OnConnect = _ => Task.FromResult<string?>(null);
        var screen = FleetScreenOf();
        void Render() => fleet.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));
        Render();
        var before = Arranged(fleet.Tree(screen));
        var connectBefore = Painted(fleet).Single(r => r.Result is HitResult.ButtonHit { Action: "fleet:10.0.0.9:47731:connect" });

        // The divider after the Account column: Account grows by what was dragged, at the expense of Sessions beside it.
        Drag(fleet, Render, "fleet:table:divider:1", 40f);

        var after = Arranged(fleet.Tree(screen));
        (after.HeaderX("Sessions") - after.HeaderX("Account and usage") - ListTable.ColumnGap).ShouldBe(320f, 0.5f);
        foreach (var (title, value) in new[] { ("Node", "SE2 · DESKTOP-7"), ("Account and usage", "ada@example.org"), ("Sessions", "3 live"), ("Rings", "—") })
        {
            after.HeaderX(title).ShouldBe(after.RowX(value), $"{title} still sits over its values");
        }
        after.RowX("bo@example.org").ShouldBe(after.HeaderX("Account and usage"), "the second row follows too");
        fleet.Table.Saved.ShouldContainKeyAndValue("Account and usage", 320f);

        var regions = Painted(fleet);
        var connect = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "fleet:10.0.0.9:47731:connect" });
        // A divider moves the boundary between two columns, so what lies beyond the neighbour stays: the buttons column too.
        (after.HeaderX("Flights and jobs") - after.HeaderX("Sessions") - ListTable.ColumnGap).ShouldBe(130f, 0.5f, "the neighbour gave up what Account gained");
        after.HeaderX("Rings").ShouldBe(before.HeaderX("Rings"), 0.5f);
        connect.X.ShouldBe(connectBefore.X, 0.5f);
        connect.Width.ShouldBe(connectBefore.Width, 0.5f);
        // The dividers stand in the header, above where any row begins: a press on one is never a press on a row.
        var rowTop = regions.Where(r => r.Result is HitResult.ListItemHit).Min(r => r.Y);
        regions.Where(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("fleet:table:divider:", StringComparison.Ordinal))
            .ShouldAllBe(d => d.Y + d.Height <= rowTop + 0.5f);
    }

    [Fact]
    public void FleetWidthsComeBackFromTheSettingsFileInANewWindowEachPageUnderItsOwnKey()
    {
        var path = TempFile();
        try
        {
            var screen = FleetScreenOf();
            var chrome = Chrome();
            using (var file = GuiSettingsFile.Open(path))
            {
                chrome.KeepColumnWidths(file, () => { });
                Drag(chrome.Fleet, () => chrome.Fleet.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight)), "fleet:table:divider:2", 30f);
                file.Flush();
            }

            var written = File.ReadAllText(path);
            written.ShouldContain("\"fleet\"");
            written.ShouldContain("\"Sessions\": 200");
            written.ShouldContain("\"sessions\": {}", customMessage: "the other pages were not dragged");
            written.ShouldContain("\"jobs\": {}");
            written.ShouldContain("\"flights\": {}");
            new[] { chrome.Sessions.Table, chrome.Jobs.Table, chrome.Flights.Table, chrome.Fleet.Table, chrome.Repos.Table }
                .Select(t => t.Page).Distinct().Count().ShouldBe(5, "no two pages share a key");

            var again = Chrome();
            using var reopened = GuiSettingsFile.Open(path);
            again.KeepColumnWidths(reopened, () => { });
            var cells = Arranged(again.Fleet.Tree(screen));
            (cells.HeaderX("Flights and jobs") - cells.HeaderX("Sessions") - ListTable.ColumnGap).ShouldBe(200f, 0.5f);
            again.Fleet.Table.Saved["Sessions"].ShouldBe(200f);
            again.Flights.Table.Saved.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void FlightsListWithNoDragLaysOutAsItAlwaysDidAndADragResizesHeaderAndRows()
    {
        var view = new FlightsView<RgbaImage>(_renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List };
        var screen = FlightsScreenOf();
        var first = screen.Lines[0];
        (string Title, string Value)[] columns = [("Flight", first.Code), ("Repo", first.Where), ("State", first.State), ("Branch", first.Branch)];

        var cells = Arranged(view.Tree(screen), headerTitle: "Flight");
        (cells.HeaderX("State") - cells.HeaderX("Repo") - ListTable.ColumnGap).ShouldBe(140f, 0.5f);
        (cells.HeaderX("Legs") - cells.HeaderX("State") - ListTable.ColumnGap).ShouldBe(160f, 0.5f);
        (cells.HeaderX("Branch") - cells.HeaderX("Legs") - ListTable.ColumnGap).ShouldBe(210f, 0.5f);
        (cells.HeaderX("What") - cells.HeaderX("Flight") - ListTable.ColumnGap).ShouldBe(120f, 0.5f);
        foreach (var (title, value) in columns)
        {
            cells.HeaderX(title).ShouldBe(cells.RowX(value), $"{title} sits over its values");
        }
        cells.HeaderX("What").ShouldBe(cells.RowX(first.Title));
        cells.HasHeader("Node").ShouldBeFalse();
        view.Table.Saved.ShouldBeEmpty();

        // The divider after State (the fourth column).
        Drag(view, () => view.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight)), "flights:table:divider:3", 30f);

        var after = Arranged(view.Tree(screen), headerTitle: "Flight");
        (after.HeaderX("Legs") - after.HeaderX("State") - ListTable.ColumnGap).ShouldBe(190f, 0.5f);
        foreach (var (title, value) in columns)
        {
            after.HeaderX(title).ShouldBe(after.RowX(value), $"{title} still sits over its values");
        }
        after.RowX(screen.Lines[1].Code).ShouldBe(after.HeaderX("Flight"));
        view.Table.Saved.ShouldContainKeyAndValue("State", 190f);
    }

    [Fact]
    public void ReposWithNoDragLayOutAsTheyAlwaysDidTheWorktreeRowsUnderTheSameColumns()
    {
        var view = Chrome().Repos;
        var screen = ReposScreenOf();
        view.ExpandAll(screen);
        var repo = screen.Lines[0];
        var worktree = repo.Worktrees[0];

        var cells = Arranged(view.Tree(screen), headerTitle: "Repo · worktree");
        (cells.HeaderX("Open PRs · session") - cells.HeaderX("Checkout · diff") - ListTable.ColumnGap).ShouldBe(220f, 0.5f);
        (cells.HeaderX("Worktrees · action") - cells.HeaderX("Open PRs · session") - ListTable.ColumnGap).ShouldBe(280f, 0.5f);
        (cells.HeaderX("Flight · PR") - cells.HeaderX("Worktrees · action") - ListTable.ColumnGap).ShouldBe(170f, 0.5f);
        (cells.HeaderX("Checkout · diff") - cells.HeaderX("Repo · worktree")).ShouldBeGreaterThanOrEqualTo(190f + ListTable.ColumnGap);
        // The repo with worktrees opens (a caret icon) them with a toggle ahead of its name, at the column's start.
        view.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));
        var toggle = Painted(view).Single(r => r.Result is HitResult.ButtonHit b && b.Action == $"repo:{repo.Key}:worktrees");
        cells.HeaderX("Repo · worktree").ShouldBe(toggle.X, 0.5f);
        cells.HeaderX("Checkout · diff").ShouldBe(cells.RowX(repo.Main));
        // The sub-row's cells sit under the same columns; its name is indented from the column's start.
        cells.HeaderX("Checkout · diff").ShouldBe(cells.RowX(worktree.DiffStat), "the diff stat is under Checkout · diff");
        cells.HeaderX("Open PRs · session").ShouldBe(cells.RowX(worktree.Activity), "the session is under Open PRs · session");
        cells.RowX($"↳ {worktree.Branch}").ShouldBe(cells.HeaderX("Repo · worktree") + 16f, 0.5f);
        cells.HasHeader("Node").ShouldBeFalse();
        view.Table.Saved.ShouldBeEmpty();
    }

    [Fact]
    public void ADragOnAReposDividerMovesTheWorktreeRowsCellsWithTheHeaderAndTheRepoRows()
    {
        var view = Chrome().Repos;
        var screen = ReposScreenOf();
        view.ExpandAll(screen);
        var repo = screen.Lines[0];
        var worktree = repo.Worktrees[0];
        void Render() => view.Render(screen, new RectF32(0f, 0f, PageWidth, PageHeight));

        // The divider after Checkout · diff (the second column).
        Drag(view, Render, "repos:table:divider:1", 30f);

        var after = Arranged(view.Tree(screen), headerTitle: "Repo · worktree");
        (after.HeaderX("Open PRs · session") - after.HeaderX("Checkout · diff") - ListTable.ColumnGap).ShouldBe(250f, 0.5f);
        after.HeaderX("Open PRs · session").ShouldBe(after.RowX(worktree.Activity), "the worktree row's session follows the resized column");
        after.HeaderX("Checkout · diff").ShouldBe(after.RowX(worktree.DiffStat));
        after.HeaderX("Checkout · diff").ShouldBe(after.RowX(repo.Main));
        view.Table.Saved.ShouldContainKeyAndValue("Checkout · diff", 250f);
    }

    [Fact]
    public void SessionsWithNoDragLayOutAsTheyAlwaysDid()
    {
        var view = Sessions();
        var screen = SessionsScreenOf();
        // Wide enough for every optional column, Age and Spend/min among them.
        var cells = Arranged(view.Tree(screen), 1400f);
        var first = screen.Lines[0];

        // Each title sits over its values, and the fixed columns have the widths the page declares: the next column starts a gap after.
        foreach (var (title, value) in SessionColumns(first))
        {
            cells.HeaderX(title).ShouldBe(cells.RowX(value), $"{title} sits over its values");
        }
        (cells.HeaderX("Last response") - cells.HeaderX("Context") - ListTable.ColumnGap).ShouldBe(160f, 0.5f);
        (cells.HeaderX("Spend") - cells.HeaderX("State") - ListTable.ColumnGap).ShouldBe(160f, 0.5f);
        (cells.HeaderX("Age") - cells.HeaderX("Spend") - ListTable.ColumnGap).ShouldBe(130f, 0.5f);
        (cells.HeaderX("Spend/min") - cells.HeaderX("Age") - ListTable.ColumnGap).ShouldBe(80f, 0.5f);
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
        // Wide enough that the Age column added to the page leaves every column its room after the drag.
        const float WideWidth = 1500f;
        var view = Sessions();
        var screen = SessionsScreenOf();
        void Render() => view.Render(screen, new RectF32(0f, 0f, WideWidth, PageHeight));
        var before = Arranged(view.Tree(screen), WideWidth);

        // The divider after the State column: State takes what Spend, the column on its other side, gives up.
        Drag(view, Render, "sessions:table:divider:4", 40f);

        var after = Arranged(view.Tree(screen), WideWidth);
        var first = screen.Lines[0];
        (after.HeaderX("Spend") - after.HeaderX("State") - ListTable.ColumnGap).ShouldBe(200f, 0.5f);
        after.HeaderX("State").ShouldBe(before.HeaderX("State"), 0.5f, "State's far edge stands");
        after.HeaderX("Age").ShouldBe(before.HeaderX("Age"), 0.5f, "and so does Spend's");
        foreach (var (title, value) in SessionColumns(first))
        {
            after.HeaderX(title).ShouldBe(after.RowX(value), $"{title} still sits over its values");
        }
        (before.HeaderX("Spend") - before.HeaderX("State") - ListTable.ColumnGap).ShouldBe(160f, 0.5f);
        view.Table.Saved.ShouldContainKeyAndValue("State", 200f);
    }

    [Theory]
    // Divider 0 (the left edge of the first column) is not in the 1700 rows: with no room to spare it has nothing to trade,
    // so it follows the pointer only where the Session column has room, as the 2200 row shows.
    [InlineData(0, 30f, 2200f)]
    [InlineData(1, 30f, 1700f)]
    [InlineData(2, 30f, 1700f)]
    [InlineData(3, 30f, 1700f)]
    [InlineData(4, 30f, 1700f)]
    [InlineData(5, 30f, 1700f)]
    [InlineData(6, 30f, 1700f)]
    [InlineData(3, -30f, 1700f)]
    [InlineData(4, -30f, 1700f)]
    // Wide enough that the Session column has room to spare, where the columns after it stand from the right.
    [InlineData(3, 30f, 2200f)]
    [InlineData(4, 30f, 2200f)]
    [InlineData(3, -30f, 2200f)]
    [InlineData(4, -30f, 2200f)]
    public void SessionsDividersOneToSixFollowThePointerHeaderAndRowsTogether(int divider, float by, float wide)
    {
        // A divider beside State once moved State's far edge and stood still under the pointer (2026-11-11): the two columns
        // beside a divider trade width (DIR.Lib's table), so it follows the pointer whatever kind each is.
        var view = Sessions();
        var screen = SessionsScreenOf();
        void Render() => view.Render(screen, new RectF32(0f, 0f, wide, PageHeight));
        var hit = $"sessions:table:divider:{divider}";
        Render();
        var before = Painted(view).Single(r => r.Result is HitResult.ButtonHit b && b.Action == hit).X;

        Drag(view, Render, hit, by);

        Painted(view).Single(r => r.Result is HitResult.ButtonHit b && b.Action == hit).X.ShouldBe(before + by, 0.5f, "the divider is where the pointer took it");
        var after = Arranged(view.Tree(screen), wide);
        foreach (var (title, value) in SessionColumns(screen.Lines[0]))
        {
            after.HeaderX(title).ShouldBe(after.RowX(value), $"{title} still sits over its values");
        }
    }

    [Theory]
    [InlineData(1280f)]
    [InlineData(1200f)]
    public void EveryGripOnANarrowSessionsPageFollowsThePointerBothWays(float wide)
    {
        // Where columns drop, DIR.Lib holds a trade to each side's MaxWidth and draws grips only on drawn columns, so no drag
        // widens a column to where it would drop (it left Spend/min at 120 and gone at 1280, 2026-11-12).
        var screen = SessionsScreenOf();
        var grips = DividerActions(wide);
        grips.ShouldContain("sessions:table:divider:6", "the grip between Age and Spend/min is drawn, the ones beside a hidden column are not: " + string.Join(", ", grips));
        foreach (var hit in grips)
        {
            foreach (var by in new[] { 30f, -30f })
            {
                var view = Sessions();
                void Render() { view.Render(screen, new RectF32(0f, 0f, wide, PageHeight)); view.Render(screen, new RectF32(0f, 0f, wide, PageHeight)); }
                Render();
                var before = Painted(view).Single(r => r.Result is HitResult.ButtonHit b && b.Action == hit).X;

                Drag(view, Render, hit, by);

                // The grip stays drawn (Single throws otherwise): the drag never widens a column to where it drops.
                // The grip may stand still where the trade is held, but never moves against the pointer.
                var after = Painted(view).Single(r => r.Result is HitResult.ButtonHit b && b.Action == hit).X;
                (by < 0f ? after <= before + 0.5f : after >= before - 0.5f).ShouldBeTrue($"{hit} by {by} at {wide} moved against the pointer ({before} to {after})");
            }
        }
    }

    private List<string> DividerActions(float wide)
    {
        var view = Sessions();
        view.Render(SessionsScreenOf(), new RectF32(0f, 0f, wide, PageHeight));
        view.Render(SessionsScreenOf(), new RectF32(0f, 0f, wide, PageHeight));
        return Painted(view).Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith("sessions:table:divider:", StringComparison.Ordinal)).Distinct().ToList();
    }

    [Fact]
    public void AColumnDraggedWiderDropsWhenTheWindowNarrowsAfterwardsAsARestoredOneDoes()
    {
        var view = Sessions();
        var screen = SessionsScreenOf();
        void Render() => view.Render(screen, new RectF32(0f, 0f, 1700f, PageHeight));

        // The divider after State dragged left widens Spend, the column on its other side, past its declared 130.
        Drag(view, Render, "sessions:table:divider:4", -60f);
        view.Table.Saved["Spend"].ShouldBeGreaterThan(160f);

        // Narrowing the window, the view that dragged Spend is laid out as one given the saved widths is (which Unpin does:
        // DIR.Lib pins a column dragged wider, so without it Spend would keep its dragged width and never drop).
        var gone = 0;
        foreach (var width in new[] { 1700f, 1500f, 1400f, 1300f, 1200f, 1100f, 1000f, 900f, 800f })
        {
            var again = Sessions();
            again.Table.Restore(view.Table.Saved);
            // Rendering gives each view the page width its columns' room is measured against.
            again.Render(screen, new RectF32(0f, 0f, width, PageHeight));
            view.Render(screen, new RectF32(0f, 0f, width, PageHeight));
            var restored = Arranged(again.Tree(screen), width);
            var dragged = Arranged(view.Tree(screen), width);
            dragged.HasHeader("Spend").ShouldBe(restored.HasHeader("Spend"), $"Spend at {width}");
            if (restored.HasHeader("Spend"))
            {
                dragged.HeaderX("Age").ShouldBe(restored.HeaderX("Age"), 0.5f, $"Age at {width}");
                dragged.HeaderX("Spend").ShouldBe(restored.HeaderX("Spend"), 0.5f, $"Spend at {width}");
            }
            else
            {
                gone++;
            }
        }
        gone.ShouldBeGreaterThan(0, "Spend drops once the window is narrow enough");
    }

    [Fact]
    public void SpendNarrowedByADragDropsBelowItsDraggedWidthAndComesBackFromTheFile()
    {
        const float WideWidth = 1700f;
        var view = Sessions();
        var screen = SessionsScreenOf();
        void Render() => view.Render(screen, new RectF32(0f, 0f, WideWidth, PageHeight));

        Drag(view, Render, "sessions:table:divider:4", 40f);

        var after = Arranged(view.Tree(screen), WideWidth);
        (after.HeaderX("Age") - after.HeaderX("Spend") - ListTable.ColumnGap).ShouldBe(90f, 0.5f, "Spend gave up what was dragged");
        view.Table.Saved.ShouldContainKeyAndValue("Spend", 90f);
        view.Table.Saved.ShouldContainKeyAndValue("State", 200f);

        // A new window given the saved widths shows Spend at 90, not gone: it drops below the dragged width, not the declared one.
        var again = Sessions();
        again.Table.Restore(view.Table.Saved);
        var restored = Arranged(again.Tree(screen), WideWidth);
        restored.HasHeader("Spend").ShouldBeTrue();
        (restored.HeaderX("Age") - restored.HeaderX("Spend") - ListTable.ColumnGap).ShouldBe(90f, 0.5f);
    }

    [Fact]
    public void TheActionColumnHasNoGripAndAnOptionalColumnIsNotDraggedWiderThanThePageHasRoomFor()
    {
        const float Width = 1400f;
        var view = Sessions();
        var screen = SessionsScreenOf();
        void Render() => view.Render(screen, new RectF32(0f, 0f, Width, PageHeight));
        Render();

        // Eight titled columns and the action column: the divider before the action column (7) is a plain gap.
        Painted(view).Any(r => r.Result is HitResult.ButtonHit b && b.Action == "sessions:table:divider:7").ShouldBeFalse();

        // Model, beside the Session column that takes the slack, dragged as wide as the pointer goes: it stops where the
        // Session column would have less than its least, and stays on the page with every column after it.
        Drag(view, Render, "sessions:table:divider:0", -2000f);

        var after = Arranged(view.Tree(screen), Width);
        after.HasHeader("Model").ShouldBeTrue("the column is not dragged off the page");
        after.HasHeader("Spend/min").ShouldBeTrue();
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

        // Wide enough for everything (Age and Spend/min included), then too narrow for the optional columns: they go from the header and the rows alike.
        var wide = Arranged(view.Tree(screen), 1400f);
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
