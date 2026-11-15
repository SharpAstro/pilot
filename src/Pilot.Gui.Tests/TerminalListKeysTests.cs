using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1052: on every list page of pilot-tui, Down, Down and Up move the selection through the router, on the frame the page arranges
/// (the rows are <see cref="HitResult.ListItemHit"/>s in its tree), and Enter on Sessions opens the selected row's transcript.
/// </summary>
public sealed class TerminalListKeysTests
{
    private const int Columns = 160, Rows = 50;

    private sealed class NullViewport(int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private sealed class Rig : IDisposable
    {
        public RgbaImageRenderer Vessel { get; } = new(1, 1);
        public PilotChrome<RgbaImage> Chrome { get; }
        public TerminalInput<RgbaImage> Input { get; }
        public CellInputSurface Surface { get; }
        public Func<Layout.Node> PageTree { get; set; } = () => Layout.Builder.Box(0f, 0f);

        public Rig()
        {
            var sessions = new SessionsView<RgbaImage>(Vessel) { FontPath = SessionsCapture.FontPath };
            Chrome = new PilotChrome<RgbaImage>(Vessel, sessions) { FontPath = SessionsCapture.FontPath };
            Chrome.Repos.Tracker = new BackgroundTaskTracker();
            Chrome.Play.ShowEverything();
            Surface = new CellInputSurface(sessions.Ui);
            Input = new TerminalInput<RgbaImage>(Chrome, sessions, sessions.Ui.Focus, () => { }, () => { })
            {
                Router = CellInputRouter.Create(Surface, new BackgroundTaskTracker(), () => { }),
                HitTest = (tree, column, row) => CellLayout.HitTest(tree, column, row),
            };
            // As Program.cs: the terminal keeps the Flights list (cells cannot draw the map).
            Chrome.Flights.CanvasAvailable = false;
        }

        public ImmutableArray<Layout.ArrangedNode<int>> Frame()
        {
            var rail = TerminalRail.Tree(Chrome.Page, GlyphMode.Emoji, null, null, Live.At, Input.Show, null, Columns, Input.Chords());
            var arranged = TerminalFrame.Clip(Layout.Engine.Arrange(TerminalFrame.Tree(rail, PageTree(), null), new Rect<int>(0, 0, Columns, Rows), CellMeasureContext.PixelAuthored));
            CellLayout.Paint(new NullViewport(Columns, Rows), arranged, Surface);
            Input.Painted();
            Input.Arranged = arranged;
            return arranged;
        }

        public void Key(InputKey key) => Input.Handle(new InputEvent.KeyDown(key, InputModifier.None), null);

        public void Dispose() => Vessel.Dispose();
    }

    private static NodeSummary Summary(string stream) => new()
    {
        Connected = true, Status = "live", Stream = stream, Account = stream + "@example.org", LiveSessions = 3,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-" + stream, MachinePrefix = "SE" + stream, ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream },
    };

    private static JobRow Job(string id, JobState state, int minutes) => new()
    {
        Id = id, Command = $"dotnet test {id}", Cwd = "/r/orbit", State = state, LogPath = $"/data/jobs/{id}.log", Timeout = TimeSpan.FromHours(1),
        StartedAt = Live.At.AddMinutes(-minutes), QueuedAt = Live.At.AddMinutes(-minutes),
    };

    private static FlightRow Flight(string code, int minutes) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = code, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = FlightState.Flying,
        Connections = [], CreatedAt = Live.At.AddMinutes(-minutes),
    };

    private static PullRequestRow Pr(string repo, int number) => new()
    {
        Repo = repo, Number = number, Title = $"Pull request {number}", Url = $"https://github.com/{repo}/pull/{number}", Author = "ada", Kind = PullRequestKind.Ready,
        Label = "Ready", UpdatedAt = Live.At.AddHours(-number), CreatedAt = Live.At.AddDays(-2), Head = "feature/x", Base = "main",
    };

    public static TheoryData<string> Pages => ["Sessions", "Flights", "Jobs", "Fleet", "PullRequests", "Repos"];

