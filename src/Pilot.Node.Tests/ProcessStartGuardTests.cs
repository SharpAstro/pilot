using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The process-start guard (#952 part 5) decides from a test's traits alone, so every case here runs in memory with no real process:
/// an unmarked test's start throws, a Slow or Integration test's does not, report mode never throws, and a start no test can be named
/// for is reported, not thrown.
/// </summary>
[Trait("Category", "Invariant")]
public sealed class ProcessStartGuardTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> Unmarked =
        new Dictionary<string, IReadOnlyCollection<string>>();

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Category(params string[] values) =>
        new Dictionary<string, IReadOnlyCollection<string>> { ["Category"] = values };

    [Fact]
    public void AnUnmarkedTestsStartThrowsNamingTheKindAndTheCommand()
    {
        var ex = Should.Throw<ProcessStartNotAllowedException>(() =>
            ProcessStartGuard.Check("git", "git status --short", "Some.Test", Unmarked, ProcessStartGuard.GuardMode.Enforce));

        ex.Message.ShouldContain("git");
        ex.Message.ShouldContain("git status --short");
        ex.Message.ShouldContain("Some.Test");
    }

    [Fact]
    public void ATestWithOnlyOtherCategoriesStillThrows()
    {
        Should.Throw<ProcessStartNotAllowedException>(() =>
            ProcessStartGuard.Check("gh", "gh pr list", "Some.Test", Category("Invariant"), ProcessStartGuard.GuardMode.Enforce));
    }

    [Theory]
    [InlineData("Slow")]
    [InlineData("Integration")]
    [InlineData("slow")]
    public void ASlowOrIntegrationTestsStartDoesNotThrow(string category)
    {
        ProcessStartGuard.Check("git", "git status", "Some.Test", Category(category), ProcessStartGuard.GuardMode.Enforce);
    }

    [Fact]
    public void ANamedKindIsAllowedInAnyTestAndEveryNamedKindHasAReason()
    {
        ProcessStartGuard.Check($"git ({ProcessStartGuard.SeedTemplateKind})", "git init", "Some.Test", Unmarked, ProcessStartGuard.GuardMode.Enforce);
        ProcessStartGuard.Check($"git ({ProcessStartGuard.SeedTemplateKind})", "git init", null, null, ProcessStartGuard.GuardMode.Enforce);
        ProcessStartGuard.KindsAllowed.Values.ShouldAllBe(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ReportModeNeverThrows()
    {
        ProcessStartGuard.Check("claude", "claude --version", "Some.Test", Unmarked, ProcessStartGuard.GuardMode.Report);
        ProcessStartGuard.Check("git", "git status", null, null, ProcessStartGuard.GuardMode.Report);
    }

    [Fact]
    public void AStartOutsideAnyTestIsReportedUnlessItsKindIsNamedWithAReasonButNeverThrows()
    {
        var message = ProcessStartGuard.Verdict("git", "git gc", null, null);

        message.ShouldNotBeNull();
        message.ShouldContain("outside any test");
        ProcessStartGuard.NoTestAllowed.Values.ShouldAllBe(reason => !string.IsNullOrWhiteSpace(reason));
        // Whose start it is, and so whether it may be made, is not known: the report names it (CI fails the default run on it), and a
        // throw would break a Slow test's node as surely as an unmarked one's.
        ProcessStartGuard.Check("git", "git gc", null, null, ProcessStartGuard.GuardMode.Enforce);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("enforce", false)]
    [InlineData("report", true)]
    [InlineData("REPORT", true)]
    public void TheModeIsEnforceUnlessTheVariableSaysReport(string? value, bool report)
    {
        ProcessStartGuard.ModeFor(value).ShouldBe(report ? ProcessStartGuard.GuardMode.Report : ProcessStartGuard.GuardMode.Enforce);
    }

    [Fact]
    public void TheCommandLineIsTheProgramAndItsArguments()
    {
        var start = new ProcessStartInfo("git");
        start.ArgumentList.Add("log");
        start.ArgumentList.Add("-1");

        ProcessStartGuard.Command(start).ShouldBe("git log -1");
        ProcessStartGuard.Command(new ProcessStartInfo("pwsh", "-NoProfile")).ShouldBe("pwsh -NoProfile");
        ProcessStartGuard.Command(new ProcessStartInfo("gh")).ShouldBe("gh");
    }

    // The wiring: the observer TestEnvironment installs reads the running test's own traits. Observe tells it of a start and starts
    // nothing, so these run in memory.
    [Fact]
    public void TheObserverThrowsInAnUnmarkedTestNamingTheKindAndTheCommand()
    {
        // Provoked on purpose, so the observer checks it but leaves it out of the process-start report.
        using var provoking = TestEnvironment.ProvokingAStart();

        // PILOT_TEST_PROCESS_GUARD=report is the run's own mode: the observer then never throws (the report is its only effect).
        if (ReportMode)
        {
            ProcessStarts.Observe("git", new ProcessStartInfo("git", "status"));
            return;
        }
        var ex = Should.Throw<ProcessStartNotAllowedException>(() => ProcessStarts.Observe("git", new ProcessStartInfo("git", "status")));

        ex.Message.ShouldContain("git status");
    }

    private static bool ReportMode =>
        ProcessStartGuard.ModeFor(Environment.GetEnvironmentVariable("PILOT_TEST_PROCESS_GUARD")) == ProcessStartGuard.GuardMode.Report;

    [Fact]
    [Trait("Category", "Slow")] // the Slow mark is what lets the observer pass this start; nothing is started
    public void TheObserverLetsAMethodLevelSlowTestsStartThrough()
    {
        ProcessStarts.Observe("git", new ProcessStartInfo("git", "status"));
    }

    // A test node's requests run on Kestrel's threads, which carry no test; the node's middleware (NodeHarness) carries the test that
    // started it instead. These run the observer on a flow with no test, as Kestrel does, and start nothing.
    [Fact]
    public async Task OnAThreadWithNoTestAStartIsTheCarriedTestsAndThrowsWhenThatTestIsUnmarked()
    {
        var ex = await OnAThreadWithNoTestAsync(() =>
        {
            TestEnvironment.CarryOwner(new TestEnvironment.Owner("Some.UnmarkedNodeTest", Unmarked));
            if (ReportMode)
            {
                // Report mode never throws (see the observer test above).
                ProcessStarts.Observe("git", new ProcessStartInfo("git", "fetch origin"));
                return null;
            }
            return Should.Throw<ProcessStartNotAllowedException>(() => ProcessStarts.Observe("git", new ProcessStartInfo("git", "fetch origin")));
        });

        if (ex is not null)
        {
            ex.Message.ShouldContain("Some.UnmarkedNodeTest");
            ex.Message.ShouldContain("git fetch origin");
        }
    }

    [Fact]
    public async Task OnAThreadWithNoTestAStartIsLetThroughWhenTheCarriedTestIsSlow()
    {
        await OnAThreadWithNoTestAsync(() =>
        {
            TestEnvironment.CarryOwner(new TestEnvironment.Owner("Some.SlowNodeTest", Category("Slow")));
            ProcessStarts.Observe("git", new ProcessStartInfo("git", "fetch origin"));
            return 0;
        });
    }

    [Fact]
    public async Task OnAThreadWithNoTestAndNoneCarriedAStartDoesNotThrow()
    {
        await OnAThreadWithNoTestAsync(() =>
        {
            ProcessStarts.Observe("git", new ProcessStartInfo("git", "worktree list --porcelain"));
            return 0;
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a pool thread that carries nothing of this test's flow (as Kestrel runs a node's request), after
    /// proving that no test is running there; what it provokes is checked but left out of the process-start report.
    /// </summary>
    private static async Task<T> OnAThreadWithNoTestAsync<T>(Func<T> body)
    {
        Task<T> run;
        using (ExecutionContext.SuppressFlow())
        {
            run = Task.Run(() =>
            {
                TestContext.Current.Test.ShouldBeNull();
                using var provoking = TestEnvironment.ProvokingAStart();
                return body();
            });
        }
        return await run;
    }
}

[Trait("Category", "Invariant")]
[Trait("Category", "Slow")] // a class-level Slow mark is what lets the observer pass this start; nothing is started
public sealed class ProcessStartGuardSlowClassTests
{
    [Fact]
    public void TheObserverLetsAClassLevelSlowTestsStartThrough()
    {
        ProcessStarts.Observe("git", new ProcessStartInfo("git", "status"));
    }
}
