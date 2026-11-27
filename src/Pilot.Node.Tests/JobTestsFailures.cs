using Pilot.Contracts;
using Pilot.Node.Flights;
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
        // A Working owner is told at once; an idle one (the say would resume it) is held, see the PIL1382 tests below.
        _state.PutSession(_state.Sessions().Single(s => s.Id == "a1b2c3d4") with { State = SessionState.Working });
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
        again.Error.ShouldNotBeNull().ShouldContain($"rerun once as job {second}");
        fake.Job(second).Launch.Environment.ShouldNotBeNull().ShouldContainKeyAndValue("RestoreNoHttpCache", "true");
        _said.Select(s => s.Args[0]).ToList().ShouldHaveSingleItem().ShouldContain($"Your job {second}");
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
        _said.Select(s => s.Args[0]).ToList().ShouldHaveSingleItem().ShouldContain("failed");
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

    private const string BuildLine = "/w/App.cs(3,5): error CS1656: Cannot assign to 'x' because it is a method group [/w/App.csproj]";

    // PIL1382: a runner on a clock the test moves (timers virtual), a recording Say, and a job that has read a build error and runs on.
    private (JobRunner Jobs, Clock Clock, FakeJobLauncher Launcher, string Id) RunsOnWithABuildError()
    {
        UseFake();
        var (jobs, clock, _, _) = Idle();
        clock.VirtualTimers = true;
        jobs.Say = (command, _) =>
        {
            _said.Enqueue(command);
            return Task.FromResult(_state.Complete(command, null));
        };
        Fake!.On(_ => true, FakeScript.Holds(BuildLine));
        return (jobs, clock, Fake, Hand(jobs, "dotnet build"));
    }

    private async Task EndsAndIsTold(Clock clock, FakeJobLauncher launcher, string id)
    {
        launcher.Exit(id, 1);
        var ended = _state.Jobs().Single(j => j.Id == id);
        await AdvanceUntil(clock, When(() => _state.Jobs().Single(j => j.Id == id) is { EndedAt: not null, Told: true }, "the job never ended told"), TimeSpan.FromSeconds(1));
        ended = _state.Jobs().Single(j => j.Id == id);
        ended.State.ShouldBe(JobState.Failed);
    }

    [Fact]
    public async Task AnIdleOwnerWhoseJobEndsInsideTheHoldIsTheEndTellAloneAndNoRunsOnNotice()
    {
        var (_, clock, launcher, id) = RunsOnWithABuildError();
        await Until(id, j => j.BuildErrors == 1);

        await EndsAndIsTold(clock, launcher, id);
        // Past the hold: a dropped notice is never said later.
        clock.Advance(JobRunner.EarlyNoticeHold * 2);

        var said = _said.Select(s => s.Args[0]).ToList().ShouldHaveSingleItem();
        said.ShouldContain("CS1656");
        said.ShouldContain($"Your job {id}");
        said.ShouldNotContain("runs on");
        _state.Jobs().Single(j => j.Id == id).FailureTold.ShouldBeTrue();
    }

    [Fact]
    public async Task AWorkingOwnerIsToldTheEarlyNoticeAtOnceThenTheEnd()
    {
        var idle = _state.Sessions().Single(s => s.Id == "a1b2c3d4");
        _state.PutSession(idle with { State = SessionState.Working });
        var (_, clock, launcher, id) = RunsOnWithABuildError();

        await When(() => _said.Count == 1, "no early notice came at once");
        _said.Single().Args[0].ShouldContain("runs on");
        _said.Single().Args[0].ShouldContain("CS1656");

        await EndsAndIsTold(clock, launcher, id);

        _said.Count.ShouldBe(2);
        _said.Last().Args[0].ShouldContain("failed: exit 1");
    }

    [Fact]
    public async Task AnIdleOwnerWhoseJobRunsOnPastTheHoldIsToldTheEarlyNoticeThenTheEnd()
    {
        var (_, clock, launcher, id) = RunsOnWithABuildError();
        await Until(id, j => j.BuildErrors == 1);
        _said.ShouldBeEmpty();

        // The hold's timer may not be made yet when the first step lands: each step is taken again until the notice is said.
        await AdvanceUntil(clock, When(() => _said.Count == 1, "the held notice never came"), JobRunner.EarlyNoticeHold + TimeSpan.FromSeconds(1));
        _said.Single().Args[0].ShouldContain("runs on");

        await EndsAndIsTold(clock, launcher, id);

        _said.Count.ShouldBe(2);
        _said.Last().Args[0].ShouldContain("failed: exit 1");
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
