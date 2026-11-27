using Pilot.Ui;
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
    private readonly WindowsSessionKind _session = new();
    private readonly BuddyPresence _presence;
    private int _sessionChanged;
    private BuddySettingsWatcher? _watcher;
    private BuddySettings? _fromDisk;

    private SdlVulkanApp? _app;
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

    private nint _handCursor, _arrowCursor;
    private bool _handShown;

    // The hand over a pressable peek row, the arrow elsewhere; set only when it changes.
    private void ShowCursor(bool hand)
    {
        if (hand == _handShown)
        {
            return;
        }
        _handShown = hand;
        if (hand)
        {
            _handCursor = _handCursor != 0 ? _handCursor : CreateSystemCursor(SystemCursor.Pointer);
            SetCursor(_handCursor);
        }
        else
        {
            _arrowCursor = _arrowCursor != 0 ? _arrowCursor : CreateSystemCursor(SystemCursor.Default);
            SetCursor(_arrowCursor);
        }
    }

    public BuddyHost(BuddyOptions options, Action<string> log, NodeClient client, IGuiHandOff handOff, bool otherNode = false)
    {
        _options = options;
        _log = log;
        _settings = BuddySettings.Load(_settingsPath);
        _theme = BuddyTheme.Read();
        _model = new BuddyModel(_time);
        _feed = new BuddyFeed(_model, _time, () => _settings) { OtherNode = otherNode };
        _chat = new BuddyChat(new NodeBuddyClient(client), handOff, () => _feed.ChatDisabledReason, _gate);
        _relay = new BuddyRelay(_model, _feed, _time, _gate);
        _desk = new BuddyDesk(_model, _feed, _chat, handOff, this, () => _settings, Apply, _time, TimeZoneInfo.Local, _gate, Wake, log);
        _presence = new BuddyPresence(_session, _feed, () => _settings, ShowWindow);
        _policy = new BuddyFramePolicy(_model, () => _desk.Version, _desk.ContentKey, () => _settings.SnoozedUntil, _time, () => _presence.Hidden);
        _model.LongSleep = _settings.LongSleep;
        Link = new BuddyNodeLink(client, _feed, _chat, _gate, Wake, log, TimeSpan.FromSeconds(5));
        Relay = _relay;
        _relay.Changed = Wake;
        _relay.MemoryReport = MemoryReport;
        _relay.OpenSettings = _desk.ShowSettings;
        // A console or remote connect arrives on SDL's message pump: it is noted and the loop woken, never polled.
        _session.Changed += () =>
        {
            Interlocked.Exchange(ref _sessionChanged, 1);
            Wake();
        };
    }

    // Shows or hides the window for the presence rule; a window shown again draws its scene at once.
    private void ShowWindow(bool shown)
    {
        if (_window is null)
        {
            return;
        }
        if (shown)
        {
            _window.Show();
            _window.SetAlwaysOnTop(true);
            _policy.Redraw();
        }
        else
        {
            HideWindow(_window.Handle);
        }
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
        // The GUI's Settings pane writes the same file: a different one on disk is taken up by the loop (no polling, the watcher's timer wakes it).
        _watcher = new BuddySettingsWatcher(_settingsPath, changed =>
        {
            Volatile.Write(ref _fromDisk, changed);
            Wake();
        });
        _app = SdlVulkanApp.Create(appMetadata: new SdlAppMetadata(Name: "pilot-buddy", Version: null, Identifier: "com.sharpastro.pilot.buddy"));
        // No ordinary hidden window first (the probe makes one): CreateTransparentWindow makes the shared device from its own surface, so the extra window,
        // its swapchain and its context are memory the cat never needed (#1381 section 11).

        // The window is measured from the cat at the largest size, whatever is chosen now (the swapchain is not recreated): Large is only about 30 design px taller
        // than Medium, so a later choice of any size draws at once and the window never has to follow it.
        var (designWidth, designHeight) = BuddyWindowSize.Measure(_font, BuddySettings.SizeScales.Max());
        _window = _app.CreateTransparentWindow("pilot-buddy", (int)designWidth, (int)designHeight, borderless: true, alwaysOnTop: true, focusable: true, hidden: true);
        // The design is drawn at the display's scale: the window is asked for that many pixels, and the scale is what it got.
        var wanted = Math.Max(_window.DisplayScale, 1f);
        _window.SetSizeInPixels((int)MathF.Ceiling(designWidth * wanted), (int)MathF.Ceiling(designHeight * wanted));
        _window.GetSizeInPixels(out _pixelWidth, out _pixelHeight);
        GetWindowSize(_window.Handle, out _unitWidth, out _unitHeight);
        _density = _unitWidth > 0 ? (float)_pixelWidth / _unitWidth : 1f;
        _scale = _pixelWidth / designWidth;

        _context = VulkanContext.CreateTransparentForSharedDevice(_app.DeviceFor(_window), _window, (uint)_pixelWidth, (uint)_pixelHeight);
        // One SDF atlas page of AtlasPageDim squared (the renderer's default is 2048, 4 MB of R8 and its upload ring): the cat draws a few dozen glyphs, and a
        // page appends when one fills, so a small first page costs nothing later.
        _renderer = new VkRenderer(_context, (uint)_pixelWidth, (uint)_pixelHeight, sdfInitialAtlasDim: AtlasPageDim);
        _warmer = new BuddyGlyphWarmer(new RendererAtlas(_renderer, _font));
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
        _session.Watch(_window.GetNativeWindowHandle());
        lock (_gate)
        {
            _presence.Update();
        }
        if (!_presence.Hidden)
        {
            _window.Show();
            _window.SetAlwaysOnTop(true);
        }

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

    // The corner the scene draws the cat in now: the chosen one in Corner placement, and in Free the one with room for the bubble and the cards where the cat was dropped.
    private int _sceneCorner;
    private AwayPhase _placedPhase = AwayPhase.Present;

    /// <summary>The cat's own box in window units, at the size chosen now: what Free placement keeps on the work area.</summary>
    private BuddyPlacement.CatBox CatBox()
    {
        var unit = _scale / _density;
        var cs = _settings.SizeScale;
        return new BuddyPlacement.CatBox((int)MathF.Round((GrumpCat.Width + 4) * cs * unit), (int)MathF.Round((GrumpCat.CushionY + GrumpCat.CushionHeight) * cs * unit), (int)MathF.Round(BuddyScene.Margin * unit));
    }

    /// <summary>The side of the first SDF atlas page: 1024 squared holds the cat's glyphs (the printable ASCII of the card and bubble text) with room to spare.</summary>
    private const int AtlasPageDim = 1024;

    private BuddyGlyphWarmer? _warmer;

    // The renderer's atlas as the warmer sees it; an SDF glyph is rasterised at one size and scaled, so the size passed is only the key's.
    private sealed class RendererAtlas(VkRenderer renderer, string font) : IBuddyAtlas
    {
        public void PreWarm(System.Text.Rune glyph) => renderer.PreWarmSdfGlyph(font, 14, glyph);
    }

    /// <summary>The memory report the cat's MCP tool serves (read under the gate, so the window and the mirror are not changing).</summary>
    private string MemoryReport()
    {
        lock (_gate)
        {
            return new BuddyMemory(
                () => _context is null ? null : new BuddyGpuFacts(
                    (int)_context.SwapchainWidth, (int)_context.SwapchainHeight, null, _context.MainTargetHasDepth, AtlasPageDim, _warmer?.Warmed ?? 0, _context.AlphaPresentation.ToString()),
                () =>
                {
                    var m = _feed.Mirror;
                    return new BuddyMirrorCounts(m.Cards.Count, m.Sessions.Count, m.Flights.Count, m.Legs.Count, m.Merges.Count, m.Jobs.Count, m.Issues.Count);
                }).Report();
        }
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
        var body = MeanDifference(after, _backdrop, (int)(layout.CatX + 80 * layout.CatDevice), (int)(layout.CatY + 70 * layout.CatDevice), (int)(40 * layout.CatDevice), (int)(20 * layout.CatDevice));
        // Where a click at the cat's body would go, as the desktop says: this window (caught) or another (through).
        _window!.GetPosition(out var wx, out var wy);
        var at = WindowsShell.WindowAt((int)(wx * _density + layout.CatX + 100 * layout.CatDevice), (int)(wy * _density + layout.CatY + 80 * layout.CatDevice));
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
                if (Interlocked.Exchange(ref _sessionChanged, 0) == 1)
                {
                    _presence.Update();
                }
                TakeFromDisk();
                SnoozeEnded();
                // In Free the cat walks off the nearest side and its paw badge sits on that screen edge: the window follows the phase (the dropped place comes back with the cat).
                if (_settings.IsFree && _model.Phase != _placedPhase)
                {
                    Place();
                }
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
                ShowCursor(_desk.OverRow);
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
        var view = BuddyScene.ViewOf(_model) with { BadgeWhileWalking = !_settings.IsFree };
        var corner = _sceneCorner;
        var catSize = _settings.SizeScale;
        _policy.Time(() =>
        {
            SceneLayout layout;
            using (var cpu = new RgbaImageRenderer((uint)_pixelWidth, (uint)_pixelHeight))
            {
                // The CPU pass names every text the scene draws: the glyphs new to the atlas are rasterised before the GPU frame begins, so that
                // frame's flush uploads them and its draw finds them (BuddyGlyphWarmer says why the first frame used to be empty).
                Pen<RgbaImage>.TextSeen = _warmer!.Collect;
                try
                {
                    layout = BuddyScene.Draw(cpu, _font, palette, view, _scale, false, corner, _desk.CardFactory<RgbaImage>(), catSize);
                }
                finally
                {
                    Pen<RgbaImage>.TextSeen = null;
                }
                _warmer.Warm();
                var geometry = new BuddyGeometry(layout);
                _desk.Geometry = geometry;
                SetShape(BuddySilhouette.Of(cpu.Surface));
                SetClickThrough(BuddySilhouette.PassesClicks(geometry, _model.Current is null, _settings.ClicksPassThrough));
            }
            if (_renderer!.BeginFrame(new RGBAColor32(0, 0, 0, 0)))
            {
                BuddyScene.Draw(_renderer, _font, palette, view, _scale, false, corner, _desk.CardFactory<VulkanContext>(), catSize);
                _renderer.EndFrame();
            }
        });
        // Anything the warm-up missed was skipped this frame and the atlas says so: draw the same state again, a bounded number of times.
        _policy.AtlasSettled(_renderer!.FontAtlasDirty);
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

    /// <summary>Takes up settings the watcher found on disk: the cat's own save is not one (the watcher is told it, so it stays silent).</summary>
    private void TakeFromDisk()
    {
        if (Interlocked.Exchange(ref _fromDisk, null) is not { } changed)
        {
            return;
        }
        var cornerMoved = Moves(changed);
        _settings = changed;
        _model.LongSleep = changed.LongSleep;
        _presence.Update();
        _feed.Refresh();
        if (cornerMoved)
        {
            Place();
        }
    }

    private void Apply(BuddySettings changed)
    {
        var cornerMoved = Moves(changed);
        _settings = changed;
        _model.LongSleep = changed.LongSleep;
        _presence.Update();
        // A file read the watcher handed over before this save is older than it: dropped, so TakeFromDisk never puts it back over the card's choice.
        Interlocked.Exchange(ref _fromDisk, null);
        _watcher?.Remember(changed);
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

    /// <summary>Whether <paramref name="changed"/> puts the window somewhere else: another corner or placement, or (in Free) another stored position for this monitor.</summary>
    private bool Moves(BuddySettings changed) =>
        changed.CornerIndex != _settings.CornerIndex || changed.PlacementIndex != _settings.PlacementIndex || (changed.IsFree && changed.SizeIndex != _settings.SizeIndex)
        || (changed.IsFree && changed.Monitor is { } m && changed.FreeAt(m) != _settings.FreeAt(m));

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
            var phase = _settings.IsFree ? _model.Phase : AwayPhase.Present;
            var (x, y, corner) = BuddyPlacement.Resolve(_settings, area, _unitWidth, _unitHeight, CatBox(), phase);
            _window!.SetPosition(x, y);
            if (corner != _sceneCorner || phase != _placedPhase)
            {
                _policy.Redraw();
            }
            _sceneCorner = corner;
            _placedPhase = phase;
        }
    }

    /// <inheritdoc />
    public void TextInput(bool on)
    {
        if (_window is null)
        {
            return;
        }
        if (on)
        {
            // A click on the cat need not have given the always-on-top window the keyboard: it is raised to take it.
            RaiseWindow(_window.Handle);
            StartTextInput(_window.Handle);
        }
        else
        {
            StopTextInput(_window.Handle);
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
        if (BuddyPlacement.Drop(_settings, Displays(), x, y, _unitWidth, _unitHeight, CatBox(), _sceneCorner) is { } dropped)
        {
            // Corner: the chosen corner of the monitor it was dropped on is where it returns. Free: where it was dropped, clamped onto the work area, is remembered for that monitor.
            Apply(dropped.Settings);
            Place();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watcher?.Dispose();
        _session.Dispose();
        _renderer?.Dispose();
        _context?.Dispose();
        _window?.Dispose();
        _app?.Dispose();
    }
}
