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
            return Query([path]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// The processes holding any of the first <paramref name="cap"/> files under <paramref name="folder"/>, as <c>name (pid n)</c>:
    /// Restart Manager, else <c>handle.exe</c> when it is on PATH. Empty when neither can tell; never throws.
    /// </summary>
    public static IReadOnlyList<string> OfFolder(string folder, int cap = 200)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        try
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(cap).ToArray();
            if (files.Length > 0 && Query(files) is { Count: > 0 } found)
            {
                return [.. found.Distinct()];
            }
            return HandleExe(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or ArgumentException)
        {
            return HandleExe(folder);
        }
    }

    private static IReadOnlyList<string> HandleExe(string folder)
    {
        try
        {
            var exe = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d.Trim(), "handle.exe")).FirstOrDefault(File.Exists);
            if (exe is null)
            {
                return [];
            }
            var start = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-accepteula");
            start.ArgumentList.Add("-nobanner");
            start.ArgumentList.Add(folder);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return [];
            }
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return [];
            }
            // Lines read: "name.exe   pid: 1234   type: File   B4: C:\path".
            return [.. System.Text.RegularExpressions.Regex.Matches(output.Result, @"^(\S+)\s+pid:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => $"{m.Groups[1].Value} (pid {m.Groups[2].Value})").Distinct()];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> Query(string[] paths)
    {
        var key = new char[33];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return [];
        }
        try
        {
            if (RmRegisterResources(session, (uint)paths.Length, paths, 0, null, 0, null) != 0)
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
