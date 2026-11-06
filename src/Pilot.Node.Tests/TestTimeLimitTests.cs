using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class TestTimeLimitTests
{
    private static readonly TimeSpan Low = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void AShortTestPasses()
        => Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", false, TimeSpan.FromMilliseconds(10), Low, TestTimeMode.Enforce));

    [Fact]
    public void ASlowTestFailsNamingItselfAndItsTimeWhenEnforced()
    {
        var ex = Should.Throw<TestTooSlowException>(() => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, TimeSpan.FromSeconds(2), Low, TestTimeMode.Enforce));
        ex.Message.ShouldContain("My.Class.TheMethod");
        ex.Message.ShouldContain("2.0 s");
    }

    [Fact]
    public void ASlowTestPassesButIsReportedWhenNotEnforced()
    {
        var lines = new List<string>();
        Should.NotThrow(() => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, TimeSpan.FromSeconds(2), Low, TestTimeMode.Report, lines.Add));
        var line = lines.ShouldHaveSingleItem();
        line.ShouldContain("My.Class.TheMethod");
        line.ShouldContain("2.0 s");
        line.ShouldContain("0.1 s");
    }

    [Fact]
    public void AShortTestIsNotReportedInReportMode()
    {
        var lines = new List<string>();
        TestTimeLimitAttribute.Check("C", "M", false, TimeSpan.FromMilliseconds(10), Low, TestTimeMode.Report, lines.Add);
        lines.ShouldBeEmpty();
    }

    [Fact]
    public void ASlowTestTraitedSlowIsExemptInBothModes()
    {
        var lines = new List<string>();
        Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", true, TimeSpan.FromSeconds(2), Low, TestTimeMode.Enforce, lines.Add));
        Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", true, TimeSpan.FromSeconds(2), Low, TestTimeMode.Report, lines.Add));
        lines.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("true", null, TestTimeMode.Enforce)]
    [InlineData(null, "1", TestTimeMode.Enforce)]
    [InlineData("true", "1", TestTimeMode.Enforce)]
    [InlineData(null, null, TestTimeMode.Report)]
    [InlineData("false", "0", TestTimeMode.Report)]
    [InlineData("", "", TestTimeMode.Report)]
    public void TheModeIsReadFromGithubActionsAndStrict(string? githubActions, string? strict, TestTimeMode expected)
        => TestTimeLimitAttribute.ModeFor(githubActions, strict).ShouldBe(expected);

    [Fact]
    public void AnEnforcedRunThatIsNotStrictAllowsThreeTimesTheLimit()
    {
        TestTimeLimitAttribute.Effective(TimeSpan.FromSeconds(5), strict: true).ShouldBe(TimeSpan.FromSeconds(5));
        TestTimeLimitAttribute.Effective(TimeSpan.FromSeconds(5), strict: false).ShouldBe(TimeSpan.FromSeconds(15));
        Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", false, TimeSpan.FromSeconds(8),
            TestTimeLimitAttribute.Effective(TimeSpan.FromSeconds(5), strict: false), TestTimeMode.Enforce));
    }

    [Fact]
    public void TheDefaultLimitIsFiveSeconds()
        => TestTimeLimitAttribute.DefaultLimit.ShouldBe(TimeSpan.FromSeconds(5));
}
