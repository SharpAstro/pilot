using DIR.Lib;
using Microsoft.Extensions.Logging;
using Pilot.Contracts;
using Pilot.Gui;
using SdlVulkan.Renderer;
using SharpAstro.AppShell;
using SharpAstro.Png;
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
// A name (DE1) is found on the LAN as the CLI finds it; one no node announces is refused, not taken for this machine.
string? remoteEndpoint = null;
if (nodeTarget is not null)
{
    var (resolved, _, unknown) = await Pilot.Commands.NodeTargets.ResolveAsync(nodeTarget, CancellationToken.None);
    if (unknown is not null)
    {
        Console.Error.WriteLine($"pilot-gui: {unknown}");
        return 2;
    }
    remoteEndpoint = resolved is not null && NodeClient.IsEndpoint(resolved, out var endpoint) ? endpoint : null;
}
// No remote is this machine's socket: PILOT_NODE is folded into nodeTarget already, so it is not read again.
NodeClient NewClient() => Pilot.Commands.NodeTargets.Connect(remoteEndpoint, options.NodeSocket, here: remoteEndpoint is null);
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
JobsScreen? capturedJobs = null;
ReposScreen? capturedRepos = null;
MachineRow? capturedMachine = null;
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
        var code = (await oneShot.GetNodeAsync(CancellationToken.None)).MachinePrefix is { Length: > 0 } prefix ? prefix : null;
        screen = new SessionsScreen([.. SessionLine.Build(rows, now).Select(l => l with { NodeCode = code })], $"live from the node on {nodeWhere}", now);
        // The flights and the cards are on the stream's snapshot, which a capture reads once.
        await using (var events = await oneShot.OpenEventsAsync(since: null, stream: null, CancellationToken.None))
        {
            if (await events.ReadAsync(CancellationToken.None) is SnapshotEvent live)
            {
                capturedFlights = new FlightsScreen(FlightLine.Build(live.Flights, live.Sessions, now, live.Legs), screen.Source, now);
                capturedCards = CardsScreen.From(live.Cards);
                capturedMachine = live.Machine;
                capturedJobs = new JobsScreen(JobLine.Build(live.Jobs, now), screen.Source, now);
                if (await oneShot.GetRepoStatusAsync(CancellationToken.None) is { Success: true, Response: { } read })
                {
                    capturedRepos = new ReposScreen(RepoLine.Build(read,live.Flights, live.Sessions, live.Cards, live.Legs, now), screen.Source, now);
                }
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
    capturedMachine ??= snapshot?.Machine;
    capturedJobs ??= snapshot is null ? null : new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), screen.Source, snapshot.At);
    if (capturedRepos is null && snapshot is not null && File.Exists(SessionsCapture.ReposSamplePath))
    {
        capturedRepos = SessionsCapture.ReposScreenFor(snapshot, SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath), screen.Source);
    }
    var rowsPath = SessionsCapture.Write(screen, capturePath, options.Width, options.Height, canSay: snapshot is null, flights: capturedFlights, page: options.Page, cards: capturedCards,
        jobs: capturedJobs, openFlight: options.Flight, repos: capturedRepos, machines: capturedMachine is null ? null : [new MachineLine(null, capturedMachine)],
        hover: options.Hover, flightsView: options.View, select: options.Select);
    Console.WriteLine($"pilot-gui: rendered {screen.Summary} at {options.Width}x{options.Height} ({screen.Source})");
    Console.WriteLine($"pilot-gui: wrote {Path.GetFullPath(capturePath)} and {Path.GetFullPath(rowsPath)}");
    return 0;
}

// One window per user and node: a second launch on the same node raises the first one and leaves; a window on another
// node (--node) is a window of its own, saying whose it is. The Fleet page opens none.
// Set by the update light: the window closes, then starts again on the binary the node installed.
var restarting = false;
var gateChannel = InstanceGate.ChannelFor("pilot-v2-gui", remoteEndpoint?.ToLowerInvariant() ?? "");
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

var Title = remoteEndpoint is null ? "pilot v2 · sessions" : $"pilot v2 · {remoteEndpoint}";
using var window = SdlVulkanWindow.Create(Title, 1280, 800);
// The window's icon (the exe's is the ApplicationIcon): a missing file or one the decoder rejects leaves the platform's
// default. The decoded array is adopted by the RgbaImage, not copied.
var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "pilot-256.png");
if (File.Exists(iconPath) && PngImageDecoder.TryDecodeRgba8(File.ReadAllBytes(iconPath), out var iconRgba, out var iconWidth, out var iconHeight))
{
    window.SetIcon(new RgbaImage(iconWidth, iconHeight, iconRgba));
}
window.GetSizeInPixels(out var width, out var height);
using var context = VulkanContext.Create(window.Instance, window.Surface, (uint)width, (uint)height);
using var renderer = new VkRenderer(context, (uint)width, (uint)height);
var view = new SessionsView<VulkanContext>(renderer);
// The In play / Everything choice is kept in the user's GUI file, so a restart opens as the window was left.
var chrome = new PilotChrome<VulkanContext>(renderer, view, PlayFilter.DefaultFile) { FontPath = SessionsCapture.FontPath, DpiScale = window.DisplayScale };

