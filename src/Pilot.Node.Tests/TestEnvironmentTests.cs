using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The git config the tests' environment hands every git (#462, #1063). Reads the process environment only; starts no process.
/// </summary>
public sealed class TestEnvironmentTests
{
    private static Dictionary<string, string> GitConfig()
    {
        int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"), out var count).ShouldBeTrue();
        var config = new Dictionary<string, string>();
        for (var i = 0; i < count; i++)
        {
            var key = Environment.GetEnvironmentVariable($"GIT_CONFIG_KEY_{i}");
            key.ShouldNotBeNull();
            config[key] = Environment.GetEnvironmentVariable($"GIT_CONFIG_VALUE_{i}") ?? "";
        }
        return config;
    }

    [Fact]
    public void GitsAutomaticMaintenanceIsOffSoNothingWritesTheRepoAfterACommandReturns()
    {
        var config = GitConfig();

        config["gc.auto"].ShouldBe("0");
        config["maintenance.auto"].ShouldBe("false");
        config["gc.autoDetach"].ShouldBe("false");
    }

    [Fact]
    public void GitHubStaysRedirectedToNowhere()
    {
        var config = GitConfig();

        config.Where(c => c.Key.EndsWith(".insteadOf", StringComparison.Ordinal) && c.Value == "https://github.com/").ShouldNotBeEmpty();
    }
}
