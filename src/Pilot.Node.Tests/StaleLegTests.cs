using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A leg past 2 go-arounds or 6 hours flying raises one leg-stale card with a table of its check runs (#752).</summary>
public sealed class StaleLegTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly LegWatch _watch;

    public StaleLegTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time);
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "pilot", RepoPath = "/r", Issue = 1, Title = "A flight", Branch = "flight/tia1",
            Worktree = "/r/.claude/worktrees/tia1", Base = "origin/main", State = FlightState.Flying, CreatedAt = Start,
        });
        _watch.ChangedFiles = (_, _) => Task.FromResult<IReadOnlyList<string>?>(["src/Pilot.Node/Flights/Parser.cs"]);
    }

    private void Leg(int goArounds = 0) => _state.PutLeg(new LegRow
    {
        Flight = "TIA1", Name = "parser", Order = 1, Goal = "Parse", DoneWhen = "Tests pass", State = LegState.Flying,
        Check = "dotnet test Pilot.Node.Tests --filter FullyQualifiedName~ParserTests", GoArounds = goArounds, StartedAt = Start,
    });

    private void Job(string id, JobState state, TimeSpan after, TimeSpan took, string? failed = null) => _state.PutJob(new JobRow
    {
        Id = id, Command = "dotnet test", Cwd = "/r", Kind = JobKind.Check, Flight = "TIA1", Leg = "parser", State = state,
        LogPath = "/tmp/" + id + ".log", Timeout = TimeSpan.FromMinutes(30), StartedAt = Start + after, EndedAt = Start + after + took,
        TestsFailed = failed is null ? 0 : 1, FirstTestFailed = failed,
    });

    private CardRow[] Stale() => [.. _state.Cards().Where(c => c.Kind == LegWatch.LegStale)];

    [Fact]
    public async Task ALegAtGoAroundThreeRaisesOneStaleCardWithATableOfItsCheckJobs()
    {
        Leg(goArounds: 3);
        Job("j1", JobState.Failed, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(4), "ParserTests.Parses");
        Job("j2", JobState.Failed, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5), "EphemerisTests.Loads");
        Job("j3", JobState.Succeeded, TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(6));

        await _watch.LookAsync(Ct);

        var card = Stale().ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.Handled, "flight/TIA1/parser"));
        card.Title.ShouldContain("go-around 3");
        var rows = card.Detail!.Split('\n');
        rows.Length.ShouldBe(4);
        rows[1].ShouldContain("j1");
        rows[1].ShouldContain("4 m");
        rows[1].ShouldContain("failed");
        rows[1].ShouldContain("ParserTests.Parses (the flight's)");
        rows[2].ShouldContain("EphemerisTests.Loads (not this flight's)");
        rows[3].ShouldContain("passed");

        await _watch.LookAsync(Ct);
        Stale().Length.ShouldBe(1);
    }

    [Fact]
    public async Task ALegFlyingOverSixHoursRaisesOneStaleCardAndASecondSweepNone()
    {
        Leg();
        Job("j1", JobState.Failed, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(4), "ParserTests.Parses");
        _time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        var card = Stale().ShouldHaveSingleItem();
        card.Title.ShouldContain("flying 6 h");
        card.Detail!.Split('\n').Length.ShouldBe(2);
    }

    [Fact]
    public async Task ALegUnderBothLimitsRaisesNone()
    {
        Leg(goArounds: 2);
        Job("j1", JobState.Failed, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(4), "ParserTests.Parses");
        _time.Advance(TimeSpan.FromHours(5));

        await _watch.LookAsync(Ct);

        Stale().ShouldBeEmpty();
    }
}
