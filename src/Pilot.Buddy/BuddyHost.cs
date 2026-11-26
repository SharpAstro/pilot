using DIR.Lib;
using Pilot.Contracts;
using SdlVulkan.Renderer;
using SharpAstro.AppShell;
using SharpAstro.Png;
using static SDL3.SDL;

namespace Pilot.Buddy;

/// <summary>
/// The desktop cat's window (DSN-2 v5): a transparent, borderless, always-on-top SDL window with no taskbar entry, built as SdlVulkan.Renderer's
/// TransparencyProbe "buddy" does it (a hidden ordinary window makes the shared device, then <c>CreateTransparentWindow</c> and a context on
/// <c>DeviceFor</c>). It draws only on a change and sleeps on the SDL event queue in between, waking for the next timer (the breath, the end of good
/// news, the end of a snooze); the shape of what was drawn is the window's click-through region.
/// </summary>
internal sealed class BuddyHost : IBuddyWindow, IDisposable
{
    /// <summary>The longest the loop sleeps without a reason: a deadline for the wait, not a poll (every change wakes it sooner).</summary>
    private static readonly TimeSpan LongestSleep = TimeSpan.FromSeconds(30);

    private readonly BuddyOptions _options;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly BuddyModel _model;
    private readonly BuddyFeed _feed;
    private readonly BuddyChat _chat;
    private readonly BuddyRelay _relay;
    private readonly BuddyDesk _desk;
    private readonly BuddyFramePolicy _policy;
    private readonly string _settingsPath = BuddySettings.DefaultPath;
    private readonly string _font = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");
    private BuddySettings _settings;
    private BuddyTheme.Reading _theme;
    private bool _snoozeSeen;

    private SdlVulkanApp? _app;
    private SdlVulkanWindow? _plain;
    private VulkanContext? _plainContext;
    private SdlVulkanWindow? _window;
    private VulkanContext? _context;
    private VkRenderer? _renderer;
    private float _scale = 1f;
    private float _density = 1f;
    private int _pixelWidth, _pixelHeight, _unitWidth, _unitHeight;
    private byte[]? _shape;
    private uint _wakeEvent;
    private int _wakePending;
    private bool _running = true;

    public BuddyHost(BuddyOptions options, Action<string> log, NodeClient client, IGuiHandOff handOff)
    {
        _options = options;
        _log = log;
        _settings = BuddySettings.Load(_settingsPath);
        _theme = BuddyTheme.Read();
        _model = new BuddyModel(_time);
        _feed = new BuddyFeed(_model, _time, () => _settings);
        _chat = new BuddyChat(new NodeBuddyClient(client), handOff, () => _feed.ChatDisabledReason, _gate);
        _relay = new BuddyRelay(_model, _feed, _time, _gate);
        _desk = new BuddyDesk(_model, _feed, _chat, handOff, this, () => _settings, Apply, _time, TimeZoneInfo.Local, _gate, Wake, log);
        _policy = new BuddyFramePolicy(_model, () => _desk.Version, _desk.ContentKey, () => _settings.SnoozedUntil, _time);
        Link = new BuddyNodeLink(client, _feed, _chat, _gate, Wake, log, TimeSpan.FromSeconds(5));
        Relay = _relay;
        _relay.Changed = Wake;
        _relay.OpenSettings = _desk.ShowSettings;
    }

    /// <summary>The reader of the node's stream, started by <see cref="Run"/>.</summary>
    public BuddyNodeLink Link { get; }

    /// <summary>The end of the pipe the MCP server talks to.</summary>
    public BuddyRelay Relay { get; }

    /// <summary>Wakes the loop from any thread; any number of calls before it looks are one wake.</summary>
    public void Wake()
    {
        if (_wakeEvent == 0 || Interlocked.Exchange(ref _wakePending, 1) != 0)
        {
            return;
        }
        var evt = default(Event);
        evt.User.Type = _wakeEvent;
        PushEvent(ref evt);
    }

