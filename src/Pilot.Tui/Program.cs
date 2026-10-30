using System.CommandLine;
using System.Text;
using Console.Lib;
using DIR.Lib;
using Pilot.Commands;
using Pilot.Contracts;

// pilot-tui: the dashboard in a terminal. The GUI's own pages and panels (Pilot.Ui), their trees arranged in cells by
// Console.Lib rather than painted on pixels: the Sessions page (1), the Flights page (2) with its
// buttons, the Background tasks page (3), the Fleet page (4), and the watchtower's panel with its cards, all live from
// this machine's node or one on the LAN.
var nodeOption = new Option<string?>("--node") { Description = "A node on the LAN: host:port, or its name (SE1) or host; else PILOT_NODE, else this machine's" };
var socketOption = new Option<string?>("--node-socket") { Description = "This machine's node's socket; else PILOT_NODE_SOCKET, else this user's default" };
var root = new RootCommand("pilot v2's dashboard in a terminal: 1 home, 2 sessions, 3 flights, 4 jobs, 5 fleet, 6 settings, 7 repos, 8 chat, 9 pull requests, Alt+Left or Backspace back and Alt+Right forward, Ctrl+T the chat with the watchtower, q quits")
{
    Options = { nodeOption, socketOption },
};
var parsed = root.Parse(args);
if (parsed.Errors.Count > 0 || parsed.Action is System.CommandLine.Help.HelpAction)
{
    return parsed.Invoke();
}

var nodeTarget = parsed.GetValue(nodeOption) ?? (Environment.GetEnvironmentVariable(NodeTargets.EnvironmentVariable) is { Length: > 0 } env ? env : null);
// A name (DE1) is found on the LAN as the CLI finds it; one no node announces is refused, not taken for this machine.
string? remote = null;
if (nodeTarget is not null)
{
    var (resolved, _, unknown) = await NodeTargets.ResolveAsync(nodeTarget, CancellationToken.None);
    if (unknown is not null)
    {
        System.Console.Error.WriteLine($"pilot-tui: {unknown}");
        return 2;
    }
    remote = resolved is not null && NodeClient.IsEndpoint(resolved, out var endpoint) ? endpoint : null;
}
string[] nodeArgs = remote is not null ? ["--node", remote] : ["--node-socket", NodeSocket.Resolve(parsed.GetValue(socketOption))];
const string Who = "tui";

// No remote is this machine's socket: PILOT_NODE is folded into nodeTarget already, so it is not read again.
using var client = NodeTargets.Connect(remote, parsed.GetValue(socketOption), here: remote is null);
await using var feed = NodeFeed.For(client);
// The fleet beyond this node: every node heard on the LAN or granted to this user, watched on its own stream, its rows
// merged into every page.
await using var fleetWatch = new FleetWatch();
var fleetKnownAt = DateTimeOffset.MinValue;
IReadOnlySet<string> granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
bool CanCommand(string where) => string.Equals(where, remote, StringComparison.OrdinalIgnoreCase) || granted.Contains(where);
// A row's command goes to the node it came from: this terminal's client for its own, the grant's for another's.
using var clients = new FleetClients(client, CanCommand);
var nodeWhere = remote ?? client.SocketPath;

// The views are the GUI's, generic over the surface; their renderer is never painted with here (the trees are arranged
// in cells), so a one-pixel CPU surface stands in for it.
using var vessel = new RgbaImageRenderer(1, 1);
var sessions = new SessionsView<RgbaImage>(vessel) { CanSay = true };
var chrome = new PilotChrome<RgbaImage>(vessel, sessions);
// Each node streamed from is told when the Pull requests page is shown and when it is left, so its watch reads every minute only meanwhile.
chrome.OnPageShown = page =>
{
    feed.SayPage(page);
    fleetWatch.SayPage(page);
};
var flights = chrome.Flights;
flights.CanvasAvailable = false; // cells cannot draw an arrow: the terminal keeps the list
var jobs = chrome.Jobs;
var repos = chrome.Repos;
var pullRequests = chrome.PullRequests;
var home = chrome.Home;
var fleet = chrome.Fleet;
var settings = chrome.Settings;
var cards = chrome.Cards;
var chat = chrome.Chat;
var focus = sessions.Ui.Focus;
var tracker = new BackgroundTaskTracker();
using var stop = new CancellationTokenSource();
var dirty = true;
void Redraw() => dirty = true;

