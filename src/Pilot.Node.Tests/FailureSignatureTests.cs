using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What a failed run's log says is wrong, read as GitHub prints it (PIL716): each failed xunit test with its full name and assertion
/// message, else a normalised error line, and whether the PR touches the test. The two real shapes are NodePlanetaryTests' 10 s
/// timeout (tianwen #1988) and McpParentTests' process check.
/// </summary>
public sealed class FailureSignatureTests
{
    // dotnet test as a log shows it: xunit's own line with the message under it, then VSTest's block with the whole message.
    private const string Planetary = """
        [xUnit.net 00:00:41.52]     Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing [FAIL]
        [xUnit.net 00:00:41.52]       Shouldly.ShouldAssertException : (503, No answer within 10s) should be (409, "a refused start says why")
        [xUnit.net 00:00:41.52]       Stack Trace:
        [xUnit.net 00:00:41.52]         /_/src/Pilot.Node.Tests/NodePlanetaryTests.cs(88,0): at Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing()
          Failed Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing [10 s]
          Error Message:
           Shouldly.ShouldAssertException : (503, No answer within 10s) should be (409, "a refused start says why")
          Stack Trace:
             at Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing() in /_/src/Pilot.Node.Tests/NodePlanetaryTests.cs:line 88

        Failed!  - Failed:     1, Passed:   812, Skipped:     0, Total:   813, Duration: 2 m 3 s - Pilot.Node.Tests.dll (net10.0)
        """;

    // The same as gh run view --log-failed prints it: job, step and timestamp before every line, ANSI colours in places.
    private const string Mcp = """
        test (ubuntu-latest)	Test	2026-11-05T10:00:01.1234567Z   Failed Pilot.Node.Tests.McpParentTests.ARunningPilotMcpIsOneAndTheTestProcessIsNot [112 ms]
        test (ubuntu-latest)	Test	2026-11-05T10:00:01.1234999Z   Error Message:
        test (ubuntu-latest)	Test	2026-11-05T10:00:01.1235001Z    Shouldly.ShouldAssertException : McpParent.IsPilotMcp(Process.GetCurrentProcess()) should be False but was True
        test (ubuntu-latest)	Test	2026-11-05T10:00:01.1235999Z   Stack Trace:
        test (ubuntu-latest)	Test	2026-11-05T10:00:01.1236001Z      at Pilot.Node.Tests.McpParentTests.ARunningPilotMcpIsOneAndTheTestProcessIsNot() in /home/runner/work/pilot/pilot/src/Pilot.Node.Tests/McpParentTests.cs:line 31
        test (ubuntu-latest)	Test	2026-11-05T10:00:02.0000000Z [31;1mFailed![0m  - Failed:     1, Passed:   99, Skipped:     0, Total:   100
        """;

    [Fact]
    public void ARefusedStartsTimeoutIsOneFailureWithItsFullNameAndTheWholeAssertionMessage()
    {
        var failure = FailureSignature.Tests(Planetary).ShouldHaveSingleItem();

        failure.Test.ShouldBe("Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing");
        failure.Message.ShouldContain("(503, No answer within 10s) should be (409,");
        failure.Message.ShouldNotContain("Stack Trace");
        failure.Message.ShouldNotContain("at Pilot.Node.Tests");
    }

    [Fact]
    public void APrefixedGhLogReadsTheSame()
    {
        var failure = FailureSignature.Tests(Mcp).ShouldHaveSingleItem();

        failure.Test.ShouldBe("Pilot.Node.Tests.McpParentTests.ARunningPilotMcpIsOneAndTheTestProcessIsNot");
        failure.Message.ShouldBe("Shouldly.ShouldAssertException : McpParent.IsPilotMcp(Process.GetCurrentProcess()) should be False but was True");
    }

    [Fact]
    public void XunitsOwnLineAloneIsAFailureWithItsMessage()
    {
        var log = """
            [xUnit.net 00:00:12.34]     Pilot.Node.Tests.FooTests.Bar [FAIL]
            [xUnit.net 00:00:12.34]       Assert.Equal() Failure: Values differ
            [xUnit.net 00:00:12.34]       Expected: 1
            [xUnit.net 00:00:12.34]       Actual:   2
            [xUnit.net 00:00:12.34]       Stack Trace:
            [xUnit.net 00:00:12.34]         at Foo()
            """;

        var failure = FailureSignature.Tests(log).ShouldHaveSingleItem();

        failure.Test.ShouldBe("Pilot.Node.Tests.FooTests.Bar");
        failure.Message.ShouldBe("Assert.Equal() Failure: Values differ Expected: 1 Actual: 2");
    }

