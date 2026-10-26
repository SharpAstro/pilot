using DIR.Lib;
using Pilot.Contracts;
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
        "ad hoc · stargazer | fix the flaky clock test | Opus 5.5 · medium | 38.4k / 1M · 4% | last response 3m ago | waiting on you (permission to run: dotnet test) | spend 402k",
        "watchtower | xy1-watchtower | Opus 5.5 · xhigh | 84.2k / 1M · 8% | last response 2s ago | working | spend 1.83M",
        "worker · ORB204 / parser | xy1-orb204-parser | Sonnet 5.5 · medium | 61.2k / 200k · 31% | last response 18s ago | working | spend 2.31M / 4M · 58%",
        "pilot · ORB204 | pilot-12 | Opus 5.5 · high | 213k / 1M · 21% | last response 28m ago | idle | spend 6.4M / 10M · 64%",
        "worker · LIB117 / bump | xy1-lib117-bump | no response yet | 0 / 200k · 0% | last response none yet | failed | spend 0 / 1M · 0%",
        "reviewer · ORB204 / parser | xy1-orb204-review | Opus 5.5 · high | 143k / 200k · 72% | last response 1h 42m ago | ended | spend 1.29M / 2M · 65%",
        "planner · ORB204 | xy1-orb204-plan | Opus 5.5 · high | 143k / 200k · 72% | last response 1h 42m ago | ended | spend 1.29M / 2M · 65%",
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