    /// <summary>Opens the window and runs until it is closed, a capture is done or <c>--exit-after</c> ends it.</summary>
    public int Run(CancellationToken cancellationToken)
    {
        if (!Init(InitFlags.Video | InitFlags.Events))
        {
            _log($"pilot-buddy: SDL did not start: {GetError()}");
            return 1;
        }
        _wakeEvent = RegisterEvents(1);
        _app = SdlVulkanApp.Create(appMetadata: new SdlAppMetadata(Name: "pilot-buddy", Version: null, Identifier: "com.sharpastro.pilot.buddy"));
        // An ordinary hidden window first: it makes the shared device, as the probe does.
        _plain = _app.CreateWindow("pilot-buddy device", 320, 200, maximized: false, hidden: true);
        _plainContext = VulkanContext.CreateForSharedDevice(_app.DeviceFor(_plain), _plain.Surface, 320, 200);

        var (designWidth, designHeight) = BuddyWindowSize.Measure(_font);
        _window = _app.CreateTransparentWindow("pilot-buddy", (int)designWidth, (int)designHeight, borderless: true, alwaysOnTop: true, focusable: true, hidden: true);
        // The design is drawn at the display's scale: the window is asked for that many pixels, and the scale is what it got.
        var wanted = Math.Max(_window.DisplayScale, 1f);
        _window.SetSizeInPixels((int)MathF.Ceiling(designWidth * wanted), (int)MathF.Ceiling(designHeight * wanted));
        _window.GetSizeInPixels(out _pixelWidth, out _pixelHeight);
        GetWindowSize(_window.Handle, out _unitWidth, out _unitHeight);
        _density = _unitWidth > 0 ? (float)_pixelWidth / _unitWidth : 1f;
        _scale = _pixelWidth / designWidth;

        _context = VulkanContext.CreateTransparentForSharedDevice(_app.DeviceFor(_window), _window, (uint)_pixelWidth, (uint)_pixelHeight);
        _renderer = new VkRenderer(_context, (uint)_pixelWidth, (uint)_pixelHeight);
        _log($"pilot-buddy: window {_pixelWidth}x{_pixelHeight} px at scale {_scale:0.##}, IsTransparent {_window.IsTransparent}, AlphaPresentation {_context.AlphaPresentation}, "
            + $"theme {(_theme.Dark ? "dark" : "bright")}{(_theme.Reason is { } reason ? $" ({reason})" : string.Empty)}");

        WindowsShell.HideFromTaskbar(_window.GetNativeWindowHandle());
        Place();
        // The first frame is drawn (and its shape set) before the window is shown, so it never shows empty.
        lock (_gate)
        {
            DrawFrame();
        }
        // A capture looks at the screen under the window first without it: whatever the desktop shows there is the known backdrop.
        _backdrop = _options.Capture is null ? null : Grab();
        _window.Show();
        _window.SetAlwaysOnTop(true);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Link.RunAsync(linked.Token);
        // A capture is the window up for --hold (the frame drawn, the compositor given its time), then one look at the screen.
        var deadline = _options.Capture is not null ? _time.GetUtcNow() + _options.Hold
            : _options.ExitAfter is { } after ? _time.GetUtcNow() + after : (DateTimeOffset?)null;
        var exit = 0;
        try
        {
            Loop(deadline);
            if (_options.Capture is { } path)
            {
                exit = TakeCapture(path);
            }
        }
        finally
        {
            linked.Cancel();
            _log(_policy.Summary());
        }
        return exit;
    }

    private byte[]? _backdrop;

    /// <summary>The screen under the window now, RGBA, or null.</summary>
    private byte[]? Grab()
    {
        _window!.GetPosition(out var x, out var y);
        return WindowsShell.CaptureScreen((int)(x * _density), (int)(y * _density), _pixelWidth, _pixelHeight);
    }

    /// <summary>
    /// Writes the screen under the window to <paramref name="path"/> (and the backdrop alone beside it), and says in the log what it shows: whether the
    /// corner away from the cat is the backdrop (transparent and clicks through) and the cat's body is not (drawn).
    /// </summary>
    private int TakeCapture(string path)
    {
        var after = Grab();
        if (after is null || _backdrop is null)
        {
            _log("pilot-buddy: the screen could not be captured (not Windows, or no display)");
            return 1;
        }
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is { Length: > 0 })
        {
            Directory.CreateDirectory(folder);
        }
        File.WriteAllBytes(path, PngWriter.Encode(after, _pixelWidth, _pixelHeight));
        var backdropPath = Path.ChangeExtension(path, ".backdrop.png");
        File.WriteAllBytes(backdropPath, PngWriter.Encode(_backdrop, _pixelWidth, _pixelHeight));

