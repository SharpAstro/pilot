using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The update light only for a window beside the node: a build in a plane restarted as itself and offered it again.</summary>
public sealed class InstallFolderTests
{
    [Fact]
    public void TheSameFolderMatchesWithOrWithoutItsTrailingSeparator()
    {
        var folder = AppContext.BaseDirectory;
        InstallFolder.Same(Path.TrimEndingDirectorySeparator(folder), folder).ShouldBeTrue();
        InstallFolder.Same(Path.Combine(folder, "plane"), folder).ShouldBeFalse();
        InstallFolder.Same(null, folder).ShouldBeFalse();
    }

    [Fact]
    public void AProcessRunFromElsewhereIsNotBesideAndOneThatIsGoneIsNot()
    {
        // The test host runs from dotnet's folder, not this assembly's: a node there is not this window's install.
        InstallFolder.RunsBeside(Environment.ProcessId).ShouldBe(InstallFolder.Same(Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory));
        InstallFolder.RunsBeside(int.MaxValue).ShouldBeFalse();
    }
}
