using System.Text.Json;
using Pilot.Contracts;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Writes a store the way a node does, through <see cref="FileJournal"/> and <see cref="NodeState"/>: a snapshot holding every kind of
/// stored record, then a journal with a delta of each kind, a decision, and <c>store.json</c>. It is how the store fixtures under
/// <c>Fixtures/Store/&lt;version&gt;/</c> are made, by the code of the release they stand for (see PILOT.md and <c>release.yml</c>).
/// </summary>
/// <remarks>
/// Kept to the contract's members that every release since the first fixture has had (required members and the long-standing ones), so the
/// same file compiles in a detached worktree of an older release, which is how the first fixture was written. A member added later is not
/// set here: its absence from the old fixture is the point of it.
/// </remarks>
public static class StoreFixtureWriter
{
    /// <summary>Environment variable naming the folder the gated test writes into; the test is skipped without it.</summary>
    public const string FolderVariable = "PILOT_WRITE_STORE_FIXTURE";

    /// <summary>Environment variable naming the version <c>store.json</c> says wrote the store; this build's own when unset.</summary>
    public const string VersionVariable = "PILOT_STORE_FIXTURE_VERSION";

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Writes the store into <paramref name="folder"/>: the snapshot with everything first, then the deltas after it.</summary>
    public static void Write(string folder, string? version = null)
    {
        var sample = (JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot.sample.json")), PilotJsonContext.Default.NodeEvent)
            as SnapshotEvent) ?? throw new InvalidDataException("the sample snapshot holds no snapshot");

        using (var journal = new FileJournal(folder, version: version))
        {
            var state = new NodeState(journal, new StepClock(Start));

            // The snapshot: every kind of record, put as the node puts it.
            FillSnapshot(state, sample);
            var snapshot = state.Snapshot();
            journal.Append(snapshot, JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(snapshot, PilotJsonContext.Default.NodeEvent));

            // The journal after it: a delta of each kind.
            FillDeltas(state, sample);
        }

        // Writing the snapshot kept the journal it replaced as a backup, and a store is only the files a node reads.
        foreach (var kept in Directory.EnumerateFiles(folder).Where(f => f.Contains(".bak-", StringComparison.Ordinal) || f.EndsWith(".tmp", StringComparison.Ordinal)))
        {
            File.Delete(kept);
        }
    }

    private static void FillSnapshot(NodeState state, SnapshotEvent sample)
    {
        foreach (var session in sample.Sessions)
        {
            state.PutSession(session);
        }
        state.Execute(Note("a note in the snapshot"));
        state.Execute(new CommandRequest { Verb = Verbs.Order, Target = Verbs.NodeTarget, Args = ["a standing order in the snapshot"], Who = "fixture" });

        state.PutFlight(Flight("PIL1", FlightState.Flying, 1));
        state.PutFlight(Flight("PIL2", FlightState.Held, 2));
        state.PutLeg(Leg("PIL1", "first", 1, LegState.Done));
        state.PutLeg(Leg("PIL1", "second", 2, LegState.Flying));
        state.PutLeg(Leg("PIL1", "third", 3, LegState.Planned));
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/PIL1", "PR ready to merge", "its checks are green");
        state.RaiseCard(CardBin.ForYou, "landed", "flight/PIL2", "PIL2 landed");

        foreach (var job in sample.Jobs)
        {
            state.PutJob(job);
        }
        foreach (var review in sample.PrReviews)
        {
            state.PutPrReview(review);
        }
        state.PutMerge(Merge("PIL1", MergeEntryState.Waiting, 1));

        state.PutUsage(Usage(40));
        state.PutMachine(Machine(30, 8L << 30));
        state.PutAdmission(Admission(2, ["a1b2c3d4 (PIL1/second)", "b2c3d4e5 (PIL2/first)"]));
        state.PutNode(new NodeRow { Maintenance = false, Autopilot = true, AcceptsFleetFlights = true });
        state.PutRepos(Repos(flights: 2));
        state.PutPullRequests(PullRequests("Ready"));

        foreach (var group in sample.Ideas.GroupBy(i => i.Repo))
        {
            state.PutIdeas(group.Key, group);
        }
        foreach (var move in sample.IdeaMoves)
        {
            state.PutIdeaMove(move);
        }
        foreach (var group in sample.Milestones.GroupBy(m => m.Repo))
        {
            state.PutMilestones(group.Key, group);
        }
        foreach (var group in sample.Issues.GroupBy(i => i.Repo))
        {
            state.PutIssues(group.Key, group);
        }
        foreach (var summary in sample.Summaries)
        {
            state.PutSummary(summary);
        }
    }

