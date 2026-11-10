using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The frame time of the views PIL740's fixes touched, at the "many" and "long" states (sixty rows, text far past a column), on the short window
/// that scrolls the page and on a normal one: the arrange of the page's tree must stay far under a frame budget, and nothing here blocks.
/// With PILOT_FRAME_TIMES set to a file, one line a case is written there (what the commit message of the fix quotes).
/// </summary>
public sealed class PageFrameTimeTests
{
    private static readonly string[] Pages =
        ["flight page", "flights canvas", "flights table", "jobs", "chat", "sessions", "transcript", "settings", "ideas", "pull requests"];

    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() =>
        from page in Pages
        from state in new[] { "many", "long" }
        from size in new[] { (500, 1.5f, 500), (1400, 1f, 900) }
        select new TheoryDataRow<string, string, int, float, int>(page, state, size.Item1, size.Item2, size.Item3) { TestDisplayName = PageTextOverlapTests.Key(page, state, size.Item1, size.Item2, size.Item3) };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ArrangeStaysFarUnderAFrameBudget(string page, string state, int width, float dpi, int height)
    {
        var (prepare, arrange) = await PageTextOverlapTests.TimedArrange(page, state, width, dpi, height);
        var line = $"{PageTextOverlapTests.Key(page, state, width, dpi, height)}\tsetup+first frame {prepare:F1} ms\tarrange {arrange[0]:F1} / {arrange[1]:F1} / {arrange[2]:F1} ms";
        if (Environment.GetEnvironmentVariable("PILOT_FRAME_TIMES") is { Length: > 0 } file)
        {
            lock (Gate)
            {
                File.AppendAllLines(file, [line]);
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        // Generous: a CPU-rendered CI machine under load, not a budget for the live GUI (its frame is the GPU's); it catches an arrange that turned quadratic.
        arrange.Skip(1).Max().ShouldBeLessThan(1000d, line);
    }

    /// <summary>
    /// The Settings page's whole frame cost (#770): its tree built (the range rows' label forms, every chip's elide, every editor's need, all measured
    /// with the font) and arranged, on the many sample with a repo list of forty long names, picked and listed, at narrow and wide sizes and DPI 1 and 1.5.
    /// PILOT_FRAME_TIMES gets a line a case, as above.
    /// </summary>
    [Theory]
    [InlineData(500, 1.5f, 900)]
    [InlineData(500, 1f, 900)]
    [InlineData(1400, 1.5f, 900)]
    [InlineData(1400, 1f, 900)]
    public async Task TheSettingsTreeWithManyLongRepoNamesIsBuiltAndArrangedFarUnderAFrameBudget(int width, float dpi, int height)
    {
        var longNames = Enumerable.Range(0, 40).Select(i => $"a-repo-with-a-very-long-name-number-{i}-that-goes-past-any-chip-and-any-row-{new string('x', 30)}").ToArray();
        var many = PageData.SettingsFor("many")!;
        var page = many with
        {
            Settings = [.. many.Settings.Select(s => s.Key is "repo-roots" or "repo-trackers" ? s with { Value = string.Join('\n', longNames.Select(n => s.Key == "repo-trackers" ? $"{n}=github" : $@"C:\work\{n}")), Saved = null } : s)],
        };
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        chrome.Settings.Load = _ => Task.FromResult(page);
        chrome.Settings.LoadRepos = _ => Task.FromResult<RepoInfo[]>([.. longNames.Select(n => new RepoInfo { Name = n, Path = $@"C:\work\{n}" })]);
        chrome.Show(PilotPage.Settings);
        for (var i = 0; i < 200 && chrome.Settings.Page is null; i++)
        {
            chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, height));
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        chrome.Settings.Page.ShouldNotBeNull();
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, height));
        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);
        var room = chrome.Settings.PageWidth.ShouldNotBeNull();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var times = new (double Tree, double Arrange)[3];
        for (var i = 0; i < times.Length; i++)
        {
            watch.Restart();
            var tree = chrome.Settings.Tree(PageData.Now);
            var built = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            Layout.Engine.Arrange(tree, new Rect<float>(0, 0, room * dpi, height), ctx);
            times[i] = (built, watch.Elapsed.TotalMilliseconds);
        }
        var line = $"settings tree | many long repo names | {width} wide | DPI {dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)}\ttree {times[0].Tree:F1} / {times[1].Tree:F1} / {times[2].Tree:F1} ms\tarrange {times[0].Arrange:F1} / {times[1].Arrange:F1} / {times[2].Arrange:F1} ms";
        if (Environment.GetEnvironmentVariable("PILOT_FRAME_TIMES") is { Length: > 0 } file)
        {
            lock (Gate)
            {
                File.AppendAllLines(file, [line]);
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        times.Skip(1).Max(t => t.Tree + t.Arrange).ShouldBeLessThan(1000d, line);
    }

    private static readonly Lock Gate = new();
}
