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

    [Fact]
    public void TheFirstFailuresReportRunsFromItsFailedBlockThroughItsStackTrace()
    {
        // As a real log has it (j964): two [FAIL] lines, then the first one's block, whose Shouldly message is not indented.
        var reader = new TestFailures();
        string[] before = [Xunit, "[xUnit.net 00:19:36.40]     Pilot.Node.Tests.Other.Test [FAIL]", Failed, "  Error Message:", "   Shouldly.ShouldAssertException : x", "    should be", "12", "    but was", "252"];
        string[] stack = ["  Stack Trace:", "     at Pilot.Node.Tests.BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit() in C:\\r\\B.cs:line 4", "--- End of stack trace from previous location ---", "   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)"];

        foreach (var line in before)
        {
            reader.Read(line);
            (reader.FirstTestReported, reader.PastFirstReport).ShouldBe((false, false), line);
        }
        foreach (var line in stack)
        {
            reader.Read(line);
            (reader.FirstTestReported, reader.PastFirstReport).ShouldBe((true, false), line);
        }
        reader.Read("  Failed Pilot.Node.Tests.Other.Test [15 ms]");

        reader.FirstTest.ShouldBe("BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit");
        (reader.FirstTestReported, reader.PastFirstReport).ShouldBe((true, true));
    }

    [Theory]
    [InlineData("  Failed Pilot.Node.Tests.Other.Test [15 ms]")]
    [InlineData("Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4")]
    public void AReportWithNoStackTraceEndsAtTheNextFailedTestOrTheSummary(string next)
    {
        var reader = new TestFailures();
        reader.Read(Failed);
        reader.Read("  Error Message:");
        reader.FirstTestReported.ShouldBeFalse();

        reader.Read(next);

        (reader.FirstTestReported, reader.PastFirstReport).ShouldBe((true, true));
    }

    [Fact]
    public void ABuildErrorIsNoFirstFailedTest()
    {
        var reader = new TestFailures();

        reader.Read(@"C:\r\A.cs(1,2): error CS0103: no x [C:\r\P.csproj]");

        (reader.FirstTest, reader.FirstTestReported).ShouldBe((null, false));
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
