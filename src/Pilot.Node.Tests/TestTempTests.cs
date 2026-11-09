using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The one helper that makes and removes a test's temp root (pilot #895): under one prefix, and a delete that never throws.</summary>
public sealed class TestTempTests : IDisposable
{
    private readonly string _root = TestTemp.Create();

    public void Dispose() => TestTemp.Delete(_root);

    [Fact]
    public void ARootIsMadeUnderTheTempFolderWithTheTestPrefix()
    {
        var path = TestTemp.NewPath();

        Path.GetDirectoryName(path).ShouldBe(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        Path.GetFileName(path).ShouldStartWith(TestTemp.Prefix);
        Directory.Exists(path).ShouldBeFalse();
        TestTemp.NewPath().ShouldNotBe(path);
        Directory.Exists(_root).ShouldBeTrue();
        Path.GetFileName(_root).ShouldStartWith(TestTemp.Prefix);
    }

    [Fact]
    public void ADeleteRemovesATreeWithAReadOnlyFileInIt()
    {
        var folder = TestTemp.Create();
        var nested = Directory.CreateDirectory(Path.Combine(folder, "a", "b")).FullName;
        var file = Path.Combine(nested, "object.bin");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        TestTemp.Delete(folder).ShouldBeTrue();

        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public void ADeleteOfAFolderThatIsNotThereOrNoFolderIsDoneAndDoesNotThrow()
    {
        TestTemp.Delete(TestTemp.NewPath()).ShouldBeTrue();
        TestTemp.Delete(null).ShouldBeTrue();
        TestTemp.Delete("").ShouldBeTrue();
    }

    [Fact]
    public void ADeleteWhileAFileIsHeldOpenLeavesTheFolderAndDoesNotThrowThenTakesItOnceTheFileIsLetGo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows refuses to delete a folder holding an open file");
        var folder = TestTemp.Create();
        var file = Path.Combine(folder, "held.log");

        var held = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            TestTemp.Delete(folder, attempts: 3, pause: TimeSpan.Zero).ShouldBeFalse();
            Directory.Exists(folder).ShouldBeTrue();
        }
        finally
        {
            held.Dispose();
        }

        TestTemp.Delete(folder).ShouldBeTrue();
        Directory.Exists(folder).ShouldBeFalse();
    }
}
