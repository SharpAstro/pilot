using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// No stall card is raised, and no nudge sent, while the session that would be stalled waits on a job of its own that is running or queued,
/// or on its leg's check rerun (pilot #1229, Decision 2: 381 watchtower dismissals said so). One kind of LegWatch's stall paths per test,
/// each with a running job, a queued job, an open check rerun where the kind has a leg, and after the job ended, when it is raised as before.
/// Fakes only: an in-memory node and a manual clock.
/// </summary>
public sealed class StallWhileJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly List<CommandRequest> _said = [];
    private readonly List<string> _stopped = [];
    private readonly LegWatch _watch;

    public StallWhileJobTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _watch = new LegWatch(_state, Run, _time) { Pilot = "pilot --node-socket \"/tmp/n.sock\"" };
        _watch.StopSession = (id, _) => { _stopped.Add(id); return Task.CompletedTask; };
        _state.PutLeg(new LegRow
        {
            Flight = "TIA1", Name = "parser", Order = 1, Goal = "Parse", DoneWhen = "Tests pass",
            Checklist = [new CheckItem { Text = "Write it", Done = true }, new CheckItem { Text = "Test it" }],
            State = LegState.Flying, Worker = "du1-tia1-parser", WorkerJob = "a1b2c3d4", StartedAt = Start,
        });
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private void Session(string id, string name, SessionRole role, SessionState state, string? flight = "TIA1", string? leg = "parser",
        long context = 50_000, bool turned = true, string? waitingFor = null, string? apiError = null, bool gone = false) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/r/w", Role = role,
            Flight = flight, Leg = leg, ContextTokens = turned ? context : 0, ContextWindow = 200_000, State = state, WaitingFor = waitingFor,
            ApiError = apiError, Gone = gone, SpendWeighted = 0, LastResponseAt = turned ? Start : null, StartedAt = Start,
        });

    private void Worker(SessionState state, string? waitingFor = null, string? apiError = null, bool turned = true) =>
        Session("a1b2c3d4", "du1-tia1-parser", SessionRole.Worker, state, waitingFor: waitingFor, apiError: apiError, turned: turned);

    private void Job(JobState state, string owner = "a1b2c3d4", string? ownerName = "du1-tia1-parser", string id = "j1", bool check = false, bool ofLeg = false) =>
        _state.PutJob(new JobRow
        {
            Id = id, Command = "dotnet test", Cwd = "/r/w", State = state, Owner = check ? null : owner, OwnerName = check ? null : ownerName,
            Kind = check ? JobKind.Check : JobKind.Run, Flight = check || ofLeg ? "TIA1" : null, Leg = check || ofLeg ? "parser" : null,
            LogPath = "/tmp/" + id + ".log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start,
            EndedAt = state is JobState.Running or JobState.Queued ? null : _time.GetUtcNow(),
            Told = state is not (JobState.Running or JobState.Queued), ToldAt = state is JobState.Running or JobState.Queued ? null : _time.GetUtcNow(),
        });

    private void Recheck() =>
        _state.PutLeg(_state.Legs("TIA1").Single() with { Recheck = new LegRecheck { Check = "dotnet test", Since = Start } });

    private async Task LookEvery(TimeSpan step, int times)
    {
        for (var i = 0; i < times; i++)
        {
            _time.Advance(step);
            await _watch.LookAsync(Ct);
        }
    }

    private void EndJob(string id = "j1")
    {
        var job = _state.Jobs().Single(j => j.Id == id);
        _state.PutJob(job with { State = JobState.Succeeded, EndedAt = _time.GetUtcNow(), Told = true, ToldAt = _time.GetUtcNow() });
    }

    private void ShouldBeQuiet()
    {
        _said.ShouldBeEmpty();
        _stopped.ShouldBeEmpty();
        Open().ShouldBeEmpty();
    }

    // ---- LegWatch.Idle: the nudges, "answers its nudges but stays idle", "ended with the leg unfinished" ----

    [Theory]
    [InlineData(JobState.Running, false)]
    [InlineData(JobState.Queued, false)]
    [InlineData(JobState.Running, true)]
    [InlineData(JobState.Queued, true)]
    public async Task An_idle_or_ended_worker_with_a_job_of_its_own_open_is_neither_nudged_nor_rung(JobState job, bool ended)
    {
        Worker(ended ? SessionState.Ended : SessionState.Idle);
        Job(job);

        await LookEvery(TimeSpan.FromMinutes(5), 8);

        ShouldBeQuiet();
    }

    [Fact]
    public async Task An_idle_worker_with_its_leg_check_rerun_open_is_neither_nudged_nor_rung()
    {
        Worker(SessionState.Idle);
        Recheck();

        await LookEvery(TimeSpan.FromMinutes(5), 8);

        ShouldBeQuiet();
    }

    [Fact]
    public async Task An_idle_worker_is_nudged_then_rung_as_before_once_its_job_has_ended_and_the_wait_passes()
    {
        Worker(SessionState.Idle);
        Job(JobState.Running);
        await LookEvery(TimeSpan.FromMinutes(5), 4);
        ShouldBeQuiet();

        EndJob();
        // Its grace counts from the telling of the job's end, so the first look after it says nothing.
        await LookEvery(TimeSpan.FromMinutes(5), 1);
        await LookEvery(TimeSpan.FromMinutes(5), 4);

        _said.Count(c => c.Verb == Verbs.Say).ShouldBe(2);
        Open().ShouldContain(c => c.Kind == LegWatch.Idle && c.Bin == CardBin.Ring);
    }

    [Fact]
    public async Task A_worker_that_answered_its_nudges_and_idles_again_is_still_rung_as_before_with_no_job_open()
    {
        Worker(SessionState.Idle);
        await LookEvery(TimeSpan.FromMinutes(5), 6);

        Open().ShouldContain(c => c.Kind == LegWatch.Idle);
    }

    // ---- Waiting ----

    [Theory]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Queued)]
    public async Task A_waiting_worker_with_a_job_of_its_own_open_raises_no_waiting_card(JobState job)
    {
        Worker(SessionState.Waiting, waitingFor: "the job to end");
        Job(job);

        await LookEvery(TimeSpan.FromMinutes(5), 4);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_waiting_worker_with_its_check_rerun_open_raises_no_waiting_card()
    {
        Worker(SessionState.Waiting, waitingFor: "the check");
        Recheck();

        await LookEvery(TimeSpan.FromMinutes(5), 4);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
    }

    [Fact]
    public async Task A_waiting_worker_rings_as_before_once_its_job_has_ended()
    {
        Worker(SessionState.Waiting, waitingFor: "a decision");
        Job(JobState.Running);
        await LookEvery(TimeSpan.FromMinutes(5), 2);
        Open().ShouldNotContain(c => c.Kind == LegWatch.Waiting);

        EndJob();
        await LookEvery(TimeSpan.FromMinutes(5), 1);

        Open().ShouldContain(c => c.Kind == LegWatch.Waiting && c.Bin == CardBin.Ring);
    }

    // ---- Stopped ----

    [Theory]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Queued)]
    public async Task A_worker_stopped_by_the_API_with_a_job_of_its_own_open_raises_no_stopped_card(JobState job)
    {
        Worker(SessionState.Idle, apiError: "overloaded");
        Job(job);

        await LookEvery(TimeSpan.FromMinutes(5), 4);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Stopped);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_worker_stopped_by_the_API_with_its_check_rerun_open_raises_no_stopped_card()
    {
        Worker(SessionState.Idle, apiError: "overloaded");
        Recheck();

        await LookEvery(TimeSpan.FromMinutes(5), 2);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Stopped);
    }

    [Fact]
    public async Task A_stopped_worker_rings_as_before_once_its_job_has_ended()
    {
        Worker(SessionState.Idle, apiError: "overloaded");
        Job(JobState.Running);
        await LookEvery(TimeSpan.FromMinutes(5), 2);
        EndJob();

        await LookEvery(TimeSpan.FromMinutes(5), 1);

        Open().ShouldContain(c => c.Kind == LegWatch.Stopped && c.Bin == CardBin.Ring);
    }

    // ---- NeverTurned ----

    [Theory]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Queued)]
    public async Task A_worker_that_owns_a_job_by_its_row_is_not_restarted_or_rung_as_never_turned(JobState job)
    {
        HookWorkers();
        Worker(SessionState.Working, turned: false);
        Job(job);

        await LookEvery(TimeSpan.FromMinutes(5), 4);

        Open().ShouldNotContain(c => c.Kind == LegWatch.NeverTurned);
        _stopped.ShouldBeEmpty();
        _restarted.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_worker_that_never_turned_is_restarted_as_before_when_only_a_replaced_sessions_job_shares_its_name()
    {
        HookWorkers();
        Worker(SessionState.Working, turned: false);
        // A job of the replaced session, found by name alone, must not keep a silent replacement alive.
        Job(JobState.Running, owner: "c0000000");

        await LookEvery(TimeSpan.FromMinutes(2), 2);

        _restarted.ShouldBe(["TIA1/parser"]);
    }

    [Fact]
    public async Task A_worker_that_never_turned_with_no_job_is_restarted_as_before()
    {
        HookWorkers();
        Worker(SessionState.Working, turned: false);

        await LookEvery(TimeSpan.FromMinutes(2), 2);

        _restarted.ShouldBe(["TIA1/parser"]);
    }

    private readonly List<string> _restarted = [];

    private void HookWorkers() =>
        _watch.RestartWorker = (flight, leg, _) =>
        {
            _restarted.Add($"{flight}/{leg}");
            _state.RemoveSession("a1b2c3d4");
            return Task.CompletedTask;
        };

    // ---- The ticker path ----

    private void FlyWithTicker(SessionState ticker, bool gone = false)
    {
        _state.PutLeg(_state.Legs("TIA1").Single() with { TickerJob = "t1t2t3t4" });
        Worker(SessionState.Ended);
        Session("t1t2t3t4", "du1-tia1-parser-ticker", SessionRole.Worker, ticker, gone: gone);
    }

    [Theory]
    [InlineData(JobState.Running, SessionState.Idle, false)]
    [InlineData(JobState.Queued, SessionState.Idle, false)]
    [InlineData(JobState.Running, SessionState.Ended, true)]
    [InlineData(JobState.Queued, SessionState.Ended, false)]
    public async Task A_ticker_with_a_job_of_its_own_open_is_not_rung_as_stuck(JobState job, SessionState ticker, bool gone)
    {
        FlyWithTicker(ticker, gone);
        Job(job, owner: "t1t2t3t4", ownerName: "du1-tia1-parser-ticker");

        await LookEvery(TimeSpan.FromMinutes(5), 6);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Idle);
        _said.ShouldBeEmpty();
        _stopped.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_ticker_with_the_leg_check_rerun_open_is_not_rung_as_stuck()
    {
        FlyWithTicker(SessionState.Ended);
        Recheck();

        await LookEvery(TimeSpan.FromMinutes(5), 6);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Idle);
    }

    [Fact]
    public async Task A_ticker_with_a_job_of_its_leg_open_is_not_rung_as_stuck()
    {
        FlyWithTicker(SessionState.Ended);
        Job(JobState.Running, owner: "c0000000", ownerName: "someone else", ofLeg: true);

        await LookEvery(TimeSpan.FromMinutes(5), 6);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Idle);
    }

    [Fact]
    public async Task A_stopped_ticker_is_acted_on_as_before_once_its_job_has_ended()
    {
        FlyWithTicker(SessionState.Ended);
        Job(JobState.Running, owner: "t1t2t3t4", ownerName: "du1-tia1-parser-ticker");
        await LookEvery(TimeSpan.FromMinutes(5), 2);
        Open().ShouldNotContain(c => c.Kind == LegWatch.Idle);

        EndJob();
        await LookEvery(TimeSpan.FromMinutes(5), 1);

        // Steps are unticked, so the leg is sent round once (a Handled line) rather than rung; either way the stop is acted on.
        _said.ShouldContain(c => c.Verb == Verbs.GoAround);
    }

    // ---- LegStale: an age line in the Handled bin, never a stall ring or a nudge ----

    [Fact]
    public async Task A_leg_flying_past_the_stale_limit_with_a_job_running_raises_only_the_one_handled_age_line()
    {
        Worker(SessionState.Working);
        Job(JobState.Running);

        await LookEvery(StaleLegs.MostFlying + TimeSpan.FromMinutes(1), 1);
        await LookEvery(TimeSpan.FromMinutes(5), 2);

        Open().ShouldBeEmpty();
        _said.ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == LegWatch.LegStale).ShouldBe(1);
        _state.Cards().Single(c => c.Kind == LegWatch.LegStale).Bin.ShouldBe(CardBin.Handled);
    }

    // ---- ReviewStalled ----

    private void ReviewingFlight()
    {
        _state.PutLeg(_state.Legs("TIA1").Single() with { State = LegState.Done });
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/w", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start,
            Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "du1-tia1-review", ReviewerJob = "a1b2c3d4", StartedAt = Start },
        });
        Session("a1b2c3d4", "du1-tia1-review", SessionRole.Reviewer, SessionState.Idle);
    }

    [Theory]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Queued)]
    public async Task A_reviewer_with_a_job_of_its_own_open_is_neither_told_restarted_nor_rung(JobState job)
    {
        ReviewingFlight();
        Job(job, ownerName: "du1-tia1-review");

        await LookEvery(TimeSpan.FromMinutes(5), 8);

        _said.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegWatch.ReviewStalled);
    }

    [Fact]
    public async Task A_reviewer_is_told_as_before_once_its_job_has_ended_and_the_wait_passes()
    {
        ReviewingFlight();
        Job(JobState.Running, ownerName: "du1-tia1-review");
        await LookEvery(TimeSpan.FromMinutes(5), 4);
        _said.ShouldBeEmpty();

        EndJob();
        await LookEvery(TimeSpan.FromMinutes(5), 1);
        await LookEvery(TimeSpan.FromMinutes(5), 1);

        _said.ShouldContain(c => c.Verb == Verbs.Say);
    }

    // ---- PlanStalled ----

    private void Unplanned()
    {
        _state.RemoveLeg("TIA1", "parser");
        _state.PutFlight(new FlightRow
        {
            Code = "PIL9", Repo = "pilot", RepoPath = "/r", Issue = 9, Branch = "flight/pil9", Worktree = "/r/pil9", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start,
        });
    }

    [Theory]
    [InlineData(JobState.Running, SessionState.Idle, false)]
    [InlineData(JobState.Queued, SessionState.Idle, false)]
    [InlineData(JobState.Running, SessionState.Ended, true)]
    [InlineData(JobState.Queued, SessionState.Idle, true)]
    public async Task A_planner_with_a_job_of_its_own_open_is_neither_told_replaced_nor_rung(JobState job, SessionState planner, bool gone)
    {
        Unplanned();
        Session("e5f6a7b8", "du1-pil9-plan", SessionRole.Planner, planner, flight: "PIL9", leg: null, gone: gone);
        Job(job, owner: "e5f6a7b8", ownerName: "du1-pil9-plan");

        await LookEvery(TimeSpan.FromMinutes(5), 6);

        _said.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegWatch.PlanStalled);
    }

    [Fact]
    public async Task An_idle_planner_is_told_as_before_once_its_job_has_ended()
    {
        Unplanned();
        Session("e5f6a7b8", "du1-pil9-plan", SessionRole.Planner, SessionState.Idle, flight: "PIL9", leg: null);
        Job(JobState.Running, owner: "e5f6a7b8", ownerName: "du1-pil9-plan");
        await LookEvery(TimeSpan.FromMinutes(5), 3);
        _said.ShouldBeEmpty();

        EndJob();
        await LookEvery(TimeSpan.FromMinutes(5), 2);

        _said.ShouldContain(c => c.Verb == Verbs.Say && c.Target == "session/e5f6a7b8");
    }
}