// The same wiring as the window's: a verb on the node for each button, and Plan a leg said in the chat.
flights.CanCommand = true;
// The LAN as the dashboard sees it, discovering from the start.
_ = Task.Run(async () =>
{
    try
    {
        flights.Lan = await LanWatch.StartAsync(stop.Token);
        Redraw();
    }
    catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
    {
        flights.Say($"the LAN cannot be watched: {ex.Message}");
    }
});
flights.Wizard.LoadRepos = async ct => (await client.GetReposAsync(ct)) is { Success: true, Response: { } repos }
    ? repos
    : throw new InvalidOperationException("the node did not list its repos");
flights.Wizard.LoadIssues = async (path, ct) => (await client.GetIssuesAsync(path, ct)) is { Success: true, Response: { } issues }
    ? issues
    : throw new InvalidOperationException("the node could not list the issues");
flights.Wizard.Create = async picks =>
{
    var said = await NewFlights.CreateAsync(client, picks, Who, stop.Token);
    flights.Say(said);
    return said;
};
flights.Wizard.CreateIssue = (path, title, body) => RepoActions.CreateIssueAsync(client, path, title, body, Who, stop.Token);
flights.Wizard.Changed = Redraw;
repos.SyncRepos = async ct =>
{
    var outcomes = await RepoActions.SyncAsync(client, Who, ct);
    feed.RefreshRepos();
    return outcomes;
};
repos.Changed = Redraw;
// The Pull requests page's Refresh asks the node to read GitHub now; the row comes back on the event stream.
pullRequests.OnRefresh = async ct =>
{
    var result = (await client.SendAsync(new CommandRequest { Verb = Verbs.RefreshPullRequests, Target = Verbs.NodeTarget, Who = Who }, ct)).Response;
    if (result is not { Ok: true })
    {
        throw new InvalidOperationException(result?.Error ?? "the node did not answer");
    }
};
pullRequests.Changed = Redraw;
flights.OnAction = action => _ = Task.Run(async () =>
{
    flights.Say(clients.For(action.Node) is { } to ? await DashboardCommands.ActAsync(to, action, Who, stop.Token, envelope => clients.NoteRefusal(action.Node, envelope)) : FleetClients.WatchOnly(action.Node));
    Redraw();
});
flights.OnOpen = location => flights.Say(DashboardCommands.Open(location) ? $"opened {location}" : $"could not open {location}");
sessions.Transcript.Load = async (rowId, agent, after, ct) => (await clients.For(sessions.Transcript.Node, command: false)!.GetTranscriptAsync(rowId, after, ct, agent)) is { Success: true, Response: { } page }
    ? page
    : throw new InvalidOperationException("the node has no transcript for this session");
