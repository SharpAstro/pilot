using System.Globalization;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// What one frame of the Ideas page costs at DE1's size (#1406): about 4,700 sessions in the SessionsScreen the chrome is given, 77 flights, 60 ideas with
/// Markdown bodies, 300 open issues across 5 repos and 15 milestones, and 20 open cards in the panel. Each case renders the page through PilotChrome in
/// List and Tree view at 1400x900 DPI 1 and 500x900 DPI 1.5, and writes one line (to PILOT_FRAME_TIMES, else frame-times.txt in PILOT_JOB_ARTIFACTS) that
/// splits a first frame and a repeat frame of the same pages into the Ideas tree build, the chrome around it and the page's layout; the repeat frame's
/// layout is split again into arrange and draw (the first frame's layout is arrange and draw together, as arrange alone cannot be timed cold twice).
/// </summary>
public sealed class IdeasFrameCostTests
{
    private static readonly string[] Repos = ["pilot", "tianwen", "DIR.Lib", "Console.Lib", "SdlVulkan.Renderer"];

    internal static IdeasScreen DeSized()
    {
        var now = PageData.Now;
        var ideas = Enumerable.Range(0, 60).Select(i => new IdeaRow
        {
            Id = IdeaIds.Id(900 + i), Repo = Repos[i % Repos.Length], Title = $"Idea {i}: an idea with a title of some length that wraps on a narrow window",
            Body = BodyOf(i),
            State = i % 10 == 9 ? IdeaState.Planned : IdeaState.Noted, UpdatedAt = now.AddMinutes(i), Age = "3h", Origin = "ada",
            Url = $"https://github.com/SharpAstro/{Repos[i % Repos.Length]}/issues/{900 + i}", NeedsDesign = i % 3 == 0,
            Designs = DesignsOf(i),
        }).Select(idea => idea with { Images = IdeaImages.In(idea.Body), Design = idea.Designs.FirstOrDefault()?.Id, DesignVersion = idea.Designs.FirstOrDefault()?.Version }).ToArray();
        var milestones = Enumerable.Range(0, 15).Select(i => new MilestoneRow { Repo = Repos[i % Repos.Length], Number = 20 + i, Title = $"Milestone {i}", OpenIssues = 20, ClosedIssues = 5, DueOn = now.AddDays(i) }).ToArray();
        var issues = Enumerable.Range(0, 300).Select(i => new IssueRow
        {
            Repo = Repos[i % Repos.Length], Number = 100 + i, Title = $"Issue {i}: work that is open for a while", Body = "Issue " + i, Age = "2d", Author = "ada",
            Milestone = i % 5 == 0 ? null : $"Milestone {i % 15}", UpdatedAt = now.AddMinutes(-i), Url = $"https://github.com/SharpAstro/pilot/issues/{100 + i}",
            Readiness = i % 4 == 0 ? new IssueReadiness(i % 8 == 0, ["No tests named."], now - TimeSpan.FromSeconds(30)) : null,
        }).ToArray();
        // The milestones of the issues are of the issue's own repo: keep each issue's milestone one of its repo's.
        issues = [.. issues.Select(i => i.Milestone is null ? i : i with { Milestone = milestones.First(m => m.Repo == i.Repo && int.Parse(m.Title["Milestone ".Length..], CultureInfo.InvariantCulture) % Repos.Length == Array.IndexOf(Repos, i.Repo)).Title })];
        var flights = Enumerable.Range(0, 77).Select(i => new FlightRow
        {
            Code = $"PIL{380 + i}", Repo = "pilot", RepoPath = "C:/repos/pilot", Issue = 380 + i, FromIdea = i < 5 ? IdeaIds.Id(900 + i) : null, Branch = $"flight/pil{380 + i}",
            Worktree = $"C:/repos/pilot/.claude/worktrees/pil{380 + i}", Base = "origin/main", State = (FlightState)(i % 3), CreatedAt = now.AddHours(-i - 1),
        }).ToArray();
        return new IdeasScreen(ideas, [], [.. Repos], "live from the node", now) { Milestones = milestones, Issues = issues, Flights = flights };
    }

