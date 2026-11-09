using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #799: a card rings only when the person must act, checked against the facts first. One golden test per row of the issue's
/// table, named after its row: the same inputs that rang before now raise no ring (or the right action). Git is the in-memory
/// <c>FakeRepoGit</c> except in the three tests that compare squash-merge trees (commit-tree, merge-tree, <c>^{tree}</c>), which ask
/// for real git; GitHub is the fake.
/// </summary>
public sealed class FalseRingTests : ReviewTestsBase
{
    private void AutopilotOn() =>
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    /// <summary>A leg of <paramref name="flight"/> that flew and is not finished: the PR merged while it was.</summary>
    private void LegNotDone(FlightRow flight) =>
        _state.PutLeg(new LegRow { Flight = flight.Code, Name = "render", Order = 2, Goal = "Draw", DoneWhen = "It draws", State = LegState.Flying, StartedAt = DateTimeOffset.UtcNow });

    /// <summary>
    /// What a squash merge leaves in the base: one commit on top of it with <paramref name="tree"/> as its tree, not an ancestor of the
    /// plane and with no ancestor in common with its commits.
    /// </summary>
    private static string SquashOf(FlightRow flight, string tree) =>
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit-tree", tree, "-p", flight.Base.Length > 0 ? flight.Base : "origin/main", "-m", "squash of the flight");

