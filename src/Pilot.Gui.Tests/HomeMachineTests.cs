using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Home's machine block: each shown node's CPU, GPU and memory as bars with their figures, and its top five processes under
/// them, its own sessions and jobs named. A node that sends no row shows nothing, and a node the pills hide shows no block.
/// </summary>
public sealed class HomeMachineTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNode _se1 = new();
    private readonly FakeNode _le1 = new();
    private readonly NodeFeed _here;
    private readonly FleetWatch _watch;
    private const string Lan = "10.0.0.9:47731";

    public HomeMachineTests()
    {
        _here = new NodeFeed(_se1.Open, "node.sock", TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("SE1", "s-se1")));
        _watch = new FleetWatch(_ => new NodeFeed(_le1.Open, Lan, TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("LE1", "s-le1"))));
    }

    public async ValueTask DisposeAsync()
    {
        await _watch.DisposeAsync();
        await _here.DisposeAsync();
    }

    private static NodeInfo Info(string prefix, string stream) => new()
    {
        WireVersion = 1, Version = "2.0.9", MachineName = prefix == "SE1" ? "DESKTOP-7" : "LAPTOP-3", MachinePrefix = prefix, ProcessId = 1, StartedAt = Live.At,
        DataFolder = "/d", SocketPath = "/s", Stream = stream,
    };

    private static JobRow Job(string id, JobState state) => new()
    {
        Id = id, Command = $"dotnet test {id}", Cwd = "/r", State = state, LogPath = $"/data/jobs/{id}.log", Timeout = TimeSpan.FromHours(1), StartedAt = Live.At,
    };

    private static MachineProcess Process(int pid, string name, double cpu, long bytes, string? owner = null) =>
        new() { Pid = pid, Name = name, CpuPercent = cpu, MemoryBytes = bytes, Owner = owner };

    private static MachineRow Gpu() => new()
    {
        SampledAt = Live.At, CpuPercent = 34, Cores = 16, MemoryUsed = 21L << 30, MemoryTotal = 32L << 30,
        Gpu = new MachineGpu { Percent = 81, MemoryUsed = (long)(6.1 * (1L << 30)), MemoryTotal = 12L << 30 },
        NetInBytesPerSecond = 287_500, NetOutBytesPerSecond = 50_000, NetLinkBitsPerSecond = 1_000_000_000,
        Processes =
        [
            Process(4012, "claude", 11.4, 1_500_000_000, "se1-tia1-gpu"),
            Process(5120, "dotnet", 8.2, 900L << 20, "j1"),
            Process(77, "pilot-node", 1.5, 120L << 20, "watchtower"),
            Process(912, "chrome", 0.9, 2L << 30),
        ],
    };

    private static MachineRow NoGpu() => new()
    {
        SampledAt = Live.At, CpuPercent = 12, Cores = 8, MemoryUsed = 6L << 30, MemoryTotal = 16L << 30,
        Processes = [Process(300, "claude", 5, 700L << 20, "le1-orb2-parser")],
    };

    /// <summary>SE1 (this window's, a GPU) and LE1 (on the LAN, none), each sending a row unless <paramref name="le1Row"/> is false (an older node).</summary>
    private async Task<FleetPages> TwoNodes(NodeFilter filter, bool le1Row = true)
    {
        _here.Start();
        _watch.Know([(Lan, "LE1 LAPTOP-3")]);
        for (var i = 0; i < 500 && (_se1.OpenCount == 0 || _le1.OpenCount == 0); i++)
        {
            await Task.Delay(10, Ct);
        }
        _se1.Send(Live.Snapshot(1, "s-se1", Live.Row("aaaa0001", "se1-tia1-gpu", SessionState.Working), Live.Row("aaaa0002", "se1-watchtower", SessionState.Idle) with { Role = SessionRole.Watchtower }) with
        {
            Jobs = [Job("j1", JobState.Running)], Machine = Gpu(),
        });
        _le1.Send(Live.Snapshot(1, "s-le1", Live.Row("cccc0003", "le1-orb2-parser", SessionState.Working)) with { Machine = le1Row ? NoGpu() : null });
        var pages = _watch.Pages("node.sock", _here, filter, Live.At);
        for (var i = 0; i < 500 && (pages.Sessions.Lines.Count != 3 || !filter.Choices.Select(c => c.Name).SequenceEqual(["SE1", "LE1"])); i++)
        {
            await Task.Delay(10, Ct);
            pages = _watch.Pages("node.sock", _here, filter, Live.At);
        }
        return pages;
    }

    private static HomeScreen Home(FleetPages pages) => new(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards, pages.Machines);

    [Fact]
    public async Task EachNodesRowReachesHomeAndTheDumpHasAMachineLineForEveryShownNode()
    {
        var pages = await TwoNodes(new NodeFilter());

        pages.Machines!.Select(m => m.Node!.Name).ShouldBe(["SE1", "LE1"]);
        var dump = Home(pages).ToDump().Split('\n');
        dump.ShouldContain("machine SE1: cpu 34% · gpu 81% (6.1/12 GB) · mem 21/32 GB · net 2.3/0.4 Mbps (in/out)");
        dump.ShouldContain("machine LE1: cpu 12% · mem 6/16 GB");
        // The processes under their node's line, the owner as the chip text: a session's name, a job's id and state, the watchtower's session.
        dump.ShouldContain("  claude · pid 4012 · 11.4% · 1.4 GB · se1-tia1-gpu");
        dump.ShouldContain("  dotnet · pid 5120 · 8.2% · 900 MB · j1 · running");
        dump.ShouldContain("  pilot-node · pid 77 · 1.5% · 120 MB · se1-watchtower");
        dump.ShouldContain("  chrome · pid 912 · 0.9% · 2 GB");
        dump.ShouldContain("  claude · pid 300 · 5% · 700 MB · le1-orb2-parser");
    }

    [Fact]
    public async Task AnOlderNodeThatSendsNoRowShowsNothing()
    {
        var pages = await TwoNodes(new NodeFilter(), le1Row: false);

        pages.Machines!.Select(m => m.Node!.Name).ShouldBe(["SE1"]);
        Home(pages).ToDump().ShouldNotContain("machine LE1");
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var home = new HomeView<RgbaImage>(renderer);
        var described = Texts(home, Home(pages));
        described.ShouldContain("4012");
        described.ShouldNotContain("300");

        // Nothing at all, not an empty heading, where no node sends a row.
        var none = Texts(home, Home(pages) with { Machines = [] });
        none.ShouldNotContain("Machine");
        none.ShouldNotContain("CPU");
    }

    [Fact]
    public async Task APillThatHidesANodeHidesItsBlockInTheMergeAndInTheView()
    {
        var filter = new NodeFilter();
        var all = await TwoNodes(filter);
        filter.Pick(filter.Choices.Single(c => c.Name == "LE1").Key, InputModifier.None);

        // The merge leaves out the hidden node's row...
        var picked = _watch.Pages("node.sock", _here, filter, Live.At);
        picked.Machines!.Select(m => m.Node!.Name).ShouldBe(["LE1"]);

        // ...and the view filters a screen that still holds both, as it does the chips.
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var home = new HomeView<RgbaImage>(renderer) { Nodes = filter };
        var described = Texts(home, Home(all));
        described.ShouldContain("300");
        described.ShouldNotContain("4012");
        home.Nodes = null;
        Texts(home, Home(all)).ShouldContain("4012");
    }

    [Fact]
    public async Task TheBlockHasThreeLabelledBarsAndFiveProcessRowsOrTwoBarsWithoutAGpu()
    {
        var pages = await TwoNodes(new NodeFilter());
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var home = new HomeView<RgbaImage>(renderer);

        var described = Texts(home, Home(pages));
        described.ShouldContain("CPU");
        described.ShouldContain("GPU");
        described.ShouldContain("MEM");
        described.ShouldContain("81% (6.1/12 GB)");
        described.ShouldContain("21/32 GB");
        described.ShouldContain("NET");
        described.ShouldContain("2.3/0.4 Mbps (in/out)");

        // SE1's block draws the GPU; LE1's, a node without one, does not.
        var le1 = Home(pages) with { Machines = [pages.Machines![1]] };
        var lone = Texts(home, le1);
        lone.ShouldContain("CPU");
        lone.ShouldContain("MEM");
        lone.ShouldNotContain("GPU");
    }

    [Fact]
    public async Task TheGaugesShareOneHeightAndMiddleAndTheBlockKeepsItsHeightWhateverTheProcesses()
    {
        var pages = await TwoNodes(new NodeFilter());
        using var renderer = new RgbaImageRenderer(1280, 2400);
        var home = new HomeView<RgbaImage>(renderer);
        // Wide enough for all four gauges on one line (326 each and 28 between); a narrower page wraps the last one, as the chips wrap.
        ImmutableArray<Layout.ArrangedNode<float>> Arrange(HomeScreen screen, float width = 1500) =>
            Layout.Engine.Arrange(home.Tree(screen), new Rect<float>(0, 0, width, 2400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        Rect<float> Text(ImmutableArray<Layout.ArrangedNode<float>> arranged, string value) =>
            arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == value).Bounds;

        // One node's block, so each label is there once.
        var full = Arrange(Home(pages) with { Machines = [pages.Machines![0]] });
        // Label and figure of one gauge, and of the next one, on one middle.
        var middles = new[] { "CPU", "GPU", "MEM", "NET", "34%", "81% (6.1/12 GB)", "21/32 GB", "2.3/0.4 Mbps (in/out)" }
            .Select(t => Text(full, t)).Select(b => b.Y + b.Height / 2f).ToList();
        middles.ShouldAllBe(m => Math.Abs(m - middles[0]) < 0.6f);
        Text(full, "CPU").Height.ShouldBe(Text(full, "GPU").Height);
        Text(full, "CPU").Height.ShouldBeGreaterThan(0f);

        // At the usual 1280 the net gauge wraps to a line of its own, whole and under the others, not over them.
        var wrapped = Arrange(Home(pages) with { Machines = [pages.Machines![0]] }, 1280);
        Text(wrapped, "2.3/0.4 Mbps (in/out)").Y.ShouldBeGreaterThanOrEqualTo(Text(wrapped, "21/32 GB").Y + Text(wrapped, "21/32 GB").Height - 0.5f);

        // The page ends at the same place whether the node has one busy process or five: nothing below the block moves.
        var one = Home(pages) with { Machines = [pages.Machines![0] with { Row = pages.Machines![0].Row with { Processes = [Process(1, "claude", 5, 1L << 20)] } }] };
        float Bottom(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
            arranged.Where(a => a.Node is Layout.Node.Leaf && a.Bounds.Height > 0f).Max(a => a.Bounds.Y + a.Bounds.Height);
        var none = Home(pages) with { Machines = [pages.Machines![0] with { Row = pages.Machines![0].Row with { Processes = [] } }] };
        Bottom(Arrange(one)).ShouldBe(Bottom(Arrange(Home(pages) with { Machines = [pages.Machines![0]] })), 0.5f);
        Bottom(Arrange(none)).ShouldBe(Bottom(Arrange(one)), 0.5f);
    }

    [Fact]
    public async Task AProcessOfASessionOrJobOpensThatPageOnClickAsItsChipDoes()
    {
        var pages = await TwoNodes(new NodeFilter());
        var screen = Home(pages);
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Render(pages.Sessions, pages.Flights, pages.Cards, pages.Jobs, null, null, new RectF32(0, 0, 1280, 2000), pages.Machines);
        var regions = new List<ClickableRegion>();
        chrome.Home.CollectPaintedRegions(regions);
        var buttons = regions.Where(r => r.Result is HitResult.ButtonHit).ToList();

        var opened = new List<string>();
        chrome.Home.OnSession = s => opened.Add($"session {s.Row.Name}");
        chrome.Home.OnJob = j => opened.Add($"job {j.Id}");
        foreach (var region in buttons.Where(r => ((HitResult.ButtonHit)r.Result).Action.StartsWith("home:machine:", StringComparison.Ordinal)))
        {
            region.OnClick!.Invoke(InputModifier.None);
        }

        // Every owned process opens its session or job (three of SE1's, one of LE1's); chrome, nobody's, opens nothing and is no button.
        opened.Order().ShouldBe(["job j1", "session le1-orb2-parser", "session se1-tia1-gpu", "session se1-watchtower"]);
        screen.OwnerChip(pages.Machines![0], pages.Machines![0].Row.Processes.Last()).ShouldBeNull();
    }

    [Fact]
    public async Task ALongProcessNameAndOwnerAreCutWithAnEllipsisAndCarryTheirWholeTextOnHover()
    {
        var pages = await TwoNodes(new NodeFilter());
        var longName = "Microsoft.VisualStudio.Setup.Service.exe --background-with-a-very-long-command-line";
        var longOwner = "se1-tianwen-reconciliation-parser-worker-3";
        var row = pages.Machines![0].Row with { Processes = [Process(10, longName, 40, 3L << 30, longOwner)] };
        var screen = new HomeScreen(pages.Flights, pages.Sessions with { Lines = [.. pages.Sessions.Lines, pages.Sessions.Lines.First(l => l.Row.Name == "se1-tia1-gpu") with { Name = longOwner, Row = pages.Sessions.Lines.First(l => l.Row.Name == "se1-tia1-gpu").Row with { Name = longOwner } }] }, pages.Jobs, pages.Cards,
            [pages.Machines![0] with { Row = row }]);
        using var renderer = new RgbaImageRenderer(1280, 1600);
        var home = new HomeView<RgbaImage>(renderer);

        var arranged = Layout.Engine.Arrange(home.Tree(screen), new Rect<float>(0, 0, 900, 1600), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        arranged.Any(a => a.Node.Tooltip is { } t && t.Contains(longName)).ShouldBeTrue();
        arranged.Any(a => a.Node.Tooltip == longOwner).ShouldBeTrue();
    }

    [Fact]
    public async Task AShortProcessNameCarriesItsWholeTextOnHoverToo()
    {
        var pages = await TwoNodes(new NodeFilter());
        var row = pages.Machines![0].Row with { Processes = [Process(10, "node.exe", 40, 3L << 30, null)] };
        var screen = new HomeScreen(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards, [pages.Machines![0] with { Row = row }]);
        using var renderer = new RgbaImageRenderer(1280, 1600);
        var home = new HomeView<RgbaImage>(renderer);

        // A narrow window can cut even this name, so it carries its text whatever its length.
        var arranged = Layout.Engine.Arrange(home.Tree(screen), new Rect<float>(0, 0, 420, 1600), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        arranged.Any(a => a.Node.Tooltip is { } t && t.Contains("node.exe")).ShouldBeTrue();
    }

    [Fact]
    public async Task AProcessSaysHowItWasStartedOnHoverAndItsWorkingDirInAColumnANarrowWindowDrops()
    {
        var pages = await TwoNodes(new NodeFilter());
        const string commandLine = "\"C:\\Program Files\\dotnet\\dotnet.exe\" test src/Pilot.Node.Tests --filter Machine";
        const string folder = @"C:\Users\se\source\repos\sharpastro\pilot";
        var row = pages.Machines![0].Row with
        {
            Processes = [Process(5120, "dotnet", 40, 3L << 30) with { CommandLine = commandLine, WorkingDirectory = folder }, Process(912, "chrome", 1, 1L << 30)],
        };
        var screen = new HomeScreen(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards, [pages.Machines![0] with { Row = row }]);
        using var renderer = new RgbaImageRenderer(1280, 1600);
        var home = new HomeView<RgbaImage>(renderer);
        ImmutableArray<Layout.ArrangedNode<float>> Arrange(float width) =>
            Layout.Engine.Arrange(home.Tree(screen), new Rect<float>(0, 0, width, 1600), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        static bool Shown(ImmutableArray<Layout.ArrangedNode<float>> arranged, string text) =>
            arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == text && a.Bounds.Width > 0f);

        // The name's hover says which dotnet it is: how it was started and where. One the node could not read says its name alone.
        var wide = Arrange(1280);
        wide.ShouldContain(a => a.Node.Tooltip == $"dotnet (pid 5120)\n{commandLine}\nin {folder}");
        wide.ShouldContain(a => a.Node.Tooltip == "chrome (pid 912)");

        // On a wide window the folder has its column, its end shown and the whole on hover...
        Shown(wide, "working dir").ShouldBeTrue();
        Shown(wide, @"…\se\source\repos\sharpastro\pilot").ShouldBeTrue();
        wide.ShouldContain(a => a.Node.Tooltip == folder);
        // ...and a narrow one drops it, header and rows together, so the name keeps its room.
        var narrow = Arrange(520);
        Shown(narrow, "working dir").ShouldBeFalse();
        Shown(narrow, @"…\se\source\repos\sharpastro\pilot").ShouldBeFalse();
        Shown(narrow, "dotnet").ShouldBeTrue();

        Home(pages with { Machines = [pages.Machines![0] with { Row = row }] }).ToDump()
            .ShouldContain($"  dotnet · pid 5120 · 40% · 3 GB · in {folder}", Case.Sensitive);
    }

    [Fact]
    public void AFolderTooLongForItsColumnIsWrittenFromItsEnd()
    {
        static string Of(string? folder) => MachineLine.Folder(new MachineProcess { Pid = 1, Name = "p", CpuPercent = 0, MemoryBytes = 0, WorkingDirectory = folder });

        Of(null).ShouldBe("");
        Of(@"C:\work").ShouldBe(@"C:\work");
        Of("/home/se/source/repos/sharpastro/pilot/src/Pilot.Node").ShouldBe("…/sharpastro/pilot/src/Pilot.Node");
        // A last folder longer than the column is its own end, cut.
        Of(@"C:\" + new string('x', 60)).ShouldBe("…" + new string('x', MachineLine.FolderChars - 1));
        Of(@"C:\" + new string('x', 60)).Length.ShouldBe(MachineLine.FolderChars);
    }

    [Fact]
    public void TheBarsAndFiguresMeetTheContrastFloorOnTheirGround()
    {
        var page = new RGBAColor32(0x12, 0x13, 0x19, 0xff);
        // The figures and labels are Bright and Dim text on the page; the bar's fill against its track at every meter colour is a 3:1 control edge.
        foreach (var fraction in new[] { 0.1f, 0.75f, 0.95f })
        {
            Contrast(SessionsView<RgbaImage>.MeterColor(fraction), SessionsView<RgbaImage>.Track).ShouldBeGreaterThanOrEqualTo(3.0, $"{fraction}");
        }
        Contrast(new RGBAColor32(0xe6, 0xe7, 0xee, 0xff), page).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(new RGBAColor32(0x86, 0x89, 0x98, 0xff), page).ShouldBeGreaterThanOrEqualTo(4.5);
    }

    private static double Luminance(RGBAColor32 c)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    // Every text the page draws, whole: the arranged tree's leaves, so a cut is the painter's and the text is still all there.
    private static string[] Texts(HomeView<RgbaImage> home, HomeScreen screen)
    {
        using var renderer = new RgbaImageRenderer(1280, 2400);
        return [.. Layout.Engine.Arrange(home.Tree(screen), new Rect<float>(0, 0, 1280, 2400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];
    }
}
