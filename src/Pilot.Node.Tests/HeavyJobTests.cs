using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Which jobs are heavy (<see cref="JobRunner.IsHeavy"/>): a test run, or a build of a solution, a test project or a folder. The queue
/// runs one heavy job at a time on a machine, whatever max-jobs allows (the owner, 2026-11-09: "allowing 2 jobs on a machine doesn't
/// mean two heavy test suites can run"), and one job per repo. Pure: no process, no disk.
/// </summary>
public sealed class HeavyJobTests
{
    [Theory]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --no-build")]
    [InlineData("dotnet test src/Pilot.Node.Tests -c Release --no-build --filter FullyQualifiedName~X")]
    [InlineData("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Gui.Tests -c Release --no-build")]
    [InlineData("dotnet build src/Pilot.slnx -c Release")]
    [InlineData("dotnet build src/Pilot.Node.Tests/Pilot.Node.Tests.csproj -c Release")]
    [InlineData("dotnet build")]
    [InlineData("dotnet build -c Release")]
    public void ATestRunOrASolutionBuildIsHeavy(string command) => JobRunner.IsHeavy(command).ShouldBeTrue();

    [Theory]
    [InlineData("dotnet build src/SdlVulkan.Renderer.WebView/SdlVulkan.Renderer.WebView.csproj -c Release")]
    [InlineData("dotnet run --project C:\\scratch\\Scratch204.csproj -c Release")]
    [InlineData("git status")]
    [InlineData("pwsh -File tools/fetch.ps1")]
    public void AOneProjectBuildOrAnythingElseIsLight(string command) => JobRunner.IsHeavy(command).ShouldBeFalse();
}
