using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A leg's check runs as written: quotes, pipes and all.</summary>
public sealed class CheckRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A quoted | is an argument, not a pipe: a test filter such as <c>--filter "A|B"</c> went to cmd with its quotes
    /// escaped the .NET way (\"), which cmd does not read, so it piped on it and every such check failed (2026-10-24).
    /// </summary>
    [Fact]
    public async Task AQuotedPipeIsPassedNotPiped()
    {
        var run = await new ShellCheckRunner().RunAsync(OperatingSystem.IsWindows() ? "echo \"A|B\"" : "echo 'A|B'", Path.GetTempPath(), Ct);

        run.Passed.ShouldBeTrue(run.Tail);
        run.Tail.ShouldContain("A|B");
    }

    [Fact]
    public async Task AFailingCommandFails()
    {
        var run = await new ShellCheckRunner().RunAsync(OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3", Path.GetTempPath(), Ct);

        run.Passed.ShouldBeFalse();
        run.ExitCode.ShouldBe(3);
    }
}
