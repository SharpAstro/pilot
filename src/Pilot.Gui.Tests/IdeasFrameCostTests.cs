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
/// splits a first frame and a repeat frame of the same pages into the Ideas tree build, the chrome around it, arrange and draw.
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
            Body = $"Show **ideas** in a pane, number {i}.\n\n- a list with `code`\n- a box with a [link](https://example.com/{i})\n- a long word https://github.com/SharpAstro/pilot/issues/{900 + i}/comments\n\nA second paragraph that says a good deal more about idea {i}, so the body is laid out over several lines.",
            State = i % 10 == 9 ? IdeaState.Planned : IdeaState.Noted, UpdatedAt = now.AddMinutes(i), Age = "3h", Origin = "ada",
            Url = $"https://github.com/SharpAstro/{Repos[i % Repos.Length]}/issues/{900 + i}", NeedsDesign = i % 3 == 0,
        }).ToArray();
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
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        chrome.Play.ShowEverything();
        chrome.Cards.CanTell = true;
        chrome.Notifications.State = BarStates.Docked;
        chrome.Ideas.ViewMode = mode;
        var bounds = new RectF32(0, 0, width, height);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double Frame(Action render)
        {
            watch.Restart();
            render();
            return watch.Elapsed.TotalMilliseconds;
        }

        // The chrome alone: the same window on a page that is cheap to draw, so what is left of an Ideas frame is the page's.
        chrome.Show(PilotPage.Fleet);
        var chromeFirst = Frame(() => chrome.Render(sessions, null, cards, null, null, null, null, bounds, null, ideas));
        var chromeRepeat = Frame(() => chrome.Render(sessions, null, cards, null, null, null, null, bounds, null, ideas));

        chrome.Show(PilotPage.Ideas);
        var first = Frame(() => chrome.Render(sessions, null, cards, null, null, null, null, bounds, null, ideas));
        var repeat = Frame(() => chrome.Render(sessions, null, cards, null, null, null, null, bounds, null, ideas));
        var repeat2 = Frame(() => chrome.Render(sessions, null, cards, null, null, null, null, bounds, null, ideas));

        var page = chrome.PageBounds.ShouldNotBeNull();
        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);
        var treeFirst = Frame(() => chrome.Ideas.Tree(ideas));
        Layout.Node tree = null!;
        var treeRepeat = Frame(() => tree = chrome.Ideas.Tree(ideas));
        var arrange = Frame(() => Layout.Engine.Arrange(tree, new Rect<float>(page.X, page.Y, page.Width, page.Height), ctx));
        var draw = Math.Max(0d, repeat2 - treeRepeat - arrange - chromeRepeat);

        var inv = CultureInfo.InvariantCulture;
        var line = $"ideas frame | {mode} | {width} wide | DPI {dpi.ToString(inv)}\tfirst frame {first:F1} ms (chrome {chromeFirst:F1})\trepeat frame {repeat:F1} / {repeat2:F1} ms"
            + $"\ttree build {treeFirst:F1} / {treeRepeat:F1} ms\tchrome {chromeRepeat:F1} ms\tarrange {arrange:F1} ms\tdraw (rest) {draw:F1} ms";
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
        Math.Max(repeat, repeat2).ShouldBeLessThan(1000d, line);
    }

    private static readonly Lock Gate = new();
}
