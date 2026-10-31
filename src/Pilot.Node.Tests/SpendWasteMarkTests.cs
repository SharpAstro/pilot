using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Where the waste marks come from. A session is marked as having no result only where the node gives it up (a go-around of its
/// flying leg, a fresh planner of a flight with no legs), never for being seen ended: an ended session is resumed or nudged and
/// may yet finish. Real commanders and the real watch on a real plane, a manual clock.
/// </summary>
[Trait("Category", "Slow")]
public sealed class SpendWasteMarkTests : LegHoldTestsBase
{
    private readonly SpendLedger _ledger = new();
    private readonly List<CommandRequest> _said = [];
    private readonly LegWatch _legWatch;

    public SpendWasteMarkTests()
    {
        _legs.Ledger = _ledger;
        // The watch's says go to a session, which the tests only note; the rest is the commanders'.
        _legWatch = new LegWatch(_state, (command, cancellationToken) =>
        {
            if (command.Verb == Verbs.Say)
            {
                _said.Add(command);
                return Task.FromResult(_state.Complete(command, null));
            }
            return _legs.RunAsync(command, cancellationToken);
        }, _time) { Pilot = "pilot" };
    }

    private WasteMark[] Marks(string kind) => [.. _ledger.Marks().Where(m => m.Kind == kind)];

    private void Session(string id, string name, SessionRole role, SessionState state, DateTimeOffset lastResponse, string? leg = null) =>
        _state.PutSession(new SessionRow
        {
            Id = id,
            SessionId = id + "-0000-4000-8000-000000000000",
            EarlierSessionIds = [id + "-before"],
            Name = name,
            Kind = SessionKind.Background,
            Cwd = Repo,
            Role = role,
            Flight = "TIA1",
            Leg = leg,
            ContextTokens = 0,
            ContextWindow = 200_000,
            State = state,
            SpendWeighted = 0,
            LastResponseAt = lastResponse,
            StartedAt = Start,
        });

    [Fact]
    public async Task AWorkerSeenEndedWhileItsLegWaitsRechecksOrIsNudgedIsNotMarkedAndOneAGoAroundGivesUpIs()
    {
        await FlyingParser();
        var job = L("parser").WorkerJob.ShouldNotBeNull();
        Session(job, "du1-tia1-parser", SessionRole.Worker, SessionState.Ended, _time.GetUtcNow(), "parser");

        // Ended within the grace: it may be between turns.
        await _legWatch.LookAsync(Ct);
        // Ended on a wait it recorded: the wait wakes it.
        _state.PutLeg(L("parser") with { Wait = new LegWait { Path = Path.Combine(_root, "never"), Since = _time.GetUtcNow() } });
        _time.Advance(TimeSpan.FromMinutes(10));
        await _legWatch.LookAsync(Ct);
        // Ended on a finish whose check is being rerun: the node tells it the outcome.
        _state.PutLeg(L("parser") with { Wait = null, Recheck = new LegRecheck { Check = "dotnet test", Since = _time.GetUtcNow() } });
        await _legWatch.LookAsync(Ct);
        // Idle past the grace: nudged until the nudges run out, then rung, and a person may still resume it.
        _state.PutLeg(L("parser") with { Recheck = null });
        for (var i = 0; i < 4; i++)
        {
            await _legWatch.LookAsync(Ct);
        }
        _said.Count.ShouldBe(_legWatch.MaxNudges);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegWatch.Idle && c.Bin == CardBin.Ring);
        Marks(WasteKinds.NoResult).ShouldBeEmpty();

        // The person redoes the leg: the worker is given up, under each id it ran as.
        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "the", "worker", "stalled")).Ok.ShouldBeTrue();

        Marks(WasteKinds.NoResult).Select(m => (m.Session, m.Flight, m.Reason)).ShouldBe(
        [
            (job + "-before", "TIA1", "worker given up before it finished its leg"),
            (job + "-0000-4000-8000-000000000000", "TIA1", "worker given up before it finished its leg"),
        ]);
        var round = Marks(WasteKinds.Round).ShouldHaveSingleItem();
        (round.Flight, round.Leg, round.Round, round.Reason).ShouldBe(("TIA1", "parser", 1, WasteReasons.Other));

        // What it spent is waste of that kind; the next round's worker is the round's.
        var at = _time.GetUtcNow();
        _ledger.Record(job + "-0000-4000-8000-000000000000", "du1-tia1-parser", new SessionRoleInfo(SessionRole.Worker, "TIA1", "parser", null), "worker", 0,
            [new SpendMessage("m1", new MessageUsage(0, 0, 0, 100), at, "claude-sonnet-5-5", "high")], at);
        SpendWaste.Classify(_ledger.Entries(), _ledger.Marks()).ShouldHaveSingleItem().Kind.ShouldBe(WasteKinds.NoResult);
    }

    [Fact]
    public async Task APlannerSeenIdleIsToldNotMarkedAndOneEndedWithNoLegsIsMarkedWhenAFreshPlannerReplacesIt()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(5));
        Session("job00001", "du1-tia1-plan", SessionRole.Planner, SessionState.Idle, Start);

        // Idle without legs: told to hand them in, which it may yet do.
        await _legWatch.LookAsync(Ct);
        _said.ShouldHaveSingleItem().Target.ShouldBe("session/job00001");
        Marks(WasteKinds.NoResult).ShouldBeEmpty();

        // Ended without legs: the watch starts a fresh planner, which gives the first one up.
        Session("job00001", "du1-tia1-plan", SessionRole.Planner, SessionState.Ended, Start);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _legWatch.LookAsync(Ct);

        _workers.Started.Count(w => w.Name == "du1-tia1-plan").ShouldBe(2);
        Marks(WasteKinds.NoResult).Select(m => m.Reason).Distinct().ShouldBe(["planner given up without planning any legs"]);
        Marks(WasteKinds.NoResult).Select(m => m.Session).ShouldContain("job00001-0000-4000-8000-000000000000");
        Marks(WasteKinds.Replanned).ShouldBeEmpty();
    }

    [Fact]
    public async Task AStartThatFailsIsMarkedUnderTheSessionNameItWouldHaveHad()
    {
        await FlyingParser();
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "it went wrong")).Ok.ShouldBeTrue();
        _workers.Fail = new InvalidOperationException("claude refused the start");

        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "try", "again")).Ok.ShouldBeFalse();

        var failed = Marks(WasteKinds.FailedStart).ShouldHaveSingleItem();
        (failed.Who, failed.Flight, failed.Reason).ShouldBe(("du1-tia1-parser", "TIA1", "claude refused the start"));
        // A failed leg's worker said how it ended: the go-around gives nobody up.
        Marks(WasteKinds.NoResult).ShouldBeEmpty();
    }
}

