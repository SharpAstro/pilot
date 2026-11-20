using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A toast over the Sessions page is a surface (DIR.Lib#282, taken by PIL1220): with the pointer resting on the card's body over a session row,
/// the row is not lit and shows no tooltip, and a press and a wheel turn on the card never reach the row. Run through the router the window
/// uses (<c>chrome.Widgets</c>, the page's own settings) and a stepped clock: nothing waits, no process starts. The row in the open, beside the
/// card, is the control that lights, tips, selects and scrolls. Captures of both toast kinds with the pointer on the card go to PILOT_JOB_ARTIFACTS.
/// </summary>
public sealed class ToastSurfaceTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    public enum Kind { Staying, Fading }

    public static TheoryData<Kind, int, int, float> Cases => new()
    {
        { Kind.Staying, 1920, 1080, 1f },
        { Kind.Staying, 2880, 1620, 1.5f },
        { Kind.Fading, 1920, 1080, 1f },
        { Kind.Fading, 2880, 1620, 1.5f },
    };

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static CardRow Card(string id, CardBin bin, string title) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Summary = "A short line on what happened.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static CardsScreen Ringing() => CardsScreen.From([Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge")]);

    private sealed class Rig : IDisposable
    {
        private readonly PageData _data = PageData.For("sample");
        private readonly int _width, _height;
        private readonly Kind _kind;
        private readonly StepClock _clock = new();
        private DateTimeOffset _at = T0;
        private readonly RgbaImageRenderer _renderer;

        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }

        public Rig(Kind kind, int width, int height, float dpi)
        {
            _kind = kind;
            _width = width;
            _height = height;
            _renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Sessions);
            Router = new InputRouter(Chrome.Sessions.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets, Clock = _clock };
            Paint(withToast: false);
            if (kind == Kind.Fading)
            {
                _at = T0.AddSeconds(1);
            }
            Paint(withToast: true);
            Paint(withToast: true);
        }

        /// <summary>One frame, as the window draws it: the chrome, the due tooltip, then the router's after-paint.</summary>
        public void Paint(bool withToast = true)
        {
            var cards = _kind == Kind.Staying && withToast ? Ringing() : CardsScreen.Empty;
            var jobs = _kind == Kind.Fading && withToast ? Jobs(JobState.Succeeded) : Jobs(JobState.Running);
            Chrome.Render(Sessions(), _data.Flights, cards, jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, _width, _height), _data.Machines);
            Chrome.Tooltips.Render(Router.Tooltip, new RectF32(0, 0, _width, _height));
            Router.AfterPaint();
        }

        /// <summary>The sample sessions repeated until the list runs past the card at the bottom, so rows lie under it and the list can scroll.</summary>
        private SessionsScreen Sessions() => _data.Sessions with
        {
            AsOf = _at,
            Lines = [.. Enumerable.Range(0, 8).SelectMany(n => _data.Sessions.Lines.Select(l => n == 0 ? l : l with { Name = l.Name + "-" + n, Row = l.Row with { Name = l.Row.Name + "-" + n, Id = l.Row.Id + "-" + n } }))],
        };

        private JobsScreen Jobs(JobState state) => _data.Jobs with
        {
            Lines = [.. _data.Jobs.Lines.Select((l, i) => i == 0 ? l with { Row = l.Row with { State = state }, State = state.ToString().ToLowerInvariant() } : l)],
        };

        public void Move((float X, float Y) at)
        {
            Router.Handle(new InputEvent.MouseMove(at.X, at.Y, MouseButton.None));
            Paint();
            Paint();
        }

        /// <summary>The tooltip due once the pointer has rested at <paramref name="at"/> past the delay.</summary>
        public TooltipRequest? TooltipAfterRest((float X, float Y) at)
        {
            Move(at);
            _clock.Advance(TimeSpan.FromSeconds(1));
            return Router.Tooltip;
        }

        public void Press((float X, float Y) at)
        {
            Chrome.HandleInput(new InputEvent.MouseDown(at.X, at.Y));
            Router.Handle(new InputEvent.MouseDown(at.X, at.Y));
            Router.Handle(new InputEvent.MouseUp(at.X, at.Y, MouseButton.Left));
            Paint();
            Paint();
        }

        public bool Wheel((float X, float Y) at, float delta = -3f) => Router.Handle(new InputEvent.Scroll(delta, at.X, at.Y));

        public RGBAColor32 PixelAt((float X, float Y) at)
        {
            var image = _renderer.Surface;
            var i = (((int)at.Y * image.Width) + (int)at.X) * 4;
            return new RGBAColor32(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
        }

        public List<ClickableRegion> SessionRows()
        {
            var regions = new List<ClickableRegion>();
            Chrome.Sessions.CollectPaintedRegions(regions);
            return [.. regions.Where(r => r.Result is HitResult.ListItemHit)];
        }

        public List<ClickableRegion> ToastRegions()
        {
            var regions = new List<ClickableRegion>();
            Chrome.NotificationsLayer.CollectPaintedRegions(regions);
            return regions;
        }

        public void Save(string name)
        {
            var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), PngWriter.Encode(_renderer.Surface.Pixels, _width, _height));
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static bool Inside(ClickableRegion r, float x, float y) => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;

    /// <summary>
    /// A point on the card's body (inside a surface of the toast layer, outside any button or other region of it) that lies over a session row,
    /// and a point of that same row in the open, left of the card.
    /// </summary>
    private static ((float X, float Y) Body, (float X, float Y) Open, ClickableRegion Row) Find(Rig rig)
    {
        var toast = rig.ToastRegions();
        var surfaces = toast.Where(r => r.IsSurface).ToList();
        surfaces.ShouldNotBeEmpty("the toast is a surface");
        var page = rig.Chrome.PageBounds!.Value;
        foreach (var surface in surfaces)
        {
            surface.X.ShouldBeGreaterThan(page.X + page.Width / 2f, "the toast is at the right");
            (surface.Y + surface.Height).ShouldBeGreaterThan(page.Y + page.Height / 2f, "and in the lower half");
        }

        var rows = rig.SessionRows();
        for (var y = surfaces.Min(s => s.Y) + 4f; y < surfaces.Max(s => s.Y + s.Height); y += 3f)
        {
            for (var x = surfaces.Min(s => s.X) + 4f; x < surfaces.Max(s => s.X + s.Width); x += 3f)
            {
                if (!surfaces.Any(s => Inside(s, x, y)) || toast.Any(r => !r.IsSurface && Inside(r, x, y)))
                {
                    continue;
                }
                if (rows.Cast<ClickableRegion?>().FirstOrDefault(r => Inside(r!.Value, x, y)) is { } row)
                {
                    var open = (X: row.X + 40f, Y: y);
                    if (!surfaces.Any(s => Inside(s, open.X, open.Y)))
                    {
                        return ((x, y), open, row);
                    }
                }
            }
        }
        string R(ClickableRegion r) => $"[{r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0} surf={r.IsSurface} {r.Result?.GetType().Name}]";
        throw new Xunit.Sdk.XunitException("no point of the card's body lies over a session row; toast: " + string.Join(" ", toast.Select(R)) + "; rows: " + string.Join(" ", rows.Select(R)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheRowUnderTheCardIsNotLitAndShowsNoTooltip_TheRowInTheOpenIs(Kind kind, int width, int height, float dpi)
    {
        using var rig = new Rig(kind, width, height, dpi);
        var (body, open, _) = Find(rig);
        var resting = rig.PixelAt(open);

        rig.Move(body);
        rig.PixelAt(open).ShouldBe(resting, "the pointer on the card lights no row beneath it");
        rig.TooltipAfterRest(body).ShouldBeNull("the card's body has nothing to say, and the row beneath must not say it for it");
        rig.Save($"toast-surface-{kind.ToString().ToLowerInvariant()}-{width}");

        // The control: the same row in the open does light and tip.
        rig.Move(open);
        rig.PixelAt(open).ShouldNotBe(resting, "the row in the open lights");
        rig.TooltipAfterRest(open).ShouldNotBeNull();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void APressAndAWheelTurnOnTheCardsBodyNeverReachTheRow(Kind kind, int width, int height, float dpi)
    {
        using var rig = new Rig(kind, width, height, dpi);
        var (body, open, row) = Find(rig);
        var rows = rig.SessionRows();
        var first = rows[0];
        var selected = rig.Chrome.Sessions.Selected;
        var firstY = first.Y;

        rig.Wheel(body).ShouldBeTrue("the card takes the wheel");
        rig.Paint();
        rig.SessionRows()[0].Y.ShouldBe(firstY, "the list did not scroll");

        rig.Press(body);
        rig.Chrome.Sessions.Selected.ShouldBe(selected, "a press on the card does not select the row beneath it");
        rig.Chrome.Sessions.Transcript.IsOpen.ShouldBeFalse();
        if (kind == Kind.Staying)
        {
            rig.Chrome.Page.ShouldBe(PilotPage.Sessions);
        }

        // A fading toast's own press opens the job it names, which moves the page; back on Sessions, the row is still where it was.
        rig.Chrome.Show(PilotPage.Sessions);
        rig.Paint();

        // The control: a press on the same row in the open selects it.
        var index = rows.IndexOf(row);
        rig.Press(open);
        rig.Chrome.Sessions.Selected.ShouldBe(index, "the row in the open takes the press");
    }
}
