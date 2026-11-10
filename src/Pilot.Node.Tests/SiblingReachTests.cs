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

    // pilot's shape: a project reaches the org root through SharpAstroRoot, 3 levels up from a main clone and 6 from a plane, the
    // latter only with UseLocalSiblings on (PIL802: a plane builds on the packages unless its flight is an exception).
    private string PilotPlane()
    {
        foreach (var at in new[] { Path.Combine(_org, "pilot"), Path.Combine(_org, "pilot", ".claude", "worktrees", "pil1") })
        {
            Directory.CreateDirectory(Path.Combine(at, ".git"));
            Directory.CreateDirectory(Path.Combine(at, "src", "Pilot.Ui"));
            File.WriteAllText(Path.Combine(at, "src", "Pilot.Ui", "Pilot.Ui.csproj"), """
                <Project>
                  <PropertyGroup>
                    <SharpAstroRoot Condition="'$(SharpAstroRoot)' == '' AND Exists('$(MSBuildThisFileDirectory)..\..\..\DIR.Lib')">$(MSBuildThisFileDirectory)..\..\..\</SharpAstroRoot>
                    <SharpAstroRoot Condition="'$(SharpAstroRoot)' == '' AND '$(UseLocalSiblings)' == 'true' AND Exists('$(MSBuildThisFileDirectory)..\..\..\..\..\..\DIR.Lib')">$(MSBuildThisFileDirectory)..\..\..\..\..\..\</SharpAstroRoot>
                    <UseLocalDirLib Condition="'$(UseLocalDirLib)' == '' AND '$(SharpAstroRoot)' != '' AND Exists('$(SharpAstroRoot)DIR.Lib\src\DIR.Lib\DIR.Lib.csproj')">true</UseLocalDirLib>
                  </PropertyGroup>
                </Project>
                """);
        }
        return Path.Combine(_org, "pilot", ".claude", "worktrees", "pil1");
    }

    private string DirLib => Path.Combine(_org, "DIR.Lib");

    [Fact]
    public void APilotPlaneBuildsOnThePackagesAndHoldsNoSibling()
    {
        SiblingSet.Of("dotnet build src/Pilot.slnx -c Release", PilotPlane()).ShouldBeEmpty();
    }

    [Fact]
    public void APilotPlaneOfASiblingSourceFlightHoldsTheCloneItsOptInProbeReaches()
    {
        var plane = PilotPlane();

        SiblingSet.Of("dotnet build src/Pilot.slnx -c Release", plane, siblingSource: true).ShouldContain(p => p.Equals(DirLib, StringComparison.OrdinalIgnoreCase));
        SiblingSet.Of("dotnet build src/Pilot.slnx -c Release -p:UseLocalSiblings=true", plane).ShouldContain(p => p.Equals(DirLib, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UseLocalSiblingsFalseOnTheCommandHoldsNoSiblingEvenInTheMainClone()
    {
        PilotPlane();
        var main = Path.Combine(_org, "pilot");

        SiblingSet.Of("dotnet build src/Pilot.slnx -c Release", main).ShouldContain(p => p.Equals(DirLib, StringComparison.OrdinalIgnoreCase));
        SiblingSet.Of("dotnet build src/Pilot.slnx -c Release -p:UseLocalSiblings=false", main).ShouldNotContain(p => p.Equals(DirLib, StringComparison.OrdinalIgnoreCase));
        SiblingSet.Of("dotnet build src/Pilot.slnx -c Release -p:UseLocalSiblings=false", PilotPlane(), siblingSource: true).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("-p:UseLocalSiblings=true", true)]
    [InlineData("--property:Configuration=Release;UseLocalSiblings=false", false)]
    [InlineData("-property:uselocalsiblings=TRUE", true)]
    [InlineData("-p:UseLocalDirLib=true", null)]
    [InlineData("--no-build", null)]
    public void TheSwitchIsReadFromTheFlags(string flag, bool? expected)
    {
        SiblingSet.UseLocalSiblingsOf([flag]).ShouldBe(expected);
    }

    /// <summary>Every 6-level SharpAstroRoot probe in pilot's own projects is opt-in, so no plane of pilot reaches a sibling by default.</summary>
    [Fact]
    [Trait("Category", "Invariant")]
    public void EverySixLevelProbeInThisRepoAsksForUseLocalSiblings()
    {
        var src = new DirectoryInfo(AppContext.BaseDirectory);
        while (src is not null && !File.Exists(Path.Combine(src.FullName, "src", "Pilot.slnx")))
        {
            src = src.Parent;
        }
        src.ShouldNotBeNull();
        var probes = Directory.EnumerateFiles(Path.Combine(src.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(line => (File: Path.GetFileName(f), Line: line.Trim())))
            .Where(l => l.Line.StartsWith("<SharpAstroRoot", StringComparison.Ordinal) && l.Line.Contains(@"..\..\..\..\..\..\", StringComparison.Ordinal))
            .ToList();

        probes.ShouldNotBeEmpty();
        probes.Where(l => !l.Line.Contains("'$(UseLocalSiblings)' == 'true'", StringComparison.Ordinal)).Select(l => l.File).ShouldBeEmpty();
    }

    public void Dispose()
    {
        try
        {
            TestDirs.DeleteTree(_org);
        }
        catch (IOException)
        {
        }
    }
}
