using System.Collections.Immutable;
using System.Text;
using Console.Lib;
using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// DSN-5 v1, List mode: the Notifications page draws every notification as the library's toast (its tinted fill and edge, corner radius, shadow and
/// severity icon) in a row across the page, larger text, open rows with their actions and a Close cross on the right, closed rows one quiet line,
/// duplicates folded into one row marked x2, a card's detail folded behind Detail and never repeating its summary. CPU-rendered at 1600 and 2000
/// design units wide, and in the terminal.
/// </summary>
public sealed class NotificationsToastPageTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    private const string StrayTitle = "Stray conhost.exe 28420 is still running";
    private const string StraySummary = "19 min after session se1-pil1340 ended.";

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCaret(int column, int row, CaretStyle style) { }
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static CardRow Card(string id, CardBin bin, string title, string summary, string? detail = null) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Summary = summary, Detail = detail, At = T0, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    // Two open cards and a stray card whose detail starts with its own summary.
    internal static CardsScreen Cards() => CardsScreen.From(
    [
        Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge", "A short line on what happened."),
        Card("c2", CardBin.ForYou, "PIL795: 5 legs planned", "Five legs are ready to fly."),
        Card("c3", CardBin.Ring, StrayTitle, StraySummary, StraySummary + "\nProcess 28420 (conhost.exe), started 05:24Z, running 4 h 39 min."),
    ]);

    internal static PilotEvent Event(string id, string title, string text, NotificationSeverity severity, string node = "DE1") =>
        new(PilotEventKind.JobEnded, id, title, text, severity, node);

    internal sealed class Window : IDisposable
    {
        private ImmutableArray<Layout.ArrangedNode<float>> _arranged = [];
        private readonly PageData _data = PageData.For("sample");
        public int Width { get; }
        public int Height { get; }
        public RgbaImageRenderer Renderer { get; }
        public PilotChrome<RgbaImage> Chrome { get; }
        public ImmutableArray<Layout.ArrangedNode<float>> Arranged => _arranged;

        public Window(int width, int height = 1500, NotificationsViewMode mode = NotificationsViewMode.List)
        {
            Width = width;
            Height = height;
            Renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(Renderer, new SessionsView<RgbaImage>(Renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1f };
            Chrome.Notifications.Open = false;
            Chrome.NotificationsPage.Mode = mode;
            Chrome.Show(PilotPage.Jobs);
            var seen = Chrome.NotificationsPage.Arranged;
            Chrome.NotificationsPage.Arranged = nodes =>
            {
                _arranged = nodes;
                seen?.Invoke(nodes);
            };
        }

        public void Frame(DateTimeOffset at, CardsScreen cards) =>
            Chrome.Render(_data.Sessions with { AsOf = at }, _data.Flights, cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, Width, Height), _data.Machines);

        /// <summary>Three open cards (one a stray with detail) and three history rows, one of them posted twice.</summary>
        public void Fill()
        {
            Frame(T0, CardsScreen.Empty);
            Chrome.Notifications.Open = true;
            Chrome.Notifications.PostEvent(Event("j1", "Job one finished", "took 4 min", NotificationSeverity.Success), null, T0.AddMinutes(1));
            Chrome.Notifications.PostEvent(Event("j2", "Job two failed", "1 failed, 437 passed", NotificationSeverity.Error), null, T0.AddMinutes(2));
            Chrome.Notifications.PostEvent(Event("j3", "Job three finished", "dotnet build: exit 0", NotificationSeverity.Success, "SE1"), null, T0.AddMinutes(3));
            Chrome.Notifications.PostEvent(Event("j4", "Job three finished", "dotnet build: exit 0", NotificationSeverity.Success, "SE1"), null, T0.AddMinutes(4));
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Notifications);
            Frame(T0.AddMinutes(5), Cards());
        }

        public void Save(string name) => File.WriteAllBytes(Path.Combine(ArtifactDir(), name + ".png"), PngWriter.Encode(Renderer.Surface.Pixels, Width, Height));

        public void Dispose() => Renderer.Dispose();
    }

    internal static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given
            ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string TextOf(Layout.ArrangedNode<float> a) => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } ? v : "";

    private static float SizeOf(Layout.ArrangedNode<float> a) => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).FontSize;

    /// <summary>The page's rows: the toast-framed nodes (the library's corner radius) as wide as the page, top to bottom.</summary>
    internal static List<Layout.ArrangedNode<float>> Rows(Window w)
    {
        var radius = NotificationsHost.Colors().CornerRadius;
        return [.. w.Arranged.Where(a => a.Node.BorderStyle is { } b && b.Radius == radius && a.Bounds.Width > w.Width * 0.5f && a.Bounds.Height > 20f).OrderBy(a => a.Bounds.Y)];
    }

    internal static IEnumerable<Layout.ArrangedNode<float>> Inside(Window w, Layout.ArrangedNode<float> row) =>
        w.Arranged.Where(a => a.Bounds.X >= row.Bounds.X && a.Bounds.Y >= row.Bounds.Y
            && a.Bounds.X + a.Bounds.Width <= row.Bounds.X + row.Bounds.Width + 0.5f && a.Bounds.Y + a.Bounds.Height <= row.Bounds.Y + row.Bounds.Height + 0.5f);

    [Theory]
    [InlineData(1600)]
    [InlineData(2000)]
    public void EveryRowSpansThePageLessItsGuttersAndTheColumnCapIsGone(int width)
    {
        using var w = new Window(width);
        w.Fill();

        var rows = Rows(w);
        rows.Count.ShouldBe(6, "three open rows and three history rows (the pair folded)");
        foreach (var row in rows)
        {
            row.Bounds.Width.ShouldBeGreaterThan(width - (52f + 2f * 20f) - 1f, "a row fills the page less its rail and gutters, not a 440 column");
            row.Bounds.Width.ShouldBeLessThanOrEqualTo(width, "and never runs past the page");
        }
        rows.Select(r => MathF.Round(r.Bounds.X)).Distinct().Count().ShouldBe(1, "one left edge");
        TextOverlap.Faults(w.Arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void OpenRowsCarryTheLibrarysTintEdgeRadiusShadowIconCloseAndActionsOnTheRight()
    {
        using var w = new Window(1600);
        w.Fill();
        var tokens = NotificationsHost.Colors();
        var rows = Rows(w);

        var warn = tokens.TintedFill(NotificationSeverity.Warn);
        var info = tokens.TintedFill(NotificationSeverity.Info);
        foreach (var row in rows.Take(3))
        {
            // The open cards are Ring (Warn) and For you (Info): each row's fill and edge are its own severity's tokens.
            var severity = row.Node.Background == warn ? NotificationSeverity.Warn : NotificationSeverity.Info;
            row.Node.Background.ShouldBe(severity == NotificationSeverity.Warn ? warn : info);
            row.Node.BorderStyle!.Color.ShouldBe(tokens.TintedEdge(severity));
            row.Node.BorderStyle!.Radius.ShouldBe(tokens.CornerRadius);
            row.Node.ShadowStyle.ShouldBe(tokens.ToastShadow, "an open row carries the toast's shadow");
            row.Node.Background.ShouldNotBeNull();
            Inside(w, row).Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon }).ShouldBeGreaterThanOrEqualTo(2, "the severity icon and the Close cross");
            var button = Inside(w, row).First(a => TextOf(a).Contains("Start", StringComparison.Ordinal) || TextOf(a).Contains("Chat", StringComparison.Ordinal));
            button.Bounds.X.ShouldBeGreaterThan(row.Bounds.X + row.Bounds.Width * 0.5f, "the actions are on the right");
            Inside(w, row).Count(a => TextOf(a).Trim() == "Dismiss").ShouldBe(0, "the Close cross is the dismiss");
        }
        rows.Take(3).Select(r => r.Node.Background).Distinct().Count().ShouldBe(2, "a Ring card and a For you card differ in tint");
    }

    [Fact]
    public void ClosedRowsAreOneQuietLineEachWithNoButtonsAndNoClose()
    {
        using var w = new Window(1600);
        w.Fill();
        var rows = Rows(w);
        var open = rows.Take(3).Min(r => r.Bounds.Height);

        foreach (var row in rows.Skip(3))
        {
            var inside = Inside(w, row).ToList();
            row.Node.ShadowStyle.ShouldBeNull("a closed row is quiet: no shadow");
            inside.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon }).ShouldBe(1, "the severity icon only, no Close");
            inside.Where(a => TextOf(a).Trim() is "Dismiss" or "Chat about this").ShouldBeEmpty();
            inside.Any(a => TextOf(a).Contains("Start", StringComparison.Ordinal)).ShouldBeFalse("no buttons");
            row.Bounds.Height.ShouldBeLessThan(open, "one line, shorter than an open row");
        }
        // Title, summary and the node and time: "Job one finished" / "took 4 min" / "DE1 · 21:..".
        var closedTexts = rows.Skip(3).SelectMany(r => Inside(w, r)).Select(TextOf).ToList();
        closedTexts.ShouldContain(t => t.Trim() == "one");
        closedTexts.ShouldContain(t => t.Trim() == "took");
        closedTexts.ShouldContain(t => t.Trim() == "DE1");
    }

    [Fact]
    public void TwoNotificationsWithTheSameTitleNodeAndTextFoldIntoOneRowMarkedX2()
    {
        using var w = new Window(1600);
        w.Fill();

        w.Arranged.Count(a => TextOf(a).Trim() == "three").ShouldBe(1, "one row for the pair");
        w.Arranged.Count(a => TextOf(a).Trim() == "×2").ShouldBe(1, "marked ×2");
        w.Arranged.Count(a => TextOf(a).Contains("×", StringComparison.Ordinal)).ShouldBe(1, "and nothing else is");

        var folded = NotificationsView<RgbaImage>.Folded(w.Chrome.Notifications.History);
        folded.Count.ShouldBe(3);
        folded.Single(f => f.Count == 2).Notification.Posted.ShouldBe(w.Chrome.Notifications.History.Where(n => n.Title == "Job three finished").Max(n => n.Posted), "the newest time is kept");
    }

    [Fact]
    public void ACardsDetailIsFoldedBehindDetailAndALineThatRepeatsTheSummaryIsNotDrawn()
    {
        using var w = new Window(1600);
        w.Fill();

        var texts = () => w.Arranged.Select(TextOf).ToList();
        texts().ShouldContain(t => t.Contains("Detail", StringComparison.Ordinal), "the fold is there");
        texts().ShouldNotContain(t => t.Trim() == "Process", "the detail is folded");

        w.Chrome.Cards.ToggleDetail("c3");
        w.Frame(T0.AddMinutes(6), Cards());
        texts().ShouldContain(t => t.Trim() == "Process", "unfolded, its own line is drawn");
        // The summary's words are drawn once: the detail's first line repeats it and is not drawn.
        w.Arranged.Count(a => TextOf(a).Trim() == "se1-pil1340").ShouldBe(1, "the repeated line is not drawn");
        TextOverlap.Faults(w.Arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void TitleSummaryAndMetaAreThePrototypesSizesFromOneSetOfConstants()
    {
        NotificationToast.OpenTitleSize.ShouldBe(18f);
        NotificationToast.ClosedTitleSize.ShouldBe(17f);
        NotificationToast.SummarySize.ShouldBe(15f);
        NotificationToast.MetaSize.ShouldBe(14f);

        using var w = new Window(1600);
        w.Fill();
        float Size(string word) => SizeOf(w.Arranged.First(a => TextOf(a).Trim() == word));
        Size("Stray").ShouldBe(18f, "an open title");
        Size("short").ShouldBe(15f, "an open summary");
        var metas = Rows(w).SelectMany(r => Inside(w, r)).Where(a => TextOf(a).Trim() == "·" && a.Node is Layout.Node.Leaf).ToList();
        metas.ShouldNotBeEmpty();
        metas.Select(SizeOf).Distinct().ShouldBe([14f], "meta");
        Size("one").ShouldBe(17f, "a closed title");
        Size("took").ShouldBe(15f, "a closed summary");
    }

    [Fact]
    public void TheTerminalDrawsTheSameDeclarationWithNoTextCutSilently()
    {
        const int columns = 160, rows = 60;
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Notifications.Open = true;
        chrome.NotificationsPage.GridAvailable = false; // as pilot-tui sets it
        chrome.Notifications.PostEvent(Event("j3", "Job three finished", "dotnet build: exit 0", NotificationSeverity.Success, "SE1"), null, T0.AddMinutes(3));
        chrome.Notifications.PostEvent(Event("j4", "Job three finished", "dotnet build: exit 0", NotificationSeverity.Success, "SE1"), null, T0.AddMinutes(4));
        chrome.Notifications.Sync(Cards().Open, _ => "DE1", T0.AddMinutes(5));

        var page = chrome.NotificationsPage.Tree("sample", T0.AddMinutes(5), TerminalFrame.PageColumnsWithBar(columns) * 8f);
        var rail = TerminalRail.Tree(PilotPage.Notifications, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
        var arranged = TerminalFrame.Build(rail, page, null, columns, rows, CellMeasureContext.PixelAuthored, null, (chrome.Notifications, PilotPage.Notifications));
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);

        var text = new StringBuilder();
        for (var y = 0; y < rows; y++)
        {
            text.AppendLine(string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph)).TrimEnd());
        }
        File.WriteAllText(Path.Combine(ArtifactDir(), "notifications-toast-tui.txt"), text.ToString());
        var shown = text.ToString();
        shown.ShouldContain("Job three finished", Case.Sensitive, "the whole title");
        shown.ShouldContain("×2", Case.Sensitive, "the fold is marked");
        shown.ShouldContain("dotnet build: exit 0", Case.Sensitive, "the whole summary");
        shown.ShouldContain("TIA1818's PR #63 is ready to merge", Case.Sensitive, "an open title, whole");
        shown.ShouldNotContain("…", Case.Sensitive, "nothing cut with an ellipsis");
    }

    [Theory]
    [InlineData(1600)]
    [InlineData(2000)]
    public void TheCapturesAreWrittenBesideTheTestOutput(int width)
    {
        using var w = new Window(width);
        w.Fill();
        // Beside the test output, or in the job's artifacts folder when it is set, as the other capture tests do.
        w.Save($"notifications-toast-list-{width}");
        File.Exists(Path.Combine(ArtifactDir(), $"notifications-toast-list-{width}.png")).ShouldBeTrue();
    }
}
