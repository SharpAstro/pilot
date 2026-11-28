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
    public void TheStatusIsAppendedToTheMessage()
    {
        var message = HelperGit.WithStatus("git commit failed", " M a.txt\n", "warn\n");

        message.ShouldStartWith("git commit failed");
        message.ShouldContain("git status --short:");
        message.ShouldContain("M a.txt");
        message.ShouldContain("warn");
    }

    [Theory]
    [InlineData(new[] { "commit", "-q" }, true)]
    [InlineData(new[] { "-c", "user.name=t", "commit", "-q" }, true)]
    [InlineData(new[] { "-C", "x", "commit" }, true)]
    [InlineData(new[] { "log", "-m", "commit" }, false)]
    [InlineData(new[] { "-c", "commit", "status" }, false)]
    [InlineData(new string[0], false)]
    public void OnlyTheSubcommandMatches(string[] args, bool expected) =>
        HelperGit.IsSubcommand(args, "commit").ShouldBe(expected);

    [Fact]
    public void TheFolderAndTheCommandLineAreNamed()
    {
        var message = HelperGit.Describe(["commit", "-q", "-m", "mine"], @"C:\t\plane", 1, "", "boom");

        message.ShouldContain(@"git commit -q -m mine in 'C:\t\plane'");
        message.ShouldContain("stderr: boom");
    }
}
