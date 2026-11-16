using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The jobs step of the idle-time maintenance sweep (PIL1102): the transcript folders of archived flights' planes, on a fake profile root with fake times.</summary>
public sealed class MaintenanceJobsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pilot-mjobs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _projects;
    private readonly NodeState _state;
    private readonly ManualTime _time = new(Now);

    public MaintenanceJobsTests()
    {
        _projects = Path.Combine(_root, "profile", "projects");
        Directory.CreateDirectory(_projects);
        _state = new NodeState(new InMemoryJournal(), _time) { Archive = new FlightArchive(Path.Combine(_root, "data")) };
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private string PlaneOf(string code) => Path.Combine(_root, "repo", ".claude", "worktrees", code.ToLowerInvariant());

    private void PutFlight(string code, bool archived)
    {
        _state.PutFlight(new FlightRow
        {
            Code = code, Repo = "pilot", RepoPath = Path.Combine(_root, "repo"), Issue = int.Parse(code[3..]), Title = "t", Branch = "flight/" + code.ToLowerInvariant(),
            Worktree = PlaneOf(code), Base = "main", State = archived ? FlightState.Landed : FlightState.Flying, CreatedAt = Now.AddDays(-30),
            LandedAt = archived ? Now.AddDays(-20) : null, ReviewRounds = [],
        });
        if (archived)
        {
            _state.ArchiveFlight(code);
        }
    }

    /// <summary>A transcript folder of <paramref name="cwd"/> with a transcript of <paramref name="size"/> bytes, last written <paramref name="daysAgo"/> days ago.</summary>
    private string Transcripts(string cwd, int daysAgo, int size = 1000, bool memory = false)
    {
        var folder = Path.Combine(_projects, MaintenanceJobs.ProjectFolderOf(cwd));
        Directory.CreateDirectory(folder);
        var files = new List<string> { Path.Combine(folder, "a.jsonl") };
        File.WriteAllBytes(files[0], new byte[size]);
        if (memory)
        {
            Directory.CreateDirectory(Path.Combine(folder, "memory"));
            files.Add(Path.Combine(folder, "memory", "MEMORY.md"));
            File.WriteAllText(files[1], "- note");
        }
        var at = Now.UtcDateTime - TimeSpan.FromDays(daysAgo);
        foreach (var file in files)
        {
            File.SetLastWriteTimeUtc(file, at);
        }
        foreach (var dir in files.Select(Path.GetDirectoryName).Distinct())
        {
            Directory.SetLastWriteTimeUtc(dir!, at);
        }
        return folder;
    }

    private void LiveSession(string cwd) => _state.PutSession(new SessionRow
    {
        Id = "live0001", SessionId = "live0001-0000-4000-8000-000000000000", Name = "s-live", Kind = SessionKind.Background, Cwd = cwd,
        Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Now,
    });

    private MaintenanceJobs Step(int days = 2) => new(_state, _projects, _time) { ScratchDays = () => days };

    [Fact]
    public async Task AnArchivedFlightsOldTranscriptFolderGoesAndNothingElseDoes()
    {
        PutFlight("PIL1", archived: true);
        PutFlight("PIL2", archived: false);
        PutFlight("PIL3", archived: true);
        PutFlight("PIL4", archived: true);
        PutFlight("PIL5", archived: true);
        var gone = Transcripts(PlaneOf("PIL1"), daysAgo: 5, size: 4000);
        var deeper = Transcripts(Path.Combine(PlaneOf("PIL1"), "src"), daysAgo: 5, size: 500);
        var notArchived = Transcripts(PlaneOf("PIL2"), daysAgo: 30);
        var newer = Transcripts(PlaneOf("PIL3"), daysAgo: 1);
        var memory = Transcripts(PlaneOf("PIL4"), daysAgo: 30, memory: true);
        var live = Transcripts(PlaneOf("PIL5"), daysAgo: 30);
        var other = Transcripts(Path.Combine(_root, "some", "other", "cwd"), daysAgo: 90);
        LiveSession(Path.Combine(PlaneOf("PIL5"), "src"));

        var result = await Step().RunAsync(Ct);

        Directory.Exists(gone).ShouldBeFalse();
        Directory.Exists(deeper).ShouldBeFalse();
        new[] { notArchived, newer, memory, live, other }.ShouldAllBe(f => Directory.Exists(f));
        result.Bytes.ShouldBe(4500);
        result.Untouched.ShouldBeNull();
    }

    [Fact]
    public async Task ADryRunMeasuresWhatARealRunFreesAndRemovesNothing()
    {
        PutFlight("PIL1", archived: true);
        PutFlight("PIL2", archived: true);
        var one = Transcripts(PlaneOf("PIL1"), daysAgo: 5, size: 3000);
        var two = Transcripts(PlaneOf("PIL2"), daysAgo: 9, size: 2500);

        var measure = await Step().MeasureAsync(Ct);

        Directory.Exists(one).ShouldBeTrue();
        Directory.Exists(two).ShouldBeTrue();
        measure.Items.ShouldBe(2);
        var real = await Step().RunAsync(Ct);
        real.Bytes.ShouldBe(measure.Bytes);
        measure.Bytes.ShouldBe(5500);
    }

    [Fact]
    public async Task TheScratchDaysSettingIsReadAtEachRun()
    {
        PutFlight("PIL1", archived: true);
        var folder = Transcripts(PlaneOf("PIL1"), daysAgo: 5);
        var days = 10;
        var step = new MaintenanceJobs(_state, _projects, _time) { ScratchDays = () => days };

        (await step.MeasureAsync(Ct)).Items.ShouldBe(0);
        days = 2;
        (await step.MeasureAsync(Ct)).Items.ShouldBe(1);

        Directory.Exists(folder).ShouldBeTrue();
    }

    [Fact]
    public async Task WithNoProjectsFolderOrNoArchiveThereIsNothingToDo()
    {
        PutFlight("PIL1", archived: true);
        Transcripts(PlaneOf("PIL1"), daysAgo: 5);

        (await new MaintenanceJobs(_state, Path.Combine(_root, "missing"), _time).MeasureAsync(Ct)).Items.ShouldBe(0);
        (await new MaintenanceJobs(new NodeState(new InMemoryJournal(), _time), _projects, _time).MeasureAsync(Ct)).Items.ShouldBe(0);
    }

    [Fact]
    public void TheStepIsNamedJobs() => Step().Name.ShouldBe("jobs");
}
