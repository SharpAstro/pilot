using System.Threading.Channels;
using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Rows and events for the live tests.</summary>
internal static class Live
{
    public static readonly DateTimeOffset At = new(2026, 10, 19, 1, 0, 0, TimeSpan.Zero);

    public static SessionRow Row(string id, string name, SessionState state = SessionState.Idle) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = "C:\\work\\pilot",
        Role = SessionRole.AdHoc,
        ContextTokens = 1000,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 2000,
        StartedAt = At,
    };

    public static SnapshotEvent Snapshot(long seq, string stream, params SessionRow[] rows) =>
        new() { Seq = seq, At = At, Stream = stream, Sessions = rows, Notes = [] };

    public static SessionChangedEvent Changed(long seq, SessionRow row) => new() { Seq = seq, At = At, Session = row };

    public static SessionAddedEvent Added(long seq, SessionRow row) => new() { Seq = seq, At = At, Session = row };
}

/// <summary>A node's event stream the test writes into, and every open the feed asked for.</summary>
internal sealed class FakeNode
{
    private readonly Lock _lock = new();
    private Channel<NodeEvent?>? _current;

    public List<(long? Since, string? Stream)> Opens { get; } = [];

    /// <summary>When set, an open fails with this, as when no node listens.</summary>
    public Exception? Refuse { get; set; }

