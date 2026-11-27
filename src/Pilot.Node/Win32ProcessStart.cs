using System.Runtime.InteropServices;
using System.Text;

namespace Pilot.Node;

/// <summary>
/// The one wrapper over <c>CreateProcessW</c> that the node's own starts share: a job's detached shell (<see cref="Flights.DetachedShell"/>)
/// and the node the watchdog starts (<see cref="BreakawayStart"/>). Both ask for <c>CREATE_BREAKAWAY_FROM_JOB</c> so the child leaves the
/// starter's job object, and both fall back to a start without it when the job does not allow breakaway (access denied).
/// </summary>
internal static class Win32ProcessStart
{
    public const uint NewProcessGroup = 0x00000200;
    public const uint BreakawayFromJob = 0x01000000;
    public const uint NoWindow = 0x08000000;
    public const uint UnicodeEnvironment = 0x00000400;
    public const uint Suspended = 0x00000004;
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint UseShowWindow = 0x1;
    public const uint UseStdHandles = 0x100;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public nint Reserved2;
        public nint StdInput, StdOutput, StdError;
    }

    /// <summary>A <c>STARTUPINFOEX</c>. A caller with no attribute list leaves <see cref="AttributeList"/> zero, sets
    /// <see cref="StartupInfo.Size"/> to the plain <see cref="StartupInfo"/>'s size and does not pass <see cref="ExtendedStartupInfoPresent"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo Startup;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    /// <summary>
    /// Starts <paramref name="line"/> with <paramref name="flags"/> plus <see cref="BreakawayFromJob"/>, and again without the breakaway
    /// when the starter's job refuses it. Returns false with the Win32 error in <paramref name="error"/> when neither start works.
    /// The caller closes <see cref="ProcessInformation.Process"/> and <see cref="ProcessInformation.Thread"/>.
    /// </summary>
    public static bool TryStart(string line, bool inheritHandles, uint flags, string? directory, ref StartupInfoEx startup, out ProcessInformation information, out int error)
    {
        error = 0;
        if (CreateProcessW(null, new StringBuilder(line), 0, 0, inheritHandles, flags | BreakawayFromJob, 0, directory, ref startup, out information))
        {
            return true;
        }
        error = Marshal.GetLastWin32Error();
        if (error != ErrorAccessDenied)
        {
            return false;
        }
        if (CreateProcessW(null, new StringBuilder(line), 0, 0, inheritHandles, flags & ~BreakawayFromJob, 0, directory, ref startup, out information))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastWin32Error();
        return false;
    }
}
