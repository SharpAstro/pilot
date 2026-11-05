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

    private static readonly Lock Gate = new();
}