    public Task<INodeEvents> Open(long? since, string? stream, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Opens.Add((since, stream));
            if (Refuse is { } refuse)
            {
                return Task.FromException<INodeEvents>(refuse);
            }

            _current = Channel.CreateUnbounded<NodeEvent?>();
            return Task.FromResult<INodeEvents>(new Events(_current.Reader));
        }
    }

    public int OpenCount
    {
        get
        {
            lock (_lock)
            {
                return Opens.Count;
            }
        }
    }

    public void Send(NodeEvent nodeEvent)
    {
        lock (_lock)
        {
            _current!.Writer.TryWrite(nodeEvent);
        }
    }

    /// <summary>Closes the open stream, as a node does that goes away or drops a slow client.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _current!.Writer.TryWrite(null);
        }
    }

    private sealed class Events(ChannelReader<NodeEvent?> reader) : INodeEvents
    {
        public async ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken) => await reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class NodeFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task TheRowsFollowTheSnapshotAndItsDeltas()
    {
        var node = new FakeNode();
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();
        await Until(() => node.OpenCount == 1);

        node.Send(Live.Snapshot(4, "s1", Live.Row("aaaa1111", "one")));
        node.Send(Live.Added(5, Live.Row("bbbb2222", "two", SessionState.Working)));
        node.Send(Live.Changed(6, Live.Row("aaaa1111", "one", SessionState.Ended)));
        await Until(() => feed.Screen(Live.At).Lines.Count == 2 && feed.Screen(Live.At).Lines[1].Row.State == SessionState.Ended);

        var screen = feed.Screen(Live.At);
        feed.Connected.ShouldBeTrue();
        screen.Source.ShouldBe("live from the node on node.sock");
        // Live sessions first: the working one, then the one that ended.
        screen.Lines.Select(l => l.Name).ShouldBe(["two", "one"]);
    }

    [Fact]
    public async Task AWindowOlderThanItsNodeSaysToReopen()
    {
        var node = new FakeNode();
        var nodeVersion = NodeFeed.ClientVersion;
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20), _ => Task.FromResult<string?>(nodeVersion));
        feed.Start();
        await Until(() => node.OpenCount == 1);
        node.Send(Live.Snapshot(4, "s1", Live.Row("aaaa1111", "one")));
        await Until(() => feed.Connected);
        feed.Screen(Live.At).Source.ShouldBe("live from the node on node.sock");

        // The node updated itself and restarted: the window reconnects, and asks again.
        nodeVersion = "99.0.0+abc";
        node.Close();
        await Until(() => node.OpenCount == 2);
        node.Send(Live.Snapshot(5, "s1", Live.Row("aaaa1111", "one")));
        await Until(() => feed.UpdateNote.Length > 0 && feed.Screen(Live.At).Source.StartsWith("live", StringComparison.Ordinal));

        feed.Screen(Live.At).Source.ShouldBe($"live from the node on node.sock · the node is 99.0.0, this window {NodeFeed.ClientVersion}: reopen to update");
    }

    [Fact]
    public async Task ADroppedStreamResumesAfterTheLastEventApplied()
    {
        var node = new FakeNode();
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();
        await Until(() => node.OpenCount == 1);
        node.Send(Live.Snapshot(9, "s1", Live.Row("aaaa1111", "one")));
        node.Send(Live.Added(10, Live.Row("bbbb2222", "two")));
        await Until(() => feed.Screen(Live.At).Lines.Count == 2);

        node.Close();
        await Until(() => node.OpenCount == 2);

        node.Opens[0].ShouldBe((null, null));
        node.Opens[1].ShouldBe((10L, "s1"));
        // The rows stay on screen while it reconnects.
        feed.Screen(Live.At).Lines.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ADeltaOutOfOrderStartsAgainFromASnapshot()
    {
        var node = new FakeNode();
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();
        await Until(() => node.OpenCount == 1);
        node.Send(Live.Snapshot(1, "s1", Live.Row("aaaa1111", "one")));
        node.Send(Live.Added(3, Live.Row("bbbb2222", "two")));

        await Until(() => node.OpenCount == 2);

        node.Opens[1].ShouldBe((null, null));
    }

    [Fact]
    public async Task NoNodeSaysSoAndKeepsTrying()
    {
        var node = new FakeNode { Refuse = new HttpRequestException("refused") };
        await using var feed = new NodeFeed(node.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        feed.Start();

        await Until(() => node.OpenCount >= 3);

        feed.Connected.ShouldBeFalse();
        feed.Screen(Live.At).Source.ShouldStartWith("waiting for the node on node.sock");

        // When the GUI could not start one, the header says why.
        feed.Problem = "pilot-node.exe is not beside pilot-gui";
        var opens = node.OpenCount;
        await Until(() => node.OpenCount > opens + 1);
        feed.Screen(Live.At).Source.ShouldStartWith("no node on node.sock: pilot-node.exe is not beside pilot-gui");
    }
}

public sealed class GuiSayTests
{
    private static readonly SessionRow[] Rows =
    [
        Live.Row("aaaa1111", "pil5-say-int"),
        Live.Row("bbbb2222", "twin"),
        Live.Row("bbbb3333", "twin"),
    ];

    [Theory]
    [InlineData("aaaa1111", "aaaa1111")]
    [InlineData("aaaa1111-0000-4000-8000-000000000000", "aaaa1111")]
    [InlineData("AAAA", "aaaa1111")]
    [InlineData("pil5-say-int", "aaaa1111")]
    public void ASessionIsFoundByIdSessionIdOrName(string to, string id) =>
        GuiSay.Find(Rows, to, out _).ShouldNotBeNull().Id.ShouldBe(id);

    [Fact]
    public void ANameOrStartTwoSessionsShareIsRefused()
    {
        GuiSay.Find(Rows, "twin", out var why).ShouldBeNull();
        why.ShouldBe("2 sessions match 'twin': twin (bbbb2222), twin (bbbb3333)");
        GuiSay.Find(Rows, "bbbb", out why).ShouldBeNull();
        why.ShouldNotBeNull().ShouldStartWith("2 session ids match 'bbbb'");
        GuiSay.Find(Rows, "nobody", out why).ShouldBeNull();
        why.ShouldBe("no session matches 'nobody'");
    }

    [Fact]
    public void AResultReadsAsWhatBecameOfIt()
    {
        CommandResult Result(bool ok, string? way = null, string? error = null, string? sid = null) =>
            new() { Verb = Verbs.Say, Target = "session/x", Who = GuiSay.Who, Seq = 1, Ok = ok, Way = way, Error = error, SessionId = sid };

        GuiSay.Describe(Result(true, SayWays.Resumed, sid: "201dda08-9080"), null).ShouldBe("resumed it with your text (now 201dda08)");
        GuiSay.Describe(Result(true, SayWays.Relayed), null).ShouldBe("delivered as a message; its answer shows in its own turn");
        GuiSay.Describe(Result(false, error: "twin is ambiguous"), "twin is ambiguous").ShouldBe("refused: twin is ambiguous");
        GuiSay.Describe(null, "").ShouldBe("refused: the node gave no reason");
    }
}

public sealed class GuiOptionsTests
{
    [Fact]
    public void SayTakesTheSessionAndEverythingAfterAsTheText()
    {
        var options = GuiOptions.Parse(["--node-socket", "x.sock", "--say", "pil5-say-int", "Reply", "with", "ok"], out var error).ShouldNotBeNull(error);

        options.NodeSocket.ShouldBe("x.sock");
        options.SayTo.ShouldBe("pil5-say-int");
        options.SayText.ShouldBe("Reply with ok");
    }

    [Fact]
    public void ASnapshotTakesNoNode()
    {
        GuiOptions.Parse(["--snapshot", "s.json", "--node-socket", "x.sock"], out var error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldStartWith("--snapshot shows a file");
        GuiOptions.Parse(["--say", "only-a-session"], out error).ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("'--say'");
    }
}

public sealed class SayBarTests
{
    private static SessionsScreen Screen(params SessionRow[] rows) => new(SessionLine.Build(rows, Live.At), "test", Live.At);

    [Fact]
    public void ArchivedSessionsAreLeftOutUntilShownAndTheButtonsArchiveThroughTheNode()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var acted = new List<FlightAction>();
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, OnAction = acted.Add };
        var live = Live.Row("aaaa1111", "live", SessionState.Working);
        var ended = Live.Row("bbbb2222", "ended", SessionState.Ended);
        var archived = Live.Row("cccc3333", "archived", SessionState.Ended) with { Archived = true };
        var bounds = new RectF32(0, 0, 1280, 800);

        view.Render(Screen(live, ended, archived), bounds);
        view.Select(Screen(live, ended).Lines.ToList().FindIndex(l => l.Row.Id == "bbbb2222"));
        view.Render(Screen(live, ended, archived), bounds);

        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        Hit(regions, "sessions:archive-ended");
        Hit(regions, "session:bbbb2222:archive");
        acted.Select(a => (a.Verb, a.Target, a.Args.Length)).ShouldBe([(Verbs.Archive, Verbs.SessionsTarget, 0), (Verbs.Archive, "session/bbbb2222", 0)]);

        // The archived one is left out until the header's toggle shows it.
        view.SelectedId.ShouldBe("bbbb2222");
        Hit(regions, "sessions:show-archived");
        view.ShowArchived.ShouldBeTrue();
        view.Render(Screen(live, ended, archived), bounds);
        view.Select(Screen(live, ended, archived).Lines.ToList().FindIndex(l => l.Row.Id == "cccc3333"));
        view.Render(Screen(live, ended, archived), bounds);
        regions.Clear();
        view.CollectPaintedRegions(regions);
        Hit(regions, "session:cccc3333:restore");
        acted[^1].Args.ShouldBe(["undo"]);
    }

    [Fact]
    public void ARightClickOpensTheRowsMenuWideEnoughToReadAndItsEntriesAct()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var acted = new List<FlightAction>();
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, OnAction = acted.Add, CanSay = true };
        var ended = Live.Row("bbbb2222", "ended", SessionState.Ended);
        var bounds = new RectF32(0, 0, 1280, 800);
        view.Render(Screen(ended), bounds);

        view.OpenMenu(Screen(ended).Lines[0], 0, new PointerPress(400f, 200f, MouseButton.Right, InputModifier.None, 1));
        view.Render(Screen(ended), bounds);

        view.MenuItems.Select(i => i.Label).ShouldContain("Archive");
        var rows = new List<ClickableRegion>();
        view.CollectPaintedRegions(rows);
        var menu = rows.Where(r => r.Result is HitResult.ListItemHit { ListId: DropdownMenuState<string>.ListId }).ToList();
        menu.Count.ShouldBe(view.MenuItems.Count);
        // As wide as its widest entry ("Say to ended…" and the rest), every row the same, and on screen.
        menu.Select(r => r.Width).Distinct().ShouldHaveSingleItem().ShouldBeGreaterThan(80f);
        menu.ShouldAllBe(r => r.X >= 0f && r.X + r.Width <= bounds.Width);
        menu[view.MenuItems.ToList().FindIndex(i => i.Label == "Archive")].OnClick!.Invoke(InputModifier.None);
        acted.ShouldHaveSingleItem().Target.ShouldBe("session/bbbb2222");
    }

    private static void Hit(List<ClickableRegion> regions, string action)
    {
        var at = regions.FindIndex(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
        at.ShouldBeGreaterThanOrEqualTo(0, $"no {action} among {string.Join(", ", regions.Select(r => r.Result))}");
        regions[at].OnClick!.Invoke(InputModifier.None);
    }

    [Fact]
    public void TheSelectionFollowsItsSessionWhenTheRowsReorder()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanSay = true };
        var one = Live.Row("aaaa1111", "one");
        var two = Live.Row("bbbb2222", "two");
        view.Render(Screen(one, two), new RectF32(0, 0, 1280, 800));
        view.Select(1);
        view.SelectedId.ShouldBe("bbbb2222");

        // "one" starts working, so it moves above "two": the selection stays on "two", now the second row still,
        // and when a third session comes in ahead of both it moves with "two" to the third place.
        view.Render(Screen(one with { State = SessionState.Working }, two), new RectF32(0, 0, 1280, 800));
        view.SelectedId.ShouldBe("bbbb2222");
        view.Render(Screen(one with { State = SessionState.Working }, two, Live.Row("cccc3333", "three", SessionState.Waiting)), new RectF32(0, 0, 1280, 800));
        view.SelectedId.ShouldBe("bbbb2222");
        view.Selected.ShouldBe(2);
        view.SayInput.Placeholder.ShouldBe("Say to two · Enter sends");
    }

    [Fact]
    public void TheFirstSessionStaysSelectedAsTheLiveRowsChange()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanSay = true };
        var bounds = new RectF32(0, 0, 1280, 800);
        var a = Live.Row("aaaa1111", "pilot-43", SessionState.Working);
        var b = Live.Row("bbbb2222", "ytplayer-d1", SessionState.Working);

        // The window's first frames: no node yet, then its snapshot, then one session goes idle, frame after frame.
        view.Render(Screen(), bounds);
        view.Render(Screen(a, b), bounds);
        view.SelectedId.ShouldBe("aaaa1111");
        for (var i = 0; i < 5; i++)
        {
            view.Render(Screen(a, b with { State = SessionState.Idle }), bounds);
            view.SelectedId.ShouldBe("aaaa1111");
        }
    }

    [Fact]
    public async Task EnterSaysTheTextToTheSelectedSessionAndClearsTheBar()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanSay = true };
        var said = new List<(string Id, string Text)>();
        view.OnSay = (line, text) =>
        {
            said.Add((line.Row.Id, text));
            return Task.CompletedTask;
        };
        view.Render(Screen(Live.Row("aaaa1111", "one"), Live.Row("bbbb2222", "two")), new RectF32(0, 0, 1280, 800));
        view.Select(1);
        view.Render(Screen(Live.Row("aaaa1111", "one"), Live.Row("bbbb2222", "two")), new RectF32(0, 0, 1280, 800));

        view.SayInput.Text = "  hello there ";
        await view.SayInput.OnCommit.ShouldNotBeNull()(view.SayInput.Text);

        said.ShouldBe([("bbbb2222", "hello there")]);
        view.SayInput.Text.ShouldBe("");
    }

    [Fact]
    public void CtrlSTakesTheKeyboardToTheSayBar()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanSay = true };
        var router = new InputRouter(view.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => [view], Unhandled = view.HandleInput };
        view.Render(Screen(Live.Row("aaaa1111", "one")), new RectF32(0, 0, 1280, 800));
        view.Ui.Focus.Current.ShouldBeNull();

        router.Handle(new InputEvent.KeyDown(InputKey.S, InputModifier.Ctrl)).ShouldBeTrue();

        view.Ui.Focus.Current.ShouldBe(view.SayInput);
    }

    [Fact]
    public void TheSayBarShowsOnlyWithANode()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        view.Render(Screen(Live.Row("aaaa1111", "one")), new RectF32(0, 0, 1280, 800));
        view.GetRegisteredTextInputs().ShouldBeEmpty();

        view.CanSay = true;
        view.Render(Screen(Live.Row("aaaa1111", "one")), new RectF32(0, 0, 1280, 800));
        view.GetRegisteredTextInputs().ShouldBe([view.SayInput]);
    }
}