    private static (PilotPage Page, Func<int> Selected, Func<Layout.Node> Tree) Prepare(Rig rig, string name)
    {
        var chrome = rig.Chrome;
        switch (name)
        {
            case "Sessions":
            {
                var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
                return (PilotPage.Sessions, () => chrome.Sessions.Selected, () => chrome.Sessions.Tree(screen));
            }
            case "Flights":
            {
                var screen = new FlightsScreen(FlightLine.Build([Flight("A", 1), Flight("B", 2), Flight("C", 3), Flight("D", 4)], [], Live.At), "test", Live.At);
                return (PilotPage.Flights, () => chrome.Flights.Selected, () => chrome.Flights.Tree(screen));
            }
            case "Jobs":
            {
                var screen = new JobsScreen(JobLine.Build([Job("j1", JobState.Running, 10), Job("j2", JobState.Running, 20), Job("j3", JobState.Succeeded, 30), Job("j4", JobState.Failed, 40)], Live.At), "live", Live.At);
                return (PilotPage.Jobs, () => chrome.Jobs.Selected, () => chrome.Jobs.Tree(screen));
            }
            case "Fleet":
            {
                var screen = new FleetScreen([
                    FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("a")),
                    FleetLine.From("10.0.0.9:47731", here: false, canCommand: true, null, Summary("b")),
                    FleetLine.From("10.0.0.5:47731", here: false, canCommand: true, null, Summary("c")),
                ], Live.At);
                return (PilotPage.Fleet, () => chrome.Fleet.Selected, () => chrome.Fleet.Tree(screen));
            }
            case "PullRequests":
            {
                var row = new PullRequestsRow
                {
                    Orgs = ["SharpAstro"], ReadAt = Live.At, Viewer = "ada",
                    PullRequests = [Pr("SharpAstro/pilot", 1), Pr("SharpAstro/pilot", 2), Pr("SharpAstro/pilot", 3), Pr("SharpAstro/pilot", 4)],
                };
                var screen = new PullRequestsScreen(row, "live", Live.At);
                chrome.PullRequests.OnRefresh = _ => Task.CompletedTask;
                return (PilotPage.PullRequests, () => chrome.PullRequests.Selected, () => chrome.PullRequests.Tree(screen, Columns));
            }
            default:
            {
                var sample = SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath);
                // The sample holds two repos; a third, the first's copy under another path, makes three rows.
                RepoStatus[] repos = [.. sample, sample[0] with { Path = sample[0].Path + "-copy", Name = sample[0].Name + "-copy" }];
                var screen = new ReposScreen(RepoLine.Build(repos, [], [], [], [], Live.At), "live", Live.At);
                return (PilotPage.Repos, () => chrome.Repos.Selected, () => chrome.Repos.Tree(screen));
            }
        }
    }

    private static (Rig Rig, Func<int> Selected) Shown(string name)
    {
        var rig = new Rig();
        var (page, selected, tree) = Prepare(rig, name);
        rig.Chrome.Show(page);
        rig.PageTree = tree;
        rig.Frame();
        return (rig, selected);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void DownDownSelectsTheThirdRowAndUpGoesBackToTheSecond(string name)
    {
        var (rig, selected) = Shown(name);
        using (rig)
        {
            var arranged = rig.Frame();
            arranged.Count(a => a.Node.Hit is HitResult.ListItemHit).ShouldBeGreaterThanOrEqualTo(3, $"{name} declares its rows as ListItemHit");
            var first = selected();

            rig.Key(InputKey.Down);
            rig.Frame();
            rig.Key(InputKey.Down);
            rig.Frame();
            selected().ShouldBe(first + 2, $"{name}: Down, Down");

            rig.Key(InputKey.Up);
            rig.Frame();
            selected().ShouldBe(first + 1, $"{name}: Up");
        }
    }

    [Fact]
    public void EnterOnSessionsOpensTheSelectedRowsTranscript()
    {
        var (rig, selected) = Shown("Sessions");
        using (rig)
        {
            rig.Chrome.Sessions.Transcript.Load = (_, _, _, _) => Task.FromResult(new TranscriptPage { Entries = [], Total = 0 });
            rig.Key(InputKey.Down);
            rig.Frame();
            rig.Key(InputKey.Down);
            rig.Frame();
            selected().ShouldBeGreaterThanOrEqualTo(2);
            rig.Chrome.Sessions.Transcript.IsOpen.ShouldBeFalse();
            rig.Key(InputKey.Enter);
            rig.Frame();
            rig.Chrome.Sessions.Transcript.IsOpen.ShouldBeTrue("Enter opens the transcript");
            rig.Key(InputKey.Escape);
            rig.Frame();
            rig.Chrome.Sessions.Transcript.IsOpen.ShouldBeFalse("Escape goes up from the transcript to the list");
        }
    }
}
