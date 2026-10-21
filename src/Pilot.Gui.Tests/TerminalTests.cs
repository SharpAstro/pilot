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
    public void AFlightsMenuOffersItsActionsItsFoldersAndCopiesAndTheQuestionMarkSaysWhereThingsAre()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var flights = chrome.Flights;
        flights.CanCommand = true;
        var acted = new List<FlightAction>();
        var copied = new List<string>();
        flights.OnAction = acted.Add;
        flights.OnOpen = _ => { };
        flights.CopyText = copied.Add;
        var screen = Screen();
        flights.Tree(screen);

        flights.OpenMenu(screen.Lines[0], 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));

        flights.MenuItems.Select(i => i.Label).ShouldBe(["Start render", "Plan a leg…", "Open its plane's folder", "Open its repo's folder", "Copy TIA1611", "Copy its branch"]);
        flights.MenuItems[0].OnChoose!();
        acted.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.StartLeg);
        flights.MenuItems[^1].OnChoose!();
        copied.ShouldBe(["flight/tia1611"]);

        chrome.About.Facts = () => AboutFacts.For("pilot-gui", null, "node.sock");
        chrome.About.Toggle();
        var described = CellLayout.Describe(Layout.Engine.Arrange(flights.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored));
        described.ShouldContain("pilot-gui");
        described.ShouldContain(NodeFeed.ClientVersion);
        described.ShouldContain("not answering on node.sock");
        described.ShouldContain("Ctrl+S");
        chrome.About.Close();
        CellLayout.Describe(Layout.Engine.Arrange(flights.Tree(screen), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored)).ShouldNotContain("Ctrl+S");
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
        // What the assistant wrote is Markdown, a leaf a word.
        described.ShouldContain("\"Fixed ");
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
    public async Task ACallShowsWithWhatItGaveBackFoldedToItsFirstLinesAndARefusedOneSaysWhy()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        var copied = new List<string>();
        sessions.CopyText = copied.Add;
        sessions.Transcript.Load = (_, after, _) => Task.FromResult(after < 0
            ? new TranscriptPage
            {
                Total = 6,
                Entries =
                [
                    new() { Index = 0, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "dotnet test", ToolUseId = "t1" },
                    new() { Index = 1, Kind = TranscriptKind.Assistant, Text = "Meanwhile, reading." },
                    new() { Index = 2, Kind = TranscriptKind.ToolResult, Text = "one\ntwo\nthree\nfour\nfive", ToolUseId = "t1" },
                    new() { Index = 3, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "rm -rf build", ToolUseId = "t2" },
                    new() { Index = 4, Kind = TranscriptKind.Error, Text = "Permission for this action was denied", ToolUseId = "t2", Denied = "auto mode: [Destructive]" },
                    new() { Index = 5, Kind = TranscriptKind.User, Text = "## Found it", From = "Explore", FromAgent = "a1" },
                ],
            }
            : new TranscriptPage { Total = 6, Entries = [] });
        sessions.Tree(screen);
        sessions.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        for (var i = 0; i < 100 && sessions.Transcript.Entries.Count < 6; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        string Described() => CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 160, 60), CellMeasureContext.PixelAuthored));
        List<ClickableRegion> Regions()
        {
            sessions.Render(screen, new RectF32(0, 0, 1280, 800));
            var regions = new List<ClickableRegion>();
            sessions.CollectPaintedRegions(regions);
            return regions;
        }

        var described = Described();
        // The result sits under its call, not after the words that came between them, and shows its first three lines.
        described.IndexOf("└ one", StringComparison.Ordinal).ShouldBeLessThan(described.IndexOf("Meanwhile,", StringComparison.Ordinal));
        described.ShouldContain("three");
        described.ShouldNotContain("four");
        described.ShouldContain("+2 lines (click to expand)");
        described.ShouldContain("Denied · auto mode: [Destructive]");
        described.ShouldContain("Explore");
        described.ShouldContain("reported");

        Regions().Single(r => r.Result is HitResult.ButtonHit { Action: "transcript:fold:2" }).OnClick!.Invoke(InputModifier.None);
        described = Described();
        described.ShouldContain("five");
        described.ShouldContain("(click to collapse)");

        Regions().Single(r => r.Result is HitResult.ButtonHit { Action: "transcript:copy:3" }).OnClick!.Invoke(InputModifier.None);
        copied.ShouldBe(["rm -rf build"]);
        sessions.Transcript.Close();
    }

    [Fact]
    public async Task OpenedAtAMomentTheTranscriptStartsThereUntilJumpToEndAndSaysWhatIsQueued()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        var line = screen.Lines.First(l => l.Row.State is not (SessionState.Ended or SessionState.Failed));
        var at = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        sessions.Transcript.Load = (_, after, _) => Task.FromResult(new TranscriptPage
        {
            Total = 2,
            Entries = after < 0
                ? [
                    new() { Index = 0, At = at.AddMinutes(-5), Kind = TranscriptKind.Assistant, Text = "Earlier words." },
                    new() { Index = 1, At = at.AddSeconds(3), Kind = TranscriptKind.Assistant, Text = "Stuck on the build." },
                ]
                : [],
            Queued = ["then push it"],
        });
        sessions.Tree(screen);

        sessions.Transcript.Open(line, at);
        for (var i = 0; i < 100 && sessions.Transcript.Entries.Count < 2; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        string Described() => CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 160, 60), CellMeasureContext.PixelAuthored));

        var described = Described();
        described.ShouldContain("● live");
        described.ShouldContain("Stuck");
        described.ShouldNotContain("Earlier");
        described.ShouldContain("not read yet");
        described.ShouldContain("then push it");

        sessions.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        sessions.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "transcript:end" }).OnClick!.Invoke(InputModifier.None);

        described = Described();
        described.ShouldContain("Earlier");
        described.ShouldNotContain("Jump to end");
        sessions.Transcript.Close();
    }

    [Fact]
    public async Task WhatTheAssistantWroteReadsAsMarkdownAndOnlyAWebLinkOpens()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        var opened = new List<string>();
        sessions.OnOpen = opened.Add;
        sessions.Transcript.Load = (_, after, _) => Task.FromResult(new TranscriptPage
        {
            Total = 1,
            Entries = after < 0
                ? [new() { Index = 0, Kind = TranscriptKind.Assistant, Text = "## Found it\n\nThe **clock** was `UtcNow`: a long paragraph that wraps at the width of the pane rather than running off its edge, as a line of plain text did, cut wherever the window happened to end and losing the rest of what the assistant said.\n\n| file | change |\n|---|---|\n| Clock.cs | takes the injected time |\n\n```\nvar now = time.GetUtcNow();\n```\n\n- see [the PR](https://github.com/x/y/pull/1)\n- or [this](file:///C:/Windows/notepad.exe)" }]
                : [],
        });
        sessions.Tree(screen);
        sessions.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        for (var i = 0; i < 100 && sessions.Transcript.Entries.Count < 1; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var described = CellLayout.Describe(Layout.Engine.Arrange(sessions.Tree(screen), new Rect<int>(0, 0, 160, 60), CellMeasureContext.PixelAuthored));
        described.ShouldContain("Found");
        described.ShouldContain("clock");
        described.ShouldContain("UtcNow");
        described.ShouldContain("•");
        described.ShouldNotContain("**");
        described.ShouldNotContain("##");

        sessions.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        sessions.CollectPaintedRegions(regions);
        foreach (var link in regions.Where(r => r.Result is HitResult.LinkHit).ToList())
        {
            link.OnClick!.Invoke(InputModifier.None);
        }
        opened.Distinct().ShouldBe(["https://github.com/x/y/pull/1"]);
        sessions.Transcript.Close();
    }

    [Fact]
    public void ACardAboutALegNamesItsWorkersSession()
    {
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        var worker = screen.Lines[0];
        var flights = Screen() with
        {
            Lines = [.. Screen().Lines.Select(f => f with { LegRows = [.. f.LegRows.Select(l => l.Name == "parser" ? l with { Worker = worker.Name } : l)] })],
        };
        CardRow Card(string subject) => new() { Id = "c1", Bin = CardBin.Ring, Kind = "worker-idle", Subject = subject, Title = "t", At = Live.At, State = CardState.Open };

        PilotChrome<RgbaImage>.WorkerOf(Card("flight/TIA1611/parser"), flights, screen).ShouldBe(worker);
        PilotChrome<RgbaImage>.WorkerOf(Card("flight/TIA1611"), flights, screen).ShouldBeNull();
        PilotChrome<RgbaImage>.WorkerOf(Card("flight/TIA1611/renderer"), flights, screen).ShouldBeNull();
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
