using Pilot.TestShared;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The run's temp files go under one root and are gone when the run ends (<see cref="TestTempRoot"/>): no test writes loose into
/// %TEMP%, and a killed run's folder is swept by the next run, never one a live run holds.
/// </summary>
public sealed class TestTempRootTests
{
    [Fact]
    public void EveryTempPathOfTheRunIsInTheRunsFolderUnderTheRoot()
    {
        TestTempRoot.Run.ShouldNotBeNullOrEmpty();
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(TestTempRoot.Run)).ShouldBe(Path.TrimEndingDirectorySeparator(TestTempRoot.Root));
        Path.GetFullPath(Path.GetTempPath()).ShouldStartWith(Path.GetFullPath(TestTempRoot.Run));
        Environment.GetEnvironmentVariable("TEMP").ShouldBe(TestTempRoot.Run);
        Environment.GetEnvironmentVariable("TMPDIR").ShouldBe(TestTempRoot.Run);
        // What the tests start inherits the run, and reuses it rather than making a folder of its own.
        Environment.GetEnvironmentVariable(TestTempRoot.RunVariable).ShouldBe(TestTempRoot.Run);
    }

    [Fact]
    public void TheSweepDeletesADeadRunsFolderAndKeepsALiveOneAndOneStarting()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sweep-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var dead = Directory.CreateDirectory(Path.Combine(root, "dead")).FullName;
            File.WriteAllText(Path.Combine(dead, TestTempRoot.LockName), "");
            var deadRepo = Directory.CreateDirectory(Path.Combine(dead, "repo", ".git", "objects", "ab")).FullName;
            var readOnly = Path.Combine(deadRepo, "cdef");
            File.WriteAllText(readOnly, "object");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            var live = Directory.CreateDirectory(Path.Combine(root, "live")).FullName;
            using var held = new FileStream(Path.Combine(live, TestTempRoot.LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            var starting = Directory.CreateDirectory(Path.Combine(root, "starting")).FullName;
            var abandoned = Directory.CreateDirectory(Path.Combine(root, "abandoned")).FullName;
            Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromHours(2));

            var deleted = TestTempRoot.SweepStale(root, DateTime.UtcNow);

            deleted.Select(Path.GetFileName).Order().ShouldBe(["abandoned", "dead"]);
            Directory.Exists(dead).ShouldBeFalse("a run's folder whose lock opens is no live run's, read-only git objects and all");
            Directory.Exists(live).ShouldBeTrue("a live run holds its lock");
            Directory.Exists(starting).ShouldBeTrue("a folder with no lock yet may be a run that is starting");
        }
        finally
        {
            TestTempRoot.Delete(root);
        }
    }

    [Fact]
    public void DeleteTakesReadOnlyFilesAndSaysWhenTheFolderIsGone()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "del-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "a", "b")).FullName, "pack");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        TestTempRoot.Delete(folder).ShouldBeTrue();
        Directory.Exists(folder).ShouldBeFalse();
        TestTempRoot.Delete(folder).ShouldBeTrue("a folder already gone is gone");
    }
}
