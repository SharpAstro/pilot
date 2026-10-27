using System.Collections.Concurrent;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Jobs: a long run handed to the node, its owner woken with how it ended, so no worker polls.</summary>
public sealed class JobTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2j-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly ConcurrentQueue<CommandRequest> _said = new();
    private readonly JobRunner _jobs;
    private string? _refuse;

    // The machine's free memory as the runner reads it; null (unknown) holds nothing back.
    private long? _free;

    public JobTests()
    {
        Directory.CreateDirectory(_root);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            FreeMemory = () => _free,
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                return Task.FromResult(_state.Complete(command, _refuse));
            },
        };
        // The dispatcher is the runner's hosted loop: started here as the node starts it.
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-parser", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
        _jobs.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        // StartAsync returns before ExecuteAsync's recovery has run. Until it has, a Queued or Running row a test puts can be
        // failed as orphaned under it: wait, so every row a test puts is the test's alone.
        _jobs.Recovered.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // The same line in pwsh and sh: the node runs no cmd. Quoted, since pwsh's echo writes each word on a line of its own.
    private static string Echo(string text, int exit = 0) => $"echo '{text}'; exit {exit}";

    // Not a bare sleep, which the node refuses (SleepOnly): the echo is what makes it a job.
    private static string Sleep(int seconds) => $"sleep {seconds}; echo slept";

    /// <summary>A file as it is: the job's wrapper (or the child that writes its pid) may still hold it open for writing a moment after it shows, and a plain File.ReadAllText then fails on Windows.</summary>
    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private CommandResult Run(string command, string target = "session/a1b2c3d4", long timeoutSeconds = 0, string? cwd = null) =>
        _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = target, Args = [cwd ?? _root, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), command], Who = "se1-tia1-parser" });

    /// <summary>The job once <paramref name="condition"/> holds: the dispatcher acts on its own loop, a moment after the call.</summary>
    private async Task<JobRow> Until(string id, Func<JobRow, bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (_state.Jobs().Single(j => j.Id == id) is var job && condition(job))
            {
                return job;
            }
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException($"job {id} never came to the state awaited");
    }

    private async Task<JobRow> Ended(string id)
    {
        for (var i = 0; i < 300; i++)
        {
            if (_state.Jobs().Single(j => j.Id == id) is { EndedAt: not null } job && (job.Owner is null || job.Told || _refuse is not null))
            {
                return job;
            }
            await Task.Delay(100, Ct);
        }
        throw new TimeoutException($"job {id} did not end");
    }

    [Fact]
    public async Task AJobRunsOnTheNodeAndItsOwnerIsToldHowItEndedWithItsLastLines()
    {
        var started = Run(Echo("all 12 tests passed"));

        started.Ok.ShouldBeTrue();
        var id = started.Made.ShouldNotBeNull();
        var running = await Until(id, j => j.State != JobState.Queued);
        (running.Id, running.Owner, running.OwnerName, running.Flight, running.Leg).ShouldBe((id, "a1b2c3d4", "se1-tia1-parser", "TIA1", "parser"));

        var job = await Ended(id);
        (job.State, job.ExitCode, job.Step, job.Told).ShouldBe((JobState.Succeeded, 0, "all 12 tests passed", true));
        ReadLog(job.LogPath).ShouldContain("all 12 tests passed");
        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/a1b2c3d4", "node"));
        say.Args[0].ShouldContain($"Your job {id}");
        say.Args[0].ShouldContain("succeeded");
        say.Args[0].ShouldContain("all 12 tests passed");
    }

    [Fact]
    public async Task AJobEndsWhenItsProcessExitsThoughAProcessItStartedStillHoldsItsOutput()
    {
        // SE1's j122: the tests passed in 6 minutes, a process they started kept the job's output open, and the job read as
        // running for four and a half hours with six jobs queued behind it.
        var leftBehind = OperatingSystem.IsWindows()
            ? "Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 20' -NoNewWindow; echo 'started'"
            : "(sleep 20 &); echo 'started'";
        var started = DateTimeOffset.UtcNow;

        var job = await Ended(Run(leftBehind).Made!);

        job.State.ShouldBe(JobState.Succeeded);
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task AJobInheritsOnlyItsLogAndNulNotAnOtherInheritableHandleTheNodeHolds()
    {
        // PIL365: the node holds inheritable handles of its own (a pipe to a child, say). A job that inherited them would keep the
        // far end open for as long as it ran. Here the test's own client end is that handle: once the test closes its copy,
        // the only other holder would be the job.
        using var server = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In, HandleInheritability.Inheritable);
        var clientClosed = false;
        try
        {
            var id = Run(Sleep(30)).Made.ShouldNotBeNull();
            var running = await Until(id, j => j.State == JobState.Running && j.Pid is not null);

            server.DisposeLocalCopyOfClientHandle();
            clientClosed = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var read = await server.ReadAsync(new byte[1], timeout.Token);

            read.ShouldBe(0);
            IsAlive(running.Pid!.Value).ShouldBeTrue();
        }
        finally
        {
            if (!clientClosed)
            {
                server.DisposeLocalCopyOfClientHandle();
            }
        }
    }

    [Fact]
    public async Task AFailingJobSaysItsExitCode()
    {
        var job = await Ended(Run(Echo("3 failed", 3)).Made!);

        (job.State, job.ExitCode, job.Error).ShouldBe((JobState.Failed, 3, "exit 3"));
        _said.Single().Args[0].ShouldContain("failed: exit 3");
    }

    [Fact]
    public async Task AFinishedJobLeavesItsRealExitCodeInTheExitFileAndItsRowHoldsItsPidAndStartTime()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var ok = await Ended(Run(Echo("fine")).Made!);
        var bad = await Ended(Run(Echo("not fine", 7)).Made!);

        foreach (var (job, code) in new[] { (ok, 0), (bad, 7) })
        {
            job.ExitPath.ShouldBe(job.LogPath + ".exit");
            File.ReadAllText(job.ExitPath!).Trim().ShouldBe(code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Exists(job.ExitPath + ".tmp").ShouldBeFalse();
            job.ExitCode.ShouldBe(code);
            job.Pid.ShouldNotBeNull().ShouldBeGreaterThan(0);
            job.ProcessStartedAt.ShouldNotBeNull().ShouldBeInRange(before, DateTimeOffset.UtcNow.AddSeconds(5));
        }
        // The script a Windows job ran is deleted once the job has ended, not when it started.
        (ok.ScriptPath is null).ShouldBe(!OperatingSystem.IsWindows());
        if (ok.ScriptPath is { } script)
        {
            File.Exists(script).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ARunningJobsRowHoldsItsPidBeforeItEnds()
    {
        var id = Run(Sleep(30)).Made!;

        var running = await Until(id, j => j.State == JobState.Running);

        running.Pid.ShouldNotBeNull();
        running.ProcessStartedAt.ShouldNotBeNull();
        running.ExitPath.ShouldBe(running.LogPath + ".exit");
        File.Exists(running.ExitPath).ShouldBeFalse();
        if (OperatingSystem.IsWindows())
        {
            File.Exists(running.ScriptPath).ShouldBeTrue();
        }
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
        await Ended(id);
    }

    [Fact]
    public async Task BothStdoutAndStderrReachTheLog()
    {
        var command = OperatingSystem.IsWindows()
            ? "'to stdout é'; [Console]::Error.WriteLine('to stderr'); 'and stdout again'"
            : "echo 'to stdout é'; echo 'to stderr' 1>&2; echo 'and stdout again'";

        var job = await Ended(Run(command).Made!);

        job.State.ShouldBe(JobState.Succeeded);
        ReadLog(job.LogPath).ReplaceLineEndings("\n").ShouldBe("to stdout é\nto stderr\nand stdout again\n");
        job.Step.ShouldBe("and stdout again");
    }

    [Fact]
    public async Task ACancelKillsTheJobsChildProcessesToo()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; $p.WaitForExit()"
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command).Made!;
        await Until(id, j => j.State == JobState.Running);
        for (var i = 0; i < 300 && !(File.Exists(pidFile) && ReadLog(pidFile).Trim().Length > 0); i++)
        {
            await Task.Delay(50, Ct);
        }
        var childPid = int.Parse(ReadLog(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        IsAlive(childPid).ShouldBeTrue();

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        job.State.ShouldBe(JobState.Cancelled);
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
    }

    [Fact]
    public async Task AForcedCancelEndsAJobWhoseChildOutlivedItsWrapperAndKillsTheChild()
    {
        // The watch looks once an hour, so the row still reads running when only the wrapper has been killed: the stuck state.
        _jobs.FollowEvery = TimeSpan.FromHours(1);
        _jobs.TreeEvery = TimeSpan.FromMilliseconds(100);
        var pidFile = Path.Combine(_root, "child.pid");
        // The child runs in the wrapper's own stdout, which is the job's log: it holds the log open.
        var command = OperatingSystem.IsWindows()
            ? $"pwsh -NoProfile -Command \"Set-Content '{pidFile}' `$PID; Start-Sleep 60\""
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command).Made!;
        var running = await Until(id, j => j.State == JobState.Running);
        for (var i = 0; i < 300 && !(File.Exists(pidFile) && ReadLog(pidFile).Trim().Length > 0); i++)
        {
            await Task.Delay(50, Ct);
        }
        var childPid = int.Parse(ReadLog(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        IsAlive(childPid).ShouldBeTrue();

        // The tree under the wrapper, as the log read gives it, once the child is in it: it is remembered from here on.
        JobLog Log() => _jobs.Describe(_state.Jobs().Single(j => j.Id == id), new JobLog { Lines = [], Length = 0 });
        for (var i = 0; i < 300 && Log().Tree?.Any(p => p.Pid == childPid) != true; i++)
        {
            await Task.Delay(50, Ct);
        }
        var tree = Log();
        tree.Pid.ShouldBe(running.Pid);
        tree.Tree![0].Pid.ShouldBe(running.Pid!.Value);
        tree.Tree[0].Depth.ShouldBe(0);
        tree.Tree.Single(p => p.Pid == childPid).Depth.ShouldBeGreaterThan(0);
        tree.Stuck.ShouldBeNull();

        // Only the wrapper is killed: the child lives on, holding the log, and the row still says running.
        using (var wrapper = System.Diagnostics.Process.GetProcessById(running.Pid.Value))
        {
            wrapper.Kill();
            wrapper.WaitForExit();
        }
        for (var i = 0; i < 300 && Log().Stuck is null; i++)
        {
            await Task.Delay(50, Ct);
        }
        var stuck = Log();
        stuck.Stuck.ShouldNotBeNull().ShouldStartWith("process exited, output still held by ");
        stuck.Tree!.ShouldContain(p => p.Pid == childPid);
        stuck.Tree!.ShouldNotContain(p => p.Pid == running.Pid);
        _state.Jobs().Single(j => j.Id == id).State.ShouldBe(JobState.Running);
        IsAlive(childPid).ShouldBeTrue();

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Args = [Verbs.ForceArg], Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled (forced) by controller"));
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
        // Its owner was told once, and the tree is not asked again once it has ended.
        var say = _said.ShouldHaveSingleItem();
        say.Args[0].ShouldContain("cancelled (forced) by controller");
        Log().Tree.ShouldBeNull();
    }

    [Fact]
    public async Task AForcedCancelOfAJobWithItsWrapperAliveKillsTheWholeTreeAtOnce()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"pwsh -NoProfile -Command \"Set-Content '{pidFile}' `$PID; Start-Sleep 60\""
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command).Made!;
        await Until(id, j => j.State == JobState.Running);
        for (var i = 0; i < 300 && !(File.Exists(pidFile) && ReadLog(pidFile).Trim().Length > 0); i++)
        {
            await Task.Delay(50, Ct);
        }
        var childPid = int.Parse(ReadLog(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Args = [Verbs.ForceArg], Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled (forced) by controller"));
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
        _said.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ACancelTakesNoArgumentButForce()
    {
        var id = Run(Sleep(30)).Made!;
        await Until(id, j => j.State == JobState.Running);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Args = ["now"], Who = "controller" })
            .Error.ShouldBe("cancel-job takes only `force`, not now");
        _state.Jobs().Single(j => j.Id == id).State.ShouldBe(JobState.Running);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        (await Ended(id)).Error.ShouldBe("cancelled by controller");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task AJobPastItsTimeoutIsStoppedWithItsWholeTreeAndSaysSo()
    {
        // A child of its own, which a kill of the wrapper alone leaves running: the timeout ends both.
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; $p.WaitForExit()"
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var id = Run(command, timeoutSeconds: 5).Made!;
        var running = await Until(id, j => j.State == JobState.Running);
        for (var i = 0; i < 300 && !(File.Exists(pidFile) && ReadLog(pidFile).Trim().Length > 0); i++)
        {
            await Task.Delay(50, Ct);
        }
        var childPid = int.Parse(ReadLog(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Failed, "stopped after its timeout of 5s"));
        (job.EndedAt!.Value - job.StartedAt).ShouldBeLessThan(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 100 && (IsAlive(childPid) || IsAlive(running.Pid!.Value)); i++)
        {
            await Task.Delay(50, Ct);
        }
        (IsAlive(running.Pid!.Value), IsAlive(childPid)).ShouldBe((false, false));
    }

    [Fact]
    public async Task APwshCommandJobKeepsTheExitCodeOfItsLastNativeCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // Unwrapped, as a check's line is: read twice, the outer pwsh would expand $LASTEXITCODE to nothing and end 0 (TIA1827).
        var job = await Ended(Run("pwsh -NoProfile -Command \"cmd /c exit 3; exit $LASTEXITCODE\"").Made!);

        (job.State, job.ExitCode, job.Error).ShouldBe((JobState.Failed, 3, "exit 3"));
    }

    [Fact]
    public async Task ACancelledJobStopsAndItsOwnerIsTold()
    {
        var id = Run(Sleep(30)).Made!;
        await Until(id, j => j.State == JobState.Running);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller"));
        _said.Single().Args[0].ShouldContain("cancelled by controller");
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Error.ShouldBe($"job {id} is not running");
    }

    [Fact]
    public async Task ASecondJobWaitsItsTurnAndStartsWhenTheFirstEnds()
    {
        var first = Run(Sleep(2)).Made!;
        var second = Run(Echo("second ran")).Made!;

        var waiting = await Until(second, j => j.Step is not null);
        (waiting.State, waiting.Step).ShouldBe((JobState.Queued, "next in line: another job is running; needs 1.0 GB (a guess, it has not run before)"));
        waiting.QueuedAt.ShouldNotBeNull();

        (await Ended(first)).State.ShouldBe(JobState.Succeeded);
        var ran = await Ended(second);
        (ran.State, ran.Step).ShouldBe((JobState.Succeeded, "second ran"));
        ran.StartedAt.ShouldBeGreaterThanOrEqualTo(ran.QueuedAt!.Value);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AJobWaitsForFreeMemoryAndStartsOnceThereIsEnough()
    {
        // Two may run at once, so it is memory that holds the second back, not the slot.
        _jobs.MaxRunning = 2;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        _free = 512L << 20;

        var id = Run(Echo("ran")).Made!;

        // Nothing is known of it: the guess of 1 GB, with no job-memory floor set.
        (await Until(id, j => j.Step is not null)).Step.ShouldBe("next in line: waiting for 1.0 GB of free memory (a guess, it has not run before; 0.5 GB now)");
        _free = 4L << 30;
        _jobs.Pump();
        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
        Cancel(holder);
    }

    private void Cancel(string id) =>
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });

    [Fact]
    public async Task TheHeadOfTheQueueWithNothingRunningStartsWhateverTheFreeMemory()
    {
        _free = 0;

        var id = Run(Echo("ran")).Made!;

        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    public async Task ASecondJobIsHeldForTheMedianOfItsKindsPastRunsAndNotForJobMemory()
    {
        // The node ran this kind of command before: three runs, medians of 3, 5 and 4 GB (the estimate is 4), peaks higher.
        SeedRun("j1", Echo("x"), median: 3L << 30, peak: 6L << 30, ended: 3);
        SeedRun("j2", Echo("x"), median: 5L << 30, peak: 9L << 30, ended: 2);
        SeedRun("j3", Echo("x"), median: 4L << 30, peak: 5L << 30, ended: 1);
        // The floor is below the estimate here, so the estimate is what the job is held for.
        _jobs.MinFreeMemory = 1L << 30;
        _jobs.MaxRunning = 2;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        _free = 3L << 30;

        var id = Run(Echo("x")).Made!;

        (await Until(id, j => j.Step is not null)).Step.ShouldBe("next in line: waiting for 4.0 GB of free memory (median of its last 3 runs, peak 9.0 GB; 3.0 GB now)");
        _free = 4L << 30;
        _jobs.Pump();
        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
        Cancel(holder);
    }

    [Fact]
    public async Task JobMemoryIsAFloorSoAKindMeasuredAboveItWaitsForAllItNeeds()
    {
        // Review of #357: as a cap, a kind measured at 8 GB started beside another with 2 GB free.
        SeedRun("j1", Echo("x"), median: 8L << 30, peak: 9L << 30, ended: 1);
        _jobs.MinFreeMemory = 2L << 30;
        _jobs.MaxRunning = 2;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        _free = 2L << 30;

        var id = Run(Echo("x")).Made!;

        (await Until(id, j => j.Step is not null)).Step.ShouldBe("next in line: waiting for 8.0 GB of free memory (median of its last run, peak 9.0 GB; 2.0 GB now)");
        _jobs.Pump();
        (await Until(id, j => j.Step is not null)).State.ShouldBe(JobState.Queued);
        _free = 8L << 30;
        _jobs.Pump();
        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
        Cancel(holder);
    }

    [Fact]
    public async Task ARunsSpikeDoesNotRaiseTheEstimateButARunWhoseMedianIsHigherDoesAtOnce()
    {
        // A median of 1 GB with a peak of 7 GB: the spike is shown, never gated on.
        var calls = 0;
        var spiky = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n3.sock"), Path.Combine(_root, "data3")), TimeProvider.System)
        {
            FreeMemory = () => null,
            TreeEvery = TimeSpan.FromMilliseconds(10),
            FollowEvery = TimeSpan.FromMilliseconds(20),
            // One spike among the samples of a run that otherwise holds 1 GB.
            MemorySample = _ => Interlocked.Increment(ref calls) == 4 ? 7L << 30 : 1L << 30,
            Say = (command, _) => Task.FromResult(_state.Complete(command, null)),
        };
        await spiky.StartAsync(Ct);
        await spiky.Recovered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            var made = spiky.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.NodeTarget, Args = [_root, "0", "sleep 1; echo slept"], Who = "t" }).Made!;
            var ran = await Until(made, j => j.EndedAt is not null);

            (ran.MemoryMedian, ran.MemoryPeak).ShouldBe((1L << 30, 7L << 30));
            // The spike did not move the estimate: its median did not, and the estimate is made of medians.
            JobMemory.Estimate(_state.Jobs(), JobKey.Parse(ran.MemoryKey)!).Bytes.ShouldBe(1L << 30);
        }
        finally
        {
            await spiky.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ARunWhoseMedianIsAboveTheEstimateRaisesItAtOnceAndAnOldHighRunStaysInTheMedian()
    {
        var key = JobKey.Of(Echo("x"), _root);
        SeedRun("j1", Echo("x"), median: 1L << 30, peak: 1L << 30, ended: 5);
        SeedRun("j2", Echo("x"), median: 1L << 30, peak: 1L << 30, ended: 4);
        SeedRun("j3", Echo("x"), median: 1L << 30, peak: 1L << 30, ended: 3);
        JobMemory.Estimate(_state.Jobs(), key).Bytes.ShouldBe(1L << 30);

        // One run at 6 GB: the median of five is still 1 GB, but the run's median is above it, so it counts at once.
        SeedRun("j4", Echo("x"), median: 6L << 30, peak: 6L << 30, ended: 2);
        JobMemory.Estimate(_state.Jobs(), key).Bytes.ShouldBe(6L << 30);
    }

    [Fact]
    public async Task AQueuedJobBehindAnotherSaysWhatItNeedsAndTheMemoryFreeNow()
    {
        _free = 1L << 30;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        Run(Echo("b"));
        _free = 0;

        var behind = Run(Echo("c")).Made!;

        (await Until(behind, j => j.Step is not null)).Step.ShouldBe("queued, 1 ahead of it; needs 1.0 GB (a guess, it has not run before), 0.0 GB free now");
        Cancel(holder);
    }

    /// <summary>A job that ended by itself some hours ago, which the node measured.</summary>
    private void SeedRun(string id, string command, long median, long peak, int ended) =>
        _state.PutJob(new JobRow
        {
            Id = id, Command = command, Cwd = _root, State = JobState.Succeeded, ExitCode = 0, LogPath = Path.Combine(_root, id + ".log"),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddHours(-ended - 1), EndedAt = DateTimeOffset.UtcNow.AddHours(-ended), Told = true,
            MemoryKey = JobKey.Of(command, _root).ToString(), MemoryMedian = median, MemoryPeak = peak,
        });

    [Fact]
    public async Task AQueuedJobCancelledNeverRunsAndItsOwnerIsTold()
    {
        _free = 0;
        var holder = Run(Sleep(30)).Made!;
        await Until(holder, j => j.State == JobState.Running);
        var id = Run(Echo("never")).Made!;
        await Until(id, j => j.Step is not null);

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller before its turn"));
        File.Exists(job.LogPath).ShouldBeFalse();
        _said.Single().Args[0].ShouldContain("cancelled by controller before its turn");
        Cancel(holder);
    }

    [Fact]
    public async Task ACancelThatReachesTheDispatcherBeforeItsJobStillCancelsIt()
    {
        // As when a cancel lands between Run putting the row and handing the job over: Withdrawn before HandedOver. A
        // runner that is not started has no dispatcher loop, so the test drives the signals in that order itself.
        var idle = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n2.sock"), Path.Combine(_root, "data2")), TimeProvider.System)
        {
            FreeMemory = () => null,
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                return Task.FromResult(_state.Complete(command, null));
            },
        };
        var job = new JobRow
        {
            Id = "j90", Command = Echo("never"), Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Queued,
            LogPath = Path.Combine(_root, "j90.log"), Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow,
        };
        _state.PutJob(job);

        idle.Dispatch(new JobRunner.Withdrawn("j90", "controller"));
        idle.Dispatch(new JobRunner.HandedOver(job));

        var ended = await Ended("j90");
        (ended.State, ended.Error).ShouldBe((JobState.Cancelled, "cancelled by controller before its turn"));
        File.Exists(job.LogPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ACancelThatSeesARunningJobFindsItsProcess()
    {
        // The moment the Running row is published, a cancel must find the process: the runner adds it first.
        string? answer = null;
        using var watch = _state.Subscribe(null, null, new OnJobRunning(id =>
            answer ??= _jobs.IsRunning(id) ? "found" : "not running"));

        var id = Run(Sleep(30)).Made!;
        await Until(id, j => j.State == JobState.Running);
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
        await Ended(id);

        answer.ShouldBe("found");
    }

    [Theory]
    [InlineData("python -c 'import os,time\nwhile not os.path.exists(\"/tmp/done\"): time.sleep(5)'")]
    [InlineData("while [ ! -f /tmp/done ]; do sleep 5; done")]
    [InlineData("while (-not (Test-Path 'C:\\x\\done')) { Start-Sleep -Seconds 5 }")]
    [InlineData("sleep 600")]
    public void AJobThatOnlySleepsIsRefusedBeforeItTakesAnId(string command)
    {
        var result = Run(command);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("pilot leg wait --for-file");
        _state.Jobs().ShouldBeEmpty();
    }

    [Fact]
    public async Task ARealCommandWithASleepAmongOtherWorkIsNotRefused()
    {
        var started = Run("echo 'tests passed'; sleep 1");

        started.Ok.ShouldBeTrue();
        (await Ended(started.Made!)).State.ShouldBe(JobState.Succeeded);
    }

    /// <summary>A clock the test moves.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now += span;
    }

    // The runner is not started: the test is the dispatcher, so nothing else touches its queue, and the clock and the CPU are the test's.
    private (JobRunner Jobs, Clock Clock, Dictionary<string, TimeSpan> Cpu, Func<string, Task> Gone) Idle()
    {
        var clock = new Clock();
        var cpu = new Dictionary<string, TimeSpan>();
        var jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), clock)
        {
            FreeMemory = () => null,
            IdleWindow = TimeSpan.FromMinutes(15),
            IdleCpu = TimeSpan.FromSeconds(5),
            JobCpu = id => cpu.TryGetValue(id, out var used) ? used : null,
        };
        return (jobs, clock, cpu, async id =>
        {
            jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });
            await Until(id, j => j.EndedAt is not null);
        });
    }

    private string Hand(JobRunner jobs, string command)
    {
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [_root, "0", command], Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private CardRow[] IdleCards(bool openOnly = true) => [.. _state.Cards(openOnly).Where(c => c.Kind == JobRunner.IdleKind)];

    [Fact]
    public async Task ANearIdleJobWithAnotherQueuedBehindItRaisesOneCardNamingJobOwnerAndWaiter()
    {
        var (jobs, clock, cpu, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        var second = Hand(jobs, Echo("later"));
        cpu[first] = TimeSpan.FromSeconds(1);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        IdleCards().ShouldBeEmpty();

        clock.Advance(TimeSpan.FromMinutes(16));
        cpu[first] = TimeSpan.FromSeconds(2);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        var card = IdleCards().ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, Verbs.JobTarget(first)));
        card.Title.ShouldContain(first);
        (card.Detail ?? "").ShouldContain("se1-tia1-parser");
        (card.Detail ?? "").ShouldContain(second);

        await gone(first);
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        IdleCards().ShouldBeEmpty();
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ANearIdleJobWithNothingQueuedBehindItRaisesNoCard()
    {
        var (jobs, clock, cpu, _) = Idle();
        var first = Hand(jobs, Sleep(60));
        cpu[first] = TimeSpan.Zero;
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        clock.Advance(TimeSpan.FromMinutes(16));
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        IdleCards(openOnly: false).ShouldBeEmpty();
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AJobThatKeepsWorkingRaisesNoCardAndTheCardClosesWhenTheQueueEmpties()
    {
        var (jobs, clock, cpu, _) = Idle();
        var first = Hand(jobs, Sleep(60));
        var second = Hand(jobs, Echo("later"));
        cpu[first] = TimeSpan.Zero;
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        // Busy: 20 s of CPU in each ten minutes, so the window keeps starting again.
        for (var i = 1; i <= 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            cpu[first] = TimeSpan.FromSeconds(20 * i);
            jobs.Dispatch(JobRunner.LookAgain.Instance);
        }
        IdleCards(openOnly: false).ShouldBeEmpty();

        // Idle now: the card comes, and goes when the waiting job is withdrawn.
        clock.Advance(TimeSpan.FromMinutes(16));
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        IdleCards().ShouldHaveSingleItem();
        jobs.Dispatch(new JobRunner.Withdrawn(second, "controller"));
        IdleCards().ShouldBeEmpty();
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AJobWhoseCpuReadingFallsWhenAChildEndsAndRisesAgainRaisesNoCard()
    {
        var (jobs, clock, cpu, _) = Idle();
        var first = Hand(jobs, Sleep(60));
        Hand(jobs, Echo("later"));
        double[] sawtooth = [0.8, 2.0, 3.1, 1.2, 2.2, 3.3, 1.4, 2.5, 3.7, 0.0];
        foreach (var seconds in sawtooth.Concat(sawtooth))
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            cpu[first] = TimeSpan.FromSeconds(seconds);
            jobs.Dispatch(JobRunner.LookAgain.Instance);
        }
        IdleCards(openOnly: false).ShouldBeEmpty();
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void TheCpuTallyNeverFallsWhenAProcessEndsAndKeepsWhatAnEndedOneUsed()
    {
        var tally = new ProcessTreeCpu.Tally();
        var started = new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
        tally.Note(10, started, TimeSpan.FromSeconds(1));
        tally.Note(11, started, TimeSpan.FromSeconds(3));
        tally.Total.ShouldBe(TimeSpan.FromSeconds(4));

        // Pid 11 ended and is not noted again: its 3 s still count, and a new child adds to them.
        tally.Note(12, started.AddSeconds(5), TimeSpan.FromSeconds(2));
        tally.Note(10, started, TimeSpan.FromSeconds(1.5));
        tally.Total.ShouldBe(TimeSpan.FromSeconds(6.5));

        // A reused pid with a later start time is another process.
        tally.Note(11, started.AddSeconds(9), TimeSpan.FromSeconds(0.5));
        tally.Total.ShouldBe(TimeSpan.FromSeconds(7));
    }

    /// <summary>Calls back, as the state publishes it, when a job's Running row is put.</summary>
    private sealed class OnJobRunning(Action<string> running) : INodeEventSink
    {
        public int Capacity => 10_000;

        public bool TryWrite(byte[] message)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("job", out var job) && job.GetProperty("state").GetString() == "running")
            {
                running(job.GetProperty("id").GetString()!);
            }
            return true;
        }

        public void Overflowed()
        {
        }
    }

    /// <summary>
    /// A node on the test's data folder: its own journal, state and runner, as a restart builds them. Stopped, it leaves its
    /// jobs running and its journal on disk for the next one, which is how a node's end looks to its successor.
    /// </summary>
    private sealed class Node : IDisposable
    {
        private readonly FileJournal _journal;

        private Node(string root, Func<long?> freeMemory)
        {
            _journal = new FileJournal(Path.Combine(root, "journal"));
            State = new NodeState(_journal, TimeProvider.System);
            Jobs = new JobRunner(State, new NodeOptions(Path.Combine(root, "n.sock"), Path.Combine(root, "data")), TimeProvider.System)
            {
                FreeMemory = freeMemory,
                Say = (command, _) =>
                {
                    Said.Enqueue(command);
                    return Task.FromResult(State.Complete(command, null));
                },
            };
        }

        public NodeState State { get; }

        public JobRunner Jobs { get; }

        public ConcurrentQueue<CommandRequest> Said { get; } = new();

        public static async Task<Node> StartAsync(string root, Func<long?>? freeMemory = null)
        {
            var node = new Node(root, freeMemory ?? (() => null));
            if (node.State.Sessions().Length == 0)
            {
                node.State.PutSession(new SessionRow
                {
                    Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-parser", Kind = SessionKind.Background, Cwd = root,
                    Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
                    SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
                });
            }
            await node.Jobs.StartAsync(CancellationToken.None);
            await node.Jobs.Recovered.WaitAsync(TimeSpan.FromSeconds(10));
            return node;
        }

        public CommandResult Run(string cwd, string command, long timeoutSeconds = 0) =>
            Jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [cwd, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), command], Who = "se1-tia1-parser" });

        public CommandResult Cancel(string id) =>
            Jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" });

        public JobRow Job(string id) => State.Jobs().Single(j => j.Id == id);

        public async Task<JobRow> UntilAsync(string id, Func<JobRow, bool> condition, CancellationToken cancellationToken)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Job(id) is var job && condition(job))
                {
                    return job;
                }
                await Task.Delay(50, cancellationToken);
            }
            throw new TimeoutException($"job {id} never came to the state awaited");
        }

        /// <summary>The job once it has ended and its owner has been told.</summary>
        public Task<JobRow> EndedAsync(string id, CancellationToken cancellationToken) => UntilAsync(id, j => j.EndedAt is not null && j.Told, cancellationToken);

        /// <summary>The node going away: its jobs run on and their rows stay as they are.</summary>
        public async Task StopAsync()
        {
            await Jobs.StopAsync(CancellationToken.None);
            _journal.Dispose();
        }

        public void Dispose() => _journal.Dispose();
    }

    [Fact]
    public async Task AJobARestartedNodeFindsAgainEndsUnderItWithItsRealExitCodeAndItsOwnerIsToldOnce()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, "sleep 2; echo 'finished under the next node'; exit 5").Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);

        await first.StopAsync();
        // The node going away is not the job's end: nothing was said, and the process still runs.
        first.Said.ShouldBeEmpty();
        IsAlive(running.Pid!.Value).ShouldBeTrue();

        var second = await Node.StartAsync(_root);
        try
        {
            second.Job(id).State.ShouldBe(JobState.Running);
            second.Jobs.IsRunning(id).ShouldBeTrue();

            var job = await second.EndedAsync(id, Ct);

            (job.State, job.ExitCode, job.Error, job.Step).ShouldBe((JobState.Failed, 5, "exit 5", "finished under the next node"));
            var say = second.Said.ShouldHaveSingleItem();
            say.Args[0].ShouldContain($"Your job {id}");
            say.Args[0].ShouldContain("failed: exit 5");
            say.Args[0].ShouldContain("finished under the next node");
            first.Said.ShouldBeEmpty();

            // A third node finds it ended, and tells nobody again.
            await second.StopAsync();
            using var third = await Node.StartAsync(_root);
            third.Said.ShouldBeEmpty();
            third.Job(id).Told.ShouldBeTrue();
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public async Task AJobThatEndedWhileNoNodeWasUpIsEndedFromItsExitFile()
    {
        var first = await Node.StartAsync(_root);
        // The sleep keeps the job running until the first node is stopped: a command that ends at once can be ended by that node itself before the test sees it running.
        var id = first.Run(_root, "sleep 2; echo 'ended in the dark'; exit 4").Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        await first.StopAsync();
        for (var i = 0; i < 300 && !(File.Exists(running.ExitPath) && !IsAlive(running.Pid!.Value)); i++)
        {
            await Task.Delay(50, Ct);
        }
        File.Exists(running.ExitPath).ShouldBeTrue();

        using var second = await Node.StartAsync(_root);
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.ExitCode, job.Error, job.Step).ShouldBe((JobState.Failed, 4, "exit 4", "ended in the dark"));
        job.EndedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(job.StartedAt);
        second.Said.ShouldHaveSingleItem().Args[0].ShouldContain("failed: exit 4");
        // The script a Windows job ran is deleted, the node that ended it was not the one that started it.
        if (job.ScriptPath is { } script)
        {
            File.Exists(script).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task AJobThatEndedWhileNoNodeWasUpWithExitCodeZeroSucceeded()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, "sleep 2; " + Echo("all passed")).Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        await first.StopAsync();
        for (var i = 0; i < 300&& !(File.Exists(running.ExitPath) && !IsAlive(running.Pid!.Value)); i++)
        {
            await Task.Delay(50, Ct);
        }

        using var second = await Node.StartAsync(_root);
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.ExitCode, job.Error, job.Step).ShouldBe((JobState.Succeeded, 0, null, "all passed"));
        second.Said.ShouldHaveSingleItem().Args[0].ShouldContain("succeeded");
    }

    [Fact]
    public async Task AJobWhoseProcessIsGoneWithNoExitFileFailsSayingSoAndIsToldOnce()
    {
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Pid = int.MaxValue - 7, ProcessStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await _jobs.RecoverAsync(Ct);
        // Looked at again (as a second start would): it has ended and been told, so nothing more is said.
        await _jobs.RecoverAsync(Ct);

        var job = _state.Jobs().Single();
        (job.State, job.Error, job.Told).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded", true));
        _said.Single().Args[0].ShouldContain("Your job j1");
    }

    [Fact]
    public async Task AJobWhoseRowSaysNoProcessAtAllFailsAsOneGoneWithoutAnExitFile()
    {
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await _jobs.RecoverAsync(Ct);

        (_state.Jobs().Single().State, _state.Jobs().Single().Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"));
    }

    [Fact]
    public async Task APidAnotherProcessHasTakenIsNotTakenForTheJob()
    {
        // The pid is a live process's (this test's own), but it started long after the job did: it is not the job's.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Pid = self.Id, ProcessStartedAt = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero).AddHours(-3),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
        });

        await _jobs.RecoverAsync(Ct);

        var job = _state.Jobs().Single();
        (job.State, job.Error).ShouldBe((JobState.Failed, "ended while the node was down, no exit recorded"));
        _jobs.IsRunning("j1").ShouldBeFalse();
        // And it was not killed for it.
        IsAlive(self.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task APidAnotherProcessHasTakenDoesNotHideTheExitFileTheJobLeft()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var log = Path.Combine(_root, "j1.log");
        File.WriteAllText(log + ".exit", "0\n");
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = log, ExitPath = log + ".exit", Pid = self.Id, ProcessStartedAt = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero).AddHours(-3),
            Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
        });

        await _jobs.RecoverAsync(Ct);

        (_state.Jobs().Single().State, _state.Jobs().Single().ExitCode).ShouldBe((JobState.Succeeded, 0));
    }

    [Fact]
    public async Task QueuedJobsSurviveARestartAndRunInTheOrderTheyCame()
    {
        // A job runs while the three wait behind it: with nothing running the head would start at once, whatever the memory.
        var first = await Node.StartAsync(_root, () => 0);
        var blocker = first.Run(_root, Sleep(30)).Made!;
        await first.UntilAsync(blocker, j => j.State == JobState.Running, Ct);
        var one = first.Run(_root, Echo("one ran")).Made!;
        var two = first.Run(_root, Echo("two ran")).Made!;
        var three = first.Run(_root, Echo("three ran")).Made!;
        await first.UntilAsync(three, j => j.Step is not null, Ct);
        await first.StopAsync();

        using var second = await Node.StartAsync(_root, () => 0);
        // Still waiting, nothing failed, and the node said nothing; the job that ran on is followed again.
        foreach (var id in new[] { one, two, three })
        {
            second.Job(id).State.ShouldBe(JobState.Queued);
        }
        second.Job(blocker).State.ShouldBe(JobState.Running);
        await second.UntilAsync(three, j => j.Step is { } step && step.StartsWith("queued, 2 ahead of it", StringComparison.Ordinal), Ct);
        second.Said.ShouldBeEmpty();
        second.Job(one).Step.ShouldNotBeNull().ShouldStartWith("next in line");

        second.Jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(blocker), Who = "controller" }).Ok.ShouldBeTrue();
        var ended = new[] { await second.EndedAsync(one, Ct), await second.EndedAsync(two, Ct), await second.EndedAsync(three, Ct) };

        ended.Select(j => j.State).ShouldAllBe(state => state == JobState.Succeeded);
        ended.Select(j => j.Step).ShouldBe(["one ran", "two ran", "three ran"]);
        ended[0].StartedAt.ShouldBeLessThanOrEqualTo(ended[1].StartedAt);
        ended[1].StartedAt.ShouldBeLessThanOrEqualTo(ended[2].StartedAt);
        second.Said.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ACancelKillsAReattachedJobAndItsChildProcessesToo()
    {
        var pidFile = Path.Combine(_root, "child.pid");
        var command = OperatingSystem.IsWindows()
            ? $"$p = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 60' -NoNewWindow -PassThru; Set-Content '{pidFile}' $p.Id; $p.WaitForExit()"
            : $"sleep 60 & echo $! > '{pidFile}'; wait";
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, command).Made!;
        await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        for (var i = 0; i < 300 && !(File.Exists(pidFile) && ReadLog(pidFile).Trim().Length > 0); i++)
        {
            await Task.Delay(50, Ct);
        }
        var childPid = int.Parse(ReadLog(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await first.StopAsync();
        IsAlive(childPid).ShouldBeTrue();

        using var second = await Node.StartAsync(_root);
        second.Jobs.IsRunning(id).ShouldBeTrue();
        second.Cancel(id).Ok.ShouldBeTrue();
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller"));
        for (var i = 0; i < 100 && IsAlive(childPid); i++)
        {
            await Task.Delay(50, Ct);
        }
        IsAlive(childPid).ShouldBeFalse();
        second.Said.ShouldHaveSingleItem().Args[0].ShouldContain("cancelled by controller");
    }

    [Fact]
    public async Task AReattachedJobsTimeoutCountsFromItsStartNotFromTheRestart()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, Sleep(60), timeoutSeconds: 3).Made!;
        await first.UntilAsync(id, j => j.State == JobState.Running, Ct);
        await first.StopAsync();
        await Task.Delay(2000, Ct);

        using var second = await Node.StartAsync(_root);
        var job = await second.EndedAsync(id, Ct);

        (job.State, job.Error).ShouldBe((JobState.Failed, "stopped after its timeout of 3s"));
        // From the start: it had 1s left when the second node came up, not 3s anew.
        (DateTimeOffset.UtcNow - job.StartedAt).ShouldBeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task StoppingTheNodeLeavesItsRunningJobRunningAndItsRowRunning()
    {
        var first = await Node.StartAsync(_root);
        var id = first.Run(_root, Sleep(60)).Made!;
        var running = await first.UntilAsync(id, j => j.State == JobState.Running, Ct);

        await first.StopAsync();

        try
        {
            IsAlive(running.Pid!.Value).ShouldBeTrue();
            File.Exists(running.ExitPath).ShouldBeFalse();
            first.Said.ShouldBeEmpty();
            using var second = await Node.StartAsync(_root);
            second.Job(id).State.ShouldBe(JobState.Running);
            second.Cancel(id).Ok.ShouldBeTrue();
            (await second.EndedAsync(id, Ct)).State.ShouldBe(JobState.Cancelled);
        }
        finally
        {
            JobKiller.Kill(running);
        }
    }

    [Fact]
    public async Task AnOwnerThatCannotBeToldRings()
    {
        _refuse = "the folder is gone";

        var job = await Ended(Run(Echo("done")).Made!);
        // The ring follows the refused tell, a moment after the job ends: waited for, as the end is (flaky under load).
        for (var i = 0; i < 100 && _state.Cards(openOnly: true).Length == 0; i++)
        {
            await Task.Delay(50, Ct);
        }

        job.Told.ShouldBeFalse();
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, JobRunner.UntoldKind, Verbs.JobTarget(job.Id)));
        card.Detail.ShouldNotBeNull().ShouldContain("the folder is gone");
    }

    [Fact]
    public void ARunIsRefusedWithWhy()
    {
        Run(Echo("x"), cwd: Path.Combine(_root, "nope")).Error.ShouldNotBeNull().ShouldStartWith("there is no folder");
        Run(Echo("x"), target: "session/ffffffff").Error.ShouldBe("There is no session ffffffff to wake with the result");
        Run(Echo("x"), timeoutSeconds: 9 * 3600).Error.ShouldBe("a job may run 8 hours at most");
        _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "node", Args = [_root, "0"], Who = "t" }).Error.ShouldNotBeNull().ShouldStartWith("run takes");
    }

    [Fact]
    public async Task AWorkerIdleWhileItsJobRunsIsNotNudged()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var state = new NodeState(new InMemoryJournal(), time);
        state.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = "a1b2c3d4", StartedAt = time.GetUtcNow() });
        state.PutSession(_state.Sessions().Single() with { LastResponseAt = time.GetUtcNow() });
        state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", State = JobState.Running, LogPath = "x", Timeout = JobRunner.DefaultTimeout,
            StartedAt = time.GetUtcNow(),
        });
        var said = new List<CommandRequest>();
        var watch = new LegWatch(state, (c, _) => { said.Add(c); return Task.FromResult(state.Complete(c, null)); }, time);

        time.Advance(TimeSpan.FromMinutes(30));
        await watch.LookAsync(Ct);
        said.ShouldBeEmpty();

        state.PutJob(state.Jobs().Single() with { State = JobState.Succeeded, EndedAt = time.GetUtcNow() });
        await watch.LookAsync(Ct);
        said.ShouldHaveSingleItem().Args[0].ShouldContain("is not finished yet");
    }

    [Theory]
    [InlineData("90m", 90 * 60)]
    [InlineData("2h", 7200)]
    [InlineData("45s", 45)]
    public void ATimeoutIsWrittenAsPeopleWriteIt(string text, int seconds)
    {
        PilotCommand.TryDuration(text, out var span).ShouldBeTrue();
        span.ShouldBe(TimeSpan.FromSeconds(seconds));
        PilotCommand.TryDuration("2d", out _).ShouldBeFalse();
    }
}

