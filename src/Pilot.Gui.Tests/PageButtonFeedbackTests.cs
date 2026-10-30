using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Background tasks, Flights (the panel under the list and the canvas, and the flight's page), Home and Sessions pages' buttons that
/// ask the node (issue 461, the owner 2026-10-29: "all buttons should always have a feedback in one form or the other"): each says it is
/// working on the frame it is pressed and takes no second press, shows the outcome at once when the node answers, and says a refusal in
/// the node's words where it was pressed. Every test's node answers late, through a <see cref="TaskCompletionSource{T}"/>.
/// </summary>
public sealed class PageButtonFeedbackTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    // ---- the page's words and buttons, as the cell layout draws them ----

    internal static string Words(Layout.Node tree) =>
        TestText.Flat(CellLayout.Describe(Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored)));

    internal static bool Has(Layout.Node tree, string action) =>
        Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored).Any(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);

    internal static void Click(Layout.Node tree, string action)
    {
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored);
        var button = arranged.FirstOrDefault(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);
        button.Node.ShouldNotBeNull($"no button {action} on the page: it is not there to press");
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y);
    }

    /// <summary>The node: each ask is kept for the test to answer later, in order.</summary>
    internal sealed class LateNode
    {
        public List<string> Asked { get; } = [];
        private readonly Queue<TaskCompletionSource<string?>> _answers = new();

        public Task<string?> Ask(string what)
        {
            Asked.Add(what);
            var answer = new TaskCompletionSource<string?>();
            _answers.Enqueue(answer);
            return answer.Task;
        }

        /// <summary>Answers the oldest ask: null is "taken", text is the refusal in the node's words.</summary>
        public void Answer(string? refusal) => _answers.Dequeue().SetResult(refusal);
    }

    internal static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        done().ShouldBeTrue("the press did not settle");
    }

    // ---- Background tasks ----

    private static JobRow Job(string id, JobState state) => new()
    {
        Id = id,
        Command = $"dotnet test {id}",
        Cwd = "/r/orbit",
        Owner = "c3d4e5f6",
        OwnerName = "xy1-orb204-parser",
        Flight = "ORB204",
        Leg = "parser",
        State = state,
        LogPath = $"/data/jobs/{id}.log",
        Timeout = TimeSpan.FromHours(1),
        StartedAt = Live.At.AddMinutes(-11),
    };

    [Fact]
    public async Task JobsPauseCancelAndForceCancelSayWorkingAtOnceThenTheRowSaysSoOrTheNodesWordsStandBesideThem()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        var jobs = chrome.Jobs;
        jobs.CanCommand = true;
        var node = new LateNode();
        jobs.OnPause = job => node.Ask($"pause {job.Id}");
        jobs.OnResume = job => node.Ask($"resume {job.Id}");
        jobs.OnCancel = job => node.Ask($"cancel {job.Id}");
        jobs.OnForceCancel = job => node.Ask($"force {job.Id}");
        var screen = new JobsScreen(JobLine.Build([Job("j14", JobState.Running)], Live.At), "live", Live.At);
        Layout.Node Tree() => jobs.Tree(screen);

        // Pause: working at once, on the row and on the button, and no second press while the node thinks.
        Click(Tree(), "job:j14:pause");
        node.Asked.ShouldBe(["pause j14"]);
        Words(Tree()).ShouldContain("pausing…");
        Has(Tree(), "job:j14:pause").ShouldBeFalse();
        Has(Tree(), "job:j14:resume").ShouldBeFalse();

        // Taken: the row keeps saying what is being done until a read shows the job changed.
        node.Answer(null);
        await Until(() => Has(Tree(), "job:j14:pause"));
        Words(Tree()).ShouldContain("pausing…");

        // Refused: the node's words beside the buttons, and the row says what the job is again.
        Click(Tree(), "job:j14:pause");
        node.Answer("j14 already ended");
        await Until(() => Has(Tree(), "job:j14:pause"));
        Words(Tree()).ShouldContain("j14 already ended");
        Words(Tree()).ShouldNotContain("pausing…");

        // Cancel asks first, then goes through the helper from its confirm.
        Click(Tree(), "job:j14:cancel");
        node.Asked.Count.ShouldBe(2);
        Click(Tree(), "job:j14:cancel-yes");
        node.Asked.ShouldBe(["pause j14", "pause j14", "cancel j14"]);
        Words(Tree()).ShouldContain("cancelling…");
        Has(Tree(), "job:j14:pause").ShouldBeFalse();
        node.Answer(null);
        await Until(() => Has(Tree(), "job:j14:pause"));
        Words(Tree()).ShouldContain("cancelling…");

        // Force cancel, from its own confirm, refused.
        Click(Tree(), "job:j14:force");
        Click(Tree(), "job:j14:force-yes");
        node.Asked[^1].ShouldBe("force j14");
        Words(Tree()).ShouldContain("force cancelling…");
        node.Answer("the wrapper is gone");
        await Until(() => Has(Tree(), "job:j14:force"));
        Words(Tree()).ShouldContain("the wrapper is gone");
    }

    [Fact]
    public async Task AJobsResumeIsWorkingThenTakenAndItsConnectReadsAskingUntilTheGrantAnswers()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        var jobs = chrome.Jobs;
        jobs.CanCommand = true;
        var node = new LateNode();
        jobs.OnCancel = _ => Task.FromResult<string?>(null);
        jobs.OnResume = job => node.Ask($"resume {job.Id}");
        var paused = Job("j16", JobState.Running) with { PausedAt = Live.At.AddMinutes(-2) };
        var screen = new JobsScreen(JobLine.Build([paused], Live.At), "live", Live.At);

        Click(jobs.Tree(screen), "job:j16:resume");
        Words(jobs.Tree(screen)).ShouldContain("resuming…");
        Has(jobs.Tree(screen), "job:j16:resume").ShouldBeFalse();
        node.Answer(null);
        await Until(() => Has(jobs.Tree(screen), "job:j16:resume"));

        // A job on a node this user holds no grant for: Connect reads asking… until the grant answers, then says why when it was refused.
        var far = new NodeMark("192.168.0.79:47731", "192.168.0.79:47731", "DE1", Here: false);
        jobs.CanSteer = n => n?.Here != false;
        jobs.OnConnect = n => node.Ask($"connect {n.Name}");
        var watched = new JobsScreen([.. JobLine.Build([Job("j20", JobState.Running)], Live.At).Select(l => l with { Node = far })], "live", Live.At);
        Click(jobs.Tree(watched), "job:j20:connect");
        node.Asked[^1].ShouldBe("connect DE1");
        Words(jobs.Tree(watched)).ShouldContain("asking…");
        Has(jobs.Tree(watched), "job:j20:connect").ShouldBeFalse();
        node.Answer("DE1 did not allow it");
        await Until(() => Has(jobs.Tree(watched), "job:j20:connect"));
        Words(jobs.Tree(watched)).ShouldContain("DE1 did not allow it");
    }

    // ---- Flights: the panel under the list and the canvas, and the flight's own page ----

    private static FlightRow Flight(string code, Func<FlightRow, FlightRow>? with = null)
    {
        var row = new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = $"flight/{code.ToLowerInvariant()}",
            Worktree = $"/r/tianwen/.claude/worktrees/{code}", Base = "main", State = FlightState.Flying, CreatedAt = Live.At,
        };
        return with?.Invoke(row) ?? row;
    }

    private static LegRow Leg(string flight, string name, int order, LegState state) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state,
    };

    // A flight between legs: its next leg is Start render, a button that asks the node.
    private static FlightsScreen Between() => new(FlightLine.Build([Flight("TIA1611")], [], Now,
        [Leg("TIA1611", "parser", 1, LegState.Done), Leg("TIA1611", "render", 2, LegState.Planned)]), "live", Now);

    private static string StartHit => $"flight:TIA1611:{Verbs.StartLeg}";

    private static FlightsView<RgbaImage> FlightsOf(RgbaImageRenderer renderer, FlightsViewMode mode, LateNode node)
    {
        var flights = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath }.Flights;
        flights.CanCommand = true;
        flights.Mode = mode;
        flights.OnAction = action => node.Ask($"{action.Verb} {action.Target}");
        return flights;
    }

    [Fact]
    public Task AFlightsButtonUnderTheListIsWorkingAtOnceThenSaidInThePagesBarOrRefusedInTheNodesWords() => FlightButtonAsync(FlightsViewMode.List);

    [Fact]
    public Task AFlightsButtonOnTheCanvasIsWorkingAtOnceThenSaidInThePagesBarOrRefusedInTheNodesWords() => FlightButtonAsync(FlightsViewMode.Canvas);

    private static async Task FlightButtonAsync(FlightsViewMode mode)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var node = new LateNode();
        var flights = FlightsOf(renderer, mode, node);
        var screen = Between();
        Layout.Node Tree() => flights.Tree(screen);
        flights.ListCursor.MoveTo(0);

        Click(Tree(), StartHit);
        node.Asked.ShouldBe([$"{Verbs.StartLeg} {Verbs.LegTarget("TIA1611", "render")}"]);
        // Working at once on the button, said in the bar, and no second press (of this flight's buttons) meanwhile.
        Words(Tree()).ShouldContain("Start render …");
        flights.Said.ShouldBe("Start render…");
        Has(Tree(), StartHit).ShouldBeFalse();

        // Taken: the outcome at once, in the page's bar.
        node.Answer(null);
        await Until(() => Has(Tree(), StartHit));
        flights.Said.ShouldBe("ok: Start render");

        // Refused: the node's words, in the bar and under the buttons where it was pressed.
        Click(Tree(), StartHit);
        node.Answer("TIA1611 is held for a package");
        await Until(() => Has(Tree(), StartHit));
        flights.Said.ShouldBe("refused: Start render: TIA1611 is held for a package");
        Words(Tree()).ShouldContain("TIA1611 is held for a package");
    }

    [Fact]
    public async Task TheFlightPagesPlanItAndRetryMoveAreWorkingAtOnceThenSaidOrRefusedWhereTheyWerePressed()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var node = new LateNode();
        var flights = FlightsOf(renderer, FlightsViewMode.List, node);
        // Not planned yet: Plan it.
        var unplanned = new FlightsScreen([FlightLine.Build([Flight("TIA1611")], [], Now, []).Single()], "live", Now);
        flights.Tree(unplanned);
        flights.OpenDetail("TIA1611");
        Layout.Node Tree() => flights.Tree(unplanned);

        Click(Tree(), "flight:TIA1611:plan");
        node.Asked.ShouldBe([$"{Verbs.PlanFlight} flight/TIA1611"]);
        Words(Tree()).ShouldContain("Plan it …");
        Has(Tree(), "flight:TIA1611:plan").ShouldBeFalse();
        node.Answer(null);
        await Until(() => Has(Tree(), "flight:TIA1611:plan"));
        flights.Said.ShouldBe("ok: plan TIA1611");
        Click(Tree(), "flight:TIA1611:plan");
        node.Answer("a planner is already running");
        await Until(() => Has(Tree(), "flight:TIA1611:plan"));
        Words(Tree()).ShouldContain("a planner is already running");

        // A failed move: Retry move in the red band, through the same helper.
        var failed = new FlightsScreen([FlightLine.Build([Flight("TIA1611", f => f with { MoveFailure = "DE1 refused TIA1611. Retry move." })], [], Now, []).Single()], "live", Now);
        var retry = MoveFailureBand.ActionId("TIA1611");
        Click(flights.Tree(failed), retry);
        Words(flights.Tree(failed)).ShouldContain("Retry move …");
        Has(flights.Tree(failed), retry).ShouldBeFalse();
        node.Answer("no node took it");
        await Until(() => Has(flights.Tree(failed), retry));
        Words(flights.Tree(failed)).ShouldContain("no node took it");
    }

    [Fact]
    public async Task AFlightsConnectReadsAskingUntilTheGrantAnswersAndSaysWhyWhenRefused()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var node = new LateNode();
        var flights = FlightsOf(renderer, FlightsViewMode.List, node);
        var far = new NodeMark("192.168.0.79:47731", "192.168.0.79:47731", "DE1", Here: false);
        flights.CanSteer = n => n?.Here != false;
        flights.OnConnect = n => node.Ask($"connect {n.Name}");
        var screen = new FlightsScreen([FlightLine.Build([Flight("TIA1611")], [], Now, []).Single() with { Node = far }], "live", Now);
        flights.ListCursor.MoveTo(0);

        Click(flights.Tree(screen), "flight:TIA1611:connect");
        node.Asked.ShouldBe(["connect DE1"]);
        Words(flights.Tree(screen)).ShouldContain("asking…");
        Has(flights.Tree(screen), "flight:TIA1611:connect").ShouldBeFalse();
        node.Answer(null);
        await Until(() => Has(flights.Tree(screen), "flight:TIA1611:connect"));
    }

    // ---- Home ----

    [Fact]
    public async Task HomesRetryMoveIsWorkingAtOnceThenSaidInThePagesBarOrRefusedInTheNodesWords()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var node = new LateNode();
        var said = new List<string>();
        var home = new HomeView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, OnAction = action => node.Ask($"{action.Verb}"), Say = said.Add };
        var flights = new FlightsScreen([FlightLine.Build([Flight("TIA1611", f => f with { MoveFailure = "DE1 refused TIA1611. Retry move." })], [], Now, []).Single()], "live", Now);
        var screen = new HomeScreen(flights, new SessionsScreen([], "live", Now), new JobsScreen([], "live", Now), CardsScreen.Empty);
        var retry = "home:" + MoveFailureBand.ActionId("TIA1611");
        Layout.Node Tree() => home.Tree(screen);

        Click(Tree(), retry);
        node.Asked.ShouldBe([Verbs.MoveFlight]);
        Words(Tree()).ShouldContain("Retry move …");
        Has(Tree(), retry).ShouldBeFalse();
        said.ShouldBe(["Retry move…"]);

        node.Answer(null);
        await Until(() => Has(Tree(), retry));
        said[^1].ShouldBe("ok: Retry move");

        Click(Tree(), retry);
        node.Answer("no node took TIA1611");
        await Until(() => Has(Tree(), retry));
        said[^1].ShouldBe("refused: Retry move: no node took TIA1611");
        Words(Tree()).ShouldContain("no node took TIA1611");
    }

    // ---- Sessions ----

    private static SessionLine Ended(string id, string name) => SessionLine.From(Live.Row(id, name, SessionState.Ended), Live.At);

    private static SessionLine Working(string id, string name) => SessionLine.From(Live.Row(id, name, SessionState.Working), Live.At);

    [Fact]
    public async Task SessionsArchiveAndArchiveEndedAreWorkingAtOnceThenSaidBesideTheSayBarOrRefusedInTheNodesWords()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var node = new LateNode();
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, OnAction = action => node.Ask($"{action.Verb} {action.Target}") };
        var screen = new SessionsScreen(SessionLine.Order([Ended("bbbb2222", "b-ended")]), "test", Live.At);
        view.Tree(screen);
        view.Select(0);
        Layout.Node Tree() => view.Tree(screen);

        Click(Tree(), "session:bbbb2222:archive");
        node.Asked.ShouldBe([$"{Verbs.Archive} session/bbbb2222"]);
        Words(Tree()).ShouldContain("Archive …");
        Has(Tree(), "session:bbbb2222:archive").ShouldBeFalse();
        view.SayStatus.ShouldBe("archive b-ended…");

        node.Answer(null);
        await Until(() => Has(Tree(), "session:bbbb2222:archive"));
        view.SayStatus.ShouldBe("ok: archive b-ended");

        Click(Tree(), "session:bbbb2222:archive");
        node.Answer("b-ended is not finished");
        await Until(() => Has(Tree(), "session:bbbb2222:archive"));
        view.SayStatus.ShouldBe("refused: archive b-ended: b-ended is not finished");

        // The header's Archive ended goes through the same helper.
        Click(Tree(), "sessions:archive-ended");
        Has(Tree(), "sessions:archive-ended").ShouldBeFalse();
        node.Answer(null);
        await Until(() => Has(Tree(), "sessions:archive-ended"));
        view.SayStatus.ShouldStartWith("ok: archive");
    }

    [Fact]
    public async Task SayKeepsItsTextUntilTheNodeTakesItAndSayAllIsWorkingThenSummedUp()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var node = new LateNode();
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanSay = true };
        view.OnSay = (line, text) => node.Ask($"say {line.Name}: {text}");
        view.OnSayAll = (line, text) => node.Ask($"say-all {line.Name}: {text}");
        var screen = new SessionsScreen(SessionLine.Order([Working("aaaa0001", "w-one"), Working("aaaa0002", "w-two")]), "test", Live.At);
        view.Tree(screen);
        view.Select(0);
        view.Tree(screen);
        var target = view.SelectedId!;
        var name = screen.Lines.Single(l => l.Row.Id == target).Name;

        // Enter: said "saying…" at once, the text kept, and a second Enter sends nothing while the node thinks.
        view.SayInput.Text = "hello there";
        var say = view.SayInput.OnCommit.ShouldNotBeNull()(view.SayInput.Text);
        node.Asked.ShouldBe([$"say {name}: hello there"]);
        view.SayStatus.ShouldBe($"saying to {name}…");
        view.SayInput.Text.ShouldBe("hello there");
        await view.SayInput.OnCommit(view.SayInput.Text);
        node.Asked.Count.ShouldBe(1);

        // Refused: the text stays to be sent again, and the node's words are beside the bar.
        node.Answer("the session has ended");
        await say;
        view.SayInput.Text.ShouldBe("hello there");
        view.SayStatus.ShouldBe("refused: the session has ended");

        // Taken: the bar is cleared and the outcome said at once.
        say = view.SayInput.OnCommit(view.SayInput.Text);
        node.Answer(null);
        await say;
        view.SayInput.Text.ShouldBe("");
        view.SayStatus.ShouldBe($"said to {name}");

        // Say to all shown: working at once on its pill, then the sum.
        view.SayInput.Text = "everyone";
        view.Tree(screen);
        Click(view.Tree(screen), "sessions:say-all");
        Words(view.Tree(screen)).ShouldContain("Say to all shown (2) …");
        Has(view.Tree(screen), "sessions:say-all").ShouldBeFalse();
        node.Asked.Count.ShouldBe(3);
        node.Answer(null);
        node.Answer("w-two refused");
        await Until(() => view.Presses.PendingOf("say-all") is null);
        view.SayStatus.ShouldBe("said to 1, 1 refused");
        // The text went with the one the node took.
        view.SayInput.Text.ShouldBe("");
    }

    [Fact]
    public async Task SessionsConnectReadsAskingUntilTheGrantAnswersAndSaysWhyWhenRefused()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var node = new LateNode();
        var far = new NodeMark("192.168.0.79:47731", "192.168.0.79:47731", "DE1", Here: false);
        var view = new SessionsView<RgbaImage>(renderer)
        {
            FontPath = SessionsCapture.FontPath, CanSay = true, CanSteer = n => n?.Here != false, OnConnect = n => node.Ask($"connect {n.Name}"),
        };
        var screen = new SessionsScreen(SessionLine.Order([Working("aaaa0001", "w-one") with { Node = far }]), "test", Live.At);
        view.Tree(screen);
        view.Select(0);
        var connect = $"sessions:connect:{far.Where}";

        Click(view.Tree(screen), connect);
        node.Asked.ShouldBe(["connect DE1"]);
        Words(view.Tree(screen)).ShouldContain("asking…");
        Has(view.Tree(screen), connect).ShouldBeFalse();
        node.Answer("DE1 did not allow it");
        await Until(() => Has(view.Tree(screen), connect));
    }
}