public sealed class NodeLauncherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANamedSocketWithNoNodeGetsNoneStarted()
    {
        var socket = Path.Combine(Path.GetTempPath(), "p2l-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        var (outcome, message) = await NodeLauncher.FindOrStartAsync(socket, named: true, Ct);

        outcome.ShouldBe(NodeLaunch.NamedSocketSilent);
        message.ShouldContain("which was named, so none is started there");
    }

    [Fact]
    public void TheNodeIsFoundBesideTheGuiOrInTheSourceTreesBuildOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2l-" + Guid.NewGuid().ToString("N")[..8]);
        var exe = OperatingSystem.IsWindows() ? "pilot-node.exe" : "pilot-node";
        var gui = Directory.CreateDirectory(Path.Combine(root, "src", "Pilot.Gui", "bin", "Release", "net10.0")).FullName;
        var node = Directory.CreateDirectory(Path.Combine(root, "src", "Pilot.Node", "bin", "Release", "net10.0")).FullName;
        try
        {
            NodeLauncher.FindNodeBinary(gui).ShouldBeNull();

            File.WriteAllText(Path.Combine(node, exe), "");
            NodeLauncher.FindNodeBinary(gui + Path.DirectorySeparatorChar).ShouldBe(Path.Combine(node, exe));

            // A publish puts them side by side, and that one wins.
            File.WriteAllText(Path.Combine(gui, exe), "");
            NodeLauncher.FindNodeBinary(gui).ShouldBe(Path.Combine(gui, exe));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class RailTests
{
    [Fact]
    public void TheRailHasEveryPageAndNoneIsLocked()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer);
        var chrome = new PilotChrome<RgbaImage>(renderer, view) { FontPath = SessionsCapture.FontPath };
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 800));

        PilotChrome<RgbaImage>.Pages.Select(p => p.Label).ShouldBe(["Home", "Sessions", "Flights", "Repos", "Background tasks", "Fleet", "Settings"]);
        PilotChrome<RgbaImage>.Pages.Where(p => p.Locked is null).Select(p => p.Page).ShouldBe([PilotPage.Home, PilotPage.Sessions, PilotPage.Flights, PilotPage.Repos, PilotPage.Jobs, PilotPage.Fleet, PilotPage.Settings]);
        chrome.Page.ShouldBe(PilotPage.Home);

        var regions = new List<ClickableRegion>();
        chrome.Rail.CollectPaintedRegions(regions);
        // One cell per page down the rail's 52 px, every one live.
        var cells = regions.Where(r => r.Result is HitResult.ListItemHit).ToList();
        cells.Count.ShouldBe(6);
        cells.Count(r => r.Result is HitResult.ListItemHit { ListId: TabBarRegions.Tabs }).ShouldBe(6);
        cells.ShouldAllBe(r => r.X + r.Width <= 53f);
    }
}