sessions.Transcript.Changed = Redraw;
sessions.OnOpen = folder => DashboardCommands.Open(folder);
NodeInfo? nodeInfo = null;
chrome.About.Facts = () => AboutFacts.For("pilot-tui", nodeInfo, client.SocketPath);
chrome.About.Opening = () => _ = Task.Run(async () =>
{
    try
    {
        nodeInfo = await client.GetNodeAsync(stop.Token);
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException)
    {
    }
    Redraw();
});
sessions.OnAction = action => _ = Task.Run(async () =>
{
    sessions.SayStatus = clients.For(action.Node) is { } to ? await DashboardCommands.ActAsync(to, action, Who, stop.Token, envelope => clients.NoteRefusal(action.Node, envelope)) : FleetClients.WatchOnly(action.Node);
    Redraw();
});
sessions.OnSay = async (line, text) =>
{
    if (clients.For(line.Node) is not { } to)
    {
        sessions.SayStatus = FleetClients.WatchOnly(line.Node);
        Redraw();
        return;
    }
    var envelope = await to.SendAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(line.Row.Id), Args = [text], Who = Who }, stop.Token);
    clients.NoteRefusal(line.Node, envelope);
    sessions.SayStatus = envelope.Success ? $"said to {line.Name}" : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    Redraw();
};
sessions.OnSayAll = async (line, text) =>
{
    if (clients.For(line.Node) is not { } to)
    {
        return false;
    }
    var envelope = await to.SendAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(line.Row.Id), Args = [text], Who = Who }, stop.Token);
    clients.NoteRefusal(line.Node, envelope);
    Redraw();
    return envelope.Success && envelope.Response is { Ok: true };
};
async Task CardAsync(string verb, CardRow card, string[] cardArgs)
{
    // A card raised on another node is answered there.
    if (clients.For(cards.NodeOf(card)) is not { } to)
    {
        flights.Say($"{verb} {card.Id}: {FleetClients.WatchOnly(cards.NodeOf(card))}");
        Redraw();
        return;
    }
    var envelope = await to.SendAsync(DashboardCommands.CardRequest(verb, card, cardArgs, Who), stop.Token);
    clients.NoteRefusal(cards.NodeOf(card), envelope);
    flights.Say(envelope.Success ? $"{verb} {card.Id}: done" : $"{verb} {card.Id}: {envelope.Response?.Error ?? envelope.Error}");
    Redraw();
}
cards.OnAnswer = (card, number) => _ = CardAsync(Verbs.Answer, card, [number.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
cards.OnDismiss = card => _ = CardAsync(Verbs.Dismiss, card, []);
cards.CanTell = true;
cards.OnOpen = url => DashboardCommands.Open(url);
// The Chat page: the node's chat feed with the box that tells the watchtower; the panel's shortcut opens it with the keyboard in the box.
chat.Load = async (after, ct) => (await client.GetChatAsync(after, ct)) is { Success: true, Response: { } page }
    ? page
    : throw new InvalidOperationException("the node did not send the watchtower's chat");
chat.OnSend = async text =>
{
    var refused = await ChatCommands.TellAsync(client, text, Who, stop.Token);
    Redraw();
    return refused;
};
chat.OnSendAboutCard = async (card, node, words) =>
{
    var refused = clients.For(node) is { } to
        ? await ChatCommands.TellAboutCardAsync(to, card.Id, words, Who, stop.Token)
        : FleetClients.WatchOnly(node);
    Redraw();
    return refused;
};
chat.LoadOn = async (node, after, ct) => (await clients.For(node, command: false)!.GetChatAsync(after, ct)) is { Success: true, Response: { } page }
    ? page
    : throw new InvalidOperationException($"{node.Name} did not send its watchtower's chat");
chat.OnSendOn = async (node, text) =>
{
    var refused = clients.For(node) is { } to
        ? await ChatCommands.TellAsync(to, text, Who, stop.Token)
        : FleetClients.WatchOnly(node);
    Redraw();
    return refused;
};
// A card's Chat about this opens the chat of the node the card was raised on.
cards.OnChatAbout = card =>
{
    var node = cards.NodeOf(card);
    chrome.Show(PilotPage.Chat);
    chat.SwitchTo(node);
    chat.Attach(card, node);
};
flights.OnChatAboutCard = card => cards.OnChatAbout?.Invoke(card);
chat.OnRestart = async () =>
{
    var refused = await ChatCommands.RestartWatchtowerAsync(client, Who, stop.Token);
    Redraw();
    return refused;
};
chat.Changed = Redraw;
chat.OpenLink = url => DashboardCommands.Open(url);
cards.OnChat = () =>
{
    chrome.Show(PilotPage.Chat);
    focus.Focus(chat.Input);
};
// The Background tasks page: a job's log followed while it is selected, Cancel a verb on the node.
jobs.CanCommand = true;
jobs.LoadLog = async (node, id, ct) => (await (clients.For(node, command: false) ?? throw new InvalidOperationException($"no connection to {node?.Name ?? "this node"}")).GetJobLogAsync(id, ct)) is { Success: true, Response: { } log }
    ? log
    : throw new InvalidOperationException($"the node did not send {id}'s log");
jobs.Changed = Redraw;
// Cancel is the selected job's, whose log is followed: its node's.
Action<JobRow> CancelJob(bool force) => job => _ = Task.Run(async () =>
{
    var what = force ? "force cancel" : "cancel";
    if (clients.For(jobs.FollowingNode) is not { } to)
    {
        flights.Say($"{what} {job.Id}: {FleetClients.WatchOnly(jobs.FollowingNode)}");
        Redraw();
        return;
    }
    var envelope = await to.SendAsync(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(job.Id), Args = force ? [Verbs.ForceArg] : [], Who = Who }, stop.Token);
    clients.NoteRefusal(jobs.FollowingNode, envelope);
    flights.Say(envelope.Success && envelope.Response is { Ok: true } ? $"{(force ? "force cancelled" : "cancelled")} {job.Id}" : $"{what} {job.Id}: {envelope.Response?.Error ?? envelope.Error}");
    Redraw();
});
jobs.OnCancel = CancelJob(force: false);
jobs.OnForceCancel = CancelJob(force: true);
jobs.OnPause = job => SendJobVerb(job, Verbs.PauseJob, "paused");
jobs.OnResume = job => SendJobVerb(job, Verbs.ResumeJob, "resumed");
void SendJobVerb(JobRow job, string verb, string done) => _ = Task.Run(async () =>
{
    if (clients.For(jobs.FollowingNode) is not { } to)
    {
        flights.Say($"{verb} {job.Id}: {FleetClients.WatchOnly(jobs.FollowingNode)}");
        Redraw();
        return;
    }
    var envelope = await to.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.JobTarget(job.Id), Args = [], Who = Who }, stop.Token);
    clients.NoteRefusal(jobs.FollowingNode, envelope);
    flights.Say(envelope.Success && envelope.Response is { Ok: true } ? $"{done} {job.Id}" : $"{verb} {job.Id}: {envelope.Response?.Error ?? envelope.Error}");
    Redraw();
});
jobs.OnOpen = location => DashboardCommands.Open(location);
// The Fleet page: Connect asks a node for a grant; the merged lists then steer it.
void Connect(string where, string machine) => _ = Task.Run(async () =>
{
    fleet.Said = $"asking {machine} ({where}) for a grant: allow it on that machine";
    sessions.SayStatus = fleet.Said;
    Redraw();
    fleet.Said = (await DashboardCommands.RunAsync($"connect {where}", nodeArgs, Who, stop.Token)).ReplaceLineEndings(" ").Trim();
    sessions.SayStatus = fleet.Said;
    Redraw();
});
fleet.OnConnect = line => Connect(line.Where, line.Machine);
// Rows on a node this user cannot steer are watch-only, with a Connect in place of their actions.
sessions.CanSteer = clients.CanSteer;
jobs.CanSteer = clients.CanSteer;
flights.CanSteer = clients.CanSteer;
cards.CanSteer = clients.CanSteer;
sessions.OnConnect = node => Connect(node.Where, node.Name);
jobs.OnConnect = node => Connect(node.Where, node.Name);
flights.OnConnect = node => Connect(node.Where, node.Name);
cards.OnConnect = node => Connect(node.Where, node.Name);
// Forget, from the Settings page's held grant or an unreached Fleet row alike: the token is dropped and the fleet stops
// watching that endpoint (a node still heard on the LAN is known again at the next look). Its rows turn watch-only on
// the next draw: the steerable set is read again at once, not at the 5 s look.
void Forget(string endpoint)
{
    NodeTargets.Forget(endpoint);
    fleetWatch.Forget(endpoint);
    fleetKnownAt = DateTimeOffset.MinValue;
    fleet.Said = $"forgot {endpoint}";
    sessions.SayStatus = fleet.Said;
    Redraw();
}
fleet.OnForget = line => Forget(line.Where);
// Maintenance on / off on a row's node: this window's, or a granted one, through the client its grant gives.
fleet.OnMaintenance = (line, on) => _ = Task.Run(async () =>
{
    fleet.Said = (await FleetCommands.SetMaintenanceAsync(clients, line, on, Who, stop.Token)).ReplaceLineEndings(" ").Trim();
    sessions.SayStatus = fleet.Said;
    Redraw();
});
// The Settings page: this machine's node only, as its route is.
if (remote is null)
{
    settings.Load = async ct => (await client.GetSettingsAsync(ct)) is { Success: true, Response: { } page }
        ? page
        : throw new InvalidOperationException("the node did not send its settings");
    settings.LoadChecks = async (refresh, ct) => (await client.GetSetupChecksAsync(ct, refresh)) is { Success: true, Response: { } checks }
        ? checks
        : throw new InvalidOperationException("the node did not send its setup checks");
    settings.LoadRepos = async ct => (await client.GetReposAsync(ct)) is { Success: true, Response: { } repos }
        ? repos
        : throw new InvalidOperationException("the node did not list its repos");
    settings.Send = (verb, args, ct) => SettingsCommands.SendAsync(client, verb, args, Who, ct);
    settings.LoadGrants = async ct => (await client.GrantsAsync(ct)) is { Success: true, Response: { } given }
        ? given
        : throw new InvalidOperationException("the node did not send its grants");
    settings.LoadHeld = () => NodeTargets.Granted();
    settings.Forget = Forget;
    settings.Changed = Redraw;
}
feed.Start();

