using DIR.Lib;
using Pilot.Contracts;
using Pilot.Gui;
using SdlVulkan.Renderer;
using SharpAstro.AppShell;
using static SDL3.SDL;

// pilot v2's GUI: every session on the machine as a row, live from the node, and a bar to say something to the
// selected one. --snapshot shows a file instead (the bundled sample is the contract's); --say says one thing and
// exits.
ConsoleOutput.AttachToParent();

if (GuiOptions.Parse(args, out var error) is not { } options)
{
    Console.Error.WriteLine(error);
    return 2;
}

var socketPath = NodeSocket.Resolve(options.NodeSocket);
// A node on the LAN (--node, else PILOT_NODE) is only connected to, with the grant pilot connect saved for it; this
// machine's node is started when none runs, as before.
var nodeTarget = options.Node ?? (Environment.GetEnvironmentVariable(Pilot.Commands.NodeTargets.EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : null);
var remoteEndpoint = nodeTarget is not null && NodeClient.IsEndpoint(nodeTarget, out var endpoint) ? endpoint : null;
NodeClient NewClient() => Pilot.Commands.NodeTargets.Connect(remoteEndpoint, options.NodeSocket);
string[] NodeArgs() => remoteEndpoint is { } remote ? ["--node", remote] : ["--node-socket", socketPath];
var nodeWhere = remoteEndpoint ?? socketPath;
// A socket the user named is only connected to; the default one gets a node started on it when none runs.
var namedSocket = options.NodeSocket is not null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NodeSocket.EnvironmentVariable));

async Task EnsureNodeAsync()
{
    if (remoteEndpoint is not null)
    {
        return;
    }
    var (outcome, message) = await NodeLauncher.FindOrStartAsync(socketPath, namedSocket, CancellationToken.None);
    if (outcome != NodeLaunch.Found)
    {
        Console.Error.WriteLine($"pilot-gui: {message}");
    }
}

if (options.SayTo is { } sayTo)
{
    await EnsureNodeAsync();
    using var oneShot = NewClient();
    return await GuiSay.RunAsync(oneShot, sayTo, options.SayText ?? "", Console.Out, CancellationToken.None);
}

