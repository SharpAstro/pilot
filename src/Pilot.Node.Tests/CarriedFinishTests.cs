using System.Collections.Concurrent;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg whose finish was rerunning its check when the node restarted is finished by the next node itself: the node keeps what the finish
/// was asked (its report and who asked), waits on the carried check job or runs the check afresh, and records the leg as an ordinary finish
/// would, with the worker told what the node did instead of asked to finish again (pilot #819).
/// </summary>
[Collection(JobsCollection.Name)]
[Trait("Category", "Slow")]
public sealed class CarriedFinishTests : LegCommanderTestsBase
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    private const string Report = "parser in";

    private string Gate => Path.Combine(_root, "gate");

    // A check that runs until the gate file exists, then exits with the given code: the same line in pwsh and sh as RestartTests uses.
    private string WaitingFor(int exit) =>
        OperatingSystem.IsWindows()
            ? $"while (-not (Test-Path -LiteralPath '{Gate}')) {{ Start-Sleep -Milliseconds 100 }}; echo 'finished'; exit {exit}"
            : $"while [ ! -e '{Gate}' ]; do sleep 0.1; done; echo 'finished'; exit {exit}";

    private static string Echoing(string text, int exit) => $"echo '{text}'; exit {exit}";

    private sealed class Restarted(NodeState state, JobRunner jobs, LegCommander legs, LegWatch watch, ConcurrentQueue<CommandRequest> said) : IAsyncDisposable
    {
        public NodeState State { get; } = state;

        public JobRunner Jobs { get; } = jobs;

        public LegCommander Legs { get; } = legs;

        public LegWatch Watch { get; } = watch;

        public ConcurrentQueue<CommandRequest> Said { get; } = said;

        public LegRow Parser => State.Legs("TIA1").Single(l => l.Name == "parser");

        public JobRow[] Checks => [.. State.Jobs().Where(j => j.Kind == JobKind.Check)];

        public async ValueTask DisposeAsync() => await Jobs.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// What a node that stopped while a worker's finish reran its check leaves: the leg flying and rechecking (asked by <c>tests</c> with a
    /// report), and the check's job as <paramref name="job"/> says. A fresh node is built on that, and its job runner takes up what was left.
    /// </summary>
    private async Task<Restarted> RestartedAsync(string check, Func<FlightRow, JobRow>? job, bool asked = true)
    {
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        await Leg(Verbs.Check, "flight/TIA1/parser", "2");
        var flight = _state.Flights().Single();
        _state.PutLeg(L("parser") with
        {
            Check = check,
            Recheck = new LegRecheck { Check = check, Since = DateTimeOffset.UtcNow, By = asked ? "tests" : null, Report = asked ? Report : null },
        });
        if (job is not null)
        {
            _state.PutJob(job(flight));
        }

        var next = new NodeState(_journal, TimeProvider.System);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        var jobs = new JobRunner(next, options, TimeProvider.System) { FreeMemory = () => null, Say = (command, _) => Task.FromResult(next.Complete(command, null)) };
        await jobs.StartAsync(CancellationToken.None);
        await jobs.Recovered.WaitAsync(Ceiling, Ct);
        var git = new GitCli();
        var legs = new LegCommander(next, _workers, new JobCheckRunner(jobs), _github, git, new Planes(git), new FlightCommander(next, new Planes(git), TimeProvider.System), options, TimeProvider.System);
        var said = new ConcurrentQueue<CommandRequest>();
        var watch = new LegWatch(next, (command, token) =>
        {
            if (command.Verb != Verbs.Say)
            {
                return legs.RunAsync(command, token);
            }
            said.Enqueue(command);
            return Task.FromResult(next.Complete(command, null));
        }, TimeProvider.System)
        {
            Ready = jobs.Recovered,
        };
        return new Restarted(next, jobs, legs, watch, said);
    }

    private JobRow Queued(string check, FlightRow flight) => new()
    {
        Id = "jc1",
        Command = check,
        Cwd = flight.Worktree,
        Kind = JobKind.Check,
        FailFast = true,
        Flight = "TIA1",
        Leg = "parser",
        State = JobState.Queued,
        LogPath = Path.Combine(Logs, "jc1.log"),
        Timeout = TimeSpan.FromMinutes(30),
        StartedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        MemoryKey = JobKey.Of(check, flight.Worktree).ToString(),
    };

    // Where the runner keeps its job logs: under the data folder the nodes of these tests share.
    private string Logs
    {
        get
        {
            var folder = Path.Combine(_root, "data", "jobs");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static async Task UntilAsync(Func<bool> done, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            clock.Elapsed.ShouldBeLessThan(Ceiling, what);
            await Task.Delay(50, Ct);
        }
    }

    // The node's finish runs detached and its word is told by the watch's next look: looks until it has said something.
    private static async Task UntilToldAsync(Restarted node) =>
        await UntilAsync(() =>
        {
            node.Watch.LookAsync(Ct).GetAwaiter().GetResult();
            return !node.Said.IsEmpty;
        }, "the worker is told what the node did");

    [Fact(Timeout = 120_000)]
    public async Task ARerunCarriedOverTheRestartWithItsCheckJobQueuedIsFinishedDoneByTheNodeOnceThatJobPassesWithTheWorkersReport()
    {
        var check = WaitingFor(0);
        await using var node = await RestartedAsync(check, flight => Queued(check, flight));
        node.Parser.Recheck.ShouldNotBeNull().ShouldSatisfyAllConditions(
            r => r.Resume.ShouldBeTrue(),
            r => r.Outcome.ShouldBeNull(),
            r => r.By.ShouldBe("tests"),
            r => r.Report.ShouldBe(Report));

        await node.Watch.LookAsync(Ct);
        await UntilAsync(() => node.Checks.Any(j => j.State == JobState.Running), "the carried check job runs");
        node.Parser.State.ShouldBe(LegState.Flying);
        File.WriteAllText(Gate, "go");
        await UntilAsync(() => node.Parser.State == LegState.Done, "the node finishes the leg");

        node.Parser.ShouldSatisfyAllConditions(
            l => l.Report.ShouldBe(Report),
            l => l.Recheck.ShouldBeNull());
        // The one job is the carried one, which passed; no finish was run by the worker.
        var only = node.Checks.ShouldHaveSingleItem();
        (only.Id, only.State).ShouldBe(("jc1", JobState.Succeeded));
        await UntilToldAsync(node);
        var said = node.Said.ShouldHaveSingleItem();
        said.Args[0].ShouldBe(LegRecheck.NodeFinishedOutcome(check));
        said.Args[0].ShouldNotContain("`leg finish done` again");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheSameWithTheJobFailingLeavesTheLegFlyingWithTheFailedOutcomeSaidToTheWorkerOnce()
    {
        var check = Echoing("two tests failed", 1);
        await using var node = await RestartedAsync(check, flight => Queued(check, flight));

        await node.Watch.LookAsync(Ct);
        await UntilToldAsync(node);

        node.Parser.State.ShouldBe(LegState.Flying);
        node.Checks.ShouldHaveSingleItem().State.ShouldBe(JobState.Failed);
        var said = node.Said.ShouldHaveSingleItem();
        said.Args[0].ShouldContain("check `" + check + "` failed (1)");
        said.Args[0].ShouldContain("two tests failed");
        said.Args[0].ShouldContain("`leg finish done` again");
        // Said once: a later look says nothing more of it.
        await node.Watch.LookAsync(Ct);
        node.Said.Count.ShouldBe(1);
        node.Parser.Recheck.ShouldBeNull();
    }

    [Fact(Timeout = 120_000)]
    public async Task ACheckJobACrashEndedIsRerunByTheNodeAndTheLegFinished()
    {
        // What a crash leaves: the check's row still Running, its process gone, and no exit file (RestartedOutcome's case).
        var check = Echoing("fresh", 0);
        await using var node = await RestartedAsync(check, flight => Queued(check, flight) with
        {
            State = JobState.Running,
            Pid = int.MaxValue - 7,
            ProcessStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        node.Parser.Recheck.ShouldNotBeNull().Resume.ShouldBeTrue();

        await node.Watch.LookAsync(Ct);
        await UntilAsync(() => node.Parser.State == LegState.Done, "the node reruns the check and finishes the leg");

        node.Parser.Report.ShouldBe(Report);
        var checks = node.Checks;
        checks.Length.ShouldBe(2);
        checks.Single(j => j.Id == "jc1").State.ShouldBe(JobState.Failed);
        checks.Single(j => j.Id != "jc1").State.ShouldBe(JobState.Succeeded);
        await UntilToldAsync(node);
        node.Said.ShouldHaveSingleItem().Args[0].ShouldBe(LegRecheck.NodeFinishedOutcome(check));
    }

    [Fact(Timeout = 120_000)]
    public async Task AWorkersOwnFinishArrivingWhileTheNodeFinishesItIsAnsweredInProgressAndMakesNoSecondJobRow()
    {
        var check = WaitingFor(0);
        await using var node = await RestartedAsync(check, flight => Queued(check, flight));

        await node.Watch.LookAsync(Ct);
        await UntilAsync(() => node.Parser.Recheck is { Resume: false } && node.Checks.Any(j => j.State == JobState.Running), "the node's finish is rerunning the check");

        var second = await node.Legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["done", "again"], Who = "tests" }, Ct);

        second.Ok.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("rerunning");
        second.Error.ShouldContain("already");
        node.Checks.ShouldHaveSingleItem().Id.ShouldBe("jc1");
        File.WriteAllText(Gate, "go");
        await UntilAsync(() => node.Parser.State == LegState.Done, "the node finishes the leg");
        node.Checks.ShouldHaveSingleItem().State.ShouldBe(JobState.Succeeded);
        node.Parser.Report.ShouldBe(Report);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARerunWithNoOneWhoAskedItKeepsTheOldOutcomeThatTellsTheWorkerToFinishAgain()
    {
        var check = Echoing("old", 0);
        await using var node = await RestartedAsync(check, flight => Queued(check, flight), asked: false);

        var recheck = node.Parser.Recheck.ShouldNotBeNull();
        recheck.Resume.ShouldBeFalse();
        recheck.Outcome.ShouldBe(LegRecheck.CarriedOutcome(check));
        await node.Watch.LookAsync(Ct);
        node.Said.ShouldHaveSingleItem().Args[0].ShouldContain("`leg finish done` again");
        node.Parser.State.ShouldBe(LegState.Flying);
    }
}
