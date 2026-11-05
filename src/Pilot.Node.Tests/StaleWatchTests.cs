using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The stale watch (pilot #736): half-bound actions, one priority raise per bound, the attached summary.</summary>
public sealed class StaleWatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeTracker : ITracker
    {
        public bool HasField { get; set; } = true;

        public IssuePriority? Level { get; set; }

        public List<IssuePriority?> Sets { get; } = [];

        public TrackerKind Kind => TrackerKind.GitHub;

        public Task<string?> TitleAsync(string repoPath, int id, CancellationToken cancellationToken) => Task.FromResult<string?>("t");

        public string? MissingTitle(WorkItem item) => null;

        public Task<string[]> LabelsAsync(string repoPath, int id, CancellationToken cancellationToken) => Task.FromResult<string[]>([]);

        public Task AddLabelAsync(string repoPath, int id, string label, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveLabelAsync(string repoPath, int id, string label, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommentAsync(string repoPath, int id, string body, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SubTask> AddSubTaskAsync(string repoPath, int id, string title, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompleteSubTaskAsync(string repoPath, int id, SubTask subTask, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveSubTaskAsync(string repoPath, int id, SubTask subTask, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SubTaskItem[]> SubTasksAsync(string repoPath, int id, CancellationToken cancellationToken) => Task.FromResult<SubTaskItem[]>([]);

        public Task<PriorityRead> PriorityAsync(string repoPath, int id, CancellationToken cancellationToken) =>
            Task.FromResult(HasField ? new PriorityRead(true, Level) : PriorityRead.NoField);

        public Task SetPriorityAsync(string repoPath, int id, IssuePriority? level, CancellationToken cancellationToken)
        {
            Sets.Add(level);
            Level = level;
            return Task.CompletedTask;
        }
    }

    private readonly Clock _clock = new(Start);
    private readonly NodeState _state;
    private readonly FakeTracker _tracker = new();
    private readonly StaleWatch _watch;
    private readonly List<string> _said = [];
    private int _told;

    public StaleWatchTests()
    {
        _state = new NodeState(new InMemoryJournal(), _clock);
        _watch = new StaleWatch(_state, _clock)
        {
            Trackers = () => new Trackers([_tracker]),
            TellEndedJobs = (_, _) => Task.FromResult(_told-- > 0),
            Say = (id, text, _) =>
            {
                _said.Add($"{id}: {text}");
                return Task.FromResult(true);
            },
        };
    }

    private void Fly(TimeSpan age, string code = "DIR178")
    {
        _state.PutFlight(new FlightRow
        {
            Code = code, Repo = "dir", RepoPath = "C:/r/dir", Issue = 178, Branch = "flight/dir178", Worktree = "C:/r/dir/.claude/worktrees/DIR178",
            Base = "origin/main", State = FlightState.Flying, CreatedAt = Start - age,
        });
        _state.PutLeg(new LegRow { Flight = code, Name = "main", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
    }

    private FlightRow Row() => _state.Flights().Single();

    private void Worker(SessionState state) =>
        _state.PutSession(new SessionRow
        {
            Id = "w0000001", SessionId = "w0000001-0000-4000-8000-000000000000", Name = "de1-dir178-main", Kind = SessionKind.Background, Cwd = "C:/r",
            Role = SessionRole.Worker, Flight = "DIR178", Leg = "main", ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0, State = state,
            LastResponseAt = Start, StartedAt = Start.AddHours(-1),
        });

    [Fact]
    public async Task APastBoundRaisesOnceAndSaysWhy()
    {
        Fly(TimeSpan.FromHours(19));

        await _watch.TickAsync(Ct);
        await _watch.TickAsync(Ct);
        _clock.Now += TimeSpan.FromMinutes(5);
        await _watch.TickAsync(Ct);

        _tracker.Sets.ShouldBe([IssuePriority.High]);
        Row().IssuePriority.ShouldBe(IssuePriority.High);
        var raise = Row().StaleRaises.ShouldNotBeNull().ShouldHaveSingleItem();
        raise.Note.ShouldBe("raised to high: open 19 h");
        raise.Level.ShouldBe(IssuePriority.High);
    }

    [Fact]
    public async Task AFurtherBoundRaisesAnotherStepButNeverPastUrgent()
    {
        _tracker.Level = IssuePriority.High;
        Fly(TimeSpan.FromHours(19));
        _state.PutLeg(new LegRow { Flight = "DIR178", Name = "main", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Recheck = new LegRecheck { Check = "x", Since = Start - TimeSpan.FromHours(3) } });

        await _watch.TickAsync(Ct);
        await _watch.TickAsync(Ct);
        await _watch.TickAsync(Ct);

        // Two different bounds are passed (the flight's age, the recheck's); the second finds it urgent already.
        _tracker.Sets.ShouldBe([IssuePriority.Urgent]);
        _tracker.Level.ShouldBe(IssuePriority.Urgent);
    }

    [Theory]
    [InlineData(null, IssuePriority.High)]
    [InlineData(IssuePriority.Low, IssuePriority.High)]
    [InlineData(IssuePriority.Medium, IssuePriority.High)]
    [InlineData(IssuePriority.High, IssuePriority.Urgent)]
    [InlineData(IssuePriority.Urgent, null)]
    public void TheStepsUp(IssuePriority? from, IssuePriority? to) => StaleWatch.Raised(from).ShouldBe(to);

    [Fact]
    public async Task UrgentStaysUrgentAndIsNotRecorded()
    {
        _tracker.Level = IssuePriority.Urgent;
        Fly(TimeSpan.FromHours(30));

        await _watch.TickAsync(Ct);

        _tracker.Sets.ShouldBeEmpty();
        Row().StaleRaises.ShouldBeNull();
    }

    [Fact]
    public async Task ARepoWithoutAPriorityFieldSaysItCouldNotBeRaised()
    {
        _tracker.HasField = false;
        Fly(TimeSpan.FromHours(19));

        await _watch.TickAsync(Ct);
        await _watch.TickAsync(Ct);

        _tracker.Sets.ShouldBeEmpty();
        var raise = Row().StaleRaises.ShouldNotBeNull().ShouldHaveSingleItem();
        raise.Level.ShouldBeNull();
        raise.Note.ShouldStartWith("could not be raised");
    }

    [Fact]
    public async Task HalfABoundTellsAnEndedJobFirst()
    {
        Fly(TimeSpan.FromHours(7));
        Worker(SessionState.Idle);
        _told = 1;

        await _watch.TickAsync(Ct);
        await _watch.TickAsync(Ct);

        _said.ShouldBeEmpty();
        _tracker.Sets.ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Kind == StaleWatch.ActedKind && c.Title.Contains("told its worker of a job that had ended"));
    }

    [Fact]
    public async Task HalfABoundNudgesAnIdleWorkerOnce()
    {
        Fly(TimeSpan.FromHours(7));
        Worker(SessionState.Idle);

        await _watch.TickAsync(Ct);
        await _watch.TickAsync(Ct);

        _said.ShouldHaveSingleItem().ShouldStartWith("w0000001: Stale check: DIR178");
        _tracker.Sets.ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Kind == StaleWatch.ActedKind && c.Title.Contains("nudged its idle worker"));
    }

    [Fact]
    public async Task HalfABoundReRingsAnUnansweredCardOnce()
    {
        Fly(TimeSpan.FromHours(1));
        var card = _state.RaiseCard(CardBin.Ring, "x", Verbs.FlightTarget("DIR178"), "question");
        _clock.Now += TimeSpan.FromHours(1.5);

        await _watch.TickAsync(Ct);
        await _watch.TickAsync(Ct);

        _state.Cards().Count(c => c.Kind == StaleWatch.ReminderKind).ShouldBe(1);
        _state.Cards().ShouldContain(c => c.Kind == StaleWatch.ActedKind && c.Title.Contains(card.Id));
    }

    [Fact]
    public async Task TheSummaryIsAttachedAndRefreshedOnlyWhenTheCauseChanges()
    {
        Fly(TimeSpan.FromHours(19));
        await _watch.TickAsync(Ct);
        var first = Row();
        first.StaleSummary.ShouldNotBeNullOrWhiteSpace();
        first.StaleCause.ShouldNotBeNull();
        var since = first.StaleSince;

        // Time passes with the same cause: the summary stays as it was.
        _clock.Now += TimeSpan.FromHours(1);
        await _watch.TickAsync(Ct);
        Row().StaleSince.ShouldBe(since);
        Row().StaleSummary.ShouldBe(first.StaleSummary);

        // The cause changes (the leg is rechecking now): the summary is made anew.
        _state.PutLeg(new LegRow { Flight = "DIR178", Name = "main", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Recheck = new LegRecheck { Check = "x", Since = _clock.Now - TimeSpan.FromMinutes(10) } });
        await _watch.TickAsync(Ct);
        Row().StaleCause.ShouldNotBe(first.StaleCause);
        Row().StaleSince.ShouldBe(_clock.Now);
    }

    [Fact]
    public async Task TheDigestLineSaysTheRaise()
    {
        Fly(TimeSpan.FromHours(19));
        await _watch.TickAsync(Ct);

        var stale = StaleSection.Read(_state, _clock.Now, new StaleBounds(), TimeZoneInfo.Utc);
        var line = StaleSection.Lines(stale, _state.Flights()).Last();

        line.ShouldContain("(raised to high: open 19 h)");
    }
}
