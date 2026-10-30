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
        // These tests are about every job's row; the In play default has its own tests.
        chrome.Play.ShowEverything();
        return (chrome, chrome.Jobs);
    }

    private static string Cells(JobsView<RgbaImage> jobs, JobsScreen screen) =>
        CellLayout.Describe(Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored));

    [Fact]
    public void TheRailsTasksGlyphSaysWhatTheJobsAreDoingAndItsTooltipCountsThem()
    {
        // The rail showed the same hammer and pick whatever its jobs did (SE1, 2026-10-25).
        JobsScreen Of(params JobRow[] rows) => new(JobLine.Build(rows, Live.At), "test", Live.At);

        PilotChrome<RgbaImage>.JobsGlyph(Of(), Live.At).ShouldBe(("📋", null));
        PilotChrome<RgbaImage>.JobsGlyph(Of(Job("j1", JobState.Queued, Live.At)), Live.At).ShouldBe(("🕓", "1 waiting"));
        PilotChrome<RgbaImage>.JobsGlyph(Of(Job("j1", JobState.Running, Live.At), Job("j2", JobState.Queued, Live.At)), Live.At).ShouldBe(("🔄", "1 running, 1 waiting"));
        // A failure in play outranks a run: it is what wants a look.
        PilotChrome<RgbaImage>.JobsGlyph(Of(Job("j1", JobState.Running, Live.At), Job("j3", JobState.Failed, Live.At.AddMinutes(-5), Live.At.AddMinutes(-2))), Live.At)
            .ShouldBe(("🔴", "1 running, 1 failed just now"));
        // One that failed long ago is history, not news.
        PilotChrome<RgbaImage>.JobsGlyph(Of(Job("j3", JobState.Failed, Live.At.AddHours(-3), Live.At.AddHours(-2))), Live.At).ShouldBe(("📋", null));

        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            // The rail four times over, idle, waiting, running and failed, side by side.
            using var renderer = new RgbaImageRenderer(4 * 300, 420);
            JobsScreen[] states = [Of(), Of(Job("j1", JobState.Queued, Live.At)), Of(Job("j1", JobState.Running, Live.At)), Of(Job("j3", JobState.Failed, Live.At.AddMinutes(-5), Live.At.AddMinutes(-2)))];
            for (var i = 0; i < states.Length; i++)
            {
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                chrome.Render(new SessionsScreen([], "test", Live.At), null, null, states[i], new RectF32(i * 300, 0, 300, 420));
            }
            File.WriteAllBytes(capture, SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, 4 * 300, 420));
        }
    }

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
        // A click on Cancel only asks: a job may have run for hours, so nothing is sent until the question is answered.
        var cancel = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel" });
        CellLayout.HitTest(arranged, cancel.Bounds.X + 1, cancel.Bounds.Y);
        cancelled.ShouldBeEmpty();
        arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        TestText.Flat(CellLayout.Describe(arranged)).ShouldContain("Cancel j14?");

        // Keep running takes the question back; asked again, Cancel job sends it.
        var keep = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel-no" });
        CellLayout.HitTest(arranged, keep.Bounds.X + 1, keep.Bounds.Y);
        cancelled.ShouldBeEmpty();
        arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        TestText.Flat(CellLayout.Describe(arranged)).ShouldNotContain("Cancel j14?");
        cancel = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel" });
        CellLayout.HitTest(arranged, cancel.Bounds.X + 1, cancel.Bounds.Y);
        arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        var yes = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel-yes" });
        CellLayout.HitTest(arranged, yes.Bounds.X + 1, yes.Bounds.Y);
        cancelled.ShouldBe(["j14"]);

        // An ended job, clicked, has nothing left to cancel.
        var failed = arranged.First(a => a.Node.Hit is HitResult.ListItemHit { ListId: JobsView<RgbaImage>.ListId, Index: 2 });
        CellLayout.HitTest(arranged, failed.Bounds.X + 1, failed.Bounds.Y);
        jobs.Selected.ShouldBe(2);
        Cells(jobs, Screen()).ShouldNotContain("Cancel");
    }

    private static readonly JobProcessInfo[] Tree =
    [
        new("pwsh", 4812, 0),
        new("dotnet", 5120, 1),
        new("testhost", 6044, 2),
        new("conhost", 6100, 2),
    ];

    /// <summary>The page's screen with j14 (running) on process id 4812.</summary>
    private static JobsScreen ScreenWithPid() => new(JobLine.Build(
    [
        Job("j13", JobState.Failed, Live.At.AddMinutes(-40), Live.At.AddMinutes(-35), error: "exit 1", exit: 1) with { Pid = 777 },
        Job("j14", JobState.Running, Live.At.AddMinutes(-11), step: "Passed Parser.NestedGroups") with { Pid = 4812 },
    ], Live.At), "live", Live.At);

    /// <summary>A view that has read <paramref name="log"/> for the selected job (j14, the first row).</summary>
    private static async Task<JobsView<RgbaImage>> Following(RgbaImageRenderer vessel, JobLog log, JobsScreen screen)
    {
        var (_, jobs) = Chrome(vessel);
        jobs.CanCommand = true;
        var changed = new SemaphoreSlim(0);
        jobs.LoadLog = (_, _, _) => Task.FromResult(log);
        jobs.Changed = () => changed.Release();
        jobs.Tree(screen);
        (await changed.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        return jobs;
    }

    private static JobLog LogWith(string? stuck = null, JobProcessInfo[]? tree = null) =>
        new() { Lines = ["j14: Restore complete"], Length = 64, Pid = 4812, Tree = tree ?? Tree, Stuck = stuck };

    [Fact]
    public async Task ARunningJobsFactsListItsPidAndTheTreeUnderItByDepthAndNothingElseDoes()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var screen = ScreenWithPid();
        var jobs = await Following(vessel, LogWith(), screen);

        var arranged = Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        var described = CellLayout.Describe(arranged);
        described.ShouldContain("pid");
        described.ShouldContain("4812");
        // The wrapper is the pid fact; the tree is what runs under it, a child marked └ under its parent, as pstree draws it.
        var dotnet = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "dotnet 5120" } }).Bounds;
        var testhost = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "└ testhost 6044" } }).Bounds;
        arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "pwsh 4812" } }).ShouldBeFalse();
        testhost.Y.ShouldBeGreaterThan(dotnet.Y);
        (JobsView<RgbaImage>.Branch(1), JobsView<RgbaImage>.Branch(2), JobsView<RgbaImage>.Branch(3)).ShouldBe(("", "└ ", "  └ "));

        // A job that is not running shows neither, even when its log is the one followed.
        var ended = arranged.First(a => a.Node.Hit is HitResult.ListItemHit { ListId: JobsView<RgbaImage>.ListId, Index: 1 });
        CellLayout.HitTest(arranged, ended.Bounds.X + 1, ended.Bounds.Y);
        var after = Cells(jobs, screen);
        after.ShouldNotContain("testhost");
        after.ShouldNotContain("pid");
    }

    [Fact]
    public void ARunningJobsRowTooltipShowsItsPidAndAnEndedOneShowsNone()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        var screen = ScreenWithPid();

        var arranged = Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);

        // The row's hover is the Home chip's brief (#258), whose running line carries the pid.
        arranged.Select(a => a.Node.Tooltip).OfType<string>().Where(t => t.Contains("pid ", StringComparison.Ordinal)).Count().ShouldBe(1);
        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(t => t.Contains("pid 4812", StringComparison.Ordinal));
        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldNotContain(t => t.Contains("pid 777", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStuckJobsStateLineNamesWhatHoldsItsOutputAndOffersForceCancel()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var screen = ScreenWithPid();
        var jobs = await Following(vessel, LogWith(stuck: "process exited, output still held by 5120", tree: [new("dotnet", 5120, 1)]), screen);
        jobs.OnCancel = _ => { };
        jobs.OnForceCancel = _ => { };

        var described = Cells(jobs, screen);

        described.ShouldContain("process exited, output still held by 5120");
        // The state column may cut it, so its whole text is the cell's hover.
        Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored)
            .Select(x => x.Node.Tooltip).ShouldContain("process exited, output still held by 5120");
        described.ShouldContain("Force cancel…");
        // The step it was on is not said over it.
        described.ShouldNotContain("Passed Parser.NestedGroups");
    }

    [Fact]
    public void ForceCancelAndItsMenuItemOnlyAskUntilForceCancelJobIsPressedAndThenSendForce()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        jobs.CanCommand = true;
        var plain = new List<string>();
        var forced = new List<string>();
        jobs.OnCancel = job => plain.Add(job.Id);
        jobs.OnForceCancel = job => forced.Add(job.Id);
        var screen = ScreenWithPid();
        JobLine running = screen.Lines[0];

        void Click(string action)
        {
            var arranged = Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
            var button = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);
            CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y);
        }

        Click("job:j14:force");
        forced.ShouldBeEmpty();
        TestText.Flat(Cells(jobs, screen)).ShouldContain("Force cancel j14?");
        // The question replaces the buttons: neither a plain Cancel nor another Force cancel… is there to slip onto.
        Cells(jobs, screen).ShouldNotContain("Cancel…");

        // Keep running takes it back; asked again, Force cancel job sends force and nothing else.
        Click("job:j14:force-no");
        TestText.Flat(Cells(jobs, screen)).ShouldNotContain("Force cancel j14?");
        forced.ShouldBeEmpty();
        Click("job:j14:force");
        Click("job:j14:force-yes");
        forced.ShouldBe(["j14"]);
        plain.ShouldBeEmpty();
        TestText.Flat(Cells(jobs, screen)).ShouldNotContain("Force cancel j14?");

        // The row's menu asks the same question, and sends nothing by itself.
        jobs.OpenMenu(running, 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        jobs.MenuItems.Select(i => i.Label).Take(2).ShouldBe(["Cancel…", "Force cancel…"]);
        jobs.MenuItems[1].OnChoose!();
        forced.ShouldBe(["j14"]);
        TestText.Flat(Cells(jobs, screen)).ShouldContain("Force cancel j14?");

        // A job that is only queued has no tree to force.
        var (_, fresh) = Chrome(vessel);
        fresh.CanCommand = true;
        fresh.OnCancel = _ => { };
        fresh.OnForceCancel = _ => { };
        var queued = new JobsScreen(JobLine.Build([Job("j15", JobState.Queued, Live.At.AddMinutes(-3), queued: Live.At.AddMinutes(-3))], Live.At), "live", Live.At);
        Cells(fresh, queued).ShouldContain("Cancel…");
        Cells(fresh, queued).ShouldNotContain("Force cancel…");
        fresh.OpenMenu(queued.Lines[0], 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        fresh.MenuItems.Select(i => i.Label).ShouldContain("Cancel…");
        fresh.MenuItems.Select(i => i.Label).ShouldNotContain("Force cancel…");
    }

    [Fact]
    public async Task ThePidTreeAndStuckFactChangingRedrawsThePageWhileTheLogLengthIsTheSame()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        var reads = 0;
        var changed = new SemaphoreSlim(0);
        jobs.LoadLog = (_, _, _) => Task.FromResult(Interlocked.Increment(ref reads) == 1 ? LogWith(tree: [new("pwsh", 4812, 0)]) : LogWith());
        jobs.Changed = () => changed.Release();

        jobs.Tree(ScreenWithPid());
        (await changed.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        (await changed.WaitAsync(JobsView<RgbaImage>.Every + TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        Cells(jobs, ScreenWithPid()).ShouldContain("testhost 6044");
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
    public async Task AFailedReadOfTheLogIsShownWhateverItsTypeAndTheFollowGoesOnToTheNextGoodRead()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        var reads = 0;
        var changed = new SemaphoreSlim(0);
        jobs.LoadLog = (_, id, _) => Interlocked.Increment(ref reads) switch
        {
            // Not a type the follow used to catch.
            1 => throw new ArgumentException("the node sent nonsense"),
            // What HttpClient throws when its timeout lapses: a cancellation that is not the follow's own.
            2 => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."),
            _ => Task.FromResult(new JobLog { Lines = [$"{id}: Restore complete"], Length = 64 }),
        };
        jobs.Changed = () => changed.Release();

        jobs.Tree(Screen());
        (await changed.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        var first = Cells(jobs, Screen());
        first.ShouldContain("could not read the log: the node sent nonsense");
        first.ShouldNotContain("reading…");

        // The follow went on: the timeout is shown in its turn, then the log itself, and the problem is gone.
        (await changed.WaitAsync(JobsView<RgbaImage>.Every + TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        Cells(jobs, Screen()).ShouldContain("could not read the log: The request was canceled due to the configured HttpClient.Timeout");
        (await changed.WaitAsync(JobsView<RgbaImage>.Every + TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        var recovered = Cells(jobs, Screen());
        recovered.ShouldContain("j14: Restore complete");
        recovered.ShouldNotContain("could not read the log");
    }

    [Fact]
    public void ARunningJobOffersPauseAndAPausedOneResumeAndCancelSendingTheirVerbs()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        jobs.CanCommand = true;
        var sent = new List<string>();
        jobs.OnPause = job => sent.Add("pause-job " + job.Id);
        jobs.OnResume = job => sent.Add("resume-job " + job.Id);
        jobs.OnCancel = job => sent.Add("cancel-job " + job.Id);
        var running = JobLine.Build([Job("j14", JobState.Running, Live.At.AddMinutes(-11))], Live.At).Single();
        var paused = JobLine.Build([Job("j16", JobState.Running, Live.At.AddMinutes(-11)) with { PausedAt = Live.At.AddMinutes(-2) }], Live.At).Single();

        paused.State.ShouldBe("paused");
        paused.StateDetail.ShouldBe("since " + SessionLine.Ago(TimeSpan.FromMinutes(2)));
        running.State.ShouldBe("running");

        jobs.OpenMenu(running, 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        var labels = jobs.MenuItems.Select(i => i.Label).ToList();
        labels.ShouldContain("Pause");
        labels.ShouldNotContain("Resume");
        jobs.MenuItems.First(i => i.Label == "Pause").OnChoose!();

        jobs.OpenMenu(paused, 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        labels = jobs.MenuItems.Select(i => i.Label).ToList();
        labels.ShouldContain("Resume");
        labels.ShouldContain("Cancel…");
        labels.ShouldNotContain("Pause");
        jobs.MenuItems.First(i => i.Label == "Resume").OnChoose!();

        sent.ShouldBe(["pause-job j14", "resume-job j16"]);
        new JobsScreen([running, paused], "live", Live.At).Counts.ShouldBe("2 jobs · 1 running · 1 paused");
    }

    [Fact]
    public void AJobOnAnotherNodeOffersNoOpenLogWhileThisNodesJobsKeepIt()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        jobs.OnOpen = _ => { };
        var le1 = new NodeMark("le1.local:47731", "le1.local:47731", "LE1", Here: false);
        var se1 = new NodeMark("node.sock", "node.sock", "SE1", Here: true);
        var remote = JobLine.Build([Job("j14", JobState.Running, Live.At.AddMinutes(-11))], Live.At).Single() with { Node = le1 };
        var local = JobLine.Build([Job("j15", JobState.Running, Live.At.AddMinutes(-11))], Live.At).Single() with { Node = se1 };

        var onRemote = new JobsScreen([remote], "live", Live.At);
        Cells(jobs, onRemote).ShouldNotContain("Open log");
        jobs.OpenMenu(remote, 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        jobs.MenuItems.Select(i => i.Label).ShouldNotContain("Open its log");
        jobs.MenuItems.Select(i => i.Label).ShouldNotContain("Open its folder");

        var onHere = new JobsScreen([local], "live", Live.At);
        Cells(jobs, onHere).ShouldContain("Open log");
        jobs.OpenMenu(local, 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        jobs.MenuItems.Select(i => i.Label).ShouldContain("Open its log");
    }

    [Fact]
    public void AJobsMenuOffersCancelItsSessionItsLogAndCopies()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);
        jobs.CanCommand = true;
        var copied = new List<string>();
        var sessions = new List<string>();
        var cancelled = new List<string>();
        jobs.OnCancel = job => cancelled.Add(job.Id);
        jobs.OnSession = job => sessions.Add(job.Owner!);
        jobs.OnOpen = _ => { };
        jobs.CopyText = copied.Add;
        var screen = Screen();
        jobs.Tree(screen);

        jobs.OpenMenu(screen.Lines[0], 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));

        jobs.MenuItems.Select(i => i.Label).ShouldBe(["Cancel…", "Its session", "Open its log", "Open its folder", "Copy j14", "Copy its command", "Copy its log's path"]);
        jobs.MenuItems[1].OnChoose!();
        sessions.ShouldBe(["c3d4e5f6"]);
        jobs.MenuItems[^2].OnChoose!();
        copied.ShouldBe(["dotnet test j14"]);

        // The menu's Cancel… asks, as the button does: it cancelled at once before, so it is chosen here, not only read.
        jobs.OpenMenu(screen.Lines[0], 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        jobs.MenuItems[0].OnChoose!();
        cancelled.ShouldBeEmpty();
        TestText.Flat(Cells(jobs, screen)).ShouldContain("Cancel j14?");

        // A job handed over from a terminal wakes nobody, and an ended one is past cancelling.
        jobs.OpenMenu(screen.Lines[3], 3, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        jobs.MenuItems.Select(i => i.Label).ShouldNotContain("Cancel…");
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

    [Fact]
    public void CapturesOfCancelAtRestHoveredAndAskedAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var (width, height) in new[] { (1280, 800), (2000, 900) })
        {
            foreach (var state in new[] { "rest", "hover", "asked" })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (chrome, jobs) = Chrome(renderer);
                jobs.CanCommand = true;
                jobs.OnCancel = _ => { };
                jobs.OnSession = _ => { };
                chrome.Show(PilotPage.Jobs);
                void Draw() => chrome.Render(new SessionsScreen([], "test", Live.At), null, null, Screen(), new RectF32(0, 0, width, height));
                Draw();
                if (state == "hover")
                {
                    // The page sits right of the 52 px rail; find the pill in the page's own pixel layout and put the pointer on it.
                    var page = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<float>(52f, 0f, width - 52f, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
                    var pill = page.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel" }).Bounds;
                    jobs.Pointer = (pill.X + pill.Width / 2f, pill.Y + pill.Height / 2f);
                    Draw();
                }
                else if (state == "asked")
                {
                    var arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
                    var cancel = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel" });
                    CellLayout.HitTest(arranged, cancel.Bounds.X + 1, cancel.Bounds.Y);
                    Draw();
                }
                File.WriteAllBytes(Path.Combine(dir, $"job-cancel-{state}-{width}.png"),
                    SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }

    [Fact]
    public async Task CapturesOfARunningJobsTreeAndTheForceCancelQuestionAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        // Many processes, long names: the tree is cut at four with a count, and a long name is the fact column's to carry.
        JobProcessInfo[] many =
        [
            new("pwsh", 4812, 0), new("dotnet", 5120, 1), new("testhost", 6044, 2), new("conhost.exe --headless", 6100, 2),
            new("node-with-a-rather-long-program-name", 6200, 3), new("msbuild", 6300, 1), new("vbcscompiler", 6400, 2),
        ];
        foreach (var (width, height) in new[] { (1280, 800), (2000, 900) })
        {
            foreach (var state in new[] { "tree", "tree-hover", "force-asked", "stuck" })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (chrome, jobs) = Chrome(renderer);
                jobs.CanCommand = true;
                jobs.OnCancel = _ => { };
                jobs.OnForceCancel = _ => { };
                jobs.OnSession = _ => { };
                var screen = ScreenWithPid();
                var log = state == "stuck" ? LogWith(stuck: "process exited, output still held by 5120", tree: [new("dotnet", 5120, 1)]) : LogWith(tree: many);
                var read = new SemaphoreSlim(0);
                jobs.LoadLog = (_, _, _) => Task.FromResult(log);
                jobs.Changed = () => read.Release();
                chrome.Show(PilotPage.Jobs);
                void Draw() => chrome.Render(new SessionsScreen([], "test", Live.At), null, null, screen, new RectF32(0, 0, width, height));
                Draw();
                (await read.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
                Draw();
                if (state == "tree-hover")
                {
                    var page = Layout.Engine.Arrange(jobs.Tree(screen), new Rect<float>(52f, 0f, width - 52f, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
                    var pill = page.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:force" }).Bounds;
                    jobs.Pointer = (pill.X + pill.Width / 2f, pill.Y + pill.Height / 2f);
                    Draw();
                }
                else if (state == "force-asked")
                {
                    var arranged = Layout.Engine.Arrange(jobs.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
                    var force = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:force" });
                    CellLayout.HitTest(arranged, force.Bounds.X + 1, force.Bounds.Y);
                    Draw();
                }
                File.WriteAllBytes(Path.Combine(dir, $"job-force-{state}-{width}.png"),
                    SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }

    // WCAG 2.x contrast ratio.
    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        static double Linear(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        static double L(RGBAColor32 c) => 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void ACancelButtonsLabelClearsAaAtRestAndHovered()
    {
        Contrast(JobsView<RgbaImage>.DangerLabel, JobsView<RgbaImage>.Danger).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(JobsView<RgbaImage>.DangerLabel, JobsView<RgbaImage>.DangerHover).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void ABackgroundTasksPageNamesTheJobHoldingTheQueueOnlyWhileAJobWaits()
    {
        Screen().Holder.ShouldBe("j14 holds the queue (xy1-orb204-parser), held for 11m 0s");
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, jobs) = Chrome(vessel);

        Cells(jobs, Screen()).ShouldContain("j14 holds the queue (xy1-orb204-parser), held for 11m 0s");

        var noQueue = new JobsScreen(JobLine.Build([Job("j14", JobState.Running, Live.At.AddMinutes(-11))], Live.At), "live", Live.At);
        noQueue.Holder.ShouldBeNull();
        Cells(jobs, noQueue).ShouldNotContain("holds the queue");
    }

    private static void ClickTitle(JobsView<RgbaImage> jobs, JobsScreen screen, string title)
    {
        var bounds = new RectF32(0f, 0f, 1280f, 800f);
        jobs.Render(screen, bounds);
        var regions = new List<ClickableRegion>();
        jobs.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == "jobs:table:" + title).OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        jobs.Render(screen, bounds);
    }

    [Fact]
    public void AClickOnATitleSortsTheJobsByItsValueAndASecondFlipsIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (_, jobs) = Chrome(renderer);
        jobs.FontPath = SessionsCapture.FontPath;
        var screen = Screen();

        ClickTitle(jobs, screen, "Job");
        jobs.Shown.ShouldBe(["j12", "j13", "j14", "j15"]);
        ClickTitle(jobs, screen, "Job");
        jobs.Shown.ShouldBe(["j15", "j14", "j13", "j12"]);

        // Started by time, newest first at the first click: the queued job is the newest handed over.
        ClickTitle(jobs, screen, "Started");
        jobs.Shown.ShouldBe(["j15", "j14", "j13", "j12"]);
        ClickTitle(jobs, screen, "Started");
        jobs.Shown.ShouldBe(["j12", "j13", "j14", "j15"]);

        // Took by how long: the running job has run longest, the queued one not at all.
        ClickTitle(jobs, screen, "Took");
        jobs.Shown.ShouldBe(["j14", "j13", "j12", "j15"]);
        jobs.Table.Sort.ShouldBe(new ListTable.SortState("Took", Descending: true));

        ClickTitle(jobs, screen, "State");
        jobs.Shown.Take(2).ShouldBe(["j14", "j15"]);
    }

    [Fact]
    public void TheSelectedJobStaysSelectedThroughASort()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (_, jobs) = Chrome(renderer);
        jobs.FontPath = SessionsCapture.FontPath;
        var screen = Screen();
        jobs.Render(screen, new RectF32(0f, 0f, 1280f, 800f));
        jobs.Select(screen, screen.Lines.Single(l => l.Id == "j14"));
        var before = jobs.Selected;

        ClickTitle(jobs, screen, "Started");

        jobs.Shown[jobs.Selected].ShouldBe("j14");
        jobs.Selected.ShouldNotBe(before, "the sort moved the row, and the selection went with it");
    }
}
