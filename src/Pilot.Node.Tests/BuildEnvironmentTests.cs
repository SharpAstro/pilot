using System.Diagnostics;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The one build environment (#326): a job and a worker session both start with it.</summary>
public sealed class BuildEnvironmentTests
{
    private static void ShouldCarryBuildEnvironment(ProcessStartInfo start)
    {
        start.Environment["MSBUILDDISABLENODEREUSE"].ShouldBe("1");
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"].ShouldBe("0");
        start.Environment["UseSharedCompilation"].ShouldBe("false");
        start.Environment["DOTNET_PROCESSOR_COUNT"].ShouldBe("4");
        BuildEnvironment.MaxNodes.ShouldBe(4);
    }

    [Fact]
    public void TheClaudeStartInfoOfAWorkerSessionCarriesIt()
    {
        var start = ClaudeCli.StartInfo("claude", ["-p", "hi"], Path.GetTempPath());

        ShouldCarryBuildEnvironment(start);
        start.ArgumentList.ShouldBe(["-p", "hi"]);
        start.WorkingDirectory.ShouldBe(Path.GetTempPath());
    }

    [Fact]
    public void TheClaudeStartInfoStillDropsTheNodesOwnSessionVariables()
    {
        var before = Environment.GetEnvironmentVariable("CLAUDE_PILOT_TEST_SESSION");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_PILOT_TEST_SESSION", "x");
            ClaudeCli.StartInfo("claude", [], null).Environment.ContainsKey("CLAUDE_PILOT_TEST_SESSION").ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_PILOT_TEST_SESSION", before);
        }
    }

    [Fact]
    public void ACheckOrJobCarriesIt()
    {
        using var check = ShellScript.For("dotnet test", Path.GetTempPath());
        ShouldCarryBuildEnvironment(check.Start);
    }

    [Fact]
    public void ADetachedJobsWrapperCarriesItToo()
    {
        if (OperatingSystem.IsWindows())
        {
            // The wrapper is started with the node's own environment (it inherits only the log and NUL), so it sets the build environment itself.
            var wrapper = DetachedShell.WindowsWrapper("inner.ps1", "outer.ps1", "j1.log.exit");
            foreach (var (name, value) in BuildEnvironment.Variables)
            {
                wrapper.ShouldContain($"$env:{name} = '{value}'");
            }
            wrapper.IndexOf("$env:", StringComparison.Ordinal).ShouldBeLessThan(wrapper.IndexOf("& pwsh", StringComparison.Ordinal));
            return;
        }
        var log = Path.Combine(Path.GetTempPath(), $"pilot-env-{Guid.NewGuid():N}.log");
        ShouldCarryBuildEnvironment(DetachedShell.UnixStart("dotnet test", Path.GetTempPath(), log, log + ".exit"));
    }
}
