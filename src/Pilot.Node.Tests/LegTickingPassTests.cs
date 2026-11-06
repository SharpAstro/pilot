using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A worker that commits its leg's work and ends with steps unticked is finished by the node's ticking pass, not sent round (PIL804): the
/// leg watch starts a ticker once, the ticker ticks what the commits cover and finishes the leg, and no ring card is raised for it.
/// </summary>
public sealed class LegTickingPassTests : LegCommanderTestsBase
{
    private const string Ticker = "du1-tia1-parser-ticker";

    private readonly LegWatch _watch;
    private readonly List<CommandRequest> _said = [];

    public LegTickingPassTests()
    {
        _legs.Models = new RoleModels(null);
        // The watch as the node wires it: the commander's own HEAD reader and ticker start, its commands run by the commander (a say is only recorded).
        _watch = new LegWatch(_state, RunAsync, _clock)
        {
            Models = new RoleModels(null),
            PlaneHead = _legs.PlaneHeadOfAsync,
            StartTicker = _legs.StartTickerAsync,
        };
    }

    private Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (command.Verb != Verbs.Say)
        {
            return _legs.RunAsync(command, cancellationToken);
        }
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    /// <summary>The flight with its first leg flying; the worker has committed its work when <paramref name="commit"/>.</summary>
    private async Task<FlightRow> FlyingAsync(bool commit)
    {
        await FlightWithTwoLegs();
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        var flight = _state.Flights().Single();
        L("parser").StartHead.ShouldNotBeNull();
        if (commit)
        {
            File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
            Git(flight.Worktree, "add", ".");
            Commit(flight.Worktree, "The parser");
        }
        return flight;
    }

    private SessionRow Session(string id, string name, SessionState state, TimeSpan quietFor) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = _state.Flights().Single().Worktree,
        Role = SessionRole.Worker,
        Flight = "TIA1",
        Leg = name.Replace("du1-tia1-", "", StringComparison.Ordinal),
        ContextTokens = 50_000,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 0,
        LastResponseAt = _clock.GetUtcNow() - quietFor,
        StartedAt = _clock.GetUtcNow() - quietFor - TimeSpan.FromMinutes(10),
    };

    // The worker's session as it ended, long enough ago to be past the watch's idle grace.
    private void WorkerEnded() =>
        _state.PutSession(Session(L("parser").WorkerJob!, "du1-tia1-parser", SessionState.Ended, TimeSpan.FromMinutes(10)));

    private Task<CommandResult> AsTicker(string verb, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = "flight/TIA1/parser", Args = args, Who = Ticker }, Ct);

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }

    private async Task LookUntilTickerAsync()
    {
        await _watch.LookAsync(Ct);
        await Until(() => L("parser").TickerJob is not null);
    }

    [Fact]
    public async Task An_ended_worker_with_commits_and_nothing_ticked_gets_a_ticker_that_finishes_the_leg_with_no_ring()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();

        await LookUntilTickerAsync();

        var started = _workers.Started.Single(w => w.Name == Ticker);
        (started.Reason, started.Effort).ShouldBe((SessionStartReasons.LegTicker, "low"));
        started.Brief.ShouldContain("ticker of leg `parser`");
        started.Brief.ShouldContain("The parser");
        L("parser").TickerJob.ShouldBe("job00002");
        L("parser").State.ShouldBe(LegState.Flying);
        _said.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);

        // Looks while its ticker works start no second one and ring for nothing.
        _state.PutSession(Session("job00002", Ticker, SessionState.Working, TimeSpan.Zero));
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);
        _workers.Started.Count(w => w.Name == Ticker).ShouldBe(1);

        // The ticker ticks each step its commits cover and finishes the leg: accepted like the worker's own finish, the check rerun.
        (await AsTicker(Verbs.Check, "1")).Ok.ShouldBeTrue();
        (await AsTicker(Verbs.Check, "2")).Ok.ShouldBeTrue();
        var finished = await AsTicker(Verbs.FinishLeg, "done", "ticked by the ticking pass: The parser covers both steps");

        finished.Ok.ShouldBeTrue(finished.Error);
        L("parser").State.ShouldBe(LegState.Done);
        _checks.Runs.ShouldNotBeEmpty();
        // No ring was ever raised about the leg, and its ticker-started line closed with it.
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.TickerStartedKind);
    }

    [Fact]
    public async Task An_ended_worker_with_no_new_commits_still_rings_worker_idle_after_its_nudges()
    {
        await FlyingAsync(commit: false);
        WorkerEnded();

        // Two nudges, then the ring, as before the ticking pass.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await _watch.LookAsync(Ct);
        }

        _said.Count.ShouldBe(2);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegWatch.Idle);
        card.Bin.ShouldBe(CardBin.Ring);
        L("parser").TickerJob.ShouldBeNull();
        _workers.Started.ShouldNotContain(w => w.Name == Ticker);
    }

    [Fact]
    public async Task A_ticker_that_finishes_failed_leaves_the_leg_failed_ring()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        (await AsTicker(Verbs.Check, "1")).Ok.ShouldBeTrue();

        var failed = await AsTicker(Verbs.FinishLeg, "failed", "step 2 is not covered by any commit");

        failed.Ok.ShouldBeTrue(failed.Error);
        L("parser").State.ShouldBe(LegState.Failed);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed");
        (card.Bin, card.Title).ShouldBe((CardBin.Ring, "TIA1/parser stopped: step 2 is not covered by any commit"));
    }

    [Fact]
    public async Task A_ticker_that_ends_without_finishing_rings_once_and_is_not_started_again()
    {
        await FlyingAsync(commit: true);
        WorkerEnded();
        await LookUntilTickerAsync();
        _state.PutSession(Session(L("parser").TickerJob!, Ticker, SessionState.Ended, TimeSpan.FromMinutes(5)));

        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await _watch.LookAsync(Ct);

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegWatch.Idle);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("ticker stopped without finishing the leg: its session ended");
        _workers.Started.Count(w => w.Name == Ticker).ShouldBe(1);
        _said.ShouldBeEmpty();
    }
}
