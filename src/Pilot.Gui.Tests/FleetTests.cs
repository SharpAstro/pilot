using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Fleet page: every node at once, each on its own feed and its own account, this window's first; a node heard twice
/// is one row; a node without a grant is watched and offers Connect, any other offers Open.
/// </summary>
public sealed class FleetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeSummary Summary(string stream, string? account = null, bool connected = true, int flying = 0, int rings = 0, int waiting = 0) => new()
    {
        Connected = connected, Status = connected ? "live" : "waiting for the node on 10.0.0.9:47731; retrying every 2s", Stream = stream,
        Account = account, LiveSessions = 3, WaitingOnYou = waiting, Flying = flying, Rings = rings,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream },
    };

    [Fact]
    public void ANodeReadsAsWhoseItIsAndWhatItHas()
    {
        var line = FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, "SE2 DESKTOP-7", Summary("s2", "bo@example.org (Other, max)", flying: 2, rings: 1, waiting: 1));

        line.ToDumpLine().ShouldBe("SE2 · DESKTOP-7 | 10.0.0.9:47731 | live | bo@example.org (Other, max) | - | 3 live · 1 waiting on you | 2 flying | none | 1 ringing | watch only");
        FleetLine.From("10.0.0.9:47731", false, true, "SE2 DESKTOP-7", Summary("s2", connected: false) with { Node = null }).Machine.ShouldBe("SE2 DESKTOP-7");
    }

    [Fact]
    public async Task TheWatchKeepsAFeedPerNodeAndANodeHeardTwiceIsOneRow()
    {
        var nodes = new Dictionary<string, FakeNode>(StringComparer.OrdinalIgnoreCase) { ["10.0.0.9:47731"] = new(), ["10.0.0.5:47731"] = new() };
        await using var watch = new FleetWatch(endpoint => new NodeFeed(nodes[endpoint].Open, endpoint, TimeSpan.FromMilliseconds(20)));

        watch.Know([("10.0.0.9:47731", "SE2 DESKTOP-7"), ("10.0.0.5:47731", null)]);
        watch.Know([("10.0.0.9:47731", "SE2 DESKTOP-7")]);
        for (var i = 0; i < 500 && nodes.Values.Any(n => n.OpenCount == 0); i++)
        {
            await Task.Delay(10, Ct);
        }
        nodes["10.0.0.9:47731"].Send(Live.Snapshot(1, "desktop", Live.Row("a1b2c3d4", "x", SessionState.Waiting)) with
        {
            Usage = new UsageRow { Account = new AccountRow { Email = "bo@example.org" }, ReadAt = Live.At },
        });
        // The laptop's own node, heard on the LAN too: the same stream as this window's.
        nodes["10.0.0.5:47731"].Send(Live.Snapshot(1, "laptop"));
        var screen = watch.Screen("node.sock", Summary("laptop", "ada@example.org"), e => false, Live.At);
        for (var i = 0; i < 500 && (screen.Lines.Count != 2 || !screen.Lines[1].Connected); i++)
        {
            await Task.Delay(10, Ct);
            screen = watch.Screen("node.sock", Summary("laptop", "ada@example.org"), e => false, Live.At);
        }

        screen.Lines.Select(l => (l.Where, l.Here, l.Account)).ShouldBe([("node.sock", true, "ada@example.org"), ("10.0.0.9:47731", false, "bo@example.org")]);
        screen.Lines[1].Sessions.ShouldBe("1 live · 1 waiting on you");
        nodes["10.0.0.9:47731"].OpenCount.ShouldBe(1);
        screen.Summary.ShouldBe("2 nodes · 2 live");
    }

    [Fact]
    public void ARowOffersOpenAndConnectAsItsGrantAllows()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        var opened = new List<string>();
        var asked = new List<string>();
        fleet.OnOpen = line => opened.Add(line.Where);
        fleet.OnConnect = line => asked.Add(line.Where);
        var screen = new FleetScreen(
        [
            FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop", "ada@example.org")),
            FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, null, Summary("desktop", "bo@example.org")),
            FleetLine.From("10.0.0.7:47731", here: false, canCommand: true, null, Summary("nas", "ci@example.org")),
        ], Live.At);

        var arranged = Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        var described = CellLayout.Describe(arranged);
        string[] buttons = [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith("fleet:", StringComparison.Ordinal))];

        described.ShouldContain("bo@example.org");
        described.ShouldContain("watch only");
        buttons.ShouldBe(["fleet:10.0.0.9:47731:open", "fleet:10.0.0.9:47731:connect", "fleet:10.0.0.7:47731:open"]);
        var connect = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "fleet:10.0.0.9:47731:connect" });
        CellLayout.HitTest(arranged, connect.Bounds.X + 1, connect.Bounds.Y);
        asked.ShouldBe(["10.0.0.9:47731"]);
    }
}