using var cts = new CancellationTokenSource();
// Logging as tianwen's GUI does it: a logger of its own, and the renderer's pointed at the same factory, so its lines get
// the same timestamps and routing. Warnings and worse go to stderr, beside the GUI's own pilot-gui: lines.
using var loggerFactory = LoggerFactory.Create(logging => logging
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning));
var logger = loggerFactory.CreateLogger("Pilot.Gui");
SdlVulkanLog.Logger = loggerFactory.CreateLogger("SdlVulkan.Renderer");
var tracker = new BackgroundTaskTracker();
var loop = new SdlEventLoop(window, renderer) { BackgroundColor = new RGBAColor32(0x00, 0x00, 0x00, 0xff) };
// The list pages' column widths a drag on a title's divider set: read from the GUI's own settings file, written back a
// moment after the pointer is still (and when the window closes), and a drag asks for a frame.
using var guiSettings = GuiSettingsFile.Open();
chrome.KeepColumnWidths(guiSettings, loop.RequestRedraw);
var router = new InputRouter(view.Ui, tracker, loop.RequestRedraw)
{
    Widgets = () => chrome.Widgets,
    // A key nothing painted took goes to the page shown, as a tab's own HandleInput does in tianwen.
    Unhandled = evt => chrome.PageWidget.HandleInput(evt),
    // The system clipboard, for the text fields' copy and paste and the menus' Copy entries.
    GetClipboardText = GetClipboardText,
    SetClipboardText = text => SetClipboardText(text),
    // The new-flight wizard's search boxes take Up and Down while they have the keyboard.
    ActiveSearch = () => chrome.Page == PilotPage.Flights ? chrome.Flights.Wizard.ActiveSearch : null,
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
// The fleet beyond this window's node: every node heard on the LAN or granted to this user, watched on its own stream,
// its rows merged into every page. Disposed in the shutdown drain with the feed, never by an await here (see the feed's note).
var fleetWatch = new FleetWatch();
var fleetKnownAt = DateTimeOffset.MinValue;
IReadOnlySet<string> granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
bool CanCommand(string where) => string.Equals(where, remoteEndpoint, StringComparison.OrdinalIgnoreCase) || granted.Contains(where);
// A row's command goes to the node it came from: this window's client for its own, the grant's for another's. A node that
// refuses the grant reads as watch-only until a new one is saved (the fleet tick looks).
using var clients = client is null ? null : new FleetClients(client, CanCommand);
bool CanSteer(NodeMark? node) => clients?.CanSteer(node) ?? true;
// A folder, a file or a link opened the platform's way: ShellExecute can take seconds (a browser starting, a network
// path), so never on the render thread.
void OpenAway(string location, Action? failed = null) => tracker.Run(async () =>
{
    if (!await Task.Run(() => DashboardCommands.Open(location)))
    {
        failed?.Invoke();
        loop.RequestRedraw();
    }
}, $"open {location}");
if (feed is not null && client is not null && clients is not null)
{
    view.CanSay = true;
    view.OnSay = async (line, text) =>
    {
        if (clients.For(line.Node) is not { } to)
        {
            view.SayStatus = FleetClients.WatchOnly(line.Node);
            return;
        }
        view.SayStatus = $"saying to {line.Name}…";
        loop.RequestRedraw();
        var result = await GuiSay.SendAsync(to, line.Row.Id, text, cts.Token, envelope => clients.NoteRefusal(line.Node, envelope));
        view.SayStatus = result;
        Console.WriteLine($"pilot-gui: say to {line.Name}: {result}");
        loop.RequestRedraw();
    };
    // "Say to all shown": each picked row through its own node's say, one line per session in the console.
    view.OnSayAll = async (line, text) =>
    {
        if (clients.For(line.Node) is not { } to)
        {
            return false;
        }
        var (delivered, result) = await GuiSay.SayAsync(to, line.Row.Id, text, cts.Token, envelope => clients.NoteRefusal(line.Node, envelope));
        Console.WriteLine($"pilot-gui: say to {line.Name}: {result}");
        loop.RequestRedraw();
        return delivered;
    };
    // The Sessions page's transcript pane reads the node's transcript route, and follows it while open.
    view.Transcript.Load = async (rowId, agent, after, ct) =>
    {
        // Read from the node the open session runs on; reading needs no grant, as watching does not.
        var envelope = await clients.For(view.Transcript.Node, command: false)!.GetTranscriptAsync(rowId, after, ct, agent);
        return envelope is { Success: true, Response: { } page } ? page : throw new InvalidOperationException(envelope.Error);
    };
    view.Transcript.Changed = loop.RequestRedraw;
    // The attachments the pages show: each copy fetched from the node its flight or session is on (reading needs no grant
    // on this machine, and a remote node's takes the one saved for it), decoded once, drawn as a texture.
    var attachmentPictures = new AttachmentPictures(async (copy, ct) =>
    {
        var (status, _, bytes) = await clients.For(copy.Node, command: false)!.GetAttachmentAsync(copy.Id, ct);
        return status == System.Net.HttpStatusCode.OK ? bytes : null;
    }, loop.RequestRedraw);
    chrome.Gallery.Lookup = attachmentPictures.Lookup;
    chrome.Gallery.Failed = attachmentPictures.Failed;
    chrome.Gallery.Draw = attachmentPictures.PaintOn(renderer);
    // The session menu's folder and copy entries.
    view.OnOpen = folder => OpenAway(folder);
    chrome.Flights.CopyText = text => SetClipboardText(text);
    chrome.Flights.Changed = loop.RequestRedraw;
    // The ? card: this window and the node it talks to, asked again each time it opens.
    NodeInfo? nodeInfo = null;
    chrome.About.Facts = () => AboutFacts.For("pilot-gui", nodeInfo, nodeWhere);
    chrome.About.Opening = () => tracker.Run(async () =>
    {
        nodeInfo = await client.GetNodeAsync(cts.Token);
        loop.RequestRedraw();
    }, "the ? card's node");
    // The update light: this machine's node runs a newer pilot than this window, which it installed beside it. A window on
    // another node (--node) is told nothing: that node's install is not this machine's.
    if (remoteEndpoint is null)
    {
        chrome.About.Newer = () => feed.NewerInstalled;
        chrome.About.Restart = () =>
        {
            restarting = true;
            // Cancelling runs the loop's own wind-down, off this thread: the window closes as it would, then starts again below.
            _ = cts.CancelAsync();
        };
    }
    view.OnNewFlight = repo =>
    {
        chrome.Show(PilotPage.Flights);
        chrome.Flights.Wizard.Open(repo);
    };
    view.CopyText = text => SetClipboardText(text);
    view.Changed = loop.RequestRedraw;
    // The Sessions page's archive buttons: a verb on the node, its answer under the say bar.
    view.OnAction = action => tracker.Run(async () =>
    {
        view.SayStatus = clients.For(action.Node) is { } to ? await DashboardCommands.ActAsync(to, action, GuiSay.Who, cts.Token, envelope => clients.NoteRefusal(action.Node, envelope)) : FleetClients.WatchOnly(action.Node);
        Console.WriteLine($"pilot-gui: {view.SayStatus}");
        loop.RequestRedraw();
    }, "a Sessions action");
    // A card's button is a command on the node, as a click anywhere else is; the stream closes the card.
    async Task CardCommandAsync(string verb, CardRow card, string[] args, string action)
    {
        // One press of a card at a time, from the panel or the flight page: a second would clear the first's working state.
        if (chrome.Cards.WorkingOn(card.Id) is not null)
        {
            return;
        }
        // Said on the button at once, cleared when the node has answered, whatever it said.
        chrome.Cards.NoteWorking(card.Id, action);
        loop.RequestRedraw();
        try
        {
            await SendCardCommandAsync(verb, card, args);
        }
        finally
        {
            chrome.Cards.NoteWorking(card.Id, null);
            loop.RequestRedraw();
        }
    }
    async Task SendCardCommandAsync(string verb, CardRow card, string[] args)
    {
        // A card raised on another node is answered there.
        if (clients.For(chrome.Cards.NodeOf(card)) is not { } to)
        {
            chrome.Cards.NoteRefused(card.Id, FleetClients.WatchOnly(chrome.Cards.NodeOf(card)));
            Console.WriteLine($"pilot-gui: {verb} {card.Id}: {FleetClients.WatchOnly(chrome.Cards.NodeOf(card))}");
            loop.RequestRedraw();
            return;
        }
        var result = await to.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.CardTarget(card.Id), Args = args, Who = GuiSay.Who }, cts.Token);
        clients.NoteRefusal(chrome.Cards.NodeOf(card), result);
        // A refusal is said on the card the button is on, never only here: a Merge pressed again and again on a PR GitHub
        // would not merge showed nothing (SE1, 2026-10-24).
        var refused = !result.Success ? result.Error : result.Response is { Ok: false } answer ? answer.Error : null;
        chrome.Cards.NoteRefused(card.Id, refused);
        Console.WriteLine($"pilot-gui: {verb} {card.Id} ({card.Title}): {refused ?? "done"}");
        loop.RequestRedraw();
    }
    // Every task a click starts goes through the tracker, never `_ =`: it is drained at shutdown and a fault it did not
    // expect is logged (ProcessCompletions, each frame) rather than lost with an unobserved task.
    chrome.Cards.OnAnswer = (card, number) => tracker.Run(
        () => CardCommandAsync(Verbs.Answer, card, [number.ToString(System.Globalization.CultureInfo.InvariantCulture)], $"card:{card.Id}:{number}"), $"answer {card.Id}");
    chrome.Cards.OnDismiss = card => tracker.Run(() => CardCommandAsync(Verbs.Dismiss, card, [], $"card:{card.Id}:dismiss"), $"dismiss {card.Id}");

    // The watchtower, from its panel: its Remote Control link one click away, and a shortcut to the Chat page.
    chrome.Cards.CanTell = true;
    chrome.Cards.OnOpen = url => OpenAway(url);
    chrome.Cards.CopyText = text => SetClipboardText(text);
    chrome.Cards.Changed = loop.RequestRedraw;
    chrome.Cards.OnGoTo = link => { if (link == InAppLinks.Fleet) { chrome.Show(PilotPage.Fleet); } };
    // The Chat page reads the node's chat feed while it is open; what is typed in its box and a choice button go through the
    // node's tell, a refusal said in the chat itself.
    chrome.Chat.Load = async (after, ct) => (await client.GetChatAsync(after, ct)) is { Success: true, Response: { } chat }
        ? chat
        : throw new InvalidOperationException("the node did not send the watchtower's chat");
    chrome.Chat.OnSend = async text =>
    {
        var refused = await ChatCommands.TellAsync(client, text, GuiSay.Who, cts.Token);
        Console.WriteLine($"pilot-gui: tell watchtower: {refused ?? "told"}");
        return refused;
    };
    // The chat with another node's watchtower (a card raised there was chatted about): its feed is read through its own
    // client, which needs no grant, and what is typed is told through the grant's, a refusal said in the chat.
    chrome.Chat.LoadOn = async (node, after, ct) => (await clients.For(node, command: false)!.GetChatAsync(after, ct)) is { Success: true, Response: { } chat }
        ? chat
        : throw new InvalidOperationException($"{node.Name} did not send its watchtower's chat");
    chrome.Chat.OnSendOn = async (node, text) =>
    {
        if (clients.For(node) is not { } to)
        {
            return FleetClients.WatchOnly(node);
        }
        var refused = await ChatCommands.TellAsync(to, text, GuiSay.Who, cts.Token);
        Console.WriteLine($"pilot-gui: tell {node.Name}'s watchtower: {refused ?? "told"}");
        return refused;
    };
    // A card's Chat about this: the words go to the node the card was raised on, as its tell card/<id>.
    chrome.Chat.OnSendAboutCard = async (card, node, words) =>
    {
        if (clients.For(node) is not { } to)
        {
            return FleetClients.WatchOnly(node);
        }
        var refused = await ChatCommands.TellAboutCardAsync(to, card.Id, words, GuiSay.Who, cts.Token);
        Console.WriteLine($"pilot-gui: tell card {card.Id}: {refused ?? "told"}");
        return refused;
    };
    chrome.Chat.OnRestart = async () =>
    {
        var refused = await ChatCommands.RestartWatchtowerAsync(client, GuiSay.Who, cts.Token);
        Console.WriteLine($"pilot-gui: restart watchtower: {refused ?? "restarted"}");
        return refused;
    };
    chrome.Chat.Track = (run, what) => tracker.Run(run, what);
    chrome.Chat.Changed = loop.RequestRedraw;
    chrome.Chat.OpenLink = url => OpenAway(url);
    chrome.Chat.CopyText = text => SetClipboardText(text);

    // The Flights page flies a flight from here alone: its buttons are verbs on the node, and Plan a leg is said in the chat.
    chrome.Flights.CanCommand = true;
    // The LAN as the dashboard sees it, discovering from the start: a node that cannot listen still shows why. Tracked,
    // so shutdown waits for it before it disposes the watch it started.
    tracker.Run(async () =>
    {
        try
        {
            chrome.Flights.Lan = await LanWatch.StartAsync(cts.Token);
            loop.RequestRedraw();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Console.Error.WriteLine($"pilot-gui: the LAN cannot be watched: {ex.Message}");
        }
    }, "watch the LAN");
    // The new-flight wizard reads the node's repos and issues, and its Create makes the flights through the node.
    var wizard = chrome.Flights.Wizard;
    wizard.LoadRepos = async ct => (await client.GetReposAsync(ct)) is { Success: true, Response: { } repos }
        ? repos
        : throw new InvalidOperationException("the node did not list its repos");
    wizard.LoadIssues = async (path, ct) => (await client.GetIssuesAsync(path, ct)) is { Success: true, Response: { } issues }
        ? issues
        : throw new InvalidOperationException("the node could not list the issues");
    wizard.Create = async picks =>
    {
        var said = await NewFlights.CreateAsync(client, picks, GuiSay.Who, cts.Token);
        chrome.Flights.Say(said);
        Console.WriteLine($"pilot-gui: new flight: {said}");
        return said;
    };
    wizard.CreateIssue = (path, title, body) => RepoActions.CreateIssueAsync(client, path, title, body, GuiSay.Who, cts.Token);
    wizard.Changed = loop.RequestRedraw;
    // The Repos page's Sync runs on this window's node; its New flight opens the wizard at that repo's issues.
    chrome.Repos.SyncRepos = async ct =>
    {
        var outcomes = await RepoActions.SyncAsync(client, GuiSay.Who, ct);
        feed?.RefreshRepos();
        return outcomes;
    };
    chrome.Repos.Changed = loop.RequestRedraw;
    chrome.Repos.CopyText = text => SetClipboardText(text);
    chrome.Repos.OnNewFlight = line =>
    {
        chrome.Show(PilotPage.Flights);
        wizard.Open(line.Status.Path, atIssues: true);
    };
    chrome.Flights.OnAction = action => tracker.Run(async () =>
    {
        var said = clients.For(action.Node) is { } to ? await DashboardCommands.ActAsync(to, action, GuiSay.Who, cts.Token, envelope => clients.NoteRefusal(action.Node, envelope)) : FleetClients.WatchOnly(action.Node);
        chrome.Flights.Say(said);
        Console.WriteLine($"pilot-gui: {said}");
        loop.RequestRedraw();
    }, "a Flights action");
    chrome.Flights.OnOpen = location => OpenAway(location, () => chrome.Flights.Say($"could not open {location}"));
    // The Summary cards on Home and on a flight's page: Summarise is a verb on the node the summary is of, and the card shows
    // it running until the node answers. The row it then shows is the node's own, from the stream, never this answer's.
    chrome.Summaries.Track = (run, what) => tracker.Run(run, what);
    chrome.Summaries.Changed = loop.RequestRedraw;
    chrome.Summaries.CanSteer = CanSteer;
    chrome.Summaries.Send = async ask =>
    {
        if (clients.For(ask.Node) is not { } to)
        {
            return FleetClients.WatchOnly(ask.Node);
        }
        try
        {
            var envelope = await to.SendAsync(ask.Request(GuiSay.Who), cts.Token);
            clients.NoteRefusal(ask.Node, envelope);
            var refused = !envelope.Success ? envelope.Error : envelope.Response is { Ok: false } answer ? answer.Error : null;
            Console.WriteLine($"pilot-gui: summarise {ask.Scope} on {ask.Node.Name}: {refused ?? "done"}");
            return refused;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            return $"the node could not be reached ({ex.Message})";
        }
        catch (TaskCanceledException) when (!cts.IsCancellationRequested)
        {
            // The client gives up on a call after 100 s, a summary may take longer: the node keeps it all the same.
            return "the node did not answer in time, and a summary it made late still shows here";
        }
    };
    // The Background tasks page: a job's log followed while it is selected, Cancel a verb on the node.
    chrome.Jobs.CanCommand = true;
    chrome.Jobs.LoadLog = async (node, id, ct) => (await (clients.For(node, command: false) ?? throw new InvalidOperationException($"no connection to {node?.Name ?? "this node"}")).GetJobLogAsync(id, ct)) is { Success: true, Response: { } log }
        ? log
        : throw new InvalidOperationException($"the node did not send {id}'s log");
    chrome.Jobs.Changed = loop.RequestRedraw;
    // Cancel is the selected job's, whose log is followed: its node's.
    Action<JobRow> CancelJob(bool force) => job => tracker.Run(async () =>
    {
        var what = force ? "force cancel" : "cancel";
        if (clients.For(chrome.Jobs.FollowingNode) is not { } to)
        {
            Console.WriteLine($"pilot-gui: {what} {job.Id}: {FleetClients.WatchOnly(chrome.Jobs.FollowingNode)}");
            return;
        }
        var result = await to.SendAsync(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(job.Id), Args = force ? [Verbs.ForceArg] : [], Who = GuiSay.Who }, cts.Token);
        clients.NoteRefusal(chrome.Jobs.FollowingNode, result);
        Console.WriteLine($"pilot-gui: {what} {job.Id}: {(result.Success && result.Response is { Ok: true } ? "done" : result.Response?.Error ?? result.Error)}");
        loop.RequestRedraw();
    }, $"{(force ? "force cancel" : "cancel")} {job.Id}");
    chrome.Jobs.OnCancel = CancelJob(force: false);
    chrome.Jobs.OnForceCancel = CancelJob(force: true);
    chrome.Jobs.OnPause = job => SendJobVerb(job, Verbs.PauseJob, "pause");
    chrome.Jobs.OnResume = job => SendJobVerb(job, Verbs.ResumeJob, "resume");
    void SendJobVerb(JobRow job, string verb, string word) => tracker.Run(async () =>
    {
        if (clients.For(chrome.Jobs.FollowingNode) is not { } to)
        {
            Console.WriteLine($"pilot-gui: {word} {job.Id}: {FleetClients.WatchOnly(chrome.Jobs.FollowingNode)}");
            return;
        }
        var result = await to.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.JobTarget(job.Id), Args = [], Who = GuiSay.Who }, cts.Token);
        clients.NoteRefusal(chrome.Jobs.FollowingNode, result);
        Console.WriteLine($"pilot-gui: {word} {job.Id}: {(result.Success && result.Response is { Ok: true } ? "done" : result.Response?.Error ?? result.Error)}");
        loop.RequestRedraw();
    }, $"{word} {job.Id}");
    chrome.Jobs.OnOpen = location => OpenAway(location);
    chrome.Jobs.CopyText = text => SetClipboardText(text);
    // Rows on a node this user cannot steer are watch-only: their actions say "Connect to <node> to steer", and a Connect
    // pill (the Fleet page's, the same ask) stands in for them. There is no second window per node: the lists steer them all.
    view.CanSteer = CanSteer;
    chrome.Jobs.CanSteer = CanSteer;
    chrome.Flights.CanSteer = CanSteer;
    chrome.Cards.CanSteer = CanSteer;
    void Connect(string where, string machine) => tracker.Run(async () =>
    {
        chrome.Fleet.Said = $"asking {machine} ({where}) for a grant: allow it on that machine";
        view.SayStatus = chrome.Fleet.Said;
        chrome.Flights.Lan?.Asking(where, DateTimeOffset.UtcNow);
        loop.RequestRedraw();
        var said = await DashboardCommands.RunAsync($"connect {where}", NodeArgs(), GuiSay.Who, cts.Token);
        // The token just saved is read now, so the rows steer at once rather than at the next 5 s look.
        clients.Refresh();
        chrome.Flights.Lan?.Answered(where);
        chrome.Fleet.Said = said.ReplaceLineEndings(" ").Trim();
        view.SayStatus = chrome.Fleet.Said;
        Console.WriteLine($"pilot-gui: connect {where}: {chrome.Fleet.Said}");
        loop.RequestRedraw();
    }, $"connect {where}");
    chrome.Fleet.OnConnect = line => Connect(line.Where, line.Machine);
    view.OnConnect = node => Connect(node.Where, node.Name);
    chrome.Jobs.OnConnect = node => Connect(node.Where, node.Name);
    chrome.Flights.OnConnect = node => Connect(node.Where, node.Name);
    chrome.Cards.OnConnect = node => Connect(node.Where, node.Name);
    // Forget, from the Settings page's held grant or an unreached Fleet row alike: the token is dropped and the fleet stops
    // watching that endpoint (a node still heard on the LAN is known again at the next look). Its rows turn watch-only on
    // the next draw: the steerable set is read again at once, not at the 5 s look.
    void Forget(string endpoint) => tracker.Run(() => Task.Run(() =>
    {
        // A file read, a write and a move: off the render thread.
        Pilot.Commands.NodeTargets.Forget(endpoint);
        fleetWatch.Forget(endpoint);
        fleetKnownAt = DateTimeOffset.MinValue;
        chrome.Fleet.Said = $"forgot {endpoint}";
        view.SayStatus = chrome.Fleet.Said;
        Console.WriteLine($"pilot-gui: forgot {endpoint}");
        loop.RequestRedraw();
    }), $"forget {endpoint}");
    chrome.Fleet.OnForget = line => Forget(line.Where);
    // Maintenance on / off on a row's node: this window's, or a granted one, through the client its grant gives.
    chrome.Fleet.OnMaintenance = (line, on) => tracker.Run(async () =>
    {
        chrome.Fleet.Said = (await FleetCommands.SetMaintenanceAsync(clients, line, on, GuiSay.Who, cts.Token)).ReplaceLineEndings(" ").Trim();
        view.SayStatus = chrome.Fleet.Said;
        Console.WriteLine($"pilot-gui: maintenance {(on ? "on" : "off")} on {line.Where}: {chrome.Fleet.Said}");
        loop.RequestRedraw();
    }, $"maintenance {(on ? "on" : "off")} on {line.Where}");
    // The header's Autopilot switch: this window's node, set as the person (only the person may; the node refuses anyone else).
    // The switch follows the node's own answer on the next look, never the click, so a refusal leaves it as it was. On a remote node
    // it shows the state and does not click: the node keeps autopilot "for this machine only", as the Settings page is gated.
    if (remoteEndpoint is null)
    {
        chrome.Autopilot.OnToggle = on => tracker.Run(async () =>
        {
            view.SayStatus = await AutopilotCommands.SetAsync(client, on, GuiSay.Who, cts.Token);
            Console.WriteLine($"pilot-gui: {view.SayStatus}");
            loop.RequestRedraw();
        }, $"autopilot {(on ? "on" : "off")}");
    }
    // The Settings page: this machine's node only, as its route is (a node on the LAN keeps its settings to itself).
    if (remoteEndpoint is null)
    {
        chrome.Settings.Load = async ct => (await client.GetSettingsAsync(ct)) is { Success: true, Response: { } page }
            ? page
            : throw new InvalidOperationException("the node did not send its settings");
        chrome.Settings.LoadChecks = async (refresh, ct) => (await client.GetSetupChecksAsync(ct, refresh)) is { Success: true, Response: { } checks }
            ? checks
            : throw new InvalidOperationException("the node did not send its setup checks");
        chrome.Settings.LoadRepos = async ct => (await client.GetReposAsync(ct)) is { Success: true, Response: { } repos }
            ? repos
            : throw new InvalidOperationException("the node did not list its repos");
        chrome.Settings.Send = (verb, args, ct) => SettingsCommands.SendAsync(client, verb, args, GuiSay.Who, ct);
        chrome.Settings.LoadGrants = async ct => (await client.GrantsAsync(ct)) is { Success: true, Response: { } given }
            ? given
            : throw new InvalidOperationException("the node did not send its grants");
        chrome.Settings.LoadHeld = () => Pilot.Commands.NodeTargets.Granted();
        chrome.Settings.Forget = Forget;
        chrome.Settings.Changed = loop.RequestRedraw;
    }
    feed.Start();

    // The window opens at once and the feed connects when the node answers; a node is started meanwhile if none runs.
    if (remoteEndpoint is null)
    {
        tracker.Run(async () =>
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
        }, "find or start the node");
    }
}

