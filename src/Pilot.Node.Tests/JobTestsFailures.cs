using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A job whose output already shows a failed test says so while it runs, and tells its owner once, at once (PIL731).</summary>
[Collection(JobsCollection.Name)]
public sealed class JobTestsFailures : JobTestsBase
{
    private const string Test = "Pilot.Node.Tests.BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit";

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobWithAFailedTestSaysSoWhileItRunsAndItsOwnerIsToldTheFirstOnceBeforeTheEnd()
    {
        var id = Run($"echo '[xUnit.net 00:19:36.27]     {Test} [FAIL]'; echo '  Failed {Test} [11 s]'; sleep 3; echo slept; exit 1").Made.ShouldNotBeNull();

        // A cold shell can take a while to start under load; the failure lines come before the job's own wait.
        JobRow running = _state.Jobs().Single(j => j.Id == id);
        for (var i = 0; i < 1000 && running.TestsFailed == 0; i++)
        {
            await Task.Delay(20, Ct);
            running = _state.Jobs().Single(j => j.Id == id);
        }

        running.State.ShouldBe(JobState.Running);
        (running.TestsFailed, running.FirstTestFailed, running.BuildErrors, running.FailureTold).ShouldBe((1, "BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit", 0, true));
        running.FailuresSoFar().ShouldBe("1 failed so far (BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit)");
        var early = await Said(1);
        early.Args[0].ShouldContain("BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit");
        early.Args[0].ShouldContain("runs on");
        _state.Jobs().Single(j => j.Id == id).EndedAt.ShouldBeNull();

        var ended = await Ended(id);

        (ended.State, ended.TestsFailed).ShouldBe((JobState.Failed, 1));
        _said.Count.ShouldBe(2);
        _said.Last().Args[0].ShouldContain("failed: exit 1");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobWithNoFailureLineIsToldNothingEarly()
    {
        var id = Run("echo 'Passed X.FailedThing [5 ms]'; echo 'Failed!  - Failed: 0, Passed: 3'; sleep 2; echo slept").Made.ShouldNotBeNull();

        await Until(id, j => j.State == JobState.Running);
        await Task.Delay(1000, Ct);
        _said.ShouldBeEmpty();
        _state.Jobs().Single(j => j.Id == id).FailuresSoFar().ShouldBeNull();

        var ended = await Ended(id);

        ended.State.ShouldBe(JobState.Succeeded);
        _said.ShouldHaveSingleItem();
    }

    private const string Miss = "error NU1102: Unable to find package Drawboard.Core with version (>= 1.2.7) [/w/App.csproj]";

    private string Plane()
    {
        var plane = Path.Combine(_root, ".claude", "worktrees", "TIA1");
        Directory.CreateDirectory(plane);
        return plane;
    }

    // The first start fails with the miss; every later start runs the script `later`.
    private FakeJobLauncher FirstMissesThen(FakeScript later)
    {
        var fake = UseFake();
        fake.On(_ => true, FakeScript.Prints(1, Miss));
        fake.On(_ => fake.Starts.Count > 0, later);
        return fake;
    }

    private string RerunOf(string first) => _state.Jobs().Single(j => j.Id != first && j.Kind == JobKind.Run).Id;

    [Fact]
    public async Task AStaleRestoreInAPlaneIsRerunOnceWithoutTheHttpCacheAndItsOwnerHearsTheRerunOnly()
    {
        var feed = new FakeNuGetFeed();
        feed.Publish("Drawboard.Core", "1.2.7");
        _jobs.Feed = feed;
        var fake = FirstMissesThen(FakeScript.Prints(0, "restored"));

        var first = Run("dotnet build", cwd: Plane()).Made.ShouldNotBeNull();
        var again = await Until(first, j => j.EndedAt is not null && j.Error is { } e && e.Contains("rerun once as job"));
        var second = RerunOf(first);
        var ended = await Ended(second);

        ended.State.ShouldBe(JobState.Succeeded);
        again.Error.ShouldContain($"rerun once as job {second}");
        fake.Job(second).Launch.Environment.ShouldNotBeNull().ShouldContainKeyAndValue("RestoreNoHttpCache", "true");
        _said.ShouldHaveSingleItem().Args[0].ShouldContain($"Your job {second}");
        _said.Single().Args[0].ShouldContain("succeeded");
        _said.Single().Args[0].ShouldContain("rerun once because the HTTP cache was stale for Drawboard.Core 1.2.7");
        _state.Cards().ShouldBeEmpty();
        fake.Starts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ASecondMissIsToldAsAFailureWithNoThirdRun()
    {
        var feed = new FakeNuGetFeed();
        feed.Publish("Drawboard.Core", "1.2.7");
        _jobs.Feed = feed;
        var fake = FirstMissesThen(FakeScript.Prints(1, Miss));

        var first = Run("dotnet build", cwd: Plane()).Made.ShouldNotBeNull();
        await Until(first, j => j.Error is { } e && e.Contains("rerun once as job"));
        var ended = await Ended(RerunOf(first));

        ended.State.ShouldBe(JobState.Failed);
        _said.ShouldHaveSingleItem().Args[0].ShouldContain("failed");
        fake.Starts.Count.ShouldBe(2);
        _state.Jobs().Count(j => j.Kind == JobKind.Run).ShouldBe(2);
    }

    [Fact]
    public async Task AnUnlistedVersionAJobOutsideAPlaneAndACheckAreNotRerun()
    {
        var feed = new FakeNuGetFeed();
        _jobs.Feed = feed;
        var fake = UseFake();
        fake.On(_ => true, FakeScript.Prints(1, Miss));

        var unlisted = await Ended(Run("dotnet build", cwd: Plane()).Made.ShouldNotBeNull());
        feed.Publish("Drawboard.Core", "1.2.7");
        var outside = await Ended(Run("dotnet build").Made.ShouldNotBeNull());
        var check = await _jobs.RunCheckAsync("TIA1", "parser", "dotnet build", Plane(), TimeSpan.FromMinutes(30), Ct);

        (unlisted.State, outside.State).ShouldBe((JobState.Failed, JobState.Failed));
        check.Passed.ShouldBeFalse();
        fake.Starts.Count.ShouldBe(3);
        _state.Jobs().Count(j => j.Kind == JobKind.Run).ShouldBe(2);
    }

    private async Task<CommandRequest> Said(int count)
    {
        for (var i = 0; i < 100 && _said.Count < count; i++)
        {
            await Task.Delay(20, Ct);
        }
        return _said.Skip(count - 1).First();
    }
}
