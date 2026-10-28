using System.Diagnostics;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A Linux job under systemd starts in a transient user scope of its own, outside the node unit's cgroup (#367).</summary>
public sealed class DetachedShellScopeTests
{
    [Fact]
    public void UnderSystemdTheJobIsRunInAScopeOfItsOwn()
    {
        var (program, args) = DetachedShell.UnixArgv("echo hi", "pilot-job-j1-ABC", "/usr/bin/setsid", "/usr/bin/systemd-run");

        program.ShouldBe("/usr/bin/systemd-run");
        args.ShouldBe(["--user", "--scope", "--quiet", "--collect", "--unit=pilot-job-j1-ABC", "/usr/bin/setsid", "/bin/sh", "-c", "echo hi"]);
    }

    [Fact]
    public void ElsewhereItIsSetsidAlone()
    {
        var (program, args) = DetachedShell.UnixArgv("echo hi", "pilot-job-j1-ABC", "/usr/bin/setsid", null);

        program.ShouldBe("/usr/bin/setsid");
        args.ShouldBe(["/bin/sh", "-c", "echo hi"]);
    }

    [Fact]
    public void WithNoSetsidItIsTheShellAlone()
    {
        var (program, args) = DetachedShell.UnixArgv("echo hi", "u", null, null);

        program.ShouldBe("/bin/sh");
        args.ShouldBe(["-c", "echo hi"]);
    }

    [Fact]
    public void TheUnitNameOfAnAwkwardLogPathIsAValidOne()
    {
        var unit = DetachedShell.ScopeUnit("j 1/é$", "/home/a b/jobs/it's.log");

        unit.ShouldStartWith("pilot-job-j_1__");
        unit.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':').ShouldBeTrue();
        DetachedShell.ScopeUnit("j 1/é$", "/home/a b/jobs/it's.log").ShouldBe(unit);
        DetachedShell.ScopeUnit("j 1/é$", "/other.log").ShouldNotBe(unit);
    }

    [Fact]
    public void WithoutInvocationIdThereIsNoSystemd()
    {
        var before = Environment.GetEnvironmentVariable("INVOCATION_ID");
        try
        {
            Environment.SetEnvironmentVariable("INVOCATION_ID", null);
            DetachedShell.UnderSystemd(out var systemdRun).ShouldBeFalse();
            systemdRun.ShouldBeNull();
            var (program, _) = DetachedShell.UnixArgv("x", "u", "/usr/bin/setsid", systemdRun);
            program.ShouldNotContain("systemd-run");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INVOCATION_ID", before);
        }
    }

    [Fact]
    public void ARealScopedJobHasItsPidInAPilotJobScopeCgroup()
    {
        if (!OperatingSystem.IsLinux() || !UserBusAnswers())
        {
            Assert.Skip("needs Linux with a systemd user bus that answers `systemctl --user`");
        }
        var dir = Path.Combine(Path.GetTempPath(), $"pilot-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "j1.log");
        var exit = log + ".exit";
        try
        {
            NodeRestarter.UnitMainProcessOverride = true;
            using var shell = DetachedShell.For("j1", "sleep 30", dir, log, exit);
            using var process = shell.Start();
            try
            {
                var cgroup = "";
                for (var i = 0; i < 50 && !cgroup.Contains("pilot-job-", StringComparison.Ordinal); i++)
                {
                    cgroup = File.ReadAllText($"/proc/{process.Id}/cgroup");
                    Thread.Sleep(100);
                }
                cgroup.ShouldContain("pilot-job-");
                cgroup.ShouldContain(".scope");
            }
            finally
            {
                process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            NodeRestarter.UnitMainProcessOverride = null;
            Directory.Delete(dir, true);
        }
    }

    private const string ServiceCgroup = "0::/user.slice/user-1000.slice/user@1000.service/app.slice/pilot-node.service\n";

    [Fact]
    public void TheMainProcessOfAServiceIsTheUnit()
    {
        NodeRestarter.IsUnitMainProcess("abc", ServiceCgroup, null, 42).ShouldBeTrue();
        NodeRestarter.IsUnitMainProcess("abc", ServiceCgroup, "42", 42).ShouldBeTrue();
    }

    [Fact]
    public void AnInheritedInvocationIdIsNotTheUnit()
    {
        // A node or test started from a job: the id is inherited, but the job's scope is its cgroup.
        var scope = "0::/user.slice/user-1000.slice/user@1000.service/app.slice/pilot-job-j1-ABC.scope\n";
        NodeRestarter.IsUnitMainProcess("abc", scope, null, 42).ShouldBeFalse();
        // A child of the node inside the unit's own cgroup: the exec pid is the node's, not its own.
        NodeRestarter.IsUnitMainProcess("abc", ServiceCgroup, "41", 42).ShouldBeFalse();
        NodeRestarter.IsUnitMainProcess(null, ServiceCgroup, null, 42).ShouldBeFalse();
        NodeRestarter.IsUnitMainProcess("abc", null, null, 42).ShouldBeFalse();
    }

    private static bool UserBusAnswers()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("systemctl", "--user show-environment")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return probe is not null && probe.WaitForExit(5000) && probe.ExitCode == 0
                && File.Exists("/usr/bin/systemd-run");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
