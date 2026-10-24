using System.Text.RegularExpressions;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A session's transcript reads as a list: a tool call is one folded line (check or cross, the tool, what it says it does)
/// and a click unfolds the command and its output; a failed call shows its last lines while folded.
/// </summary>
public sealed class TranscriptPaneTests
{
    private static string Output(int lines) => string.Join("\n", Enumerable.Range(1, lines).Select(i => $"output-line-{i}"));

    private static TranscriptEntry[] Entries() =>
    [
        new() { Index = 0, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "dotnet test src/Pilot.slnx --no-build", Description = "Run every test", ToolUseId = "t1" },
        new() { Index = 1, Kind = TranscriptKind.ToolResult, Text = Output(12), ToolUseId = "t1" },
        new() { Index = 2, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "dotnet build src/Pilot.slnx", Description = "Build the solution", ToolUseId = "t2" },
        new() { Index = 3, Kind = TranscriptKind.Error, Text = "restore-line-1\nrestore-line-2\nerror-line-3\nerror-line-4\nerror-line-5", ToolUseId = "t2" },
        new() { Index = 4, Kind = TranscriptKind.ToolUse, Tool = "Grep", Text = "TreeRow", ToolUseId = "t3" },
    ];

    private static async Task<(SessionsView<RgbaImage> View, SessionsScreen Screen, RgbaImageRenderer Renderer)> Open()
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        view.Transcript.Load = (_, _, after, _) => Task.FromResult(after < 0
            ? new TranscriptPage { Total = 5, Entries = Entries() }
            : new TranscriptPage { Total = 5, Entries = [] });
        view.Tree(screen);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        for (var i = 0; i < 100 && view.Transcript.Entries.Count < 5; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return (view, screen, renderer);
    }

    private static string Described(SessionsView<RgbaImage> view, SessionsScreen screen) =>
        CellLayout.Describe(Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored));

    // Everything the described tree puts on the cell row that holds `text`: a dump has a node a line, each with its cell position.
    private static string RowOf(string described, string text)
    {
        var lines = described.ReplaceLineEndings("\n").Split('\n');
        var at = lines.Single(l => l.Contains($"\"{text}\"", StringComparison.Ordinal));
        var y = Regex.Match(at, @"\((\d+),(\d+) ").Groups[2].Value;
        return string.Join("\n", lines.Where(l => Regex.Match(l, @"\((\d+),(\d+) ") is { Success: true } m && m.Groups[2].Value == y));
    }

    private static void Click(SessionsView<RgbaImage> view, SessionsScreen screen, string action)
    {
        view.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
    }

    [Fact]
    public async Task ACallIsOneFoldedLineWithItsDescriptionAndUnfoldsOnItsToggleToTheCommandAndOutput()
    {
        var (view, screen, renderer) = await Open();
        using var _ = renderer;

        var folded = Described(view, screen);
        var line = RowOf(folded, "Run every test");
        line.ShouldContain("✓");
        line.ShouldContain("Bash");
        // The command and the output wait behind the click.
        folded.ShouldNotContain("dotnet test src/Pilot.slnx");
        folded.ShouldNotContain("output-line-1");
        folded.ShouldNotContain("output-line-12");
        // A call without a description says its short form.
        RowOf(folded, "TreeRow").ShouldContain("Grep");

        Click(view, screen, "transcript:call:0");
        var unfolded = Described(view, screen);
        unfolded.ShouldContain("Run every test");
        unfolded.ShouldContain("dotnet test src/Pilot.slnx --no-build");
        unfolded.ShouldContain("output-line-1");
        unfolded.ShouldContain("output-line-12");

        Click(view, screen, "transcript:call:0");
        Described(view, screen).ShouldNotContain("output-line-1");
        view.Transcript.Close();
    }

    [Fact]
    public async Task AFailedCallShowsACrossAndItsLastLinesWhileFolded()
    {
        var (view, screen, renderer) = await Open();
        using var _ = renderer;

        var folded = Described(view, screen);
        RowOf(folded, "Build the solution").ShouldContain("✗");
        folded.ShouldContain("error-line-3");
        folded.ShouldContain("error-line-4");
        folded.ShouldContain("error-line-5");
        folded.ShouldNotContain("restore-line-1");
        folded.ShouldNotContain("dotnet build src/Pilot.slnx");

        Click(view, screen, "transcript:call:2");
        var unfolded = Described(view, screen);
        unfolded.ShouldContain("dotnet build src/Pilot.slnx");
        unfolded.ShouldContain("restore-line-1");
        view.Transcript.Close();
    }
}
