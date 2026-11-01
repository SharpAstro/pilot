using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Every page and side panel hosts the one text selection (PIL535): a drag over the Home page, the cards panel and the chat's
/// transcript selects what it covers across lines, Ctrl+C copies it through that view's own CopyText, a press on a button is
/// still the button's, and leaving the page or the panel lets the selection go.
/// </summary>
public sealed class TextSelectionHostTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static CardRow Card(string id, string title) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/TIA1818/leg-" + id, Title = title, Detail = "detail of " + title,
        At = DateTimeOffset.UnixEpoch, State = CardState.Open,
    };

    private static TranscriptEntry[] Feed() =>
    [
        new() { Index = 0, Kind = TranscriptKind.User, Text = "Which repo holds the planets?", At = Live.At },
        new() { Index = 1, Kind = TranscriptKind.User, Text = "And who reviews it?", At = Live.At.AddSeconds(5) },
    ];

    private sealed class Window : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly InputRouter _router;
        private RectF32 _size;

        public Window(int width = 1400, int height = 900)
        {
            _renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Home.CopyText = Home.Add;
            Chrome.Cards.CopyText = Cards.Add;
            Chrome.Chat.CopyText = Chat.Add;
            Chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
            _router = new InputRouter(Chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            _size = new RectF32(0, 0, width, height);
        }

        public PilotChrome<RgbaImage> Chrome { get; }
        public CardsScreen CardsScreen { get; set; } = CardsScreen.From([Card("c1", "Planets draw from the GPU"), Card("c2", "Shader compile is red")]);
        public List<string> Home { get; } = [];
        public List<string> Cards { get; } = [];
        public List<string> Chat { get; } = [];
        public RgbaImageRenderer Renderer => _renderer;

        public void Resize(int width, int height) => _size = new RectF32(0, 0, width, height);

        public void Render() =>
            Chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([], "test", Now), CardsScreen, _size);

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

        public void CtrlC() => Send(new InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl));

        public void Dispose() => _renderer.Dispose();
    }

    private static (float X, float Y) Start(SelectableTextRegion r) => (r.X + 0.5f, r.Y + r.Height / 2f);

    private static (float X, float Y) End(SelectableTextRegion r) => (r.X + r.Width - 0.5f, r.Y + r.Height / 2f);

    [Fact]
    public void ADragOverTheHomePageSelectsAcrossLinesAndCtrlCCopiesItThroughItsCopyText()
    {
        using var window = new Window { };
        window.Chrome.Show(PilotPage.Home);
        window.Render();
        var regions = window.Chrome.Home.SelectableTextRegions.ToArray();
        regions.Length.ShouldBeGreaterThanOrEqualTo(2);
        var first = regions[0];
        var last = regions[^1];

        window.Drag(Start(first), End(last));

        window.Chrome.Home.Selection.HasSelection.ShouldBeTrue();
        window.CtrlC();
        window.Home.Single().ShouldContain("\n");
        window.Home.Single().ShouldStartWith(first.Text.Trim());
        window.Cards.ShouldBeEmpty();
    }

    [Fact]
    public void ADragOverTheCardsPanelSelectsTheCardTitlesAndCopiesThemThroughItsCopyText()
    {
        using var window = new Window();
        window.Chrome.Show(PilotPage.Home);
        window.Render();
        window.Chrome.ShowsCards.ShouldBeTrue();
        var regions = window.Chrome.Cards.SelectableTextRegions.ToArray();
        var first = regions.First(r => r.Text.Contains("Planets draw", StringComparison.Ordinal));
        var last = regions.First(r => r.Text.Contains("Shader compile", StringComparison.Ordinal));

        window.Drag(Start(first), End(last));

        window.Chrome.Cards.Selection.HasSelection.ShouldBeTrue();
        window.Chrome.Home.Selection.HasSelection.ShouldBeFalse();
        window.CtrlC();
        window.Cards.Single().ShouldContain("Planets draw from the GPU");
        window.Cards.Single().ShouldEndWith("Shader compile is red");
        window.Cards.Single().ShouldContain("\n");
        window.Home.ShouldBeEmpty();
    }

    [Fact]
    public void ADragOverTheChatTranscriptSelectsAcrossMessagesAndCopiesThemThroughItsCopyText()
    {
        using var window = new Window();
        var feed = Feed();
        window.Chrome.Chat.Load = (after, _) => Task.FromResult(after < 0 ? new ChatPage { Entries = feed, Total = feed.Length } : new ChatPage { Entries = [], Total = feed.Length });
        window.Chrome.Show(PilotPage.Chat);
        for (var i = 0; i < 100 && window.Chrome.Chat.Entries.Count < feed.Length; i++)
        {
            Thread.Sleep(50);
        }
        window.Chrome.Chat.Entries.Count.ShouldBe(feed.Length);
        window.Render();
        var regions = window.Chrome.Chat.SelectableTextRegions.ToArray();
        var first = regions.First(r => r.Text.StartsWith("Which", StringComparison.Ordinal));
        var last = regions.Last(r => r.Text.Trim() == "it?");

        window.Drag(Start(first), End(last));

        window.Chrome.Chat.Selection.HasSelection.ShouldBeTrue();
        window.CtrlC();
        var copied = window.Chat.Single();
        copied.ShouldStartWith("Which repo holds the planets?");
        copied.ShouldEndWith("reviews it?");
        copied.ShouldContain("\n");
    }

    [Fact]
    public void APressOnAButtonInThePageOrThePanelIsTheButtonsAndSelectsNothing()
    {
        using var window = new Window();
        window.Chrome.Show(PilotPage.Home);
        window.Render();

        foreach (var (name, widget) in new (string, IPixelWidget)[] { ("cards", window.Chrome.Cards), ("home", window.Chrome.Home) })
        {
            var regions = new List<ClickableRegion>();
            widget.CollectPaintedRegions(regions);
            var at = regions.FindIndex(r => r is { Result: HitResult.ButtonHit } && r.Width > 4f && r.Height > 4f);
            if (name == "cards")
            {
                at.ShouldBeGreaterThanOrEqualTo(0);
            }
            if (at < 0)
            {
                continue;
            }
            var button = regions[at];
            // The chrome's selection leaves the press to the router, which gives it to the button.
            window.Chrome.HandleInput(new InputEvent.MouseDown(button.X + button.Width / 2f, button.Y + button.Height / 2f)).ShouldBeFalse($"{name}'s button");
            window.Send(new InputEvent.MouseUp(button.X + button.Width / 2f, button.Y + button.Height / 2f));
            window.Chrome.Cards.Selection.HasSelection.ShouldBeFalse(name);
            window.Chrome.Home.Selection.HasSelection.ShouldBeFalse(name);
        }
    }

    [Fact]
    public void LeavingThePageOrClosingThePanelLetsTheSelectionGo()
    {
        using var window = new Window();
        window.Chrome.Show(PilotPage.Home);
        window.Render();
        var home = window.Chrome.Home.SelectableTextRegions.ToArray().First(r => r.Text.Length > 0);
        window.Drag(Start(home), End(home));
        window.Chrome.Home.Selection.HasSelection.ShouldBeTrue();

        window.Chrome.Show(PilotPage.Jobs);
        window.Chrome.Home.Selection.HasSelection.ShouldBeFalse();

        window.Chrome.Show(PilotPage.Home);
        window.Render();
        var title = window.Chrome.Cards.SelectableTextRegions.ToArray().First(r => r.Text.Contains("Planets draw", StringComparison.Ordinal));
        window.Drag(Start(title), End(title));
        window.Chrome.Cards.Selection.HasSelection.ShouldBeTrue();

        // A window with no room for the panel beside the page: its text is not there to keep selected.
        window.Resize(500, 900);
        window.Render();
        window.Send(new InputEvent.MouseMove(10, 10));
        window.Chrome.Cards.Selection.HasSelection.ShouldBeFalse();
    }
}
