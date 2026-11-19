using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1183: the one back row. The Sessions transcript, a flight's page, the archived flights and a design's page each head their first row with the same
/// declared component, and none has a back pill in the top bar; a design's title is shown once.
/// </summary>
public sealed class BackRowTests
{
    private static readonly DateTimeOffset Now = PageData.Now;

    // Where the before and after captures go: outside the plane, in the job's tmp, under the tag the file tag.txt there holds (after when absent).
    private static string? CaptureFolder()
    {
        // The folder is named by the variable, or by the first line of a pointer file in the user profile (a job does not always carry the session's environment).
        var pointer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pilot-backrow-captures");
        var root = Environment.GetEnvironmentVariable("PILOT_BACKROW_CAPTURES") ?? (File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : null);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }
        var tmp = root;
        Directory.CreateDirectory(tmp);
        var tag = File.Exists(Path.Combine(tmp, "tag.txt")) ? File.ReadAllText(Path.Combine(tmp, "tag.txt")).Trim() : "after";
        var folder = Path.Combine(tmp, tag);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
    };

    private static FlightsScreen Flights() => new([FlightLine.Build([Flight()], [], Now, []).Single()], "test", Now);

    private static string Tui(Layout.Node tree) =>
        CellLayout.Describe(Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 160, 44), CellMeasureContext.PixelAuthored));

    private static List<Layout.ArrangedNode<float>> Arrange(Layout.Node tree, RgbaImageRenderer renderer, float width = 1600f, float height = 900f) =>
        [.. Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))];

    private static string TextOf(Layout.ArrangedNode<float> a) => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : "";

    private static PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        return chrome;
    }

    // The pill carrying the hit id, its label, and what shares its row: nothing is left of it, and it is below the top bar's row (where the Night switch is).
    private static void HeadsItsRow(List<Layout.ArrangedNode<float>> arranged, string hit, string label, string page)
    {
        var pill = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == hit);
        TextOf(pill).ShouldBe(label);
        pill.Node.Tooltip.ShouldBe($"Up to the {page} page (Escape)");
        var row = arranged.Where(a => a.Node is Layout.Node.Leaf && a.Bounds.Y < pill.Bounds.Y + pill.Bounds.Height && a.Bounds.Y + a.Bounds.Height > pill.Bounds.Y).ToList();
        row.Min(a => a.Bounds.X).ShouldBeGreaterThanOrEqualTo(pill.Bounds.X - 0.5f, "the pill is at the left of the page's own first row");
        arranged.Where(a => TextOf(a) == "Night").ShouldAllBe(night => night.Bounds.Y + night.Bounds.Height <= pill.Bounds.Y + 0.5f, "the pill is not in the top bar");
    }

    [Fact]
    public void TheDesignsPageHeadsItsFirstRowWithTheBackRowAndHasNoBackPillInTheTopBar()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        var arranged = Arrange(chrome.Designs.Tree(PageData.DesignsFor("sample", Now)), renderer);
        HeadsItsRow(arranged, "designs:list", "← Designs", "Designs");
        arranged.ShouldNotContain(a => TextOf(a).Contains("Back to designs", StringComparison.Ordinal));
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:versions" }).Bounds.Y.ShouldBeGreaterThan(arranged.Single(a => TextOf(a) == "Night").Bounds.Y, "the version chip is on the back row, not in the bar");
    }

    [Fact]
    public void TheDesignsPageHasTheEscapeOfTheBackPill()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        chrome.Designs.HandleInput(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        chrome.Designs.OpenId.ShouldBeNull();
    }

    [Fact]
    public void ADesignsTitleIsShownOnceAndALongOneEndsInAnEllipsisWithTheWholeOnHover()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        var sample = PageData.DesignsFor("sample", Now);
        var shortTitle = Arrange(chrome.Designs.Tree(sample), renderer);
        SentenceRuns.Texts(shortTitle.Select(a => a.Node)).Count(t => t.Contains("Design 0", StringComparison.Ordinal)).ShouldBe(1, "the title is drawn once, on the back row");
        shortTitle.Single(a => a.Node.Tooltip == "DSN-1 · Design 0");

        var title = PageData.MaxTitle;
        var longPage = sample.Open! with { Detail = sample.Open.Detail with { Row = sample.Open.Detail.Row with { Title = title } }, Doc = sample.Open.Doc! with { Title = title } };
        var narrow = Arrange(chrome.Designs.Tree(sample with { Open = longPage }), renderer, 900f, 900f);
        var line = narrow.Single(a => a.Node.Tooltip == $"DSN-1 · {title}");
        var drawn = narrow.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && a.Bounds.Y >= line.Bounds.Y - 0.5f && a.Bounds.Y + a.Bounds.Height <= line.Bounds.Y + line.Bounds.Height + 0.5f
            && a.Bounds.X >= line.Bounds.X - 0.5f && a.Bounds.X <= line.Bounds.X + line.Bounds.Width + 0.5f).Select(TextOf).ToList();
        var words = drawn.Where(w => w != "…").Select(w => w.Trim().TrimEnd('…')).Where(w => w.Length > 0).ToList();
        var whole = $"DSN-1 · {title}".Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The row's Build this version button (PIL1191) narrows the title, so where the line ends the engine may put its ellipsis inside the last word ("M…"): every word before it is whole.
        words.Take(words.Count - 1).ShouldBe(whole.Take(words.Count - 1), "the line is a run of whole words up to the one the ellipsis ends");
        whole[words.Count - 1].ShouldStartWith(words[^1], Case.Sensitive);
        (words.Count == whole.Length || drawn.Any(w => w.EndsWith('…'))).ShouldBeTrue("a title that does not fit ends in an ellipsis");
        SentenceRuns.Texts(narrow.Select(a => a.Node)).Count(t => t.Contains(title, StringComparison.Ordinal)).ShouldBeLessThanOrEqualTo(1, "the document does not repeat the title");
    }

    [Fact]
    public void ADesignWithNoTitleShowsItsIdAlone()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        var sample = PageData.DesignsFor("sample", Now);
        var page = DesignPages.Load(sample.Open!.Detail with { Row = sample.Open.Detail.Row with { Title = null } });
        var arranged = Arrange(chrome.Designs.Tree(sample with { Open = page }), renderer);
        arranged.ShouldContain(a => a.Node.Tooltip == "DSN-1");
    }

    [Fact]
    public void TheFlightsPageHeadsItsFirstRowWithTheBackRow()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail("TIA1611");
        var arranged = Arrange(chrome.Flights.Tree(Flights()), renderer);
        HeadsItsRow(arranged, "flight:back", "← Flights", "Flights");
    }

    [Fact]
    public void TheArchivedFlightsHeadTheirFirstRowWithTheBackRow()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = Chrome(renderer);
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenArchived();
        var arranged = Arrange(chrome.Flights.Tree(Flights()), renderer);
        var pill = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "flights:archived-back" });
        TextOf(pill).ShouldBe("← Flights");
        pill.Node.Tooltip.ShouldBe("Up to the Flights page (Escape)");
        arranged.Single(a => TextOf(a).StartsWith("Archived flights", StringComparison.Ordinal)).Bounds.X.ShouldBeGreaterThan(pill.Bounds.X);
    }

    [Fact]
    public async Task TheTranscriptHeadsItsFirstRowWithTheBackRow()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var sessions = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        view.Transcript.Load = (_, _, after, _) => Task.FromResult(new TranscriptPage { Total = 1, Entries = after < 0 ? [new() { Index = 0, Kind = TranscriptKind.Assistant, Text = "words", At = Now }] : [] });
        view.Tree(sessions);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        for (var i = 0; i < 100 && view.Transcript.Entries.Count < 1; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        var arranged = Arrange(view.Tree(sessions), renderer);
        var pill = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "transcript:back" });
        TextOf(pill).ShouldBe("← Sessions");
        pill.Node.Tooltip.ShouldBe("Up to the Sessions page (Escape)");
        pill.Bounds.X.ShouldBeLessThan(arranged.Where(a => TextOf(a) == "● live" || TextOf(a) == "session ended").Min(a => a.Bounds.X));
    }

    [Fact]
    public async Task TheBeforeAndAfterCapturesOfTheThreePagesAreWritten()
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        if (CaptureFolder() is not { } folder)
        {
            return;
        }
        var screen = new SessionsScreen([], "test", Now);
        var designs = PageData.DesignsFor("sample", Now);
        foreach (var (width, height) in new[] { (1920, 1080), (2880, 1620) })
        {
            SessionsCapture.Write(screen, Path.Combine(folder, $"flight-{width}.png"), width, height, flights: Flights(), page: PilotPage.Flights, openFlight: "TIA1611");
            SessionsCapture.Write(screen, Path.Combine(folder, $"design-{width}.png"), width, height, page: PilotPage.Designs, designs: designs, openDesign: "DSN-1", designChat: PageData.DesignChatPage());

            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
            var sessions = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
            view.Transcript.Load = (_, _, after, _) => Task.FromResult(new TranscriptPage
            {
                Total = 2,
                Entries = after < 0 ? [new() { Index = 0, Kind = TranscriptKind.Assistant, Text = "assistant-words", At = Now }, new() { Index = 1, Kind = TranscriptKind.Assistant, Text = "more words", At = Now }] : [],
            });
            view.Tree(sessions);
            view.HandleInput(new InputEvent.KeyDown(InputKey.Enter));
            for (var i = 0; i < 100 && view.Transcript.Entries.Count < 2; i++)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            view.Render(sessions, new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(folder, $"transcript-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }

        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.WithoutScrollBars();
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail("TIA1611");
        File.WriteAllText(Path.Combine(folder, "flight-160x44.txt"), Tui(chrome.Flights.Tree(Flights())));
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Terminal = (160, 44);
        chrome.Designs.Open("DSN-1");
        File.WriteAllText(Path.Combine(folder, "design-160x44.txt"), Tui(chrome.Designs.Tree(designs)));
    }
}
