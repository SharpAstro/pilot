using System.Runtime.InteropServices;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public sealed class DiskUsageTests : IDisposable
{
    private readonly string _root = TestTemp.NewPath();

    public DiskUsageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            TestTemp.Delete(_root);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AMissingFolderReadsZero() => DiskUsage.UniqueBytes(Path.Combine(_root, "nope")).ShouldBe(0);

    [Fact]
    public void DistinctFilesSum()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "b.bin"), new byte[250]);

        DiskUsage.UniqueBytes(_root).ShouldBe(1250);
    }

    [Fact]
    public void HardlinksToOneFileCountItsBytesOnce()
    {
        var original = Path.Combine(_root, "orig.bin");
        File.WriteAllBytes(original, new byte[4096]);
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        if (!Link(original, Path.Combine(_root, "link1.bin")) || !Link(original, Path.Combine(_root, "other", "link2.bin")))
        {
            Assert.Skip("This platform or file system would not make a hardlink.");
        }

        DiskUsage.UniqueBytes(_root).ShouldBe(4096);
    }

    [Fact]
    public void FreeSpaceOfTheTempDriveIsRead()
    {
        var space = DiskUsage.FreeSpace(_root);
        space.ShouldNotBeNull();
        space.Value.Total.ShouldBeGreaterThan(0);
    }

    private static bool Link(string existing, string link) =>
        OperatingSystem.IsWindows() ? CreateHardLinkW(link, existing, IntPtr.Zero) : OperatingSystem.IsLinux() && LibcLink(existing, link) == 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LibcLink([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
}
