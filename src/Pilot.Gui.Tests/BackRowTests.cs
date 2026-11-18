using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
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
        // The folder is named by the variable, or by the first line of a pointer file in the temp folder (a job does not always carry the session's environment).
        var pointer = Path.Combine(Path.GetTempPath(), "pilot-backrow-captures.txt");
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