SessionsScreen screen;
SnapshotEvent? snapshot = null;
FlightsScreen? capturedFlights = null;
CardsScreen? capturedCards = null;
if (options.Snapshot is { } snapshotFile)
{
    var snapshotPath = Path.GetFullPath(snapshotFile);
    try
    {
        snapshot = SessionsCapture.LoadSnapshot(snapshotPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
    {
        Console.Error.WriteLine($"pilot-gui: cannot read the snapshot {snapshotPath}: {ex.Message}");
        return 1;
    }

    screen = SessionsCapture.ScreenFor(snapshot, $"snapshot file {Path.GetFileName(snapshotPath)}");
}
else if (options.Capture is not null)
{
    // A capture of the live node is the rows as of now.
    await EnsureNodeAsync();
    using var oneShot = NewClient();
    try
    {
        var rows = await oneShot.GetSessionsAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        screen = new SessionsScreen(SessionLine.Build(rows, now), $"live from the node on {nodeWhere}", now);
        // The flights and the cards are on the stream's snapshot, which a capture reads once.
        await using (var events = await oneShot.OpenEventsAsync(since: null, stream: null, CancellationToken.None))
        {
            if (await events.ReadAsync(CancellationToken.None) is SnapshotEvent live)
            {
                capturedFlights = new FlightsScreen(FlightLine.Build(live.Flights, live.Sessions, now, live.Legs), screen.Source, now);
                capturedCards = CardsScreen.From(live.Cards);
            }
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException)
    {
        Console.Error.WriteLine($"pilot-gui: no node answers on {socketPath} ({ex.Message}); start pilot-node, or pass --snapshot <file>");
        return 1;
    }
}
else
{
    screen = new SessionsScreen([], $"connecting to the node on {nodeWhere}", DateTimeOffset.UtcNow);
}

if (options.Capture is { } capturePath)
{
    capturedFlights ??= snapshot is null ? null : new FlightsScreen(FlightLine.Build(snapshot.Flights, snapshot.Sessions, snapshot.At, snapshot.Legs), screen.Source, snapshot.At);
    capturedCards ??= snapshot is null ? null : CardsScreen.From(snapshot.Cards);
    var rowsPath = SessionsCapture.Write(screen, capturePath, options.Width, options.Height, canSay: snapshot is null, flights: capturedFlights, page: options.Page, cards: capturedCards);
    Console.WriteLine($"pilot-gui: rendered {screen.Summary} at {options.Width}x{options.Height} ({screen.Source})");
    Console.WriteLine($"pilot-gui: wrote {Path.GetFullPath(capturePath)} and {Path.GetFullPath(rowsPath)}");
    return 0;
}

// One window per user: a second launch raises the first one and leaves.
var gateChannel = InstanceGate.ChannelFor("pilot-v2-gui");
using var gate = InstanceGate.TryClaim(gateChannel);
if (gate is null)
{
    if (InstanceGate.TryHandOff(gateChannel, string.Empty, TimeSpan.FromSeconds(5)))
    {
        Console.WriteLine("pilot-gui: pilot v2's window is already open; raised it instead of opening another.");
        return 0;
    }

    Console.WriteLine("pilot-gui: another pilot v2 window holds the gate but did not answer; opening one anyway.");
}

const string Title = "pilot v2 · sessions";
using var window = SdlVulkanWindow.Create(Title, 1280, 800);
// The window's icon (the exe's is the ApplicationIcon): a missing or unreadable file leaves the platform's default.
if (WindowIcon.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "pilot-256.png")) is { } icon)
{
    window.SetIcon(icon);
}
window.GetSizeInPixels(out var width, out var height);
using var context = VulkanContext.Create(window.Instance, window.Surface, (uint)width, (uint)height);
using var renderer = new VkRenderer(context, (uint)width, (uint)height);
var view = new SessionsView<VulkanContext>(renderer);
var chrome = new PilotChrome<VulkanContext>(renderer, view) { FontPath = SessionsCapture.FontPath, DpiScale = window.DisplayScale };

using var cts = new CancellationTokenSource();
var tracker = new BackgroundTaskTracker();
var loop = new SdlEventLoop(window, renderer) { BackgroundColor = new RGBAColor32(0x12, 0x12, 0x18, 0xff) };
var router = new InputRouter(view.Ui, tracker, loop.RequestRedraw)
{
    Widgets = () => chrome.Widgets,
    Unhandled = view.HandleInput,
};

// The platform's text input follows the field that has the keyboard, as tianwen's GUI binds it: once, here.
view.Ui.Focus.FocusChanged += (_, next) =>
{
    if (next is null)
    {
        StopTextInput(window.Handle);
    }
    else
    {
        StartTextInput(window.Handle);
    }
    loop.RequestRedraw();
};

// Live: the node's stream feeds the rows, and the say bar talks through the same node.
using var client = snapshot is null ? NewClient() : null;
// Not `await using`: Main is async (--say and --capture await), and an await here would resume the rest of Main,
// the SDL window's teardown included, on a thread-pool thread, where closing the window hung. The feed is drained
// with the loop pumping instead (ShutdownDrain), so the teardown stays on this thread.
var feed = client is null ? null : NodeFeed.For(client);
if (feed is not null && client is not null)
{
    view.CanSay = true;
    view.OnSay = async (line, text) =>
    {
        view.SayStatus = $"saying to {line.Name}…";
        loop.RequestRedraw();
        var result = await GuiSay.SendAsync(client, line.Row.Id, text, cts.Token);
        view.SayStatus = result;
        Console.WriteLine($"pilot-gui: say to {line.Name}: {result}");
        loop.RequestRedraw();
    };
    // A card's button is a command on the node, as a click anywhere else is; the stream closes the card.
    async Task CardCommandAsync(string verb, CardRow card, string[] args)
    {
        var result = await client.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.CardTarget(card.Id), Args = args, Who = GuiSay.Who }, cts.Token);
        Console.WriteLine($"pilot-gui: {verb} {card.Id} ({card.Title}): {(result.Success ? "done" : result.Response?.Error ?? result.Error)}");
        loop.RequestRedraw();
    }
    chrome.Cards.OnAnswer = (card, number) => _ = CardCommandAsync(Verbs.Answer, card, [number.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    chrome.Cards.OnDismiss = card => _ = CardCommandAsync(Verbs.Dismiss, card, []);

    // The watchtower, from its panel: told things through the node, its Remote Control link one click away.
    chrome.Cards.CanTell = true;
    chrome.Cards.OnTell = async text =>
    {
        chrome.Cards.TellStatus = "telling the watchtower…";
        loop.RequestRedraw();
        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = [text], Who = GuiSay.Who }, cts.Token);
        chrome.Cards.TellStatus = envelope.Success && envelope.Response is { Ok: true } ? "told" : $"refused: {envelope.Response?.Error ?? envelope.Error}";
        Console.WriteLine($"pilot-gui: tell watchtower: {chrome.Cards.TellStatus}");
        loop.RequestRedraw();
    };
    chrome.Cards.OnOpen = url => DashboardCommands.Open(url);

    // The Flights page flies a flight from here alone: its buttons are verbs, its command bar the CLI's own commands.
    chrome.Flights.CanCommand = true;
    chrome.Flights.OnCommand = async line =>
    {
        chrome.Flights.Say($"> {line}");
        loop.RequestRedraw();
        var said = await DashboardCommands.RunAsync(line, NodeArgs(), GuiSay.Who, cts.Token);
        chrome.Flights.Say(said);
        Console.WriteLine($"pilot-gui: command `{line}`: {said.ReplaceLineEndings(" / ")}");
        loop.RequestRedraw();
    };
    chrome.Flights.OnAction = action => _ = Task.Run(async () =>
    {
        var said = await DashboardCommands.ActAsync(client, action, GuiSay.Who, cts.Token);
        chrome.Flights.Say(said);
        Console.WriteLine($"pilot-gui: {said}");
        loop.RequestRedraw();
    });
    chrome.Flights.OnOpen = location =>
    {
        if (!DashboardCommands.Open(location))
        {
            chrome.Flights.Say($"could not open {location}");
        }
    };
    feed.Start();

    // The window opens at once and the feed connects when the node answers; a node is started meanwhile if none runs.
    _ = remoteEndpoint is not null ? Task.CompletedTask : Task.Run(async () =>
    {
        var (outcome, message) = await NodeLauncher.FindOrStartAsync(socketPath, namedSocket, CancellationToken.None);
        if (outcome != NodeLaunch.Found)
        {
            Console.Error.WriteLine($"pilot-gui: {message}");
        }
        if (outcome is NodeLaunch.NamedSocketSilent or NodeLaunch.NoNodeBinary or NodeLaunch.DidNotStart)
        {
            feed.Problem = message;
        }
    });
}

