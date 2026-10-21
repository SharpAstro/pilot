using System.CommandLine;
using System.Text;
using Console.Lib;
using DIR.Lib;
using Pilot.Commands;
using Pilot.Contracts;

// pilot-tui: the dashboard in a terminal. The GUI's own pages and panels (Pilot.Ui), their trees arranged in cells by
// Console.Lib rather than painted on pixels: the Sessions page (1) and the Flights page (2), its command bar and
// buttons, and the watchtower's panel with its cards, all live from this machine's node or one on the LAN.
var nodeOption = new Option<string?>("--node") { Description = "A node on the LAN, host:port; else PILOT_NODE, else this machine's" };
var socketOption = new Option<string?>("--node-socket") { Description = "This machine's node's socket; else PILOT_NODE_SOCKET, else this user's default" };
var root = new RootCommand("pilot v2's dashboard in a terminal: 1 sessions, 2 flights, Ctrl+K the command bar, Ctrl+T the watchtower, q quits")
{
    Options = { nodeOption, socketOption },
};
var parsed = root.Parse(args);
if (parsed.Errors.Count > 0 || parsed.Action is System.CommandLine.Help.HelpAction)
{
    return parsed.Invoke();
}

var nodeTarget = parsed.GetValue(nodeOption) ?? (Environment.GetEnvironmentVariable(NodeTargets.EnvironmentVariable) is { Length: > 0 } env ? env : null);
var remote = nodeTarget is not null && NodeClient.IsEndpoint(nodeTarget, out var endpoint) ? endpoint : null;
string[] nodeArgs = remote is not null ? ["--node", remote] : ["--node-socket", NodeSocket.Resolve(parsed.GetValue(socketOption))];
const string Who = "tui";

using var client = NodeTargets.Connect(remote, parsed.GetValue(socketOption));
await using var feed = NodeFeed.For(client);

// The views are the GUI's, generic over the surface; their renderer is never painted with here (the trees are arranged
// in cells), so a one-pixel CPU surface stands in for it.
using var vessel = new RgbaImageRenderer(1, 1);
var sessions = new SessionsView<RgbaImage>(vessel) { CanSay = true };
var chrome = new PilotChrome<RgbaImage>(vessel, sessions);
var flights = chrome.Flights;
var cards = chrome.Cards;
var focus = sessions.Ui.Focus;
var tracker = new BackgroundTaskTracker();
using var stop = new CancellationTokenSource();
var dirty = true;
void Redraw() => dirty = true;

// The same wiring as the window's: a verb on the node for each button, the CLI's own tree for the command bar.
flights.CanCommand = true;
flights.OnCommand = async line =>
{
    flights.Say($"> {line}");
    // A connect waits on the other machine's person; the LAN line says so while it does.
    var asking = DashboardCommands.ConnectTarget(line);
    if (asking is not null)
    {
        flights.Lan?.Asking(asking, DateTimeOffset.UtcNow);
    }
    flights.Say(await DashboardCommands.RunAsync(line, nodeArgs, Who, stop.Token));
    if (asking is not null)
    {
        flights.Lan?.Answered(asking);
    }
    Redraw();
};
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
flights.Wizard.Changed = Redraw;
flights.OnAction = action => _ = Task.Run(async () =>
{
    flights.Say(await DashboardCommands.ActAsync(client, action, Who, stop.Token));
    Redraw();
});
flights.OnOpen = location => flights.Say(DashboardCommands.Open(location) ? $"opened {location}" : $"could not open {location}");
sessions.Transcript.Load = async (rowId, after, ct) => (await client.GetTranscriptAsync(rowId, after, ct)) is { Success: true, Response: { } page }
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
    sessions.SayStatus = await DashboardCommands.ActAsync(client, action, Who, stop.Token);
    Redraw();
});
sessions.OnSay = async (line, text) =>
{
    var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(line.Row.Id), Args = [text], Who = Who }, stop.Token);
    sessions.SayStatus = envelope.Success ? $"said to {line.Name}" : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    Redraw();
};
async Task CardAsync(string verb, CardRow card, string[] cardArgs)
{
    var envelope = await client.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.CardTarget(card.Id), Args = cardArgs, Who = Who }, stop.Token);
    flights.Say(envelope.Success ? $"{verb} {card.Id}: done" : $"{verb} {card.Id}: {envelope.Response?.Error ?? envelope.Error}");
    Redraw();
}
cards.OnAnswer = (card, number) => _ = CardAsync(Verbs.Answer, card, [number.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
cards.OnDismiss = card => _ = CardAsync(Verbs.Dismiss, card, []);
cards.CanTell = true;
cards.OnTell = async text =>
{
    var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = [text], Who = Who }, stop.Token);
    cards.TellStatus = envelope.Success ? "told" : $"refused: {envelope.Response?.Error ?? envelope.Error}";
    Redraw();
};
cards.OnOpen = url => DashboardCommands.Open(url);
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

var page = PilotPage.Sessions;
// The session menu's New flight in its repo: the Flights page, with the wizard open on it.
sessions.OnNewFlight = repo =>
{
    page = PilotPage.Flights;
    flights.Wizard.Open(repo);
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
    if (dirty || feed.Version != drawnVersion || now - drawnAt >= TimeSpan.FromSeconds(1))
    {
        dirty = false;
        drawnVersion = feed.Version;
        drawnAt = now;
        var (columns, rows) = terminal.Size;
        // Design units per cell, as CellMeasureContext.PixelAuthored counts them: the trees are the GUI's.
        var width = columns * 8f;
        var pageTree = page == PilotPage.Flights ? flights.Tree(feed.Flights(now)) : sessions.Tree(feed.Screen(now));
        var cardsScreen = feed.Cards();
        flights.Jobs = feed.Jobs();
        // The panel takes 38 columns (the GUI's 300 units) when there is room beside a usable page, and something to show.
        var tree = columns >= 120 && cardsScreen.HasAny
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
        case InputEvent.KeyDown(InputKey.D1, _):
            page = PilotPage.Sessions;
            break;
        case InputEvent.KeyDown(InputKey.D2, _):
            page = PilotPage.Flights;
            break;
        case InputEvent.KeyDown(InputKey.K, InputModifier.Ctrl):
            page = PilotPage.Flights;
            focus.Focus(flights.CommandInput);
            break;
        case InputEvent.KeyDown(InputKey.S, InputModifier.Ctrl):
            page = PilotPage.Sessions;
            focus.Focus(sessions.SayInput);
            break;
        case InputEvent.KeyDown(InputKey.T, InputModifier.Ctrl):
            focus.Focus(cards.TellInput);
            break;
        case InputEvent.KeyDown(InputKey.Enter, InputModifier.Ctrl) when page == PilotPage.Flights && flights.Wizard.IsOpen:
            flights.Wizard.GoOn();
            break;
        case InputEvent.KeyDown key:
            // Up and Down move the page's selection, as in the window.
            _ = page == PilotPage.Flights ? flights.HandleInput(key) : sessions.HandleInput(key);
            break;
    }
}
