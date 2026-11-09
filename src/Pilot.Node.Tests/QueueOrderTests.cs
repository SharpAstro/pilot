using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A session's queued jobs keep their order: a no-build test never starts before its own build (PIL763).</summary>
[Collection(JobsCollection.Name)]
public sealed class QueueOrderTests : JobTestsBase
{
    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    private static string Clone(string name) => Path.Combine(Path.GetTempPath(), "org-fake", name);

    // "one" builds pilot; "two" builds pilot and DIR.Lib; everything else builds nothing.
    private static string[] Sets(string command) =>
        command.Contains("one") ? [Clone("pilot")]
        : command.Contains("two") ? [Clone("pilot"), Clone("DIR.Lib")]
        : [];

    private JobRunner Runner()
    {
        var (jobs, _, _, _) = Idle();
        jobs.MaxRunning = 4;
        // The build-order rules under test, not the one-heavy-job rule (HeavyJobTests covers that).
        jobs.OneHeavyAtATime = false;
        jobs.SiblingsOf = job => Sets(job.Command);
        return jobs;
    }

    private string HandIn(JobRunner jobs, string command, string cwd, string session)
    {
        Directory.CreateDirectory(cwd);
        if (_state.Sessions().All(x => x.Id != session))
        {
            _state.PutSession(new SessionRow
            {
                Id = session, SessionId = session + "-0000-4000-8000-000000000000", Name = "se1-tia1-" + session, Kind = SessionKind.Background, Cwd = _root,
                Role = SessionRole.Worker, Flight = "TIA1", Leg = session, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
                SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
            });
        }
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = $"session/{session}", Args = [cwd, "0", command], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private async Task CancelAll(JobRunner jobs, params string[] ids)
    {
        foreach (var id in ids)
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        }
    }

    private async Task<(JobRunner Jobs, string Holder, string Held, string PlaneA, string PlaneB)> HeldBuild(string session = "a1b2c3d4")
    {
        var jobs = Runner();
        var planeA = Path.Combine(_root, "planeA");
        var planeB = Path.Combine(_root, "planeB");
        var holder = HandIn(jobs, "echo 'one'; sleep 20", Path.Combine(_root, "holder"), "ffff0000");
        await Until(holder, j => j.State == JobState.Running);
        var held = HandIn(jobs, "echo 'two'; sleep 20", planeA, session);
        Row(held).State.ShouldBe(JobState.Queued);
        return (jobs, holder, held, planeA, planeB);
    }

    [Fact]
    public async Task ANoBuildTestStaysBehindItsOwnersHeldBuildInTheSamePlane()
    {
        var (jobs, holder, held, planeA, _) = await HeldBuild();
        var test = HandIn(jobs, "echo 'three'; sleep 20", planeA, "a1b2c3d4");

        Row(test).State.ShouldBe(JobState.Queued);
        Row(test).Step.ShouldNotBeNull().ShouldContain($"waits behind job {held}, queued earlier by this session in this plane");
        await CancelAll(jobs, holder);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(held, j => j.State == JobState.Running);
        // The build has started, so the test is no longer behind a queued job and starts after it.
        await Until(test, j => j.State == JobState.Running);
        Row(test).StartedAt.ShouldBeGreaterThanOrEqualTo(Row(held).StartedAt);
        await CancelAll(jobs, held, test);
    }

    [Fact]
    public async Task ADifferentOwnersNoBuildTestStillStartsAheadOfTheHeldBuild()
    {
        var (jobs, holder, held, planeA, planeB) = await HeldBuild();
        var other = HandIn(jobs, "echo 'three'; sleep 20", planeB, "99998888");

        await Until(other, j => j.State == JobState.Running);
        Row(other).Step.ShouldNotBeNull().ShouldContain($"started ahead of 1 held job ({held})");
        Row(held).State.ShouldBe(JobState.Queued);
        await CancelAll(jobs, holder, other);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(held, j => j.State == JobState.Running);
        await CancelAll(jobs, held);
    }

