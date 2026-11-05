using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pilot.Node.Flights;

/// <summary>
/// A Windows job object that ends every process in it when the node lets go of it, a crash included (a graceful stop calls <see cref="Release"/> first),
/// for a child the node waits on and must never outlive it: a leg's check. A check whose node restarted (an update) went
/// on running its tests, and its <c>Pilot.Node.Tests.exe</c> held the plane's bin DLLs so the next check could not build
/// (MSB3021; PIL446 and PIL474, DE1, 2026-10-31). Jobs are not put here: they outlive the node on purpose (PIL294).
/// Best effort: off Windows, or where the job cannot be made, it does nothing and the caller's own kill still applies.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private const uint LimitPriorityClass = 0x20;

    private nint _handle;
    private readonly uint _priorityClass;

    private KillOnCloseJob(nint handle, uint priorityClass)
    {
        _handle = handle;
        _priorityClass = priorityClass;
    }

    /// <summary>A job holding <paramref name="process"/> and whatever it starts from now on, or null where none can be made.</summary>
    public static KillOnCloseJob? Hold(Process process, JobPriority priority = JobPriority.Normal)
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
        var priorityClass = priority.WindowsClass();
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits
            {
                LimitFlags = KillOnJobClose | (priorityClass == 0 ? 0 : LimitPriorityClass),
                PriorityClass = priorityClass,
            },
        };
        if (!SetInformationJobObject(handle, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
            || !AssignProcessToJobObject(handle, process.Handle))
        {
            CloseHandle(handle);
            return null;
        }
        return new KillOnCloseJob(handle, priorityClass);
    }

    /// <summary>
    /// Clears the kill-on-close limit (keeping the priority class), so disposing the hold no longer ends the tree. A graceful node
    /// stop (an update, a restart) lets go this way so a running check outlives it and the next node follows it; a crash never gets
    /// here, so it still kills (pilot #737). A no-op off Windows, or where the job cannot be changed.
    /// </summary>
    public void Release()
    {
        var handle = Volatile.Read(ref _handle);
        if (handle == 0 || !OperatingSystem.IsWindows())
        {
            return;
        }
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits
            {
                LimitFlags = _priorityClass == 0 ? 0 : LimitPriorityClass,
                PriorityClass = _priorityClass,
            },
        };
        SetInformationJobObject(handle, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>());
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
