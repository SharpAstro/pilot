using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The window's navigation history: every move between places (a rail page, a transcript, a flight's page, a link from
/// another page) is recorded with how to get back, and Back and Forward restore the page, the node filter, the selection and
/// what was open. Restoring records nothing; a new move after Back drops what was forward.
/// </summary>
public sealed class NavigationTests
{
    private static readonly NodeMark Here = new("node.sock", "node.sock", "SE1", Here: true);
    private static readonly NodeMark Remote = new("10.0.0.9:47731", "10.0.0.9:47731", "SE2", Here: false);

    private static FlightRow Flight(string code) => new()
    {
        Code = code,
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1,
        Title = code,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(),
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = Live.At,
    };

    private static NodeSummary Summary(string stream, int waiting = 0) => new()
    {
        Connected = true, Status = "live", Stream = stream, LiveSessions = 3, WaitingOnYou = waiting,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream },
    };

    /// <summary>A chrome with two nodes' sessions, a fleet of both, and two flights, drawn as the host does each frame.</summary>
    private sealed class Window : IDisposable
    {
        private readonly RgbaImageRenderer _renderer = new(1280, 800);
        private readonly List<SessionLine> _lines;
        public PilotChrome<RgbaImage> Chrome { get; }
        public FleetScreen Fleet { get; }
        public FlightsScreen Flights { get; } = new(FlightLine.Build([Flight("TIA1"), Flight("TIA2")], [], Live.At), "test", Live.At);

        public Window()
        {
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Nodes.Choices = [Here, Remote];
            _lines =
            [
                SessionLine.From(Live.Row("aaaa0000", "h-work", SessionState.Working), Live.At) with { Node = Here },
                SessionLine.From(Live.Row("bbbb0000", "r-wait-00", SessionState.Waiting), Live.At) with { Node = Remote },
                SessionLine.From(Live.Row("bbbb0001", "r-wait-01", SessionState.Waiting), Live.At) with { Node = Remote },
            ];
            Fleet = new FleetScreen(
            [
                FleetLine.From(Here.Where, here: true, canCommand: true, null, Summary("laptop")),
                FleetLine.From(Remote.Where, here: false, canCommand: true, null, Summary("desktop", waiting: 2)),
            ], Live.At);
            Chrome.Sessions.Transcript.Load = (_, _, _, _) => Task.FromResult(new TranscriptPage { Total = 0, Entries = [] });
            Chrome.LinkFleet();
            Chrome.LinkRepos();
            Chrome.LinkCardTranscripts(Sessions, () => Flights);
        }

        public SessionsScreen Sessions() => new(SessionLine.Order(_lines.Where(l => Chrome.Nodes.Shows(l.Node))), "test", Live.At);

        public void Draw() => Chrome.Render(Sessions(), Flights, null, null, Fleet, new RectF32(0, 0, 1280, 800));

        public void ClickWaiting(NodeMark node)
        {
            Draw();
            var regions = new List<ClickableRegion>();
            Chrome.Fleet.CollectPaintedRegions(regions);
            regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == $"fleet:{node.Where}:waiting").OnClick!.Invoke(InputModifier.None);
            Draw();
        }

        public void Dispose() => _renderer.Dispose();
    }

    [Fact]
    public void BackFromASessionsTranscriptReturnsToFleetWithItsNodeFilterAndSelection()
    {
        using var window = new Window();
        var chrome = window.Chrome;
        chrome.Show(PilotPage.Fleet);
        window.Draw();
        // The remote node's row selected, and both nodes picked (not the all-nodes default) as the window has them.
        chrome.Fleet.ListCursor.MoveTo(1);
        chrome.Nodes.Toggle(Remote.Key);
        chrome.Nodes.Toggle(Here.Key);

        window.ClickWaiting(Remote);
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Nodes.Picked.ShouldBe([Remote.Key]);
        chrome.Sessions.Selected.ShouldBe(0);

        // Enter on the selected row opens its transcript: a move of its own.
        chrome.Sessions.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        chrome.Sessions.Transcript.IsOpen.ShouldBeTrue();
        chrome.CanGoBack.ShouldBeTrue();

        // Back: the list the transcript was opened from, still the remote node's, its row selected.
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.IsOpen.ShouldBeFalse();
        chrome.Nodes.Picked.ShouldBe([Remote.Key]);

        // Back again: Fleet, with the filter it had before "waiting on you" narrowed it, and its selection.
        chrome.Back().ShouldBeTrue();
        window.Draw();
        chrome.Page.ShouldBe(PilotPage.Fleet);
        chrome.Nodes.Picked.ShouldBe([Remote.Key, Here.Key], ignoreOrder: true);
        chrome.Fleet.Selected.ShouldBe(1);
        // Only the move onto Fleet is left behind it, from Home.
        chrome.CanGoForward.ShouldBeTrue();
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Home);
        chrome.CanGoBack.ShouldBeFalse();
    }

    [Fact]
    public void BackFromAFlightsPageReturnsToTheCanvas()
    {
        using var window = new Window();
        var chrome = window.Chrome;
        chrome.Flights.Mode = FlightsViewMode.Canvas;
        chrome.Show(PilotPage.Flights);
        window.Draw();
        chrome.Flights.IsDetailOpen.ShouldBeFalse();

        // A row opened from inside the page (Enter, a click) records as the rail does.
        chrome.Flights.OpenDetail("TIA2");
        window.Draw();
        chrome.Flights.OpenCode.ShouldBe("TIA2");

        chrome.Back().ShouldBeTrue();
        window.Draw();
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.IsDetailOpen.ShouldBeFalse();
        chrome.Flights.CanvasShown.ShouldBeTrue();
    }

    [Fact]
    public void BackThenForwardReturnsToWhereItWas()
    {
        using var window = new Window();
        var chrome = window.Chrome;
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail("TIA1");
        chrome.Show(PilotPage.Sessions);
        chrome.Sessions.Transcript.Open(window.Sessions().Lines[0]);

        chrome.Back().ShouldBeTrue();
        chrome.Sessions.Transcript.IsOpen.ShouldBeFalse();
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("TIA1");
        chrome.CanGoForward.ShouldBeTrue();

        chrome.Forward().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Forward().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.IsOpen.ShouldBeTrue();
        chrome.Sessions.Transcript.RowId.ShouldBe(window.Sessions().Lines[0].Row.Id);
        chrome.CanGoForward.ShouldBeFalse();
        chrome.Forward().ShouldBeFalse();
    }

    [Fact]
    public void ANewMoveAfterBackDropsTheEntriesForward()
    {
        using var window = new Window();
        var chrome = window.Chrome;
        chrome.Show(PilotPage.Sessions);
        chrome.Show(PilotPage.Flights);
        chrome.Back().ShouldBeTrue();
        chrome.CanGoForward.ShouldBeTrue();

        chrome.Show(PilotPage.Jobs);

        chrome.CanGoForward.ShouldBeFalse();
        chrome.Forward().ShouldBeFalse();
        chrome.Page.ShouldBe(PilotPage.Jobs);
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Sessions);
    }

    [Fact]
    public void BackWithNothingToGoBackToDoesNothing()
    {
        using var window = new Window();
        var chrome = window.Chrome;

        chrome.CanGoBack.ShouldBeFalse();
        chrome.Back().ShouldBeFalse();
        chrome.Page.ShouldBe(PilotPage.Home);

        // Showing the page it is already on is no move.
        chrome.Show(PilotPage.Home);
        chrome.CanGoBack.ShouldBeFalse();
    }

    [Fact]
    public void RestoringRecordsNothingAndTheHostKeepsItsOwnPage()
    {
        using var window = new Window();
        var chrome = window.Chrome;
        var host = PilotPage.Home;
        chrome.LinkRepos(page => host = page);
        chrome.Show(PilotPage.Sessions);
        chrome.Show(PilotPage.Fleet);

        chrome.Back().ShouldBeTrue();
        host.ShouldBe(PilotPage.Sessions);
        chrome.Back().ShouldBeTrue();
        host.ShouldBe(PilotPage.Home);
        chrome.Back().ShouldBeFalse();
        chrome.Forward().ShouldBeTrue();
        chrome.Forward().ShouldBeTrue();
        host.ShouldBe(PilotPage.Fleet);
    }

    [Fact]
    public void TheHistoryIsBounded()
    {
        var history = new NavHistory<int>(3);
        for (var i = 0; i < 5; i++)
        {
            history.Push(i);
        }

        history.BackCount.ShouldBe(3);
        history.Back(99, out var place).ShouldBeTrue();
        place.ShouldBe(4);
        history.Back(4, out place).ShouldBeTrue();
        history.Back(3, out place).ShouldBeTrue();
        place.ShouldBe(2);
        history.Back(2, out _).ShouldBeFalse();
    }
}
