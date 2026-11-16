using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pilot.Node;

/// <summary>What answers whether this node can run a job that opens a window (<c>pilot run --gui</c>); a test registers its own.</summary>
public interface IDesktopProbe
{
    /// <summary>Null when the node has an interactive desktop, else why not.</summary>
    string? Why();
}

/// <summary>
/// Whether the node's process has an interactive desktop: on Windows its session id is not 0 and its window station is the
/// interactive <c>WinSta0</c> (a service runs in session 0 on a station of its own); on Linux <c>DISPLAY</c> or <c>WAYLAND_DISPLAY</c> is set.
/// </summary>
public static partial class DesktopCapability
{
    /// <summary>Null when this process can show a window, else why not.</summary>
    public static string? Why()
    {
        if (OperatingSystem.IsWindows())
        {
            return WhyWindows();
        }
        if (OperatingSystem.IsLinux())
        {
            return WhyLinux(Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        }
        return "this platform has no GUI lane";
    }

    /// <summary>The Linux answer for the two display variables.</summary>
    public static string? WhyLinux(string? display, string? wayland) =>
        string.IsNullOrEmpty(display) && string.IsNullOrEmpty(wayland) ? "neither DISPLAY nor WAYLAND_DISPLAY is set" : null;

    /// <summary>The Windows answer for a session id and window station name.</summary>
    public static string? WhyWindows(int sessionId, string? station) =>
        sessionId == 0 ? "the node runs in session 0, which has no desktop"
        : !string.Equals(station, "WinSta0", StringComparison.OrdinalIgnoreCase) ? $"the node's window station is {station ?? "unknown"}, not the interactive WinSta0"
        : null;

    private static string? WhyWindows()
    {
        using var self = Process.GetCurrentProcess();
        return WhyWindows(self.SessionId, StationName());
    }

    private static string? StationName()
    {
        try
        {
            var station = GetProcessWindowStation();
            if (station == IntPtr.Zero)
            {
                return null;
            }
            var buffer = new byte[512];
            return GetUserObjectInformationW(station, 2, buffer, buffer.Length, out var needed) && needed > 2
                ? System.Text.Encoding.Unicode.GetString(buffer, 0, (int)needed).TrimEnd('\0')
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetProcessWindowStation();

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(IntPtr handle, int index, byte[] info, int length, out uint needed);
}
