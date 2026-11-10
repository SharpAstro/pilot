using System.Collections.Concurrent;
using System.Diagnostics;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

// The real process seam: what SystemCloudProcess does with a program that runs, one that is not there, one that runs too long, a cancel, and
// the Windows batch file az is. Nothing here calls Azure: the programs are dotnet, ping or sleep, and a .cmd file the test writes.
public sealed partial class AzureBenchTests
{
    private static string[] Sleeper() => OperatingSystem.IsWindows() ? ["ping", "-n", "10", "127.0.0.1"] : ["sleep", "10"];

    [Fact]
    [Trait("Category", "Slow")] // starts a real program through SystemCloudProcess
    public async Task ARealProgramRunsAndItsLinesStreamToTheCallerAsTheyAreWritten()
    {
        var lines = new ConcurrentQueue<string>();

        var run = await new SystemCloudProcess().RunAsync(new CloudCommand("dotnet", ["--version"], OnLine: lines.Enqueue), TestContext.Current.CancellationToken);

        run.Ok.ShouldBeTrue(run.Why);
        run.Output.Trim().ShouldNotBeEmpty();
        lines.ShouldContain(run.Output.Trim());
    }

    [Fact]
    [Trait("Category", "Slow")] // starts a real program through SystemCloudProcess
    public async Task AProgramThatIsNotThereIsAFailedRunWithItsReasonNotAnException()
    {
        var run = await new SystemCloudProcess().RunAsync(new CloudCommand("pilot-no-such-program-x1", ["--version"]), TestContext.Current.CancellationToken);

        run.ExitCode.ShouldBe(CloudProcessRun.NotStarted);
        run.Error.ShouldContain("pilot-no-such-program-x1 could not be started");
    }

    [Fact]
    [Trait("Category", "Slow")] // starts a real program through SystemCloudProcess
    public async Task ARunOverItsBudgetIsKilledAndSaidSo()
    {
        var sleeper = Sleeper();
        var watch = Stopwatch.StartNew();

        var run = await new SystemCloudProcess().RunAsync(new CloudCommand(sleeper[0], sleeper[1..], Budget: TimeSpan.FromMilliseconds(300)), TestContext.Current.CancellationToken);

        run.ExitCode.ShouldBe(CloudProcessRun.OutOfBudget);
        run.Error.ShouldContain("ran past its budget");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8), "killed at its budget, not left to run its ten seconds");
    }

    [Fact]
    [Trait("Category", "Slow")] // starts a real program through SystemCloudProcess
    public async Task ACancelKillsTheProgramAndThrows()
    {
        var sleeper = Sleeper();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var caught = await Record.ExceptionAsync(() => new SystemCloudProcess().RunAsync(new CloudCommand(sleeper[0], sleeper[1..]), cancel.Token));

        caught.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public void ArgumentsForACmdFileAreQuotedWhereCmdWouldActOnThem()
    {
        SystemCloudProcess.JoinForCmd(["vm", "create", "--query", "[?starts_with(name, 'x')].id", "a&b", "C:\\x y\\", "", "Canonical:ubuntu:server:latest"])
            .ShouldBe("vm create --query \"[?starts_with(name, 'x')].id\" \"a&b\" \"C:\\x y\\\\\" \"\" Canonical:ubuntu:server:latest");
    }

    [Theory]
    [InlineData("say \"hi\"")]
    [InlineData("100%")]
    [InlineData("a\nb")]
    public void AnArgumentNoQuotingMakesSafeForCmdIsRefusedRatherThanRunAsSomethingElse(string argument)
    {
        Should.Throw<ArgumentException>(() => SystemCloudProcess.JoinForCmd(["login", argument], "az.cmd")).Message.ShouldContain("az.cmd");
    }

    [Fact]
    [Trait("Category", "Slow")] // starts a real program through SystemCloudProcess
    public async Task ThroughARealCmdFileNothingTheArgumentsHoldIsRunAsACommand()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "az is a .cmd file on Windows only");
        var shim = Path.Combine(_root, "echo.cmd");
        // %1 keeps the quotes as az.cmd's %* does; %~1 would strip them and let this file's own echo run the pipe.
        await File.WriteAllTextAsync(shim, "@echo off\r\necho [%1]\r\necho [%2]\r\necho [%3]\r\n", TestContext.Current.CancellationToken);

        var run = await new SystemCloudProcess().RunAsync(new CloudCommand(shim, ["x & echo INJECTED", "a|b (c) ^d", "plain"]), TestContext.Current.CancellationToken);

        run.Ok.ShouldBeTrue(run.Why);
        run.Output.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Length > 0).ShouldBe(["[\"x & echo INJECTED\"]", "[\"a|b (c) ^d\"]", "[plain]"]);
    }

    [Fact]
    [Trait("Category", "Slow")] // starts a real program through SystemCloudProcess
    public async Task ARefusedArgumentIsAFailedRunThatNeverStartedTheProgram()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "az is a .cmd file on Windows only");
        var shim = Path.Combine(_root, "never.cmd");
        await File.WriteAllTextAsync(shim, "@echo off\r\necho ran> ran.txt\r\n", TestContext.Current.CancellationToken);

        var run = await new SystemCloudProcess().RunAsync(new CloudCommand(shim, ["login", "100%PATH%"], WorkingDirectory: _root), TestContext.Current.CancellationToken);

        run.ExitCode.ShouldBe(CloudProcessRun.NotStarted);
        File.Exists(Path.Combine(_root, "ran.txt")).ShouldBeFalse();
    }

    [Fact]
    public void OnWindowsAzIsFoundAsItsCmdShimAndOpenSshFromWindowsRatherThanGit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the lookup is Windows's");
        var bin = Path.Combine(_root, "bin");
        var system = Path.Combine(_root, "windows");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(system, "System32", "OpenSSH"));
        File.WriteAllText(Path.Combine(bin, "az.cmd"), "");
        File.WriteAllText(Path.Combine(bin, "az"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(bin, "ssh.exe"), "");
        File.WriteAllText(Path.Combine(system, "System32", "OpenSSH", "ssh.exe"), "");

        SystemCloudProcess.Resolve("az", bin, system).ShouldBe(Path.Combine(bin, "az.cmd"), "the extensionless az is a shell script CreateProcess cannot start");
        SystemCloudProcess.Resolve("ssh", bin, system).ShouldBe(Path.Combine(system, "System32", "OpenSSH", "ssh.exe"));
        SystemCloudProcess.Resolve("ssh", bin, Path.Combine(_root, "nowhere")).ShouldBe(Path.Combine(bin, "ssh.exe"));
        File.WriteAllText(Path.Combine(bin, "az.exe"), "");
        SystemCloudProcess.Resolve("az", bin, system).ShouldBe(Path.Combine(bin, "az.exe"));
        SystemCloudProcess.Resolve("not-installed", bin, system).ShouldBe("not-installed");
        SystemCloudProcess.Resolve(Path.Combine(bin, "az.cmd"), "", system).ShouldBe(Path.Combine(bin, "az.cmd"));
    }
}