var announced = (string?)null;
// The pages as the builder last made them, and as the render thread last drew them; what the render thread wants built.
Tuple<FleetPages, FleetScreen>? built = null;
Tuple<FleetPages, FleetScreen>? drawnBuilt = null;
(PilotPage, int) drawnWanted = default;
var pagesWanted = (int)chrome.Page;
var pagesAsked = 0;
// Console lines from the render thread go through one writer task: a console write blocks (a parent console in
// selection mode holds it), and nothing on the render thread may.
var console = System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
_ = Task.Run(async () =>
{
    await foreach (var line in console.Reader.ReadAllAsync())
    {
        Console.WriteLine(line);
    }
});
void Say(string line) => console.Writer.TryWrite(line);
CardsScreen? cards = snapshot is null ? null : CardsScreen.From(snapshot.Cards);
JobsScreen? jobs = snapshot is null ? null : new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), screen.Source, snapshot.At);
FleetScreen? fleet = null;
ReposScreen? repos = null;
IReadOnlyList<MachineLine>? machines = null;
FlightsScreen? flights = snapshot is null ? null : new FlightsScreen(FlightLine.Build(snapshot.Flights, snapshot.Sessions, snapshot.At, snapshot.Legs), screen.Source, snapshot.At);
// A card about a leg opens its worker's transcript from the moment the card was raised.
chrome.LinkCardTranscripts(() => screen, () => flights ?? new FlightsScreen([], screen.Source, screen.AsOf), jobs: () => jobs ?? new JobsScreen([], screen.Source, screen.AsOf));
chrome.LinkRepos();
// The Fleet page's "N waiting on you" opens those sessions on the Sessions page.
chrome.LinkFleet();
chrome.Repos.OnOpen = location => OpenAway(location);
// The pages, built off the render thread: every node's rows merged, filtered and ordered, the Fleet page's summaries, and
// the five-second look at who is in the fleet (a file read and the LAN). Each frame built them all, under each feed's lock,
// so a burst of events (181 archived on DE1, 2026-10-24) kept frames at full cost back to back and the window stalled. Built
// again when a node's version moves (at most every 100 ms through a burst), when the render thread asks (a page switch, a
// node pick: at once), or every second (the "ago" column); the render thread only picks up the last one made.
if (feed is not null)
{
    var building = cts.Token;
    _ = Task.Run(async () =>
    {
        var seenVersion = -1L;
        var seenAsk = -1;
        var builtAt = DateTimeOffset.MinValue;
        while (!building.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(30), building);
                var now = DateTimeOffset.UtcNow;
                var version = feed.Version + fleetWatch.Version;
                var ask = Volatile.Read(ref pagesAsked);
                var asked = ask != seenAsk;
                var moved = version != seenVersion;
                if (!asked && !(moved && now - builtAt >= TimeSpan.FromMilliseconds(100)) && now - builtAt < TimeSpan.FromSeconds(1))
                {
                    continue;
                }
                seenVersion = version;
                seenAsk = ask;
                builtAt = now;
                // Who is in the fleet, looked at every few seconds: the grants are a file, the LAN a listener.
                if (now - fleetKnownAt >= TimeSpan.FromSeconds(5))
                {
                    fleetKnownAt = now;
                    clients?.Refresh();
                    granted = new HashSet<string>(Pilot.Commands.NodeTargets.Granted(), StringComparer.OrdinalIgnoreCase);
                    fleetWatch.Know(granted.Select(e => (e, (string?)null)).Concat((chrome.Flights.Lan?.Nodes ?? []).Select(n => (n.Endpoint, (string?)n.Name))));
                }
                // Every node's rows on every page, each marked with its node, as the header's node selection picks them.
                var pages = fleetWatch.Pages(nodeWhere, feed, chrome.Nodes, now, reposWanted: (PilotPage)Volatile.Read(ref pagesWanted) == PilotPage.Repos);
                var hereSummary = feed.Summary(now);
                var fleetScreen = fleetWatch.Screen(nodeWhere, hereSummary, CanCommand, now);
                // This window's node in maintenance: Home and Flights say so (a plain flag, read by the render thread).
                chrome.Home.Maintenance = chrome.Flights.Maintenance = hereSummary is { Connected: true, Node.Maintenance: true };
                // And its autopilot, in every header's switch.
                chrome.Autopilot.On = hereSummary is { Connected: true, Node.Autopilot: true };
                Volatile.Write(ref built, Tuple.Create(pages, fleetScreen));
                loop.RequestRedraw();
            }
            catch (OperationCanceledException) when (building.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A build that failed is said and tried again; the window keeps the last pages it had.
                Say($"pilot-gui: the pages could not be built: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }
    });
}
loop.OnResize = (w, h) =>
{
    width = (int)w;
    height = (int)h;
    chrome.DpiScale = window.DisplayScale;
};
loop.OnRender = () =>
{
    // The background work clicks started: a fault it did not expect is logged here, once, instead of vanishing.
    if (tracker.ProcessCompletions(logger))
    {
        loop.RequestRedraw();
    }
    if (feed is not null)
    {
        // The pages are the builder's (below), made off this thread: here they are only picked up. A page switch or a node
        // pick asks for a build at once rather than at the builder's next look.
        var wanted = (chrome.Page, chrome.Nodes.Revision);
        if (wanted != drawnWanted)
        {
            drawnWanted = wanted;
            Volatile.Write(ref pagesWanted, (int)wanted.Page);
            Interlocked.Increment(ref pagesAsked);
        }
        if (Volatile.Read(ref built) is { } made && !ReferenceEquals(made, drawnBuilt))
        {
            drawnBuilt = made;
            var pages = made.Item1;
            screen = pages.Sessions;
            flights = pages.Flights;
            cards = pages.Cards;
            chrome.Flights.Jobs = pages.JobRows;
            jobs = pages.Jobs;
            repos = pages.Repos;
            chrome.Summaries.Shelf = pages.Summaries ?? SummaryShelf.Empty;
            machines = pages.Machines;
            fleet = made.Item2;
        }
    }

    view.Ui.FrameId++;
    chrome.Render(screen, flights, cards, jobs, fleet, repos, new RectF32(0, 0, width, height), machines);
    // The hover text the router says is due, last, over everything.
    chrome.Tooltips.Render(router.Tooltip, new RectF32(0, 0, width, height));
    router.AfterPaint();

    // Say which session is selected whenever it changes, so stdout follows what the window shows.
    if (view.SelectedId != announced && view.Selected >= 0 && view.Selected < screen.Lines.Count)
    {
        announced = view.SelectedId;
        Say($"pilot-gui: selected {screen.Lines[view.Selected].ToDumpLine()}");
    }
};
loop.CheckNeedsRedraw = () =>
{
    while (gate?.TryDequeue(out _) == true)
    {
        window.Activate();
        Say("pilot-gui: another launch asked for this window; raised it.");
        return true;
    }

    // New pages from the builder (it also asks for the frame itself).
    if (feed is not null && !ReferenceEquals(Volatile.Read(ref built), drawnBuilt))
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

// The window's own Back and Forward (Alt+Left, Alt+Right, the mouse's back and forward buttons) come ahead of the router, so
// they work whatever is painted under the pointer or has the keyboard. SDL's X1 and X2 buttons arrive as MouseButton.Back and Forward.
loop.OnKeyDown = key => chrome.HandleInput(key) || router.Handle(key);
loop.OnTextInput = text => router.Handle(new InputEvent.TextInput(text));
// The cursor is the regions' (a button or a link says the hand), asked on every move rather than after a paint: a move
// over inert chrome draws no frame. Cached so a move within one region costs no SDL call. As FC.SDK.Viewer does.
var shownCursor = CursorKind.Default;
loop.OnPointerInput = evt =>
{
    if (evt is InputEvent.MouseMove move)
    {
        chrome.Pointer = (move.X, move.Y);
    }
    var handled = chrome.HandleInput(evt) || router.Handle(evt);
    if (evt is InputEvent.MouseMove moved)
    {
        var cursor = router.CursorAt(moved.X, moved.Y) ?? CursorKind.Default;
        if (cursor != shownCursor)
        {
            shownCursor = cursor;
            window.SetSystemCursor(cursor.ToSystemCursor);
        }
    }
    return handled;
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
ShutdownDrain.PumpUntilComplete(loop, ShutDownAsync(), Console.Error);
if (restarting && Environment.ProcessPath is { } self)
{
    // The gate first: held, the new window would hand off to this one and leave. The binary at this path is the new one,
    // the node's update having moved the running one aside.
    gate?.Dispose();
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self, args) { UseShellExecute = false });
    Console.WriteLine($"pilot-gui: window closed; started again on {self}.");
    return 0;
}
Console.WriteLine(cts.IsCancellationRequested ? "pilot-gui: --exit-after reached; window closed." : "pilot-gui: window closed.");
return 0;

// Shutdown, with the loop pumping (ShutdownDrain): the tracked work first, so nothing is still starting what is about to
// be disposed; then the LAN watch it started, then the fleet's feeds and the window's own.
async Task ShutDownAsync()
{
    await cts.CancelAsync();
    try
    {
        await tracker.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine($"pilot-gui: {tracker.PendingCount} background task(s) still running after 5 s; closing anyway");
    }
    if (chrome.Flights.Lan is { } lan)
    {
        await lan.DisposeAsync();
    }
    await fleetWatch.DisposeAsync();
    if (feed is not null)
    {
        await feed.DisposeAsync();
    }
}