var announced = (string?)null;
var drawnVersion = -1L;
var drawnAt = DateTimeOffset.MinValue;
CardsScreen? cards = snapshot is null ? null : CardsScreen.From(snapshot.Cards);
FlightsScreen? flights = snapshot is null ? null : new FlightsScreen(FlightLine.Build(snapshot.Flights, snapshot.Sessions, snapshot.At, snapshot.Legs), screen.Source, snapshot.At);
loop.OnResize = (w, h) =>
{
    width = (int)w;
    height = (int)h;
    chrome.DpiScale = window.DisplayScale;
};
loop.OnRender = () =>
{
    if (feed is not null)
    {
        drawnVersion = feed.Version;
        drawnAt = DateTimeOffset.UtcNow;
        screen = feed.Screen(drawnAt);
        flights = feed.Flights(drawnAt);
        cards = feed.Cards();
    }

    view.Ui.FrameId++;
    chrome.Render(screen, flights, cards, new RectF32(0, 0, width, height));
    router.AfterPaint();

    // Say which session is selected whenever it changes, so stdout follows what the window shows.
    if (view.SelectedId != announced && view.Selected >= 0 && view.Selected < screen.Lines.Count)
    {
        announced = view.SelectedId;
        Console.WriteLine($"pilot-gui: selected {screen.Lines[view.Selected].ToDumpLine()}");
    }
};
loop.CheckNeedsRedraw = () =>
{
    while (gate?.TryDequeue(out _) == true)
    {
        window.Activate();
        Console.WriteLine("pilot-gui: another launch asked for this window; raised it.");
        return true;
    }

    // A new event from the node, or a second gone by for the "ago" column.
    if (feed is not null && (feed.Version != drawnVersion || DateTimeOffset.UtcNow - drawnAt >= TimeSpan.FromSeconds(1)))
    {
        return true;
    }

    return renderer.FontAtlasDirty || router.TooltipNeedsFrame;
};
#if PILOT_DEBUG_INSPECTOR
// The live UI inspector (Debug builds against sibling source only): an agent finds this window, reads its regions
// and screenshots the real swapchain, which the CPU --capture never shows.
using var inspector = DebugInspector.Attach(loop, new DebugInspectorOptions
{
    AppName = "pilot v2",
    WindowTitle = () => Title,
    GetRegions = () =>
    {
        var regions = new List<ClickableRegion>();
        foreach (var widget in chrome.Widgets)
        {
            widget.CollectPaintedRegions(regions);
        }
        return regions;
    },
});
#endif

loop.OnKeyDown = key => router.Handle(key);
loop.OnTextInput = text => router.Handle(new InputEvent.TextInput(text));
loop.OnPointerInput = evt =>
{
    if (evt is InputEvent.MouseMove move)
    {
        chrome.Pointer = (move.X, move.Y);
    }
    return router.Handle(evt);
};

Console.WriteLine($"pilot-gui: window \"{Title}\" open (pid {Environment.ProcessId}), {width}x{height} px at scale {window.DisplayScale:0.##}");
Console.WriteLine(snapshot is null
    ? $"pilot-gui: live from the node on {nodeWhere}"
    : $"pilot-gui: showing {screen.Summary} from {screen.Source}, as of {snapshot.At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
Console.WriteLine(snapshot is null
    ? "pilot-gui: Up/Down or a click select a session; type in the bar at the foot and Enter says it; close the window to quit."
    : "pilot-gui: Up/Down select a row; close the window to quit.");
if (options.ExitAfter is { } after)
{
    Console.WriteLine($"pilot-gui: closing by itself after {after.TotalSeconds:0.##}s (--exit-after)");
    cts.CancelAfter(after);
}

loop.Run(cts.Token);
if (feed is not null)
{
    ShutdownDrain.PumpUntilComplete(loop, feed.DisposeAsync().AsTask(), Console.Error);
}
Console.WriteLine(cts.IsCancellationRequested ? "pilot-gui: --exit-after reached; window closed." : "pilot-gui: window closed.");
return 0;
