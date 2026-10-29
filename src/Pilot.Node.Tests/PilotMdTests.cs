using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A repo's PILOT.md is read at the flight's base and quoted whole into the planner, worker and reviewer briefs; a missing file
/// adds nothing, and one over the line limit is not quoted and raises one card.
/// </summary>
public sealed class PilotMdTests : IDisposable
{
    private const string Heading = "## This repo's rules for pilot (PILOT.md)";

    private const string Rules = "Never touch the lockfile.\nRun the formatter before you commit.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2k-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), new ManualTime(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero)));
    private readonly GitCli _git = new();
    private readonly LegCommander _legs;
    private readonly FlightRow _flight;

    private static readonly LegRow Leg = new() { Flight = "PIL1", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

    public PilotMdTests()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));
        var flights = new FlightCommander(_state, new Planes(_git), time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), _git, new Planes(_git), flights, options, time);
        Directory.CreateDirectory(Plane);
        _flight = new FlightRow
        {
            Code = "PIL1", Repo = "pilot", RepoPath = Plane, Issue = 1, Branch = "flight/pil1", Worktree = Plane, Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        };
        _state.PutFlight(_flight);
    }

    private string Plane => Path.Combine(_root, "plane");

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private async Task CommitAsync(string? pilotMd)
    {
        foreach (var args in new[] { new[] { "init", "-q", "-b", "main" }, ["config", "user.email", "t@example.com"], ["config", "user.name", "t"], ["config", "commit.gpgsign", "false"] })
        {
            (await _git.RunAsync(args, Plane, Ct)).Ok.ShouldBeTrue();
        }
        File.WriteAllText(Path.Combine(Plane, "a.txt"), "a");
        if (pilotMd is not null)
        {
            File.WriteAllText(Path.Combine(Plane, "PILOT.md"), pilotMd);
        }
        (await _git.RunAsync(["add", "-A"], Plane, Ct)).Ok.ShouldBeTrue();
        (await _git.RunAsync(["commit", "-q", "-m", "base"], Plane, Ct)).Ok.ShouldBeTrue();
    }

    private static string Lines(int count) => string.Join('\n', Enumerable.Range(1, count).Select(i => $"rule {i}"));

    [Fact]
    public void PlannerWorkerAndReviewerBriefsCarryThePilotMdUnderItsHeading()
    {
        string[] briefs =
        [
            Briefs.Plan(_flight, [], "pilot", "DU1", Rules),
            Briefs.For(_flight, Leg, [Leg], machine: "DU1", pilotMd: Rules),
            Briefs.Review(_flight, [Leg], new Briefs.PlaneState("", ""), "pilot", machine: "DU1", pilotMd: Rules),
        ];

        foreach (var brief in briefs)
        {
            brief.ShouldContain(Heading + "\n" + Rules);
        }
    }

    [Fact]
    public void WithoutAPilotMdNoBriefHasTheHeading()
    {
        string[] briefs =
        [
            Briefs.Plan(_flight, [], "pilot", "DU1"),
            Briefs.For(_flight, Leg, [Leg], machine: "DU1"),
            Briefs.Review(_flight, [Leg], new Briefs.PlaneState("", ""), "pilot", machine: "DU1"),
        ];

        foreach (var brief in briefs)
        {
            brief.ShouldNotContain("PILOT.md (");
            brief.ShouldNotContain(Heading);
        }
    }

    [Fact]
    public void ThePlannerBriefSaysAStepThePlaneCannotDoByPilotMdIsNotPlannedOrHasItsWayRoundNamed()
    {
        Briefs.Plan(_flight, [], "pilot", "DU1", Rules)
            .ShouldContain("A step the plane cannot do by the repo's PILOT.md rules is not planned, or is planned with the way round it named");
    }

    [Fact]
    public async Task ThePilotMdIsReadAtTheFlightsBaseNotFromTheWorkingTree()
    {
        await CommitAsync(Rules);
        File.WriteAllText(Path.Combine(Plane, "PILOT.md"), "A flight rewrote its own rules.");

        var read = await PilotMd.ReadAsync(_git, _flight, Ct);

        read.Text.ShouldBe(Rules);
        read.TooLongBy.ShouldBeNull();
    }

    [Fact]
    public async Task AMissingPilotMdReadsAsNothingAndRaisesNoCard()
    {
        await CommitAsync(null);

        (await _legs.PilotMdOfAsync(_flight)).ShouldBeNull();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task APilotMdOverTheLimitIsNotQuotedAndRaisesOneCardNamingTheRepoTheFlightAndTheLineCount()
    {
        await CommitAsync(Lines(PilotMd.MaxLines + 1));

        (await _legs.PilotMdOfAsync(_flight)).ShouldBeNull();
        (await _legs.PilotMdOfAsync(_flight)).ShouldBeNull();
        (await _legs.PilotMdOfAsync(_flight)).ShouldBeNull();

        var card = _state.Cards().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Kind.ShouldBe(PilotMd.TooLongKind);
        card.Subject.ShouldBe(Verbs.FlightTarget("PIL1"));
        card.Title.ShouldContain("pilot");
        card.Title.ShouldContain("PIL1");
        card.Detail.ShouldNotBeNull().ShouldContain($"{PilotMd.MaxLines + 1} lines, over the {PilotMd.MaxLines} limit");
    }

    [Fact]
    public async Task TheReposOwnPilotMdIsReadByTheNodeAndNotRefusedByTheLimit()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, PilotMd.FileName)))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull("no PILOT.md above the test's directory");
        var text = File.ReadAllText(Path.Combine(dir.FullName, PilotMd.FileName));
        text.ShouldNotContain("—");

        await CommitAsync(text);
        var read = await PilotMd.ReadAsync(_git, _flight, Ct);

        read.TooLongBy.ShouldBeNull();
        read.Text.ShouldBe(text.TrimEnd());
        read.Text.ShouldNotBeNull().Split('\n').Length.ShouldBeLessThanOrEqualTo(PilotMd.MaxLines);
    }

    [Fact]
    public async Task APilotMdOfExactlyTheLimitIsQuoted()
    {
        await CommitAsync(Lines(PilotMd.MaxLines));

        (await _legs.PilotMdOfAsync(_flight)).ShouldBe(Lines(PilotMd.MaxLines));
        _state.Cards().ShouldBeEmpty();
    }
}
