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

    private static string Echo(string text, int exit = 0) => OperatingSystem.IsWindows() ? $"echo {text}& exit /b {exit}" : $"echo {text}; exit {exit}";

    private static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"ping -n {seconds + 1} 127.0.0.1 >nul" : $"sleep {seconds}";

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
        File.ReadAllText(job.LogPath).ShouldContain("all 12 tests passed");
        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/a1b2c3d4", "node"));
        say.Args[0].ShouldContain($"Your job {id}");
        say.Args[0].ShouldContain("succeeded");
        say.Args[0].ShouldContain("all 12 tests passed");
    }

    [Fact]
    public async Task AFailingJobSaysItsExitCode()
    {
        var job = await Ended(Run(Echo("3 failed", 3)).Made!);

        (job.State, job.ExitCode, job.Error).ShouldBe((JobState.Failed, 3, "exit 3"));
        _said.Single().Args[0].ShouldContain("failed: exit 3");
    }

    [Fact]
    public async Task AJobPastItsTimeoutIsStoppedAndSaysSo()
    {
        var job = await Ended(Run(Sleep(30), timeoutSeconds: 1).Made!);

        (job.State, job.Error).ShouldBe((JobState.Failed, "stopped after its timeout of 1s"));
        (job.EndedAt!.Value - job.StartedAt).ShouldBeLessThan(TimeSpan.FromSeconds(20));
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
        (waiting.State, waiting.Step).ShouldBe((JobState.Queued, "next in line: another job is running"));
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
        _free = 1L << 30;

        var id = Run(Echo("ran")).Made!;

        (await Until(id, j => j.Step is not null)).Step.ShouldBe("next in line: waiting for 2.0 GB of free memory (1.0 GB now)");
        _free = 4L << 30;
        _jobs.Pump();
        (await Ended(id)).State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    public async Task AJobBehindAnotherSaysHowManyAreAhead()
    {
        _free = 0;

        Run(Echo("a"));
        var behind = Run(Echo("b")).Made!;

        (await Until(behind, j => j.Step is not null)).Step.ShouldBe("queued, 1 ahead of it");
    }

    [Fact]
    public async Task AQueuedJobCancelledNeverRunsAndItsOwnerIsTold()
    {
        _free = 0;
        var id = Run(Echo("never")).Made!;

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller before its turn"));
        File.Exists(job.LogPath).ShouldBeFalse();
        _said.Single().Args[0].ShouldContain("cancelled by controller before its turn");
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

    [Fact]
    public async Task AQueuedJobARestartOrphanedIsFailedAndItsOwnerTold()
    {
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Queued,
            LogPath = Path.Combine(_root, "j1.log"), Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await _jobs.RecoverAsync(Ct);

        (_state.Jobs().Single().State, _state.Jobs().Single().Error).ShouldBe((JobState.Failed, "the node stopped before its turn came"));
        _said.Single().Args[0].ShouldContain("Your job j1");
    }

    [Fact]
    public async Task AJobARestartOrphanedIsFailedAndItsOwnerTold()
    {
        _state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, Owner = "a1b2c3d4", OwnerName = "se1-tia1-parser", State = JobState.Running,
            LogPath = Path.Combine(_root, "j1.log"), Timeout = JobRunner.DefaultTimeout, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await _jobs.RecoverAsync(Ct);

        var job = _state.Jobs().Single();
        (job.State, job.Error, job.Told).ShouldBe((JobState.Failed, "the node stopped while it ran, so its end was never seen", true));
        _said.Single().Args[0].ShouldContain("Your job j1");
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

/// <summary>A job's command line from <c>pilot run</c>'s words, quoted again for the node's shell.</summary>
public sealed class ShellLineTests
{
    [Fact]
    public void AWordHoldingAShellCharacterIsQuotedAgainAndAnOperatorIsLeftOne()
    {
        string[] words = ["dotnet", "test", "-c", "Release", "--filter", "FullyQualifiedName~A|FullyQualifiedName~B", "2>&1"];

        Pilot.Node.Flights.ShellLine.Join(words, windows: true)
            .ShouldBe("dotnet test -c Release --filter \"FullyQualifiedName~A|FullyQualifiedName~B\" 2>&1");
        Pilot.Node.Flights.ShellLine.Join(words, windows: false)
            .ShouldBe("dotnet test -c Release --filter 'FullyQualifiedName~A|FullyQualifiedName~B' 2>&1");
        Pilot.Node.Flights.ShellLine.Join(["pwsh", "-File", "C:/temp/a b/run.ps1"], windows: true).ShouldBe("pwsh -File \"C:/temp/a b/run.ps1\"");
        Pilot.Node.Flights.ShellLine.Join(["echo", "it's"], windows: false).ShouldBe(@"echo 'it'\''s'");
    }

    [Fact]
    public void OneWordIsACommandLineAlready() =>
        Pilot.Node.Flights.ShellLine.Join(["dotnet test --filter \"A|B\" && echo done"], windows: true).ShouldBe("dotnet test --filter \"A|B\" && echo done");
}
