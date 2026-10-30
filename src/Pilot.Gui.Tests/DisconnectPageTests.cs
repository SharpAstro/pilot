using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Remove a held flight's page offers on each wait (a connection on this node or a peer), sending disconnect: none where
/// nothing is waited for or the flight landed, a confirm naming the overlap where the controller's answer made the connection,
/// and the page painted with no text over text.
/// </summary>
public sealed class DisconnectPageTests
{
    private static FlightRow Flight(FlightState state, string[]? connections = null, PeerConnection[]? peers = null, OverlapRow[]? overlaps = null) => new()
    {
        Code = "TIA1611",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1611,
        Title = "Render the planets from the GPU buffer",
        Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main",
        State = state,
        Connections = connections ?? [],
        PeerConnections = peers ?? [],
        Overlaps = overlaps ?? [],
        CreatedAt = Live.At,
    };

    private static PeerConnection Peer(string code, string node = "DE2") => new() { Code = code, Node = node, Since = Live.At };

    private static OverlapRow Answered(string other, OverlapAnswer answer = OverlapAnswer.Connect) => new()
    {
        Other = other, Why = "both rewrite the planet buffer", Files = ["src/Planets.cs"], Answer = answer, At = Live.At,
    };

    private static FlightLine Line(FlightRow flight) => FlightLine.Build([flight], [], Live.At, []).Single();

    private static FlightAction[] Removes(FlightRow flight) => [.. FlightAction.For(Line(flight)).Where(a => a.Verb == Verbs.Disconnect)];

    [Fact]
    public void AFlightHeldForTwoFlightsOffersARemoveForEach()
    {
        var removes = Removes(Flight(FlightState.Held, ["TIA1"], [Peer("PIL9")]));

        removes.Select(a => a.Label).ShouldBe(["Remove wait for TIA1", "Remove wait for PIL9"]);
        removes.ShouldAllBe(a => a.Target == "flight/TIA1611" && a.WhyPrompt == null);
        removes[0].Args.ShouldBe(["TIA1"]);
        removes[1].Args.ShouldBe(["PIL9"]);
        removes.Select(a => a.Key).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void AFlightWithNoConnectionOrThatLandedOffersNone()
    {
        Removes(Flight(FlightState.Flying)).ShouldBeEmpty();
        Removes(Flight(FlightState.Landed, ["TIA1"], [Peer("PIL9")])).ShouldBeEmpty();
    }

    [Fact]
    public void ARemoveOfAnOverlapAnsweredConnectAsksOnceAndSendsAnyway()
    {
        var flight = Flight(FlightState.Held, ["TIA1", "TIA2"], overlaps: [Answered("TIA1"), Answered("TIA2", OverlapAnswer.Separate)]);
        var removes = Removes(flight);

        var asks = removes.Single(a => a.Args[0] == "TIA1");
        asks.WhyPrompt.ShouldNotBeNull().ShouldContain("overlap with TIA1");
        asks.Args.ShouldBe(["TIA1", Verbs.AnywayFlag]);
        removes.Single(a => a.Args[0] == "TIA2").WhyPrompt.ShouldBeNull();

        // The confirm sends the action's own args, whatever was typed, and only the one asked about is open.
        using var renderer = new RgbaImageRenderer(1280, 900);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CanCommand = true };
        var sent = new List<FlightAction>();
        view.OnAction = sent.Add;
        var screen = new FlightsScreen([Line(flight)], "test", Live.At);
        view.Render(screen, new RectF32(0, 0, 1280, 900));
        view.Choose(removes.Single(a => a.Args[0] == "TIA2"), "TIA1611");
        sent.Single().Args.ShouldBe(["TIA2"]);

        sent.Clear();
        view.Choose(asks, "TIA1611");
        sent.ShouldBeEmpty();
        view.WhyInput.Text = "because";
        view.WhyInput.OnCommit!(view.WhyInput.Text).Wait();
        sent.Single().Verb.ShouldBe(Verbs.Disconnect);
        sent.Single().Args.ShouldBe(["TIA1", Verbs.AnywayFlag]);
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(600)]
    public void TheHeldFlightsPageRendersWithNoTextOverText(int width)
    {
        var flight = Flight(FlightState.Held, ["TIA1"], [Peer("PIL9")], [Answered("TIA1")]);
        var line = Line(flight);
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        flights.CanCommand = true;
        flights.OnAction = _ => { };
        var screen = new FlightsScreen([line], "test", Live.At);
        var bounds = new RectF32(0, 0, width, 900);
        flights.Render(screen, bounds);
        flights.OpenDetail(line.Code);
        flights.Render(screen, bounds);
        flights.Choose(FlightAction.For(line).First(a => a.Verb == Verbs.Disconnect), line.Code);

        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width} wide");

        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        regions.Count(r => r.Result is HitResult.ButtonHit b && b.Action.Contains(":disconnect:", StringComparison.Ordinal)).ShouldBe(2);
    }
}
