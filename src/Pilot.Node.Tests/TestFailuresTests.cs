using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public sealed class TestFailuresTests
{
    private const string Xunit = "[xUnit.net 00:19:36.27]     Pilot.Node.Tests.BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit [FAIL]";
    private const string Failed = "  Failed Pilot.Node.Tests.BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit [11 s]";

    [Fact]
    public void AnXunitFailLineIsAFailedTestNamedClassDotMethod()
    {
        TestFailures.Parse(Xunit).ShouldBe(new TestFailures.Failure(false, "BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit"));
    }

    [Fact]
    public void AFailedLineWithItsDurationIsAFailedTest()
    {
        TestFailures.Parse("  Failed Pilot.Node.Tests.JobTests1.AJobRuns [7 s]").ShouldBe(new TestFailures.Failure(false, "JobTests1.AJobRuns"));
        TestFailures.Parse("  Failed Ns.C.M(x: 1) [< 1 ms]").ShouldBe(new TestFailures.Failure(false, "C.M"));
    }

    [Fact]
    public void AnMsBuildErrorIsABuildErrorWithItsCodeAndFile()
    {
        var line = @"C:\r\src\Pilot.Gui.Tests\NavigationTests.cs(428,39): error CS0234: The namespace 'Diagnostics' does not exist [C:\r\src\Pilot.Gui.Tests\Pilot.Gui.Tests.csproj]";

        TestFailures.Parse(line).ShouldBe(new TestFailures.Failure(true, "CS0234 in NavigationTests.cs"));
    }

    [Fact]
    public void OneTestSeenOnBothItsLinesCountsOnce()
    {
        var reader = new TestFailures();

        reader.Read(Xunit).ShouldNotBeNull();
        reader.Read(Failed).ShouldBeNull();
        reader.Read(Xunit).ShouldBeNull();
    }

    [Fact]
    public void AnErrorPrintedAgainInTheBuildSummaryCountsOnce()
    {
        var reader = new TestFailures();

        reader.Read(@"C:\r\A.cs(1,2): error CS0103: no x [C:\r\P.csproj]").ShouldNotBeNull();
        reader.Read(@"C:\r\A.cs(1,2): error CS0103: no x").ShouldBeNull();
        reader.Read(@"C:\r\A.cs(3,2): error CS0103: no x [C:\r\P.csproj]").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Failed!  - Failed:     2, Passed:  3400, Skipped:     2, Total:  3404, Duration: 19 m 43 s - Pilot.Node.Tests.dll (net10.0)")]
    [InlineData("Passed!  - Failed:     0, Passed:  3400, Skipped:     2, Total:  3402")]
    [InlineData("  Failed: 0")]
    [InlineData("  Passed Pilot.Node.Tests.X.FailedThingIsHandled [5 ms]")]
    [InlineData("  Passed Pilot.Node.Tests.X.Failed [5 ms]")]
    [InlineData("    0 Error(s)")]
    [InlineData("Build FAILED.")]
    [InlineData("")]
    public void SummariesAndAPassedTestWithFailedInItsNameAreNotFailures(string line)
    {
        TestFailures.Parse(line).ShouldBeNull();
    }
}
