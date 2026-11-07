using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node holds a sibling clone for a job only when the job's build really reaches it from its own folder (PIL802,
/// 2026-11-07): a tianwen plane's ..\..\DIR.Lib lands inside .claude\worktrees, so it builds on the packages and holds
/// nothing, where the node held 20 clones for it and 19 jobs of every repo queued behind one build for up to 155 min.
/// </summary>
public sealed class SiblingReachTests : IDisposable
{
    private readonly string _org = Path.Combine(Path.GetTempPath(), "sibreach-" + Guid.NewGuid().ToString("N")[..8]);

    public SiblingReachTests()
    {
        Directory.CreateDirectory(Path.Combine(_org, "DIR.Lib", "src", "DIR.Lib"));
        File.WriteAllText(Path.Combine(_org, "DIR.Lib", "src", "DIR.Lib", "DIR.Lib.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(_org, "DIR.Lib", ".git"));
        // tianwen's shape: src/Directory.Build.props probes two levels up for each sibling.
        Repo(Path.Combine(_org, "tianwen"));
        Repo(Path.Combine(_org, "tianwen", ".claude", "worktrees", "tia1"));
    }

    private static void Repo(string at)
    {
        Directory.CreateDirectory(Path.Combine(at, ".git"));
        Directory.CreateDirectory(Path.Combine(at, "src"));
        File.WriteAllText(Path.Combine(at, "src", "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <UseLocalDirLib Condition="'$(UseLocalDirLib)' == '' AND Exists('$(MSBuildThisFileDirectory)..\..\DIR.Lib\src\DIR.Lib\DIR.Lib.csproj')">true</UseLocalDirLib>
              </PropertyGroup>
            </Project>
            """);
    }

    [Fact]
    public void APlaneWhoseProbesLandInsideItsWorktreesFolderHoldsNoSibling()
    {
        var held = SiblingSet.Of("dotnet build src/TianWen.slnx -c Release", Path.Combine(_org, "tianwen", ".claude", "worktrees", "tia1"));

        held.ShouldBeEmpty();
    }

    [Fact]
    public void TheMainCloneWhoseProbeReachesTheSiblingStillHoldsIt()
    {
        var held = SiblingSet.Of("dotnet build src/TianWen.slnx -c Release", Path.Combine(_org, "tianwen"));

        held.ShouldContain(p => p.Equals(Path.Combine(_org, "DIR.Lib"), StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_org, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
