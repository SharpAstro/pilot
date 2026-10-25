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
    /// The node runs no cmd now; the line is written into a pwsh script as it is.
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
        var run = await new ShellCheckRunner().RunAsync("exit 3", Path.GetTempPath(), Ct);

        run.Passed.ShouldBeFalse();
        run.ExitCode.ShouldBe(3);
    }

    /// <summary>A program's own exit code is the check's, not pwsh's 0 or 1 for a script that ends without exit.</summary>
    [Fact]
    public async Task AProgramsExitCodeIsTheChecks()
    {
        var run = await new ShellCheckRunner().RunAsync("git --no-such-option", Path.GetTempPath(), Ct);

        run.Passed.ShouldBeFalse();
        run.ExitCode.ShouldBe(129);
    }

    /// <summary>A line that fails before one that passes is a pass, as a shell's last command decides.</summary>
    [Fact]
    public async Task TheLastCommandDecides()
    {
        var run = await new ShellCheckRunner().RunAsync("git --no-such-option; git --version", Path.GetTempPath(), Ct);

        run.Passed.ShouldBeTrue(run.Tail);
    }

    /// <summary>The script a Windows check runs from is gone once it has run.</summary>
    [Fact]
    public void TheScriptIsDeletedWithTheRun()
    {
        string? script;
        using (var shell = ShellScript.For("echo hi", Path.GetTempPath()))
        {
            script = shell.Script;
            if (OperatingSystem.IsWindows())
            {
                File.ReadAllText(script!).ShouldStartWith("echo hi\n");
                shell.Start.FileName.ShouldBe("pwsh");
            }
            else
            {
                script.ShouldBeNull();
                shell.Start.FileName.ShouldBe("/bin/sh");
            }
        }
        if (script is not null)
        {
            File.Exists(script).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("dotnet test --filter A|B", 22)]
    [InlineData("dotnet build | tee log", 13)]
    [InlineData("dotnet test --filter 'A|B'", null)]
    [InlineData("dotnet test --filter \"A|B\"", null)]
    [InlineData("dotnet build || exit 1", null)]
    [InlineData("dotnet test", null)]
    public void ABarePipeIsFound(string check, int? at) => ShellScript.BarePipe(check).ShouldBe(at);
}