await using var terminal = new VirtualTerminal();
if (terminal.IsInputRedirected || terminal.IsOutputRedirected)
{
    await System.Console.Error.WriteLineAsync("pilot-tui: needs a terminal for its input and output; for a script, use pilot (pilot status, pilot cards)");
    return 2;
}
await terminal.InitAsync();
terminal.EnterAlternateScreen();
terminal.EnableCellBuffer();

var page = PilotPage.Home;
// A card about a leg opens its worker's transcript from the moment the card was raised.
FleetPages? merged = null;
chrome.LinkCardTranscripts(() => merged?.Sessions ?? feed.Screen(DateTimeOffset.UtcNow), () => merged?.Flights ?? feed.Flights(DateTimeOffset.UtcNow), shown => page = shown,
    () => merged?.Jobs ?? feed.JobsPage(DateTimeOffset.UtcNow));
chrome.LinkRepos(shown => page = shown);
chrome.LinkPullRequests(shown => page = shown);
// The Fleet page's "N waiting on you" opens those sessions on the Sessions page.
chrome.LinkFleet(shown => page = shown);
repos.OnOpen = location => DashboardCommands.Open(location);
pullRequests.OnOpen = location => DashboardCommands.Open(location);
// The session menu's New flight in its repo: the Flights page, with the wizard open on it.
sessions.OnNewFlight = repo =>
{
    chrome.Show(PilotPage.Flights);
    flights.Wizard.Open(repo);
};
// The Repos page's New flight: the wizard on that repo's issues.
repos.OnNewFlight = line =>
{
    chrome.Show(PilotPage.Flights);
    flights.Wizard.Open(line.Status.Path, atIssues: true);
};
var arranged = System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>>.Empty;
var drawnVersion = -1L;
var drawnAt = DateTimeOffset.MinValue;

