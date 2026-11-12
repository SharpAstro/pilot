using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The sessions view rendered on the CPU from the contract's sample snapshot. It asserts the rows' text and which rows
/// were painted, never pixels: glyph rasterisation makes pixel baselines fragile.
/// </summary>
public sealed class SessionsViewTests
{
    /// <summary>The sample's six sessions as the view shows them, as of the snapshot's own moment (09:41:09 UTC).</summary>
    private static readonly string[] ExpectedRows =
    [
        "ad hoc · stargazer | fix the flaky clock test | Opus 5.5 · medium | 38.4k / 1M · 4% | last response 3m ago | waiting on you (permission to run: dotnet test) | spend 402k | age 20 m | spend/min 19.8k/min",
        "watchtower | xy1-watchtower | Opus 5.5 · xhigh | 84.2k / 1M · 8% | last response 2s ago | working | spend 1.83M | age 3 h 41 m | spend/min 8.26k/min",
        "worker · ORB204 / parser | xy1-orb204-parser | Sonnet 5.5 · medium | 61.2k / 200k · 31% | last response 18s ago | working | spend 2.31M / 4M · 58% | age 1 h 35 m | spend/min 24.2k/min",
        "pilot · ORB204 | pilot-12 | Opus 5.5 · high | 213k / 1M · 21% | last response 28m ago | idle | spend 6.4M / 10M · 64% | age 12 h 10 m | spend/min 8.76k/min",
        "worker · LIB117 / bump | xy1-lib117-bump | no response yet | 0 / 200k · 0% | last response none yet | failed | spend 0 / 1M · 0% | age 38 m | spend/min 0/min",
        "reviewer · ORB204 / parser | xy1-orb204-review | Opus 5.5 · high | 143k / 200k · 72% | last response 1h 42m ago | ended | spend 1.29M / 2M · 65% | age 2 h 10 m | spend/min 9.92k/min",
        "planner · ORB204 | xy1-orb204-plan | Opus 5.5 · high | 143k / 200k · 72% | last response 1h 42m ago | ended | spend 1.29M / 2M · 65% | age 2 h 10 m | spend/min 9.92k/min",
    ];

