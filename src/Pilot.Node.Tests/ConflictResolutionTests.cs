using Pilot.Contracts;
using Pilot.Node;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR's conflict with its base is the node's own work, said to no one (PIL846): from each path that finds it (the PR sweep, a queue drop,
/// the merge card, the merge command, a PR open, a PR recheck) a worker is told to merge the base, or the last leg goes round with a brief that
/// names the conflicting files and the commits on the base that touch them, and no card of any kind is raised.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ConflictResolutionTests : ReviewTestsBase
{
    /// <summary>The commit subject main gets where it moves the file the leg also changed: the brief names it as a base commit that touches the file.</summary>
    private const string BaseCommit = "README rewritten on main";

    /// <summary>A flight whose leg changed README.md, then main changed it too: merging the base conflicts. Past its leg, its PR is open and reviewed as asked.</summary>
    private async Task<FlightRow> Conflicted(string issue, bool openPr = true, string? review = "pass")
    {
        var flight = await NewFlight(issue, "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        if (openPr)
        {
            await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
            if (review is not null)
            {
                (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), review, "fine")).Ok.ShouldBeTrue();
            }
            _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };
        }
        MainRewritesTheReadme();
        return F(flight.Code);
    }

    private void MainRewritesTheReadme()
    {
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", BaseCommit);
        Git(Repo, "push", "-q", "origin", "main");
    }

    private void NoCardAboutIt()
    {
        Open().ShouldNotContain(c => c.Bin == CardBin.Ring || c.Bin == CardBin.ForYou);
        _state.Cards().ShouldNotContain(c => c.Kind.Contains("conflict", StringComparison.OrdinalIgnoreCase) || c.Title.Contains("conflicts with its base", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The last leg went round once, by the node, and is no review's round; one worker started, told the files, the base's commits and the rule.</summary>
    private void ShouldHaveSentOneWorker(string code, int startedBefore)
    {
        var leg = _state.Legs(code).ShouldHaveSingleItem();
        leg.State.ShouldBe(LegState.Flying);
        (leg.GoArounds, leg.ReviewGoArounds).ShouldBe((1, 0));
        leg.GoAroundReasons.Single().By.ShouldBe(LegWatch.Who);
        _workers.Started.Count.ShouldBe(startedBefore + 1);
        var brief = _workers.Started.Last().Brief;
        File.WriteAllText(@"C:\Users\SebastianGodelet\.claude\jobs\9ee28370\tmp\brief.txt", brief);
        brief.ShouldContain("the PR conflicts with its base");
        brief.ShouldContain("conflicts in 1 file");
        brief.ShouldContain("- README.md: this branch added 1 and removed 0 lines; the base added 1 and removed 1 lines");
        brief.ShouldContain(BaseCommit);
        brief.ShouldContain("merge base");
        brief.ShouldContain("git merge --no-edit");
        brief.ShouldContain("Keep both sides' work");
        brief.ShouldContain("higher of the two versions");
        brief.ShouldContain("rerun the check");
        brief.ShouldContain("never --force");
        NoCardAboutIt();
    }

    private void LiveSession(FlightRow flight, LegRow leg) =>
        _state.PutSession(new SessionRow
        {
            Id = leg.WorkerJob!, SessionId = leg.WorkerJob + "-0000-4000-8000-000000000000", Name = leg.Worker!, Kind = SessionKind.Background, Cwd = flight.Worktree,
            Role = SessionRole.Worker, Flight = flight.Code, Leg = leg.Name, ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0, State = SessionState.Idle, StartedAt = DateTimeOffset.UtcNow, LastResponseAt = DateTimeOffset.UtcNow,
        });

    [Fact]
    public async Task ThePrSweepSendsTheLastLegRoundWithTheBriefOnceAHeadAndRaisesNoCard()
    {
        var flight = await Conflicted("41");
        var started = _workers.Started.Count;
        var checks = new PrChecks(_state, _github, TimeProvider.System, legs: _legs);

        await checks.LookAsync(Ct);

        ShouldHaveSentOneWorker(flight.Code, started);
        // A passed review is turned to changes as for any done leg sent round.
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        await checks.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(started + 1);
    }

    [Fact]
    public async Task AConflictRoundIsNotHeldByTheReviewLoopOrCountedAgainstItsCap()
    {
        var flight = await Conflicted("42");
        var started = _workers.Started.Count;
        // Past the cap of review go-arounds, where the node sends the leg round no more for a review's findings.
        _state.PutLeg(_state.Legs(flight.Code).Single() with { GoArounds = LegCommander.MaxReviewGoArounds, ReviewGoArounds = LegCommander.MaxReviewGoArounds });

        await new PrChecks(_state, _github, TimeProvider.System, legs: _legs).LookAsync(Ct);

        var leg = _state.Legs(flight.Code).Single();
        leg.State.ShouldBe(LegState.Flying);
        leg.ReviewGoArounds.ShouldBe(LegCommander.MaxReviewGoArounds);
        leg.GoArounds.ShouldBe(LegCommander.MaxReviewGoArounds + 1);
        _workers.Started.Count.ShouldBe(started + 1);
        NoCardAboutIt();
    }

    [Fact]
    public async Task AQueueDropForAConflictSendsTheLastLegRoundWithTheBriefAndRaisesNoCard()
    {
        var flight = await Conflicted("43");
        _state.CloseCard(Open().ShouldHaveSingleItem().Id, "done", "controller").ShouldBeTrue();
        var started = _workers.Started.Count;
        _github.Queue = new MergeQueueState("OPEN", _github.Pr!.Head, Queued: false, RemovedAt: DateTimeOffset.UtcNow.AddSeconds(-5), RemovedReason: "merge_conflict");

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        ShouldHaveSentOneWorker(flight.Code, started);
    }

    [Fact]
    public async Task AMergeCardOfAPrGitHubShowsConflictingSendsTheLastLegRoundAndRaisesNoCard()
    {
        var flight = await Conflicted("44");
        Open().ShouldContain(c => c.Kind == LegCommander.PrReady);
        var started = _workers.Started.Count;
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        _github.Merge = new MergeState(MergeKinds.Conflicts, 3, 3);

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        // The go-around closed the card that offered the merge: nothing is left open to say the PR conflicts.
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);
        ShouldHaveSentOneWorker(flight.Code, started);
    }

    [Fact]
    public async Task AMergeRefusedAsConflictingSendsTheLastLegRoundAndTheAnswerSaysAWorkerHasIt()
    {
        var flight = await Conflicted("45");
        var started = _workers.Started.Count;
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        _github.MergeRefuses = "the PR conflicts with its base";

        var result = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain(LegCommander.WorkerResolves);
        result.Error.ShouldNotContain("watchtower");
        ShouldHaveSentOneWorker(flight.Code, started);
    }

    [Fact]
    public async Task APrOpenThatConflictsSendsTheLastLegRoundWithTheBriefAndRaisesNoCard()
    {
        var flight = await Conflicted("46", openPr: false);
        var started = _workers.Started.Count;

        var result = await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "alice");

        result.Ok.ShouldBeFalse();
        ShouldHaveSentOneWorker(flight.Code, started);
        F(flight.Code).PullRequest.ShouldBeNull();
    }

    [Fact]
    public async Task APrOpenAtTheSameBaseHeadAfterTheWorkerFinishedWithoutResolvingItSendsNoSecondWorkerAndRingsNoOne()
    {
        var flight = await Conflicted("47", openPr: false);
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "alice");
        var started = _workers.Started.Count;
        // The leg is put done as if its worker had finished without resolving it, to look at the open once more.
        _state.PutLeg(_state.Legs(flight.Code).Single() with { State = LegState.Done });

        var result = await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "alice");

        result.Ok.ShouldBeFalse();
        _workers.Started.Count.ShouldBe(started);
        _state.Legs(flight.Code).Single().GoArounds.ShouldBe(1);
        NoCardAboutIt();
    }

    [Fact]
    public async Task AWorkerThatStopsOnTheConflictIsTheLegFailedCardsToSayAndNoConflictCardFollows()
    {
        var flight = await Conflicted("48", openPr: false);
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "alice");
        var worker = _state.Legs(flight.Code).Single().Worker!;
        var started = _workers.Started.Count;

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "failed", "could not resolve it")).Error.ShouldBeNull();

        Open().Where(c => c.Bin == CardBin.Ring).ShouldHaveSingleItem().Kind.ShouldBe("leg-failed");
        _state.Cards().ShouldNotContain(c => c.Kind.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        _workers.Started.Count.ShouldBe(started);
    }

    [Fact]
    public async Task ALegFlyingOnThePlaneIsToldToMergeTheBaseAndNoSecondWorkerStarts()
    {
        var flight = await Conflicted("49", review: "changes");
        // The review asked for changes and a worker flies them: the PR conflicts meanwhile.
        (await Run(Verbs.GoAround, Verbs.LegTarget(flight.Code, "parser"), "alice", "fix what the review found")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        var leg = _state.Legs(flight.Code).Single();
        leg.State.ShouldBe(LegState.Flying);
        LiveSession(flight, leg);
        var said = new List<(string Id, string Text)>();
        _legs.Say = (id, text, _) =>
        {
            said.Add((id, text));
            return Task.FromResult(true);
        };
        var started = _workers.Started.Count;
        var checks = new PrChecks(_state, _github, TimeProvider.System, legs: _legs);

        await checks.LookAsync(Ct);

        var told = said.ShouldHaveSingleItem();
        told.Id.ShouldBe(leg.WorkerJob);
        told.Text.ShouldContain("the PR conflicts with its base");
        told.Text.ShouldContain("- README.md:");
        told.Text.ShouldContain(BaseCommit);
        told.Text.ShouldContain("git merge --no-edit");
        told.Text.ShouldContain("Merge the base into your plane");
        _workers.Started.Count.ShouldBe(started);
        _state.Legs(flight.Code).Single().GoArounds.ShouldBe(leg.GoArounds);
        NoCardAboutIt();
        // Once for the base head: asked again, nothing more is said, by the sweep or by the resolver itself.
        await checks.LookAsync(Ct);
        (await _legs.ResolveConflictAsync(flight.Code, "again", Ct)).ShouldBeNull();
        said.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheResolverPutsAConflictToAWorkerOnceForEachHeadOfTheBase()
    {
        var flight = await Conflicted("50", openPr: false);
        Git(flight.Worktree, "fetch", "-q", "origin");
        var started = _workers.Started.Count;

        (await _legs.ResolveConflictAsync(flight.Code, "merging origin/main conflicts", Ct)).ShouldBeNull();
        (await _legs.ResolveConflictAsync(flight.Code, "merging origin/main conflicts", Ct)).ShouldBeNull();

        _workers.Started.Count.ShouldBe(started + 1);
        _state.Legs(flight.Code).Single().GoArounds.ShouldBe(1);
        // The base moves on: its next head is put again, to the worker flying by now.
        var leg = _state.Legs(flight.Code).Single();
        LiveSession(flight, leg);
        var said = new List<string>();
        _legs.Say = (_, text, _) =>
        {
            said.Add(text);
            return Task.FromResult(true);
        };
        File.WriteAllText(Path.Combine(Repo, "later.txt"), "later\n");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-qm", "later on main");
        Git(Repo, "push", "-q", "origin", "main");
        Git(flight.Worktree, "fetch", "-q", "origin");

        (await _legs.ResolveConflictAsync(flight.Code, "merging origin/main conflicts", Ct)).ShouldBeNull();
        (await _legs.ResolveConflictAsync(flight.Code, "merging origin/main conflicts", Ct)).ShouldBeNull();

        said.Count.ShouldBe(1);
        _workers.Started.Count.ShouldBe(started + 1);
    }

    [Fact]
    public async Task AFlyingLegWhoseWorkerIsNotLiveIsNotToldAndNothingRings()
    {
        var flight = await Conflicted("51", openPr: false);
        // A done leg is put flying again by a go-around (a start is refused for it).
        (await Run(Verbs.GoAround, Verbs.LegTarget(flight.Code, "parser"), "controller", "redo it")).Error.ShouldBeNull();
        var started = _workers.Started.Count;
        var told = false;
        _legs.Say = (_, _, _) =>
        {
            told = true;
            return Task.FromResult(true);
        };

        var why = await _legs.ResolveConflictAsync(flight.Code, "merging origin/main conflicts", Ct);

        told.ShouldBeFalse();
        why.ShouldNotBeNull().ShouldContain("flying");
        _workers.Started.Count.ShouldBe(started);
        NoCardAboutIt();
    }
}

/// <summary>The PR recheck's path of the same: a flight's PR it finds conflicting is a worker's, and its Handled line says so.</summary>
[Trait("Category", "Slow")]
public sealed class ConflictResolutionRecheckTests : PrRecheckTestsBase
{
    [Fact]
    public async Task APrRecheckOfAConflictingFlightPrSendsTheLastLegRoundWithTheBriefAndRaisesNoCard()
    {
        var flight = await PassedFlightAsync(readme: "theirs\n");
        MainMoves("README.md", "ours\n");
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };
        var started = _workers.Started.Count;

        await LookAsync();

        _workers.Started.Count.ShouldBe(started + 1);
        var brief = _workers.Started.Last().Brief;
        brief.ShouldContain("- README.md:");
        brief.ShouldContain("later on main");
        brief.ShouldContain("git merge --no-edit");
        Handled().ShouldHaveSingleItem().ShouldContain(LegCommander.WorkerResolves);
        var leg = _state.Legs(flight.Code).Single();
        (leg.State, leg.GoArounds, leg.ReviewGoArounds).ShouldBe((LegState.Flying, 1, 0));
        _state.Cards().ShouldNotContain(c => c.Kind.Contains("conflict", StringComparison.OrdinalIgnoreCase) || c.Bin == CardBin.Ring);
    }
}
