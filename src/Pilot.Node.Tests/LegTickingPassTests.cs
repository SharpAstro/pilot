using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A worker that commits its leg's work and ends with steps unticked is finished by the node's ticking pass, not sent round (PIL804): the
/// leg watch starts a ticker once, the ticker ticks what the commits cover and finishes the leg, and no ring card is raised for it.
/// </summary>
public sealed class LegTickingPassTests : LegCommanderTestsBase
{
    private const string Ticker = "du1-tia1-parser-ticker";

    private readonly LegWatch _watch;
    private readonly List<CommandRequest> _said = [];

    public LegTickingPassTests()
    {
        _legs.Models = new RoleModels(null);
        // The watch as the node wires it: the commander's own HEAD reader and ticker start, its commands run by the commander (a say is only recorded).
        _watch = new LegWatch(_state, RunAsync, _clock)
        {
            Models = new RoleModels(null),
            PlaneHead = _legs.PlaneHeadOfAsync,
            StartTicker = _legs.StartTickerAsync,
        };
    }

    private Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.Verb != Verbs.Say)
        {
            return _legs.RunAsync(command, cancellationToken);
        }
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    /// <summary>The flight with its first leg flying; the worker has committed its work when <paramref name="commit"/>.</summary>
    private async Task<FlightRow> FlyingAsync(bool commit)
    {
        await FlightWithTwoLegs();
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        var flight = _state.Flights().Single();
        L("parser").StartHead.ShouldNotBeNull();
        if (commit)
        {
            File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
            Git(flight.Worktree, "add", ".");
            Commit(flight.Worktree, "The parser");
        }
        return flight;
    }

    private SessionRow Session(string id, string name, SessionState state, TimeSpan quietFor) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = _state.Flights().Single().Worktree,
        Role = SessionRole.Worker,
        Flight = "TIA1",
        Leg = name.Replace("du1-tia1-", "", StringComparison.Ordinal),
        ContextTokens = 50_000,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 0,
        LastResponseAt = _clock.GetUtcNow() - quietFor,
        StartedAt = _clock.GetUtcNow() - quietFor - TimeSpan.FromMinutes(10),
    };

    // The worker's session as it ended, long enough ago to be past the watch's idle grace.
    private void WorkerEnded() =>
        _state.PutSession(Session(L("parser").WorkerJob!, "du1-tia1-parser", SessionState.Ended, TimeSpan.FromMinutes(10)));

    private Task<CommandResult> AsTicker(string verb, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = "flight/TIA1/parser", Args = args, Who = Ticker }, Ct);

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }

    private async Task LookUntilTickerAsync()
    {
        await _watch.LookAsync(Ct);
        await Until(() => L("parser").TickerJob is not null);
    }

    [Fact]
    public async Task An_ended_worker_with_commits_and_nothing_ticked_gets_a_ticker_that_finishes_the_leg_with_no_ring()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();

        await LookUntilTickerAsync();

        var started = _workers.Started.Single(w => w.Name == Ticker);
        (started.Reason, started.Effort).ShouldBe((SessionStartReasons.LegTicker, "low"));
        started.Brief.ShouldContain("ticker of leg `parser`");
        started.Brief.ShouldContain("The parser");
        L("parser").TickerJob.ShouldBe("job00002");
        L("parser").State.ShouldBe(LegState.Flying);
        _said.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);

        // Looks while its ticker works start no second one and ring for nothing.
        _state.PutSession(Session("job00002", Ticker, SessionState.Working, TimeSpan.Zero));
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);
        _workers.Started.Count(w => w.Name == Ticker).ShouldBe(1);

        // The ticker ticks each step its commits cover and finishes the leg: accepted like the worker's own finish, the check rerun.
        (await AsTicker(Verbs.Check, "1")).Ok.ShouldBeTrue();
        (await AsTicker(Verbs.Check, "2")).Ok.ShouldBeTrue();
        var finished = await AsTicker(Verbs.FinishLeg, "done", "ticked by the ticking pass: The parser covers both steps");

        finished.Ok.ShouldBeTrue(finished.Error);
        L("parser").State.ShouldBe(LegState.Done);
        _checks.Runs.ShouldNotBeEmpty();
        // No ring was ever raised about the leg, and its ticker-started line closed with it.
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.TickerStartedKind);
    }

    [Fact]
    public async Task An_ended_worker_with_no_new_commits_still_rings_worker_idle_after_its_nudges()
    {
        await FlyingAsync(commit: false);
        WorkerEnded();

        // Two nudges, then the ring, as before the ticking pass.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await _watch.LookAsync(Ct);
        }

        _said.Count.ShouldBe(2);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegWatch.Idle);
        card.Bin.ShouldBe(CardBin.Ring);
        L("parser").TickerJob.ShouldBeNull();
        _workers.Started.ShouldNotContain(w => w.Name == Ticker);
    }

    [Fact]
    public async Task A_worker_idle_past_its_nudges_with_commits_made_gets_a_ticker_instead_of_the_ring()
    {
        await FlyingAsync(commit: true);
        // Idle, not ended: the worker is still there, so the route is the one past MaxNudges, not the ended one.
        _state.PutSession(Session(L("parser").WorkerJob!, "du1-tia1-parser", SessionState.Idle, TimeSpan.FromMinutes(10)));

        // Two nudges, then the ticker where the ring would have been.
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        L("parser").TickerJob.ShouldBeNull();
        _workers.Started.ShouldNotContain(w => w.Name == Ticker);

        await LookUntilTickerAsync();

        _workers.Started.Count(w => w.Name == Ticker).ShouldBe(1);
        _said.Count.ShouldBe(2);
        L("parser").State.ShouldBe(LegState.Flying);
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Idle);
    }

    [Fact]
    public async Task A_go_around_while_the_ticker_runs_stops_it_and_clears_the_ticker_job()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        var worker = L("parser").WorkerJob!;
        var ticker = L("parser").TickerJob.ShouldNotBeNull();
        _state.PutSession(Session(ticker, Ticker, SessionState.Working, TimeSpan.Zero));

        var around = await Leg(Verbs.GoAround, "flight/TIA1/parser", "the commits do not cover the steps");

        around.Ok.ShouldBeTrue(around.Error);
        _workers.Stopped.ShouldContain(ticker);
        _workers.Stopped.ShouldContain(worker);
        await Until(() => L("parser").WorkerJob != worker);
        L("parser").TickerJob.ShouldBeNull();
        L("parser").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    public async Task A_go_around_whose_start_fails_still_clears_the_ticker_job_it_stopped()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        var ticker = L("parser").TickerJob.ShouldNotBeNull();
        _state.PutSession(Session(ticker, Ticker, SessionState.Working, TimeSpan.Zero));

        _workers.Fail = new InvalidOperationException("claude refused to start");
        await Leg(Verbs.GoAround, "flight/TIA1/parser", "the commits do not cover the steps");

        _workers.Stopped.ShouldContain(ticker);
        L("parser").TickerJob.ShouldBeNull();
    }

    [Fact]
    public async Task RestartWorker_stops_the_ticker_and_clears_the_ticker_job()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        var worker = L("parser").WorkerJob!;
        var ticker = L("parser").TickerJob.ShouldNotBeNull();

        await _legs.RestartWorkerAsync("TIA1", "parser", Ct);

        _workers.Stopped.ShouldContain(ticker);
        _workers.Stopped.ShouldContain(worker);
        await Until(() => L("parser").WorkerJob != worker);
        L("parser").TickerJob.ShouldBeNull();
        L("parser").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    public async Task RestartWorker_whose_start_fails_still_clears_the_ticker_job_it_stopped()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        var ticker = L("parser").TickerJob.ShouldNotBeNull();

        _workers.Fail = new InvalidOperationException("claude refused to start");
        await _legs.RestartWorkerAsync("TIA1", "parser", Ct);

        _workers.Stopped.ShouldContain(ticker);
        L("parser").TickerJob.ShouldBeNull();
    }

    [Fact]
    public async Task A_ticker_whose_start_fails_leaves_one_ring_not_two()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        // Left failing throughout: the ticker is asked for once and never again, so the start is never retried.
        _workers.Fail = new InvalidOperationException("claude refused to start");
        var log = new StringWriter();
        _legs.Error = TextWriter.Synchronized(log);

        await _watch.LookAsync(Ct);
        // The failed start says its cause to the node's log (nothing else hears it), which is also what the test waits for.
        await Until(() => log.ToString().Contains("claude refused to start", StringComparison.Ordinal));
        log.ToString().ShouldContain("the ticker du1-tia1-parser-ticker of TIA1/parser did not start");

        // Past the show-up grace the ordinary ladder takes the leg back: two nudges, then its one worker-idle ring. The watch's start
        // has no command waiting on it, so no start-failed card is raised on top of it.
        foreach (var _ in Enumerable.Range(0, 5))
        {
            _clock.Advance(TimeSpan.FromMinutes(10));
            await _watch.LookAsync(Ct);
        }

        var rings = _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring).ToList();
        rings.Single().Kind.ShouldBe(LegWatch.Idle);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.StartFailedKind);
        _said.Count.ShouldBe(2);
        L("parser").TickerJob.ShouldBeNull();
        _workers.Started.ShouldNotContain(w => w.Name == Ticker);
    }

    [Fact]
    public async Task A_ticker_that_finishes_failed_leaves_the_leg_failed_ring()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        (await AsTicker(Verbs.Check, "1")).Ok.ShouldBeTrue();

        var failed = await AsTicker(Verbs.FinishLeg, "failed", "step 2 is not covered by any commit");

        failed.Ok.ShouldBeTrue(failed.Error);
        L("parser").State.ShouldBe(LegState.Failed);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed");
        (card.Bin, card.Title).ShouldBe((CardBin.Ring, "TIA1/parser stopped: step 2 is not covered by any commit"));
    }

    /// <summary>The ticker of the leg ended with step 1 ticked and step 2 not, and no finish.</summary>
    private async Task TickerEndedWithStepTwoUntickedAsync()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        (await AsTicker(Verbs.Check, "1")).Ok.ShouldBeTrue();
        _state.PutSession(Session(L("parser").TickerJob!, Ticker, SessionState.Ended, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task A_ticker_that_ended_with_step_two_unticked_sends_the_leg_round_naming_the_step_and_rings_nothing()
    {
        await TickerEndedWithStepTwoUntickedAsync();
        var worker = L("parser").WorkerJob!;

        await _watch.LookAsync(Ct);

        await Until(() => L("parser").WorkerJob != worker);
        L("parser").State.ShouldBe(LegState.Flying);
        L("parser").GoArounds.ShouldBe(1);
        var why = L("parser").GoAroundReasons.Single().Why;
        why.ShouldContain("step 2: Test it");
        why.ShouldNotContain("Write the parser");
        why.ShouldContain("commits did not cover");
        // A fresh worker was started, and its brief carries the reason.
        _workers.Started.Last().Brief.ShouldContain("step 2: Test it");
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Idle);
        _state.Cards().ShouldContain(c => c.Kind == LegWatch.TickerSentRound && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task A_second_ticker_stop_on_the_same_leg_after_its_go_around_rings_worker_idle_once()
    {
        await TickerEndedWithStepTwoUntickedAsync();
        var worker = L("parser").WorkerJob!;
        await _watch.LookAsync(Ct);
        await Until(() => L("parser").WorkerJob != worker);

        // The fresh worker commits more and ends; its ticker runs and stops with a step unticked again.
        var flight = _state.Flights().Single();
        File.WriteAllText(Path.Combine(flight.Worktree, "parser2.cs"), "class Parser2 {}\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "More parser");
        WorkerEnded();
        await LookUntilTickerAsync();
        var second = L("parser").TickerJob.ShouldNotBeNull();
        _state.PutSession(Session(second, Ticker, SessionState.Ended, TimeSpan.FromMinutes(5)));

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);

        L("parser").GoArounds.ShouldBe(1);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegWatch.Idle);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("ticker stopped without finishing the leg: its session ended");
        _state.Cards(openOnly: true).Count(c => c.Bin == CardBin.Ring).ShouldBe(1);
    }

    [Fact]
    public async Task A_ticker_that_ended_with_steps_unticked_rings_naming_the_refusal_when_the_go_around_is_refused()
    {
        await TickerEndedWithStepTwoUntickedAsync();
        // A held flight refuses a go-around.
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Held });

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        L("parser").GoArounds.ShouldBe(0);
        var card = _state.Cards(openOnly: true).Single(c => c.Bin == CardBin.Ring);
        card.Kind.ShouldBe(LegWatch.Idle);
        card.Title.ShouldContain("go-around refused");
        _workers.Started.Count(w => w.Name == Ticker).ShouldBe(1);
    }

    /// <summary>The leg in its ticking pass with every step ticked by its ticker, whose session then ended with no finish.</summary>
    private async Task TickerEndedWithAllTickedAsync()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        (await AsTicker(Verbs.Check, "1")).Ok.ShouldBeTrue();
        (await AsTicker(Verbs.Check, "2")).Ok.ShouldBeTrue();
        _state.PutSession(Session(L("parser").TickerJob!, Ticker, SessionState.Ended, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task A_ticker_that_ended_with_every_step_ticked_and_no_finish_gets_the_leg_finished_done_by_the_node()
    {
        await TickerEndedWithAllTickedAsync();

        await _watch.LookAsync(Ct);

        // The node's finish runs off the watch loop, so the leg is done a moment after the look.
        await Until(() => L("parser").State == LegState.Done);
        L("parser").Report.ShouldNotBeNull().ShouldContain("finished by the node: its ticker stopped with every step ticked");
        _checks.Runs.ShouldNotBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Idle);
    }

    [Fact]
    public async Task A_ticker_that_ended_with_every_step_ticked_whose_finish_the_node_has_refused_rings_once_naming_the_refusal()
    {
        await TickerEndedWithAllTickedAsync();
        _checks.Next = new CheckRun(1, "Parser.Tests: 3 failed");

        await _watch.LookAsync(Ct);
        await Until(() => _state.Cards(openOnly: true).Any(c => c.Bin == CardBin.Ring));
        await _watch.LookAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);

        L("parser").State.ShouldBe(LegState.Flying);
        var card = _state.Cards(openOnly: true).Single(c => c.Bin == CardBin.Ring);
        card.Kind.ShouldBe(LegWatch.Idle);
        card.Title.ShouldContain("ticker stopped with every step ticked, and the node's finish was refused");
        card.Title.ShouldContain("check");
        card.Detail.ShouldNotBeNull().ShouldContain("Parser.Tests: 3 failed");
        // The node asks for the finish once per ticker.
        _checks.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_ticker_that_ran_finish_and_ended_while_its_check_rerun_is_still_running_is_not_rung_and_the_rerun_finishes_the_leg()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        (await AsTicker(Verbs.Check, "1")).Ok.ShouldBeTrue();
        (await AsTicker(Verbs.Check, "2")).Ok.ShouldBeTrue();
        var gate = new TaskCompletionSource();
        _checks.OnRun = () => gate.Task;
        var finishing = AsTicker(Verbs.FinishLeg, "done", "ticked by the ticking pass: The parser covers both steps");
        await Until(() => L("parser").Recheck is { Outcome: null });

        // The ticker was told its check is rerunning and ended its turn, as told: its session ended, long past the idle grace.
        _state.PutSession(Session(L("parser").TickerJob!, Ticker, SessionState.Ended, TimeSpan.FromMinutes(5)));
        await _watch.LookAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);

        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _workers.Stopped.ShouldNotContain(L("parser").TickerJob!);
        // The node's finish is not asked for a second time while the first one's rerun runs.
        _checks.Runs.Count.ShouldBe(1);

        gate.SetResult();
        (await finishing).Ok.ShouldBeTrue();
        L("parser").State.ShouldBe(LegState.Done);
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
    }

    [Fact]
    public async Task The_nodes_finish_of_a_stopped_ticker_runs_off_the_watch_loop_so_a_slow_check_does_not_hold_the_look()
    {
        await TickerEndedWithAllTickedAsync();
        var gate = new TaskCompletionSource();
        _checks.OnRun = () => gate.Task;

        // The look returns while the finish's check is still running.
        await _watch.LookAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Until(() => _checks.Runs.Count == 1);
        L("parser").State.ShouldBe(LegState.Flying);

        gate.SetResult();
        await Until(() => L("parser").State == LegState.Done);
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
    }

    [Fact]
    public async Task A_ticker_told_how_a_failed_rerun_ended_in_this_look_is_given_its_turn_not_finished_a_second_time()
    {
        await TickerEndedWithAllTickedAsync();
        // The ticker's finish was answered, its check rerun ended failed, and the outcome is for the ticker: the look says it, and the
        // ticker's session still shows the turn it ended before it was told.
        _state.PutLeg(L("parser") with { Recheck = new LegRecheck { Check = "dotnet test", Since = _clock.GetUtcNow(), Outcome = "the check failed: Parser.Tests: 3 failed" } });

        await _watch.LookAsync(Ct);

        _said.ShouldContain(c => c.Args.Any(a => a.Contains("Parser.Tests: 3 failed", StringComparison.Ordinal)));
        // No finish by the node on top of what the ticker was just told, and no ring.
        await Task.Delay(200, Ct);
        _checks.Runs.ShouldBeEmpty();
        L("parser").State.ShouldBe(LegState.Flying);
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _workers.Stopped.ShouldNotContain(L("parser").TickerJob!);

        // Past the idle grace with no turn of its own, it is stopped and finished as any other.
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);
        await Until(() => _checks.Runs.Count == 1);
    }

    [Fact]
    public async Task A_node_that_restarted_after_a_ticker_sent_the_leg_round_does_not_send_it_round_a_second_time()
    {
        await TickerEndedWithStepTwoUntickedAsync();
        var worker = L("parser").WorkerJob!;
        await _watch.LookAsync(Ct);
        await Until(() => L("parser").WorkerJob != worker);

        var flight = _state.Flights().Single();
        File.WriteAllText(Path.Combine(flight.Worktree, "parser2.cs"), "class Parser2 {}\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "More parser");
        WorkerEnded();
        await LookUntilTickerAsync();
        _state.PutSession(Session(L("parser").TickerJob!, Ticker, SessionState.Ended, TimeSpan.FromMinutes(5)));

        // A fresh watch over the same state, as after a restart: nothing of the first one's memory is left.
        var restarted = new LegWatch(_state, RunAsync, _clock)
        {
            Models = new RoleModels(null),
            PlaneHead = _legs.PlaneHeadOfAsync,
            StartTicker = _legs.StartTickerAsync,
        };
        await restarted.LookAsync(Ct);

        L("parser").GoArounds.ShouldBe(1);
        _state.Cards(openOnly: true).Single(c => c.Bin == CardBin.Ring).Kind.ShouldBe(LegWatch.Idle);
    }
}
