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
            .ShouldBe(("🛑", "1 running, 1 failed just now"));
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
        CellLayout.Describe(arranged).ShouldContain("Cancel j14?");

        // Keep running takes the question back; asked again, Cancel job sends it.
        var keep = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel-no" });
        CellLayout.HitTest(arranged, keep.Bounds.X + 1, keep.Bounds.Y);
        cancelled.ShouldBeEmpty();
        arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        CellLayout.Describe(arranged).ShouldNotContain("Cancel j14?");
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
        Cells(jobs, screen).ShouldContain("Cancel j14?");

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
    public void CapturesOfCancelAndItsQuestionAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var (width, height) in new[] { (1280, 800), (2000, 900) })
        {
            foreach (var asked in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (chrome, jobs) = Chrome(renderer);
                jobs.CanCommand = true;
                jobs.OnCancel = _ => { };
                jobs.OnSession = _ => { };
                chrome.Show(PilotPage.Jobs);
                chrome.Render(new SessionsScreen([], "test", Live.At), null, null, Screen(), new RectF32(0, 0, width, height));
                if (asked)
                {
                    var arranged = Layout.Engine.Arrange(jobs.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
                    var cancel = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "job:j14:cancel" });
                    CellLayout.HitTest(arranged, cancel.Bounds.X + 1, cancel.Bounds.Y);
                    chrome.Render(new SessionsScreen([], "test", Live.At), null, null, Screen(), new RectF32(0, 0, width, height));
                }
                File.WriteAllBytes(Path.Combine(dir, $"job-cancel-{(asked ? "asked" : "button")}-{width}.png"),
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
}
