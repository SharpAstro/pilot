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
        var now = Live.At.AddHours(2);
        var flight = FlightLine.Build(
            [new FlightRow { Code = "PIL790", Repo = "pilot", RepoPath = "/r/pilot", Issue = 790, Title = "Bump " + word, Branch = "flight/pil790", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At }],
            [], now,
            [new LegRow { Flight = "PIL790", Name = "review-notes-" + word, Order = 1, Goal = "Goal with " + word + " in it and a long sentence that wraps over lines.", DoneWhen = "Done when " + word + " holds.", State = LegState.Flying,
                Checklist = [new CheckItem { Text = "Step one " + word }, new CheckItem { Text = "Step two `" + word + "` is clipped?", Done = true }] }]).Single();
        foreach (var w in new[] { 420, 1300 })
        {
            using var fr = new RgbaImageRenderer((uint)w, 900);
            var fc = new PilotChrome<RgbaImage>(fr, new SessionsView<RgbaImage>(fr)) { FontPath = SessionsCapture.FontPath };
            fc.Show(PilotPage.Flights);
            fc.Flights.OpenDetail(flight.Code);
            fc.Render(new SessionsScreen([], "test", now), new FlightsScreen([flight], "test", now), new RectF32(0, 0, w, 900));
            File.WriteAllBytes(Path.Combine(folder, $"flight-{w}.png"), PngWriter.Encode(fr.Surface.Pixels, w, 900));
        }
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
