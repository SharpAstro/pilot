using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Pilot.Node.Updates;

/// <summary>Which processes hold a file open or run from it, as Windows' Restart Manager can tell. Empty elsewhere, and
/// whenever it cannot tell: the caller falls back to the OS's own message.</summary>
internal static class FileHolders
{
    private const int ErrorMoreData = 234;

    /// <summary>The processes holding <paramref name="path"/>, as <c>name (pid n)</c>.</summary>
    public static IReadOnlyList<string> Of(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        try
        {
            return Query(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or ArgumentException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> Query(string path)
    {
        var key = new char[33];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return [];
        }
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0)
            {
                return [];
            }
            uint capacity = 0;
            var infos = new ProcessInfo[0];
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var result = RmGetList(session, out var needed, ref capacity, infos, out _);
                if (result == 0)
                {
                    return [.. infos.Take((int)capacity).Select(i => $"{i.AppName} (pid {i.Process.Id})")];
                }
                if (result != ErrorMoreData)
                {
                    return [];
                }
                infos = new ProcessInfo[needed];
                capacity = needed;
            }
            return [];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public uint Id;
        public FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TerminalServicesSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, [Out, MarshalAs(UnmanagedType.LPArray)] char[] key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications, UniqueProcess[]? apps, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] infos, out uint reasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
