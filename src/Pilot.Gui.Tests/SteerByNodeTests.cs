using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A command from the merged lists goes to its row's own node, not the window's: a fake node per address records what it is
/// sent. A node this user holds no grant for (or whose grant it refused) is watch-only: its actions are disabled with the
/// Connect hint and a Connect pill, and nothing is sent.
/// </summary>
public sealed class SteerByNodeTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RecordingNode _se1 = new(HttpStatusCode.OK);
    private readonly RecordingNode _le1 = new(HttpStatusCode.OK);
    private readonly List<RecordingNode> _others = [];

    public ValueTask DisposeAsync()
    {
        _se1.Dispose();
        _le1.Dispose();
        foreach (var other in _others)
        {
            other.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private static NodeMark Se1 => new("node.sock", "node.sock", "SE1", Here: true);

    private NodeMark Le1 => new(_le1.Endpoint, _le1.Endpoint, "LE1", Here: false);

    private static SessionsScreen Screen(NodeMark se1, NodeMark le1) => new(
        SessionLine.Order([
            SessionLine.From(Live.Row("aaaa0001", "se1-watchtower", SessionState.Working), Live.At) with { Node = se1 },
            SessionLine.From(Live.Row("aaaa0001", "le1-watchtower", SessionState.Working), Live.At) with { Node = le1 },
        ]), "test", Live.At);

    private static (System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Arranged, string Text) Arrange(SessionsView<RgbaImage> view, SessionsScreen screen)
    {
        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        return (arranged, CellLayout.Describe(arranged));
    }

    [Fact]
    public async Task ASayToAnLe1RowFromSe1sWindowGoesToLe1sClientAndNotSe1s()
    {
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, where => where == _le1.Endpoint, e => NodeClient.Remote(e));
        using var vessel = new RgbaImageRenderer(1, 1);
        var view = new SessionsView<RgbaImage>(vessel) { CanSay = true, CanSteer = clients.CanSteer };
        view.OnSay = async (line, text) => view.SayStatus = await GuiSay.SendAsync(clients.For(line.Node)!, line.Row.Id, text, Ct);
        var screen = Screen(Se1, Le1);
        view.Tree(screen);
        view.Select(screen.Lines.ToList().FindIndex(l => l.Node!.Name == "LE1"));

        view.SayInput.Text = "hello le1";
        await view.SayInput.OnCommit.ShouldNotBeNull()(view.SayInput.Text);

        _le1.Commands.Count.ShouldBe(1);
        _le1.Commands[0].ShouldContain("hello le1");
        _le1.Commands[0].ShouldContain("aaaa0001");
        _se1.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutAGrantTheSayBarAndRowActionsAreDisabledWithTheConnectHintAndAPill()
    {
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, _ => false, e => NodeClient.Remote(e));
        using var vessel = new RgbaImageRenderer(1, 1);
        var asked = new List<string>();
        var said = new List<string>();
        var view = new SessionsView<RgbaImage>(vessel) { CanSay = true, CanSteer = clients.CanSteer, OnConnect = node => asked.Add(node.Where), OnSay = (_, text) => { said.Add(text); return Task.CompletedTask; } };
        var screen = Screen(Se1, Le1);
        view.Tree(screen);
        view.Select(screen.Lines.ToList().FindIndex(l => l.Node!.Name == "LE1"));

        var (arranged, text) = Arrange(view, screen);
        text.ShouldContain("Connect to LE1 to steer");
        var connect = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && action == $"sessions:connect:{_le1.Endpoint}");
        view.SayInput.Text = "ignored";
        await view.SayInput.OnCommit.ShouldNotBeNull()(view.SayInput.Text);
        said.ShouldBeEmpty();
        view.SayStatus.ShouldBe("Connect to LE1 to steer");
        CellLayout.HitTest(arranged, connect.Bounds.X + 1, connect.Bounds.Y);
        asked.ShouldBe([_le1.Endpoint]);
        _le1.Commands.ShouldBeEmpty();

        // SE1's own row says as ever.
        view.Select(screen.Lines.ToList().FindIndex(l => l.Node!.Name == "SE1"));
        view.SayStatus = null;
        Arrange(view, screen).Text.ShouldNotContain("Connect to");
    }

    [Fact]
    public void AnEndedRowOnANodeWithNoGrantOffersNeitherArchiveNorRestore()
    {
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, _ => false, e => NodeClient.Remote(e));
        using var vessel = new RgbaImageRenderer(1, 1);
        var view = new SessionsView<RgbaImage>(vessel) { CanSteer = clients.CanSteer, OnAction = _ => { }, OnConnect = _ => { } };
        var screen = new SessionsScreen(SessionLine.Order([
            SessionLine.From(Live.Row("aaaa0002", "le1-done", SessionState.Ended), Live.At) with { Node = Le1 },
        ]), "test", Live.At);
        view.Tree(screen);
        view.Select(0);

        var (arranged, _) = Arrange(view, screen);
        var actions = arranged.Select(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } ? action : null).OfType<string>().ToList();
        actions.ShouldNotContain(a => a.EndsWith(":archive") || a.EndsWith(":restore"));
    }

    private static FlightLine FlightOn(string code, NodeMark node) => FlightLine.Build([new FlightRow
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/" + code.ToLowerInvariant(), Worktree = "/w", Base = "origin/main",
        State = FlightState.Flying, Connections = [], CreatedAt = Live.At,
    }], [], Live.At).Single() with { Node = node };

    private static CardRow CardRow(string id) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/X", Title = "landed " + id, At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Merge", Verb = "answer", Target = "card/x" }],
    };

    [Fact]
    public void AFlightRowAndACardOnANodeWithNoGrantAreWatchOnlyWithTheHintAndAConnectPill()
    {
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, _ => false, e => NodeClient.Remote(e));
        using var vessel = new RgbaImageRenderer(1280, 800);
        var asked = new List<string>();
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.CanCommand = true;
        chrome.Flights.CanSteer = clients.CanSteer;
        chrome.Flights.OnConnect = node => asked.Add(node.Where);
        chrome.Cards.CanSteer = clients.CanSteer;
        chrome.Cards.OnConnect = node => asked.Add(node.Where);
        var se1Flight = FlightOn("SE11", Se1);
        var le1Flight = FlightOn("LE11", Le1);
        var flights = new FlightsScreen([se1Flight, le1Flight], "test", Live.At);
        var cardsScreen = CardsScreen.Merge([(Se1, CardsScreen.From([CardRow("cse")])), (Le1, CardsScreen.From([CardRow("cle")]))], null);

        List<string> Buttons()
        {
            var regions = new List<ClickableRegion>();
            chrome.Flights.CollectPaintedRegions(regions);
            chrome.Cards.CollectPaintedRegions(regions);
            return [.. regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action)];
        }

        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Live.At), flights, cardsScreen, new RectF32(0, 0, 1280, 800));
        var first = Buttons();

        // Cards: SE1's keep Merge and Dismiss, LE1's have neither, and a Connect.
        first.ShouldContain("card:cse:1");
        first.ShouldContain("card:cse:dismiss");
        first.ShouldNotContain("card:cle:1");
        first.ShouldNotContain("card:cle:dismiss");
        first.ShouldContain("card:cle:connect");
        first.ShouldNotContain("card:cse:connect");

        void Select(string code)
        {
            chrome.Flights.ListCursor.MoveTo(flights.Lines.ToList().FindIndex(l => l.Code == code));
            chrome.Render(new SessionsScreen([], "test", Live.At), flights, cardsScreen, new RectF32(0, 0, 1280, 800));
        }

        Select("SE11");
        var se1Buttons = Buttons();
        se1Buttons.ShouldContain("flight:SE11:plan-leg");
        se1Buttons.ShouldNotContain("flight:SE11:connect");

        Select("LE11");
        var le1Buttons = Buttons();
        le1Buttons.ShouldNotContain("flight:LE11:plan-leg");
        le1Buttons.ShouldContain("flight:LE11:connect");
        var arranged = Layout.Engine.Arrange(chrome.Flights.Tree(flights), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        CellLayout.Describe(arranged).ShouldContain("Connect to LE1 to steer");
        var regions2 = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions2);
        regions2.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:LE11:connect" }).OnClick!.Invoke(InputModifier.None);
        asked.ShouldBe([_le1.Endpoint]);
    }

    [Fact]
    public async Task AWhyAskedOnAnLe1FlightRowGoesWithItsVerbToLe1sEndpointNotThisWindowsNodeAndPlanALegGoesToTheChat()
    {
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, where => where == _le1.Endpoint, e => NodeClient.Remote(e));
        using var vessel = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.CanCommand = true;
        chrome.Flights.CanSteer = clients.CanSteer;
        var acted = new List<FlightAction>();
        chrome.Flights.OnAction = acted.Add;
        var le1Flight = FlightLine.Build([FlightOn("LE11", Le1).Row], [], Live.At,
            [new LegRow { Flight = "LE11", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying }]).Single() with { Node = Le1 };
        var flights = new FlightsScreen([FlightOn("SE11", Se1), le1Flight], "test", Live.At);
        chrome.Show(PilotPage.Flights);
        chrome.Flights.ListCursor.MoveTo(1);
        chrome.Render(new SessionsScreen([], "test", Live.At), flights, CardsScreen.From([]), new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:LE11:go-around" }).OnClick!.Invoke(InputModifier.None);
        acted.ShouldBeEmpty("a go-around asks why first");
        chrome.Flights.Tree(flights);
        await chrome.Flights.WhyInput.OnCommit.ShouldNotBeNull()("it went in circles");

        // The action carries its row's node: this window's client for none, LE1's through the grant, and the why as its one argument.
        var action = acted.ShouldHaveSingleItem();
        action.Node.ShouldNotBeNull().Where.ShouldBe(_le1.Endpoint);
        action.Args.ShouldBe(["it went in circles"]);
        (await DashboardCommands.ActAsync(clients.For(action.Node).ShouldNotBeNull(), action, "gui", Ct)).ShouldBe("ok: Redo leg parser");
        _le1.Commands.Count.ShouldBe(1);
        _le1.Commands[0].ShouldContain("go-around");
        _le1.Commands[0].ShouldContain("it went in circles");
        _se1.Commands.ShouldBeEmpty();

        // Plan a leg is said to this window's watchtower, whose pilot runs against this window's node: another node's flight has
        // no such button, and this node's flight (SE11) has one that goes to the chat and sends nothing to a node.
        chrome.Render(new SessionsScreen([], "test", Live.At), flights, CardsScreen.From([]), new RectF32(0, 0, 1280, 800));
        regions.Clear();
        chrome.Flights.CollectPaintedRegions(regions);
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: "flight:LE11:plan-leg" }).ShouldBeFalse();
        chrome.Flights.ListCursor.MoveTo(0);
        chrome.Render(new SessionsScreen([], "test", Live.At), flights, CardsScreen.From([]), new RectF32(0, 0, 1280, 800));
        regions.Clear();
        chrome.Flights.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:SE11:plan-leg" }).OnClick!.Invoke(InputModifier.None);
        chrome.Page.ShouldBe(PilotPage.Chat);
        chrome.Chat.Input.Text.ShouldBe("plan a leg on SE11: ");
        acted.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ANodeThatRefusesTheGrantAsUnauthorizedReadsAsWatchOnlyUntilANewGrant()
    {
        using var refusing = new RecordingNode(HttpStatusCode.Forbidden);
        _others.Add(refusing);
        var token = "old";
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, where => where == refusing.Endpoint, e => NodeClient.Remote(e, token), _ => token);
        var node = new NodeMark(refusing.Endpoint, refusing.Endpoint, "LE1", Here: false);

        clients.CanSteer(node).ShouldBeTrue();
        var said = await GuiSay.SendAsync(clients.For(node)!, "aaaa0001", "hi", Ct, envelope => clients.NoteRefusal(node, envelope));
        said.ShouldContain("needs a grant");
        clients.CanSteer(node).ShouldBeFalse();
        clients.For(node).ShouldBeNull();

        clients.Refresh();
        clients.CanSteer(node).ShouldBeFalse();
        token = "new";
        clients.Refresh();
        clients.CanSteer(node).ShouldBeTrue();
        clients.CanSteer(Se1).ShouldBeTrue();

        // The node now accepts, and the say goes out carrying the new token, not the refused one.
        refusing.Status = HttpStatusCode.OK;
        var sent = await GuiSay.SendAsync(clients.For(node)!, "aaaa0001", "again", Ct, envelope => clients.NoteRefusal(node, envelope));
        sent.ShouldNotContain("needs a grant");
        refusing.Auths.Last().ShouldBe("Bearer new");
        clients.CanSteer(node).ShouldBeTrue();
    }

    [Fact]
    public async Task ATokenSavedAfterATranscriptWasReadIsCarriedByTheNextSay()
    {
        string? token = null;
        var held = false;
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, _ => held, e => NodeClient.Remote(e, token), _ => token);
        var node = new NodeMark(_le1.Endpoint, _le1.Endpoint, "LE1", Here: false);

        // Reading with no grant caches a client built without a token.
        clients.For(node, command: false).ShouldNotBeNull();
        clients.For(node).ShouldBeNull();

        // Connect saves a token; the next say must not reuse the tokenless client.
        token = "fresh";
        held = true;
        clients.Refresh();
        var said = await GuiSay.SendAsync(clients.For(node)!, "aaaa0001", "hello", Ct, envelope => clients.NoteRefusal(node, envelope));
        said.ShouldNotContain("needs a grant");
        _le1.Auths.Last().ShouldBe("Bearer fresh");
        clients.CanSteer(node).ShouldBeTrue();

        // Forget then Connect again with another token: the old token is not sent.
        token = null;
        held = false;
        clients.Refresh();
        token = "second";
        held = true;
        await GuiSay.SendAsync(clients.For(node)!, "aaaa0001", "once more", Ct, envelope => clients.NoteRefusal(node, envelope));
        _le1.Auths.Last().ShouldBe("Bearer second");
    }

    /// <summary>The Fleet row's Maintenance on / off is <c>set node maintenance on|off</c> to that row's own node, under its grant; a node without a grant is sent nothing.</summary>
    [Fact]
    public async Task AFleetRowsMaintenanceToggleIsSetNodeMaintenanceOnThatNodeUnderItsGrant()
    {
        using var here = NodeClient.Remote(_se1.Endpoint);
        using var clients = new FleetClients(here, where => where == _le1.Endpoint, e => NodeClient.Remote(e, "tok-le1"), _ => "tok-le1");
        var le1 = FleetLine.From(_le1.Endpoint, here: false, canCommand: true, null, Summary());
        var se2 = FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, null, Summary());

        var said = await FleetCommands.SetMaintenanceAsync(clients, le1, on: true, "gui", Ct);
        await FleetCommands.SetMaintenanceAsync(clients, le1, on: false, "gui", Ct);
        var watchOnly = await FleetCommands.SetMaintenanceAsync(clients, se2, on: true, "gui", Ct);

        said.ShouldBe("SE1 · DESKTOP-7: done");
        _le1.Commands.Count.ShouldBe(2);
        _le1.Commands.ShouldAllBe(c => c.Contains("\"set\"", StringComparison.Ordinal) && c.Contains("maintenance", StringComparison.Ordinal));
        _le1.Commands[0].ShouldContain("\"on\"");
        _le1.Commands[1].ShouldContain("\"off\"");
        _le1.Auths.ShouldAllBe(a => a == "Bearer tok-le1");
        _se1.Commands.ShouldBeEmpty();
        watchOnly.ShouldContain("Connect to");
    }

    /// <summary>The header switch's click is <c>set node autopilot on|off</c> to the window's node, as the GUI (#208).</summary>
    [Fact]
    public async Task TheAutopilotSwitchSendsSetAutopilotToThisNodeAsGui()
    {
        using var here = NodeClient.Remote(_se1.Endpoint);

        var on = await AutopilotCommands.SetAsync(here, on: true, GuiSay.Who, Ct);
        var off = await AutopilotCommands.SetAsync(here, on: false, GuiSay.Who, Ct);

        on.ShouldBe("autopilot on: done");
        off.ShouldBe("autopilot off: done");
        _se1.Commands.Count.ShouldBe(2);
        _se1.Commands.ShouldAllBe(c => c.Contains("\"set\"", StringComparison.Ordinal) && c.Contains("autopilot", StringComparison.Ordinal) && c.Contains("\"gui\"", StringComparison.Ordinal));
        _se1.Commands[0].ShouldContain("\"on\"");
        _se1.Commands[1].ShouldContain("\"off\"");
        _le1.Commands.ShouldBeEmpty();
    }

    [Fact]
    public void TheFleetPageOffersNoOpenUnlessAHostSetsIt()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var fleet = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)).Fleet;
        fleet.OnOpen.ShouldBeNull();
        var screen = new FleetScreen(
        [
            FleetLine.From("node.sock", here: true, canCommand: true, null, Summary()),
            FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, null, Summary()),
        ], Live.At);
        fleet.OnConnect = _ => { };

        var arranged = Layout.Engine.Arrange(fleet.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        string[] buttons = [.. arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith("fleet:", StringComparison.Ordinal) && !a.StartsWith("fleet:table:", StringComparison.Ordinal))];

        buttons.ShouldBe(["fleet:10.0.0.9:47731:connect"]);
    }

    private static NodeSummary Summary() => new()
    {
        Connected = true, Status = "live", Stream = "s", Account = null, LiveSessions = 1, WaitingOnYou = 0, Flying = 0, Rings = 0,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE1", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = "s" },
    };

    /// <summary>A node on loopback that records the commands posted to it and answers each with <paramref name="status"/>.</summary>
    private sealed class RecordingNode : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public RecordingNode(HttpStatusCode status)
        {
            Status = status;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = $"127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                    using var body = new StreamReader(context.Request.InputStream);
                    var text = await body.ReadToEndAsync();
                    var status = Status;
                    lock (Commands)
                    {
                        Commands.Add(text);
                        Auths.Add(context.Request.Headers["Authorization"]);
                    }
                    context.Response.StatusCode = (int)status;
                    var answer = status == HttpStatusCode.OK
                        ? ResponseEnvelope<CommandResult>.Ok(new CommandResult { Verb = "say", Target = "", Who = "gui", Ok = true, Seq = 1 })
                        : ResponseEnvelope<CommandResult>.Fail("a command from the LAN needs a grant", (int)status);
                    await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(answer, PilotJsonContext.Default.ResponseEnvelopeCommandResult));
                    context.Response.Close();
                }
            });
        }

        public string Endpoint { get; }

        public List<string> Commands { get; } = [];

        /// <summary>The Authorization header of each request, null when none was sent.</summary>
        public List<string?> Auths { get; } = [];

        public HttpStatusCode Status { get; set; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