/// <summary>Kills a job's process from its row, for a test that must not leave one running.</summary>
internal static class JobKiller
{
    public static void Kill(JobRow job)
    {
        if (job is { Pid: { } pid, ProcessStartedAt: { } startedAt })
        {
            new JobProcess(pid, startedAt).Kill();
        }
    }
}

/// <summary>What follows a job's log.</summary>
public sealed class JobFilesTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), "p2f-" + Guid.NewGuid().ToString("N")[..8] + ".log");

    public void Dispose() => File.Delete(_log);

    [Fact]
    public void ALineTheJobHasNotEndedYetWaitsForItsNewline()
    {
        var follower = new LogFollower(_log);
        follower.Read(final: false).ShouldBeEmpty();

        File.WriteAllText(_log, "one\r\ntwo\nthr");
        follower.Read(final: false).ShouldBe(["one", "two"]);

        File.AppendAllText(_log, "ee\nfour");
        follower.Read(final: false).ShouldBe(["three"]);
        follower.Read(final: true).ShouldBe(["four"]);
        follower.Read(final: true).ShouldBeEmpty();
    }

    [Fact]
    public void AMultiByteCharacterSplitBetweenTwoReadsIsWholeInTheLine()
    {
        var follower = new LogFollower(_log);
        var bytes = System.Text.Encoding.UTF8.GetBytes("é\n");
        File.WriteAllBytes(_log, bytes[..1]);
        follower.Read(final: false).ShouldBeEmpty();

        using (var stream = new FileStream(_log, FileMode.Append))
        {
            stream.Write(bytes, 1, bytes.Length - 1);
        }

        follower.Read(final: false).ShouldBe(["é"]);
    }

    [Fact]
    public void AFollowerThatStartsAtTheEndReadsOnlyTheLastWholeLinesAndThenWhatComesAfter()
    {
        var head = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i:0000}\n"));
        File.WriteAllText(_log, head);
        var follower = new LogFollower(_log, fromEnd: true);

        var lines = follower.Read(final: false);

        lines.Count.ShouldBeLessThan(3000);
        lines[^1].ShouldBe("line 2999");
        // Whole lines only: the one the window started in is dropped.
        lines.ShouldAllBe(l => l.StartsWith("line ", StringComparison.Ordinal) && l.Length == 9);
        File.AppendAllText(_log, "after\npart");
        follower.Read(final: false).ShouldBe(["after"]);
        follower.Read(final: true).ShouldBe(["part"]);
    }
}

