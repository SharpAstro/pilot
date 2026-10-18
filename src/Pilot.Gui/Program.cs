using DIR.Lib;
using Pilot.Contracts;
using Pilot.Gui;
using SdlVulkan.Renderer;
using SharpAstro.AppShell;

// pilot v2's GUI: every session as a row. It reads a snapshot file for now (the bundled sample unless --snapshot
// names one); the live node is a later leg (gui-say).
ConsoleOutput.AttachToParent();

if (GuiOptions.Parse(args, out var error) is not { } options)
{
    Console.Error.WriteLine(error);
    return 2;
}

var snapshotPath = Path.GetFullPath(options.Snapshot ?? SessionsCapture.SamplePath);
SnapshotEvent snapshot;
try
{
    snapshot = SessionsCapture.LoadSnapshot(snapshotPath);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
{
    Console.Error.WriteLine($"pilot-gui: cannot read the snapshot {snapshotPath}: {ex.Message}");
    return 1;
}

var source = options.Snapshot is null
    ? $"sample snapshot {Path.GetFileName(snapshotPath)} (no node yet)"
    : $"snapshot file {Path.GetFileName(snapshotPath)}";
var screen = SessionsCapture.ScreenFor(snapshot, source);

if (options.Capture is { } capturePath)
{
    var rowsPath = SessionsCapture.Write(screen, capturePath, options.Width, options.Height);
    Console.WriteLine($"pilot-gui: rendered {screen.Summary} from {snapshotPath} at {options.Width}x{options.Height}");
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

var announced = -1;
loop.OnResize = (w, h) =>
{
    width = (int)w;
    height = (int)h;
    view.DpiScale = window.DisplayScale;
};
loop.OnRender = () =>
{
    view.Ui.FrameId++;
    view.Render(screen, new RectF32(0, 0, width, height));
    router.AfterPaint();

    // Say which row is selected whenever it changes, so stdout follows what the window shows.
    if (view.Selected != announced && view.Selected >= 0 && view.Selected < screen.Lines.Count)
    {
        announced = view.Selected;
        Console.WriteLine($"pilot-gui: selected {screen.Lines[announced].ToDumpLine()}");
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

    return renderer.FontAtlasDirty || router.TooltipNeedsFrame;
};
loop.OnKeyDown = key => router.Handle(key);
loop.OnPointerInput = router.Handle;

Console.WriteLine($"pilot-gui: window \"{Title}\" open (pid {Environment.ProcessId}), {width}x{height} px at scale {window.DisplayScale:0.##}");
Console.WriteLine($"pilot-gui: showing {screen.Summary}");
Console.WriteLine($"pilot-gui: data from {snapshotPath}, as of {snapshot.At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC{(options.Snapshot is null ? " (the bundled sample; no node yet)" : "")}");
Console.WriteLine("pilot-gui: Up/Down select a row; close the window to quit.");
if (options.ExitAfter is { } after)
{
    Console.WriteLine($"pilot-gui: closing by itself after {after.TotalSeconds:0.##}s (--exit-after)");
    cts.CancelAfter(after);
}

loop.Run(cts.Token);
Console.WriteLine(cts.IsCancellationRequested ? "pilot-gui: --exit-after reached; window closed." : "pilot-gui: window closed.");
return 0;
