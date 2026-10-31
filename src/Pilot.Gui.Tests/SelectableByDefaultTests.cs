using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Content text is selectable by default and controls are not (PIL535): the cards panel, the chat and the Background tasks page
/// offer their content among the frame's selectable regions and none of their buttons' labels, and a drag over a Markdown
/// message copies it as plain text.
/// </summary>
public sealed class SelectableByDefaultTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static CardRow Card(string id, string title) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/TIA1818/leg-" + id, Title = title, Detail = "detail of " + title,
        At = DateTimeOffset.UnixEpoch, State = CardState.Open,
    };

    private static string[] Texts(IEnumerable<SelectableTextRegion> regions) => [.. regions.Select(r => r.Text.Trim())];

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer) Open(int width = 1400, int height = 900)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        return (chrome, renderer);
    }

    [Fact]
    public void TheCardsPanelOffersATitleAndItsBodyAndNoButtonLabel()
    {
        var (chrome, renderer) = Open();
        using var _ = renderer;
        chrome.Show(PilotPage.Home);
        chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([], "test", Now), CardsScreen.From([Card("c1", "Planets draw from the GPU")]), new RectF32(0, 0, 1400, 900));

        var texts = Texts(chrome.Cards.SelectableTextRegions.ToArray());

        string.Join(" ", texts).ShouldContain("Planets draw from the GPU");
        texts.ShouldNotContain("Open");
        texts.ShouldNotContain("Dismiss");
    }

    [Fact]
    public void TheHomePageOffersItsFlyingAndWaitingLines()
    {
        var (chrome, renderer) = Open();
        using var _ = renderer;
        chrome.Show(PilotPage.Home);
        chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([], "test", Now), null, new RectF32(0, 0, 1400, 900));

        var texts = Texts(chrome.Home.SelectableTextRegions.ToArray());

        texts.ShouldContain("Nothing is flying");
        texts.ShouldContain("Nothing waits on you");
    }

    [Fact]
    public void TheChatOffersAMessageAndADragOverAMarkdownReplyCopiesPlainText()
    {
        var (chrome, renderer) = Open();
        using var _ = renderer;
        var copied = new List<string>();
        chrome.Chat.CopyText = copied.Add;
        TranscriptEntry[] feed =
        [
            new() { Index = 0, Kind = TranscriptKind.User, Text = "Which repo holds the planets?", At = Live.At },
            new() { Index = 1, Kind = TranscriptKind.Assistant, Text = "It is **tianwen**, in `src/Planets`.", At = Live.At.AddSeconds(5) },
        ];
        chrome.Chat.Load = (after, _) => Task.FromResult(after < 0 ? new ChatPage { Entries = feed, Total = feed.Length } : new ChatPage { Entries = [], Total = feed.Length });
        chrome.Show(PilotPage.Chat);
        for (var i = 0; i < 100 && chrome.Chat.Entries.Count < feed.Length; i++)
        {
            Thread.Sleep(50);
        }
        var bounds = new RectF32(0, 0, 1400, 900);
        void Draw() => chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([], "test", Now), null, bounds);
        Draw();

        var regions = chrome.Chat.SelectableTextRegions.ToArray();
        Texts(regions).ShouldContain(t => t.StartsWith("Which", StringComparison.Ordinal));
        var first = regions.First(r => r.Text.Contains("tianwen", StringComparison.Ordinal) || r.Text.Trim() == "It");
        var last = regions.Last(r => r.Text.Contains("Planets", StringComparison.Ordinal));

        chrome.HandleInput(new InputEvent.MouseDown(first.X + 0.5f, first.Y + first.Height / 2f));
        Draw();
        chrome.HandleInput(new InputEvent.MouseMove(last.X + last.Width - 0.5f, last.Y + last.Height / 2f, MouseButton.Left));
        Draw();
        chrome.HandleInput(new InputEvent.MouseUp(last.X + last.Width - 0.5f, last.Y + last.Height / 2f));
        Draw();
        chrome.HandleInput(new InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl));

        var text = copied.Single();
        text.ShouldContain("tianwen");
        text.ShouldContain("src/Planets");
        text.ShouldNotContain("*");
        text.ShouldNotContain("`");
    }

    [Fact]
    public void TheBackgroundTasksPageOffersAJobsCommandAndNoButtonLabel()
    {
        var (chrome, renderer) = Open();
        using var _ = renderer;
        chrome.Play.ShowEverything();
        chrome.Show(PilotPage.Jobs);
        var job = new JobRow
        {
            Id = "j14", Command = "dotnet test j14", Cwd = "/r/orbit", State = JobState.Running, LogPath = "/data/jobs/j14.log",
            Timeout = TimeSpan.FromHours(1), StartedAt = Live.At.AddMinutes(-11),
        };
        var screen = new JobsScreen(JobLine.Build([job], Live.At), "live", Live.At);
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, screen, new RectF32(0, 0, 1280, 800));

        var texts = Texts(chrome.Jobs.SelectableTextRegions.ToArray());

        texts.ShouldContain("dotnet test j14");
        texts.ShouldNotContain("Cancel");
    }
}