    // A DE1 idea's body: most are a short paragraph and a list; every fourth is long (a list of 20, a code block, a table and a wide link,
    // over 40 lines); every tenth carries one or two pasted images, written as the markers a paste leaves (the node's copies, by id).
    private static string BodyOf(int i)
    {
        var inv = CultureInfo.InvariantCulture;
        var body = new System.Text.StringBuilder();
        body.Append(inv, $"Show **ideas** in a pane, number {i}.\n\n- a list with `code`\n- a box with a [link](https://example.com/{i})\n- a long word https://github.com/SharpAstro/pilot/issues/{900 + i}/comments\n\n");
        body.Append(inv, $"A second paragraph that says a good deal more about idea {i}, so the body is laid out over several lines.\n");
        if (i % 4 == 0)
        {
            body.Append("\n## What it needs\n\n");
            for (var n = 1; n <= 20; n++)
            {
                body.Append(inv, $"{n}. step {n} of idea {i}: read the `IdeasView` row and say what it costs, then *cut* it\n");
            }
            body.Append("\n```csharp\n");
            for (var n = 0; n < 8; n++)
            {
                body.Append(inv, $"var row{n} = board.Items.Where(item => item.Section == \"open\" && item.Rank > {n}).ToList();\n");
            }
            body.Append("```\n\n| Phase | First | Repeat |\n|---|---|---|\n| tree | 12 ms | 4 ms |\n| layout | 80 ms | 40 ms |\n\n");
            body.Append(inv, $"See [the long discussion](https://github.com/SharpAstro/pilot/issues/{900 + i}#issuecomment-1234567890123456789012345678901234567890) and https://github.com/SharpAstro/DIR.Lib/blob/main/src/DIR.Lib/Layout/Engine/ArrangeWrappedTextAcrossManyLines.cs#L100-L200\n");
        }
        if (i % 10 == 3)
        {
            body.Append('\n').Append(IdeaImages.Marker("screenshot", $"{i:x4}a1b2c3d4e5f6", "png")).Append('\n');
            if (i % 20 == 3)
            {
                body.Append(IdeaImages.Marker("before", $"{i:x4}f6e5d4c3b2a1", "png")).Append('\n');
            }
        }
        return body.ToString();
    }

    // Most DE1 ideas have a design linked (every fifth has none); every seventh has two.
    private static IdeaDesignLink[] DesignsOf(int i) => i % 5 == 4 ? []
        : i % 7 == 0 ? [new($"DSN-{i}", $"Design for idea {i}", 3), new($"DSN-{i + 100}", $"A second take on idea {i}", null)]
        : [new($"DSN-{i}", $"Design for idea {i}", 1 + i % 4)];