/// <summary>
/// The review's waste marks, on a real plane with a bare origin: a reviewer seen ended is told first, and marked only when a fresh
/// review gives it up; a fresh review of a head that only a base merge changed is marked, one with a commit of the leg's own is not.
/// </summary>
[Trait("Category", "Slow")]
public sealed class SpendWasteReviewMarkTests : ReviewTestsBase
{
    private readonly SpendLedger _ledger = new();

    public SpendWasteReviewMarkTests() => _legs.Ledger = _ledger;

    private WasteMark[] Marks(string kind) => [.. _ledger.Marks().Where(m => m.Kind == kind)];

    private async Task<FlightRow> Opened()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return F(flight.Code);
    }

    private static void Commit(string worktree, string name, string message)
    {
        File.WriteAllText(Path.Combine(worktree, name), "more\n");
        Git(worktree, "add", ".");
        Git(worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }

    // Main moves on and the plane merges it in, as a leg's finish does.
    private void BaseMerged(FlightRow flight, string name)
    {
        Commit(Repo, name, "the base moves on");
        Git(Repo, "push", "-q", "origin", "main");
        Git(flight.Worktree, "fetch", "-q", "origin");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "--no-edit", flight.Base);
    }

    private async Task ReviewAgain(string code)
    {
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);
    }

    [Fact]
    public async Task AReviewerSeenEndedIsToldNotMarkedAndTheFreshReviewThatReplacesItMarksIt()
    {
        var flight = await Opened();
        var job = flight.Review.ShouldNotBeNull().ReviewerJob.ShouldNotBeNull();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        _state.PutSession(new SessionRow
        {
            Id = job, SessionId = job + "-0000-4000-8000-000000000000", Name = Reviewer(flight.Code), Kind = SessionKind.Background, Cwd = flight.Worktree,
            Role = SessionRole.Reviewer, Flight = flight.Code, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Ended, SpendWeighted = 0,
            LastResponseAt = time.GetUtcNow().AddMinutes(-10), StartedAt = time.GetUtcNow().AddMinutes(-20),
        });
        var said = new List<CommandRequest>();
        var watch = new LegWatch(_state, (command, cancellationToken) =>
        {
            if (command.Verb == Verbs.Say)
            {
                said.Add(command);
                return Task.FromResult(_state.Complete(command, null));
            }
            return _legs.RunAsync(command, cancellationToken);
        }, time) { Pilot = "pilot" };

        // Told to give its verdict, which it may yet do: not marked.
        await watch.LookAsync(Ct);
        said.ShouldHaveSingleItem().Target.ShouldBe(Verbs.SessionTarget(job));
        Marks(WasteKinds.NoResult).ShouldBeEmpty();

        // Told, it did not: the fresh review gives it up.
        time.Advance(TimeSpan.FromMinutes(4));
        await watch.LookAsync(Ct);
        await Started(_legs, flight.Code);

        F(flight.Code).Review.ShouldNotBeNull().ReviewerJob.ShouldNotBe(job);
        var mark = Marks(WasteKinds.NoResult).ShouldHaveSingleItem();
        (mark.Session, mark.Flight, mark.Reason).ShouldBe((job + "-0000-4000-8000-000000000000", flight.Code, "reviewer given up without a verdict"));
    }

    [Fact]
    public async Task AFreshReviewOfAHeadOnlyABaseMergeChangedIsMarkedAndOneWithACommitOfItsOwnIsNot()
    {
        var flight = await Opened();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "- the null check is gone")).Error.ShouldBeNull();

        BaseMerged(flight, "base-1.txt");
        await ReviewAgain(flight.Code);

        var mark = Marks(WasteKinds.BaseMergeReview).ShouldHaveSingleItem();
        mark.Flight.ShouldBe(flight.Code);
        // The reviewer that began just after it is classed so.
        var at = mark.At.AddSeconds(20);
        _ledger.Record("rev-2", Reviewer(flight.Code), new SessionRoleInfo(SessionRole.Reviewer, flight.Code, null, null), "reviewer", 0,
            [new SpendMessage("m1", new MessageUsage(0, 0, 0, 100), at, "claude-sonnet-5-5", "high")], at);
        SpendWaste.Classify(_ledger.Entries(), _ledger.Marks()).ShouldHaveSingleItem().Kind.ShouldBe(WasteKinds.BaseMergeReview);

        // A commit of the leg's own, then a base merge: the review reads something new, so it is not marked.
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "- still no null check")).Error.ShouldBeNull();
        Commit(flight.Worktree, "fix.txt", "restore the null check");
        BaseMerged(flight, "base-2.txt");
        await ReviewAgain(flight.Code);

        Marks(WasteKinds.BaseMergeReview).ShouldHaveSingleItem();
    }
}
