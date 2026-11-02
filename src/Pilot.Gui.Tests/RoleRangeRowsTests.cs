using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
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

            var (texts, faults) = Look(chrome.Settings.Tree(DateTimeOffset.UtcNow), renderer, width);

            texts.ShouldContain("set on this node");
            texts.ShouldContain("default");
            texts.ShouldContain("max");
            texts.ShouldContain("S high");
            texts.ShouldContain("O xhigh");
            texts.ShouldContain(t => t.Contains("the fleet's range is sonnet-medium..opus-xhigh"));
            texts.ShouldContain("differs");
            texts.ShouldContain("LAPTOP-2");
            faults.ShouldBeEmpty();
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
                }
                TextOverlap.Faults(arranged).ShouldBeEmpty(label);
            }
        }
    }
}