    // A decoded screenshot, as the GUI's AttachmentPictures hands the gallery once a copy is fetched: the CPU render paints it scaled into each thumb.
    internal static AttachmentPicture Screenshot()
    {
        const int Width = 1280, Height = 800;
        var rgba = new byte[Width * Height * 4];
        for (var at = 0; at < rgba.Length; at += 4)
        {
            (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = ((byte)(at / 4 % Width * 255 / Width), 90, 160, 255);
        }
        return new AttachmentPicture(Width, Height, rgba);
    }

    internal static SessionsScreen Sessions()
    {
        var rows = Enumerable.Range(0, 4700).Select(i => PageData.Row($"s{i:00000}00", $"de1-pil{380 + i % 77}-worker-{i}", (SessionState)(i % 3)) with
        {
            Repo = "pilot", Role = (SessionRole)(i % 6), Flight = $"PIL{380 + i % 77}", Leg = "overlap-check", Model = "claude-opus-5-5",
        }).ToList();
        return new SessionsScreen(SessionLine.Build(rows, PageData.Now), "test", PageData.Now);
    }

    internal static CardsScreen Cards() =>
        new([.. Enumerable.Range(0, 20).Select(i => new CardRow
        {
            Id = $"c{i}", Bin = CardBin.Ring, Kind = "question", Subject = $"flight/PIL{380 + i}", Title = $"Card {i}: a question that waits on a person", At = PageData.Now, State = CardState.Open,
        })], []);

    [Theory]
    [InlineData("List", 1400, 1f, 900)]
    [InlineData("Tree", 1400, 1f, 900)]
    [InlineData("List", 500, 1.5f, 900)]
    [InlineData("Tree", 500, 1.5f, 900)]
    public void AnIdeasFrameAtDe1sSizeIsTimedPerPhase(string viewName, int width, float dpi, int height)
    {
        var mode = Enum.Parse<IdeasViewMode>(viewName);
        var ideas = DeSized();
        var sessions = Sessions();
        var cards = Cards();
        // A warm-up frame of the same page on a throwaway chrome: the first case in a process otherwise carries the JIT and the static
        // start-up in its first frame, and the first frames of the cases could not be compared.
        using (var warm = new RgbaImageRenderer((uint)width, (uint)height))
        {
            var cold = new PilotChrome<RgbaImage>(warm, new SessionsView<RgbaImage>(warm)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
            cold.Ideas.ViewMode = mode;
            // The gallery is wired as the measured chrome's is, so the picture paint path is jitted here and not in the first measured frame.
            var warmPicture = Screenshot();
            cold.Gallery.Lookup = _ => warmPicture;
            cold.Gallery.Draw = AttachmentPictures.PaintOn(warm.Surface);
            cold.Show(PilotPage.Ideas);
            cold.Render(sessions, null, cards, null, null, null, null, new RectF32(0, 0, width, height), null, ideas);
        }
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        chrome.Play.ShowEverything();
        chrome.Cards.CanTell = true;
        chrome.Notifications.State = BarStates.Docked;
        chrome.Ideas.ViewMode = mode;
        var picture = Screenshot();
        chrome.Gallery.Lookup = _ => picture;
        chrome.Gallery.Draw = AttachmentPictures.PaintOn(renderer.Surface);
        var bounds = new RectF32(0, 0, width, height);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double Frame(Action render)
        {
            watch.Restart();
            render();
            return watch.Elapsed.TotalMilliseconds;
        }

        // Each frame is split from inside: the page's own tree build and its layout (arrange and draw) are timed by IdeasView around its
        // RenderLayout, and the chrome is what is left of the frame, so the three add up to it. The first frame is on a fresh chrome and a
        // fresh screen: nothing of this chrome's cached, though the process is warm (above). A repeat frame is the same screen again (a hover, a scroll).
        chrome.Show(PilotPage.Ideas);
        (double Total, double Tree, double Layout, double Chrome) Split(IdeasScreen screen)
        {
            var total = Frame(() => chrome.Render(sessions, null, cards, null, null, null, null, bounds, null, screen));
            var built = chrome.Ideas.LastTreeBuild.TotalMilliseconds;
            var laid = chrome.Ideas.LastLayout.TotalMilliseconds;
            return (total, built, laid, total - built - laid);
        }
        var first = Split(ideas with { AsOf = ideas.AsOf.AddSeconds(1) });
        var same = ideas with { AsOf = ideas.AsOf.AddSeconds(2) };
        Split(same);
        var repeats = new[] { Split(same), Split(same), Split(same) };
        var best = repeats.MinBy(r => r.Total);

        // Arrange alone, on the page's tree at the page's size: the layout above is arrange and draw together, so draw is what is left of it.
        var page = chrome.PageBounds.ShouldNotBeNull();
        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);
        var tree = chrome.Ideas.Tree(same);
        var arrange = Enumerable.Range(0, 3).Min(_ => Frame(() => Layout.Engine.Arrange(tree, new Rect<float>(page.X, page.Y, page.Width, page.Height), ctx)));
        var draw = best.Layout - arrange;

        var inv = CultureInfo.InvariantCulture;
        var line = $"ideas frame | {mode} | {width} wide | DPI {dpi.ToString(inv)}\tfirst frame {first.Total:F1} ms (tree {first.Tree:F1} + layout {first.Layout:F1} + chrome {first.Chrome:F1})"
            + $"\trepeat frame {best.Total:F1} ms (tree {best.Tree:F1} + layout {best.Layout:F1} + chrome {best.Chrome:F1})\tof layout: arrange {arrange:F1} ms, draw {draw:F1} ms";
        var repeatMax = repeats.Max(r => r.Total);
        var file = Environment.GetEnvironmentVariable("PILOT_FRAME_TIMES") is { Length: > 0 } given ? given
            : Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } dir ? Path.Combine(dir, "frame-times.txt")
            : null;
        if (file is not null)
        {
            lock (Gate)
            {
                File.AppendAllLines(file, [line]);
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        // Generous, as PageFrameTimeTests: a loaded CI machine must not fail it; the cut is pinned by the counted test below.
        repeatMax.ShouldBeLessThan(1000d, line);
    }

    /// <summary>The cut, counted and not timed (#1406): frames of the same IdeasScreen reuse the board; a new screen builds it again.</summary>
    [Fact]
    public void FramesOfTheSameScreenReuseTheBoardAndANewScreenBuildsItAgain()
    {
        var ideas = DeSized();
        using var renderer = new RgbaImageRenderer(1400, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Ideas);
        var sessions = new SessionsScreen([], "test", PageData.Now);
        var bounds = new RectF32(0, 0, 1400, 900);
        for (var i = 0; i < 5; i++)
        {
            chrome.Render(sessions, null, null, null, null, null, null, bounds, null, ideas);
        }
        chrome.Ideas.BoardBuilds.ShouldBe(1);

        chrome.Render(sessions, null, null, null, null, null, null, bounds, null, ideas with { AsOf = ideas.AsOf.AddSeconds(1) });
        chrome.Ideas.BoardBuilds.ShouldBe(2);
        // The filters work on the kept board without building it again: toggled on the same screen, the count stays.
        var screen = ideas with { AsOf = ideas.AsOf.AddSeconds(2) };
        chrome.Render(sessions, null, null, null, null, null, null, bounds, null, screen);
        chrome.Ideas.BoardBuilds.ShouldBe(3);
        chrome.Ideas.OnlyNeedsDesign = true;
        chrome.Render(sessions, null, null, null, null, null, null, bounds, null, screen);
        chrome.Ideas.OnlyNeedsDesign = false;
        chrome.Ideas.OnlyHasDesign = true;
        chrome.Render(sessions, null, null, null, null, null, null, bounds, null, screen);
        chrome.Ideas.BoardBuilds.ShouldBe(3);
    }

    private static readonly Lock Gate = new();
}
