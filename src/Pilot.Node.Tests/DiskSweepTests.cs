using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The disk sweep (pilot #711): job scratch of landed flights and of old jobs, and old probe outputs.</summary>
public sealed class DiskSweepTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2ds-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state;
    private readonly ManualTime _time = new(Start);
    private readonly DiskSweep _sweep;

    public DiskSweepTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Temp);
        _sweep = new DiskSweep(Jobs, Temp, _state, _time) { ScratchDays = () => 2 };
    }

    private string Jobs => Path.Combine(_root, "jobs");

    private string Temp => Path.Combine(_root, "temp");

    public void Dispose() => RemovalQueue.TryDelete(_root, out _);

    // A job folder with a state.json, a timeline and a tmp folder holding a file, all last written <paramref name="age"/> ago.
    private string Job(string id, TimeSpan age)
    {
        var job = Path.Combine(Jobs, id);
        var tmp = Path.Combine(job, "tmp");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(job, "state.json"), "{}");
        File.WriteAllText(Path.Combine(job, "timeline.jsonl"), "");
        File.WriteAllBytes(Path.Combine(tmp, "scratch.bin"), new byte[1000]);
        Age(tmp, age);
        return job;
    }

    private void Age(string folder, TimeSpan age)
    {
        var when = (Start - age).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, when);
        }
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder))
        {
            Directory.SetLastWriteTimeUtc(dir, when);
        }
    }

    private void Session(string id, string? flight, SessionState state) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "s-" + id, Kind = SessionKind.Background, Cwd = "/x",
        Role = SessionRole.Worker, Flight = flight, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Start,
    });

    private void Flight(string code, FlightState state) => _state.PutFlight(new FlightRow
    {
        Code = code, Repo = "pilot", Issue = 1, Title = "t", RepoPath = "/r", Branch = "b", Worktree = "/w", Base = "origin/main",
        State = state, CreatedAt = DateTimeOffset.UnixEpoch,
    });

    private string Probe(string name, TimeSpan age)
    {
        var folder = Path.Combine(Temp, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "out.png"), new byte[500]);
        Age(folder, age);
        return folder;
    }

    [Fact]
    public async Task ALandedFlightsEndedSessionLosesItsTmpAtOnceButKeepsItsStateJson()
    {
        Flight("PIL1", FlightState.Landed);
        Session("aaaaaaaa", "PIL1", SessionState.Ended);
        var job = Job("aaaaaaaa", TimeSpan.FromMinutes(5));

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(job, "tmp")).ShouldBeFalse();
        File.Exists(Path.Combine(job, "state.json")).ShouldBeTrue();
        File.Exists(Path.Combine(job, "timeline.jsonl")).ShouldBeTrue();
    }

    [Fact]
    public async Task ALiveSessionsTmpAndAFlightStillInTheAirKeepTheirScratch()
    {
        Flight("PIL1", FlightState.Landed);
        Flight("PIL2", FlightState.Flying);
        Session("bbbbbbbb", "PIL1", SessionState.Working);
        Session("cccccccc", "PIL2", SessionState.Ended);
        var live = Job("bbbbbbbb", TimeSpan.FromDays(10));
        var flying = Job("cccccccc", TimeSpan.FromMinutes(5));

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(live, "tmp")).ShouldBeTrue();
        Directory.Exists(Path.Combine(flying, "tmp")).ShouldBeTrue();
    }

    [Fact]
    public async Task ScratchOlderThanTheSettingGoesAndYoungerStays()
    {
        var old = Job("dddddddd", TimeSpan.FromDays(3));
        var young = Job("eeeeeeee", TimeSpan.FromDays(1));
        // One fresh file keeps a folder whose other files are old.
        var mixed = Job("ffffffff", TimeSpan.FromDays(5));
        File.WriteAllText(Path.Combine(mixed, "tmp", "fresh.txt"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(mixed, "tmp", "fresh.txt"), (Start - TimeSpan.FromHours(1)).UtcDateTime);

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(old, "tmp")).ShouldBeFalse();
        File.Exists(Path.Combine(old, "state.json")).ShouldBeTrue();
        Directory.Exists(Path.Combine(young, "tmp")).ShouldBeTrue();
        Directory.Exists(Path.Combine(mixed, "tmp")).ShouldBeTrue();
    }

    [Fact]
    public async Task ProbeOutputsOlderThanADayGoAndAFreshOneAndOtherTempFoldersStay()
    {
        var old = Probe("p2o-old", TimeSpan.FromDays(2));
        var fresh = Probe("p2o-fresh", TimeSpan.FromHours(2));
        var other = Probe("unrelated-old", TimeSpan.FromDays(9));

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
        Directory.Exists(other).ShouldBeTrue();
    }

    [Fact]
    public async Task ASweepThatFreedSomethingRaisesOneHandledLineNamingTheCountsAndTheUniqueBytes()
    {
        Job("11111111", TimeSpan.FromDays(4));
        Job("22222222", TimeSpan.FromDays(4));
        Probe("p2o-old", TimeSpan.FromDays(2));

        var (scratch, probes, bytes) = await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        (scratch, probes, bytes).ShouldBe((2, 1, 2500));
        var card = _state.Cards().Single(c => c.Kind == "disk-swept");
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldContain("2 job scratch folders");
        card.Title.ShouldContain("1 probe folder");
        card.Title.ShouldContain("2.4 KB");

        // A second sweep has nothing left to free and says nothing.
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);
        _state.Cards().Count(c => c.Kind == "disk-swept").ShouldBe(1);
    }

    [Fact]
    public async Task AFolderThatCannotBeRemovedDoesNotStopTheRest()
    {
        var held = Job("99999999", TimeSpan.FromDays(4));
        var free = Job("88888888", TimeSpan.FromDays(4));
        using var hold = new FileStream(Path.Combine(held, "tmp", "scratch.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(free, "tmp")).ShouldBeFalse();
    }
}