while (!stop.IsCancellationRequested)
{
    while (terminal.HasInput())
    {
        Handle(terminal.TryReadInput());
    }

    var now = DateTimeOffset.UtcNow;
    if (dirty || feed.Version + fleetWatch.Version != drawnVersion || now - drawnAt >= TimeSpan.FromSeconds(1))
    {
        dirty = false;
        drawnVersion = feed.Version + fleetWatch.Version;
        // Who is in the fleet, looked at every few seconds: the grants are a file, the LAN a listener.
        if (now - fleetKnownAt >= TimeSpan.FromSeconds(5))
        {
            fleetKnownAt = now;
            clients.Refresh();
            granted = new HashSet<string>(NodeTargets.Granted(), StringComparer.OrdinalIgnoreCase);
            fleetWatch.Know(granted.Select(e => (e, (string?)null)).Concat((flights.Lan?.Nodes ?? []).Select(n => (n.Endpoint, (string?)n.Name))));
        }
        drawnAt = now;
        var (columns, rows) = terminal.Size;
        // Design units per cell, as CellMeasureContext.PixelAuthored counts them: the trees are the GUI's.
        var width = columns * 8f;
        // Every node's rows on every page, each marked with its node, as the header's node selection picks them.
        var pages = merged = fleetWatch.Pages(nodeWhere, feed, chrome.Nodes, now, reposWanted: page == PilotPage.Repos);
        // This node in maintenance: Home and Flights say so.
        home.Maintenance = flights.Maintenance = feed.Summary(now) is { Connected: true, Node.Maintenance: true };
        var pageTree = page switch
        {
            PilotPage.Chat => chat.Tree(),
            PilotPage.Sessions => sessions.Tree(pages.Sessions),
            PilotPage.Flights => flights.Tree(pages.Flights),
            PilotPage.Repos => repos.Tree(pages.Repos ?? new ReposScreen([], pages.Sessions.Source, now)),
            PilotPage.PullRequests => pullRequests.Tree(pages.PullRequests ?? new PullRequestsScreen(null, pages.Sessions.Source, now), width),
            PilotPage.Jobs => jobs.Tree(pages.Jobs),
            PilotPage.Fleet => fleet.Tree(fleetWatch.Screen(nodeWhere, feed.Summary(now), CanCommand, now)),
            PilotPage.Settings => settings.Tree(now),
            _ => home.Tree(new HomeScreen(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards, pages.Machines)),
        };
        var cardsScreen = pages.Cards;
        flights.Jobs = pages.JobRows;
        // The panel takes 38 columns (the GUI's 300 units) when there is room beside a usable page, and something to show.
        var tree = columns >= 120 && (cardsScreen.HasAny || cards.CanTell)
            ? Layout.Builder.HStack(pageTree.WStar(), cards.Tree(cardsScreen).WFixed(CardsView<RgbaImage>.Width)).Stretch()
            : pageTree;
        arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
        // No clear: the cell buffer diffs each frame against the last, and the tree's backgrounds cover every cell.
        CellLayout.Paint(terminal, arranged);
        if (focus.Current is null)
        {
            terminal.HideCaret();
        }
        terminal.Flush();
    }

    await Task.Delay(30);
}
return 0;

