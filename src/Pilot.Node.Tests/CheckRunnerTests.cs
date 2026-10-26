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

    [Fact]
    public void AJobOrCheckRunsWithNoBuildServerLeftBehindToHoldItsOutput()
    {
        // MSBuild's reused worker nodes outlived a job's dotnet test, kept its pipe open, and held the node's one job slot.
        using var shell = ShellScript.For("dotnet test", Path.GetTempPath());
        shell.Start.Environment["MSBUILDDISABLENODEREUSE"].ShouldBe("1");
        shell.Start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"].ShouldBe("0");
        shell.Start.Environment["UseSharedCompilation"].ShouldBe("false");
    }

    [Theory]
    // TIA1827's check, double-quoted around a $LASTEXITCODE the script's own pwsh expanded to nothing.
    [InlineData("pwsh -NoProfile -Command \"dotnet build; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\"", "dotnet build; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }")]
    [InlineData("pwsh -Command 'python -I x.py --name ''a'''", "python -I x.py --name 'a'")]
    [InlineData("powershell.exe -NoProfile -c \"echo \"\"hi\"\"\"", "echo \"hi\"")]
    [InlineData("dotnet test --filter 'A|B'", "dotnet test --filter 'A|B'")]
    [InlineData("pwsh -NoProfile -File check.ps1", "pwsh -NoProfile -File check.ps1")]
    // Anything after the quoted body, or a second command: not one wrapper, so left as written.
    [InlineData("pwsh -Command \"dotnet build\" && echo \"done\"", "pwsh -Command \"dotnet build\" && echo \"done\"")]
    [InlineData("pwsh -c \"dotnet build\"; pwsh -c \"dotnet test\"", "pwsh -c \"dotnet build\"; pwsh -c \"dotnet test\"")]
    // A switch with a bare-word value before the command.
    [InlineData("pwsh -ExecutionPolicy Bypass -Command \"dotnet build; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\"", "dotnet build; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }")]
    // A double-quoted body is read as the outer pwsh reads it: a backtick makes the next character literal, so an escaped
    // dollar (a working check) stays a dollar, and `n and the like are their control characters.
    [InlineData("pwsh -NoProfile -c \"git --version; exit `$LASTEXITCODE\"", "git --version; exit $LASTEXITCODE")]
    [InlineData("pwsh -c \"echo a``b\"", "echo a`b")]
    [InlineData("pwsh -c \"echo `\"a`\" \"\"b\"\"\"", "echo \"a\" \"b\"")]
    [InlineData("pwsh -c \"echo a`nb`tc\"", "echo a\nb\tc")]
    public void APwshCommandWrappedAroundTheCheckIsUnwrapped(string check, string runs) => ShellScript.Unwrap(check).ShouldBe(runs);

    /// <summary>
    /// A check that escaped its dollar correctly (<c>`$LASTEXITCODE</c>) runs as the outer pwsh would have run it: the
    /// unwrapped line passes with the exit code of its last program, here 0.
    /// </summary>
    [Fact]
    public async Task AnEscapedDollarInAWrappedCheckStillPasses()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Unwrap only rewrites on Windows, where the node runs pwsh.");

        var run = await new ShellCheckRunner().RunAsync("pwsh -NoProfile -c \"git --version; exit `$LASTEXITCODE\"", Path.GetTempPath(), Ct);

        run.Passed.ShouldBeTrue(run.Tail);
        run.ExitCode.ShouldBe(0);
    }

    [Theory]
    [InlineData("pwsh -NoProfile -Command \"dotnet test\"", "pwsh")]
    [InlineData("bash -c 'make test'", "bash")]
    [InlineData("cmd /c dotnet test", "cmd")]
    [InlineData("sh -c \"x\"", "sh")]
    [InlineData("pwsh -ExecutionPolicy Bypass -Command \"dotnet test\"", "pwsh")]
    [InlineData("pwsh -NoProfile -File check.ps1", null)]
    [InlineData("dotnet test --configuration Release", null)]
    [InlineData("bash ./check.sh", null)]
    public void AShellWrappedAroundTheCheckIsFound(string check, string? shell) => ShellScript.WrappingShell(check).ShouldBe(shell);
}
