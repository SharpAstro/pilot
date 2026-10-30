using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pilot.Node.Flights;

/// <summary>
/// A Windows job object that ends every process in it when the node lets go of it, the node's exit and crash included,
/// for a child the node waits on and must never outlive it: a leg's check. A check whose node restarted (an update) went
/// on running its tests, and its <c>Pilot.Node.Tests.exe</c> held the plane's bin DLLs so the next check could not build
/// (MSB3021; PIL446 and PIL474, DE1, 2026-10-31). Jobs are not put here: they outlive the node on purpose (PIL294).
/// Best effort: off Windows, or where the job cannot be made, it does nothing and the caller's own kill still applies.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private nint _handle;

    private KillOnCloseJob(nint handle) => _handle = handle;

    /// <summary>A job holding <paramref name="process"/> and whatever it starts from now on, or null where none can be made.</summary>
    public static KillOnCloseJob? Hold(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var handle = CreateJobObjectW(0, null);
        if (handle == 0)
        {
            return null;
        }
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
            || !AssignProcessToJobObject(handle, process.Handle))
        {
            CloseHandle(handle);
            return null;
        }
        return new KillOnCloseJob(handle);
    }

    /// <summary>Ends every process in the job now: closing the last handle is the kill.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
