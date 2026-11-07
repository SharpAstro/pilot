using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The whole state of a button made through <see cref="Presses"/>: working (dimmed, no hit), then a brief done or refused mark on the
/// button itself, then rest. Drawn on the Fleet page's Take button, CPU-rendered.
/// </summary>
public sealed class PressesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A press records its outcome before it stops being pending: a reader that has seen the press pending never then looks and
    /// finds it neither pending nor taken nor refused (a draw in that gap showed a switch its old state for a frame).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APressIsNeverSeenWithNeitherPendingNorItsOutcome(bool refuse)
    {
        var presses = new Presses();
        string? gap = null;
        for (var i = 0; i < 300 && gap is null; i++)
        {
            var key = $"k{i}";
            using var stop = new CancellationTokenSource();
            var reader = Task.Run(() =>
            {
                while (presses.PendingOf(key) is null && !stop.IsCancellationRequested)
                {
                    Thread.SpinWait(10);
                }
                while (!stop.IsCancellationRequested)
                {
                    var pending = presses.PendingOf(key);
                    if (pending is null && !presses.IsTaken(key) && presses.RefusedOf(key) is null)
                    {
                        return $"{key}: neither pending nor taken nor refused";
                    }
                    if (pending is null)
                    {
                        return null;
                    }
                }
                return null;
            }, Ct);
            await presses.PressAsync(key, "hit", _ => Task.FromResult(refuse ? "no" : (string?)null), Ct);
            await stop.CancelAsync();
            gap = await reader.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        gap.ShouldBeNull();
    }

    private static NodeSummary Summary(string stream, string prefix, string name, RoleRangeInfo range) => new()
    {
        Connected = true, Status = "live", Stream = stream, Account = "ada@example.org", LiveSessions = 1,
        Node = new NodeInfo { WireVersion = 6, Version = "2.0.9", MachineName = name, MachinePrefix = prefix, ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream, Ranges = [range] },
    };

    private static FleetScreen Fleet() => new(
    [
        FleetLine.From("node.sock", true, true, null, Summary("s1", "SE1", "DESKTOP-1", new RoleRangeInfo("worker", "opus-low..opus-low", SettingSources.ThisNode))),
        FleetLine.From("10.0.0.9:47731", false, false, null, Summary("s2", "LE1", "LAPTOP-2", new RoleRangeInfo("worker", "sonnet-low..opus-low", SettingSources.ThisNode))),
    ], Live.At);

    private sealed class Page : IDisposable
    {
        private readonly RgbaImageRenderer _renderer = new(1400, 1500);
        public PilotChrome<RgbaImage> Chrome { get; }

        public Page(TimeSpan doneFor, Func<Task<string?>> take)
        {
            Chrome = new(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Show(PilotPage.Fleet);
            Chrome.Fleet.Table.PageWidth = 1400;
            Chrome.Fleet.Presses.DoneFor = doneFor;
            Chrome.Fleet.OnTakeRange = (_, _, _, _) => take();
        }

        public (string[] Texts, string[] Faults, bool Hit) Look()
        {
            var tree = Chrome.Fleet.Tree(Fleet());
            var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 1400, 2400), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f));
            var texts = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()).ToArray();
            var faults = TextOverlap.Faults(arranged).Select(f => f.ToString()).ToArray();
            return (texts, faults, HasHit());
        }

        public bool HasHit()
        {
            Chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, Fleet(), new RectF32(0, 0, 1400, 1500));
            var regions = new List<ClickableRegion>();
            Chrome.Fleet.CollectPaintedRegions(regions);
            return regions.Any(r => r.Result is HitResult.ButtonHit b && b.Action == "fleet:10.0.0.9:47731:take:worker");
        }

        public void Press()
        {
            Chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, Fleet(), new RectF32(0, 0, 1400, 1500));
            var regions = new List<ClickableRegion>();
            Chrome.Fleet.CollectPaintedRegions(regions);
            regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == "fleet:10.0.0.9:47731:take:worker").OnClick!.Invoke(InputModifier.None);
        }

        public void Dispose() => _renderer.Dispose();
    }

    [Fact]
    public async Task ATakeIsTakingWithNoHitWhilePendingThenDoneThenTheIdleLabelAfterDoneFor()
    {
        var node = new TaskCompletionSource<string?>();
        using var page = new Page(TimeSpan.FromMilliseconds(300), () => node.Task);

        var rest = page.Look();
        rest.Texts.ShouldContain("Take LE1's");
        rest.Hit.ShouldBeTrue();

        page.Press();
        var pending = page.Look();
        pending.Texts.ShouldContain("taking…");
        pending.Hit.ShouldBeFalse();
        pending.Faults.ShouldBeEmpty();

        node.SetResult(null);
        await WaitFor(() => page.Chrome.Fleet.Presses.PendingOf("range:worker") is null);
        var done = page.Look();
        done.Texts.ShouldContain("done");
        done.Texts.ShouldNotContain("Take LE1's");
        done.Hit.ShouldBeFalse();
        done.Faults.ShouldBeEmpty();
        // Not the longer taken mark that pages read.
        page.Chrome.Fleet.Presses.IsTaken("range:worker").ShouldBeTrue();

        await WaitFor(() => page.Chrome.Fleet.Presses.MarkOf("range:worker") is null);
        var after = page.Look();
        after.Texts.ShouldContain("Take LE1's");
        after.Texts.ShouldNotContain("done");
        after.Hit.ShouldBeTrue();
    }

    [Fact]
    public async Task ARefusedTakeShowsTheRefusedStateOnTheButtonAndTheNodesWordsBesideIt()
    {
        using var page = new Page(TimeSpan.FromMinutes(1), () => Task.FromResult<string?>("this node is busy with a flight"));

        page.Press();
        await WaitFor(() => page.Chrome.Fleet.Presses.RefusedOf("range:worker") is not null);

        var look = page.Look();
        look.Texts.ShouldContain("refused");
        look.Texts.ShouldContain(t => t.Contains("busy"), string.Join(" | ", look.Texts));
        look.Hit.ShouldBeFalse();
        look.Faults.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheDoneWindowAsksForOneMoreRedrawWhenItLapses()
    {
        var redraws = 0;
        var presses = new Presses { DoneFor = TimeSpan.FromMilliseconds(100), Changed = () => Interlocked.Increment(ref redraws) };
        await presses.PressAsync("k", "k:go", _ => Task.FromResult<string?>(null), Ct);
        redraws.ShouldBe(2);
        presses.DoneLine("k", 11f).ShouldNotBeNull();
        await WaitFor(() => Volatile.Read(ref redraws) == 3);
        presses.DoneLine("k", 11f).ShouldBeNull();
    }

    private static async Task WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25, Ct);
        }
        done().ShouldBeTrue();
    }
}