public sealed class FlightsPageTests
{
    private static FlightRow Flight(string code, FlightState state, string[]? connections = null, DateTimeOffset? landed = null) => new()
    {
        Code = code,
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = int.Parse(code[3..].TrimEnd('a', 'b'), System.Globalization.CultureInfo.InvariantCulture),
        Title = code == "TIA1611" ? "Planets on the GPU" : null,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(),
        Base = "origin/main",
        State = state,
        Connections = connections ?? [],
        CreatedAt = Live.At,
        LandedAt = landed,
    };

    [Fact]
    public void FlightsReadAsTheirCodeTitleStateAndSessions()
    {
        var sessions = new[]
        {
            Live.Row("aaaa1111", "du1-tia1611-gpu", SessionState.Working) with { Flight = "TIA1611" },
            Live.Row("bbbb2222", "du1-tia1611-review", SessionState.Ended) with { Flight = "TIA1611" },
        };
        var lines = FlightLine.Build(
            [Flight("TIA1611", FlightState.Flying), Flight("TIA1640", FlightState.Held, ["TIA1611"]), Flight("TIA1500", FlightState.Landed, landed: Live.At.AddHours(-3))],
            sessions, Live.At);

        lines.Select(l => l.ToDumpLine()).ShouldBe(
        [
            "TIA1611 | Planets on the GPU | tianwen #1611 | flying | no legs yet | flight/tia1611 | 2 sessions · 1 live",
            "TIA1640 | issue #1640 | tianwen #1640 | held (waiting for TIA1611) | no legs yet | flight/tia1640 | no session",
            "TIA1500 | issue #1500 | tianwen #1500 | landed (3h ago) | no legs yet | flight/tia1500 | no session",
        ]);
        new FlightsScreen(lines, "test", Live.At).Summary.ShouldBe("3 flights · 1 flying · 1 held");
    }

