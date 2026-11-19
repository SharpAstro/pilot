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
/// The Notifications page (PIL1184), DIR.Lib's list frame over the For you bar's feed, CPU-rendered on a stepped clock: it opens from the rail
/// and from the bell, lists what needs you first and the history newest first, Clear empties the history and leaves what needs you, and the
/// keep time is a setting that prunes and is kept in the GUI's settings file. Captures at 1920 and 2880 wide and 160x44 in the terminal go to
/// PILOT_JOB_ARTIFACTS.
/// </summary>
public sealed class NotificationsPageTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

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

    private static CardRow Card(string id, CardBin bin, string title) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Summary = "A short line on what happened.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static CardsScreen Ringing() => CardsScreen.From([Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge"), Card("c2", CardBin.ForYou, "PIL795: 5 legs planned")]);

    private static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PilotEvent Event(string title, NotificationSeverity severity = NotificationSeverity.Success) =>
        new(PilotEventKind.JobEnded, "j1", title, "took 4 min", severity, "DE1");

    private sealed class Window : IDisposable
    {
        private readonly int _width, _height;
        private readonly PageData _data = PageData.For("sample");
        private ImmutableArray<Layout.ArrangedNode<float>> _arranged = [];
        public RgbaImageRenderer Renderer { get; }
        public PilotChrome<RgbaImage> Chrome { get; }

        public Window(int width, int height, float dpi = 1f)
        {
            _width = width;
            _height = height;
            Renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(Renderer, new SessionsView<RgbaImage>(Renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Jobs);
            var seen = Chrome.NotificationsPage.Arranged;
            Chrome.NotificationsPage.Arranged = nodes =>
            {
                _arranged = nodes;
                seen?.Invoke(nodes);
            };
        }

        public NotificationsHost Bar => Chrome.Notifications;

        public int Width => _width;

        public void Frame(DateTimeOffset at, CardsScreen? cards = null) =>
            Chrome.Render(_data.Sessions with { AsOf = at }, _data.Flights, cards ?? CardsScreen.Empty, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, _width, _height), _data.Machines);

        /// <summary>The page's text, top to bottom, as the last frame arranged it.</summary>
        public List<string> Texts() => [.. _arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .OrderBy(a => a.Bounds.Y).ThenBy(a => a.Bounds.X).Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value)];

        public void Save(string name) => File.WriteAllBytes(Path.Combine(ArtifactDir(), name + ".png"), PngWriter.Encode(Renderer.Surface.Pixels, _width, _height));

        public void Dispose() => Renderer.Dispose();
    }

    // Two cards that need the person, and three events that went to the history at different times.
    private static void Fill(Window w)
    {
        w.Frame(T0, CardsScreen.Empty);
        w.Bar.Open = true;
        w.Bar.PostEvent(Event("Job one finished"), null, T0.AddMinutes(1));
        w.Bar.PostEvent(Event("Job two failed", NotificationSeverity.Error), null, T0.AddMinutes(2));
        w.Bar.PostEvent(Event("Job three finished"), null, T0.AddMinutes(3));
        w.Bar.Open = false;
        w.Frame(T0.AddMinutes(4), Ringing());
    }

    [Fact]
    public void TheRailHasThePageAndItIsNamedAndKeyed()
    {
        PilotChrome<RgbaImage>.Pages.ShouldContain(p => p.Page == PilotPage.Notifications && p.Label == "Notifications");
        PilotPageNames.TryParse("notifications", out var page).ShouldBeTrue();
        page.ShouldBe(PilotPage.Notifications);
        TerminalRail.KeyFor(PilotPage.Notifications).ShouldBe(TerminalRail.NotificationsKey);
        TerminalRail.PlainChord('n').ShouldBe(new KeyChord(InputKey.N, InputModifier.None));
    }

    [Fact]
    public void TheRailShowsThePageWithNeedsYouFirstThenTheHistoryNewestFirst()
    {
        using var w = new Window(1920, 1080);
        Fill(w);
        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(T0.AddMinutes(4), Ringing());
        w.Chrome.Page.ShouldBe(PilotPage.Notifications);

        var texts = w.Texts();
        var needs = texts.FindIndex(t => t.StartsWith("Needs you", StringComparison.Ordinal));
        var history = texts.FindIndex(t => t.StartsWith("History", StringComparison.Ordinal));
        needs.ShouldBeGreaterThan(-1);
        history.ShouldBeGreaterThan(needs, "Needs you comes first");
        texts.FindIndex(t => t.Contains("ready to merge", StringComparison.Ordinal)).ShouldBeInRange(needs, history);
        var three = texts.FindIndex(t => t == "Job three finished");
        var two = texts.FindIndex(t => t == "Job two failed");
        var one = texts.FindIndex(t => t == "Job one finished");
        three.ShouldBeGreaterThan(history);
        two.ShouldBeGreaterThan(three, "newest first");
        one.ShouldBeGreaterThan(two);
        texts.ShouldContain("Clear");
        texts.ShouldContain("DE1", "each line names its node");
        w.Bar.History.Select(n => n.Severity).ShouldContain(NotificationSeverity.Error);
    }

    [Fact]
    public void TheBellOpensThePage()
    {
        using var w = new Window(1920, 1080);
        Fill(w);
        var bell = Enumerable.Range(0, 200).Select(y => (X: w.Width - 24f, Y: (float)y))
            .FirstOrDefault(p => w.Chrome.NotificationsLayer.HitTest(p.X, p.Y) is NotificationHit { Part: NotificationPart.Bell });
        bell.X.ShouldBeGreaterThan(0f, "the strip has its bell");

        w.Chrome.HandleInput(new InputEvent.MouseDown(bell.X, bell.Y, MouseButton.Left)).ShouldBeTrue();
        w.Chrome.Page.ShouldBe(PilotPage.Notifications);
        w.Bar.Open.ShouldBeFalse("the bell opens the page; the bar's own key and Collapse button toggle the bar");
    }

    [Fact]
    public void TheOpenBarHasAButtonToThePage()
    {
        using var w = new Window(1920, 1080);
        w.Bar.Open = true;
        w.Frame(T0, Ringing());
        w.Bar.OpenPage.ShouldNotBeNull();
        w.Bar.OpenPage!.Invoke();
        w.Chrome.Page.ShouldBe(PilotPage.Notifications);
    }

    [Fact]
    public void ClearEmptiesTheHistoryAndLeavesNeedsYou()
    {
        using var w = new Window(1920, 1080);
        Fill(w);
        w.Bar.History.Count.ShouldBe(3);
        w.Bar.Clear();
        w.Bar.History.ShouldBeEmpty();
        w.Bar.Feed.NeedsYou.Count.ShouldBe(2);

        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(T0.AddMinutes(5), Ringing());
        var texts = w.Texts();
        texts.ShouldNotContain("Job one finished");
        texts.ShouldContain(t => t.StartsWith("Needs you", StringComparison.Ordinal));
        texts.ShouldNotContain(t => t.StartsWith("History", StringComparison.Ordinal));
    }

    [Fact]
    public void TheKeepTimeIsASettingThatPrunesAndIsKept()
    {
        using var w = new Window(1920, 1080);
        w.Bar.KeepFor.ShouldBe(TimeSpan.FromHours(24), "24 h until one is chosen");
        Fill(w);
        w.Bar.Feed.Advance(T0.AddHours(23));
        w.Bar.History.Count.ShouldBe(3);
        w.Bar.Feed.Advance(T0.AddHours(25));
        w.Bar.History.ShouldBeEmpty("older than 24 h");

        // A shorter keep time prunes sooner.
        using var s = new Window(1920, 1080);
        Fill(s);
        s.Bar.KeepFor = TimeSpan.FromHours(1);
        s.Bar.Feed.Advance(T0.AddHours(2));
        s.Bar.History.ShouldBeEmpty();

        // The file keeps the choice, and says nothing for the default.
        var path = Path.Combine(Path.GetTempPath(), $"pilot-keep-{Guid.NewGuid():N}.json");
        try
        {
            using (var file = GuiSettingsFile.Open(path))
            {
                var bar = new NotificationsHost();
                file.Attach(bar);
                bar.KeepFor.ShouldBe(NotificationsHost.DefaultKeepFor);
                bar.KeepFor = TimeSpan.FromDays(3);
                file.Flush();
            }
            File.ReadAllText(path).ShouldContain("notificationKeepHours");
            var again = new NotificationsHost();
            using (var file = GuiSettingsFile.Open(path))
            {
                file.Attach(again);
                again.KeepFor.ShouldBe(TimeSpan.FromDays(3));
                again.KeepFor = NotificationsHost.DefaultKeepFor;
                file.Flush();
            }
            File.ReadAllText(path).ShouldNotContain("notificationKeepHours");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheSettingsPageOffersTheKeepTimes()
    {
        using var w = new Window(1920, 1080);
        w.Chrome.Show(PilotPage.Settings);
        w.Frame(T0);
        w.Chrome.Settings.Notifications.ShouldBeSameAs(w.Bar);
        NotificationsHost.KeepChoices.ShouldContain(c => c.Keep == TimeSpan.FromHours(24));
    }

    [Fact]
    public void CapturesOfThePageAtBothWidths()
    {
        foreach (var (width, height, dpi) in new[] { (1920, 1080, 1f), (2880, 1620, 1.5f) })
        {
            using var w = new Window(width, height, dpi);
            Fill(w);
            w.Chrome.Show(PilotPage.Notifications);
            w.Frame(T0.AddMinutes(4), Ringing());
            w.Save($"notifications-page-{width}");
        }
    }

    [Fact]
    public void TheTerminalShowsThePageAndCapturesIt()
    {
        const int columns = 160, rows = 44;
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Notifications.Open = true;
        chrome.Notifications.PostEvent(Event("Job one finished"), null, T0.AddMinutes(1));
        chrome.Notifications.PostEvent(Event("Job two failed", NotificationSeverity.Error), null, T0.AddMinutes(2));
        chrome.Notifications.Sync(Ringing().Open, _ => "DE1", T0.AddMinutes(3));

        var page = chrome.NotificationsPage.Tree("sample", T0.AddMinutes(3), TerminalFrame.PageColumnsWithBar(columns) * 8f);
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
        File.WriteAllText(Path.Combine(ArtifactDir(), $"notifications-tui-{columns}x{rows}.txt"), text.ToString());
        var shown = text.ToString();
        shown.ShouldContain("Needs you", Case.Sensitive);
        shown.ShouldContain("History", Case.Sensitive);
        // The frame takes the toast rule from the page it is given, as Program.cs builds it: the staying cards toast on any other page, and
        // here none covers the header's counts line.
        chrome.Notifications.ToastsOnPage.ShouldBeFalse();
        shown.ShouldContain("you, 2 in the history", Case.Sensitive, "the header's counts line is whole: no toast stands over it");
        shown.IndexOf("Job two failed", StringComparison.Ordinal).ShouldBeLessThan(shown.IndexOf("Job one finished", StringComparison.Ordinal), "newest first");
    }
}