    [Theory]
    [InlineData("dotnet test --no-build", true)]
    [InlineData("dotnet run --no-build -c Release", true)]
    [InlineData("dotnet test", false)]
    [InlineData("dotnet build && dotnet test --no-build", false)]
    [InlineData("dotnet build", false)]
    [InlineData("echo hi", false)]
    public void OnlyConsumesBuildTellsANoBuildCommandFromABuildingOne(string command, bool expected) =>
        SiblingSet.OnlyConsumesBuild(command, _root).ShouldBe(expected);

    [Fact]
    public async Task ANoBuildTestWaitsForAnotherOwnersRunningBuildInItsPlane()
    {
        var jobs = Runner();
        var plane = Path.Combine(_root, "planeA");
        var build = HandIn(jobs, "sleep 20; dotnet build --help", plane, "a1b2c3d4");
        await Until(build, j => j.State == JobState.Running);
        var test = HandIn(jobs, "sleep 20; dotnet test --no-build --help", plane, "99998888");

        Row(test).State.ShouldBe(JobState.Queued);
        Row(test).Step.ShouldNotBeNull().ShouldContain($"waits for {build}, the build of this plane");
        Row(test).WaitsOnSibling.ShouldNotBe(true);
        await CancelAll(jobs, build);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(test, j => j.State == JobState.Running);
        await CancelAll(jobs, test);
    }

    [Fact]
    public async Task ABuildQueuedAheadHoldsAnotherOwnersNoBuildTestInTheSamePlane()
    {
        var jobs = Runner();
        var planeA = Path.Combine(_root, "planeA");
        var holder = HandIn(jobs, "echo 'one'; sleep 20", Path.Combine(_root, "holder"), "ffff0000");
        await Until(holder, j => j.State == JobState.Running);
        // A build held on the sibling clone the holder builds ("two" shares pilot with "one").
        var held = HandIn(jobs, "sleep 20; dotnet build two", planeA, "a1b2c3d4");
        var test = HandIn(jobs, "sleep 20; dotnet test --no-build --help", planeA, "99998888");

        Row(held).State.ShouldBe(JobState.Queued);
        Row(test).State.ShouldBe(JobState.Queued);
        Row(test).Step.ShouldNotBeNull().ShouldContain($"waits for {held}, the build of this plane");
        await CancelAll(jobs, holder);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(held, j => j.State == JobState.Running);
        await CancelAll(jobs, held);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(test, j => j.State == JobState.Running);
        await CancelAll(jobs, test);
    }

    [Fact]
    public async Task ANoBuildTestInAnotherCheckoutIsNotHeldByABuild()
    {
        var jobs = Runner();
        var build = HandIn(jobs, "sleep 20; dotnet build --help", Path.Combine(_root, "planeA"), "a1b2c3d4");
        await Until(build, j => j.State == JobState.Running);
        var test = HandIn(jobs, "sleep 20; dotnet test --no-build --help", Path.Combine(_root, "planeB"), "99998888");

        await Until(test, j => j.State == JobState.Running);
        await CancelAll(jobs, build, test);
    }

    [Fact]
    public async Task APlainDotnetTestIsNotTreatedAsNoBuild()
    {
        var jobs = Runner();
        var plane = Path.Combine(_root, "planeA");
        var build = HandIn(jobs, "sleep 20; dotnet build --help", plane, "a1b2c3d4");
        await Until(build, j => j.State == JobState.Running);
        var test = HandIn(jobs, "sleep 20; dotnet test --help", plane, "99998888");

        await Until(test, j => j.State == JobState.Running);
        (Row(test).Step ?? "").ShouldNotContain("the build of this plane");
        await CancelAll(jobs, build, test);
    }

    [Fact]
    public async Task TheSameOwnersJobInAnotherCheckoutIsNotHeldByTheRule()
    {
        var (jobs, holder, held, _, planeB) = await HeldBuild();
        var elsewhere = HandIn(jobs, "echo 'three'; sleep 20", planeB, "a1b2c3d4");

        await Until(elsewhere, j => j.State == JobState.Running);
        Row(held).State.ShouldBe(JobState.Queued);
        await CancelAll(jobs, holder, elsewhere);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        await Until(held, j => j.State == JobState.Running);
        await CancelAll(jobs, held);
    }
}