    [Fact]
    public void AFlightsLegsReadAsHowManyHowManyDoneAndTheOneFlying()
    {
        LegRow Leg(string name, int order, LegState state, int goArounds = 0) =>
            new() { Flight = "TIA1611", Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state, GoArounds = goArounds };

        FlightLine.LegsText([]).ShouldBe("no legs yet");
        FlightLine.LegsText([Leg("parser", 1, LegState.Done), Leg("render", 2, LegState.Flying, goArounds: 1), Leg("docs", 3, LegState.Planned)])
            .ShouldBe("3 legs · 1 done · render flying (go-around 1)");
        FlightLine.LegsText([Leg("parser", 1, LegState.Failed)]).ShouldBe("1 leg · 0 done · parser failed");
    }

    [Fact]
    public void TheRailOpensTheFlightsPageAndItPaintsARowPerFlight()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = new FlightsScreen(FlightLine.Build([Flight("TIA1611", FlightState.Flying), Flight("TIA1640", FlightState.Held, ["TIA1611"])], [], Live.At), "test", Live.At);

        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Live.At), flights, new RectF32(0, 0, 1280, 800));

        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Widgets.ShouldContain(chrome.Flights);
        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        regions.Count(r => r.Result is HitResult.ListItemHit { ListId: FlightsView<RgbaImage>.ListId }).ShouldBe(2);
        // Settings, the last page to arrive, is shown like the others.
        chrome.Show(PilotPage.Settings);
        chrome.Page.ShouldBe(PilotPage.Settings);
    }
}

