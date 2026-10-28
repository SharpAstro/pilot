using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The flight page's text can be selected with the mouse and copied with Ctrl+C or a right-click Copy, through the one
/// component the page and (later) the transcript share, and a press on a button or a link is still theirs (SharpAstro/pilot#383).
/// </summary>
public sealed class TextSelectionTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightRow Flight(string report) => new()
    {
        Code = "TIA1611",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1611,
        Title = "Planets on the GPU",
        Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = Live.At,
        PullRequest = 7,
        PullRequestUrl = "https://github.com/x/tianwen/pull/7",
        Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "r", Report = report, StartedAt = Live.At, GoAround = 0, ReviewedHead = "cafe0000aaaa" },
        GitHubHead = "cafe0000aaaa",
    };

    private static LegRow[] Legs() =>
    [
        new()
        {
            Flight = "TIA1611", Name = "render", Order = 1, Goal = "Draw the planets from the GPU buffer.", DoneWhen = "The capture shows all eight.",
            State = LegState.Flying, Checklist = [new CheckItem { Text = "buffer", Done = true }, new CheckItem { Text = "shader" }],
        },
    ];

    private const string Report = "1. CI is red: `LayoutDrivenTests` rejects `JobsView.cs:680`.\n2. The gate takes the smaller of the estimate and `job-memory`.";

    /// <summary>The window as the host drives it: the chrome ahead of the router, a clipboard that keeps what it is given.</summary>
    private sealed class Window : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly InputRouter _router;
        private readonly FlightsScreen _screen;

        public Window(string report = Report, int width = 1280, int height = 900, bool night = false)
        {
            _renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Flights.CopyText = Copied.Add;
            Chrome.Flights.OnOpen = Opened.Add;
            if (night)
            {
                Chrome.Flights.Mode = FlightsViewMode.Night;
            }
            _router = new InputRouter(Chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            _screen = new FlightsScreen([FlightLine.Build([Flight(report)], [], Now, Legs()).Single()], "test", Now);
            Size = new RectF32(0, 0, width, height);
            Chrome.Show(PilotPage.Flights);
            Chrome.Flights.OpenDetail("TIA1611");
            Render();
        }

        public PilotChrome<RgbaImage> Chrome { get; }
        public List<string> Copied { get; } = [];
        public List<string> Opened { get; } = [];
        public RectF32 Size { get; }

        public void Render()
        {
            Chrome.Render(new SessionsScreen([], "test", Now), _screen, Size);
        }

        public RgbaImageRenderer Renderer => _renderer;

        /// <summary>The painted region of the button or link whose hit has <paramref name="action"/>.</summary>
        public ClickableRegion Clickable(string action)
        {
            var regions = new List<ClickableRegion>();
            Chrome.Flights.CollectPaintedRegions(regions);
            return regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action);
        }

        /// <summary>The frame's selectable runs in paint order, as a drag meets them.</summary>
        public SelectableTextRegion[] Regions => Chrome.Flights.SelectableTextRegions.ToArray();

        /// <summary>What the window does with an event: the chrome first, then the router; redrawn after, as the host redraws.</summary>
        public bool Send(InputEvent evt)
        {
            var handled = Chrome.HandleInput(evt) || _router.Handle(evt);
            Render();
            return handled;
        }

        public void Drag((float X, float Y) from, (float X, float Y) to)
        {
            Send(new InputEvent.MouseDown(from.X, from.Y));
            Send(new InputEvent.MouseMove(to.X, to.Y, MouseButton.Left));
            Send(new InputEvent.MouseUp(to.X, to.Y));
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static (float X, float Y) Start(SelectableTextRegion r) => (r.X + 0.5f, r.Y + r.Height / 2f);

    private static (float X, float Y) End(SelectableTextRegion r) => (r.X + r.Width - 0.5f, r.Y + r.Height / 2f);

    [Fact]
    public void ADragAcrossTheReviewBlockSelectsItsTextAndCtrlCCopiesItAsTheFindingsRead()
    {
        using var window = new Window();
        var regions = window.Regions;
        var first = regions.First(r => r.Text == "1.");
        var last = regions.Last(r => r.Text.StartsWith("job-memory", StringComparison.Ordinal));

        window.Drag(Start(first), End(last));

        window.Chrome.Flights.Selection.HasSelection.ShouldBeTrue();
        window.Chrome.HandleInput(new InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl)).ShouldBeTrue();
        // The findings as the page shows them: the backticks are the code's colour, not characters, and a break where a finding ends.
        window.Copied.Single().ShouldBe("1. CI is red: LayoutDrivenTests rejects JobsView.cs:680.\n2. The gate takes the smaller of the estimate and job-memory");
    }

    [Fact]
    public void ADragWithinOneLineSelectsFromACharacterToACharacter()
    {
        using var window = new Window();
        var words = window.Regions.Where(r => r.Text.StartsWith("rejects", StringComparison.Ordinal)).ToArray();
        var word = words.Single();

        // From inside "rejects" (after its first two letters, as the renderer sets them) to the end of it.
        var two = window.Renderer.MeasureText("re", word.FontPath, word.FontSize).Width;
        window.Drag((word.X + two, word.Y + word.Height / 2f), End(word));

        window.Chrome.Flights.Selection.Text.ShouldBe("jects");
    }

    [Fact]
    public void ARightClickOnTheSelectionOffersCopyAndChoosingItCopiesTheSelection()
    {
        using var window = new Window();
        var regions = window.Regions;
        window.Drag(Start(regions.First(r => r.Text == "1.")), End(regions.Last(r => r.Text.StartsWith("job-memory", StringComparison.Ordinal))));
        var inside = regions.First(r => r.Text.StartsWith("rejects", StringComparison.Ordinal));

        window.Send(new InputEvent.MouseDown(inside.X + 3f, inside.Y + inside.Height / 2f, MouseButton.Right)).ShouldBeTrue();

        window.Chrome.Flights.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopySelectionLabel]);
        window.Chrome.Flights.Links.MenuItems[0].OnChoose!.Invoke();
        window.Copied.Single().ShouldStartWith("1. CI is red");
        window.Chrome.Flights.Links.NoticeShown.ShouldBeTrue();
    }

    [Fact]
    public void ARightClickOnALinkWhileTextIsSelectedOffersCopyUrlBesideCopy()
    {
        using var window = new Window();
        var regions = window.Regions;
        window.Drag(Start(regions.First(r => r.Text == "1.")), End(regions.First(r => r.Text.StartsWith("rejects", StringComparison.Ordinal))));
        var link = window.Clickable("pr:TIA1611");

        window.Send(new InputEvent.MouseDown(link.X + 3f, link.Y + link.Height / 2f, MouseButton.Right));

        window.Chrome.Flights.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel, LinkMenu.CopySelectionLabel]);
    }

    [Fact]
    public void AClickElsewhereLetsTheSelectionGoAndLeavingThePageDoesToo()
    {
        using var window = new Window();
        var regions = window.Regions;
        var from = Start(regions.First(r => r.Text == "1."));
        var to = End(regions.First(r => r.Text.StartsWith("rejects", StringComparison.Ordinal)));
        window.Drag(from, to);
        window.Chrome.Flights.Selection.HasSelection.ShouldBeTrue();

        // Empty ground, low on the page.
        window.Send(new InputEvent.MouseDown(1000f, 880f));
        window.Send(new InputEvent.MouseUp(1000f, 880f));
        window.Chrome.Flights.Selection.HasSelection.ShouldBeFalse();

        window.Drag(from, to);
        window.Chrome.Flights.Selection.HasSelection.ShouldBeTrue();
        window.Chrome.Flights.CloseDetail();
        window.Chrome.Flights.Selection.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public void APressWithNoDragSelectsNothing()
    {
        using var window = new Window();
        var word = window.Regions.First(r => r.Text.StartsWith("rejects", StringComparison.Ordinal));

        window.Send(new InputEvent.MouseDown(word.X + 2f, word.Y + word.Height / 2f));
        window.Send(new InputEvent.MouseUp(word.X + 2f, word.Y + word.Height / 2f));

        window.Chrome.Flights.Selection.HasSelection.ShouldBeFalse();
        // And Ctrl+C then is not taken, so it reaches whatever else wants it.
        window.Chrome.HandleInput(new InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl)).ShouldBeFalse();
        window.Copied.ShouldBeEmpty();
    }

    [Fact]
    public void AClickOnALinkOpensItAndSelectsNothing()
    {
        using var window = new Window();
        var link = window.Clickable("pr:TIA1611");
        var (x, y) = (link.X + 3f, link.Y + link.Height / 2f);

        // A press and a release on it, as a click: the page's own opener is asked for the PR.
        window.Send(new InputEvent.MouseDown(x, y)).ShouldBeTrue();
        window.Send(new InputEvent.MouseUp(x, y));

        window.Opened.ShouldBe(["https://github.com/x/tianwen/pull/7"]);
        window.Chrome.Flights.Selection.HasSelection.ShouldBeFalse();
        window.Chrome.Flights.Selection.Dragging.ShouldBeFalse();
        // Dragged from it over the text beside, the link still takes the press: nothing is selected from a button's or a link's press.
        var word = window.Regions.First(r => r.Text.StartsWith("rejects", StringComparison.Ordinal));
        window.Drag((x, y), End(word));
        window.Chrome.Flights.Selection.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public void AClickOnAButtonStillActsAndSelectsNothing()
    {
        // A report long enough to fold has a Show all pill, a button on the very page whose text is selectable.
        var report = string.Join("\n", Enumerable.Range(1, 3).Select(n => $"{n}. Finding {n}\n   detail A{n}\n   detail B{n}\n   detail C{n}\n   detail D{n}"));
        using var window = new Window(report);
        // A word is a run of its own: the page's words as one string.
        string Said() => string.Concat(window.Regions.Select(r => r.Text));
        Said().ShouldNotContain("D1");
        var show = window.Clickable("flight:TIA1611:review-fold");
        var (x, y) = (show.X + show.Width / 2f, show.Y + show.Height / 2f);

        window.Send(new InputEvent.MouseDown(x, y));
        window.Send(new InputEvent.MouseUp(x, y));

        window.Chrome.Flights.Selection.HasSelection.ShouldBeFalse();
        Said().ShouldContain("D1");
    }

    [Fact]
    public void TheHeaderTheGoalTheDoneWhenAndTheChecklistAreSelectableToo()
    {
        using var window = new Window();
        var said = window.Regions.Select(r => r.Text).ToArray();

        // The title, a leg's goal and Done-when, its checklist and the facts line are all runs the host can select.
        string.Concat(said).ShouldContain("Planets on the GPU");
        string.Concat(said).ShouldContain("Draw the planets from the GPU buffer.");
        string.Concat(said).ShouldContain("The capture shows all eight.");
        said.ShouldContain("[x]");
        string.Concat(said).ShouldContain("shader");
        string.Concat(said).ShouldContain("flight/tia1611");
        // The buttons and pills are not: "← Back" is a pill's label.
        said.ShouldNotContain(t => t.Contains("← Back", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHighlightAndItsTextAreReadableInBothPalettes(bool night)
    {
        var palette = night ? FlightsPalette.Night : FlightsPalette.Standard;

        Contrast(palette.SelectionText, palette.SelectionBg).ShouldBeGreaterThanOrEqualTo(4.5);
        // And the bar is told from the panel it lies on, so the selection is seen at all.
        Contrast(palette.SelectionBg, palette.PanelBg).ShouldBeGreaterThanOrEqualTo(night ? 3.0 : 1.5);
    }

    [Fact]
    public void ASelectionPaintsItsBarAndTheSelectedGlyphsOnIt()
    {
        using var window = new Window();
        var word = window.Regions.First(r => r.Text.StartsWith("rejects", StringComparison.Ordinal));
        var before = window.Renderer.Surface.Pixels.ToArray();

        window.Drag(Start(word), End(word));

        var bar = FlightsPalette.Standard.SelectionBg;
        var surface = window.Renderer.Surface;
        var (x, y) = ((int)(word.X + 1f), (int)(word.Y + 1f));
        // The bar's colour is in the pixels under the word where there were none before, the glyphs drawn over it in the selection's ink.
        var at = (y * (int)window.Size.Width + x) * 4;
        window.Renderer.Surface.Pixels.ToArray().SequenceEqual(before).ShouldBeFalse();
        var pixels = surface.Pixels;
        var onBar = false;
        var ink = false;
        for (var py = (int)word.Y; py < (int)(word.Y + word.Height); py++)
        {
            for (var px = (int)word.X; px < (int)(word.X + word.Width); px++)
            {
                var i = (py * (int)window.Size.Width + px) * 4;
                onBar |= pixels[i] == bar.Red && pixels[i + 1] == bar.Green && pixels[i + 2] == bar.Blue;
                ink |= pixels[i] >= 0xd0 && pixels[i + 1] >= 0xd0 && pixels[i + 2] >= 0xd0;
            }
        }
        at.ShouldBeGreaterThanOrEqualTo(0);
        onBar.ShouldBeTrue();
        ink.ShouldBeTrue();
    }

    [Fact]
    public void ASelectionFollowsItsTextWhenThePageIsScrolled()
    {
        // Long enough that the page scrolls under its head.
        var report = string.Join("\n", Enumerable.Range(1, 40).Select(n => $"{n}. Finding number {n} says something."));
        using var window = new Window(report);
        var word = window.Regions.First(r => r.Text.StartsWith("Finding", StringComparison.Ordinal));
        window.Drag(Start(word), End(word));
        var text = window.Chrome.Flights.Selection.Text;
        text.ShouldBe("Finding");

        window.Send(new InputEvent.KeyDown(InputKey.Down, InputModifier.None));

        // The same words, further up the window: the selection holds the text, not the place.
        window.Chrome.Flights.Selection.Text.ShouldBe("Finding");
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Lum(RGBAColor32 c) => 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
        var (hi, lo) = (Math.Max(Lum(a), Lum(b)), Math.Min(Lum(a), Lum(b)));
        return (hi + 0.05) / (lo + 0.05);
    }
}