    private static void FillDeltas(NodeState state, SnapshotEvent sample)
    {
        // Sessions: one added, one changed, one removed.
        var first = sample.Sessions[0];
        state.PutSession(first with { Id = "fffffff0", SessionId = "fffffff0-0000-4000-8000-000000000000", Name = "a session the journal adds" });
        state.PutSession(first with { SpendWeighted = first.SpendWeighted + 1000 });
        state.RemoveSession(sample.Sessions[^1].Id);

        // A command result and a note, and a decision.
        state.Execute(Note("a note in the journal"));
        state.RecordDecision("flight/PIL1", DecisionKinds.Design, "Which way?", ["a", "b"], "a", "fixture", DecisionByKinds.Person, "the fixture picks one");

        // Flights and legs: added, changed, a leg removed.
        state.PutFlight(Flight("PIL3", FlightState.Flying, 3));
        state.PutFlight(Flight("PIL1", FlightState.Flying, 1) with { Title = "A flight changed in the journal" });
        state.PutLeg(Leg("PIL3", "only", 1, LegState.Planned));
        state.PutLeg(Leg("PIL1", "second", 2, LegState.Done));
        state.RemoveLeg("PIL1", "third");

        // Cards: one raised, one changed.
        var raised = state.RaiseCard(CardBin.Ring, "worker", "flight/PIL3/only", "a worker stopped", "in the journal");
        state.AddAsker(raised.Id, "fixture");

        // Jobs, PR reviews and merges: added, changed, removed.
        state.PutJob(Job("j900", JobState.Running));
        state.PutJob(Job("j900", JobState.Succeeded) with { EndedAt = Start.AddHours(5), ExitCode = 0 });
        state.PutPrReview(PrReview(11, PrReviewState.Reviewing));
        state.PutPrReview(PrReview(11, PrReviewState.Reviewed));
        state.PutMerge(Merge("PIL3", MergeEntryState.Waiting, 2));
        state.PutMerge(Merge("PIL3", MergeEntryState.Checks, 2));
        state.RemoveMerge("PIL3");

        // What the node reads: usage, machine, admission, node, repos, pull requests.
        state.PutUsage(Usage(55));
        state.PutMachine(Machine(75, 12L << 30));
        state.PutAdmission(Admission(3, ["a1b2c3d4 (PIL1/second)"]));
        state.PutNode(new NodeRow { Maintenance = true, Autopilot = true, AcceptsFleetFlights = true });
        state.PutRepos(Repos(flights: 3));
        state.PutPullRequests(PullRequests("Draft"));

        // Ideas, issues, milestones and summaries: the rows of the sample, changed or moved.
        var idea = sample.Ideas[0];
        state.PutIdeas(idea.Repo, [idea with { Title = idea.Title + " (edited)" }, .. sample.Ideas.Skip(1).Where(i => i.Repo == idea.Repo)]);
        state.PutIdeaMove(new IdeaMoveRow("pilot", "i900", "tianwen", "i901"));
        var milestone = sample.Milestones[0];
        state.PutMilestones(milestone.Repo, [milestone with { Title = milestone.Title + " (edited)" }, .. sample.Milestones.Skip(1).Where(m => m.Repo == milestone.Repo)]);
        var issue = sample.Issues[0];
        state.PutIssues(issue.Repo, [issue with { Title = issue.Title + " (edited)" }, .. sample.Issues.Skip(1).Where(i => i.Repo == issue.Repo)]);
        state.PutSummary(sample.Summaries[0] with { Markdown = "a summary made in the journal", MadeAt = Start.AddHours(6) });
    }

    private static CommandRequest Note(string text) =>
        new() { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [text], Who = "fixture" };

    private static FlightRow Flight(string code, FlightState state, int issue) => new()
    {
        Code = code, Repo = "SharpAstro/pilot", RepoPath = "/repos/pilot", Issue = 800 + issue, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/repos/pilot/.claude/worktrees/" + code.ToLowerInvariant(), Base = "main", State = state, CreatedAt = Start.AddMinutes(issue),
        Title = "A flight " + code,
    };

