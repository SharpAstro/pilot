using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Background tasks page: the node's jobs in the order that matters (running, queued, then the ended), each in words,
/// the selected one's facts and the end of its output beneath, and Cancel, its session and its log, in the window and in
/// the terminal alike.
/// </summary>
public sealed class JobsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobRow Job(string id, JobState state, DateTimeOffset started, DateTimeOffset? ended = null, string? owner = "c3d4e5f6", bool told = false,
        string? step = null, string? error = null, int? exit = null, DateTimeOffset? queued = null) => new()
    {
        Id = id,
        Command = $"dotnet test {id}",
        Cwd = "/r/orbit",
        Owner = owner,
        OwnerName = owner is null ? null : "xy1-orb204-parser",
        Flight = owner is null ? null : "ORB204",
        Leg = owner is null ? null : "parser",
        State = state,
        Step = step,
        Error = error,
        ExitCode = exit,
        LogPath = $"/data/jobs/{id}.log",
        Timeout = TimeSpan.FromHours(1),
        StartedAt = started,
        QueuedAt = queued,
        EndedAt = ended,
        Told = told,
    };

    private static JobsScreen Screen() => new(JobLine.Build(
    [
        Job("j12", JobState.Succeeded, Live.At.AddMinutes(-60), Live.At.AddMinutes(-59), owner: null, exit: 0),
        Job("j13", JobState.Failed, Live.At.AddMinutes(-40), Live.At.AddMinutes(-35), error: "exit 1", exit: 1),
        Job("j15", JobState.Queued, Live.At.AddMinutes(-3), step: "waiting for a slot: 1 of 1 running", queued: Live.At.AddMinutes(-3)),
        Job("j14", JobState.Running, Live.At.AddMinutes(-11), step: "Passed Parser.NestedGroups"),
    ], Live.At), "live", Live.At);

    private static (PilotChrome<RgbaImage> Chrome, JobsView<RgbaImage> Jobs) Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        return (chrome, chrome.Jobs);
    }

    private static string Cells(JobsView<RgbaImage> jobs, JobsScreen screen) =>
        CellLayout.Describe(Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored));

    [Fact]
    public void JobsShowRunningThenQueuedThenTheEndedLastEndedFirstEachInWords()
    {
        var lines = Screen().Lines;

        lines.Select(l => l.Id).ShouldBe(["j14", "j15", "j13", "j12"]);
        lines[0].ToDumpLine().ShouldBe("j14 | dotnet test j14 | ORB204/parser | running (Passed Parser.NestedGroups) | 11m ago | 11m 0s");
        lines[1].ToDumpLine().ShouldBe("j15 | dotnet test j15 | ORB204/parser | queued (waiting for a slot: 1 of 1 running) | 3m ago | -");
        lines[2].ToDumpLine().ShouldBe("j13 | dotnet test j13 | ORB204/parser | failed (exit 1) | 40m ago | 5m 0s | owner not told");
        lines[3].ToDumpLine().ShouldBe("j12 | dotnet test j12 | a terminal | succeeded (exit 0) | 1h ago | 1m 0s");
        Screen().Summary.ShouldBe("4 jobs · 1 running · 1 queued");
    }

    [Fact]
    public void TheRailOpensTheBackgroundTasksPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, jobs) = Chrome(renderer);

        chrome.Show(PilotPage.Jobs);

        chrome.Page.ShouldBe(PilotPage.Jobs);
        chrome.PageWidget.ShouldBeSameAs(jobs);
        PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Jobs).Locked.ShouldBeNull();
    }

    [Fact]
    public void TheSelectedJobShowsItsFactsAndCancelWhileItRuns()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        jobs.CanCommand = true;
        var cancelled = new List<string>();
        jobs.OnCancel = job => cancelled.Add(job.Id);
        jobs.OnOpen = _ => { };

        var arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        var described = CellLayout.Describe(arranged);

        described.ShouldContain("background tasks");
        described.ShouldContain("/data/jobs/j14.log");
        described.ShouldContain("its output is in its log");
        described.ShouldContain("Open log");
        // A click on Cancel's cell cancels it, as a click on its pixels does in the window.
        var cancel = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel" });
        CellLayout.HitTest(arranged, cancel.Bounds.X + 1, cancel.Bounds.Y);
        cancelled.ShouldBe(["j14"]);

        // An ended job, clicked, has nothing left to cancel.
        var failed = arranged.First(a => a.Node.Hit is HitResult.ListItemHit { ListId: JobsView<RgbaImage>.ListId, Index: 2 });
        CellLayout.HitTest(arranged, failed.Bounds.X + 1, failed.Bounds.Y);
        jobs.Selected.ShouldBe(2);
        Cells(jobs, Screen()).ShouldNotContain("Cancel");
    }

    [Fact]
    public async Task TheSelectedJobsOutputIsReadFromTheNodeAndFollowed()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        var reads = 0;
        var changed = new SemaphoreSlim(0);
        jobs.LoadLog = (_, id, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(new JobLog { Lines = [$"{id}: Restore complete", $"{id}: Passed Parser.NestedGroups"], Length = 4096 });
        };
        jobs.Changed = () => changed.Release();

        jobs.Tree(Screen());
        (await changed.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();

        var described = Cells(jobs, Screen());
        described.ShouldContain("j14: Passed Parser.NestedGroups");
        described.ShouldContain("output · 4 KB");
        jobs.Log!.Lines.Length.ShouldBe(2);
        reads.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void AJobsMenuOffersCancelItsSessionItsLogAndCopies()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        jobs.CanCommand = true;
        var copied = new List<string>();
        var sessions = new List<string>();
        jobs.OnCancel = _ => { };
        jobs.OnSession = job => sessions.Add(job.Owner!);
        jobs.OnOpen = _ => { };
        jobs.CopyText = copied.Add;
        var screen = Screen();
        jobs.Tree(screen);

        jobs.OpenMenu(screen.Lines[0], 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));

        jobs.MenuItems.Select(i => i.Label).ShouldBe(["Cancel", "Its session", "Open its log", "Open its folder", "Copy j14", "Copy its command", "Copy its log's path"]);
        jobs.MenuItems[1].OnChoose!();
        sessions.ShouldBe(["c3d4e5f6"]);
        jobs.MenuItems[^2].OnChoose!();
        copied.ShouldBe(["dotnet test j14"]);

        // A job handed over from a terminal wakes nobody, and an ended one is past cancelling.
        jobs.OpenMenu(screen.Lines[3], 3, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        jobs.MenuItems.Select(i => i.Label).ShouldNotContain("Cancel");
        jobs.MenuItems.Select(i => i.Label).ShouldNotContain("Its session");
    }

    [Fact]
    public void ItsSessionOpensTheTranscriptOfTheSessionTheJobWakes()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, jobs) = Chrome(renderer);
        var worker = new SessionRow
        {
            Id = "c3d4e5f6", SessionId = "c3d4e5f6-0000-4000-8000-000000000000", Name = "xy1-orb204-parser", Kind = SessionKind.Background, Cwd = "/r",
            Role = SessionRole.Worker, Flight = "ORB204", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = Live.At,
        };
        var sessions = new SessionsScreen(SessionLine.Build([worker], Live.At), "live", Live.At);
        chrome.LinkCardTranscripts(() => sessions, () => new FlightsScreen([], "live", Live.At));
        chrome.Show(PilotPage.Jobs);

        jobs.OnSession!(Screen().Lines[0].Row);

        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.RowId.ShouldBe("c3d4e5f6");
    }

    [Fact]
    public void TheWindowPaintsTheJobsAndTheirButtons()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, jobs) = Chrome(renderer);
        jobs.CanCommand = true;
        jobs.OnCancel = _ => { };
        jobs.OnSession = _ => { };
        chrome.Show(PilotPage.Jobs);

        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, Screen(), new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        jobs.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain("job:j14:cancel");
        buttons.ShouldContain("job:j14:session");
        regions.Select(r => r.Result).OfType<HitResult.ListItemHit>().Count(h => h.ListId == JobsView<RgbaImage>.ListId).ShouldBe(4);

        // The output takes the panel's width beside the facts: a row sized by its height alone left it none on pixels.
        var arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<float>(0, 0, 1228, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "its output is in its log, on the node's machine" } })
            .Bounds.Width.ShouldBeGreaterThan(400f);
    }
}