    [Fact]
    public void SeveralFailedTestsAreEachOnceInTheOrderTheyAppear()
    {
        var failures = FailureSignature.Tests(Planetary + "\n" + Mcp.Replace("test (ubuntu-latest)\tTest\t2026-11-05T10:00:01.1234567Z", "  "));

        failures.Select(f => FailureSignature.ShortOf(f.Test)).ShouldContain("NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing");
        failures.Select(f => f.Test).Distinct().Count().ShouldBe(failures.Count);
    }

    [Fact]
    public void ALogWithNoFailedTestHasNoTests()
    {
        FailureSignature.Tests("Passed!  - Failed:     0, Passed:   813, Skipped:     0, Total:   813\nBuild succeeded.").ShouldBeEmpty();
        FailureSignature.Tests("").ShouldBeEmpty();
    }

    [Fact]
    public void ATheoryRowsSignatureIsItsTestsNameWhateverTheRowOrTheTiming()
    {
        var one = new FailureSignature.TestFailure("Pilot.Node.Tests.NodePlanetaryTests.Start(port: 1)", "No answer within 10s");
        var other = new FailureSignature.TestFailure("Pilot.Node.Tests.NodePlanetaryTests.Start(port: 2)", "No answer within 12s");

        FailureSignature.Of(one).ShouldBe(FailureSignature.Of(other));
        FailureSignature.NameOf(one.Test).ShouldBe("Pilot.Node.Tests.NodePlanetaryTests.Start");
        FailureSignature.ClassOf(one.Test).ShouldBe("NodePlanetaryTests");
    }

    [Theory]
    [InlineData("src/Pilot.Node.Tests/NodePlanetaryTests.cs", true)]
    [InlineData("src/Pilot.Node.Tests/NodePlanetaryTests.Start.cs", true)]
    [InlineData(@"src\Pilot.Node.Tests\NodePlanetaryTests.cs", true)]
    [InlineData("src/Pilot.Node.Tests/McpParentTests.cs", false)]
    [InlineData("src/Pilot.Node/Flights/NodePlanetary.cs", false)]
    [InlineData("NodePlanetaryTestsBase.cs", false)]
    public void ATestIsTouchedWhenOneOfTheChangedFilesHoldsItsClass(string file, bool touched) =>
        FailureSignature.Touches("Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing", [file]).ShouldBe(touched);

    [Fact]
    public void ATestIsNotTouchedByNoChangedFiles() =>
        FailureSignature.Touches("Pilot.Node.Tests.NodePlanetaryTests.A", []).ShouldBeFalse();

    [Fact]
    public void AnErrorSignatureLeavesOutNumbersPathsAndShasSoTheSameErrorReadsTheSame()
    {
        var first = "##[error]fatal: unable to access 'https://github.com/SharpAstro/DIR.Lib/': Could not resolve host after 3 tries in /home/runner/work/_temp/12345/x at 3021bcecd64a831400e54f191a11274ab26597a6";
        var second = "##[error]fatal: unable to access 'https://github.com/SharpAstro/DIR.Lib/': Could not resolve host after 9 tries in /home/runner/work/_temp/99999/x at abcdef1234567890abcdef1234567890abcdef12";

        var signature = FailureSignature.ForError("Run tests\n" + first).ShouldNotBeNull();

        signature.ShouldStartWith("error:");
        signature.ShouldBe(FailureSignature.ForError(second));
        signature.ShouldNotContain("3021bce");
        signature.ShouldNotContain("12345");
    }

    [Fact]
    public void ACompilerErrorIsABuildTheOnesOwnPrBreaksNotAFailureToLookInto()
    {
        FailureSignature.IsBuildError("src/A.cs(10,5): error CS0234: The namespace 'Diagnostics' does not exist").ShouldBeTrue();
        FailureSignature.IsBuildError("##[error]Process completed with exit code 1.").ShouldBeFalse();
    }
}
