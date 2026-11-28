using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A landing whose own stop of a session failed keeps that session's stop-unconfirmed card open (raised when none is) while its row is
/// still live, and names it in the Handled line (PIL1270:R1.9, pilot #1282). A session whose stop worked, or whose row is ended, has the
/// card closed with "&lt;code&gt; landed". The plane is a real git worktree, as <see cref="LandedFlightCardsTests"/> has it.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LandedStopUnconfirmedTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Began = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private sealed class Stops(bool fail) : IWorkerLauncher
    {
        public Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task StopAsync(string job, CancellationToken cancellationToken)
        {
            if (fail)
            {
                return Task.FromException(new InvalidOperationException("claude stop: refused"));
            }
            // A stop that worked leaves the row as it was: the tracker ends it on a later poll, as in production.
            return Task.CompletedTask;
        }
    }

    private readonly ManualTime _time = new(Began.AddHours(1));
    private readonly NodeState _state;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2u-" + Guid.NewGuid().ToString("N")[..8]);

    public LandedStopUnconfirmedTests() => _state = new NodeState(new InMemoryJournal(), _time);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Git(Path.Combine(_root, "pilot"), "worktree", "prune");
            }
            catch (InvalidOperationException)
            {
            }
            TestDirs.DeleteTree(_root);
        }
    }

    private static void Git(string cwd, params string[] args) => HelperGit.Run(cwd, args);

    private async Task<(string Code, FlightCommander Flights, FlightRow Flight)> FlightAsync(bool stopFails)
    {
        var repo = Path.Combine(_root, "pilot");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var flights = new FlightCommander(_state, new Planes(new GitCli()), _time, new Stops(stopFails)) { FinishGrace = TimeSpan.Zero };
        var made = await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["1282", "Stops"], Who = "tests" }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        var flight = _state.Flights().Single();
        return (flight.Code, flights, flight);
    }

    private void PutSession(string id, string flight, string cwd, SessionState state) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "de1-" + id, Kind = SessionKind.Background, Cwd = cwd, Flight = flight,
        Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Began,
    });

    private CardRow? Card(string id) => _state.Cards().SingleOrDefault(c => c.Kind == SessionMessenger.StopUnconfirmedKind && c.Subject == Verbs.SessionTarget(id));

    private async Task LandAsync(FlightCommander flights, string code)
    {
        var landed = await flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(code), Who = "controller" }, Ct);
        landed.Ok.ShouldBeTrue(landed.Error);
    }

    private string Handled(string code) => _state.Cards().Single(c => c.Kind == "landed" && c.Subject == Verbs.FlightTarget(code)).Detail.ShouldNotBeNull();

    [Fact]
    public async Task AFailedStopOfALiveSessionKeepsItsOpenCardAndNamesItInTheHandledLine()
    {
        var (code, flights, flight) = await FlightAsync(stopFails: true);
        PutSession("w1", code, flight.Worktree, SessionState.Idle);
        _state.RaiseCard(CardBin.Ring, SessionMessenger.StopUnconfirmedKind, Verbs.SessionTarget("w1"), "earlier");

        await LandAsync(flights, code);

        Card("w1").ShouldNotBeNull().State.ShouldBe(CardState.Open);
        Handled(code).ShouldContain("de1-w1 (w1) may still run: its stop failed, its card stays open");
    }

    [Fact]
    public async Task AFailedStopWithNoCardRaisesOneWithAnEndItAgainChoice()
    {
        var (code, flights, flight) = await FlightAsync(stopFails: true);
        PutSession("w1", code, flight.Worktree, SessionState.Idle);

        await LandAsync(flights, code);

        var card = Card("w1").ShouldNotBeNull();
        card.State.ShouldBe(CardState.Open);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Choices.ShouldNotBeNull().ShouldContain(c => c.Label == "End it again" && c.Verb == Verbs.EndSession && c.Target == Verbs.SessionTarget("w1"));
        Handled(code).ShouldContain("may still run");
        card.Detail.ShouldNotBeNull().ShouldContain("claude stop: refused");
        card.Detail.ShouldContain("Tried: claude stop on its job.");
    }

    [Fact]
    public async Task AFoldedFlightsHandledLineNamesASessionThatMayStillRun()
    {
        var (code, flights, flight) = await FlightAsync(stopFails: true);
        PutSession("w1", code, flight.Worktree, SessionState.Idle);

        await flights.RetireFoldedAsync(flight, "PIL9999", Ct);

        Card("w1").ShouldNotBeNull().State.ShouldBe(CardState.Open);
        var folded = _state.Cards().Single(c => c.Kind == FlightCommander.FoldedKind && c.Subject == Verbs.FlightTarget(code));
        folded.Detail.ShouldNotBeNull().ShouldContain("de1-w1 (w1) may still run: its stop failed, its card stays open");
    }

    [Fact]
    public async Task ASuccessfulStopClosesTheCardWithLanded()
    {
        var (code, flights, flight) = await FlightAsync(stopFails: false);
        PutSession("w1", code, flight.Worktree, SessionState.Idle);
        _state.RaiseCard(CardBin.Ring, SessionMessenger.StopUnconfirmedKind, Verbs.SessionTarget("w1"), "earlier");

        await LandAsync(flights, code);

        Card("w1").ShouldNotBeNull().Outcome.ShouldBe($"{code} landed");
        Handled(code).ShouldNotContain("may still run");
    }

    [Fact]
    public async Task AFailedStopOfAnEndedSessionClosesTheCardWithLanded()
    {
        var (code, flights, flight) = await FlightAsync(stopFails: true);
        PutSession("w1", code, flight.Worktree, SessionState.Ended);
        _state.RaiseCard(CardBin.Ring, SessionMessenger.StopUnconfirmedKind, Verbs.SessionTarget("w1"), "earlier");

        await LandAsync(flights, code);

        Card("w1").ShouldNotBeNull().Outcome.ShouldBe($"{code} landed");
        Handled(code).ShouldNotContain("may still run");
    }
}
