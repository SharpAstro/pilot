using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A flight's tokens so far ride the node's snapshot from its spend ledger, and 'pilot status' prints them on the flight's line.</summary>
public sealed class StatusTokensTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static FlightRow Flight(string code, int issue) => new()
    {
        Code = code, Repo = "o/r", RepoPath = "r", Issue = issue, Branch = "flight/" + code.ToLowerInvariant(), Worktree = "w", Base = "main",
        State = FlightState.Flying, CreatedAt = At,
    };

    private static void Spend(SpendLedger ledger, string flight, string id, long output) =>
        ledger.Record("s-" + id, "n", new SessionRoleInfo(SessionRole.Worker, flight, "parser", null), "worker", 0,
            [new SpendMessage(id, new MessageUsage(0, 0, 0, output), At, "m", "low")], At);

    private static (NodeState State, SpendLedger Ledger) Node()
    {
        var ledger = new SpendLedger();
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System) { TokensByFlight = ledger.TokensByFlight };
        state.PutFlight(Flight("TIA1", 1));
        state.PutFlight(Flight("TIA2", 2));
        return (state, ledger);
    }

    [Fact]
    public void StatusPrintsATokensFigureOnAFlightWithSpendAndNoneOnOneWithout()
    {
        var (state, ledger) = Node();
        Spend(ledger, "TIA1", "a", 1_200_000);
        Spend(ledger, "TIA1", "b", 34_567);

        var lines = StatusMarkdown.Build(state.Snapshot()).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        lines.Single(l => l.Contains("flight/tia1")).ShouldContain("1,234,567 tokens so far");
        lines.Single(l => l.Contains("flight/tia2")).ShouldNotContain("tokens");
    }

    [Fact]
    public void TheFigureRidesTheSnapshotOnlyAndNeverMakesTheStoredRowChange()
    {
        var (state, ledger) = Node();
        Spend(ledger, "TIA1", "a", 500);
        var seq = state.Seq;

        state.Snapshot().Flights.Single(f => f.Code == "TIA1").Tokens.ShouldBe(500);
        state.Snapshot().Flights.Single(f => f.Code == "TIA2").Tokens.ShouldBeNull();
        state.PutFlight(Flight("TIA1", 1));

        state.Seq.ShouldBe(seq);
    }

    [Fact]
    public void AFlightChangedEventKeepsTheFigureTheMirrorHad()
    {
        var (state, ledger) = Node();
        Spend(ledger, "TIA1", "a", 900);
        var mirror = new NodeMirror();
        mirror.Apply(state.Snapshot());
        mirror.Apply(new FlightChangedEvent { Seq = state.Seq + 1, At = At, Flight = Flight("TIA1", 1) with { Title = "x" } });

        mirror.Flights["TIA1"].Tokens.ShouldBe(900);
    }

    [Fact]
    public async Task TheTokensRouteAnswersEveryFlightsTokensInOneRead()
    {
        await using var node = await NodeHarness.StartAsync(TestContext.Current.CancellationToken);

        var answer = await node.Client.GetSpendTokensAsync(TestContext.Current.CancellationToken);

        answer.Response.ShouldNotBeNull();
    }
}
