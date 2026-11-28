using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A told job whose owner sits idle without acting is told again once, then rings (#756).</summary>
public sealed class JobRetellTests : IDisposable
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly TimeSpan After = TimeSpan.FromMinutes(4);
    private readonly string _root = Directory.CreateTempSubdirectory("retell-").FullName;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
    private readonly NodeState _state;
    private readonly JobRunner _jobs;
    private readonly TurnOutage _outage;
    private readonly List<string> _said = [];
    private bool _endedForGood;
    private int _forGoodCalls;
    private int _afterTurnCalls;

    public JobRetellTests()
    {
        _state = new NodeState(new InMemoryJournal(), _clock);
        _outage = new TurnOutage(_state);
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), _clock)
        {
            RetellAfter = After,
            Outage = _outage,
            Say = (command, _) =>
            {
                _said.Add(command.Args[0]);
                return Task.FromResult(_state.Complete(command, null, way: SayWays.Relayed));
            },
            EndedForGood = _ => { _forGoodCalls++; return _endedForGood; },
            EndedAfterTurn = _ => { _afterTurnCalls++; return false; },
            LastCommandAt = name => name == _name ? _commandAt : null,
        };
        _state.PutFlight(new FlightRow
        {
            Code = "RTL1", Repo = "r", RepoPath = "/r", Issue = 1, Branch = "flight/rtl1", Worktree = "/r/w", Base = "main", State = FlightState.Flying, CreatedAt = _clock.GetUtcNow(),
        });
        SetLeg(LegState.Flying);
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
    }

    private void SetLeg(LegState legState) =>
        _state.PutLeg(new LegRow { Flight = "RTL1", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = legState });

    public void Dispose()
    {
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            TestDirs.DeleteTree(_root);
        }
        catch (IOException)
        {
        }
    }

    private bool _adHoc;
    private SessionRole? _role;
    private string _name = "de1-retell-w1";
    private int _context;
    private DateTimeOffset? _commandAt;

    private string? _apiError;
    private DateTimeOffset? _apiResets;
    private string? _account;

    private void Session(SessionState sessionState, DateTimeOffset? lastResponse) =>
        _state.PutSession(new SessionRow
        {
            Id = "w0000001", SessionId = "w0000001-0000-4000-8000-000000000000", Name = _name, Kind = SessionKind.Background, Cwd = _root,
            Role = _role ?? (_adHoc ? SessionRole.AdHoc : SessionRole.Worker), Flight = _adHoc || _role is not null ? null : "RTL1", Leg = _adHoc || _role is not null ? null : "one", ContextTokens = _context, ContextWindow = 200_000, SpendWeighted = 0, State = sessionState,
            LastResponseAt = lastResponse, StartedAt = _clock.GetUtcNow().AddHours(-2), ApiError = _apiError, ApiErrorResetsAt = _apiResets, Account = _account,
        });

    private async Task<JobRow> TellNowAsync(bool whileWorking = false)
    {
        var now = _clock.GetUtcNow();
        var row = new JobRow
        {
            Id = "j1", Command = "echo hi", Cwd = _root, Owner = "w0000001", State = JobState.Succeeded, ExitCode = 0,
            LogPath = Path.Combine(_root, "j1.log"), Timeout = TimeSpan.FromMinutes(1), StartedAt = now.AddMinutes(-2), EndedAt = now.AddMinutes(-1),
        };
        _state.PutJob(row);
        if (whileWorking)
        {
            Session(SessionState.Working, now.AddMinutes(-10));
        }
        await _jobs.TellAsync(row, TestContext.Current.CancellationToken);
        _said.Clear();
        return _state.Jobs().Single();
    }

    private Task<bool> Sweep() => _jobs.RetellSilentAsync(TestContext.Current.CancellationToken);

    private void StopOnLimit(string error, TimeSpan resetsIn)
    {
        _apiError = error;
        _apiResets = _clock.GetUtcNow() + resetsIn;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
    }

    [Fact]
    public async Task AnOwnerStoppedOnAUsageLimitIsNotRetoldOrRungForBeforeTheResetAndIsRetoldRetellAfterTheReset()
    {
        await TellNowAsync();
        StopOnLimit("usage_limit_reached", TimeSpan.FromHours(1));

        // No outage is open, as right after a restart.
        _outage.IsOpen.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromMinutes(59));
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromMinutes(1) + After - TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ARetellSpentIntoALimitIsVoidAndRetoldOnceAfterTheResetWithNoCard()
    {
        await TellNowAsync();
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.Clear();
        _state.Jobs().Single().RetoldAt.ShouldNotBeNull();

        StopOnLimit("usage_limit_reached", TimeSpan.FromMinutes(30));
        _clock.Advance(TimeSpan.FromMinutes(30) + After);
        // The limit has reset, the retell fell before it: no card, the ladder starts again.
        await Sweep();
        _state.Cards().ShouldBeEmpty();
        var voided = _state.Jobs().Single();
        voided.RetoldAt.ShouldBeNull();
        voided.RetellRung.ShouldBeNull();
        _said.ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Sweep();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _state.Jobs().Single().RetoldAt.ShouldNotBeNull();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ALimitLiftedAfterTheRowBeganDoesNotHoldTheRetellOrMoveTheClock()
    {
        await TellNowAsync();
        StopOnLimit("usage_limit_reached", TimeSpan.FromHours(5));
        // A login change or a lift after the row began: its limit no longer counts, the retell goes RetellAfter after the tell.
        _state.PutLoginChanged(_clock.GetUtcNow());

        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
    }

    [Fact]
    public async Task ALimitEndedByEvidenceDoesNotHoldTheRetellOfARowThatBeganBeforeTheEnd()
    {
        StopOnLimit("usage_limit_reached", TimeSpan.FromHours(5));
        var seen = _clock.GetUtcNow();
        _outage.OpenLimit(_apiResets!.Value, "usage limit", seen, Verbs.ModelFamily("claude-opus-5-5"));
        _clock.Advance(TimeSpan.FromMinutes(1));

        // Another session of the same family answered a turn requested after the limit was seen: the limit ends by evidence, and the owner's
        // stored row, which still carries the error and began before the end, no longer counts.
        _state.PutSession(new SessionRow
        {
            Id = "p0000001", SessionId = "p0000001-0000-4000-8000-000000000000", Name = "de1-retell-proof", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0, State = SessionState.Idle,
            StartedAt = seen.AddSeconds(5), LastRequestAt = seen.AddSeconds(10), LastResponseAt = seen.AddSeconds(20), Model = "claude-opus-5-5",
        });
        _outage.Look(_state.Sessions(), _clock.GetUtcNow());
        _outage.LimitEvidenceEndedAt.ShouldNotBeNull();
        _outage.IsOpen.ShouldBeFalse();

        // The end moved the login-change cut with it (Look calls PutLoginChanged on an evidence end), and the retell path reads only that
        // cut: told after the outage closed, so no redo is due and only the ladder can say it. With the login-change argument gone from
        // the retell hold, the row's own limit would hold the retell and nothing would be said.
        _outage.LoginChangedAt.ShouldBe(_outage.LimitEvidenceEndedAt);
        await TellNowAsync();
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
    }

    [Fact]
    public async Task ALimitOfAnotherAccountDoesNotHoldTheRetell()
    {
        // The owner's row ran under another login than the one the node reads now: its limit is not this account's.
        _state.PutUsage(new UsageRow { Account = new AccountRow { Email = "b@example.org" }, ReadAt = _clock.GetUtcNow() });
        _account = "a@example.org";
        await TellNowAsync();
        StopOnLimit("usage_limit_reached", TimeSpan.FromHours(5));

        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
    }

    [Fact]
    public async Task APerMinuteRateLimitErrorHoldsNothing()
    {
        await TellNowAsync();
        StopOnLimit("rate_limit_error", TimeSpan.FromHours(1));

        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
    }

    [Fact]
    public async Task IdleAndSilentIsRetoldOnceThenRings()
    {
        await TellNowAsync();

        _clock.Advance(After - TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _state.Jobs().Single().RetoldAt.ShouldNotBeNull();
        _state.Cards().ShouldBeEmpty();

        // Never a third tell; one ring once the second also goes unanswered.
        _clock.Advance(After);
        (await Sweep()).ShouldBeFalse();
        await Sweep();
        _said.Count.ShouldBe(1);
        var card = _state.Cards().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, JobRunner.UnansweredKind, Verbs.JobTarget("j1")));
    }

    /// <summary>
    /// A sweep reads the sessions once per told job, not once per session it compares. With the wake target looked up inside the
    /// lambda that picks its row, each comparison copied every session under the state lock: DE1, with thousands of session rows kept,
    /// spent one core flat out on that tick for ten hours (2026-11-25). At this node's scale the sweep stays well inside a second.
    /// </summary>
    [Fact]
    public async Task ASweepOverManySessionsAndToldJobsDoesNotReadTheSessionsPerSession()
    {
        // The session rows a long-running node keeps: ended workers, none of them an owner here, all started after the owner, so the
        // owner's row sits last in Sessions() (newest start first), as an owner that has been at it a while does.
        for (var i = 0; i < 2000; i++)
        {
            _state.PutSession(new SessionRow
            {
                Id = $"x{i:D7}", SessionId = $"x{i:D7}-0000-4000-8000-000000000000", Name = $"de1-old-{i}", Kind = SessionKind.Background, Cwd = _root,
                Role = SessionRole.Worker, Flight = "OLD1", Leg = "old", ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0,
                State = SessionState.Ended, StartedAt = _clock.GetUtcNow().AddMinutes(-30),
            });
        }
        var now = _clock.GetUtcNow();
        for (var i = 0; i < 20; i++)
        {
            _state.PutJob(new JobRow
            {
                Id = $"j{i}", Command = "echo hi", Cwd = _root, Owner = "w0000001", State = JobState.Succeeded, ExitCode = 0,
                LogPath = Path.Combine(_root, $"j{i}.log"), Timeout = TimeSpan.FromMinutes(1), StartedAt = now.AddMinutes(-2), EndedAt = now.AddMinutes(-1),
                Told = true, ToldAt = now,
            });
        }
        _clock.Advance(After);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await Sweep()).ShouldBeTrue();
        watch.Stop();

        // Every job is still retold to its owner: the lookup moved, what it finds did not.
        _said.Count.ShouldBe(20);
        _said.ShouldAllBe(text => text.StartsWith("Repeat:", StringComparison.Ordinal));
        // Linear it takes milliseconds. Read per session, one sweep of 4000 sessions and 100 jobs took 5 min 34 s on DE1; at this size it
        // takes several seconds, well past the bound.
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnAdHocSessionsJobIsToldOnceAndNeverRetoldOrRungFor()
    {
        _adHoc = true;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        await TellNowAsync();

        _clock.Advance(After * 2);
        (await Sweep()).ShouldBeFalse();
        _clock.Advance(After * 2);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task AWorkersJobWhoseLegStillFliesIsRetoldNamingWhatWaitsThenRings()
    {
        await TellNowAsync();
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldContain("and leg RTL1/one is still flying and this job was started by its worker.");

        _clock.Advance(After);
        await Sweep();
        _state.Cards().ShouldHaveSingleItem().Kind.ShouldBe(JobRunner.UnansweredKind);
    }

    [Theory]
    [InlineData(LegState.Done)]
    [InlineData(LegState.Failed)]
    public async Task ASucceededJobWhoseLegHasEndedIsNeitherRetoldNorRungFor(LegState ended)
    {
        await TellNowAsync();
        SetLeg(ended);
        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ASucceededJobRetoldWhileTheLegFlewRaisesNoCardOnceTheLegEnded()
    {
        await TellNowAsync();
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        SetLeg(LegState.Done);
        _clock.Advance(After);
        await Sweep();
        _state.Cards().ShouldBeEmpty();
        _state.Jobs().Single().RetellRung.ShouldBe(true);
    }

    [Fact]
    public async Task AFailedJobStillRingsAfterItsLegEnded()
    {
        await TellNowAsync();
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _state.PutJob(_state.Jobs().Single() with { State = JobState.Failed, Error = "boom" });
        SetLeg(LegState.Failed);
        _clock.Advance(After);
        await Sweep();
        _state.Cards().ShouldHaveSingleItem().Kind.ShouldBe(JobRunner.UnansweredKind);
    }

    // #1137 review: every WhatWaits branch, and what happens to a failed or cancelled job once what waited on it has ended.

    private async Task TellAsAsync(SessionRole role, string name)
    {
        _role = role;
        _name = name;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        await TellNowAsync();
    }

    private void PutPrReview(PrReviewState reviewState, string? fixer = null) =>
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "x/r", Number = 7, Url = "u", Base = "main", Head = "h", ClonePath = _root, Worktree = _root, Reviewer = "de1-review-r-7",
            Author = "ada", State = reviewState, StartedAt = _clock.GetUtcNow(), Fixer = fixer,
        });

    private void SetFlightReview(ReviewState reviewState) =>
        _state.PutFlight(_state.Flights().Single() with { Review = new ReviewRow { State = reviewState, Reviewer = "de1-rtl1-review", StartedAt = _clock.GetUtcNow() } });

    private async Task ShouldStayToldOnceAsync()
    {
        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task AWithdrawnFlightsSucceededJobIsNeitherRetoldNorRungFor()
    {
        await TellNowAsync();
        _state.WithdrawFlight("RTL1");
        await ShouldStayToldOnceAsync();
    }

    [Fact]
    public async Task AFailedJobOfAWorkerWhoseLegHasEndedIsStillRetoldAndRings()
    {
        await TellNowAsync();
        _state.PutJob(_state.Jobs().Single() with { State = JobState.Failed, Error = "boom" });
        SetLeg(LegState.Done);
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _clock.Advance(After);
        await Sweep();
        _state.Cards().ShouldHaveSingleItem().Kind.ShouldBe(JobRunner.UnansweredKind);
    }

    [Fact]
    public async Task ACancelledJobOfAWorkerWhoseLegHasEndedIsToldOnceLikeASucceededOne()
    {
        await TellNowAsync();
        _state.PutJob(_state.Jobs().Single() with { State = JobState.Cancelled });
        SetLeg(LegState.Done);
        await ShouldStayToldOnceAsync();
    }

    [Fact]
    public async Task APrReviewersJobIsRetoldWhileItsReviewIsOpen()
    {
        PutPrReview(PrReviewState.Reviewing);
        await TellAsAsync(SessionRole.Reviewer, "de1-review-r-7");
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldContain("and the review of x/r#7 is still open and this job was started by its reviewer.");
    }

    [Fact]
    public async Task APrReviewersJobIsNotRetoldOnceItsReviewIsNoLongerOpen()
    {
        PutPrReview(PrReviewState.Reviewed);
        await TellAsAsync(SessionRole.Reviewer, "de1-review-r-7");
        await ShouldStayToldOnceAsync();
    }

    [Fact]
    public async Task AFlightReviewersJobIsRetoldWhileItsFlightReviewIsOpenThenRings()
    {
        SetFlightReview(ReviewState.Reviewing);
        await TellAsAsync(SessionRole.Reviewer, "de1-rtl1-review");
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldContain("and the review of flight RTL1 is still open and this job was started by its reviewer.");

        _clock.Advance(After);
        await Sweep();
        _state.Cards().ShouldHaveSingleItem().Kind.ShouldBe(JobRunner.UnansweredKind);
    }

    [Fact]
    public async Task AFlightReviewersJobIsNotRetoldOnceItsFlightReviewPassed()
    {
        SetFlightReview(ReviewState.Passed);
        await TellAsAsync(SessionRole.Reviewer, "de1-rtl1-review");
        await ShouldStayToldOnceAsync();
    }

    [Fact]
    public async Task AFixersJobIsRetoldWhileItsFixRoundIsOpen()
    {
        PutPrReview(PrReviewState.Reviewed, fixer: "de1-fix-r-7");
        await TellAsAsync(SessionRole.Fixer, "de1-fix-r-7");
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldContain("and the fix round of x/r#7 is still open and this job was started by its fixer.");
    }

    [Fact]
    public async Task AFixersJobIsNotRetoldOnceItsFixRoundIsClosed()
    {
        PutPrReview(PrReviewState.Closed, fixer: "de1-fix-r-7");
        await TellAsAsync(SessionRole.Fixer, "de1-fix-r-7");
        await ShouldStayToldOnceAsync();
    }

    [Fact]
    public async Task ActedAfterTheTellIsNotRetold()
    {
        await TellNowAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());

        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    // #1137: a session that took turns shows it by a context that grew or a command it sent, not only by LastResponseAt.

    [Fact]
    public async Task AWorkerWhoseContextGrewAfterTheTellIsNotRetoldNorRungFor()
    {
        _context = 50_000;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        await TellNowAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        _context = 80_000;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task AContextThatGrewAfterACompactionShrankItCountsAsActing()
    {
        // #1147: the baseline only moved up, so growth after a compaction, still under the old size, was not seen and the worker was retold.
        _context = 50_000;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        await TellNowAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        _context = 20_000;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        (await Sweep()).ShouldBeFalse("too soon to retell; the sweep notes the compaction");
        _clock.Advance(TimeSpan.FromMinutes(1));
        _context = 30_000;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        _clock.Advance(After * 3);

        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARowThatRangDropsItsRetellBaselines()
    {
        // #1147: a row's baselines were kept for the node's life; once it rings the sweep never looks at it again.
        await TellNowAsync();
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _jobs.TellBaselines.ShouldBeGreaterThan(0);

        _clock.Advance(After);
        await Sweep();

        _state.Cards().ShouldHaveSingleItem().Kind.ShouldBe(JobRunner.UnansweredKind);
        _jobs.TellBaselines.ShouldBe(0);
    }

    [Theory]
    [InlineData(SessionRole.Planner, true)]
    [InlineData(SessionRole.Designer, true)]
    [InlineData(SessionRole.Watchtower, false)]
    public async Task AFailedJobIsRetoldForAPlannerOrDesignerButToldOnceForTheWatchtower(SessionRole role, bool retold)
    {
        // #1147 R1.6, the owner's call: a failure needs its session to act, so a planner's (a validator's runs in a planner) and a designer's
        // failed job is retold as a worker's is; the watchtower is the person's and is told once.
        _role = role;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1));
        await TellNowAsync();
        _state.PutJob(_state.Jobs().Single() with { State = JobState.Failed, Error = "boom" });
        _clock.Advance(After);

        (await Sweep()).ShouldBe(retold);
        _said.Count.ShouldBe(retold ? 1 : 0);
    }

    [Fact]
    public async Task AWorkerThatSentAnyCommandAfterTheTellIsNotRetoldNorRungFor()
    {
        await TellNowAsync();

        _clock.Advance(TimeSpan.FromMinutes(1));
        _commandAt = _clock.GetUtcNow();
        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ACommandBeforeTheTellDoesNotCountAndAnIdleWorkerIsRetoldThenRings()
    {
        _commandAt = _clock.GetUtcNow().AddMinutes(-5);
        await TellNowAsync();

        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _clock.Advance(After);
        await Sweep();
        _state.Cards().ShouldHaveSingleItem().Kind.ShouldBe(JobRunner.UnansweredKind);
    }

    [Fact]
    public async Task AContextThatGrewDuringTheBusyTurnTheTellFellInDoesNotCount()
    {
        _context = 10_000;
        await TellNowAsync(whileWorking: true);

        // The busy turn grows the context and ends; the session then idles without acting.
        _clock.Advance(TimeSpan.FromMinutes(1));
        _context = 40_000;
        Session(SessionState.Idle, _clock.GetUtcNow());
        (await Sweep()).ShouldBeFalse();
        _clock.Advance(After);
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
    }

    [Fact]
    public async Task RelayedIntoAWorkingSessionThatThenIdlesWithoutActingIsRetold()
    {
        await TellNowAsync(whileWorking: true);
        _state.Jobs().Single().ToldWhileWorking.ShouldBe(true);

        // The busy turn ends: a response after the tell, but not one that acted on it.
        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());
        _clock.Advance(After - TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RelayedIntoAWorkingSessionThatThenActsIsNotRetold()
    {
        await TellNowAsync(whileWorking: true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());
        (await Sweep()).ShouldBeFalse();

        _clock.Advance(TimeSpan.FromMinutes(1));
        Session(SessionState.Idle, _clock.GetUtcNow());
        _clock.Advance(After * 2);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOwnerThatEndedForGoodIsNotRetold()
    {
        await TellNowAsync();
        _endedForGood = true;

        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    // PIL791: sessions that are not taking turns cannot act on a tell, so tells and retells wait for them and are redone once.

    private JobRow Seed(string id, DateTimeOffset? toldAt = null)
    {
        var now = _clock.GetUtcNow();
        var row = new JobRow
        {
            Id = id, Command = "echo hi", Cwd = _root, Owner = "w0000001", State = JobState.Succeeded, ExitCode = 0,
            LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(1), StartedAt = now.AddMinutes(-2), EndedAt = now.AddMinutes(-1),
            Told = toldAt is not null, ToldAt = toldAt, ToldWay = toldAt is null ? null : SayWays.Relayed,
        };
        _state.PutJob(row);
        return row;
    }

    /// <summary>The session takes a turn now, which ends an open outage; its window is (when it began, now).</summary>
    private (DateTimeOffset Since, DateTimeOffset Until) EndOutage()
    {
        Session(SessionState.Idle, _clock.GetUtcNow());
        _outage.Look(_state.Sessions(), _clock.GetUtcNow());
        return _outage.LastWindow!.Value;
    }

    private Task<bool> TellSweep() => _jobs.TellUntoldAsync(null, TestContext.Current.CancellationToken);

    private CardRow[] Cards(string kind) => [.. _state.Cards().Where(c => c.Kind == kind)];

    [Fact]
    public async Task ATellAndARetellInsideAnOutageAreToldOnceMoreWhenItEndsAndTheRetellStartsFromThere()
    {
        // The PIL771 timeline: told 19:54, the login changes 19:55, retold 19:59 (before anything knew), the sessions take turns again 20:05.
        await TellNowAsync();
        var told = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(5));
        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
        _said.Clear();
        var retold = _clock.GetUtcNow();

        // The node sees the account change a minute later; the outage began when the login did.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var since = told.AddMinutes(1);
        _outage.AccountChanged(since);

        // The second silence would ring at retold + 4 minutes; with no session able to answer, nothing is said and nothing rings.
        _clock.Advance(TimeSpan.FromMinutes(4));
        (await Sweep()).ShouldBeFalse();
        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        Cards(JobRunner.UnansweredKind).ShouldBeEmpty();
        _state.Jobs().Single().RetoldAt.ShouldBe(retold);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var window = EndOutage();
        window.ShouldBe((since, _clock.GetUtcNow()));
        _said.ShouldBeEmpty();

        // Just after it ended: the tell is made once more, saying why, and the retell ladder starts from it.
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        var redone = _clock.GetUtcNow();
        var said = _said.ShouldHaveSingleItem();
        said.ShouldStartWith("Redo:");
        said.ShouldContain("not taking turns");
        said.ShouldContain("Your job j1");
        var row = _state.Jobs().Single();
        (row.Told, row.ToldAt, row.RetoldAt, row.RetoldWay, row.RetellRung).ShouldBe((true, redone, null, null, null));
        Cards(JobRunner.UnansweredKind).ShouldBeEmpty();

        // Once only, by either sweep.
        (await Sweep()).ShouldBeFalse();
        (await TellSweep()).ShouldBeFalse();
        _said.Count.ShouldBe(1);

        // The ladder from the redo: a repeat after the usual silence, then one ring for the second.
        _clock.Advance(After - TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeTrue();
        _said.Count.ShouldBe(2);
        _said[1].ShouldStartWith("Repeat:");
        Cards(JobRunner.UnansweredKind).ShouldBeEmpty();
        _clock.Advance(After);
        await Sweep();
        _said.Count.ShouldBe(2);
        Cards(JobRunner.UnansweredKind).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ATellAskedDuringAnOutageIsNotSentAndGoesOutWhenItEnds()
    {
        _outage.AccountChanged(_clock.GetUtcNow());
        var row = Seed("j1");

        await _jobs.TellAsync(row, TestContext.Current.CancellationToken);
        (await TellSweep()).ShouldBeFalse();
        _clock.Advance(TimeSpan.FromMinutes(2));
        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
        _state.Jobs().Single().Told.ShouldBeFalse();
        Cards(JobRunner.UntoldKind).ShouldBeEmpty();

        // Its one try was not used up by the holds: the sweep after the outage sends it, as a first tell and not a redo.
        EndOutage();
        (await TellSweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Your job j1");
        _state.Jobs().Single().Told.ShouldBeTrue();
        (await TellSweep()).ShouldBeFalse();
        (await Sweep()).ShouldBeFalse();
        _said.Count.ShouldBe(1);
        Cards(JobRunner.UntoldKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyRowsToldInsideTheWindowAreRedone()
    {
        var before = Seed("j0", _clock.GetUtcNow().AddMinutes(-30));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var since = _clock.GetUtcNow();
        _outage.AccountChanged(since);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Seed("j1", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(2));
        EndOutage();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var after = Seed("j2", _clock.GetUtcNow());

        (await TellSweep()).ShouldBeTrue();

        _said.ShouldHaveSingleItem().ShouldContain("Your job j1");
        var jobs = _state.Jobs().ToDictionary(j => j.Id);
        jobs["j0"].ToldAt.ShouldBe(before.ToldAt);
        jobs["j2"].ToldAt.ShouldBe(after.ToldAt);
        jobs["j1"].ToldAt.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public async Task ARingRaisedInsideTheWindowIsClosedAndItsRowStartsTheLadderAgain()
    {
        var toldAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var since = _clock.GetUtcNow();
        _outage.AccountChanged(since);
        var row = Seed("j1", toldAt) with { RetoldAt = since.AddMinutes(1), RetoldWay = SayWays.Relayed, RetellRung = true };
        _state.PutJob(row);
        _state.RaiseCard(CardBin.Ring, JobRunner.UnansweredKind, Verbs.JobTarget("j1"), "j1 ended and w1 was told twice but has not acted", "two tells, no turn");
        _clock.Advance(TimeSpan.FromMinutes(3));
        EndOutage();
        _clock.Advance(TimeSpan.FromSeconds(1));

        (await TellSweep()).ShouldBeTrue();

        var redone = _state.Jobs().Single();
        (redone.RetoldAt, redone.RetellRung).ShouldBe((null, null));
        redone.ToldAt.ShouldBe(_clock.GetUtcNow());
        var ring = Cards(JobRunner.UnansweredKind).ShouldHaveSingleItem();
        ring.State.ShouldBe(CardState.Closed);
        ring.Outcome.ShouldBe("its tells fell in an outage and were redone");
    }

    [Fact]
    public async Task ATellToNobodyAndARowTheOwnerEndedForAreNotRedone()
    {
        var since = _clock.GetUtcNow();
        _outage.AccountChanged(since);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Seed("j1", _clock.GetUtcNow());
        _state.PutJob(_state.Jobs().Single() with { ToldWay = "owner-ended" });
        _clock.Advance(TimeSpan.FromMinutes(1));
        EndOutage();
        _clock.Advance(TimeSpan.FromSeconds(1));

        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARedoWaitsWhileAnotherOutageIsOpenAndStillCoversTheFirstWindow()
    {
        _outage.AccountChanged(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Seed("j1", _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        EndOutage();
        _clock.Advance(TimeSpan.FromMinutes(1));

        // A second outage opens before the first one's redo went: it is held, not lost.
        _outage.AccountChanged(_clock.GetUtcNow());
        (await TellSweep()).ShouldBeFalse();
        _said.ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromMinutes(1));
        EndOutage();
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await TellSweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldContain("Your job j1");
        (await TellSweep()).ShouldBeFalse();
        _said.Count.ShouldBe(1);
    }

    // #1365: a row settled as owner-ended is not judged again while nothing about its owner changed.

    private void NewestOfTheName(string id) =>
        _state.PutSession(_state.Sessions().Single(s => s.Id == "w0000001") with
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", StartedAt = _clock.GetUtcNow().AddHours(-1), State = SessionState.Idle,
            LastResponseAt = _clock.GetUtcNow().AddHours(-1),
        });

    [Fact]
    public async Task ASecondSweepWithNothingChangedDoesNotJudgeARowTheFirstSettled()
    {
        await TellNowAsync();
        _endedForGood = true;
        _clock.Advance(After * 3);
        (await Sweep()).ShouldBeFalse();
        _afterTurnCalls.ShouldBeGreaterThan(0);
        _forGoodCalls.ShouldBeGreaterThan(0);
        var (afterTurn, forGood) = (_afterTurnCalls, _forGoodCalls);

        (await Sweep()).ShouldBeFalse();
        (await Sweep()).ShouldBeFalse();

        _afterTurnCalls.ShouldBe(afterTurn);
        _forGoodCalls.ShouldBe(forGood);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASettledRowIsStillRetoldToALiveReplacementOfTheOwnersNameAndRoleThatStartsLater()
    {
        await TellNowAsync();
        _endedForGood = true;
        _clock.Advance(After);
        (await Sweep()).ShouldBeFalse();

        NewestOfTheName("w0000002");
        _clock.Advance(After);

        (await Sweep()).ShouldBeTrue();
        _said.ShouldHaveSingleItem().ShouldStartWith("Repeat:");
    }

    [Fact]
    public async Task ASettledRowIsJudgedAgainWhenItsOwnerRowChanges()
    {
        await TellNowAsync();
        _endedForGood = true;
        _clock.Advance(After);
        (await Sweep()).ShouldBeFalse();
        var calls = _forGoodCalls;

        // The owner is resumed (its row changes), then ends again: judged afresh, and it is not ended for good this time.
        _endedForGood = false;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1) + TimeSpan.FromSeconds(1));
        (await Sweep()).ShouldBeFalse();
        _forGoodCalls.ShouldBeGreaterThan(calls);

        calls = _forGoodCalls;
        _endedForGood = true;
        Session(SessionState.Idle, _clock.GetUtcNow().AddHours(-1) + TimeSpan.FromSeconds(2));
        (await Sweep()).ShouldBeFalse();
        _forGoodCalls.ShouldBeGreaterThan(calls);
    }

    [Theory]
    [InlineData("owner-ended")]
    [InlineData("gone")]
    public async Task RowsToldOwnerEndedOrGoneAreNotVisited(string way)
    {
        _state.PutJob(Seed("j9", _clock.GetUtcNow().AddMinutes(-10)) with { ToldWay = way });
        _clock.Advance(After * 3);

        (await Sweep()).ShouldBeFalse();
        (await Sweep()).ShouldBeFalse();

        _afterTurnCalls.ShouldBe(0);
        _forGoodCalls.ShouldBe(0);
        _said.ShouldBeEmpty();
    }
}
