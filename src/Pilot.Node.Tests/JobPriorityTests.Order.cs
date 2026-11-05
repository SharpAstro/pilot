using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Queue order (PIL735): the job queue starts waiting jobs by their flight's priority, then first come; a long wait lifts a job one level; a benchmark still goes first (PIL735).</summary>
public sealed partial class JobPriorityTests
{
    private double? _cpu = 5;

    private static readonly TimeSpan Eight = TimeSpan.FromHours(8);

    /// <summary>A flight whose repo has the Priority field, at <paramref name="level"/> (null is unset), with a session of the same name flying it.</summary>
    private string Flight(string code, IssuePriority? level)
    {
        var id = code.ToLowerInvariant().PadRight(8, '0')[..8];
        _state.PutFlight(FlightRowOf(code, level));
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = $"se1-{code.ToLowerInvariant()}-worker", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.Worker, Flight = code, Leg = "leg", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle,
            SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    private FlightRow FlightRowOf(string code, IssuePriority? level) => new()
    {
        Code = code, Repo = "pilot", RepoPath = _root, Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = Path.Combine(_root, "plane-" + code), Base = "main",
        State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, HasPriorityField = true, IssuePriority = level,
    };

    private string HandAs(JobRunner jobs, string session, Clock clock, TimeSpan? timeout = null, bool alone = false)
    {
        // A second apart, so first come is a strict order whatever the ids sort as.
        clock.Advance(TimeSpan.FromSeconds(1));
        string[] args = alone
            ? [Verbs.AloneArg, _root, ((long)(timeout ?? TimeSpan.Zero).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), Sleep(30)]
            : [_root, ((long)(timeout ?? TimeSpan.Zero).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), Sleep(30)];
        var id = jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.SessionTargetPrefix + session, Args = args, Who = "se1-tia1-parser" }).Made!;
        jobs.Dispatch(new JobRunner.HandedOver(_state.Jobs().Single(j => j.Id == id)));
        return id;
    }

    private static void Look(JobRunner jobs) => jobs.Dispatch(JobRunner.LookAgain.Instance);

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    /// <summary>Ends the blocker, so the head of the queue starts: it, and only it, runs; the rest wait. Then ends it.</summary>
    private async Task StartsNext(JobRunner jobs, Func<string, Task> gone, string blocker, string expected, params string[] rest)
    {
        await gone(blocker);
        Look(jobs);
        await Until(expected, j => j.State == JobState.Running);
        foreach (var other in rest)
        {
            Row(other).State.ShouldBe(JobState.Queued);
        }
        await gone(expected);
    }

    [Fact]
    public async Task AnUrgentFlightsJobQueuedLastStartsBeforeEarlierNormalOnes()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var normalA = Flight("NORMA", null);
        var normalB = Flight("NORMB", null);
        var urgent = Flight("URGNT", IssuePriority.Urgent);
        var blocker = HandAs(jobs, normalA, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var first = HandAs(jobs, normalA, clock);
        var second = HandAs(jobs, normalB, clock);
        var last = HandAs(jobs, urgent, clock);

        Row(last).Step.ShouldNotBeNull().ShouldContain("next in line");
        await StartsNext(jobs, gone, blocker, last, first, second);
    }

    [Fact]
    public async Task JobsOfOnePriorityKeepFirstComeOrder()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var a = Flight("SAMEA", IssuePriority.High);
        var b = Flight("SAMEB", IssuePriority.High);
        var blocker = HandAs(jobs, a, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var first = HandAs(jobs, b, clock);
        var second = HandAs(jobs, a, clock);
        var third = HandAs(jobs, b, clock);

        await StartsNext(jobs, gone, blocker, first, second, third);
    }

    [Fact]
    public async Task AJobQueuedOverTheBoundAgoIsLiftedPastANewerJobOneLevelAboveIt()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var normal = Flight("OLDNO", null);
        var medium = Flight("NEWME", IssuePriority.Medium);
        var blocker = HandAs(jobs, medium, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var old = HandAs(jobs, normal, clock);
        clock.Advance(JobOrder.WaitBound);
        var newer = HandAs(jobs, medium, clock);
        // The old one has waited just over the bound, so it ranks as Medium, level with the newer job, and was queued first.
        Look(jobs);

        Row(old).Step.ShouldNotBeNull().ShouldContain("next in line");
        await StartsNext(jobs, gone, blocker, old, newer);
    }

    [Fact]
    public async Task AJobWithinTheBoundIsNotLifted()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var normal = Flight("YNGNO", null);
        var medium = Flight("NEWME", IssuePriority.Medium);
        var blocker = HandAs(jobs, medium, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var young = HandAs(jobs, normal, clock);
        clock.Advance(JobOrder.WaitBound - TimeSpan.FromMinutes(5));
        var newer = HandAs(jobs, medium, clock);

        await StartsNext(jobs, gone, blocker, newer, young);
    }

    [Fact]
    public void TheLiftNeverGoesAboveUrgentAndAnUnknownFlightRanksAsUnset()
    {
        var now = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        var urgent = FlightRowOf("URGNT", IssuePriority.Urgent);
        var high = FlightRowOf("HIGHF", IssuePriority.High);
        FlightRow[] flights = [urgent, high];
        JobRow Job(string? flight, TimeSpan waited) => new()
        {
            Id = "j1", Command = "x", Cwd = _root, State = JobState.Queued, LogPath = "x.log", Timeout = JobRunner.DefaultTimeout, Flight = flight, QueuedAt = now - waited, StartedAt = now - waited,
        };

        JobOrder.RankOf(Job("URGNT", TimeSpan.FromHours(9)), flights, null, now).ShouldBe(IssuePriorities.Rank(IssuePriority.Urgent));
        JobOrder.RankOf(Job("HIGHF", TimeSpan.FromHours(3)), flights, null, now).ShouldBe(IssuePriorities.Rank(IssuePriority.Urgent));
        JobOrder.RankOf(Job("HIGHF", TimeSpan.FromHours(1)), flights, null, now).ShouldBe(IssuePriorities.Rank(IssuePriority.High));
        JobOrder.RankOf(Job("GONE", TimeSpan.Zero), flights, null, now).ShouldBe(IssuePriorities.Rank(null));
        JobOrder.RankOf(Job(null, TimeSpan.Zero), flights, null, now).ShouldBe(IssuePriorities.Rank(null));
    }

    [Fact]
    public void WithoutThePriorityFieldAFlightInTheAdmissionListRanksAsHigh()
    {
        var now = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        var marked = FlightRowOf("MARKD", null) with { HasPriorityField = false };
        var job = new JobRow { Id = "j1", Command = "x", Cwd = _root, State = JobState.Queued, LogPath = "x.log", Timeout = JobRunner.DefaultTimeout, Flight = "MARKD", QueuedAt = now, StartedAt = now };

        JobOrder.RankOf(job, [marked], new AdmissionRow { Priority = ["MARKD"] }, now).ShouldBe(IssuePriorities.Rank(IssuePriority.High));
        JobOrder.RankOf(job, [marked], new AdmissionRow(), now).ShouldBe(IssuePriorities.Rank(null));
    }

    [Fact]
    public async Task ABenchmarkStillGoesFirstWhateverTheFlightPriorities()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var urgent = Flight("URGNT", IssuePriority.Urgent);
        var normal = Flight("NORMA", null);
        var blocker = HandAs(jobs, normal, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var urgentRun = HandAs(jobs, urgent, clock);
        var bench = HandAs(jobs, normal, clock, alone: true);

        Row(bench).Kind.ShouldBe(JobKind.Bench);
        await StartsNext(jobs, gone, blocker, bench, urgentRun);
    }

    [Fact]
    public async Task ANormalJobBehindAnUrgentOneSaysUrgentFlightsGoFirst()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var normal = Flight("NORMA", null);
        var urgent = Flight("URGNT", IssuePriority.Urgent);
        var blocker = HandAs(jobs, normal, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var waiting = HandAs(jobs, normal, clock);
        var urgentJob = HandAs(jobs, urgent, clock);

        Row(waiting).Step.ShouldNotBeNull().ShouldStartWith("queued, 1 ahead of it (urgent flights first); ");
        await StartsNext(jobs, gone, blocker, urgentJob, waiting);
    }

    [Fact]
    public async Task AJobLiftedByTheWaitBoundSaysSo()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var high = Flight("HIGHF", IssuePriority.High);
        var urgent = Flight("URGNT", IssuePriority.Urgent);
        var blocker = HandAs(jobs, urgent, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var urgentJob = HandAs(jobs, urgent, clock);
        var lifted = HandAs(jobs, high, clock);
        clock.Advance(JobOrder.WaitBound);
        Look(jobs);

        // Lifted to Urgent, level with the job ahead: nothing outranks it, so the only reason is the lift.
        Row(lifted).Step.ShouldNotBeNull().ShouldStartWith("queued, 1 ahead of it (lifted, waited over 2 h); ");
        await StartsNext(jobs, gone, blocker, urgentJob, lifted);
    }

    [Fact]
    public async Task AJobBehindAnEqualOrLowerRankedOneKeepsThePlainStep()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var normalA = Flight("NORMA", null);
        var normalB = Flight("NORMB", null);
        var blocker = HandAs(jobs, normalA, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var first = HandAs(jobs, normalA, clock);
        var second = HandAs(jobs, normalB, clock);

        Row(second).Step.ShouldNotBeNull().ShouldStartWith("queued, 1 ahead of it; ");
        await StartsNext(jobs, gone, blocker, first, second);
    }

    [Fact]
    public async Task APriorityChangedWhileAJobWaitsReRanksIt()
    {
        var (jobs, clock, _, gone) = Idle(() => _cpu);
        var a = Flight("FLTAA", null);
        var b = Flight("FLTBB", null);
        var blocker = HandAs(jobs, a, clock, Eight);
        await Until(blocker, j => j.State == JobState.Running);

        var first = HandAs(jobs, a, clock);
        var second = HandAs(jobs, b, clock);
        Row(first).Step.ShouldNotBeNull().ShouldContain("next in line");

        _state.PutFlight(FlightRowOf("FLTBB", IssuePriority.Urgent));
        Look(jobs);

        Row(second).Step.ShouldNotBeNull().ShouldContain("next in line");
        Row(first).Step.ShouldNotBeNull().ShouldContain("queued, 1 ahead");
        await StartsNext(jobs, gone, blocker, second, first);
    }
}
