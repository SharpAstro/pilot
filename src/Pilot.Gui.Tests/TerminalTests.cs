using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-tui's screen: the GUI's own trees arranged in terminal cells (Console.Lib's CellLayout, pixel-authored), so the
/// same rows, buttons and command bar land in cells, and a click on a cell runs what a click on the pixels would.
/// </summary>
public sealed class TerminalTests
{
    private static FlightsScreen Screen()
    {
        var flight = new FlightRow
        {
            Code = "TIA1611", Repo = "tianwen", RepoPath = "/r", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
            Worktree = "/r/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        };
        LegRow[] legs =
        [
            new() { Flight = "TIA1611", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done },
            new() { Flight = "TIA1611", Name = "render", Order = 2, Goal = "g", DoneWhen = "d", State = LegState.Planned },
        ];
        return new FlightsScreen(FlightLine.Build([flight], [], Live.At, legs), "live", Live.At);
    }

    [Fact]
    public void TheFlightsPageLandsInCellsWithItsButtonsAndCommandBar()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var flights = chrome.Flights;
        flights.CanCommand = true;
        var acted = new List<FlightAction>();
        flights.OnAction = acted.Add;

        var arranged = Layout.Engine.Arrange(flights.Tree(Screen()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        var described = CellLayout.Describe(arranged);

        described.ShouldContain("TIA1611");
        described.ShouldContain("Planets on the GPU");
        described.ShouldContain("Start render");
        CellLayout.TextInputs(arranged).ShouldContain(flights.CommandInput);

        // A click on the button's cell runs it, as a click on its pixels does in the window.
        var button = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Start render" } } || a.Node.Hit is HitResult.ButtonHit { Action: "flight:TIA1611:start-leg" });
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y);
        acted.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.StartLeg);
    }

    [Fact]
    public void ANarrowTerminalDropsTheBranchAndSessionsColumns()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Flights;

        CellLayout.Describe(Layout.Engine.Arrange(flights.Tree(Screen()), new Rect<int>(0, 0, 100, 40), CellMeasureContext.PixelAuthored))
            .ShouldNotContain("flight/tia1611");
        CellLayout.Describe(Layout.Engine.Arrange(flights.Tree(Screen()), new Rect<int>(0, 0, 160, 40), CellMeasureContext.PixelAuthored))
            .ShouldContain("flight/tia1611");
    }

    [Fact]
    public async Task EnterOpensTheSelectedSessionsTranscriptWhichFollowsItAndEscapeGoesBack()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var sessions = new SessionsView<RgbaImage>(vessel);
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        var asked = new List<(string Row, int After)>();
        sessions.Transcript.Load = (row, after, _) =>
        {
            lock (asked)
            {
                asked.Add((row, after));
            }
            return Task.FromResult(after < 0
                ? new TranscriptPage
                {
                    Total = 3,
                    Entries =
                    [
                        new() { Index = 0, Kind = TranscriptKind.User, Text = "Fix the flaky clock test" },
                        new() { Index = 1, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "dotnet test --filter Clock" },
                        new() { Index = 2, Kind = TranscriptKind.Error, Text = "1 failed\nClockTests.Ticks" },
                    ],
                }
                : new TranscriptPage { Total = 4, Entries = [new() { Index = 3, Kind = TranscriptKind.Assistant, Text = "Fixed it." }] });
        };
        string Described() => CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 160, 40), CellMeasureContext.PixelAuthored));
        Described();

        sessions.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        for (var i = 0; i < 100 && sessions.Transcript.Entries.Count < 4; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        sessions.Transcript.IsOpen.ShouldBeTrue();
        var described = Described();
        described.ShouldContain("Fix the flaky clock test");
        described.ShouldContain("Bash");
        described.ShouldContain("dotnet test --filter Clock");
        described.ShouldContain("ClockTests.Ticks");
        described.ShouldContain("Fixed it.");
        described.ShouldNotContain("Last response");
        // The first read asked for the last entries; the next only for what came after them.
        lock (asked)
        {
            asked.Take(2).ShouldBe([(screen.Lines[0].Row.Id, -1), (screen.Lines[0].Row.Id, 2)]);
        }

        sessions.HandleInput(new InputEvent.KeyDown(InputKey.Escape)).ShouldBeTrue();
        sessions.Transcript.IsOpen.ShouldBeFalse();
        Described().ShouldContain("Last response");
    }

    [Fact]
    public void ANarrowSessionsPageDropsItsOptionalColumnsForTheSessionColumn()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var sessions = new SessionsView<RgbaImage>(vessel);
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");

        var narrow = CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 100, 40), CellMeasureContext.PixelAuthored));
        narrow.ShouldContain("Session");
        narrow.ShouldNotContain("Model");
        narrow.ShouldContain("Context");
        narrow.ShouldContain("State");
        narrow.ShouldNotContain("Last response");
        narrow.ShouldNotContain("Spend");
        // What the Session column is for: the role and the name, whole.
        narrow.ShouldContain("worker · ORB204 / parser");
        narrow.ShouldContain("xy1-orb204-parser");

        var wide = CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 160, 40), CellMeasureContext.PixelAuthored));
        wide.ShouldContain("Model");
        wide.ShouldContain("Last response");
        wide.ShouldContain("Spend");
        wide.ShouldContain("Session");
    }
}