void Handle(ConsoleInputEvent raw)
{
    var evt = raw.ToInputEvent;
    var fields = () => (IReadOnlyList<TextInputState>)CellLayout.TextInputs(arranged);
    var context = new TextInputInteraction.KeyContext(tracker, focus, Redraw,
        ActiveSearch: page == PilotPage.Flights ? flights.Wizard.ActiveSearch : null, TabFields: fields);
    Redraw();

    // A click: the arranged tree answers, as the window's regions do; a field's click focuses it.
    if (evt is InputEvent.MouseUp(var x, var y, MouseButton.Left))
    {
        var cell = terminal.CellSize;
        if (cell.Width > 0 && cell.Height > 0 && CellLayout.HitTest(arranged, (int)x / cell.Width, (int)y / cell.Height) is HitResult.TextInputHit field)
        {
            focus.Focus(field.Input);
        }
        return;
    }

    // Shift+Enter (Alt+Enter, where a terminal cannot tell Shift) in the chat's box is a new line, not a send.
    if (evt is InputEvent.KeyDown && chat.HandleNewLine(evt))
    {
        return;
    }

    // Typing into a focused field: a printable key is text; everything else is the field's (Enter commits, Escape leaves).
    if (focus.Current is { } current)
    {
        if (raw.KeyChar is { } rune && !Rune.IsControl(rune) && (raw.Modifiers & ~ConsoleModifiers.Shift) == 0)
        {
            TextInputInteraction.HandleText(current, rune.ToString());
            return;
        }
        if (evt is InputEvent.KeyDown(var fieldKey, var fieldModifiers))
        {
            TextInputInteraction.HandleKey(fieldKey, fieldModifiers, context);
        }
        return;
    }

    switch (evt)
    {
        case InputEvent.KeyDown(InputKey.Q, InputModifier.None) or InputEvent.KeyDown(InputKey.C, InputModifier.Ctrl):
            stop.Cancel();
            break;
        // Back and Forward through the window's history: Alt+Left and Alt+Right (PilotChrome's own keys), and Backspace for Back
        // here, where no field has the keyboard (a focused field took its keys above, so Backspace there still edits text).
        case InputEvent.KeyDown(InputKey.Backspace, InputModifier.None):
            chrome.Back();
            break;
        case InputEvent.KeyDown navKey when chrome.HandleInput(navKey):
            break;
        // The page keys are moves the history records; the show callback of the Link calls keeps `page` in step.
        case InputEvent.KeyDown(InputKey.D1, _):
            chrome.Show(PilotPage.Home);
            break;
        case InputEvent.KeyDown(InputKey.D2, _):
            chrome.Show(PilotPage.Sessions);
            break;
        case InputEvent.KeyDown(InputKey.D3, _):
            chrome.Show(PilotPage.Flights);
            break;
        case InputEvent.KeyDown(InputKey.D4, _):
            chrome.Show(PilotPage.Jobs);
            break;
        case InputEvent.KeyDown(InputKey.D5, _):
            chrome.Show(PilotPage.Fleet);
            break;
        case InputEvent.KeyDown(InputKey.D6, _):
            chrome.Show(PilotPage.Settings);
            break;
        case InputEvent.KeyDown(InputKey.D7, _):
            chrome.Show(PilotPage.Repos);
            break;
        case InputEvent.KeyDown(InputKey.D9, _):
            chrome.Show(PilotPage.PullRequests);
            break;
        case InputEvent.KeyDown(InputKey.D8, _):
            chrome.Show(PilotPage.Chat);
            focus.Focus(chat.Input);
            break;
        case InputEvent.KeyDown(InputKey.S, InputModifier.Ctrl):
            chrome.Show(PilotPage.Sessions);
            focus.Focus(sessions.SayInput);
            break;
        case InputEvent.KeyDown(InputKey.T, InputModifier.Ctrl):
            chrome.Show(PilotPage.Chat);
            focus.Focus(chat.Input);
            break;
        case InputEvent.KeyDown(InputKey.Enter, InputModifier.Ctrl) when page == PilotPage.Flights && flights.Wizard.IsOpen:
            flights.Wizard.GoOn();
            break;
        case InputEvent.KeyDown key:
            // Up and Down move the page's selection, as in the window.
            _ = chrome.PageWidget.HandleInput(key);
            break;
    }
}
