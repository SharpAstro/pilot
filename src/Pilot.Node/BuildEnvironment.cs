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
    /// the machine (#326), so the fan-out is capped too.
    /// <para>
    /// The cap is <c>DOTNET_PROCESSOR_COUNT</c>, not <c>MSBUILDNODECOUNT</c>: MSBuild reads no such variable (nor any other for
    /// its node count; <c>MSBUILDNODECOUNT=4</c> left a Release build of Pilot.slnx at 33 worker nodes, the same as with none).
    /// <c>dotnet build</c> and <c>msbuild</c> run with a bare <c>-maxcpucount</c>, which is the process count of the .NET
    /// runtime, and that variable is what the runtime reads it from, so it needs no edit of the project being built. It also
    /// caps what else sizes itself by the core count in the build's processes (a test host's default parallelism, the GC's heaps).
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
