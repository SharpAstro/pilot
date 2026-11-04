using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Background tasks page says which shell ran a job (pilot #697): the selected job's facts carry a <c>shell</c> line, and a long
/// Git Bash path ends in an ellipsis with the whole on hover, never cut silently, at a narrow and a wide window.
/// </summary>
public sealed class JobShellViewTests
{
    private const string LongGitBash = @"C:\Users\SebastianGodelet\AppData\Local\Programs\Git With A Long Folder Name\usr\local\bin\bash.exe";

    private static JobRow Job(string id, string shell, JobState state = JobState.Running) => new()
    {
        Id = id,
        Command = $"bash -c 'dotnet test src/Pilot.Node.Tests -c Release --no-build --filter FullyQualifiedName~JobShell {id}'",
        Cwd = @"C:\r\pilot\.claude\worktrees\pil697",
        Owner = "c3d4e5f6",
        OwnerName = "se1-pil697-shell-shown",
        Flight = "PIL697",
        Leg = "shell-shown",
        State = state,
        Shell = shell,
        LogPath = $@"C:\data\jobs\{id}.log",
        Timeout = TimeSpan.FromHours(1),
        StartedAt = Live.At.AddMinutes(-11),
    };

    private static JobsScreen Screen(string shell) => new(JobLine.Build(
    [
        Job("j14", shell),
        Job("j13", "pwsh", JobState.Succeeded) with { EndedAt = Live.At.AddMinutes(-5), ExitCode = 0 },
    ], Live.At), "live", Live.At);

    private static (PilotChrome<RgbaImage> Chrome, JobsView<RgbaImage> Jobs) Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        return (chrome, chrome.Jobs);
    }

    [Fact]
    public void TheSelectedJobsFactsNameTheShellThatRanIt()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(renderer);
        var screen = Screen("git-bash " + LongGitBash);

        var described = TestText.Flat(CellLayout.Describe(Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored)));

        described.ShouldContain("git-bash");
        // A job with no shell on its row (one from before the node said it) shows no shell fact.
        var bare = new JobsScreen(JobLine.Build([Job("j20", "pwsh") with { Shell = null }], Live.At), "live", Live.At);
        TestText.Flat(CellLayout.Describe(Layout.Engine.Arrange(jobs.Tree(bare), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored)))
            .ShouldNotContain("git-bash");
    }

    [Fact]
    public void CapturesOfAJobRunByGitBashWithALongPathAtNarrowAndWideWindows()
    {
        // Always rendered, into a folder of the machine's own temp (never the plane): a person or a worker looks at the files.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture
            ? Path.GetDirectoryName(capture) ?? "."
            : Path.Combine(Path.GetTempPath(), "pilot-job-shell");
        Directory.CreateDirectory(dir);
        foreach (var (width, height) in new[] { (1000, 700), (2000, 900) })
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var (chrome, _) = Chrome(renderer);
            chrome.Show(PilotPage.Jobs);
            chrome.Render(new SessionsScreen([], "test", Live.At), null, null, Screen("git-bash " + LongGitBash), new RectF32(0, 0, width, height));
            var file = Path.Combine(dir, $"job-shell-{width}.png");
            File.WriteAllBytes(file, SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
            new FileInfo(file).Length.ShouldBeGreaterThan(0);
        }
    }
}
