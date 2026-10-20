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
    private readonly List<CommandRequest> _said = [];
    private readonly JobRunner _jobs;
    private string? _refuse;

    public JobTests()
    {
        Directory.CreateDirectory(_root);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            Say = (command, _) =>
            {
                lock (_said)
                {
                    _said.Add(command);
                }
                return Task.FromResult(_state.Complete(command, _refuse));
            },
        };
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia1-parser", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
    }

    public void Dispose()
    {
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
        var running = _state.Jobs().Single();
        (running.Id, running.State, running.Owner, running.OwnerName, running.Flight, running.Leg).ShouldBe((id, JobState.Running, "a1b2c3d4", "se1-tia1-parser", "TIA1", "parser"));

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

        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Ok.ShouldBeTrue();
        var job = await Ended(id);

        (job.State, job.Error).ShouldBe((JobState.Cancelled, "cancelled by controller"));
        _said.Single().Args[0].ShouldContain("cancelled by controller");
        _jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(id), Who = "controller" }).Error.ShouldBe($"job {id} is not running");
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
