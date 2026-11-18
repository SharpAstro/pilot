using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A job running a filtered <c>dotnet test</c> that ran no test, or whose filter has a name term matching no test, ends Failed and
/// says which term (pilot #1157), so neither a leg's check nor a pilot run counts it as passed. All on fakes: no real process.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class EmptyFilterJobTests : JobTestsBase
{
    private const string NoMatch = "No test matches the given testcase filter `FullyQualifiedName~Nope` in T.dll";
    private const string Passed = "Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1";

    public EmptyFilterJobTests() : base(fakeLauncher: true)
    {
    }

    // A test project T with one test class, ATests.
    private void Project()
    {
        var dir = Path.Combine(_root, "T");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "T.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(dir, "A.cs"), "namespace N; public class ATests { [Xunit.Fact] public void Works() { } }");
    }

    private async Task<JobRow> RunAs(string command, FakeScript script)
    {
        Fake!.On(_ => true, script);
        var id = Run(command, cwd: _root).Made.ShouldNotBeNull();
        return await Ended(id);
    }

    [Fact]
    public async Task ANoTestMatchesLineAtExitZeroEndsFailedNamingTheFilter()
    {
        Project();

        var ended = await RunAs("dotnet test T --filter FullyQualifiedName~ATests", FakeScript.Prints(0, NoMatch));

        ended.State.ShouldBe(JobState.Failed);
        ended.ExitCode.ShouldBe(0);
        ended.Error.ShouldNotBeNull().ShouldContain("No test matches the given testcase filter");
    }

    [Fact]
    public async Task AnOrFilterWithOneUnmatchedTermEndsFailedNamingThatTerm()
    {
        Project();

        var ended = await RunAs("dotnet test T --filter 'FullyQualifiedName~ATests|FullyQualifiedName~Ghost'", FakeScript.Prints(0, Passed));

        ended.State.ShouldBe(JobState.Failed);
        ended.ExitCode.ShouldBe(0);
        ended.Error.ShouldNotBeNull().ShouldContain("FullyQualifiedName~Ghost");
    }

    [Fact]
    public async Task AFilterWhoseTermsAllMatchSucceeds()
    {
        Project();

        var ended = await RunAs("dotnet test T --filter 'FullyQualifiedName~ATests|Name~Works'", FakeScript.Prints(0, Passed));

        ended.State.ShouldBe(JobState.Succeeded);
        ended.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ASolutionRunWithOneProjectsNoMatchAndAnotherPassingSucceeds()
    {
        var ended = await RunAs("dotnet test S.slnx", FakeScript.Prints(0, NoMatch, "Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4"));

        ended.State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    public async Task AJobWithNoDotnetTestSegmentIsNeverFailedForTheLine()
    {
        var ended = await RunAs("echo hi", FakeScript.Prints(0, NoMatch));

        ended.State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    public async Task ALegCheckWithAnUnmatchedTermIsNotPassedAndItsTailNamesTheTerm()
    {
        Project();
        Fake!.On(_ => true, FakeScript.Prints(0, Passed));

        var run = await _jobs.RunCheckAsync("TIA1", "parser", "dotnet test T --filter FullyQualifiedName~Ghost", _root, TimeSpan.FromMinutes(5), Ct).WaitAsync(WaitCeiling, Ct);

        run.Passed.ShouldBeFalse();
        run.Tail.ShouldContain("FullyQualifiedName~Ghost");
    }

    [Fact]
    public void TheEndingReadAfterARestartFailsTheSameWay()
    {
        Project();
        var log = Path.Combine(_root, "old.log");
        File.WriteAllText(log, NoMatch + "\n");
        var job = new JobRow { Id = "j1", Command = "dotnet test T --filter FullyQualifiedName~ATests", Cwd = _root, LogPath = log };

        EmptyFilter.ErrorFromLog(job).ShouldNotBeNull().ShouldContain("No test matches");
    }
}
