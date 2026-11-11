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

    // gh run view --log-failed prefixes each line with its job, step and timestamp.
    private static string Gh(string job, params string[] lines) =>
        string.Join('\n', lines.Select((l, i) => $"{job}\tTest\t2026-11-09T09:00:{i % 60:00}.0000000Z{l}"));

    private const string Compaction = "Pilot.Node.Tests.WatchtowerStreamTests.ACompactionIsSlashCompactOnItsStdinWhileIdleAndAGenerationOfItsOwn";
    private const string Shouldly = "Shouldly.ShouldAssertException : events.Count should be 2 but was 1";
    private const string SkipReason = "Starts the real claude: set PILOT_LIVE_CLAUDE=1 to run it.";

    // Run 42604667012's windows job: a test's [FAIL] and Failed blocks, then a Skipped test whose block has its own "Error Message:"
    // with the skip reason, then Passed lines (#931).
    private static readonly string FailedThenSkipped = Gh("test (windows-latest)",
        $" [xUnit.net 00:01:10.00]     {Compaction} [FAIL]",
        $" [xUnit.net 00:01:10.00]       {Shouldly}",
        " [xUnit.net 00:01:10.00]       Stack Trace:",
        $" [xUnit.net 00:01:10.00]         at {Compaction}()",
        $"   Failed {Compaction} [3 s]",
        "   Error Message:",
        $"    {Shouldly}",
        "   Stack Trace:",
        $"      at {Compaction}() in /_/src/Pilot.Node.Tests/WatchtowerStreamTests.cs:line 210",
        "",
        " [xUnit.net 00:01:11.00]     Pilot.Node.Tests.StreamLiveTests.ATurnStreamsItsWordsAndEnds [SKIP]",
        $" [xUnit.net 00:01:11.00]       {SkipReason}",
        "   Skipped Pilot.Node.Tests.StreamLiveTests.ATurnStreamsItsWordsAndEnds [1 ms]",
        "   Error Message:",
        $"    {SkipReason}",
        "",
        "   Passed Pilot.Node.Tests.StreamTests.AnotherOne [2 ms]",
        "   Passed Pilot.Node.Tests.StreamTests.AndAnother [4 ms]");

    [Fact]
    public void ASkippedTestsReasonIsNeverTheMessageOfTheFailedTestBeforeIt()
    {
        var failure = FailureSignature.Tests(FailedThenSkipped).ShouldHaveSingleItem();

        failure.Test.ShouldBe(Compaction);
        failure.Message.ShouldBe(Shouldly);
        failure.Message.ShouldNotContain("PILOT_LIVE_CLAUDE");
    }

    [Fact]
    public void ASkippedOrPassedResultLineEndsTheReportInHand()
    {
        // No Failed block of its own: only xunit's [FAIL] message is there, and a later block must not be taken for it.
        var log = Gh("job",
            " [xUnit.net 00:00:01.00]     Ns.FooTests.Bar [FAIL]",
            " [xUnit.net 00:00:01.00]       the real message",
            " [xUnit.net 00:00:01.00]       Stack Trace:",
            "   Passed Ns.FooTests.Fine [1 ms]",
            "   Error Message:",
            "    not a failure's message");

        FailureSignature.Tests(log).ShouldHaveSingleItem().Message.ShouldBe("the real message");

        var skipped = "[xUnit.net 00:00:01.00]     Ns.FooTests.Bar [FAIL]\n[xUnit.net 00:00:01.00]       the real message\n"
            + "[xUnit.net 00:00:01.00]     Ns.FooTests.Skip [SKIP]\n[xUnit.net 00:00:01.00]       a skip reason";
        FailureSignature.Tests(skipped).ShouldHaveSingleItem().Message.ShouldBe("the real message");
    }

    [Fact]
    public void ATestNamesTheJobsItFailedIn()
    {
        var log = Gh("test (ubuntu-latest)", $"   Passed {Compaction} [3 ms]") + "\n" + FailedThenSkipped;

        FailureSignature.Tests(log).ShouldHaveSingleItem().Jobs.ShouldBe(["test (windows-latest)"]);
    }

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

    private const string ShiftK = "TianWen.Lib.Tests.ViewerBestStackTests.ShiftKShowsTheWholeCaptureAsTheAutoViewAndWritesBothMasters(dpi: 1)";
    private const string Dll = @"  from D:\a\tianwen\tianwen\src\TianWen.Lib.Tests\bin\Release\net10.0\TianWen.Lib.Tests.dll (net10.0|x64)";

    // Run 42905294654's test-windows (unit 4/5), as Microsoft.Testing.Platform's detailed output prints it: a step's echo with
    // download-artifact's "digest-mismatch: error" input, then a passed test, the cleanup failure and the timed-out test, the summary.
    private static readonly string ShiftKTimeout = Gh("test-windows (unit 4/5)",
        " ##[group]Run actions/download-artifact@v8",
        " with:",
        "   name: windows-unit-bin",
        "   digest-mismatch: error",
        " env:",
        "   BUILD_CONF: Release",
        " ##[endgroup]",
        " passed TianWen.Lib.Tests.PlanetaryBestStackTests.ARunReportsItsProgressToTheEndAndSaysHowItWasSharpened (33ms)",
        Dll,
        $" failed [Test Cleanup Failure ({ShiftK})]",
        Dll,
        "   Xunit.MicrosoftTestingPlatform.XunitException: System.Threading.Tasks.TaskCanceledException : A task was canceled.",
        "     at TianWen.Lib.Tests.ViewerE2E.PumpUntilAsync(Func`1 done, String what, CancellationToken ct, Boolean untilTimeout) in /_/src/TianWen.Lib.Tests/ViewerE2E.cs:226",
        "     --- End of stack trace from previous location ---",
        $" failed (canceled) {ShiftK} (3m 00s 001ms)",
        Dll,
        "   Xunit.MicrosoftTestingPlatform.XunitException: Test execution timed out after 180000 milliseconds",
        "     --- End of stack trace from previous location ---",
        " passed TianWen.Lib.Tests.ViewerBestStackTests.ACaptureWhoseNameGivesNoPlanetTakesThePlanetChosenInThePanel(dpi: 1) (943ms)",
        Dll,
        @" D:\a\tianwen\tianwen\src\TianWen.Lib.Tests\bin\Release\net10.0\TianWen.Lib.Tests.dll (net10.0|x64) failed with 2 error(s) (4m 41s 691ms)",
        " Test run summary: Failed!",
        " ##[error]Process completed with exit code 1.");

    [Fact]
    public void TheTestingPlatformsTimedOutTestIsOneFailureWithItsOwnResultsMessage()
    {
        var failure = FailureSignature.Tests(ShiftKTimeout).ShouldHaveSingleItem();

        failure.Test.ShouldBe(ShiftK);
        failure.Message.ShouldBe("Test execution timed out after 180000 milliseconds");
        failure.Jobs.ShouldBe(["test-windows (unit 4/5)"]);
    }

    [Fact]
    public void ACleanupFailureAloneIsItsTestsFailureWithTheCleanupsMessage()
    {
        var log = Gh("job", $" failed [Test Cleanup Failure ({ShiftK})]", Dll,
            "   Xunit.MicrosoftTestingPlatform.XunitException: System.Threading.Tasks.TaskCanceledException : A task was canceled.",
            "     at TianWen.Lib.Tests.ViewerE2E.PumpUntilAsync() in /_/src/TianWen.Lib.Tests/ViewerE2E.cs:226",
            " passed Ns.FooTests.Fine (1ms)", Dll);

        var failure = FailureSignature.Tests(log).ShouldHaveSingleItem();

        failure.Test.ShouldBe(ShiftK);
        failure.Message.ShouldBe("System.Threading.Tasks.TaskCanceledException : A task was canceled.");
    }

    [Fact]
    public void AStepsEchoedInputIsNeverTheErrorLine()
    {
        var log = Gh("job",
            " ##[group]Run actions/download-artifact@v8",
            " with:",
            "   digest-mismatch: error",
            " ##[endgroup]",
            " ##[error]Process completed with exit code 1.");

        FailureSignature.ErrorText(log).ShouldBe("##[error]Process completed with exit code 1.");
        FailureSignature.ForError(log).ShouldNotBeNull().ShouldNotContain("digest-mismatch");
    }

    [Fact]
    public void ACompilerErrorIsABuildTheOnesOwnPrBreaksNotAFailureToLookInto()
    {
        FailureSignature.IsBuildError("src/A.cs(10,5): error CS0234: The namespace 'Diagnostics' does not exist").ShouldBeTrue();
        FailureSignature.IsBuildError("##[error]Process completed with exit code 1.").ShouldBeFalse();
    }
}
