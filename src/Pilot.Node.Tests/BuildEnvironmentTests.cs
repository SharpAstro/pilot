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
        var log = Path.Combine(Path.GetTempPath(), $"pilot-env-{Guid.NewGuid():N}.log");
        using var job = ShellScript.ForJob("dotnet test", Path.GetTempPath(), log, log + ".exit");
        try
        {
            ShouldCarryBuildEnvironment(job.Start);
        }
        finally
        {
            File.Delete(Path.ChangeExtension(log, ".ps1"));
        }
    }

    [Fact]
    public void TheEnvironmentBlockOfADetachedJobHoldsWhatItsStartInfoHolds()
    {
        var start = BuildEnvironment.Apply(new ProcessStartInfo("x"));

        var block = JobProcess.EnvironmentBlock(start);

        block.ShouldEndWith("\0\0");
        var entries = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        entries.ShouldContain("MSBUILDDISABLENODEREUSE=1");
        entries.ShouldContain("DOTNET_PROCESSOR_COUNT=4");
        entries.ShouldBe(entries.OrderBy(entry => entry[..entry.IndexOf('=', 1)], StringComparer.OrdinalIgnoreCase));
    }
}
