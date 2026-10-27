using System.Diagnostics;

namespace Pilot.Node;

/// <summary>
/// The build environment of everything the node starts that builds: a job (<c>pilot run</c>, a check, a validator) and every
/// <c>claude</c> process (a worker's own builds inherit it). One place, so a job and a worker session can not differ.
/// </summary>
public static class BuildEnvironment
{
    /// <summary>How many MSBuild nodes one build may fan out to.</summary>
    public const int MaxNodes = 4;

    /// <summary>
    /// The variables and their values. The build servers a dotnet build leaves running are switched off: MSBuild's reused worker
    /// nodes and the compiler server outlive the build, inherit its output, and kept a job's pipe open after its tests had
    /// finished, so the job read as running and the node's one slot stayed taken (DE1's j302 and SE1's j122, 2026-10-26); idle
    /// ones had also held worktree folders and gigabytes. A full-width build (12 cores, several projects at once) is what filled
    /// the machine (#326); capping that fan-out from the environment is not possible on Windows, see below.
    /// <para>
    /// <c>DOTNET_PROCESSOR_COUNT</c> caps what sizes itself by the .NET runtime's processor count in the build's processes (a test
    /// host's default parallelism, the GC's heaps). It does <b>not</b> cap MSBuild's node count on Windows: <c>dotnet build</c>
    /// runs with a bare <c>-maxcpucount</c>, and MSBuild takes that from the OS (<c>GetLogicalProcessorInformationEx</c> in its
    /// <c>GetLogicalCoreCountOnWindows</c>), not from the runtime. Measured on this 12-core machine, SDK 10.0.401, as the peak
    /// count of <c>/nodemode</c> worker processes under a Release build of Pilot.slnx (descendants of that build only):
    /// 11 with none, 11 with <c>DOTNET_PROCESSOR_COUNT=4</c>, 11 with <c>MSBUILDNODECOUNT=4</c> (MSBuild reads no such
    /// variable), 11 with <c>MSBUILDCORELIMIT=4</c>, 11 with <c>MSBUILDNODELIMITOFFSET=-8</c>. No environment variable of
    /// MSBuild's or the CLI's limits the node count; <c>-maxcpucount:4</c> or a <c>Directory.Build.rsp</c> would, and both edit
    /// the build, which this class does not.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["UseSharedCompilation"] = "false",
        ["DOTNET_PROCESSOR_COUNT"] = MaxNodes.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Puts <see cref="Variables"/> in the environment <paramref name="start"/> will give its process; returns it.</summary>
    public static ProcessStartInfo Apply(ProcessStartInfo start)
    {
        foreach (var (name, value) in Variables)
        {
            start.Environment[name] = value;
        }
        return start;
    }
}
