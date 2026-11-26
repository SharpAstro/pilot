using System.Runtime.InteropServices;

namespace Pilot.Buddy;

/// <summary>The few Win32 calls the buddy needs that SDL does not wrap: a window with no taskbar button, and a picture of the screen under it.</summary>
internal static partial class WindowsShell
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>
    /// Makes <paramref name="hwnd"/> a tool window: no taskbar button and no Alt+Tab entry. Called while the window is still hidden, so the shell
    /// never lists it (the style of a shown window is only re-read by the taskbar when it is hidden and shown again).
    /// </summary>
    public static void HideFromTaskbar(nint hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0)
        {
            return;
        }
        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)((style | WsExToolWindow) & ~WsExAppWindow));
    }
}
