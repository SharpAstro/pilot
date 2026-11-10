using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The role ranges (#599) on the Settings page (a line of pairs with a default and a max handle, the source in words and the nodes
/// that differ) and on the Fleet page (a row per role with each node's range and the odd ones marked), CPU-rendered.
/// </summary>
public sealed class RoleRangeRowsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Line = ["sonnet-low", "sonnet-medium", "sonnet-high", "sonnet-xhigh", "opus-low", "opus-medium", "opus-high", "opus-xhigh"];

    private static SettingRow Range(string value, string source, string? saved = null, string? fleet = null, params SettingDeviation[] deviations) => new()
    {
        Key = "worker-range", Label = "Worker range", Detail = "The pair a worker starts on and the highest it steps up to", Value = value, Saved = saved, Source = source,
        Choices = Line, FleetValue = fleet, Deviations = deviations,
    };

    private static SettingsPage Page(SettingRow row) => new() { File = @"C:\Users\ada\AppData\Local\pilot2\settings.json", Settings = [row] };

    private static PilotChrome<RgbaImage> Chrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    private static async Task Loaded(PilotChrome<RgbaImage> chrome, SettingsPage page, Func<string, string[], CancellationToken, Task<SettingsReply>>? send = null)
    {
        chrome.Settings.Load = _ => Task.FromResult(page);
        chrome.Settings.Send = send ?? ((_, _, _) => Task.FromResult(new SettingsReply("done")));
        chrome.Show(PilotPage.Settings);
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 1500));
        for (var i = 0; i < 200 && chrome.Settings.Page is null; i++)
        {
            await Task.Delay(25, Ct);
        }
        chrome.Settings.Page.ShouldNotBeNull();
    }

    private static (string[] Texts, string[] Faults) Look(Layout.Node tree, RgbaImageRenderer renderer, float width)
    {
        var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, 2400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return (
            [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())],
            [.. TextOverlap.Faults(arranged).Select(f => f.ToString())]);
    }

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome)
    {
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 1500));
        var regions = new List<ClickableRegion>();
        chrome.Settings.CollectPaintedRegions(regions);
        return regions;
    }

    private static void Click(List<ClickableRegion> regions, string action) =>
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);

    [Fact]
    public async Task ARangeRowDrawsTheLineOfPairsItsSourceAndTheNodesThatDiffer()
    {
        var row = Range("sonnet-high..opus-medium", SettingSources.ThisNode, "sonnet-high..opus-medium", "sonnet-medium..opus-xhigh", new SettingDeviation("LE1 · LAPTOP-2", "sonnet-low..opus-low"));
        foreach (var width in new[] { 700f, 1280f })
        {
            using var renderer = new RgbaImageRenderer((uint)width, 1500);
            var chrome = Chrome(renderer);
            await Loaded(chrome, Page(row));
            chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 1500));

            var (texts, faults) = Look(chrome.Settings.Tree(DateTimeOffset.UtcNow), renderer, width);

            texts.ShouldContain("set on this node");
            texts.ShouldContain("default");
            texts.ShouldContain("max");
            if (width > 1000)
            {
                texts.ShouldContain("S high");
                texts.ShouldContain("O xhigh");
            }
            else
            {
                // Too narrow for 'S high': the effort alone, or its first letter, under the model it belongs to.
                texts.ShouldContain(t => t == "high" || t == "h");
                texts.ShouldContain(t => t == "xhigh" || t == "x");
                texts.ShouldContain(t => t == "sonnet" || t == "S");
            }
            texts.ShouldContain(t => t.Contains("the fleet's range is sonnet-medium..opus-xhigh"));
            texts.ShouldContain("differs");
            texts.ShouldContain("LAPTOP-2");
            faults.ShouldBeEmpty();
        }
    }

    /// <summary>Text the page cuts with an ellipsis is never cut silently (#887): the range row's summary and help lines and the section hint carry their whole text as the tooltip.</summary>
    [Theory]
    [InlineData(500)]
    [InlineData(700)]
    public async Task TheRangeRowsSummaryAndHelpLinesAndTheSectionHintCarryTheirWholeTextOnHover(int width)
    {
        var row = Range("sonnet-high..opus-medium", SettingSources.ThisNode, "sonnet-high..opus-medium", "sonnet-medium..opus-xhigh");
        using var renderer = new RgbaImageRenderer((uint)width, 1500);
        var chrome = Chrome(renderer);
        await Loaded(chrome, Page(row));
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 1500));

        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(DateTimeOffset.UtcNow), new Rect<float>(0, 0, width, 2400), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var tooltips = arranged.Select(a => a.Node.Tooltip).OfType<string>().ToList();

        tooltips.ShouldContain(t => t.StartsWith("sonnet-high..opus-medium", StringComparison.Ordinal) && t.EndsWith("the fleet's range is sonnet-medium..opus-xhigh", StringComparison.Ordinal));
        tooltips.ShouldContain("setting this node's range: a press moves the nearer handle");
        tooltips.ShouldContain("the node restarts to run with a change at once; its sessions, flights and jobs carry on");
    }

    public static TheoryData<string, string, int, float> RangeRowCases()
    {
        var data = new TheoryData<string, string, int, float>();
        foreach (var (name, value) in new[]
        {
            ("worker", "sonnet..opus-xhigh"), ("watchtower", "sonnet..sonnet-high"),
            ("adjacent", "sonnet-high..sonnet-xhigh"), ("one stop", "opus-medium..opus-medium"),
            // The handles on columns 0 and 1 (6 and 7): 'default' and 'max' span two columns each at the narrow widths and the shift search finds no room, so they stack.
            ("close at the start", "sonnet-low..sonnet-medium"), ("close at the end", "opus-high..opus-xhigh"),
        })
        {
            foreach (var width in new[] { 500, 700, 900, 1300, 1900 })
            {
                foreach (var dpi in new[] { 1f, 1.5f, 2f })
                {
                    data.Add(name, value, width, dpi);
                }
            }
        }
        return data;
    }

    private static IEnumerable<Layout.Node> SelfAndDescendants(Layout.Node node) => Descendants(node).Prepend(node);

    // A text run's horizontal extent as drawn: measured, then placed in its leaf by the run's own alignment and the leaf's padding.
    private static (float Left, float Right) Extent(Layout.Node leaf, Layout.Content.Text text, Rect<float> bounds, float measured, float dpi)
    {
        var pad = leaf.Padding * dpi;
        return text.HAlign switch
        {
            TextAlign.Center => (bounds.X + bounds.Width / 2f - measured / 2f, bounds.X + bounds.Width / 2f + measured / 2f),
            TextAlign.Far => (bounds.X + bounds.Width - pad - measured, bounds.X + bounds.Width - pad),
            _ => (bounds.X + pad, bounds.X + pad + measured),
        };
    }

    [Theory]
    [MemberData(nameof(RangeRowCases))]
    public async Task NoTwoTextRunsOfARangeRowOverlapByTheirMeasuredWidthAndEveryTextFitsItsLeaf(string name, string value, int width, float dpi)
    {
        var row = Range(value, SettingSources.BuiltIn);
        using var renderer = new RgbaImageRenderer((uint)width, 1500);
        var chrome = Chrome(renderer);
        chrome.DpiScale = dpi;
        await Loaded(chrome, Page(row));
        chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 1500));
        var room = chrome.Settings.PageWidth.ShouldNotBeNull();

        var context = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);
        var arranged = Layout.Engine.Arrange(chrome.Settings.Tree(DateTimeOffset.UtcNow), new Rect<float>(0, 0, room * dpi, 6000), context);
        var bounds = new Dictionary<Layout.Node, Rect<float>>(ReferenceEqualityComparer.Instance);
        foreach (var a in arranged)
        {
            bounds[a.Node] = a.Bounds;
        }

        // The line of pairs and its captions: what sits in the lines column before the 'setting ...' help line.
        var column = arranged.Select(a => a.Node).OfType<Layout.Node.Stack>()
            .First(s => s.Axis == Layout.Axis.Vertical && s.Children.Any(c => TextOf(c)?.StartsWith("setting this node's range", StringComparison.Ordinal) == true));
        var help = Array.FindIndex([.. column.Children], c => TextOf(c)?.StartsWith("setting this node's range", StringComparison.Ordinal) == true);
        var runs = column.Children.Take(help).SelectMany(SelfAndDescendants)
            .Select(n => (Node: n, Text: n is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.Trim().Length > 0 ? (Layout.Content.Text?)t : null))
            .Where(r => r.Text is not null)
            .Select(r =>
            {
                var b = bounds[r.Node];
                var measured = context.MeasureText(r.Text!.Value, r.Text.FontSize).Width;
                var (left, right) = Extent(r.Node, r.Text, b, measured, dpi);
                return (r.Text.Value, b, measured, left, right);
            })
            .ToList();
        var who = $"{name} {value} at {width} x{dpi} (room {room})";
        runs.Count.ShouldBeGreaterThanOrEqualTo(9, who);

        foreach (var (text, b, measured, _, _) in runs)
        {
            measured.ShouldBeLessThanOrEqualTo(b.Width + 0.5f, $"{who}: '{text.Trim()}' is {measured} wide in a leaf {b.Width} wide");
        }
        for (var i = 0; i < runs.Count; i++)
        {
            for (var j = i + 1; j < runs.Count; j++)
            {
                var (ta, ba, _, la, ra) = runs[i];
                var (tb, bb, _, lb, rb) = runs[j];
                var across = Math.Min(ra, rb) - Math.Max(la, lb);
                var down = Math.Min(ba.Y + ba.Height, bb.Y + bb.Height) - Math.Max(ba.Y, bb.Y);
                (across > 0.5f && down > 0.5f).ShouldBeFalse($"{who}: '{ta.Trim()}' [{la}..{ra}] meets '{tb.Trim()}' [{lb}..{rb}]");
            }
        }
        var texts = runs.Select(r => r.Value.Trim()).ToList();
        if (value.StartsWith("opus-medium..opus-medium", StringComparison.Ordinal))
        {
            texts.ShouldContain("default = max", who);
            texts.ShouldNotContain("default", who);
            texts.ShouldNotContain("max", who);
        }
        else
        {
            texts.ShouldContain("default", who);
            texts.ShouldContain("max", who);
        }
    }

    [Fact]
    public async Task APairPressMovesTheNearerHandleAndSendsTheRangeForThisNodeOrTheFleet()
    {
        var row = Range("sonnet-high..opus-medium", SettingSources.Fleet, fleet: "sonnet-high..opus-medium");
        using var renderer = new RgbaImageRenderer(1280, 1500);
        var chrome = Chrome(renderer);
        var sent = new List<string[]>();
        await Loaded(chrome, Page(row), (_, args, _) =>
        {
            lock (sent)
            {
                sent.Add(args);
            }
            return Task.FromResult(new SettingsReply("done"));
        });

        // Below the default: the default handle moves down. Above the max: the max moves up.
        Click(Regions(chrome), "settings:worker-range:pair:sonnet-low");
        await Sent(sent, 1);
        Click(Regions(chrome), "settings:worker-range:pair:opus-xhigh");
        await Sent(sent, 2);
        // Between them, the nearer one: opus-low is nearer the max (opus-medium) than the default.
        Click(Regions(chrome), "settings:worker-range:pair:opus-low");
        await Sent(sent, 3);
        // For the fleet.
        Click(Regions(chrome), "settings:worker-range:scope:fleet");
        Click(Regions(chrome), "settings:worker-range:pair:sonnet-low");
        await Sent(sent, 4);
        lock (sent)
        {
            sent[0].ShouldBe(["worker-range", "sonnet-low..opus-medium"]);
            sent[1].ShouldBe(["worker-range", "sonnet-high..opus-xhigh"]);
            sent[2].ShouldBe(["worker-range", "sonnet-high..opus-low"]);
            sent[3].ShouldBe(["worker-range", "sonnet-low..opus-medium", "--fleet"]);
        }
    }

    // PILOT_TEST_CAPTURE_DIR names the folder the frame is kept in as a PNG (without it nothing is written).
    private static void Capture(RgbaImageRenderer renderer, string name)
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"pil770-pair-{name}.png"), PngWriter.Encode(renderer.Surface.Pixels, 1280, 1500));
        }
    }

    /// <summary>Captures of the range row narrow (500, 700) and wide: a mid-line range and one with its handles close at the start (stacked captions), into PILOT_TEST_CAPTURE_DIR.</summary>
    [Fact]
    public async Task TheRangeRowCapturedNarrowWithAMidLineRangeAndOneCloseAtTheStart()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR") is not { Length: > 0 } dir)
        {
            return;
        }
        Directory.CreateDirectory(dir);
        foreach (var (name, value) in new[] { ("mid", "sonnet-high..opus-medium"), ("close-at-start", "sonnet-low..sonnet-medium") })
        {
            foreach (var width in new[] { 500, 700, 1280 })
            {
                using var renderer = new RgbaImageRenderer((uint)width, 520);
                var chrome = Chrome(renderer);
                chrome.Settings.Load = _ => Task.FromResult(Page(Range(value, SettingSources.ThisNode, value, "sonnet-medium..opus-xhigh", new SettingDeviation("LE1 · LAPTOP-2", "sonnet-low..opus-low"))));
                chrome.Settings.Send = (_, _, _) => Task.FromResult(new SettingsReply("done"));
                chrome.Show(PilotPage.Settings);
                for (var i = 0; i < 200 && chrome.Settings.Page is null; i++)
                {
                    chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 520));
                    await Task.Delay(25, Ct);
                }
                chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, width, 520));
                File.WriteAllBytes(Path.Combine(dir, $"pil770-range-{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 520));
            }
        }
    }

    [Fact]
    public async Task APairPressedAndWithTheNodeIsDrawnDimmedAndWithNoHitWhereIdleItIsAButton()
    {
        var row = Range("sonnet-high..opus-medium", SettingSources.Fleet);
        using var renderer = new RgbaImageRenderer(1280, 1500);
        var chrome = Chrome(renderer);
        var answer = new TaskCompletionSource<SettingsReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loaded(chrome, Page(row), (_, _, _) =>
        {
            asked.TrySetResult();
            return answer.Task;
        });

        // The pair as drawn now: sonnet-low is below the default handle, so a press on it moves the default and it is drawn as a button.
        Layout.Node.Leaf Cell() => Descendants(chrome.Settings.Tree(DateTimeOffset.UtcNow)).OfType<Layout.Node.Leaf>().Single(l => TextOf(l) == "S low");
        var idle = Cell();
        idle.Background.ShouldBe(Palette.Current.Button);
        var action = "settings:worker-range:pair:sonnet-low";
        Regions(chrome).Any(r => r.Result is HitResult.ButtonHit b && b.Action == action).ShouldBeTrue();
        Capture(renderer, "idle");

        Click(Regions(chrome), action);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // While the node has it: dimmed off the button blue, and no hit to press a second time.
        var working = Cell();
        working.Background.ShouldNotBe(idle.Background);
        working.Background.ShouldBe(Palette.Current.MenuBg);
        ((Layout.Content.Text)working.Content).Color.ShouldBe(Palette.Current.Dim);
        ((Layout.Content.Text)idle.Content).Color.ShouldBe(Palette.Current.Body);
        Regions(chrome).Any(r => r.Result is HitResult.ButtonHit b && b.Action == action).ShouldBeFalse();
        Capture(renderer, "working");

        answer.SetResult(new SettingsReply("done"));
    }

    /// <summary>A handle's pair keeps its Bright ink while the node has the press (Body on Chosen is 3.7:1, under the 4.5 floor): a chosen working Pill does the same.</summary>
    [Fact]
    public async Task AWorkingHandleKeepsItsBrightInkOnTheChosenMarkAndAllowsNoSecondPress()
    {
        var row = Range("sonnet-high..opus-medium", SettingSources.ThisNode, "sonnet-high..opus-medium");
        using var renderer = new RgbaImageRenderer(1280, 1500);
        var chrome = Chrome(renderer);
        var answer = new TaskCompletionSource<SettingsReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loaded(chrome, Page(row), (_, _, _) =>
        {
            asked.TrySetResult();
            return answer.Task;
        });
        // With the max handle chosen, a press on the default handle's own pair moves the max down onto it: that pair is a handle and is pressable.
        Click(Regions(chrome), "settings:worker-range:handle:max");
        Layout.Node.Leaf Cell() => Descendants(chrome.Settings.Tree(DateTimeOffset.UtcNow)).OfType<Layout.Node.Leaf>().Single(l => l.Tooltip == "sonnet-high");
        var action = "settings:worker-range:pair:sonnet-high";
        Regions(chrome).Any(r => r.Result is HitResult.ButtonHit b && b.Action == action).ShouldBeTrue();
        ((Layout.Content.Text)Cell().Content).Color.ShouldBe(Palette.Current.Bright);

        Click(Regions(chrome), action);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var working = Cell();
        working.Background.ShouldBe(Palette.Current.Chosen);
        ((Layout.Content.Text)working.Content).Color.ShouldBe(Palette.Current.Bright);
        Regions(chrome).Any(r => r.Result is HitResult.ButtonHit b && b.Action == action).ShouldBeFalse();
        Contrast(Palette.Current.Bright, Palette.Current.Chosen).ShouldBeGreaterThanOrEqualTo(4.5);
        answer.SetResult(new SettingsReply("done"));
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        static double Luminance(RGBAColor32 c)
        {
            static double Linear(byte v)
            {
                var x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
        }
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// pilot-tui hands the page's width to the tree as it does the pull requests' (columns x 8 units), so a range row decides stacked or beside
    /// against the terminal's width and not the 900-unit default: at 60 columns the line of pairs sits under its title, at 120 beside it.
    /// </summary>
    [Theory]
    [InlineData(60, true)]
    [InlineData(120, false)]
    public async Task ARangeRowInATerminalStacksOrSitsBesideByTheColumnsItIsHanded(int columns, bool stacked)
    {
        using var renderer = new RgbaImageRenderer(1280, 1500);
        // No FontPath: the terminal's page measures by the guess.
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer));
        chrome.Settings.Load = _ => Task.FromResult(Page(Range("sonnet-high..opus-medium", SettingSources.ThisNode)));
        chrome.Settings.Send = (_, _, _) => Task.FromResult(new SettingsReply("done"));
        for (var i = 0; i < 200 && chrome.Settings.Page is null; i++)
        {
            chrome.Settings.Tree(DateTimeOffset.UtcNow, columns * 8f);
            await Task.Delay(25, Ct);
        }
        chrome.Settings.Page.ShouldNotBeNull();
        var tree = chrome.Settings.Tree(DateTimeOffset.UtcNow, columns * 8f);

        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, columns, 200), CellMeasureContext.PixelAuthored);
        var title = arranged.Single(a => TextOf(a.Node) == "Worker range").Bounds;
        var cell = arranged.First(a => a.Node.Tooltip == "sonnet-low").Bounds;
        if (stacked)
        {
            cell.Y.ShouldBeGreaterThan(title.Y);
            cell.X.ShouldBeLessThanOrEqualTo(title.X + 1);
        }
        else
        {
            cell.X.ShouldBeGreaterThan(title.X + 20);
        }
    }

    [Fact]
    public void APressReplacesOnlyTheEndThatMovedSoABareEndStaysBare()
    {
        var row = Range("sonnet..opus-xhigh", SettingSources.BuiltIn);
        var at = (string pair) => Array.IndexOf(Line, pair);
        SettingsView<RgbaImage>.PressedRange(row, row.Value, at("opus-high"), "").ShouldBe("sonnet..opus-high");
        SettingsView<RgbaImage>.PressedRange(row, "opus..opus-high", at("opus-xhigh"), "max").ShouldBe("opus..opus-xhigh");
    }

    [Fact]
    public void ADefaultPressOnABareMaxsOwnPairOrAboveItSendsNothing()
    {
        var row = Range("sonnet-high..opus", SettingSources.BuiltIn);
        var at = (string pair) => Array.IndexOf(Line, pair);
        SettingsView<RgbaImage>.PressedRange(row, row.Value, at("opus-low"), "default").ShouldBeNull();
        SettingsView<RgbaImage>.PressedRange(row, row.Value, at("opus-medium"), "default").ShouldBeNull();
        SettingsView<RgbaImage>.PressedRange(row, "sonnet..opus", at("sonnet-high"), "default").ShouldBe("sonnet-high..opus");
    }

    [Fact]
    public void AFleetScopePressStartsFromTheFleetsRangeNotTheNodesOverride()
    {
        var row = Range("opus-low..opus-low", SettingSources.ThisNode, saved: "opus-low", fleet: "sonnet..opus-xhigh");
        var text = SettingsView<RgbaImage>.EditedText(row, fleet: true);
        text.ShouldBe("sonnet..opus-xhigh");
        SettingsView<RgbaImage>.PressedRange(row, text, Array.IndexOf(Line, "opus-high"), "").ShouldBe("sonnet..opus-high");
        SettingsView<RgbaImage>.EditedText(row, fleet: false).ShouldBe("opus-low..opus-low");
    }

    [Fact]
    public void AHandleNeverLandsBelowTheDefaultOrAboveTheMaxAndNeverOnTheLineBeyondEffortXhigh()
    {
        SettingsView<RgbaImage>.Moved((2, 5), 1, "max").ShouldBeNull();
        SettingsView<RgbaImage>.Moved((2, 5), 6, "default").ShouldBeNull();
        SettingsView<RgbaImage>.Moved((2, 5), 4, "default").ShouldBe((4, 5));
        SettingsView<RgbaImage>.Moved((2, 5), 3, "").ShouldBe((3, 5));
        SettingsView<RgbaImage>.Moved((2, 5), 4, "").ShouldBe((2, 4));
        // The line holds no pair with effort max, so there is none to land on.
        Line.ShouldNotContain(p => p.EndsWith("-max", StringComparison.Ordinal));
        SettingsView<RgbaImage>.HandlesOf(Range("sonnet..opus-medium", SettingSources.BuiltIn)).ShouldBe((0, 5));
    }

    private static async Task Sent(List<string[]> sent, int count)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (sent)
            {
                if (sent.Count >= count)
                {
                    return;
                }
            }
            await Task.Delay(25, Ct);
        }
        sent.Count.ShouldBeGreaterThanOrEqualTo(count);
    }

    private static NodeSummary Summary(string stream, string prefix, string name, params RoleRangeInfo[] ranges) => new()
    {
        Connected = true, Status = "live", Stream = stream, Account = "ada@example.org", LiveSessions = 1,
        Node = new NodeInfo { WireVersion = 6, Version = "2.0.9", MachineName = name, MachinePrefix = prefix, ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream, Ranges = ranges },
    };

    [Fact]
    public void TheFleetPageHasARowPerRoleAndMarksTheNodeThatDiffers()
    {
        var fleet = new FleetScreen(
        [
            FleetLine.From("node.sock", true, true, null, Summary("s1", "SE1", "DESKTOP-1", new RoleRangeInfo("worker", "sonnet-high..opus-medium", SettingSources.Fleet), new RoleRangeInfo("planner", "opus-medium..opus-high", SettingSources.BuiltIn))),
            FleetLine.From("10.0.0.9:47731", false, false, null, Summary("s2", "LE1", "LAPTOP-2", new RoleRangeInfo("worker", "sonnet-low..opus-low", SettingSources.ThisNode), new RoleRangeInfo("planner", "opus-medium..opus-high", SettingSources.BuiltIn))),
        ], Live.At);

        var roles = fleet.Roles;
        roles.Select(r => r.Role).ShouldBe(["worker", "planner"]);
        roles[0].Fleet.ShouldBe("sonnet-high..opus-medium");
        roles[0].Nodes.Select(n => n.Odd).ShouldBe([false, true]);
        roles[1].Odd.ShouldBe(0);

        foreach (var width in new[] { 700, 1400 })
        {
            using var renderer = new RgbaImageRenderer((uint)width, 1500);
            var chrome = Chrome(renderer);
            chrome.Show(PilotPage.Fleet);
            chrome.Fleet.Table.PageWidth = width;
            var (texts, faults) = Look(chrome.Fleet.Tree(fleet), renderer, width);

            texts.ShouldContain("worker");
            texts.ShouldContain("planner");
            texts.ShouldContain(t => t.StartsWith("fleet: sonnet-high..opus-medium") && t.Contains("1 differ"));
            texts.ShouldContain("differs: LE1 · LAPTOP-2: sonnet-low..opus-low (this node)");
            texts.ShouldContain("SE1 · DESKTOP-1: sonnet-high..opus-medium");
            faults.ShouldBeEmpty();
        }
    }

    // This node (SE1) runs its own opus-low; LE1 differs on its own setting, RP1 differs on the fleet layer, MT1 matches this node.
    private static FleetScreen TakeFleet() => new(
    [
        FleetLine.From("node.sock", true, true, null, Summary("s1", "SE1", "DESKTOP-1", new RoleRangeInfo("worker", "opus-low..opus-low", SettingSources.ThisNode))),
        FleetLine.From("10.0.0.9:47731", false, false, null, Summary("s2", "LE1", "LAPTOP-2", new RoleRangeInfo("worker", "sonnet-low..opus-low", SettingSources.ThisNode))),
        FleetLine.From("10.0.0.10:47731", false, false, null, Summary("s3", "RP1", "PI-ROOF", new RoleRangeInfo("worker", "sonnet-high..opus-medium", SettingSources.Fleet))),
        FleetLine.From("10.0.0.11:47731", false, false, null, Summary("s4", "MT1", "MATCH-4", new RoleRangeInfo("worker", "opus-low..opus-low", SettingSources.BuiltIn))),
    ], Live.At);

    [Fact]
    public void ADifferingLineOfAnotherNodeShowsTakeAndNoPushAndThisNodesOwnAndMatchingLinesShowNone()
    {
        var roles = TakeFleet().Roles;
        roles[0].Nodes.Select(n => n.Differs).ShouldBe([false, true, true, false]);
        foreach (var width in new[] { 700, 1400 })
        {
            using var renderer = new RgbaImageRenderer((uint)width, 1500);
            var chrome = Chrome(renderer);
            chrome.Show(PilotPage.Fleet);
            chrome.Fleet.Table.PageWidth = width;
            chrome.Fleet.OnTakeRange = (_, _, _, _) => Task.FromResult<string?>(null);
            var (texts, faults) = Look(chrome.Fleet.Tree(TakeFleet()), renderer, width);

            texts.ShouldContain("Take LE1's");
            texts.ShouldContain("Take RP1's");
            texts.ShouldNotContain("Take SE1's");
            texts.ShouldNotContain("Take MT1's");
            texts.ShouldNotContain(t => t.StartsWith("Push", StringComparison.Ordinal));
            faults.ShouldBeEmpty();
        }

        // No callback, no button.
        using var bare = new RgbaImageRenderer(1400, 1500);
        var plain = Chrome(bare);
        plain.Show(PilotPage.Fleet);
        plain.Fleet.Table.PageWidth = 1400;
        Look(plain.Fleet.Tree(TakeFleet()), bare, 1400).Texts.ShouldNotContain(t => t.StartsWith("Take ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PressingTakeCallsTheViewWithTheRangeAndFleetWhenItsSourceIsTheFleetLayer()
    {
        using var renderer = new RgbaImageRenderer(1600, 1500);
        var chrome = Chrome(renderer);
        var taken = new List<(string Where, string Role, string Range, bool Fleet)>();
        chrome.Fleet.OnTakeRange = (node, role, range, fleet) =>
        {
            lock (taken)
            {
                taken.Add((node.Where, role, range, fleet));
            }
            return Task.FromResult<string?>(null);
        };
        chrome.Show(PilotPage.Fleet);
        var screen = TakeFleet();
        void Press(string where)
        {
            chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, screen, new RectF32(0, 0, 1600, 1500));
            var regions = new List<ClickableRegion>();
            chrome.Fleet.CollectPaintedRegions(regions);
            regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == $"fleet:{where}:take:worker").OnClick!.Invoke(InputModifier.None);
        }

        async Task Count(int n)
        {
            for (var i = 0; i < 200; i++)
            {
                lock (taken)
                {
                    if (taken.Count >= n)
                    {
                        return;
                    }
                }
                await Task.Delay(25, Ct);
            }
            taken.Count.ShouldBeGreaterThanOrEqualTo(n);
        }

        Press("10.0.0.9:47731");
        await Count(1);
        // The first press is over (taken), so the page takes a second.
        await Task.Delay(50, Ct);
        Press("10.0.0.10:47731");
        await Count(2);
        lock (taken)
        {
            taken[0].ShouldBe(("10.0.0.9:47731", "worker", "sonnet-low..opus-low", false));
            taken[1].ShouldBe(("10.0.0.10:47731", "worker", "sonnet-high..opus-medium", true));
        }
    }

    private static NodeInfo InfoWith(string range) => new()
    {
        WireVersion = 6, Version = "2.0.9", MachineName = "DESKTOP-1", MachinePrefix = "SE1", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = "s1",
        Ranges = [new RoleRangeInfo("worker", range, SettingSources.ThisNode)],
    };

    [Fact]
    public async Task AfterATakeThisNodesLineShowsTheTakenRangeAndNoTakeButtonRemains()
    {
        // The node answers its old range at first, then the taken one, as it does once the set went through.
        var answers = new Queue<string>(["opus-low..opus-low", "sonnet-low..opus-low"]);
        await using var feed = new NodeFeed(new FakeNode().Open, "node.sock", nodeInfo: _ => Task.FromResult(InfoWith(answers.Dequeue())));
        await feed.RefreshNodeAsync(Ct);

        FleetScreen Screen() => new(
        [
            FleetLine.From("node.sock", true, true, null, feed.Summary(Live.At)),
            FleetLine.From("10.0.0.9:47731", false, false, null, Summary("s2", "LE1", "LAPTOP-2", new RoleRangeInfo("worker", "sonnet-low..opus-low", SettingSources.ThisNode))),
        ], Live.At);

        using var renderer = new RgbaImageRenderer(1600, 1500);
        var chrome = Chrome(renderer);
        // The same helper both hosts call, its take standing in for the node's client.
        chrome.Fleet.OnTakeRange = (_, _, _, _) => FleetCommands.ThenRefreshAsync(() => Task.FromResult<(string?, string)>((null, "done")), feed, Ct).ContinueWith(t => t.Result.Refused, Ct);
        chrome.Show(PilotPage.Fleet);

        string[] Texts()
        {
            var screen = Screen();
            chrome.Fleet.Table.PageWidth = 1600;
            return Look(chrome.Fleet.Tree(screen), renderer, 1600).Texts;
        }

        Texts().ShouldContain("Take LE1's");
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, Screen(), new RectF32(0, 0, 1600, 1500));
        var regions = new List<ClickableRegion>();
        chrome.Fleet.CollectPaintedRegions(regions);
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == "fleet:10.0.0.9:47731:take:worker").OnClick!.Invoke(InputModifier.None);

        for (var i = 0; i < 200 && answers.Count > 0; i++)
        {
            await Task.Delay(25, Ct);
        }
        var after = Texts();
        after.ShouldContain(t => t.Contains("SE1 · DESKTOP-1: sonnet-low..opus-low"));
        after.ShouldNotContain(t => t.StartsWith("Take ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefreshThatThrowsKeepsTheLastNodeInfoAndARefusedTakeDoesNotRefresh()
    {
        var asked = 0;
        await using var feed = new NodeFeed(new FakeNode().Open, "node.sock", nodeInfo: _ =>
            ++asked == 1 ? Task.FromResult(InfoWith("opus-low..opus-low")) : throw new IOException("node gone"));
        await feed.RefreshNodeAsync(Ct);
        await feed.RefreshNodeAsync(Ct);
        asked.ShouldBe(2);
        feed.Node!.Ranges!.Single().Range.ShouldBe("opus-low..opus-low");

        await FleetCommands.ThenRefreshAsync(() => Task.FromResult<(string?, string)>(("refused", "refused")), feed, Ct);
        asked.ShouldBe(2);
    }

    [Fact]
    public void TakeRangeSendsTheRoleRangeKeyAndFleetOnlyForTheFleetLayer()
    {
        // The args are built from role and range exactly as the Settings page's own set does.
        FleetCommands.TakeArgs("worker", "sonnet-low..opus-low", false).ShouldBe(["worker-range", "sonnet-low..opus-low"]);
        FleetCommands.TakeArgs("planner", "opus..opus-high", true).ShouldBe(["planner-range", "opus..opus-high", "--fleet"]);
    }

    private static IEnumerable<Layout.Node> Descendants(Layout.Node node) => node switch
    {
        Layout.Node.Stack s => s.Children.SelectMany(c => Descendants(c).Prepend(c)),
        Layout.Node.Wrap w => w.Children.SelectMany(c => Descendants(c).Prepend(c)),
        _ => [],
    };

    private static string? TextOf(Layout.Node n) => n is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value.Trim() : null;

    [Fact]
    public void TheFleetRangeRowsKeepTheRoleLeftOfTheFleetLineAndEveryNodeLineInsideItsRow()
    {
        var long1 = "sonnet-low..opus-low";
        RoleRangeInfo[] Ranges(string worker, string planner) =>
        [
            new("watchtower", "opus-high..opus-xhigh", SettingSources.Fleet), new("worker", worker, SettingSources.Fleet),
            new("planner", planner, SettingSources.BuiltIn), new("reviewer", "sonnet-medium..opus-medium", SettingSources.Fleet),
        ];
        var fleet = new FleetScreen(
        [
            FleetLine.From("node.sock", true, true, null, Summary("s1", "SE1", "DESKTOP-1", Ranges("sonnet-high..opus-medium", "opus-medium..opus-high"))),
            FleetLine.From("10.0.0.9:47731", false, false, null, Summary("s2", "LE1", "LAPTOP-2", Ranges(long1, "sonnet-low..opus-xhigh"))),
            FleetLine.From("10.0.0.10:47731", false, false, null, Summary("s3", "RP1", "PI-ROOF", Ranges(long1, "opus-low..opus-low"))),
        ], Live.At);

        foreach (var width in new[] { 700, 1400 })
        {
            foreach (var scale in new[] { 1f, 1.5f })
            {
                var label = $"width {width}, scale {scale}";
                using var renderer = new RgbaImageRenderer((uint)width, 1500);
                var chrome = Chrome(renderer);
                chrome.Show(PilotPage.Fleet);
                chrome.Fleet.Table.PageWidth = width;
                var tree = chrome.Fleet.Tree(fleet);
                var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, 3000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, scale));
                var bounds = new Dictionary<Layout.Node, Rect<float>>(ReferenceEqualityComparer.Instance);
                foreach (var a in arranged)
                {
                    bounds[a.Node] = a.Bounds;
                }

                var rows = arranged.Select(a => a.Node).OfType<Layout.Node.Stack>()
                    .Where(s => s.Axis == Layout.Axis.Horizontal && s.Children.Length == 2 && fleet.Roles.Any(r => TextOf(s.Children[0]) == r.Role))
                    .ToList();
                rows.Count.ShouldBe(4, label);
                var previousBottom = float.MinValue;
                var diag = "";
                foreach (var row in rows)
                {
                    var rowBounds = bounds[row];
                    var role = bounds[row.Children[0]];
                    var texts = Descendants(row.Children[1]).Where(n => TextOf(n) is not null).ToList();
                    var fleetText = texts.Single(n => TextOf(n)!.StartsWith("fleet: ", StringComparison.Ordinal));
                    var fleetLeft = bounds[fleetText].X;
                    var who = $"{label}, {TextOf(row.Children[0])}";
                    (role.X + role.Width).ShouldBeLessThanOrEqualTo(fleetLeft, who);
                    role.X.ShouldBeGreaterThanOrEqualTo(rowBounds.X, who);
                    foreach (var text in texts.Where(n => n != fleetText))
                    {
                        var b = bounds[text];
                        b.X.ShouldBeGreaterThanOrEqualTo(fleetLeft - 0.5f, $"{who}: {TextOf(text)}");
                        b.X.ShouldBeGreaterThanOrEqualTo(rowBounds.X, $"{who}: {TextOf(text)}");
                        (b.Y + b.Height).ShouldBeLessThanOrEqualTo(rowBounds.Y + rowBounds.Height + 0.5f, $"{who}: {TextOf(text)}");
                    }
                    rowBounds.Y.ShouldBeGreaterThanOrEqualTo(previousBottom - 0.5f, who);
                    previousBottom = rowBounds.Y + rowBounds.Height;
                    diag += $" [{TextOf(row.Children[0])} row={rowBounds} role={role} fleetX={fleetLeft}]";
                }
                TextOverlap.Faults(arranged).ShouldBeEmpty(label + diag);
            }
        }
    }
}
