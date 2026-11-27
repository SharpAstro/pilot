using System.Runtime.InteropServices;
using Pilot.Ui;
using static SDL3.SDL;

namespace Pilot.Buddy;

/// <summary>
/// Where this desktop session runs: on the machine's own console or inside a remote desktop connection (#1381 section 7). A fake in the tests, the
/// Windows one (<see cref="WindowsSessionKind"/>) in the host. It is told, never polled: <see cref="Changed"/> fires when a session change arrives.
/// </summary>
public interface ISessionKind
{
    /// <summary>True while the session is a remote desktop one.</summary>
    bool IsRemote { get; }

    /// <summary>Raised when a console or remote connect or disconnect arrived (the value read again with <see cref="IsRemote"/>); may come from any thread.</summary>
    event Action? Changed;
}

/// <summary>
/// What the cat does about the session it is in: in a remote desktop session, with "Show in remote desktop sessions" off, it is absent: the window is
/// hidden, the buddy says <c>ShowsCards</c> false (so the node's toasts carry on) and no frame is drawn; it comes back on a change to the console.
/// Pure and driven by <see cref="Update"/>, which the host calls when the session kind or the settings changed, so nothing polls.
/// </summary>
public sealed class BuddyPresence(ISessionKind session, BuddyFeed feed, Func<BuddySettings> settings, Action<bool> showWindow)
{
    /// <summary>True while the cat is absent because the session is remote and the setting is off.</summary>
    public bool Hidden => session.IsRemote && !settings().ShowsInRemoteSessions;

    /// <summary>Says again what the window and the feed show: call it at the start, on a session change and on a settings change. The caller holds the gate.</summary>
    public void Update()
    {
        var hidden = Hidden;
        showWindow(!hidden);
        feed.WindowVisible = !hidden;
    }
}

/// <summary>
/// The real session kind on Windows: <c>GetSystemMetrics(SM_REMOTESESSION)</c> for the kind, and <c>WTSRegisterSessionNotification</c> so Windows sends
/// <c>WM_WTSSESSION_CHANGE</c> to the cat's window, read through SDL's Windows message hook (a callback run by SDL's own message pump, so there is no timer
/// and no polling). Elsewhere it is never remote.
/// </summary>
internal sealed class WindowsSessionKind : ISessionKind, IDisposable
{
    private const int SmRemoteSession = 0x1000;
    private const int WmWtsSessionChange = 0x02B1;
    private const int NotifyForThisSession = 0;

    // WTS_CONSOLE_CONNECT, WTS_CONSOLE_DISCONNECT, WTS_REMOTE_CONNECT, WTS_REMOTE_DISCONNECT.
    private const int WtsConsoleConnect = 1, WtsConsoleDisconnect = 2, WtsRemoteConnect = 3, WtsRemoteDisconnect = 4;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(nint hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(nint hwnd);

    // Kept in a field: SDL holds the native pointer of the delegate, so it must not be collected.
    private WindowsMessageHook? _hook;
    private nint _registered;
    private volatile bool _remote;

    /// <summary>Reads the kind now; no hook yet (<see cref="Watch"/> adds it).</summary>
    public WindowsSessionKind()
    {
        _remote = Read();
    }

    /// <inheritdoc />
    public bool IsRemote => _remote;

    /// <inheritdoc />
    public event Action? Changed;

    private static bool Read() => OperatingSystem.IsWindows() && GetSystemMetrics(SmRemoteSession) != 0;

    /// <summary>Registers for session notifications on <paramref name="hwnd"/> and hooks SDL's message pump. Main thread, once the window exists.</summary>
    public void Watch(nint hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0 || _hook is not null)
        {
            return;
        }
        _hook = OnMessage;
        SetWindowsMessageHook(_hook, 0);
        if (WTSRegisterSessionNotification(hwnd, NotifyForThisSession))
        {
            _registered = hwnd;
        }
    }

    // The MSG is { HWND hwnd; UINT message; WPARAM wParam; LPARAM lParam; ... }: on a 64-bit Windows the message is at 8, wParam at 16.
    private bool OnMessage(nint userdata, nint msg)
    {
        if (msg != 0 && Marshal.ReadInt32(msg, 8) == WmWtsSessionChange)
        {
            var what = (int)Marshal.ReadIntPtr(msg, 16);
            if (what is WtsConsoleConnect or WtsConsoleDisconnect or WtsRemoteConnect or WtsRemoteDisconnect)
            {
                // A connect says which kind the session is now; a disconnect leaves it to the metric.
                _remote = what switch
                {
                    WtsConsoleConnect => false,
                    WtsRemoteConnect => true,
                    _ => Read(),
                };
                Changed?.Invoke();
            }
        }
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_registered != 0)
        {
            WTSUnRegisterSessionNotification(_registered);
            _registered = 0;
        }
        if (_hook is not null)
        {
            SetWindowsMessageHook(null!, 0);
            _hook = null;
        }
    }
}
