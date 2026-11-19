using System.Numerics;
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
/// The toasts of the collapsed For you bar (PIL1184), on DIR.Lib's notifications component and a stepped clock (every render is given its
/// time, nothing waits): a ringing card is a staying toast until pressed or dismissed; a job that ended is a fading toast of 5 s that
/// pauses under the pointer and is kept in the history afterwards; no toast is placed under the pointer; at most three; none while
/// the bar is open. The captures (a staying toast, a fading toast, the toasts dodging the pointer) at 1920 and 2880 wide in pilot-gui and
/// 160x44 in pilot-tui go to PILOT_JOB_ARTIFACTS.
/// </summary>
public sealed class ToastTests
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

    /// <summary>The sample's jobs, the first one in <paramref name="state"/> (and named by it).</summary>
    private static JobsScreen JobsWith(PageData data, JobState state) => data.Jobs with
    {
        Lines = [.. data.Jobs.Lines.Select((l, i) => i == 0 ? l with { Row = l.Row with { State = state }, State = state.ToString().ToLowerInvariant() } : l)],
    };

    private sealed class Window : IDisposable
    {
        private readonly int _width, _height;
        private readonly PageData _data = PageData.For("sample");
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
        }

        public PageData Data => _data;

        public NotificationFeed Feed => Chrome.Notifications.Feed;

        /// <summary>One frame at <paramref name="at"/>.</summary>
        public void Frame(DateTimeOffset at, CardsScreen? cards = null, JobState job = JobState.Running, (float X, float Y)? pointer = null)
        {
            Chrome.Pointer = pointer;
            if (pointer is null)
            {
                Chrome.Notifications.Pointer = null;
            }
            Chrome.Render(_data.Sessions with { AsOf = at }, _data.Flights, cards ?? CardsScreen.Empty, JobsWith(_data, job), _data.Fleet, _data.Repos, new RectF32(0, 0, _width, _height), _data.Machines);
        }

        public void Save(string name) => File.WriteAllBytes(Path.Combine(ArtifactDir(), name + ".png"), PngWriter.Encode(Renderer.Surface.Pixels, _width, _height));

        public void Dispose() => Renderer.Dispose();
    }

    private static bool IsToast(ClickableRegion region) => region.Result is NotificationHit { Part: NotificationPart.Toast };

    private static List<Notification> Fading(Window w) => [.. w.Feed.Toasts.Where(t => t.Notification.Kind == NotificationKind.Fading).Select(t => t.Notification)];

    [Fact]
    public void ARingingCardWhileCollapsedIsAStayingToastUntilItIsGoneOrDismissed()
    {
        using var w = new Window(1920, 1080);
        w.Frame(T0, CardsScreen.Empty);
        w.Frame(T0.AddSeconds(1), Ringing());

        var staying = w.Feed.Toasts.Where(t => t.Notification.Kind == NotificationKind.Staying).ToList();
        staying.Count.ShouldBe(2);
        staying.ShouldAllBe(t => t.RemainingFraction == 1.0);
        staying.Select(t => t.Notification.Severity).ShouldContain(NotificationSeverity.Warn, "the ringing card is a warning");
        staying.ShouldAllBe(t => t.Notification.Row != null, "the card's own choice is a button on its toast");

        // An hour on, with no one at the window: still there.
        w.Frame(T0.AddHours(1), Ringing());
        w.Feed.Toasts.Count(t => t.Notification.Kind == NotificationKind.Staying).ShouldBe(2);

        // Dismissed by the person: the toast goes, into the history.
        var ringing = w.Feed.Toasts.Single(t => t.Notification.Severity == NotificationSeverity.Warn).Notification;
        w.Feed.Dismiss(ringing.Id);
        w.Feed.Toasts.ShouldNotContain(t => t.Notification.Id == ringing.Id);
        w.Feed.History.ShouldContain(n => n.Id == ringing.Id);

        // The other one is answered: the card leaves the panel, its toast leaves, and the history keeps it.
        w.Frame(T0.AddHours(1).AddSeconds(1), CardsScreen.Empty);
        w.Feed.Toasts.ShouldBeEmpty();
        w.Feed.History.Count.ShouldBe(2);
    }

    [Fact]
    public void AJobThatEndedIsAFadingToastOfFiveSecondsAndTheHistoryKeepsIt()
    {
        using var w = new Window(1920, 1080);
        w.Frame(T0, job: JobState.Running);
        Fading(w).ShouldBeEmpty("what is already there is not news");

        w.Frame(T0.AddSeconds(10), job: JobState.Succeeded);
        var toast = Fading(w).ShouldHaveSingleItem();
        toast.Severity.ShouldBe(NotificationSeverity.Success);
        w.Feed.Toasts.Single().RemainingFraction.ShouldBe(1.0, 0.01);

        w.Frame(T0.AddSeconds(14.9), job: JobState.Succeeded);
        Fading(w).Count.ShouldBe(1, "still there just short of 5 s");
        w.Feed.Toasts.Single().RemainingFraction.ShouldBeLessThan(0.1);

        w.Frame(T0.AddSeconds(15.1), job: JobState.Succeeded);
        Fading(w).ShouldBeEmpty("gone after 5 s");
        w.Feed.History.ShouldContain(n => n.Id == toast.Id, "kept for the list afterwards");
        w.Feed.History.Single(n => n.Id == toast.Id).Posted.ShouldBe(T0.AddSeconds(10));

        // Kept for 24 h, then pruned by the keep time passed in.
        w.Frame(T0.AddHours(25), job: JobState.Succeeded);
        w.Feed.History.ShouldBeEmpty();
    }

    [Fact]
    public void AFailedJobIsAnErrorToastAndAnEventWhileTheBarIsOpenGoesStraightToTheHistory()
    {
        using var w = new Window(1920, 1080);
        w.Frame(T0, job: JobState.Running);
        w.Frame(T0.AddSeconds(1), job: JobState.Failed);
        Fading(w).ShouldHaveSingleItem().Severity.ShouldBe(NotificationSeverity.Error);

        using var open = new Window(1920, 1080);
        open.Chrome.Notifications.Open = true;
        open.Frame(T0, job: JobState.Running);
        open.Frame(T0.AddSeconds(1), job: JobState.Succeeded);
        Fading(open).ShouldBeEmpty("no toast while the bar is open");
        open.Feed.History.Count.ShouldBe(1, "the list still has it");
    }

    [Fact]
    public void AFadingToastPausesWhileThePointerIsOverIt()
    {
        const float dpi = 1.5f;
        using var w = new Window(2880, 1620, dpi);
        w.Frame(T0, job: JobState.Running);
        w.Frame(T0.AddSeconds(1), job: JobState.Succeeded);

        // A point in the toast the placement rule's estimate does not cover (the real card is taller than it): there the toast stays put under the pointer.
        var page = w.Chrome.PageBounds!.Value;
        var x = page.Right - NotificationsHost.Style.Margin * dpi - NotificationsHost.Style.ToastWidth * dpi / 2;
        (float X, float Y)? over = null;
        for (var y = page.Y + 40f; y < page.Y + 400f && over is null; y += 4f)
        {
            if (NotificationsLayout.ToastIdOf(w.Chrome.NotificationsLayer.HitTest(x, y)) is not null
                && !NotificationsLayout.TopRightRegion(page, 1, NotificationsHost.Style).Contains(new Vector2(x, y)))
            {
                over = (x, y);
            }
        }
        over.ShouldNotBeNull("the fading toast declares its hover region below the placement estimate");

        w.Frame(T0.AddSeconds(1.5), job: JobState.Succeeded, pointer: over);
        var before = w.Feed.Toasts.Single().RemainingFraction;
        w.Frame(T0.AddSeconds(4), job: JobState.Succeeded, pointer: over);
        w.Frame(T0.AddSeconds(9), job: JobState.Succeeded, pointer: over);
        w.Feed.Toasts.Single().RemainingFraction.ShouldBe(before, 0.001, "the countdown stops under the pointer");

        w.Frame(T0.AddSeconds(9.1), job: JobState.Succeeded);
        w.Frame(T0.AddSeconds(15), job: JobState.Succeeded);
        Fading(w).ShouldBeEmpty("it fades once the pointer leaves");
    }

    [Theory]
    [InlineData(1920, 1080, 1f)]
    [InlineData(2880, 1620, 1.5f)]
    public void NoToastIsPlacedUnderThePointerAndNoMoreThanThreeShow(int width, int height, float dpi)
    {
        using var w = new Window(width, height, dpi);
        w.Frame(T0, job: JobState.Running);
        w.Frame(T0.AddSeconds(1), job: JobState.Succeeded);
        var page = w.Chrome.PageBounds!.Value;
        var corner = new Vector2(page.Right - NotificationsHost.Style.Margin - NotificationsHost.Style.ToastWidth / 2f, page.Y + NotificationsHost.Style.Margin + 10f);

        w.Frame(T0.AddSeconds(1.1), job: JobState.Succeeded, pointer: (corner.X, corner.Y));
        NotificationsLayout.ToastIdOf(w.Chrome.NotificationsLayer.HitTest(corner.X, corner.Y)).ShouldBeNull("the toast is not under the pointer");
        // It moved: the lower half of the content, at the toast's column, now holds a toast (scanned, so the test does not depend on the card's height).
        var moved = false;
        for (var y = page.Bottom - 2f; y > page.Y + page.Height / 2f && !moved; y -= 4f)
        {
            moved = NotificationsLayout.ToastIdOf(w.Chrome.NotificationsLayer.HitTest(corner.X, y)) is not null;
        }
        moved.ShouldBeTrue("it moved to the bottom right");
        w.Save($"toasts-dodging-the-pointer-{width}");

        // Five at once: three show.
        var more = w.Feed;
        for (var i = 0; i < 5; i++)
        {
            more.Post(new Notification { Title = "Event " + i, Text = "something ended", Kind = NotificationKind.Fading }, T0.AddSeconds(2));
        }
        more.Toasts.Count.ShouldBe(3);
        w.Frame(T0.AddSeconds(2.1), job: JobState.Succeeded);
        more.Toasts.Count.ShouldBeLessThanOrEqualTo(3);
        more.Toasts[0].Notification.Title.ShouldBe("Event 4", "newest on top");
    }

    [Fact]
    public void PilotsEventsAreTheOnesThatChangedNotTheOnesThatWereThere()
    {
        var watch = new PilotEventWatch();
        var data = PageData.For("sample");
        watch.Observe(JobsWith(data, JobState.Succeeded), data.Flights, null).ShouldBeEmpty("the first look only learns");
        watch.Observe(JobsWith(data, JobState.Succeeded), data.Flights, null).ShouldBeEmpty("a job that was done is not news");

        var running = new PilotEventWatch();
        running.Observe(JobsWith(data, JobState.Running), null, null);
        var ended = running.Observe(JobsWith(data, JobState.Succeeded), null, null);
        ended.ShouldHaveSingleItem().Kind.ShouldBe(PilotEventKind.JobEnded);
        running.Observe(JobsWith(data, JobState.Succeeded), null, null).ShouldBeEmpty("told once");
    }

    [Fact]
    public void AFadingToastLeavesOnTheWallClockWhenThePagesTimeStandsStill()
    {
        // Home builds no new pages while nothing changed, so the snapshot's AsOf stops: the window's Clock carries the countdown.
        using var w = new Window(1920, 1080);
        var now = T0;
        w.Chrome.Clock = () => now;
        w.Frame(T0, job: JobState.Running);
        now = T0.AddSeconds(1);
        w.Frame(T0, job: JobState.Succeeded);
        Fading(w).ShouldHaveSingleItem();
        now = T0.AddSeconds(4);
        w.Frame(T0, job: JobState.Succeeded);
        w.Feed.Toasts.Single().RemainingFraction.ShouldBeLessThan(0.6, "the countdown moved with the clock, the snapshot's time unchanged");
        now = T0.AddSeconds(7);
        w.Frame(T0, job: JobState.Succeeded);
        Fading(w).ShouldBeEmpty();
    }

    [Fact]
    public void APressOnAToastOpensWhatItIsAboutForEachKindOfEvent()
    {
        var data = PageData.For("sample");
        using var w = new Window(1920, 1080);
        var line = data.Flights.Lines[0];
        FlightsScreen FlightIn(FlightState state) => data.Flights with { Lines = [line with { Row = line.Row with { State = state } }] };
        DesignsScreen DesignAt(int versions) => new([new DesignRow { Id = "DSN-1", Title = "A design", Versions = versions }], "sample", T0);

        w.Chrome.ObserveEvents(null, FlightIn(FlightState.Flying), DesignAt(1), T0);
        w.Chrome.ObserveEvents(null, FlightIn(FlightState.Landed), DesignAt(2), T0.AddSeconds(1));
        var toasts = Fading(w);
        toasts.Count.ShouldBe(2);

        // Each toast, pressed as the layer's hit would be, opens its page and goes into the history as handled.
        var landed = toasts.Single(t => t.Title.Contains("landed"));
        w.Chrome.Show(PilotPage.Home);
        w.Chrome.PressNotification(new NotificationHit(NotificationPart.Toast, landed.Id)).ShouldBeTrue();
        w.Chrome.Page.ShouldBe(PilotPage.Flights);
        w.Chrome.Flights.OpenCode.ShouldBe(line.Code);

        var design = toasts.Single(t => t.Title.Contains("published"));
        w.Chrome.Show(PilotPage.Home);
        w.Chrome.PressNotification(new NotificationHit(NotificationPart.Toast, design.Id)).ShouldBeTrue();
        w.Chrome.Page.ShouldBe(PilotPage.Designs);
        w.Chrome.Designs.OpenId.ShouldBe("DSN-1");
        Fading(w).ShouldBeEmpty("handled toasts leave");

        w.Frame(T0.AddSeconds(2), job: JobState.Running);
        w.Frame(T0.AddSeconds(3), job: JobState.Succeeded);
        var job = Fading(w).ShouldHaveSingleItem();
        w.Chrome.Show(PilotPage.Home);
        w.Chrome.PressNotification(new NotificationHit(NotificationPart.Toast, job.Id)).ShouldBeTrue();
        w.Chrome.Page.ShouldBe(PilotPage.Jobs);
    }

    [Fact]
    public void NoToastStandsOverTheNotificationsPage()
    {
        using var w = new Window(1920, 1080);
        w.Frame(T0, CardsScreen.From([new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/X", Title = "A ringing card", Summary = "s", At = Live.At, State = CardState.Open }]));
        w.Feed.Toasts.ShouldNotBeEmpty("the card toasts on another page while collapsed");
        var before = new List<ClickableRegion>();
        w.Chrome.NotificationsLayer.CollectPaintedRegions(before);
        before.Any(IsToast).ShouldBeTrue("the toast is painted on the Jobs page");
        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(T0.AddSeconds(1), CardsScreen.From([new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/X", Title = "A ringing card", Summary = "s", At = Live.At, State = CardState.Open }]));
        w.Chrome.Notifications.ToastsOnPage.ShouldBeFalse();
        w.Chrome.Notifications.Animating.ShouldBeFalse();
        // What the layer paints, not only the flags: no toast region on the page.
        var regions = new List<ClickableRegion>();
        w.Chrome.NotificationsLayer.CollectPaintedRegions(regions);
        regions.Any(IsToast).ShouldBeFalse("a toast is painted over the Notifications page");
    }

    [Fact]
    public void CapturesOfAStayingAndAFadingToastAtBothWidths()
    {
        foreach (var (width, height, dpi) in new[] { (1920, 1080, 1f), (2880, 1620, 1.5f) })
        {
            using var staying = new Window(width, height, dpi);
            staying.Frame(T0, CardsScreen.Empty);
            staying.Frame(T0.AddSeconds(1), Ringing());
            staying.Save($"toast-staying-{width}");

            using var fading = new Window(width, height, dpi);
            fading.Frame(T0, job: JobState.Running);
            fading.Frame(T0.AddSeconds(1), job: JobState.Succeeded);
            fading.Frame(T0.AddSeconds(3), job: JobState.Succeeded);
            fading.Save($"toast-fading-{width}");
        }
    }

    [Fact]
    public void TheTerminalShowsTheToastsAndCapturesThem()
    {
        const int columns = 160, rows = 44;
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Notifications.Open = false;
        var data = PageData.For("sample");
        chrome.Notifications.Sync(CardsScreen.Empty.Open, _ => null, T0);
        chrome.ObserveEvents(JobsWith(data, JobState.Running), null, null, T0);
        var cards = Ringing();
        chrome.Notifications.Sync(cards.Open, _ => null, T0.AddSeconds(1));
        chrome.ObserveEvents(JobsWith(data, JobState.Succeeded), null, null, T0.AddSeconds(1));
        chrome.Notifications.Feed.Toasts.Count.ShouldBe(3, "two cards and an event, the most there are");

        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        var page = chrome.Home.Tree(new HomeScreen(new FlightsScreen([], "sample", Live.At), sessions, jobs, cards, []));
        var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
        var arranged = TerminalFrame.Build(rail, page, null, columns, rows, CellMeasureContext.PixelAuthored, null, (chrome.Notifications, PilotPage.Home));
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);

        var text = new StringBuilder();
        for (var y = 0; y < rows; y++)
        {
            text.AppendLine(string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph)).TrimEnd());
        }
        File.WriteAllText(Path.Combine(ArtifactDir(), $"toasts-tui-{columns}x{rows}.txt"), text.ToString());
        text.ToString().ShouldContain("ready to merge", Case.Sensitive, "a staying toast is in the terminal frame");
    }
}