    [Fact]
    [Trait("Category", "Slow")]
    public async Task MergedWithTheLegsTreeLandsNotRings_TIA2000()
    {
        RealGit(); // the squash's tree is built with commit-tree: git's own object model is what the check compares
        var (flight, number) = await Passed("2000");
        LegNotDone(flight);
        // The squash merge's tree is the plane's head tree: the leg's commit was in it, so nothing is left behind.
        var merge = SquashOf(flight, Git(flight.Worktree, "rev-parse", "HEAD^{tree}"));
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = merge };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Landed);
        Open().ShouldNotContain(c => c.Kind == FlightCommander.MergedUnderLegKind || c.Kind == "not-landed");
        _state.Cards().ShouldNotContain(c => c.Kind == FlightCommander.MergedUnderLegKind);
        var leg = _state.Legs(flight.Code).Single(l => l.Name == "render");
        leg.State.ShouldBe(LegState.Done);
        leg.Report.ShouldNotBeNull().ShouldContain($"#{number}");
        _state.Cards().ShouldContain(c => c.Kind == "landed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeWhoseTreeLacksThePlanesWorkStillRaisesItsCardAndStaysFlying_TIA2000()
    {
        RealGit(); // trees are compared as git builds them
        var (flight, _) = await Passed("2001");
        LegNotDone(flight);
        // The merge holds the base's tree alone: the plane's work is not in it, so the new work goes to a new PR and the node rings.
        var merge = SquashOf(flight, Git(flight.Worktree, "rev-parse", $"{(flight.Base.Length > 0 ? flight.Base : "origin/main")}^{{tree}}"));
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = merge };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Flying);
        Open().Single(c => c.Kind == FlightCommander.MergedUnderLegKind).Bin.ShouldBe(CardBin.ForYou);
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeWhoseTreeCannotBeReadKeepsTheRingAndSaysWhy_TIA2000()
    {
        var (flight, _) = await Passed("2002");
        LegNotDone(flight);
        // GitHub names no merge commit: the trees cannot be compared, which is not "equal".
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = "" };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Flying);
        Open().Single(c => c.Kind == FlightCommander.MergedUnderLegKind).Detail.ShouldNotBeNull().ShouldContain("could not be compared");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeRefusedBecauseItIsMergedLandsNotRings_c17100()
    {
        var (flight, number) = await Passed("2037");
        _github.Checks = ChoiceStatus.Passing;
        AutopilotOn();
        // The PR reads open at the look and merged when the autopilot's merge asks: GitHub merged it in between.
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && _github.Pr is { State: "OPEN" } open)
            {
                _github.Pr = open with { State = "MERGED" };
            }
            return Task.CompletedTask;
        };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Landed);
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.AutopilotMergeRefused);
        Open().ShouldNotContain(c => c.Choices.Any(ch => ch.Verb == Verbs.Merge));
        Open().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _github.Merged.ShouldBeEmpty();
        number.ShouldBe(_github.Pr!.Number);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACardsMergeRefusedBecauseItIsMergedLandsNotRings_c17100()
    {
        var (flight, _) = await Passed("2038");
        // The person's Merge press, on a card whose PR GitHub has merged since it was read.
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && _github.Pr is { State: "OPEN" } open)
            {
                _github.Pr = open with { State = "MERGED" };
            }
            return Task.CompletedTask;
        };

        var result = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        result.Ok.ShouldBeTrue(result.Error);
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        Open().ShouldNotContain(c => c.Bin == CardBin.Ring);
    }

    // Rules 2, 3 and 4 (#799): a leg card is for a cause still open, and one that needs the person.

    private readonly ManualTime _watchTime = new(DateTimeOffset.UtcNow);

    /// <summary>A watch on the node's state, its says delivered and its commands run by the real commander.</summary>
    private LegWatch Watch(List<CommandRequest>? said = null) =>
        new(_state, (command, token) =>
        {
            if (command.Verb != Verbs.Say)
            {
                return _legs.RunAsync(command, token);
            }
            said?.Add(command);
            return Task.FromResult(_state.Complete(command, null));
        }, _watchTime) { Pilot = "pilot" };

    /// <summary>A leg started by the node, with a commit made and its step ticked: its work is done, its finish not said.</summary>
    private async Task<(FlightRow Flight, LegRow Leg)> FlyingLeg(string issue, bool commit = true)
    {
        var flight = await NewFlight(issue, "render");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "render"), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        if (commit)
        {
            File.WriteAllText(Path.Combine(flight.Worktree, $"render-{Guid.NewGuid():N}.txt"), "work\n");
            Git(flight.Worktree, "add", ".");
            Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "render work");
        }
        var leg = _state.Legs(flight.Code).Single(l => l.Name == "render");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "render"), leg.Worker!, "1")).Ok.ShouldBeTrue();
        return (flight, _state.Legs(flight.Code).Single(l => l.Name == "render"));
    }

    private void WorkerIs(LegRow leg, SessionState state, string? waitingFor = null) =>
        _state.PutSession(new SessionRow
        {
            Id = leg.WorkerJob!, SessionId = leg.WorkerJob + "-0000-4000-8000-000000000000", Name = leg.Worker!, Kind = SessionKind.Background, Cwd = "/work",
            Role = SessionRole.Worker, Flight = leg.Flight, Leg = leg.Name, ContextTokens = 1000, ContextWindow = 200_000, State = state, WaitingFor = waitingFor, SpendWeighted = 0,
            LastResponseAt = _watchTime.GetUtcNow(), StartedAt = _watchTime.GetUtcNow(),
        });

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALegOfALandedFlightWhoseWorkerIdlesRaisesNoCardAndLandingClosedItsOpenOnes_c3651()
    {
        var (flight, leg) = await FlyingLeg("3651", commit: false);
        var subject = Verbs.LegTarget(flight.Code, leg.Name);
        // A leg card open before the landing: the landing closes it, naming itself.
        var stale = _state.RaiseCard(CardBin.Ring, LegWatch.Idle, subject, $"{flight.Code}/{leg.Name}'s worker has been idle 8 min");
        // Nothing of its own in the plane (its work went in through another PR), so the landing is the person's plain land.
        (await Run(Verbs.Land, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        _state.Cards().Single(c => c.Id == stale.Id).Outcome.ShouldNotBeNull().ShouldContain("landed");
        WorkerIs(leg, SessionState.Idle);
        var watch = Watch();

        // The leg is still flying in the row, and its worker idles: nothing a person could act on.
        foreach (var _ in Enumerable.Range(0, 6))
        {
            _watchTime.Advance(TimeSpan.FromMinutes(4));
            await watch.LookAsync(Ct);
        }

        // The running session of a landed flight has its own stray-session card (a different cause, with a Stop choice): no card is about the leg.
        Open().ShouldNotContain(c => c.Subject.StartsWith(subject, StringComparison.Ordinal));
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Idle && c.Id != stale.Id);
        // Not a leg card by any other raiser either: the node refuses a ring for a leg of a landed flight.
        _state.RaiseCard(CardBin.Ring, "leg-failed", subject, "late").State.ShouldBe(CardState.Closed);
        Open().ShouldNotContain(c => c.Subject.StartsWith(subject, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWorkerWaitingOnlyOnTheNodesCheckWhileItsLegRechecksRaisesNoCard_c17099()
    {
        var (flight, leg) = await FlyingLeg("17099");
        _state.PutLeg(leg with { Recheck = new LegRecheck { Check = "dotnet test", Since = _watchTime.GetUtcNow() } });
        WorkerIs(leg, SessionState.Waiting, "the node's check to pass; the work is committed and pushed");
        var watch = Watch();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            _watchTime.Advance(TimeSpan.FromMinutes(5));
            await watch.LookAsync(Ct);
        }

        Open().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
        _state.Cards().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
        // The running check finishes the leg: nothing here finished it, nor failed it.
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWorkerSayingItsWorkIsCommittedAndDoneIsFinishedByTheNodeNotRung_c17099()
    {
        var (flight, leg) = await FlyingLeg("17098");
        WorkerIs(leg, SessionState.Waiting, "nothing: the work is committed and the leg is done");
        var watch = Watch();

        await watch.LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == LegWatch.Waiting);
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Done);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWorkerWithARealQuestionForThePersonStillRings_c17099()
    {
        var (_, leg) = await FlyingLeg("17097");
        WorkerIs(leg, SessionState.Waiting, "Should I delete the old render path, or keep it behind a flag?");

        await Watch().LookAsync(Ct);

        var card = Open().Single(c => c.Kind == LegWatch.Waiting);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("Should I delete the old render path");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALegRetiredOnTheControllersWordEndsRetiredWithNoCardAndNoChoices_c3623()
    {
        var (flight, leg) = await FlyingLeg("3623");

        var result = await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "render"), leg.Worker!, "retired", "the controller told me to stop: the work moved to another flight");

        result.Ok.ShouldBeTrue(result.Error);
        var ended = _state.Legs(flight.Code).Single(l => l.Name == "render");
        ended.State.ShouldBe(LegState.Done);
        ended.Report.ShouldNotBeNull().ShouldStartWith("retired:");
        _state.Cards().ShouldNotContain(c => c.Kind == "leg-failed");
        Open().ShouldNotContain(c => c.Subject.StartsWith(Verbs.LegTarget(flight.Code, "render"), StringComparison.Ordinal));
        Open().SelectMany(c => c.Choices).ShouldNotContain(c => c.Verb == Verbs.GoAround || c.Args.Contains("accepted"));
        _state.Cards().Single(c => c.Kind == LegCommander.RetiredKind).Bin.ShouldBe(CardBin.Handled);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARetiredFinishNeedsAReasonAndAFlyingLeg_c3623()
    {
        var (flight, leg) = await FlyingLeg("3624");

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "render"), leg.Worker!, "retired")).Ok.ShouldBeFalse();
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Flying);
    }

    // Rules 5 and 6 (#799): no meta-card for an unanswered card, and no choice the node would refuse.

    [Fact]
    public async Task AStaleCardIsRungAgainInPlaceNotByACardAboutIt_StillRinging()
    {
        var card = _state.RaiseCard(CardBin.Ring, "worker-waiting", Verbs.FlightTarget("TIA9"), "TIA9's worker asks which model to use", null,
            [new CardChoice { Label = "Answer in the chat", Verb = Verbs.Say, Target = "session/a1b2c3d4" }]);
        var later = new ManualTime(card.At + TimeSpan.FromHours(1.5));
        var watch = new StaleWatch(_state, later);

        await watch.TickAsync(Ct);
        await watch.TickAsync(Ct);

        // The same card, id and all, rung again (at the node state's own time): it sorts first, and its age (At) stays true.
        var rung = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        rung.Id.ShouldBe(card.Id);
        rung.RungAgainAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(card.At);
        rung.At.ShouldBe(card.At);
        rung.Choices.ShouldBe(card.Choices);
        _state.Cards().ShouldNotContain(c => c.Kind == "stale-card-reminder");
        _state.Cards().Count(c => c.Kind == StaleWatch.ActedKind && c.Title.Contains(card.Id, StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ARungAgainCardSortsBeforeACardRaisedAfterItAndSaysItWasRungAgain_StillRinging()
    {
        var first = _state.RaiseCard(CardBin.Ring, "worker-waiting", Verbs.FlightTarget("TIA9"), "TIA9's worker asks which model to use");
        await Task.Delay(5, Ct);
        var second = _state.RaiseCard(CardBin.Ring, "worker-waiting", Verbs.FlightTarget("TIA8"), "TIA8's worker asks which branch to use");
        _state.Cards(openOnly: true).First().Id.ShouldBe(second.Id);

        _state.RingAgain(first.Id).ShouldBeTrue();

        _state.Cards(openOnly: true).First().Id.ShouldBe(first.Id);
        _state.RingAgain("c999").ShouldBeFalse();
    }

    private FlightRow Held(string code, params string[] connections)
    {
        var flight = new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = Repo, Issue = 9, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = Path.Combine(_root, "wt", code),
            Base = "origin/main", State = connections.Length > 0 ? FlightState.Held : FlightState.Flying, Connections = connections, CreatedAt = DateTimeOffset.UtcNow,
        };
        _state.PutFlight(flight);
        _state.PutLeg(new LegRow { Flight = code, Name = "parser", Order = 1, Goal = "Parse", DoneWhen = "It parses", State = LegState.Planned });
        return flight;
    }

    [Fact]
    public async Task APlannedCardForAHeldFlightOffersNoStartAndOffersItOnceTheHoldLifts_Held()
    {
        var flight = Held("TIA9", "PIL1");

        (await _legs.RunAsync(new CommandRequest { Verb = Verbs.Planned, Target = Verbs.FlightTarget(flight.Code), Args = ["parser first"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.PlannedKind);
        card.Choices.ShouldBeEmpty();
        card.Detail.ShouldNotBeNull().ShouldContain("Waiting for PIL1");
        // The node would refuse the start while the flight is held; the press is never offered.
        (await _legs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget(flight.Code, "parser"), Who = "tests" }, Ct)).Ok.ShouldBeFalse();

        // The flight it waits for lands: the same card now offers Start, with no waiting line.
        _state.PutFlight(flight with { State = FlightState.Flying, Connections = [] });

        var lifted = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.PlannedKind);
        lifted.Id.ShouldBe(card.Id);
        var start = lifted.Choices.ShouldHaveSingleItem();
        (start.Label, start.Verb, start.Target).ShouldBe(("Start parser", Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser")));
        lifted.Detail.ShouldNotBeNull().ShouldNotContain("Waiting for");
    }

    [Fact]
    public async Task APlannedCardForAFlyingFlightStillOffersStart_Held()
    {
        var flight = Held("TIA9");

        (await _legs.RunAsync(new CommandRequest { Verb = Verbs.Planned, Target = Verbs.FlightTarget(flight.Code), Args = ["parser first"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.PlannedKind).Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.StartLeg);
    }

    // The review's not-blocking notes (#799), each pinned.

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWorkerRetiringItsOwnLegMustNameWhoseWordStoppedIt_c3623()
    {
        var (flight, leg) = await FlyingLeg("3625");

        // A reason that names no one is a worker ending its own leg with no check: refused, the leg flies on.
        var refused = await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "render"), leg.Worker!, "retired", "I do not feel like finishing");

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("whose word");
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Flying);
        // The controller retiring it needs no such words: it is their word.
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "render"), "controller", "retired", "moved to another flight")).Ok.ShouldBeTrue();
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Done);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeOnAMainThatMovedAfterThePlaneLastTookItStillHoldsThePlanesWork_TIA2000()
    {
        RealGit(); // merge-tree --write-tree computes the squash's tree: that is git itself under test
        var (flight, _) = await Passed("2003");
        LegNotDone(flight);
        // Main moved (a file the plane never touched) after the plane's last take of it: the squash holds the plane's work and main's.
        MainMoves();
        Git(flight.Worktree, "fetch", "-q", "origin");
        var tree = Git(flight.Worktree, "merge-tree", "--write-tree", "HEAD", "origin/main");
        var merge = Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit-tree", tree, "-p", "origin/main", "-m", "squash of the flight");
        tree.ShouldNotBe(Git(flight.Worktree, "rev-parse", "HEAD^{tree}"));
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = merge };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Landed);
        Open().ShouldNotContain(c => c.Kind == FlightCommander.MergedUnderLegKind);
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Done);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAutopilotMergeRefusedOnAClosedPrIsAForYouCardWithNothingToPress_c17100()
    {
        var (flight, _) = await Passed("2039");
        _github.Checks = ChoiceStatus.Passing;
        AutopilotOn();
        // The PR was closed without a merge between the look and the autopilot's merge: there is nothing to merge.
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && _github.Pr is { State: "OPEN" } open)
            {
                _github.Pr = open with { State = "CLOSED" };
            }
            return Task.CompletedTask;
        };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldNotBe(FlightState.Landed);
        var card = _state.Cards().Single(c => c.Kind == PrChecks.AutopilotMergeRefused);
        card.Choices.ShouldBeEmpty();
        card.Bin.ShouldBe(CardBin.ForYou);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, "waiting for the node's check", true)]
    [InlineData(true, "Should I keep the flag?", false)]
    [InlineData(false, "Should I keep the flag? Everything is committed.", false)]
    [InlineData(false, "the work is committed and the leg is done", true)]
    [InlineData(false, "Waiting for your answer, I committed a draft", false)]
    public void WaitsOnNodeIsNarrow(bool rechecking, string? waitingFor, bool expected)
    {
        var leg = new LegRow
        {
            Flight = "TIA9", Name = "render", Order = 1, Goal = "Draw", DoneWhen = "It draws", State = LegState.Flying,
            Recheck = rechecking ? new LegRecheck { Check = "dotnet test", Since = DateTimeOffset.UtcNow } : null,
        };
        var worker = new SessionRow
        {
            Id = "j1", SessionId = "j1-0000-4000-8000-000000000000", Name = "w", Kind = SessionKind.Background, Cwd = "/work", Role = SessionRole.Worker,
            ContextTokens = 1, ContextWindow = 2, State = SessionState.Waiting, WaitingFor = waitingFor, SpendWeighted = 0, LastResponseAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow,
        };

        LegWatch.WaitsOnNode(leg, worker).ShouldBe(expected);
    }
}
