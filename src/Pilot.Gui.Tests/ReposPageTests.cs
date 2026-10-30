using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Repos page: each tracked repo a row with its main checkout and open PRs, expanding to its worktrees, each joined with
/// its flight, its session and what waits on the controller from the feed the page already has; merged across the fleet and
/// filtered by the node selection; on pixels and in terminal cells.
/// </summary>
public sealed class ReposPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RepoStatus[] Sample() => SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath);

    private static SessionRow Session(string id, string name, string cwd, SessionState state, string? flight = null, string? waiting = null) => Live.Row(id, name, state) with
    {
        Cwd = cwd, Flight = flight, Role = flight is null ? SessionRole.AdHoc : SessionRole.Worker, WaitingFor = waiting, LastResponseAt = Live.At.AddMinutes(-3),
    };

    private static FlightRow Flight(string code) => new()
    {
        Code = code, Repo = "orbit", RepoPath = "/home/ada/src/example-org/orbit", Issue = 204, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/w", Base = "origin/main", State = FlightState.Flying, Connections = [], CreatedAt = Live.At,
    };

    private static CardRow Card(string subject, string title) => new()
    {
        Id = "c1", Bin = CardBin.Ring, Kind = "stop", Subject = subject, Title = title, At = Live.At, State = CardState.Open,
    };

    private static LegRow Leg(string flight, string name, LegState state) => new()
    {
        Flight = flight, Name = name, Order = 1, Goal = "g", DoneWhen = "d", State = state,
    };

    /// <summary>The sample's three repos over a feed with a working session in orb204's plane (and a card for it), a leg flying in orb190's, an idle session of lib117's flight.</summary>
    private static ReposScreen Screen() => new(RepoLine.Build(
        Sample(),
        [Flight("ORB204"), Flight("ORB190")],
        [
            Session("c3d4e5f6", "xy1-orb204-parser", "/home/ada/src/example-org/orbit/.claude/worktrees/orb204/src", SessionState.Working, "ORB204"),
            Session("e5f6a7b8", "xy1-lib117-bump", "/elsewhere", SessionState.Idle, "LIB117"),
            Session("0a1b2c3d", "old", "/home/ada/src/example-org/orbit/.claude/worktrees/orb190", SessionState.Ended, "ORB190"),
        ],
        [Card("flight/ORB204/parser", "parser needs a go-around decision")],
        [Leg("ORB190", "render", LegState.Flying)],
        Live.At), "live", Live.At);

    private static (PilotChrome<RgbaImage> Chrome, ReposView<RgbaImage> Repos) Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        return (chrome, chrome.Repos);
    }

    private static System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Arrange(ReposView<RgbaImage> repos, ReposScreen screen, int columns = 200) =>
        Layout.Engine.Arrange(repos.Tree(screen), new Rect<int>(0, 0, columns, 60), CellMeasureContext.PixelAuthored);

    [Fact]
    public void TheRailListsReposAfterFlightsAndItsChordKeepsTheOthersTheirKeys()
    {
        PilotChrome<RgbaImage>.Pages.Select(p => p.Page).Where(p => p != PilotPage.Chat).Take(4).ShouldBe([PilotPage.Home, PilotPage.Sessions, PilotPage.Flights, PilotPage.Repos]);
        PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Repos).Shortcut.ShouldBe(new KeyChord(InputKey.D5, InputModifier.Ctrl));
        PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Jobs).Shortcut.ShouldBe(new KeyChord(InputKey.D6, InputModifier.Ctrl));
        PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Repos).Locked.ShouldBeNull();

        using var renderer = new RgbaImageRenderer(1, 1);
        var (chrome, repos) = Chrome(renderer);
        chrome.Show(PilotPage.Repos);
        chrome.PageWidget.ShouldBeSameAs(repos);
    }

    [Fact]
    public void ARepoReadsAsItsCheckoutItsPRsAndItsWorktreesWithTheStatusesTheFeedGives()
    {
        var lines = Screen().Lines;

        // Tracked first, then by name; the untracked repo is in the list for All repos.
        lines.Select(l => l.Name).ShouldBe(["orbit", "widgets", "stargazer"]);
        lines[0].ToDumpLine().ShouldBe("orbit | main · clean · 2 behind | #205 | Parser: nested groups | ready | passing; #203 | Bump the lexer | draft | pending | 2 worktrees · 1 working");
        lines[1].Main.ShouldBe("feature/grid · dirty · 3 ahead");
        lines[2].ToDumpLine().ShouldBe("stargazer (untracked) | main · clean | no open PRs | no worktrees");

        var orb204 = lines[0].Worktrees[0];
        orb204.ToDumpLine().ShouldBe("  flight/orb204 | 7 files +412 -38 | ORB204 | working (xy1-orb204-parser: last response 3m ago) | card waiting: parser needs a go-around decision | no PR");
        orb204.CardWaiting.ShouldBeTrue();

        // An ended session is none, a leg flying is the action, and the PR is the one from its branch.
        var orb190 = lines[0].Worktrees[1];
        orb190.ToDumpLine().ShouldBe("  flight/orb190 | 2 files +20 -4 | ORB190 | none | leg render flying | PR #205");
        orb190.Session.ShouldBeNull();

        // A session of the flight, whatever its folder, is the worktree's; nothing waiting and nothing flying is nothing.
        var lib117 = lines[1].Worktrees.ShouldHaveSingleItem();
        lib117.ToDumpLine().ShouldBe("  flight/lib117 | no changes | LIB117 | idle (xy1-lib117-bump: last response 3m ago) | nothing | no PR");

        Screen().Summary.ShouldBe("2 repos · 3 worktrees · 2 open PRs");
    }

    [Fact]
    public void APullRequestRowShowsItsLabelsTheNodeLabelIncluded()
    {
        var repo = Sample()[0];
        var labelled = repo with { PullRequests = [.. repo.PullRequests.Select((p, i) => i == 0 ? p with { Labels = ["pilot node: SE1", "bug"] } : p)] };
        var screen = RepoLine.Build([labelled], [], [], [], [], Live.At);
        var line = screen[0];

        line.Pulls[0].Labels.ShouldBe("pilot node: SE1, bug");
        line.Pulls[1].Labels.ShouldBe("");
        line.ToDumpLine().ShouldContain("ready | passing | pilot node: SE1, bug;");

        using var renderer = new RgbaImageRenderer(1, 1);
        var (_, repos) = Chrome(renderer);
        var reposScreen = new ReposScreen(screen, "live", Live.At);
        var arranged = Layout.Engine.Arrange(repos.Tree(reposScreen), new Rect<int>(0, 0, 1280, 600), CellMeasureContext.PixelAuthored);
        arranged.ShouldNotBeEmpty();
    }

    [Fact]
    public void AWaitingSessionSaysWhatItWaitsOnAndTheWorkingOneWinsOverAnIdleOne()
    {
        var repo = Sample()[0];
        var screen = RepoLine.Build([repo], [], [
            Session("aaaa0001", "idle-one", "/home/ada/src/example-org/orbit/.claude/worktrees/orb204", SessionState.Idle),
            Session("bbbb0002", "asks", "/home/ada/src/example-org/orbit/.claude/worktrees/orb204", SessionState.Waiting, waiting: "permission to run: dotnet test"),
        ], [], [], Live.At);

        var worktree = screen[0].Worktrees[0];
        worktree.Activity.ShouldBe("waiting");
        worktree.LastMessage.ShouldBe("permission to run: dotnet test");
        worktree.Session!.Name.ShouldBe("asks");
    }

    [Fact]
    public void RepoRowsExpandToTheirWorktreesWithRightAndLeftAndAClick()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, repos) = Chrome(vessel);
        var screen = Screen();

        var collapsed = CellLayout.Describe(Arrange(repos, screen));
        collapsed.ShouldContain("orbit");
        collapsed.ShouldContain("#205 Parser: nested groups");
        collapsed.ShouldContain("main · clean · 2 behind");
        collapsed.ShouldContain("2 worktrees · 1 working");
        collapsed.ShouldNotContain("flight/orb204");
        collapsed.ShouldNotContain("stargazer");
        repos.Selected.ShouldBe(0);

        repos.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.None)).ShouldBeTrue();
        var expanded = CellLayout.Describe(Arrange(repos, screen));
        expanded.ShouldContain("↳ flight/orb204");
        expanded.ShouldContain("7 files +412 -38");
        expanded.ShouldContain("xy1-orb204-parser");
        expanded.ShouldContain("card waiting: parser needs a go-around decision");
        expanded.ShouldContain("leg render flying");
        expanded.ShouldContain("✈ ORB204");

        repos.HandleInput(new InputEvent.KeyDown(InputKey.Left, InputModifier.None)).ShouldBeTrue();
        CellLayout.Describe(Arrange(repos, screen)).ShouldNotContain("flight/orb204");

        // A click on the repo's triangle does the same.
        var arranged = Arrange(repos, screen);
        var toggle = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action.EndsWith(":worktrees", StringComparison.Ordinal));
        CellLayout.HitTest(arranged, toggle.Bounds.X, toggle.Bounds.Y);
        CellLayout.Describe(Arrange(repos, screen)).ShouldContain("↳ flight/orb204");

        // A click anywhere on the repo's row does it too: here it folds the worktrees away again.
        arranged = Arrange(repos, screen);
        var first = arranged.First(a => a.Node.Hit is HitResult.ListItemHit { Index: 0 });
        CellLayout.HitTest(arranged, first.Bounds.X + 1, first.Bounds.Y);
        CellLayout.Describe(Arrange(repos, screen)).ShouldNotContain("flight/orb204");
        repos.Selected.ShouldBe(0);

        // A click on the next repo's row selects it.
        arranged = Arrange(repos, screen);
        var second = arranged.First(a => a.Node.Hit is HitResult.ListItemHit { Index: 1 });
        CellLayout.HitTest(arranged, second.Bounds.X + 1, second.Bounds.Y);
        repos.Selected.ShouldBe(1);
    }

    [Fact]
    public void TheHeadersButtonsAreAllAsTallAsItsQuestionMark()
    {
        using var renderer = new RgbaImageRenderer(2000, 800);
        var (chrome, repos) = Chrome(renderer);
        repos.SyncRepos = _ => Task.FromResult<RepoSyncOutcome[]>([]);
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);

        var arranged = Layout.Engine.Arrange(repos.Tree(Screen()), new Rect<float>(0, 0, 2000, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        Rect<float> Of(string action) => arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action).Bounds;

        // Sync, All repos and Autopilot each stood their own height beside the ? (SE1, 2026-10-25).
        var help = Of("about:toggle");
        foreach (var action in new[] { "repos:sync", "repos:all", AutopilotSwitch.Action })
        {
            (Of(action).Y, Of(action).Height).ShouldBe((help.Y, help.Height), action);
        }
    }

    [Fact]
    public void AllReposAddsTheUntrackedOnes()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, repos) = Chrome(vessel);
        var screen = Screen();

        var arranged = Arrange(repos, screen);
        CellLayout.Describe(arranged).ShouldNotContain("stargazer");
        var pill = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "repos:all" });
        CellLayout.HitTest(arranged, pill.Bounds.X + 1, pill.Bounds.Y);

        repos.ShowAll.ShouldBeTrue();
        CellLayout.Describe(Arrange(repos, screen)).ShouldContain("stargazer");
        repos.Lines.Select(l => l.Name).ShouldBe(["orbit", "widgets", "stargazer"]);
    }

    [Fact]
    public void TheFlightSessionAndPRLinksOpenTheirTargets()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, repos) = Chrome(renderer);
        chrome.LinkRepos();
        var opened = new List<string>();
        repos.OnOpen = opened.Add;
        var screen = Screen();
        chrome.Show(PilotPage.Repos);
        Arrange(repos, screen);
        repos.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.None));

        void Click(Func<HitResult.ButtonHit, bool> which)
        {
            var arranged = Arrange(repos, screen);
            var node = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && which(b));
            CellLayout.HitTest(arranged, node.Bounds.X, node.Bounds.Y);
        }

        // A PR, in the repo's row and in the worktree's, opens its URL.
        Click(b => b.Action == "pr:205");
        opened.ShouldBe(["https://github.com/example-org/orbit/pull/205"]);
        Click(b => b.Action.EndsWith(":pr:205", StringComparison.Ordinal));
        opened.Count.ShouldBe(2);

        // The session opens its transcript on the Sessions page.
        Click(b => b.Action.EndsWith(":session:c3d4e5f6", StringComparison.Ordinal));
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.RowId.ShouldBe("c3d4e5f6");

        // The flight opens its page on the Flights page.
        chrome.Show(PilotPage.Repos);
        Click(b => b.Action.EndsWith(":flight:ORB204", StringComparison.Ordinal));
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.IsDetailOpen.ShouldBeTrue();
    }

    [Fact]
    public void ANarrowTerminalDropsTheOptionalColumnsAndKeepsTheRepoAndItsCheckout()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, repos) = Chrome(vessel);

        var narrow = CellLayout.Describe(Arrange(repos, Screen(), columns: 60));

        narrow.ShouldContain("orbit");
        narrow.ShouldContain("main · clean · 2 behind");
        narrow.ShouldNotContain("#205 Parser: nested groups");
        narrow.ShouldNotContain("2 worktrees · 1 working");
    }

    [Fact]
    public void TheWindowPaintsTheRowsAndTheirLinks()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, repos) = Chrome(renderer);
        chrome.Show(PilotPage.Repos);
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, null, new RectF32(0, 0, 1280, 800));
        var empty = renderer.Surface.Pixels.ToArray();
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, Screen(), new RectF32(0, 0, 1280, 800));
        repos.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.None)).ShouldBeTrue();

        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, Screen(), new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        repos.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain("repos:all");
        buttons.ShouldContain("pr:205");
        buttons.ShouldContain(b => b.EndsWith(":flight:ORB204", StringComparison.Ordinal));
        buttons.ShouldContain(b => b.EndsWith(":session:c3d4e5f6", StringComparison.Ordinal));
        regions.Select(r => r.Result).OfType<HitResult.ListItemHit>().Count(h => h.ListId == ReposView<RgbaImage>.ListId).ShouldBe(2);
        renderer.Surface.Pixels.ToArray().ShouldNotBe(empty);
    }

    [Fact]
    public async Task AFinishedSyncReadsTheReposAgainAtOnceAndTheRowsShowTheNewStatus()
    {
        var node = new FakeNode();
        var reads = 0;
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20), repoLoader: _ =>
        {
            // The first read sees every repo; after the sync the node reports one fewer.
            var n = Interlocked.Increment(ref reads);
            return Task.FromResult<RepoStatus[]?>(n == 1 ? Sample() : Sample().Take(2).ToArray());
        });
        feed.Start();
        await Until(() => node.OpenCount > 0);
        node.Send(Live.Snapshot(1, "s-se1"));
        await Until(() => feed.Connected);
        var start = DateTimeOffset.UtcNow;

        feed.Repos(start, wanted: true);
        await Until(() => feed.Repos(start, wanted: true).Lines.Count == 3);
        reads.ShouldBe(1);

        using var vessel = new RgbaImageRenderer(1, 1);
        var (_, repos) = Chrome(vessel);
        repos.SyncRepos = _ =>
        {
            feed.RefreshRepos();
            return Task.FromResult<RepoSyncOutcome[]>([]);
        };
        await repos.SyncAsync();

        // Same instant, well inside the cadence: it reads again anyway.
        await Until(() => feed.Repos(start, wanted: true).Lines.Count == 2);
        reads.ShouldBe(2);
    }

    [Fact]
    public async Task ReposAreReadFromTheNodeOnlyWhileThePageIsWantedAndKeptForAWhile()
    {
        var node = new FakeNode();
        var reads = 0;
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20), repoLoader: _ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<RepoStatus[]?>(Sample());
        });
        feed.Start();
        await Until(() => node.OpenCount > 0);
        node.Send(Live.Snapshot(1, "s-se1"));
        await Until(() => feed.Connected);
        var start = DateTimeOffset.UtcNow;

        // Not wanted: nothing is read, however often it is looked at.
        feed.Repos(start, wanted: false).Lines.ShouldBeEmpty();
        reads.ShouldBe(0);

        // Wanted: read once, and the rows appear.
        feed.Repos(start, wanted: true);
        await Until(() => feed.Repos(start, wanted: true).Lines.Count == 3);
        reads.ShouldBe(1);

        // Looked at again within the cadence: not read again. After it, and for a minute after the page was left: read again.
        feed.Repos(start.AddSeconds(5), wanted: true);
        reads.ShouldBe(1);
        feed.Repos(start.AddSeconds(20), wanted: false);
        await Until(() => reads == 2);
        reads.ShouldBe(2);

        // Long after the page was left: left alone.
        feed.Repos(start.AddSeconds(200), wanted: false);
        await Task.Delay(100, Ct);
        reads.ShouldBe(2);
    }

    [Fact]
    public async Task TheFleetsReposMergeMarkedWithTheirNodeAndFilterLikeTheOtherPages()
    {
        const string lan = "10.0.0.9:47731";
        var se1 = new FakeNode();
        var le1 = new FakeNode();
        var here = new NodeFeed(se1.Open, "node.sock", TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("SE1", "s-se1")),
            repoLoader: _ => Task.FromResult<RepoStatus[]?>([Sample()[0]]));
        await using var watch = new FleetWatch(endpoint => new NodeFeed(le1.Open, endpoint, TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("LE1", "s-le1")),
            repoLoader: _ => Task.FromResult<RepoStatus[]?>([Sample()[0] with { Name = "orbit-laptop", Path = "/l/orbit" }])));
        await using var hereOwner = here;
        here.Start();
        watch.Know([(lan, "LE1 LAPTOP-3")]);
        await Until(() => se1.OpenCount > 0 && le1.OpenCount > 0);
        se1.Send(Live.Snapshot(1, "s-se1"));
        le1.Send(Live.Snapshot(1, "s-le1"));
        using var vessel = new RgbaImageRenderer(1, 1);
        var (chrome, repos) = Chrome(vessel);
        var filter = chrome.Nodes;
        var now = DateTimeOffset.UtcNow;

        FleetPages pages = watch.Pages("node.sock", here, filter, now, reposWanted: true);
        await Until(() => (pages = watch.Pages("node.sock", here, filter, now, reposWanted: true)).Repos!.Lines.Count == 2);

        pages.Repos!.Lines.Select(l => (l.Node!.Name, l.Name)).ShouldBe([("SE1", "orbit"), ("LE1", "orbit-laptop")]);
        pages.Repos.Lines[1].Worktrees.ShouldAllBe(w => w.Node!.Name == "LE1");

        // The terminal shows the Node column, and the header's pill picks one node's repos.
        var arranged = Layout.Engine.Arrange(repos.Tree(pages.Repos), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        CellLayout.Describe(arranged).ShouldContain("Node");

        // New flight flies this window's own node: only the local row has it, a remote row offers none.
        repos.OnNewFlight = _ => { };
        var hits = Arrange(repos, pages.Repos).Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        hits.ShouldContain($"repo:{pages.Repos.Lines[0].Key}:new-flight");
        hits.ShouldNotContain($"repo:{pages.Repos.Lines[1].Key}:new-flight");
        filter.Line().ShouldBe("[All nodes] SE1 LE1");
        filter.Toggle(filter.Choices.Single(c => c.Name == "LE1").Key);
        watch.Pages("node.sock", here, filter, now, reposWanted: true).Repos!.Lines.ShouldHaveSingleItem().Name.ShouldBe("orbit-laptop");
    }

    private static void ClickCell(ReposView<RgbaImage> repos, ReposScreen screen, Func<HitResult.ButtonHit, bool> which)
    {
        var arranged = Arrange(repos, screen);
        var node = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && which(b));
        CellLayout.HitTest(arranged, node.Bounds.X, node.Bounds.Y);
    }

    [Fact]
    public async Task SyncShowsItRunningThenEachReposOutcomeAndAnythingTheNodeRefuses()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (_, repos) = Chrome(renderer);
        var screen = Screen();
        CellLayout.Describe(Arrange(repos, screen)).ShouldNotContain("Sync");

        var running = new TaskCompletionSource<RepoSyncOutcome[]>();
        var redraws = 0;
        repos.SyncRepos = _ => running.Task;
        repos.Changed = () => redraws++;
        CellLayout.Describe(Arrange(repos, screen)).ShouldContain("Sync");

        ClickCell(repos, screen, b => b.Action == "repos:sync");
        repos.Syncing.ShouldBeTrue();
        CellLayout.Describe(Arrange(repos, screen)).ShouldContain("Syncing…");
        // A second press while it runs is not there to make: the button has no hit.
        Arrange(repos, screen).Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().ShouldNotContain(b => b.Action == "repos:sync");

        running.SetResult([
            new RepoSyncOutcome { Name = "orbit", Status = RepoSyncStatus.Updated, Detail = "main took 2 commits from origin/main" },
            new RepoSyncOutcome { Name = "widgets", Status = RepoSyncStatus.Skipped, Detail = "the main checkout is on feature/grid, not main" },
        ]);
        await Until(() => !repos.Syncing && redraws == 2);
        var text = CellLayout.Describe(Arrange(repos, screen));
        text.ShouldContain("sync: 1 skipped, 1 updated");
        text.ShouldContain("orbit  updated  main took 2 commits from origin/main");
        text.ShouldContain("widgets  skipped  the main checkout is on feature/grid, not main");

        ClickCell(repos, screen, b => b.Action == "repos:sync-clear");
        CellLayout.Describe(Arrange(repos, screen)).ShouldNotContain("sync:");

        repos.SyncRepos = _ => Task.FromException<RepoSyncOutcome[]>(new InvalidOperationException("watchtower: not yours"));
        await repos.SyncAsync();
        repos.SyncError.ShouldBe("watchtower: not yours");
        CellLayout.Describe(Arrange(repos, screen)).ShouldContain("sync refused: watchtower: not yours");
    }

    [Fact]
    public async Task NewFlightOnARowOpensTheWizardOnThatReposIssuesAndANewIssueIsCreatedAndPicked()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (_, repos) = Chrome(renderer);
        var screen = Screen();
        var orbit = Sample()[0];
        var wizard = new NewFlightWizard
        {
            LoadRepos = _ => Task.FromResult<RepoInfo[]>([
                new() { Name = "widgets", Path = "/home/ada/src/example-org/widgets" },
                new() { Name = orbit.Name, Path = orbit.Path, GitHub = orbit.GitHub }]),
            LoadIssues = (_, _) => Task.FromResult<IssueInfo[]>([new() { Number = 204, Title = "Parser" }]),
        };
        RepoLine? asked = null;
        repos.OnNewFlight = line =>
        {
            asked = line;
            wizard.Open(line.Status.Path, atIssues: true);
        };

        ClickCell(repos, screen, b => b.Action == $"repo:{screen.Lines[0].Key}:new-flight");

        asked.ShouldNotBeNull().Name.ShouldBe("orbit");
        // Straight to that repo's issues: no repos step.
        wizard.IsOpen.ShouldBeTrue();
        wizard.Current.ShouldBe(NewFlightWizard.Step.Issues);
        wizard.Chosen.Select(r => r.Name).ShouldBe(["orbit"]);
        wizard.IssueResults.Select(i => i.Number).ShouldBe([204]);

        // No New issue without the node's way to make one; with it, a form that Escape closes first.
        wizard.BeginNewIssue();
        wizard.NewIssueOpen.ShouldBeFalse();
        var made = new List<(string Path, string Title, string? Body)>();
        string? refuse = "gh is not signed in";
        wizard.CreateIssue = (path, title, body) =>
        {
            made.Add((path, title, body));
            return refuse is { } why ? Task.FromException<IssueInfo>(new InvalidOperationException(why)) : Task.FromResult(new IssueInfo { Number = 300, Title = title });
        };
        wizard.BeginNewIssue();
        wizard.NewIssueOpen.ShouldBeTrue();
        wizard.ActiveSearch.ShouldBeNull();
        wizard.Back();
        wizard.NewIssueOpen.ShouldBeFalse();
        wizard.Current.ShouldBe(NewFlightWizard.Step.Issues);

        // A title is needed; a refusal keeps the form and says why.
        wizard.BeginNewIssue();
        await wizard.CreateIssueAsync();
        wizard.IssueStatus.ShouldBe("failed: an issue needs a title");
        wizard.IssueTitle.Text = "Rings";
        wizard.IssueBody.Text = "  around Saturn ";
        await wizard.CreateIssueAsync();
        wizard.IssueStatus.ShouldBe("failed: gh is not signed in");
        wizard.NewIssueOpen.ShouldBeTrue();

        // Created: picked as the repo's issue, so the review is next with it.
        refuse = null;
        await wizard.CreateIssueAsync();
        made.Last().ShouldBe((orbit.Path, "Rings", "around Saturn"));
        wizard.NewIssueOpen.ShouldBeFalse();
        wizard.Current.ShouldBe(NewFlightWizard.Step.Review);
        var pick = wizard.Picks.ShouldHaveSingleItem();
        (pick.Repo.Name, pick.Issue.Number, pick.Issue.Title).ShouldBe(("orbit", 300, "Rings"));

        // Back to the issues: the new issue is listed with the others.
        wizard.Back();
        wizard.IssueResults.Select(i => i.Number).ShouldBe([300, 204]);
    }

    private static NodeInfo Info(string prefix, string stream) => new()
    {
        WireVersion = 1, Version = "2.0.9", MachineName = prefix == "SE1" ? "DESKTOP-7" : "LAPTOP-3", MachinePrefix = prefix, ProcessId = 1, StartedAt = Live.At,
        DataFolder = "/d", SocketPath = "/s", Stream = stream,
    };

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }
}
