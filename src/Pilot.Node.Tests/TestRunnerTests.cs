using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Which runner a folder's <c>dotnet test</c> uses, read from global.json and dotnet.config only (pilot #1132).</summary>
public sealed class TestRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);

    public TestRunnerTests() => Directory.CreateDirectory(Path.Combine(_root, "src", "App.Tests"));

    public void Dispose() => TestDirs.DeleteTree(_root);

    private string Child => Path.Combine(_root, "src", "App.Tests");

    [Fact]
    public void AGlobalJsonSelectingThePlatformMakesItThePlatformInAnyCase()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "sdk": { "version": "10.0.100" }, "test": { "runner": "microsoft.testing.platform" } }""");

        TestRunner.IsPlatform(_root).ShouldBeTrue();
        TestRunner.Of(_root).ShouldBe("Microsoft.Testing.Platform");
    }

    [Fact]
    public void ADotnetConfigSelectingThePlatformMakesItThePlatform()
    {
        File.WriteAllText(Path.Combine(_root, "dotnet.config"), "[dotnet.test.runner]\nname = \"Microsoft.Testing.Platform\"\n");

        TestRunner.IsPlatform(_root).ShouldBeTrue();
    }

    [Fact]
    public void AParentFoldersFileIsFoundFromAChild()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");

        TestRunner.IsPlatform(Child).ShouldBeTrue();
    }

    [Fact]
    public void TheNearestFileWins()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        File.WriteAllText(Path.Combine(Child, "global.json"), """{ "test": { "runner": "VSTest" } }""");

        TestRunner.IsPlatform(Child).ShouldBeFalse();
    }

    [Fact]
    public void NothingSelectingItGivesVsTest()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "sdk": { "version": "10.0.100" } }""");

        TestRunner.IsPlatform(Child).ShouldBeFalse();
        TestRunner.Of(Child).ShouldBe("VSTest");
    }

    [Fact]
    public void MalformedJsonReadsAsNoSelection()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), "{ not json");

        TestRunner.IsPlatform(Child).ShouldBeFalse();
    }
}
