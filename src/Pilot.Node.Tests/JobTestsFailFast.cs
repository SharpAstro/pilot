using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A check, a --fail-fast run and a reviewer's or fixer's leg check end at their first failed test and free the queue (PIL731).
/// They run on the in-memory <see cref="FakeJobLauncher"/> (#933); the one test with a real child process is Slow.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class JobTestsFailFast : JobTestsBase
{
    private const string Test = "Pilot.Node.Tests.BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit";
    private const string Short = "BranchShapeTests.ARebaseThatConflictsIsLaidAsOneCommit";
    private const string Assertion = "Shouldly.ShouldAssertException : commits.Count should be 1 but was 2";

    // The test's report as VSTest prints it, up to its stack trace: what says why it failed.
    private static readonly string Report = $"echo '  Failed {Test} [1 s]'; echo '  Error Message:'; echo '   {Assertion}'; echo '  Stack Trace:'; echo '     at {Test}() in B.cs:line 4'";

    // Prints the failure at once, then would run for a minute.
    private static readonly string FailsThenWaits = $"{Report}; sleep 60; echo slept";

    // A ceiling on the wait for a check's result, not a pace: a check that was never stopped fails the asserts that say so, rather
    // than a wait.
    private static readonly TimeSpan CheckWait = TimeSpan.FromSeconds(25);

    // Starts a child that would sleep a minute and writes its pid, then prints the failure: the job's tree is more than its shell.
    private static string FailsWithAChild(string pidFile) => OperatingSystem.IsWindows()
        ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; {Report}; $p.WaitForExit()"
        : $"sleep 60 & echo $! > '{pidFile}'; {Report}; wait";

    [Fact]
    public async Task ACheckEndsAtItsFirstFailedTestNamesItAndFreesTheQueue()
    {
        var fake = UseFake();
        var check = _jobs.RunCheckAsync("TIA1", "parser", FailsThenWaits, _root, TimeSpan.FromMinutes(30), Ct);
        var queued = Run("echo 'behind'; exit 0").Made.ShouldNotBeNull();

        var run = await check.WaitAsync(CheckWait, Ct);

        run.Passed.ShouldBeFalse();
        run.Tail.ShouldContain($"stopped at its first failure: {Short}");
        run.Tail.ShouldContain(Assertion);
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check);
        (row.State, row.Error).ShouldBe((JobState.Failed, $"stopped at its first failure: {Short}"));
        // Stopped at its first failure (the error above) is the proof: the job would otherwise have held on at its own sleep.
        var process = fake.Job(row.Id);
        (process.IsAlive, process.Killed).ShouldBe((false, true));
        (await Ended(queued)).State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACheckEndsAtItsFirstFailedTestAndLeavesNoProcessItStarted()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var check = _jobs.RunCheckAsync("TIA1", "parser", FailsWithAChild(pidFile), _root, TimeSpan.FromMinutes(30), Ct);

        var run = await check.WaitAsync(CheckWait, Ct);

        run.Tail.ShouldContain($"stopped at its first failure: {Short}");
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check);
        // Stopped at its first failure (the error above) is the proof: the job would otherwise have run its own 60 s sleep out. No wall-clock bound.
        IsAlive(row.Pid!.Value).ShouldBeFalse();
        var childPid = int.Parse(ReadLog(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
    }

    [Fact]
    public async Task ACheckStoppedAtItsFirstFailureWaitsForWhyItFailedBeforeTheKill()
    {
        var fake = UseFake();
        // xunit's [FAIL] line comes first; the block that says why comes later, once the test says so.
        var check = _jobs.RunCheckAsync("TIA1", "parser", $"echo '[xUnit.net 00:00:01.62]     {Test} [FAIL]'; sleep 60", _root, TimeSpan.FromMinutes(30), Ct);
        var row = _state.Jobs().Single(j => j.Kind == JobKind.Check);
        await Until(row.Id, j => j.State == JobState.Running);
        check.IsCompleted.ShouldBeFalse();

        var process = fake.Job(row.Id);
        foreach (var line in new[] { $"  Failed {Test} [1 s]", "  Error Message:", $"   {Assertion}", "  Stack Trace:", $"     at {Test}() in B.cs:line 4", "  Failed Pilot.Node.Tests.Other.Test [15 ms]" })
        {
            process.Print(line);
        }
        var run = await check.WaitAsync(CheckWait, Ct);

        run.Tail.ShouldContain($"stopped at its first failure: {Short}");
        run.Tail.ShouldContain(Assertion);
        // The tail ends with the first failure's report: the next test's is cut off with the run.
        run.Tail.ShouldNotContain("Other.Test");
    }

    [Fact]
    public async Task AFirstFailureWhoseReportNeverComesStopsTheCheckAfterTheGrace()
    {
        UseFake();
        // The runner's clock is the test's: the grace runs out when the test moves it, not after a wait.
        var (jobs, clock, _, _) = Idle();
        jobs.FailFastGrace = TimeSpan.FromSeconds(10);
        var check = jobs.RunCheckAsync("TIA1", "parser", $"echo '  Failed {Test} [1 s]'; sleep 60; echo slept", _root, TimeSpan.FromMinutes(30), Ct);
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Kind == JobKind.Check)));

        await AdvanceUntil(clock, check, TimeSpan.FromSeconds(11));
        var run = await check;

        run.Tail.ShouldContain($"stopped at its first failure: {Short}");
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APlainRunKeepsRunningToItsOwnEndAfterAFailedTest()
    {
        var fake = UseFake();
        var id = Run($"{Report}; sleep 2; echo slept; exit 1").Made.ShouldNotBeNull();
        await Until(id, j => j.TestsFailed == 1);
        // Still running after the failure: it is the line's own end, here the test's say, that ends it.
        _state.Jobs().Single(j => j.Id == id).State.ShouldBe(JobState.Running);

        fake.Finish(id);
        var ended = await Ended(id);

        (ended.State, ended.ExitCode, ended.TestsFailed).ShouldBe((JobState.Failed, 1, 1));
        ended.Error.ShouldBe("exit 1");
    }

    [Fact]
    public async Task ARunHandedOverWithFailFastStopsLikeACheckAndTellsItsOwnerOnce()
    {
        var fake = UseFake();
        var id = _jobs.Run(new CommandRequest
        {
            Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [Verbs.FailFastArg, _root, "0", FailsThenWaits], Who = "se1-tia1-parser",
        }).Made.ShouldNotBeNull();

        var ended = await Ended(id);

        (ended.State, ended.Error, ended.FailFast).ShouldBe((JobState.Failed, $"stopped at its first failure: {Short}", true));
        // Stopped at its first failure (the error above) is the proof, not a wall-clock bound.
        fake.Job(id).IsAlive.ShouldBeFalse();
        // No early "runs on" word: the end, which names the test, is the one message.
        _said.ShouldHaveSingleItem().Args[0].ShouldContain($"stopped at its first failure: {Short}");
    }

    [Theory]
    [InlineData(SessionRole.Reviewer)]
    [InlineData(SessionRole.Worker)]
    public async Task ARunOfALegsCheckStopsForAReviewerAndNotForAWorker(SessionRole role)
    {
        UseFake();
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
            _said.ShouldHaveSingleItem().Args[0].ShouldContain($"stopped at its first failure: {Short}");
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
        UseFake();
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
        _said.ShouldHaveSingleItem();
    }
}
