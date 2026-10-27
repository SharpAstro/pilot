using System.Diagnostics;
using System.Runtime.InteropServices;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Reads the GPU, as <see cref="MachineSampler.ParseGpu"/> and <see cref="MachineSampler.ParseNvidiaSmi"/> parse it. On
/// Windows from the performance counters (<c>GPU Engine</c> Utilization Percentage and <c>GPU Adapter Memory</c>
/// Dedicated Usage) through PDH, and the adapters' dedicated memory from DXGI: the node is published AOT, where
/// <c>System.Diagnostics.PerformanceCounter</c>'s reflection is not to be had. On Linux from <c>nvidia-smi</c> when it
/// is on the PATH. Nowhere else, or when the counters are not there, the GPU is null.
/// </summary>
internal sealed class MachineGpuCounters : IDisposable
{
    /// <summary><c>nvidia-smi</c> is a process to start: asked no more often than this, and its last answer kept between.</summary>
    private static readonly TimeSpan SmiEvery = TimeSpan.FromSeconds(15);

    private const string SmiArguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits";

    private readonly List<(string Instance, double Value)> _engines = [];
    private readonly List<(string Instance, double Value)> _memory = [];

    // Windows: the query and its two wildcard counters are opened once and reused, the buffer kept between reads.
    private nint _query;
    private nint _engineCounter;
    private nint _memoryCounter;
    private nint _buffer;
    private uint _bufferSize;
    private bool _opened;
    private bool _unavailable;
    private Dictionary<string, long>? _totals;

    // Linux
    private string? _smi;
    private bool _smiLooked;
    private long _smiAt = long.MinValue;
    private MachineGpu? _smiGpu;

    /// <summary>The GPU now; null where there is none to read. Windows' utilisation is a rate, so the first read says nothing.</summary>
    public MachineGpu? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ReadWindows();
            }
            if (OperatingSystem.IsLinux())
            {
                return ReadSmi();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            MachineNative.PdhCloseQuery(_query);
            _query = 0;
        }
        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
        }
    }

    // ---- Windows ----

    private MachineGpu? ReadWindows()
    {
        if (_unavailable || IntPtr.Size != 8)
        {
            return null;
        }
        if (!_opened)
        {
            _opened = true;
            // Counters a machine without a WDDM GPU driver (a VM, a container) does not have: then there is no GPU to say.
            if (MachineNative.PdhOpenQueryW(null, 0, out _query) != 0
                || MachineNative.PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _engineCounter) != 0
                || MachineNative.PdhAddEnglishCounterW(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _memoryCounter) != 0)
            {
                _unavailable = true;
                Dispose();
                return null;
            }
            _totals = MachineNative.DedicatedMemoryByLuid();
        }
        if (MachineNative.PdhCollectQueryData(_query) != 0)
        {
            return null;
        }
        _engines.Clear();
        _memory.Clear();
        // The first collect has no earlier one to make a rate of: the counters then answer "no data", which is nothing yet.
        if (!Items(_engineCounter, _engines, skipIdle: true) || !Items(_memoryCounter, _memory, skipIdle: false))
        {
            return null;
        }
        return MachineSampler.ParseGpu(_engines, _memory, _totals!);
    }

    private const uint MoreData = 0x800007D2;

    /// <summary>The instances of a wildcard counter with their values; false when it has none to give yet.</summary>
    private bool Items(nint counter, List<(string Instance, double Value)> into, bool skipIdle)
    {
        const uint formatDouble = 0x200;
        // PDH_FMT_COUNTERVALUE_ITEM_W: the name's pointer, then the value (a status, 4 bytes of padding, a double).
        const int itemSize = 24;
        var size = _bufferSize;
        uint status, count;
        while ((status = MachineNative.PdhGetFormattedCounterArrayW(counter, formatDouble, ref size, out count, _buffer)) == MoreData)
        {
            if (_buffer != 0)
            {
                Marshal.FreeHGlobal(_buffer);
            }
            _buffer = Marshal.AllocHGlobal((nint)size);
            _bufferSize = size;
        }
        if (status != 0)
        {
            return false;
        }
        for (var i = 0; i < count; i++)
        {
            var at = i * itemSize;
            var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(_buffer, at + 16));
            // An engine at 0 adds nothing to a sum, and most of hundreds are: left unread, so no name is made of them.
            if (Marshal.ReadInt32(_buffer, at + 8) is not (0 or 1) || (skipIdle && value <= 0))
            {
                continue;
            }
            if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(_buffer, at)) is { } name)
            {
                into.Add((name, value));
            }
        }
        // A memory counter with no instance is no adapter; an engine counter with only idle instances is an idle one.
        return skipIdle || into.Count > 0;
    }

    // ---- Linux ----

    private MachineGpu? ReadSmi()
    {
        if (!_smiLooked)
        {
            _smiLooked = true;
            _smi = OnPath("nvidia-smi");
        }
        if (_smi is not { } smi)
        {
            return null;
        }
        if (Since(_smiAt) < SmiEvery)
        {
            return _smiGpu;
        }
        _smiAt = Stopwatch.GetTimestamp();
        using var process = Process.Start(new ProcessStartInfo(smi, SmiArguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return _smiGpu = null;
        }
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            // A driver that hangs must not hold the sample: kill it, and say nothing until the next ask.
            process.Kill(entireProcessTree: true);
            return _smiGpu = null;
        }
        return _smiGpu = process.ExitCode == 0 ? MachineSampler.ParseNvidiaSmi(output.GetAwaiter().GetResult()) : null;
    }

    private static TimeSpan Since(long since) => since == long.MinValue ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(since);

    private static string? OnPath(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}

