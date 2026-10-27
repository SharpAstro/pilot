using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// One window for the fleet: every node's sessions, flights, jobs and cards on the window's pages, each row naming its
/// node, a node heard twice counted once, and the header's node selection (All nodes, one, several) filtering every page
/// and holding across a page switch.
/// </summary>
public sealed class MergedFleetTests : IAsyncDisposable
{
    private const string Lan = "10.0.0.9:47731";
    private const string LanAgain = "192.168.1.9:47731";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNode _se1 = new();
    private readonly FakeNode _le1 = new();
    private readonly FakeNode _le1Again = new();
    private readonly NodeFeed _here;
    private readonly FleetWatch _watch;

    public MergedFleetTests()
    {
        _here = new NodeFeed(_se1.Open, "node.sock", TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("SE1", "s-se1")));
        _watch = new FleetWatch(endpoint => endpoint == Lan
            ? new NodeFeed(_le1.Open, endpoint, TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("LE1", "s-le1")))
            : new NodeFeed(_le1Again.Open, endpoint, TimeSpan.FromMilliseconds(20), nodeInfo: _ => Task.FromResult(Info("LE1", "s-le1"))));
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

    private static FlightRow Flight(string code, FlightState state) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/" + code.ToLowerInvariant(), Worktree = "/w", Base = "origin/main",
        State = state, Connections = [], CreatedAt = Live.At,
    };

    private static JobRow Job(string id, JobState state) => new()
    {
        Id = id, Command = $"dotnet test {id}", Cwd = "/r", State = state, LogPath = $"/data/jobs/{id}.log", Timeout = TimeSpan.FromHours(1), StartedAt = Live.At,
    };

    private static CardRow Card(string id, string title) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/X", Title = title, At = Live.At, State = CardState.Open,
    };

    /// <summary>SE1 (this window's) with 2 sessions, LE1 on the LAN with 5; both have a session <c>aaaa0001</c> and a job <c>j1</c>.</summary>
    private async Task<FleetPages> TwoNodes(NodeFilter filter, bool heardTwice = false)
    {
        _here.Start();
        _watch.Know(heardTwice ? [(Lan, "LE1 LAPTOP-3"), (LanAgain, null)] : [(Lan, "LE1 LAPTOP-3")]);
        await Until(() => _se1.OpenCount > 0 && _le1.OpenCount > 0 && (!heardTwice || _le1Again.OpenCount > 0));
        _se1.Send(Live.Snapshot(1, "s-se1", Live.Row("aaaa0001", "se1-watchtower", SessionState.Working), Live.Row("bbbb0002", "se1-tia1-gpu")) with
        {
            Flights = [Flight("TIA1", FlightState.Flying)], Jobs = [Job("j1", JobState.Running)], Cards = [Card("c1", "TIA1 landed")],
        });
        SnapshotEvent le1 = Live.Snapshot(1, "s-le1",
            Live.Row("aaaa0001", "le1-watchtower", SessionState.Waiting), Live.Row("cccc0003", "le1-orb2-parser"), Live.Row("dddd0004", "le1-orb2-render"),
            Live.Row("eeee0005", "le1-orb3-ui"), Live.Row("ffff0006", "scratch")) with
        {
            Flights = [Flight("ORB2", FlightState.Flying), Flight("ORB3", FlightState.Held)], Jobs = [Job("j1", JobState.Queued), Job("j2", JobState.Succeeded)],
            Cards = [Card("c1", "ORB2's PR is ready")],
        };
        _le1.Send(le1);
        if (heardTwice)
        {
            _le1Again.Send(le1);
        }
        var pages = _watch.Pages("node.sock", _here, filter, Live.At);
        for (var i = 0; i < 500 && (pages.Sessions.Lines.Count != 7 || !filter.Choices.Select(c => c.Name).SequenceEqual(["SE1", "LE1"])); i++)
        {
            await Task.Delay(10, Ct);
            pages = _watch.Pages("node.sock", _here, filter, Live.At);
        }
        return pages;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task TwoNodesSessionsAreOneListEachNamingItsNodeAndPickingOneLeavesItsOwn()
    {
        var filter = new NodeFilter();
        var pages = await TwoNodes(filter);

        pages.Sessions.Lines.Count.ShouldBe(7);
        pages.Sessions.Lines.Count(l => l.Node?.Name == "SE1").ShouldBe(2);
        pages.Sessions.Lines.Count(l => l.Node?.Name == "LE1").ShouldBe(5);
        pages.Sessions.Lines.ShouldAllBe(l => l.ToDumpLine().StartsWith(l.Node!.Name + " | ", StringComparison.Ordinal));
        filter.Line().ShouldBe("[All nodes] SE1 LE1");
        // Both nodes have a session aaaa0001; the rows stay apart.
        pages.Sessions.Lines.Select(l => l.Key).Distinct().Count().ShouldBe(7);

        filter.Toggle(filter.Choices.Single(c => c.Name == "LE1").Key);
        pages = _watch.Pages("node.sock", _here, filter, Live.At);

        pages.Sessions.Lines.Count.ShouldBe(5);
        pages.Sessions.Lines.ShouldAllBe(l => l.Node!.Name == "LE1");
        filter.Line().ShouldBe("All nodes SE1 [LE1]");
    }

    [Fact]
    public async Task AnUnprefixedSessionShowsItsOwnNodesCodeAndAPrefixedOneIsAsItWas()
    {
        var filter = new NodeFilter();
        await TwoNodes(filter);
        // SE1 gains an unprefixed session of its own, as LE1 already has ("scratch"): each is told by its own node's code.
        _se1.Send(Live.Snapshot(2, "s-se1", Live.Row("aaaa0001", "se1-watchtower", SessionState.Working), Live.Row("bbbb0002", "se1-tia1-gpu"), Live.Row("gggg0007", "stargazer")));
        var pages = _watch.Pages("node.sock", _here, filter, Live.At);
        for (var i = 0; i < 500 && !pages.Sessions.Lines.Any(l => l.Name == "stargazer"); i++)
        {
            await Task.Delay(10, Ct);
            pages = _watch.Pages("node.sock", _here, filter, Live.At);
        }

        string Shown(string name) => pages.Sessions.Lines.Single(l => l.Name == name).ShownName;
        Shown("stargazer").ShouldBe("se1-stargazer");
        Shown("scratch").ShouldBe("le1-scratch");
        Shown("le1-orb2-parser").ShouldBe("le1-orb2-parser");
        Shown("se1-tia1-gpu").ShouldBe("se1-tia1-gpu");
        // This node's own page too, which is where the GUI's single-node list comes from.
        _here.Screen(Live.At).Lines.Single(l => l.Name == "stargazer").ShownName.ShouldBe("se1-stargazer");
    }

    [Fact]
    public async Task AnotherNodesRowsKeepTheirMarkWhileItsStreamIsDown()
    {
        var filter = new NodeFilter();
        await TwoNodes(filter);

        // LE1's stream drops: it is no choice in the header until it is back, but its last rows are still shown.
        _le1.Close();
        var pages = _watch.Pages("node.sock", _here, filter, Live.At);
        for (var i = 0; i < 500 && filter.Choices.Count != 1; i++)
        {
            await Task.Delay(10, Ct);
            pages = _watch.Pages("node.sock", _here, filter, Live.At);
        }
        filter.Choices.ShouldHaveSingleItem().Name.ShouldBe("SE1");

        // This node's rows unmarked, as with no fleet; LE1's still LE1's, so a transcript or a command goes to LE1.
        pages.Sessions.Lines.Where(l => l.Node is null).Select(l => l.Name).ShouldBe(["se1-watchtower", "se1-tia1-gpu"], ignoreOrder: true);
        pages.Sessions.Lines.Where(l => l.Node is not null).ShouldAllBe(l => l.Node!.Name == "LE1");
        pages.Flights.Lines.Single(l => l.Code == "ORB2").Node.ShouldNotBeNull().Name.ShouldBe("LE1");
        // Its cards are not: they leave the panel for one line naming LE1 (UnreachableNodeTests).
        pages.Cards.Open.Select(c => c.Title).ShouldBe(["TIA1 landed"]);
        pages.Cards.Held.ShouldHaveSingleItem().Node.Name.ShouldBe("LE1");
    }

    [Fact]
    public async Task FlightsJobsAndCardsMergeAndFilterTheSameWay()
    {
        var filter = new NodeFilter();
        var pages = await TwoNodes(filter);

        pages.Flights.Lines.Select(l => (l.Node!.Name, l.Code)).ShouldBe([("LE1", "ORB2"), ("SE1", "TIA1"), ("LE1", "ORB3")], ignoreOrder: true);
        pages.Flights.Lines[^1].Code.ShouldBe("ORB3");
        pages.Jobs.Lines.Select(l => l.ToDumpLine().Split(" | ")[0] + " " + l.Id).ShouldBe(["SE1 j1", "LE1 j1", "LE1 j2"]);
        pages.Jobs.Lines.Select(l => l.Key).Distinct().Count().ShouldBe(3);
        pages.Cards.Open.Select(c => $"{pages.Cards.NodeOf(c)!.Name}: {c.Title}").ShouldBe(["SE1: TIA1 landed", "LE1: ORB2's PR is ready"], ignoreOrder: true);

        filter.Toggle(filter.Choices.Single(c => c.Name == "SE1").Key);
        pages = _watch.Pages("node.sock", _here, filter, Live.At);

        filter.Line().ShouldBe("All nodes [SE1] LE1");
        pages.Flights.Lines.ShouldHaveSingleItem().Code.ShouldBe("TIA1");
        pages.Jobs.Lines.ShouldHaveSingleItem().Node!.Name.ShouldBe("SE1");
        pages.Cards.Open.ShouldHaveSingleItem().Title.ShouldBe("TIA1 landed");

        // Several: both picked is both shown.
        filter.Toggle(filter.Choices.Single(c => c.Name == "LE1").Key);
        _watch.Pages("node.sock", _here, filter, Live.At).Sessions.Lines.Count.ShouldBe(7);
        filter.Line().ShouldBe("All nodes [SE1] [LE1]");
    }

    [Fact]
    public async Task APlainClickOnANodeShowsItAloneAndShiftAddsOrTakesOne()
    {
        var filter = new NodeFilter();
        await TwoNodes(filter);
        var se1 = filter.Choices.Single(c => c.Name == "SE1").Key;
        var le1 = filter.Choices.Single(c => c.Name == "LE1").Key;

        filter.Pick(se1, InputModifier.None);
        filter.Line().ShouldBe("All nodes [SE1] LE1");
        // A plain click on the other is that one alone, not both.
        filter.Pick(le1, InputModifier.None);
        filter.Line().ShouldBe("All nodes SE1 [LE1]");
        // Shift adds it to the pick, and takes it out again.
        filter.Pick(se1, InputModifier.Shift);
        filter.Line().ShouldBe("All nodes [SE1] [LE1]");
        filter.Pick(le1, InputModifier.Shift);
        filter.Line().ShouldBe("All nodes [SE1] LE1");
    }

    [Fact]
    public async Task ANodeHeardAtTwoAddressesIsCountedOnce()
    {
        var filter = new NodeFilter();
        await TwoNodes(filter, heardTwice: true);
        // Until the second address has said its stream, it is a node not yet known to be the same one.
        await Until(() => _watch.Feeds("node.sock", _here, Live.At).Count == 2);

        var pages = _watch.Pages("node.sock", _here, filter, Live.At);
        pages.Sessions.Lines.Count.ShouldBe(7);
        filter.Choices.Select(c => c.Name).ShouldBe(["SE1", "LE1"]);
    }

    [Fact]
    public async Task ForYouCardsCarryTheirNode()
    {
        var pages = await TwoNodes(new NodeFilter());
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));

        var described = CellLayout.Describe(Layout.Engine.Arrange(chrome.Cards.Tree(pages.Cards), new Rect<int>(0, 0, 60, 40), CellMeasureContext.PixelAuthored));

        described.ShouldContain("for you · SE1");
        described.ShouldContain("for you · LE1");
        chrome.Cards.NodeOf(pages.Cards.Open.Single(c => c.Title == "ORB2's PR is ready"))!.Where.ShouldBe(Lan);
    }

    [Fact]
    public async Task TheSelectionIsTheWindowsAndSurvivesAPageSwitchAndTheTerminalShowsTheSameRows()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var sessions = new SessionsView<RgbaImage>(vessel);
        var chrome = new PilotChrome<RgbaImage>(vessel, sessions);
        var pages = await TwoNodes(chrome.Nodes);

        // The terminal arranges the page's own tree in cells: the Node column and every node's rows.
        var arranged = Layout.Engine.Arrange(sessions.Tree(pages.Sessions), new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored);
        var described = CellLayout.Describe(arranged);
        described.ShouldContain("Node");
        described.ShouldContain("le1-orb2-parser");
        described.ShouldContain("se1-tia1-gpu");

        // A click on the header's LE1 pill picks it.
        var le1 = chrome.Nodes.Choices.Single(c => c.Name == "LE1").Key;
        var pill = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == $"nodes:{le1}");
        CellLayout.HitTest(arranged, pill.Bounds.X + 1, pill.Bounds.Y);
        chrome.Nodes.Line().ShouldBe("All nodes SE1 [LE1]");

        chrome.Show(PilotPage.Flights);
        pages = _watch.Pages("node.sock", _here, chrome.Nodes, Live.At);
        pages.Flights.Lines.Select(l => l.Code).ShouldBe(["ORB2", "ORB3"]);
        var flightsPage = CellLayout.Describe(Layout.Engine.Arrange(chrome.Flights.Tree(pages.Flights), new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored));
        flightsPage.ShouldContain("ORB2");
        flightsPage.ShouldNotContain("TIA1");
        chrome.Show(PilotPage.Fleet);
        var fleetPage = CellLayout.Describe(Layout.Engine.Arrange(chrome.Fleet.Tree(_watch.Screen("node.sock", _here.Summary(Live.At), _ => false, Live.At)),
            new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored));
        fleetPage.ShouldContain("LAPTOP-3");
        fleetPage.ShouldNotContain("DESKTOP-7");
        chrome.Show(PilotPage.Sessions);

        _watch.Pages("node.sock", _here, chrome.Nodes, Live.At).Sessions.Lines.Count.ShouldBe(5);

        // All nodes brings every row back.
        var all = Layout.Engine.Arrange(sessions.Tree(pages.Sessions), new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored);
        var allPill = all.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "nodes:all" });
        CellLayout.HitTest(all, allPill.Bounds.X + 1, allPill.Bounds.Y);
        _watch.Pages("node.sock", _here, chrome.Nodes, Live.At).Sessions.Lines.Count.ShouldBe(7);
    }

    [Fact]
    public async Task TheSelectionFollowsTheSessionWhenTwoNodesRowsInterleave()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var view = new SessionsView<RgbaImage>(vessel);
        var pages = await TwoNodes(new NodeFilter());
        view.Tree(pages.Sessions);
        // LE1's aaaa0001 (waiting on you: first), not SE1's.
        var le1Watchtower = pages.Sessions.Lines.Single(l => l.Name == "le1-watchtower");
        view.Select(pages.Sessions.Lines.ToList().IndexOf(le1Watchtower));
        view.SelectedId.ShouldBe(le1Watchtower.Key);

        // LE1's watchtower goes idle and sinks below SE1's working rows; the selection goes with it, not with the id.
        _le1.Send(Live.Changed(2, Live.Row("aaaa0001", "le1-watchtower", SessionState.Idle)));
        await Until(() => _watch.Pages("node.sock", _here, new NodeFilter(), Live.At).Sessions.Lines.First().Name != "le1-watchtower");
        var after = _watch.Pages("node.sock", _here, new NodeFilter(), Live.At).Sessions;
        view.Tree(after);

        view.SelectedId.ShouldBe(le1Watchtower.Key);
        after.Lines[view.Selected].Name.ShouldBe("le1-watchtower");
        after.Lines[view.Selected].Node!.Name.ShouldBe("LE1");
    }

    [Fact]
    public void ACommandGoesToItsRowsNodeAndANodeWithoutAGrantIsOnlyRead()
    {
        using var here = NodeClient.Remote("127.0.0.1:1");
        var made = new List<string>();
        using var clients = new FleetClients(here, where => where == "10.0.0.7:47731", where =>
        {
            made.Add(where);
            return NodeClient.Remote(where);
        });

        clients.For(null).ShouldBeSameAs(here);
        clients.For(new NodeMark("s", "node.sock", "SE1", Here: true)).ShouldBeSameAs(here);
        clients.For(new NodeMark("s9", Lan, "LE1", Here: false)).ShouldBeNull();
        clients.For(new NodeMark("s9", Lan, "LE1", Here: false), command: false).ShouldNotBeNull();
        var granted = clients.For(new NodeMark("s7", "10.0.0.7:47731", "NA1", Here: false));
        granted.ShouldNotBeNull();
        clients.For(new NodeMark("s7", "10.0.0.7:47731", "NA1", Here: false)).ShouldBeSameAs(granted);
        made.ShouldBe([Lan, "10.0.0.7:47731"]);
        FleetClients.WatchOnly(new NodeMark("s9", Lan, "LE1", Here: false)).ShouldContain("Connect");
    }
}