/// <summary>A job's command line from <c>pilot run</c>'s words, quoted again for the node's shell.</summary>
public sealed class ShellLineTests
{
    [Fact]
    public void AWordHoldingAShellCharacterIsQuotedAgainAndAnOperatorIsLeftOne()
    {
        string[] words = ["dotnet", "test", "-c", "Release", "--filter", "FullyQualifiedName~A|FullyQualifiedName~B", "2>&1"];

        Pilot.Node.Flights.ShellLine.Join(words, windows: true)
            .ShouldBe("dotnet test -c Release --filter 'FullyQualifiedName~A|FullyQualifiedName~B' 2>&1");
        Pilot.Node.Flights.ShellLine.Join(words, windows: false)
            .ShouldBe("dotnet test -c Release --filter 'FullyQualifiedName~A|FullyQualifiedName~B' 2>&1");
        Pilot.Node.Flights.ShellLine.Join(["pwsh", "-File", "C:/temp/a b/run.ps1"], windows: true).ShouldBe("pwsh -File 'C:/temp/a b/run.ps1'");
        Pilot.Node.Flights.ShellLine.Join(["echo", "it's"], windows: true).ShouldBe("echo 'it''s'");
        Pilot.Node.Flights.ShellLine.Join(["echo", "it's"], windows: false).ShouldBe(@"echo 'it'\''s'");
    }

    [Fact]
    public void OneWordIsACommandLineAlready() =>
        Pilot.Node.Flights.ShellLine.Join(["dotnet test --filter \"A|B\" && echo done"], windows: true).ShouldBe("dotnet test --filter \"A|B\" && echo done");
}
