using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Signals: the digest twice a day, and a worker past its budget rung for until the budget is raised.</summary>
public sealed class SignalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;

    public SignalTests() => _state = new NodeState(new InMemoryJournal(), _time);

    private static FlightRow Flight(string code, FlightState state, DateTimeOffset? landedAt = null, string[]? connections = null) => new FlightRow
    {
        Code = code,
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1,
        Title = "Planets " + code,
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(),
        Branch = "flight/" + code.ToLowerInvariant(),
        Base = "origin/main",
        State = state,
        Connections = connections ?? [],
        CreatedAt = Start.AddDays(-1),
        LandedAt = landedAt,
    };

    [Theory]
    [InlineData("07:59", "08:00", true)]
    [InlineData("08:00", "08:01", false)]
    [InlineData("08:01", "17:59", false)]
    [InlineData("17:59", "18:30", true)]
    public void ADigestIsDueWhenEightOrSixPassed(string from, string to, bool due)
    {
        var digest = new Digest(_state, _time);
        var day = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

        digest.Due(day + TimeSpan.Parse(from), day + TimeSpan.Parse(to)).ShouldBe(due);
    }

    [Fact]
    public void TheDigestTimesAreLocal()
    {
        _time.Zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var digest = new Digest(_state, _time);
        var day = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

        // 08:00 at +2 is 06:00 UTC.
        digest.Due(day.AddHours(5.9), day.AddHours(6)).ShouldBeTrue();
        digest.Due(day.AddHours(7.9), day.AddHours(8.1)).ShouldBeFalse();
    }

    [Fact]
    public void TheDigestListsWhatLandedWhatWasHandledAndWhatIsParkedAndReplacesTheLastOne()
    {
        _state.PutFlight(Flight("TIA1", FlightState.Landed, landedAt: Start.AddHours(-1)));
        _state.PutFlight(Flight("TIA2", FlightState.Held, connections: ["TIA3"]));
        _state.PutFlight(Flight("TIA3", FlightState.Flying));
        _state.PutLeg(new LegRow { Flight = "TIA3", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Failed, Report = "the API is down\nmore" });
        _state.RaiseCard(CardBin.Handled, "worker-nudged", "flight/TIA3/parser", "TIA3/parser's worker went idle; nudged (1 of 2)");
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA3/parser", "TIA3/parser failed");
        var digest = new Digest(_state, _time);

        var first = digest.Raise(_time.GetUtcNow());

        (first.Bin, first.Kind, first.Subject).ShouldBe((CardBin.ForYou, Digest.Kind, "node"));
        first.Title.ShouldBe("Digest: 1 landed, 1 flying, 1 card open");
        first.Detail.ShouldBe(string.Join(Environment.NewLine,
            "Landed:",
            "- TIA1: Planets TIA1",
            "Handled:",
            "- TIA3/parser's worker went idle; nudged (1 of 2)",
            "Parked:",
            "- TIA2 held, waiting for TIA3",
            "- TIA3/parser failed: the API is down",
            "- c2 open: TIA3/parser failed"));

        _time.Advance(TimeSpan.FromHours(10));
        var second = digest.Raise(_time.GetUtcNow());
        second.Title.ShouldBe("Digest: 0 landed, 1 flying, 1 card open");
        _state.Cards().Single(c => c.Id == first.Id).Outcome.ShouldBe("a newer digest");
    }

    [Fact]
    public async Task AWorkerPastItsBudgetRingsUntilTheBudgetIsRaised()
    {
        var root = Path.Combine(Path.GetTempPath(), "p2b-" + Guid.NewGuid().ToString("N")[..8]);
        _state.PutFlight(Flight("TIA3", FlightState.Flying));
        _state.PutLeg(new LegRow { Flight = "TIA3", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Budget = 1_000_000, WorkerJob = "a1b2c3d4", StartedAt = Start });
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "se1-tia3-parser", Kind = SessionKind.Background, Cwd = "/w",
            Role = SessionRole.Worker, Flight = "TIA3", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working,
            SpendWeighted = 1_200_000, Budget = 1_000_000, StartedAt = Start,
        });
        var watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time);

        await watch.LookAsync(Ct);

        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Detail).ShouldBe((CardBin.Ring, LegWatch.OverBudget, "1,200,000 of 1,000,000 weighted tokens; it keeps working until you choose"));
        card.Choices.Select(c => (c.Label, c.Verb, c.Args.FirstOrDefault())).ShouldBe(
            [("Extend to 1,500,000", Verbs.ExtendBudget, "1500000"), ("Go around", Verbs.GoAround, "the last worker spent 1,200,000 of a 1,000,000 budget")]);

        var git = new GitCli();
        var legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), new FlightCommander(_state, new Planes(git), _time),
            new NodeOptions(Path.Combine(root, "n.sock"), root, MachinePrefix: "SE1"), _time);
        Task<CommandResult> Extend(string tokens) =>
            legs.RunAsync(new CommandRequest { Verb = Verbs.ExtendBudget, Target = "flight/TIA3/parser", Args = [tokens], Who = "tests" }, Ct);

        (await Extend("900000")).Error.ShouldBe("TIA3/parser's budget is 1,000,000 already; an extension raises it");
        (await Extend("1500000")).Ok.ShouldBeTrue();

        _state.Legs("TIA3").Single().Budget.ShouldBe(1_500_000);
        _state.Cards(openOnly: true).ShouldBeEmpty();
        _state.Cards().Single().Outcome.ShouldBe("budget extended to 1,500,000 by tests");
    }
}