        var layout = _desk.Geometry!.Layout;
        var cornerX = layout.CatX < _pixelWidth / 2f ? _pixelWidth - 16 : 2;
        var cornerY = layout.CatY < _pixelHeight / 2f ? _pixelHeight - 16 : 2;
        var corner = MeanDifference(after, _backdrop, cornerX, cornerY, 14, 14);
        var body = MeanDifference(after, _backdrop, (int)(layout.CatX + 80 * _scale), (int)(layout.CatY + 70 * _scale), (int)(40 * _scale), (int)(20 * _scale));
        // Where a click at the cat's body would go, as the desktop says: this window (caught) or another (through).
        _window!.GetPosition(out var wx, out var wy);
        var at = WindowsShell.WindowAt((int)(wx * _density + layout.CatX + 100 * _scale), (int)(wy * _density + layout.CatY + 80 * _scale));
        var clicks = at == 0 ? "unknown" : at == _window.GetNativeWindowHandle() ? "caught by the cat" : "passing through to the window below";
        _log($"pilot-buddy: capture {path} (backdrop alone: {backdropPath}); AlphaPresentation {_context!.AlphaPresentation}; "
            + $"mean difference from the backdrop per channel: corner {corner} (0 means transparent), cat's body {body} (more than 20 means drawn); a click on the cat's body is {clicks} (click-through is {(_clickThrough ? "on" : "off")}); "
            + (corner < 3 && body > 20 ? "VERDICT: as expected" : "VERDICT: NOT as expected, look at the pictures"));
        return corner < 3 && body > 20 ? 0 : 3;
    }

    private int MeanDifference(byte[] a, byte[] b, int x, int y, int w, int h)
    {
        long sum = 0;
        var count = 0;
        for (var j = Math.Max(0, y); j < Math.Min(_pixelHeight, y + h); j++)
        {
            for (var i = Math.Max(0, x); i < Math.Min(_pixelWidth, x + w); i++)
            {
                var at = (j * _pixelWidth + i) * 4;
                sum += Math.Abs(a[at] - b[at]) + Math.Abs(a[at + 1] - b[at + 1]) + Math.Abs(a[at + 2] - b[at + 2]);
                count += 3;
            }
        }
        return count == 0 ? 0 : (int)(sum / count);
    }

    private void Loop(DateTimeOffset? deadline)
    {
        while (_running)
        {
            DateTimeOffset wakeAt;
            lock (_gate)
            {
                SnoozeEnded();
                if (_policy.NeedsFrame())
                {
                    DrawFrame();
                }
                wakeAt = _policy.NextWake;
            }
            var now = _time.GetUtcNow();
            if (deadline is { } end)
            {
                if (now >= end)
                {
                    return;
                }
                wakeAt = wakeAt < end ? wakeAt : end;
            }
            // Sleep on the event queue until the next timer (or an event, or a wake from another thread); the cap is a deadline, not a poll.
            var sleep = wakeAt - now;
            var ms = (int)Math.Clamp(sleep.TotalMilliseconds, 1, LongestSleep.TotalMilliseconds);
            if (!WaitEventTimeout(out var evt, ms))
            {
                continue;
            }
            lock (_gate)
            {
                do
                {
                    Handle(evt);
                }
                while (_running && PollEvent(out evt));
            }
        }
    }

    /// <summary>The end of a snooze is the one change that comes with no event: the feed re-reads the cards then.</summary>
    private void SnoozeEnded()
    {
        var snoozed = _settings.IsSnoozed(_time.GetUtcNow());
        if (_snoozeSeen && !snoozed)
        {
            _feed.Refresh();
        }
        _snoozeSeen = snoozed;
    }

    private void Handle(Event evt)
    {
        var type = (EventType)evt.Type;
        if (evt.Type == _wakeEvent)
        {
            Volatile.Write(ref _wakePending, 0);
            return;
        }
        switch (type)
        {
            case EventType.Quit:
            case EventType.WindowCloseRequested:
                _running = false;
                // Closing: the buddy no longer shows the cards, so the node's toasts come back at once (the node forgets it with the stream too).
                _feed.WindowVisible = false;
                break;
            case EventType.MouseMotion:
                _desk.PointerMoved(evt.Motion.X * _density, evt.Motion.Y * _density);
                break;
            case EventType.MouseButtonDown:
                _desk.ButtonDown(evt.Button.Button, evt.Button.X * _density, evt.Button.Y * _density);
                break;
            case EventType.MouseButtonUp:
                _desk.ButtonUp(evt.Button.Button, evt.Button.X * _density, evt.Button.Y * _density);
                break;
            case EventType.WindowMouseLeave:
                _desk.PointerLeft();
                break;
            case EventType.WindowFocusLost:
                _desk.FocusLost();
                break;
            case EventType.TextInput:
                _desk.TextTyped(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(evt.Text.Text) ?? string.Empty);
                break;
            case EventType.KeyDown:
                switch (evt.Key.Key)
                {
                    case Keycode.Escape:
                        _desk.Escape();
                        break;
                    case Keycode.Backspace:
                        _desk.BackspacePressed();
                        break;
                    case Keycode.Return or Keycode.KpEnter:
                        _desk.EnterPressed();
                        break;
                }
                break;
            case EventType.SystemThemeChanged:
                _theme = BuddyTheme.Read();
                _desk.Touch();
                break;
            case EventType.WindowDisplayChanged or EventType.DisplayAdded or EventType.DisplayRemoved:
                Place();
                break;
        }
    }

    /// <summary>One frame: the scene on the CPU first (its layout and its click-through shape, set before anything shows), then the same scene on the GPU.</summary>
    private void DrawFrame()
    {
        var palette = BuddyTheme.PaletteFor(_theme);
        var view = BuddyScene.ViewOf(_model, _feed.Summary);
        var corner = _settings.CornerIndex;
        _policy.Time(() =>
        {
            SceneLayout layout;
            using (var cpu = new RgbaImageRenderer((uint)_pixelWidth, (uint)_pixelHeight))
            {
                layout = BuddyScene.Draw(cpu, _font, palette, view, _scale, false, corner, _desk.CardFactory<RgbaImage>());
                var geometry = new BuddyGeometry(layout);
                _desk.Geometry = geometry;
                SetShape(BuddySilhouette.Of(cpu.Surface));
                SetClickThrough(BuddySilhouette.PassesClicks(geometry, _model.Current is null, _settings.ClicksPassThrough));
            }
            if (_renderer!.BeginFrame(new RGBAColor32(0, 0, 0, 0)))
            {
                BuddyScene.Draw(_renderer, _font, palette, view, _scale, false, corner, _desk.CardFactory<VulkanContext>());
                _renderer.EndFrame();
            }
        });
    }

    private bool _clickThrough;

    private void SetClickThrough(bool through)
    {
        if (through != _clickThrough)
        {
            _clickThrough = through;
            WindowsShell.SetClickThrough(_window!.GetNativeWindowHandle(), through);
        }
    }

    /// <summary>Sets the window's click-through shape, only when it differs from the one set (it costs a few milliseconds).</summary>
    private void SetShape(RgbaImage mask)
    {
        if (_shape is not null && _shape.AsSpan().SequenceEqual(mask.Pixels))
        {
            return;
        }
        _shape = (byte[])mask.Pixels.Clone();
        if (!_window!.SetShape(mask))
        {
            _log("pilot-buddy: the window's shape was not set; clicks will not pass through");
        }
    }

    private void Apply(BuddySettings changed)
    {
        var cornerMoved = changed.CornerIndex != _settings.CornerIndex;
        _settings = changed;
        if (!changed.Save(_settingsPath))
        {
            _log($"pilot-buddy: the settings were not saved to {_settingsPath}");
        }
        _feed.Refresh();
        if (cornerMoved)
        {
            Place();
        }
    }

    private List<DisplayArea> Displays()
    {
        var list = new List<DisplayArea>();
        var ids = GetDisplays(out var count);
        for (var i = 0; ids is not null && i < count; i++)
        {
            if (GetDisplayUsableBounds(ids[i], out var bounds))
            {
                list.Add(new DisplayArea(GetDisplayName(ids[i]) ?? $"display {ids[i]}", bounds.X, bounds.Y, bounds.W, bounds.H));
            }
        }
        return list;
    }

    /// <summary>Puts the window in the chosen corner of the chosen monitor (the primary one when none was chosen or it is gone).</summary>
    private void Place()
    {
        if (BuddyPlacement.Chosen(Displays(), _settings.Monitor) is { } area)
        {
            var (x, y) = BuddyPlacement.Corner(area, _unitWidth, _unitHeight, _settings.CornerIndex);
            _window!.SetPosition(x, y);
        }
    }

    /// <inheritdoc />
    public void MoveBy(int dx, int dy)
    {
        _window!.GetPosition(out var x, out var y);
        _window.SetPosition(x + (int)MathF.Round(dx / _density), y + (int)MathF.Round(dy / _density));
    }

    /// <inheritdoc />
    public void Dropped()
    {
        _window!.GetPosition(out var x, out var y);
        if (BuddyPlacement.Under(Displays(), x, y, _unitWidth, _unitHeight) is { } area)
        {
            // The chosen corner of the monitor it was dropped on is where it returns, and is remembered for it.
            Apply(_settings with { Monitor = area.Name });
            Place();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _renderer?.Dispose();
        _context?.Dispose();
        _window?.Dispose();
        _plainContext?.Dispose();
        _plain?.Dispose();
        _app?.Dispose();
    }
}