public sealed class CardsPanelTests
{
    private static CardRow Card(string id, CardBin bin, string title, int minutesAgo, params string[] choices) => new()
    {
        Id = id,
        Bin = bin,
        Kind = "k",
        Subject = "flight/TIA1",
        Title = title,
        Choices = [.. choices.Select(c => new CardChoice { Label = c, Verb = Verbs.Note, Target = Verbs.NodeTarget })],
        At = Live.At.AddMinutes(-minutesAgo),
        State = bin == CardBin.Handled ? CardState.Closed : CardState.Open,
    };

    [Fact]
    public void RingsComeFirstAndTheSummaryCountsWhatWaits()
    {
        var screen = CardsScreen.From(
        [
            Card("c1", CardBin.ForYou, "TIA2 landed", 1),
            Card("c2", CardBin.Ring, "TIA1's PR #63 is ready to merge", 5, "Merge"),
            Card("c3", CardBin.Handled, "Resumed du1-tia1-gpu after the window reset", 2),
            Card("c4", CardBin.Ring, "TIA1/parser failed", 3, "Go around"),
        ]);

        screen.Open.Select(c => c.Id).ShouldBe(["c4", "c2", "c1"]);
        screen.Handled.ShouldHaveSingleItem().Id.ShouldBe("c3");
        screen.Summary.ShouldBe("2 ringing · 1 for you");
        CardsScreen.Empty.Summary.ShouldBe("Nothing waits on you");
    }

    [Fact]
    public void ACardsButtonsAreItsChoicesAndDismissAndAClickAnswers()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var answered = new List<(string, int)>();
        var dismissed = new List<string>();
        view.OnAnswer = (card, n) => answered.Add((card.Id, n));
        view.OnDismiss = card => dismissed.Add(card.Id);
        view.Render(CardsScreen.From([Card("c2", CardBin.Ring, "TIA1's PR #63 is ready to merge", 5, "Merge")]), new RectF32(0, 0, 340, 800));

        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var buttons = regions.Where(r => r.Result is HitResult.ButtonHit).ToList();
        buttons.Select(r => ((HitResult.ButtonHit)r.Result).Action).ShouldBe(["card:c2:1", "card:c2:dismiss"]);

        // Clicked as the window clicks: a press and a release through the router.
        var router = new InputRouter(view.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => [view] };
        foreach (var button in buttons)
        {
            router.Handle(new InputEvent.MouseDown(button.X + 2, button.Y + 2));
            router.Handle(new InputEvent.MouseUp(button.X + 2, button.Y + 2));
        }
        answered.ShouldBe([("c2", 1)]);
        dismissed.ShouldBe(["c2"]);
    }
}
