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

    private async Task<CommandRequest> Said(int count)
    {
        for (var i = 0; i < 100 && _said.Count < count; i++)
        {
            await Task.Delay(20, Ct);
        }
        return _said.Skip(count - 1).First();
    }
}
