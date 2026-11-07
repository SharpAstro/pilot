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
        // Done is written first, then the node's word goes on the leg as its recheck's outcome, and only the watch's look tells it and
        // clears it (this test runs no loop): so wait for Done alone, and check the recheck is cleared only after that look.
        await UntilAsync(() => node.Parser.State == LegState.Done, "the node finishes the leg");

        node.Parser.Report.ShouldBe(Report);
        // The one job is the carried one, which passed; no finish was run by the worker.
        var only = node.Checks.ShouldHaveSingleItem();
        (only.Id, only.State).ShouldBe(("jc1", JobState.Succeeded));
        await UntilToldAsync(node);
        var said = node.Said.ShouldHaveSingleItem();
        said.Args[0].ShouldBe(LegRecheck.NodeFinishedOutcome(check));
        said.Args[0].ShouldNotContain("`leg finish done` again");
        node.Parser.Recheck.ShouldBeNull();
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
        // Done alone: the recheck is not cleared until a look tells the node's word, and this test runs none.
        await UntilAsync(() => node.Parser.State == LegState.Done, "the node finishes the leg");
        node.Checks.ShouldHaveSingleItem().State.ShouldBe(JobState.Succeeded);
        node.Parser.Report.ShouldBe(Report);
    }

    [Fact(Timeout = 120_000)]
    public async Task AWorkersOwnFinishTakingTheCallBeforeTheNodesFinishDoesLeavesTheNodeSilentForTheRefusalAndTheWorkerToItsOwnAnswer()
    {
        // The other order, with no timing assumed: the node's finish is launched by a look but held on Ready (as it is while the job
        // runner takes up the old jobs), so the worker's own finish takes the flight's call first, and only then is Ready completed.
        var check = WaitingFor(0);
        await using var node = await RestartedAsync(check, flight => Queued(check, flight));
        var ready = new TaskCompletionSource();
        node.Watch.Ready = ready.Task;

        await node.Watch.LookAsync(Ct);
        node.Watch.ResumesInFlight.ShouldBe(1);
        var workers = Task.Run(() => node.Legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["done", Report], Who = "tests" }, Ct), Ct);
        await UntilAsync(() => node.Parser.Recheck is { Resume: false, Outcome: null } && node.Checks.Any(j => j.State == JobState.Running), "the worker's own finish is rerunning the check");

        ready.SetResult();
        await UntilAsync(() => node.Watch.ResumesInFlight == 0, "the node's held finish ends, refused as the call is taken");
        await node.Watch.LookAsync(Ct);

        // Nothing of the refusal is told, and the worker's rerun is neither cleared nor given an outcome.
        node.Said.ShouldBeEmpty();
        node.Parser.Recheck.ShouldNotBeNull().Outcome.ShouldBeNull();
        File.WriteAllText(Gate, "go");
        var answer = await workers.WaitAsync(Ceiling, Ct);
        if (answer.Later is { } later)
        {
            answer = await later;
        }
        answer.Ok.ShouldBeTrue(answer.Error);
        await UntilAsync(() => node.Parser.State == LegState.Done, "the worker's own finish ends the leg");
        await node.Watch.LookAsync(Ct);
        node.Said.ShouldBeEmpty();
        node.Checks.ShouldHaveSingleItem().State.ShouldBe(JobState.Succeeded);
    }

    [Fact(Timeout = 120_000)]
    public async Task TheNodesWordIsKeptOnTheLegUntilTheWatchTellsItSoANodeStoppingBeforeThatLookLosesNothing()
    {
        var check = Echoing("two tests failed", 1);
        await using var node = await RestartedAsync(check, flight => Queued(check, flight));

        // Looks without telling: the watch's own loop is not run again once the node's finish has ended, as a node stopping there would not.
        await node.Watch.LookAsync(Ct);
        await UntilAsync(() => node.Watch.ResumesInFlight == 0, "the node's finish has ended");

        var kept = node.Parser.Recheck.ShouldNotBeNull();
        kept.Outcome.ShouldNotBeNull().ShouldContain("`leg finish done` again");
        node.Said.ShouldBeEmpty();

        // A node that comes up on that state (its journal) tells it at its first look.
        var next = new NodeState(_journal, TimeProvider.System);
        next.Legs("TIA1").Single(l => l.Name == "parser").Recheck.ShouldNotBeNull().Outcome.ShouldBe(kept.Outcome);
        await node.Watch.LookAsync(Ct);
        node.Said.ShouldHaveSingleItem().Args[0].ShouldBe(kept.Outcome);
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeFinishRefusedBecauseTheFlightHasCallsOutIsNotToldWhateverPhaseTheHoldersFinishIsInAndIsRunAgainOnceItIsFree()
    {
        // The refusal a worker's own finish causes before its check rerun (merging the base) or after it (pushing): the leg then still
        // carries the restart's mark, or none, so the recheck's own state cannot tell it. The refusal's words do.
        var check = Echoing("ok", 0);
        await using var node = await RestartedAsync(check, null);
        var finishes = 0;
        var busy = true;
        var told = new ConcurrentQueue<CommandRequest>();
        var watch = new LegWatch(node.State, (command, token) =>
        {
            if (command.Verb == Verbs.Say)
            {
                told.Enqueue(command);
                return Task.FromResult(node.State.Complete(command, null));
            }
            Interlocked.Increment(ref finishes);
            var error = busy ? LegCommander.BusyRefusal("TIA1", "being brought up to date", "wait for its answer, then ask again") : "the check failed";
            return Task.FromResult(node.State.Complete(command, error));
        }, TimeProvider.System)
        {
            Ready = Task.CompletedTask,
        };

        await watch.LookAsync(Ct);
        await UntilAsync(() => watch.ResumesInFlight == 0 && Volatile.Read(ref finishes) == 1, "the node's finish is refused as the flight is busy");

        told.ShouldBeEmpty();
        node.Parser.Recheck.ShouldNotBeNull().ShouldSatisfyAllConditions(
            r => r.Resume.ShouldBeTrue(),
            r => r.Outcome.ShouldBeNull());
        // Free again: the next look runs the finish, which now refuses on its merits, and that is kept to be told.
        busy = false;
        await watch.LookAsync(Ct);
        await UntilAsync(() => watch.ResumesInFlight == 0 && Volatile.Read(ref finishes) == 2, "the node's finish is run again");
        node.Parser.Recheck.ShouldNotBeNull().Outcome.ShouldNotBeNull().ShouldContain("the check failed");
    }

    [Fact(Timeout = 120_000)]
    public async Task ADoneLegsOutcomeThatIsNotTheNodesOwnFinishIsNotSaidToAnyone()
    {
        // What an accepted finish leaves when the worker's rerun then ends late: a changed-outcome on a Done leg.
        var check = Echoing("ok", 0);
        await using var node = await RestartedAsync(check, null);
        var changed = new LegRecheck { Check = check, Since = DateTimeOffset.UtcNow, Outcome = LegRecheck.ChangedOutcome(check) };
        node.State.PutLeg(node.Parser with { State = LegState.Done, Recheck = changed });

        await node.Watch.LookAsync(Ct);

        node.Said.ShouldBeEmpty();
        node.Parser.Recheck.ShouldBe(changed);
    }

    [Fact(Timeout = 120_000)]
    public async Task AGoAroundOfADoneLegDropsAnOutcomePendingOnItSoTheNewRoundsWorkerIsNotToldIt()
    {
        var check = Echoing("ok", 0);
        await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        _state.PutLeg(L("parser") with
        {
            State = LegState.Done,
            Recheck = new LegRecheck { Check = check, Since = DateTimeOffset.UtcNow, Outcome = LegRecheck.NodeFinishedOutcome(check) },
        });

        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "the", "review", "asked")).Ok.ShouldBeTrue();

        L("parser").ShouldSatisfyAllConditions(
            l => l.State.ShouldBe(LegState.Flying),
            l => l.Recheck.ShouldBeNull());
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
