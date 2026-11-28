using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The message a failed setup git run throws (#1431): pure, no process.</summary>
public sealed class HelperGitTests
{
    [Fact]
    public void EmptyStderrStillShowsStdoutAndExitCode()
    {
        var message = HelperGit.Describe(["commit", "-q", "-m", "mine"], @"C:\t\plane", 1, "nothing to commit, working tree clean", "");

        message.ShouldContain("nothing to commit, working tree clean");
        message.ShouldContain("exited 1");
    }

    [Fact]
    public void AnNtStatusExitCodeIsShownInHex()
    {
        var message = HelperGit.Describe(["status"], @"C:\t\plane", -1073741502, "", "");

        message.ShouldContain("0xC0000142");
    }

    [Fact]
    public void TheFolderAndTheCommandLineAreNamed()
    {
        var message = HelperGit.Describe(["commit", "-q", "-m", "mine"], @"C:\t\plane", 1, "", "boom");

        message.ShouldContain(@"git commit -q -m mine in 'C:\t\plane'");
        message.ShouldContain("stderr: boom");
    }
}
