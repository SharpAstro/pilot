using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The process-start guard (#952 part 5) decides from a test's traits alone, so every case here runs in memory with no real process:
/// an unmarked test's start throws, a Slow or Integration test's does not, report mode never throws.
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
    public void ReportModeNeverThrows()
    {
        ProcessStartGuard.Check("claude", "claude --version", "Some.Test", Unmarked, ProcessStartGuard.GuardMode.Report);
        ProcessStartGuard.Check("git", "git status", null, null, ProcessStartGuard.GuardMode.Report);
    }

    [Fact]
    public void AStartOutsideAnyTestThrowsUnlessItsKindIsNamedWithAReason()
    {
        var message = ProcessStartGuard.Verdict("git", "git gc", null, null);

        message.ShouldNotBeNull();
        message.ShouldContain("outside any test");
        ProcessStartGuard.NoTestAllowed.Values.ShouldAllBe(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData(null, ProcessStartGuard.GuardMode.Enforce)]
    [InlineData("", ProcessStartGuard.GuardMode.Enforce)]
    [InlineData("enforce", ProcessStartGuard.GuardMode.Enforce)]
    [InlineData("report", ProcessStartGuard.GuardMode.Report)]
    [InlineData("REPORT", ProcessStartGuard.GuardMode.Report)]
    public void TheModeIsEnforceUnlessTheVariableSaysReport(string? value, ProcessStartGuard.GuardMode expected)
    {
        ProcessStartGuard.ModeFor(value).ShouldBe(expected);
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
}
