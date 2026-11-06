using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

public sealed class TmpWordCapture
{
    [Fact]
    public void Shoot()
    {
        var folder = @"C:\Users\SebastianGodelet\.claude\jobs\7cfaac1d\tmp\shots";
        Directory.CreateDirectory(folder);
        var updated = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);
        string word = "Supercalifragilisticexpialidocious_Pneumonoultramicroscopicsilicovolcanoconiosis_" + new string('x', 60);
        IdeaRow Idea(int n, string title, string body) => new()
        {
            Id = IdeaIds.Id(n), Repo = "pilot", Title = title, Body = body, State = IdeaState.Noted, UpdatedAt = updated.AddMinutes(n),
            Age = "3h", Origin = "ada", Url = $"https://github.com/SharpAstro/pilot/issues/{n}", Images = IdeaImages.In(body),
        };
        var ideas = Enumerable.Range(1, 6).Select(i => Idea(i, i % 2 == 0 ? "Title " + word : "A long title that wraps over several lines at a narrow width " + i,
            "Body with a word " + word + " and then a long sentence that wraps over several lines in the narrow pane.\n\n- item " + word)).ToList();
        foreach (var w in new[] { 420, 1300 })
        {
            using var renderer = new RgbaImageRenderer((uint)w, 900);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Show(PilotPage.Ideas);
            var screen = new IdeasScreen([.. ideas], [], ["pilot"], "live", Live.At);
            chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, null, new RectF32(0, 0, w, 900), null, screen);
            File.WriteAllBytes(Path.Combine(folder, $"ideas-{w}.png"), PngWriter.Encode(renderer.Surface.Pixels, w, 900));
        }
    }
}