    private static SessionsScreen SampleScreen()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        return SessionsCapture.ScreenFor(snapshot, "sample");
    }

    private static List<HitResult.ListItemHit> PaintedRows(SessionsView<RgbaImage> view)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions.Select(r => r.Result).OfType<HitResult.ListItemHit>().Where(h => h.ListId == SessionsView<RgbaImage>.ListId).ToList();
    }

    [Fact]
    public void TheSampleRendersEveryRowInOrderAtTheCaptureSize()
    {
        var screen = SampleScreen();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = SessionsCapture.Render(renderer, screen);

        SessionsCapture.RowsText(screen).ShouldBe(string.Join('\n', ExpectedRows) + "\n");
        PaintedRows(view).Select(h => h.Index).Order().ShouldBe([0, 1, 2, 3, 4, 5, 6]);
        screen.Summary.ShouldBe("7 sessions · 4 live · 1 waiting on you");
    }

    [Fact]
    public void AFixerAPrReviewerAndAnAdHocRowAreToldApart()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var template = snapshot.Sessions.First(s => s.Role == SessionRole.AdHoc);
        var rows = new[]
        {
            template with { Id = "fix-1", Name = "de1-fix-pilot-1000", Role = SessionRole.Fixer, Flight = null, Leg = null, Pr = 1000, FixRound = 4 },
            template with { Id = "rev-1", Name = "de1-review-pilot-1000", Role = SessionRole.Reviewer, Flight = null, Leg = null, Pr = 1000, FixRound = null },
            template with { Id = "adhoc-1", Name = "mine", Role = SessionRole.AdHoc, Flight = null, Leg = null, Pr = null, FixRound = null, Repo = "pilot" },
        };
        var screen = SessionsCapture.ScreenFor(snapshot with { Sessions = rows }, "sample");

        var text = SessionsCapture.RowsText(screen);
        text.ShouldContain("PR fix · #1000 round 4 | ");
        text.ShouldContain("PR review · #1000 | ");
        text.ShouldContain("ad hoc · pilot | ");
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = SessionsCapture.Render(renderer, screen);
        PaintedRows(view).Count.ShouldBe(3);

        var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f);
        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 1280, 800), ctx);
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void AnEndedRowShowsWhyItEnded()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var ended = snapshot.Sessions.First(s => s.State == SessionState.Ended) with { EndedBecause = "its process went (no longer listed)" };
        var screen = SessionsCapture.ScreenFor(snapshot with { Sessions = [.. snapshot.Sessions.Select(s => s.Id == ended.Id ? ended : s)] }, "sample");

        SessionsCapture.RowsText(screen).ShouldContain("ended (its process went (no longer listed))");
        screen.Lines.Single(l => l.Row.Id == ended.Id).EndedBecause.ShouldBe("its process went (no longer listed)");
        screen.Lines.Where(l => l.Row.Id != ended.Id).ShouldAllBe(l => l.EndedBecause == null || l.Row.Id == ended.Id);
        using var renderer = new RgbaImageRenderer(1280, 800);
        SessionsCapture.Render(renderer, screen);
    }

    [Fact]
    public void TheWatchtowerRowShowsWhenAndWhyItWasLastCompacted()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var tower = snapshot.Sessions.First(s => s.Role == SessionRole.Watchtower) with
        {
            LastCompactedAt = snapshot.At - TimeSpan.FromMinutes(12), CompactedBecause = "its context reached 70% of its window",
        };
        var screen = SessionsCapture.ScreenFor(snapshot with { Sessions = [.. snapshot.Sessions.Select(s => s.Id == tower.Id ? tower : s)] }, "sample");

        var line = screen.Lines.Single(l => l.Row.Id == tower.Id);
        line.Compacted.ShouldBe("compacted 12 m ago");
        SessionsCapture.RowsText(screen).ShouldContain("| compacted 12 m ago");
        ChipBrief.Session(line).ShouldContain("compacted 12 m ago: its context reached 70% of its window");
        screen.Lines.Where(l => l.Row.Id != tower.Id).ShouldAllBe(l => l.Compacted == null);
        using var renderer = new RgbaImageRenderer(1280, 800);
        SessionsCapture.Render(renderer, screen);
    }

    [Fact]
    public void ALiveRowShowsTheNoteThatItWasRenamed()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var live = snapshot.Sessions.First(s => s.State == SessionState.Working) with { EndedBecause = "renamed to my-own-name" };
        var screen = SessionsCapture.ScreenFor(snapshot with { Sessions = [.. snapshot.Sessions.Select(s => s.Id == live.Id ? live : s)] }, "sample");

        screen.Lines.Single(l => l.Row.Id == live.Id).EndedBecause.ShouldBe("renamed to my-own-name");
    }

    [Fact]
    public void LiveSessionsComeBeforeTheOnesThatStopped()
    {
        var screen = SampleScreen();

        screen.Lines.Select(l => l.Row.State).ShouldBe(
        [
            SessionState.Waiting, SessionState.Working, SessionState.Working, SessionState.Idle,
            SessionState.Failed, SessionState.Ended, SessionState.Ended,
        ]);
        screen.Lines.TakeWhile(l => l.IsLive).Count().ShouldBe(4);
    }

    [Fact]
    public void UpAndDownMoveTheSelectionOverThePaintedRows()
    {
        var screen = SampleScreen();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = SessionsCapture.Render(renderer, screen);

        view.Selected.ShouldBe(0);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Up)).ShouldBeFalse();
        view.Selected.ShouldBe(0);

        for (var i = 1; i < screen.Lines.Count; i++)
        {
            view.HandleInput(new InputEvent.KeyDown(InputKey.Down)).ShouldBeTrue();
            view.Selected.ShouldBe(i);
        }

        view.HandleInput(new InputEvent.KeyDown(InputKey.Down)).ShouldBeFalse();
        view.Selected.ShouldBe(screen.Lines.Count - 1);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Up)).ShouldBeTrue();
        view.Selected.ShouldBe(screen.Lines.Count - 2);
    }

    [Fact]
    public void AClickOnARowSelectsIt()
    {
        var screen = SampleScreen();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = SessionsCapture.Render(renderer, screen);
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var third = regions.Single(r => r.Result is HitResult.ListItemHit { Index: 2 });

        third.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

        view.Selected.ShouldBe(2);
    }

    private static void Click(SessionsView<RgbaImage> view, SessionsScreen screen, string action)
    {
        var bounds = new RectF32(0f, 0f, 1500f, 800f);
        view.Render(screen, bounds);
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        view.Render(screen, bounds);
    }

    [Fact]
    public void AClickOnContextSortsByTokensBiggestFirstAndASecondClickFlipsIt()
    {
        var screen = SampleScreen();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        Click(view, screen, "sessions:table:Context");
        view.Shown.Select(l => l.Row.ContextTokens).ShouldBe([.. screen.Lines.Select(l => l.Row.ContextTokens).OrderByDescending(t => t)]);
        view.Table.Sort.ShouldBe(new ListTable.SortState("Context", Descending: true));

        Click(view, screen, "sessions:table:Context");
        view.Shown.Select(l => l.Row.ContextTokens).ShouldBe([.. screen.Lines.Select(l => l.Row.ContextTokens).Order()]);
        view.Table.Sort.ShouldBe(new ListTable.SortState("Context", Descending: false));
    }

    [Fact]
    public void ASortKeepsEachNodesRowsTogetherAndSortsWithinThem()
    {
        var screen = SampleScreen();
        screen = screen with { Lines = [.. screen.Lines.Select((l, i) => l with { Node = new NodeMark(i % 2 == 0 ? "s" : "s9", "node.sock", i % 2 == 0 ? "SE1" : "LE1", Here: i % 2 == 0) })] };
        using var renderer = new RgbaImageRenderer(1500, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        Click(view, screen, "sessions:table:Spend");

        var groups = view.Shown.GroupBy(l => l.Node!.Name).ToList();
        view.Shown.Select(l => l.Node!.Name).Distinct().Count().ShouldBe(2);
        view.Shown.Select(l => l.Node!.Name).ShouldBe([.. view.Shown.Select(l => l.Node!.Name).OrderBy(n => n, StringComparer.Ordinal)], "a node's rows are not split");
        foreach (var group in groups)
        {
            group.Select(l => l.Row.SpendWeighted).ShouldBe([.. group.Select(l => l.Row.SpendWeighted).OrderByDescending(s => s)]);
        }
    }

    [Fact]
    public void TheSelectionFollowsItsRowThroughASort()
    {
        var screen = SampleScreen();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        view.Render(screen, new RectF32(0f, 0f, 1280f, 800f));
        view.Select(4);
        var key = view.SelectedId.ShouldNotBeNull();

        Click(view, screen, "sessions:table:Spend");

        view.SelectedId.ShouldBe(key);
        view.Shown[view.Selected].Key.ShouldBe(key);
        view.Selected.ShouldNotBe(4, "the sort moved the row, and the selection went with it");
    }

    [Fact]
    public void AnEmptySnapshotSaysSo()
    {
        var screen = new SessionsScreen([], "empty", DateTimeOffset.UnixEpoch);
        using var renderer = new RgbaImageRenderer(640, 400);
        var view = SessionsCapture.Render(renderer, screen);

        PaintedRows(view).ShouldBeEmpty();
        view.Selected.ShouldBe(-1);
        screen.Summary.ShouldBe("0 sessions · 0 live");
    }

    [Fact]
    public void AReportedBlockShowsAsBlockedWithItsMessageAndAnUnreportedWaitStaysWaiting()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var waiting = snapshot.Sessions.First(s => s.State == SessionState.Waiting);
        var plain = SessionsCapture.ScreenFor(snapshot, "sample");
        var blocked = waiting with { Reported = "blocked", WaitingFor = "which branch should this land on?" };
        var screen = SessionsCapture.ScreenFor(snapshot with { Sessions = [.. snapshot.Sessions.Select(s => s.Id == waiting.Id ? blocked : s)] }, "sample");

        var line = screen.Lines.Single(l => l.Row.Id == waiting.Id);
        line.State.ShouldBe("blocked");
        line.WaitingFor.ShouldBe("which branch should this land on?");
        line.IsWaiting.ShouldBeTrue();
        SessionsCapture.RowsText(screen).ShouldContain("blocked (which branch should this land on?)");
        plain.Lines.Single(l => l.Row.Id == waiting.Id).State.ShouldBe("waiting on you");
        SessionsCapture.RowsText(plain).ShouldContain("waiting on you");
        screen.Lines.Where(l => l.Row.Id != waiting.Id).Select(l => l.State).ShouldBe(plain.Lines.Where(l => l.Row.Id != waiting.Id).Select(l => l.State));
        using var renderer = new RgbaImageRenderer(1280, 800);
        SessionsCapture.Render(renderer, screen);
    }

    [Fact]
    public void CaptureWritesThePngAndTheRowsBesideIt()
    {
        var folder = Directory.CreateTempSubdirectory("pilot-gui-tests-");
        try
        {
            var png = Path.Combine(folder.FullName, "gui.png");
            var screen = SampleScreen();

            var rows = SessionsCapture.Write(screen, png, 1280, 800);

            rows.ShouldBe(png + ".rows.txt");
            File.ReadAllBytes(png).Take(8).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a });
            File.ReadAllLines(rows).ShouldBe(ExpectedRows);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
