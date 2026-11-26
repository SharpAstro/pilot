using System.Runtime.InteropServices;

namespace Pilot.Buddy;

/// <summary>The few Win32 calls the buddy needs that SDL does not wrap: a window with no taskbar button, and a picture of the screen under it.</summary>
internal static class WindowsShell
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern int BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern int DeleteDC(nint dc);

    /// <summary>The screen under a rectangle (what the user sees, windows included), as RGBA rows from the top; null when it could not be read. For the GUI lane's capture.</summary>
    public static byte[]? CaptureScreen(int x, int y, int width, int height)
    {
        if (!OperatingSystem.IsWindows() || width <= 0 || height <= 0)
        {
            return null;
        }
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            // SRCCOPY | CAPTUREBLT: layered and transparent windows are part of what is copied.
            if (BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020 | 0x40000000) == 0)
            {
                return null;
            }
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var bgra = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, bgra, ref info, 0) == 0)
            {
                return null;
            }
            for (var i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                bgra[i + 3] = 255;
            }
            return bgra;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

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
