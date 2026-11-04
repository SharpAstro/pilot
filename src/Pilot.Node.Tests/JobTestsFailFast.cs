using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A check, a --fail-fast run and a reviewer's or fixer's leg check end at their first failed test and free the queue (PIL731).</summary>
[Collection(JobsCollection.Name)]
public sealed class JobTestsFailFast : JobTestsBase
{
    private const string Test = "Pilot.Node.Tests.BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit";
    private const string Short = "BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit";

    // Prints the failure at once, then would run for a minute.
    private static readonly string FailsThenWaits = $"echo '  Failed {Test} [1 s]'; sleep 60; echo slept";

    [Fact]
    public async Task ACheckEndsAtItsFirstFailedTestNamesItFreesTheQueueAndLeavesNoProcess()
    {
        var check = _jobs.RunCheckAsync("TIA1", "parser", FailsThenWaits, _root, TimeSpan.FromMinutes(30), Ct);
        var queued = Run("echo 'behind'; exit 0").Made.ShouldNotBeNull();

        var run = await check.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        run.Passed.ShouldBeFalse();
        run.Tail.ShouldContain($"stopped at its first failure: {Short}");
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check);
        (row.State, row.Error).ShouldBe((JobState.Failed, $"stopped at its first failure: {Short}"));
        IsAlive(row.Pid!.Value).ShouldBeFalse();
        (await Ended(queued)).State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    public async Task APlainRunKeepsRunningToItsOwnEndAfterAFailedTest()
    {
        var id = Run($"echo '  Failed {Test} [1 s]'; sleep 2; echo slept; exit 1").Made.ShouldNotBeNull();

        var ended = await Ended(id);

        (ended.State, ended.ExitCode, ended.TestsFailed).ShouldBe((JobState.Failed, 1, 1));
        ended.Error.ShouldBe("exit 1");
    }

    [Fact]
    public async Task ARunHandedOverWithFailFastStopsLikeACheck()
    {
        var id = _jobs.Run(new CommandRequest
        {
            Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [Verbs.FailFastArg, _root, "0", FailsThenWaits], Who = "se1-tia1-parser",
        }).Made.ShouldNotBeNull();

        var ended = await Ended(id);

        (ended.State, ended.Error, ended.FailFast).ShouldBe((JobState.Failed, $"stopped at its first failure: {Short}", true));
        ended.EndedAt!.Value.ShouldBeLessThan(ended.StartedAt + TimeSpan.FromSeconds(30));
        IsAlive(ended.Pid!.Value).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SessionRole.Reviewer)]
    [InlineData(SessionRole.Worker)]
    public async Task ARunOfALegsCheckStopsForAReviewerAndNotForAWorker(SessionRole role)
    {
        _state.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Check = FailsThenWaits });
        _state.PutSession(new SessionRow
        {
            Id = "b1b2c3d4", SessionId = "b1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-review", Kind = SessionKind.Background, Cwd = _root,
            Role = role, Flight = "TIA1", Leg = role == SessionRole.Worker ? "parser" : null, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
        var id = Run($"  {FailsThenWaits}  ", target: "session/b1b2c3d4").Made.ShouldNotBeNull();

        if (role == SessionRole.Reviewer)
        {
            var ended = await Ended(id);
            (ended.State, ended.Error).ShouldBe((JobState.Failed, $"stopped at its first failure: {Short}"));
        }
        else
        {
            var running = await Until(id, j => j.TestsFailed == 1);
            running.State.ShouldBe(JobState.Running);
            running.FailFast.ShouldBeFalse();
            _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "test" });
            await Ended(id);
        }
    }

    [Fact]
    public async Task AFixerSessionsLegCheckStopsAtItsFirstFailure()
    {
        _state.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Check = FailsThenWaits });
        _state.PutSession(new SessionRow
        {
            Id = "c1b2c3d4", SessionId = "c1b2c3d4-0000-4000-8000-000000000000", Name = "se1-fix-pilot-1", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.AdHoc, Flight = "TIA1", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "SharpAstro/pilot", Number = 1, Url = "u", Base = "main", Head = "h", ClonePath = _root, Worktree = _root, Reviewer = "x",
            State = default, StartedAt = DateTimeOffset.UtcNow, Fixer = "se1-fix-pilot-1",
        });

        var id = Run(FailsThenWaits, target: "session/c1b2c3d4").Made.ShouldNotBeNull();

        (await Ended(id)).Error.ShouldBe($"stopped at its first failure: {Short}");
    }
}
