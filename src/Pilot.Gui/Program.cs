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

if (options.SayTo is { } sayTo)
{
    using var oneShot = new NodeClient(socketPath);
    return await GuiSay.RunAsync(oneShot, sayTo, options.SayText ?? "", Console.Out, CancellationToken.None);
}

SessionsScreen screen;
SnapshotEvent? snapshot = null;
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
    using var oneShot = new NodeClient(socketPath);
    try
    {
        var rows = await oneShot.GetSessionsAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        screen = new SessionsScreen(SessionLine.Build(rows, now), $"live from the node on {socketPath}", now);
    }
    catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException)
    {
        Console.Error.WriteLine($"pilot-gui: no node answers on {socketPath} ({ex.Message}); start pilot-node, or pass --snapshot <file>");
        return 1;
    }
}
else
{
    screen = new SessionsScreen([], $"connecting to the node on {socketPath}", DateTimeOffset.UtcNow);
}

if (options.Capture is { } capturePath)
{
    var rowsPath = SessionsCapture.Write(screen, capturePath, options.Width, options.Height, canSay: snapshot is null);
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
window.GetSizeInPixels(out var width, out var height);
using var context = VulkanContext.Create(window.Instance, window.Surface, (uint)width, (uint)height);
using var renderer = new VkRenderer(context, (uint)width, (uint)height);
var view = new SessionsView<VulkanContext>(renderer) { FontPath = SessionsCapture.FontPath, DpiScale = window.DisplayScale };

using var cts = new CancellationTokenSource();
var tracker = new BackgroundTaskTracker();
var loop = new SdlEventLoop(window, renderer) { BackgroundColor = new RGBAColor32(0x12, 0x12, 0x18, 0xff) };
var router = new InputRouter(view.Ui, tracker, loop.RequestRedraw)
{
    Widgets = () => [view],
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
using var client = snapshot is null ? new NodeClient(socketPath) : null;
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
    feed.Start();
}

var announced = (string?)null;
var drawnVersion = -1L;
var drawnAt = DateTimeOffset.MinValue;
loop.OnResize = (w, h) =>
{
    width = (int)w;
    height = (int)h;
    view.DpiScale = window.DisplayScale;
};
loop.OnRender = () =>
{
    if (feed is not null)
    {
        drawnVersion = feed.Version;
        drawnAt = DateTimeOffset.UtcNow;
        screen = feed.Screen(drawnAt);
    }

    view.Ui.FrameId++;
    view.Render(screen, new RectF32(0, 0, width, height));
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
        view.CollectPaintedRegions(regions);
        return regions;
    },
});
#endif

loop.OnKeyDown = key => router.Handle(key);
loop.OnTextInput = text => router.Handle(new InputEvent.TextInput(text));
loop.OnPointerInput = router.Handle;

Console.WriteLine($"pilot-gui: window \"{Title}\" open (pid {Environment.ProcessId}), {width}x{height} px at scale {window.DisplayScale:0.##}");
Console.WriteLine(snapshot is null
    ? $"pilot-gui: live from the node on {socketPath}"
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
