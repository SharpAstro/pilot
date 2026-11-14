using DIR.Lib;
using Xunit;
namespace Pilot.Gui.Tests;
public sealed class TmpDumpTests
{
    [Fact]
    public async Task Dump()
    {
        using var prepared = await PageTextOverlapTests.Prepare("jobs", "long", 1000, 1.5f, null, 900, (w, h) => new WireframeRenderer(w, h));
        var renderer = (WireframeRenderer)prepared.Renderer;
        var data = prepared.Data;
        renderer.Begin();
        prepared.Chrome.Render(data.Sessions, data.Flights, PageTextOverlapTests.CardsOf(data, "jobs"), data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, 1000, 900), data.Machines, null, data.Spend);
        var lines = renderer.Frame.Regions.Where(r => r.Rect.Y is > 320 and < 400).Select(r => $"R {r.Rect.X} {r.Rect.Y} {r.Rect.Width}x{r.Rect.Height} {r.Result}")
            .Concat(renderer.Frame.Texts.Where(t => t.Box.Y is > 320 and < 400).Select(t => $"T {t.Box.X} {t.Box.Width} {t.Text}"));
        File.WriteAllLines(@"C:\Users\SebastianGodelet\.claude\jobs\4c070aec\tmp\dump.txt", lines);
    }
}
