using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pilot.Node.Flights;

/// <summary>
/// Disk figures by unique bytes: every file counted once by its file id (Windows: volume serial plus file index; Unix:
/// device plus inode), so a hardlinked bin folder does not read as gigabytes. Reparse points and symlinks are not followed.
/// </summary>
public static partial class DiskUsage
{
    /// <summary>The bytes of every distinct file under <paramref name="folder"/>; 0 for a folder that is missing.</summary>
    public static long UniqueBytes(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }
        var seen = new HashSet<(ulong Volume, ulong Index)>();
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    // A link, junction or mount point is somewhere else's bytes.
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                    {
                        continue;
                    }
                    if (entry is DirectoryInfo)
                    {
                        pending.Push(entry.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        var id = FileId(file.FullName);
                        if (id is null || seen.Add(id.Value))
                        {
                            total += file.Length;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone since the listing, or not ours to read.
            }
        }
        return total;
    }

    /// <summary>Free and total bytes of the drive holding <paramref name="path"/>, and its name; null where it cannot be read.</summary>
    public static (long Free, long Total, string Name)? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize, drive.Name.TrimEnd('\\', '/') is { Length: > 0 } name ? name : drive.Name) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (ulong Volume, ulong Index)? FileId(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                return !handle.IsInvalid && GetFileInformationByHandle(handle, out var info)
                    ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                    : null;
            }
            if (OperatingSystem.IsLinux())
            {
                return StatIds(path);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
        }
        return null;
    }

    // ---- Linux: st_dev and st_ino are the first two 8-byte words of struct stat on x86_64 and arm64 glibc. ----

    private static (ulong Volume, ulong Index)? StatIds(string path)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            int result;
            try
            {
                result = LibcStat(path, buffer);
            }
            catch (EntryPointNotFoundException)
            {
                // glibc before 2.33 has no stat symbol, only the versioned wrapper.
                result = LibcXStat(1, path, buffer);
            }
            return result != 0 ? null : ((ulong)Marshal.ReadInt64(buffer, 0), (ulong)Marshal.ReadInt64(buffer, 8));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int LibcStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
    private static extern int LibcXStat(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
