using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Whether a refused pilot command's effect is there, read from the node's state (pilot #1353): each verb of the refused-command table, there and
/// not there, and the verbs with no such fact answer that they cannot be checked. In memory, no wait.
/// </summary>
public sealed class RefusedEffectTests
{
    private static readonly DateTimeOffset Began = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    private readonly NodeState _state = new(new InMemoryJournal(), new ManualTime(Began));
    private string? _prState = "OPEN";

    private RefusedEffects Effects() => new(_state, (_, _) => Task.FromResult(_prState));

    private FlightRow Put(string code = "PIL1", Func<FlightRow, FlightRow>? change = null)
    {
        var flight = new FlightRow { Code = code, Repo = "pilot", RepoPath = @"C:\r", Issue = 1, Branch = "flight/" + code.ToLowerInvariant(), Worktree = @"C:\r", Base = "main", State = FlightState.Flying, CreatedAt = Began };
        flight = change?.Invoke(flight) ?? flight;
        _state.PutFlight(flight);
        return flight;
    }

    private static FlightRow Held(FlightRow f, int allowed = 0) => f with
    {
        Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "r", StartedAt = Began, FinishedAt = Began },
        ReviewRounds = [.. Enumerable.Range(1, ReviewConvergence.RunawayRounds).Select(n => new ReviewRound { Round = n, Verdict = ReviewState.Changes, Findings = "f" })],
        ReviewRoundsAllowed = allowed,
    };

    private Task<EffectSeen> SeeAsync(string verb, string target, params string[] args) =>
        Effects().SeeAsync(new PressedCommand(verb, target, args, null), CancellationToken.None);

    [Fact]
    public async Task AllowRoundIsThereOnceTheReviewLoopIsNoLongerHeld()
    {
        Put(change: f => Held(f));
        var not = await SeeAsync(Verbs.AllowRound, "flight/PIL1", "why");
        not.There.ShouldBe(false);
        not.Seen.ShouldNotBeNull().ShouldContain("still held: it has had 8 rounds with changes");

        Put(change: f => Held(f, allowed: 9));
        var there = await SeeAsync(Verbs.AllowRound, "flight/PIL1", "why");
        there.There.ShouldBe(true);
        there.Seen.ShouldContain("no longer held");
    }

    [Fact]
    public async Task MergeIsThereWhenThePrIsMergedOrQueuedOrTheFlightLanded()
    {
        Put(change: f => f with { PullRequestUrl = "https://github.com/SharpAstro/pilot/pull/9" });
        (await SeeAsync(Verbs.Merge, "flight/PIL1", "abc", "squash")).There.ShouldBe(false);

        _prState = "MERGED";
        (await SeeAsync(Verbs.Merge, "flight/PIL1", "abc")).There.ShouldBe(true);

        _prState = "OPEN";
        Put(change: f => f with { PullRequestUrl = "https://github.com/SharpAstro/pilot/pull/9", MergeQueue = "in the merge queue, position 2" });
        var queued = await SeeAsync(Verbs.Merge, "flight/PIL1", "abc");
        queued.There.ShouldBe(true);
        queued.Seen.ShouldContain("merge queue");

        Put(change: f => f with { State = FlightState.Landed });
        (await SeeAsync(Verbs.Merge, "flight/PIL1", "abc")).There.ShouldBe(true);
    }

    [Fact]
    public async Task LandIsThereOnceTheFlightLanded()
    {
        Put();
        (await SeeAsync(Verbs.Land, "flight/PIL1")).There.ShouldBe(false);
        Put(change: f => f with { State = FlightState.Landed });
        (await SeeAsync(Verbs.Land, "flight/PIL1")).There.ShouldBe(true);
    }

    [Fact]
    public async Task FinishLegIsThereOnceTheLegIsNoLongerFlying()
    {
        Put();
        LegRow Leg(LegState s) => new() { Flight = "PIL1", Name = "a", Order = 1, Goal = "g", DoneWhen = "d", State = s };
        _state.PutLeg(Leg(LegState.Flying));
        var not = await SeeAsync(Verbs.FinishLeg, "flight/PIL1/a", "done", "all done");
        not.There.ShouldBe(false);
        not.Seen.ShouldContain("still flying");

        _state.PutLeg(Leg(LegState.Done));
        (await SeeAsync(Verbs.FinishLeg, "flight/PIL1/a", "done")).There.ShouldBe(true);

        (await SeeAsync(Verbs.FinishLeg, "flight/PIL1/zzz", "done")).There.ShouldBeNull();
    }

    [Fact]
    public async Task ClearIsThereOnceTheFlightIsNoLongerHeldForClearance()
    {
        Put(change: f => f with { Holding = new FlightHolding { Since = Began, Reason = "notes" } });
        (await SeeAsync(Verbs.Clear, "flight/PIL1", "minor")).There.ShouldBe(false);
        Put();
        (await SeeAsync(Verbs.Clear, "flight/PIL1", "minor")).There.ShouldBe(true);
    }

    [Fact]
    public async Task GroundAndUngroundAreThereWhenTheFlightIsGroundedOrNot()
    {
        Put();
        (await SeeAsync(Verbs.Ground, "flight/PIL1", "hold")).There.ShouldBe(false);
        (await SeeAsync(Verbs.Unground, "flight/PIL1", "release")).There.ShouldBe(true);

        Put(change: f => f with { OnHold = new FlightOnHold { By = "person", Reason = "owner asked", Since = Began } });
        (await SeeAsync(Verbs.Ground, "flight/PIL1", "hold")).There.ShouldBe(true);
        (await SeeAsync(Verbs.Unground, "flight/PIL1", "release")).There.ShouldBe(false);
    }

    [Fact]
    public async Task ConnectAndDisconnectAreThereWhenTheConnectionIsThereOrGone()
    {
        Put();
        (await SeeAsync(Verbs.Connect, "flight/PIL1", "PIL2")).There.ShouldBe(false);
        (await SeeAsync(Verbs.Disconnect, "flight/PIL1", "PIL2")).There.ShouldBe(true);

        Put(change: f => f with { Connections = ["PIL2"] });
        (await SeeAsync(Verbs.Connect, "flight/PIL1", "PIL2")).There.ShouldBe(true);
        (await SeeAsync(Verbs.Disconnect, "flight/PIL1", "PIL2")).There.ShouldBe(false);
    }

    [Fact]
    public async Task EndSessionIsThereOnceTheSessionEnded()
    {
        (await SeeAsync(Verbs.EndSession, "session/s1", "not needed")).There.ShouldBeNull("a session the node does not hold is not seen");
        var row = new SessionRow
        {
            Id = "s1", SessionId = "s1-0000", Name = "worker", Kind = SessionKind.Background, Cwd = "/w", Role = SessionRole.Planner, ContextTokens = 1, ContextWindow = 2,
            State = SessionState.Working, SpendWeighted = 0, StartedAt = Began,
        };
        _state.PutSession(row);
        (await SeeAsync(Verbs.EndSession, "session/s1", "not needed")).There.ShouldBe(false);
        _state.PutSession(row with { State = SessionState.Ended });
        (await SeeAsync(Verbs.EndSession, "session/s1", "not needed")).There.ShouldBe(true);
    }

    [Theory]
    [InlineData(Verbs.GoAround, "flight/PIL1/a")]
    [InlineData(Verbs.CiBudget, "flight/PIL1")]
    public async Task AVerbWithNoFactInTheNodesStateSaysItCannotBeChecked(string verb, string target)
    {
        Put();
        var seen = await SeeAsync(verb, target, "x");
        seen.There.ShouldBeNull();
        seen.Seen.ShouldContain("cannot check");
        RefusedEffects.CanCheck(new PressedCommand(verb, target, [], null)).ShouldBeFalse();
    }

    [Fact]
    public async Task AFlightThisNodeDoesNotHoldCannotBeSeen()
    {
        (await SeeAsync(Verbs.Land, "flight/SE7")).There.ShouldBeNull();
    }

    [Fact]
    public void TheCommandRoundTripsThroughTheCheckedButtonsArgs()
    {
        var command = new PressedCommand(Verbs.Merge, "flight/PIL1", ["abc", "squash"], "why");
        var decoded = RefusedEffects.Decode(RefusedEffects.Encode(command)).ShouldNotBeNull();
        decoded.Verb.ShouldBe(command.Verb);
        decoded.Target.ShouldBe(command.Target);
        decoded.Args.ShouldBe(command.Args);
        RefusedEffects.Decode(["only-one"]).ShouldBeNull();
    }
}
