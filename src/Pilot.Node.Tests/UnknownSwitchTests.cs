using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>MSB1001 "Unknown switch" in a check's output is a wrong command for the repo's runner (pilot #1132), not an SDK problem.</summary>
public sealed class UnknownSwitchTests
{
    private const string Line = "MSBUILD : error MSB1001: Unknown switch.   Switch: --project";

    [Fact]
    public void TheSwitchIsReadFromTheRealLine()
    {
        var found = UnknownSwitch.In($"Build started\n{Line}\nFor switch syntax, type \"MSBuild -help\"").ShouldNotBeNull();

        found.Switch.ShouldBe("--project");
        found.Text.ShouldContain("--project");
        found.Text.ShouldContain("wrong command");
        found.Text.ShouldContain("not an SDK or environment problem");
        found.Text.ShouldContain("VSTest");
    }

    [Fact]
    public void ATailWithoutItIsNone()
    {
        UnknownSwitch.In("error CS0111: duplicate").ShouldBeNull();
        UnknownSwitch.In(null).ShouldBeNull();
    }

    [Fact]
    public void ASwitchWithAValueIsNamedWithoutIt()
    {
        UnknownSwitch.In("MSBUILD : error MSB1001: Unknown switch.   Switch: --filter-class=Foo").ShouldNotBeNull().Switch.ShouldBe("--filter-class");
    }
}