/// <summary>The Windows calls the sampler makes: the system's CPU times, PDH's counters and DXGI's adapters.</summary>
internal static unsafe class MachineNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? source, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(nint query);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>
    /// Each hardware adapter's dedicated video memory by LUID, in the counters' spelling (<c>0x00000000_0x0000d1b2</c>),
    /// from DXGI's factory: the counters say what is used, and only the adapter says what there is. Empty when DXGI is
    /// not there. The COM calls go through the interfaces' vtables, as no marshalling is generated for AOT.
    /// </summary>
    public static Dictionary<string, long> DedicatedMemoryByLuid()
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (CreateDXGIFactory1(FactoryId, out var factory) < 0 || factory == 0)
            {
                return totals;
            }
            try
            {
                // IDXGIFactory1::EnumAdapters1 is slot 12; IDXGIAdapter1::GetDesc1 is slot 10; IUnknown::Release is slot 2.
                // The description's buffer, one for every adapter.
                var desc = stackalloc byte[312];
                for (uint index = 0; ; index++)
                {
                    nint adapter;
                    if (((delegate* unmanaged<nint, uint, nint*, int>)(*(nint**)factory)[12])(factory, index, &adapter) < 0)
                    {
                        break;
                    }
                    try
                    {
                        // DXGI_ADAPTER_DESC1: 128 wide characters of name, four UINTs, three SIZE_Ts (the first the
                        // dedicated video memory, at 272), the LUID (296: low part, then high), the flags (304), 312 in all.
                        if (((delegate* unmanaged<nint, byte*, int>)(*(nint**)adapter)[10])(adapter, desc) >= 0)
                        {
                            const uint software = 2;
                            var flags = *(uint*)(desc + 304);
                            var dedicated = *(long*)(desc + 272);
                            if ((flags & software) == 0 && dedicated > 0)
                            {
                                totals[$"0x{*(int*)(desc + 300):x8}_0x{*(uint*)(desc + 296):x8}"] = dedicated;
                            }
                        }
                    }
                    finally
                    {
                        ((delegate* unmanaged<nint, uint>)(*(nint**)adapter)[2])(adapter);
                    }
                }
            }
            finally
            {
                ((delegate* unmanaged<nint, uint>)(*(nint**)factory)[2])(factory);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return totals;
    }
}