    private static LegRow Leg(string flight, string name, int order, LegState state) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = $"Do {name} for {flight}", DoneWhen = "The check passes.", State = state,
    };

    private static MergeRow Merge(string flight, MergeEntryState state, int place) => new()
    {
        Flight = flight, Repo = "SharpAstro/pilot", Head = "4f2c9a01d6e8", Method = "squash", State = state, AddedAt = Start.AddHours(2), Place = place,
    };

    private static JobRow Job(string id, JobState state) => new()
    {
        Id = id, Command = "dotnet test", Cwd = "/repos/pilot", State = state, LogPath = $"/tmp/{id}.log", Timeout = TimeSpan.FromHours(1), StartedAt = Start.AddHours(4),
    };

    private static PrReviewRow PrReview(int number, PrReviewState state) => new()
    {
        Repo = "SharpAstro/pilot", Number = number, Url = $"https://github.com/SharpAstro/pilot/pull/{number}", Base = "main", Head = "9d8c7b6a5f4e",
        ClonePath = "/repos/pilot", Worktree = $"/repos/pilot/.claude/worktrees/review-pilot-{number}", Reviewer = $"de1-review-pilot-{number}",
        State = state, StartedAt = Start.AddHours(3),
    };

    private static UsageRow Usage(double percent) => new()
    {
        ReadAt = Start.AddHours(1).AddMinutes(percent),
        FiveHour = new UsageWindow { UsedPercent = percent, ResetsAt = Start.AddHours(5) },
        SevenDay = new UsageWindow { UsedPercent = percent / 2, ResetsAt = Start.AddDays(5) },
    };

    private static MachineRow Machine(double cpu, long memoryUsed) => new()
    {
        SampledAt = Start.AddHours(2).AddMinutes(cpu), CpuPercent = cpu, Cores = 16, MemoryUsed = memoryUsed, MemoryTotal = 32L << 30,
        Gpu = new MachineGpu { Percent = cpu / 2, MemoryUsed = 2L << 30, MemoryTotal = 8L << 30 },
        Processes = [new MachineProcess { Pid = 4242, Name = "dotnet", CpuPercent = cpu / 2, MemoryBytes = 1L << 30 }],
    };

    private static AdmissionRow Admission(int count, string[] counted) => new()
    {
        OpusCount = count, OpusCap = 3, OneFlightPerRepo = true, Counted = counted, Priority = ["PIL1"],
    };

    private static ReposRow Repos(int flights) => new()
    {
        ReadAt = Start.AddHours(2).AddMinutes(flights * 5),
        Repos =
        [
            new RepoStatus
            {
                Name = "pilot", Path = "/repos/pilot", GitHub = "SharpAstro/pilot", Tracked = true, Flights = flights, DefaultBranch = "main", Branch = "main",
                Worktrees = [new WorktreeStatus { Path = "/repos/pilot/.claude/worktrees/pil1" }],
                PullRequests = [new PullRequestStatus { Number = 11, Title = "A PR", Head = "flight/pil1" }],
                UpdatedAt = Start.AddHours(2),
            },
        ],
    };

    private static PullRequestsRow PullRequests(string label) => new()
    {
        ReadAt = Start.AddHours(2),
        PullRequests = [new PullRequestRow { Repo = "SharpAstro/pilot", Number = 11, Title = "A PR", Url = "https://github.com/SharpAstro/pilot/pull/11", Kind = PullRequestKind.Ready, Label = label }],
    };

    /// <summary>A clock that moves one second at each reading, so the events of a written store are in order and the same on every run.</summary>
    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => start.AddSeconds(Interlocked.Increment(ref _ticks));
    }
}

/// <summary>The gated writer: with <see cref="StoreFixtureWriter.FolderVariable"/> set, writes a store there; an ordinary run skips it.</summary>
public sealed class StoreFixtureWriterTests
{
    [Fact]
    public void WriteTheStoreFixture()
    {
        var folder = Environment.GetEnvironmentVariable(StoreFixtureWriter.FolderVariable);
        Assert.SkipUnless(!string.IsNullOrEmpty(folder), $"set {StoreFixtureWriter.FolderVariable} to a folder to write a store fixture into it");

        StoreFixtureWriter.Write(folder!, Environment.GetEnvironmentVariable(StoreFixtureWriter.VersionVariable) is { Length: > 0 } version ? version : null);
    }
}
