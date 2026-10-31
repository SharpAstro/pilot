using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class TestTimeLimitTests
{
    private static readonly TimeSpan Low = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void AShortTestPasses()
        => Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", false, TimeSpan.FromMilliseconds(10), Low));

    [Fact]
    public void ASlowTestFailsNamingItselfAndItsTime()
    {
        var ex = Should.Throw<TestTooSlowException>(() => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, TimeSpan.FromSeconds(2), Low));
        ex.Message.ShouldContain("My.Class.TheMethod");
        ex.Message.ShouldContain("2.0 s");
    }

    [Fact]
    public void ASlowTestTraitedSlowIsExempt()
        => Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", true, TimeSpan.FromSeconds(2), Low));

    [Fact]
    public void TheSlowTraitIsRecognised()
    {
        var slow = new Dictionary<string, IReadOnlyCollection<string>> { ["Category"] = ["Slow"] };
        TestTimeLimitAttribute.IsSlow(slow).ShouldBeTrue();
        TestTimeLimitAttribute.IsSlow(new Dictionary<string, IReadOnlyCollection<string>>()).ShouldBeFalse();
    }

    [Fact]
    public void TheDefaultLimitIsFiveSeconds()
        => TestTimeLimitAttribute.DefaultLimit.ShouldBe(TimeSpan.FromSeconds(5));
}
