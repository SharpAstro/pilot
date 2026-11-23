using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight PR that is mergeable but not rebaseable on a base that merges by rebase (pilot #1299): between legs the node reshapes the
/// branch at its next look and nothing rings for the person; with a leg flying nothing is pushed and the flight says it is handled;
/// a real conflict keeps the conflict path; a squash-only base gets no action. In memory: FakeRepoGit and FakeGitHub.
/// </summary>
public sealed class RebaseBlockedTests : ReviewTestsBase
{
    private static void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-a", "-m", message);

    /// <summary>A passed flight whose branch holds a merge of main (which moved on another file), pushed, with GitHub calling its PR not rebaseable.</summary>
    private async Task<(FlightRow Flight, string Old)> BlockedFlight(string issue, string baseMethod = "REBASE")
    {
        _github.MergeQueueMethod = baseMethod;
        _github.Queues = true;
        _github.CanBeRebased = false;
        var (flight, _) = await Passed(issue);
        File.WriteAllText(Path.Combine(Repo, "base.txt"), "base\n");
        Git(Repo, "add", "base.txt");
        Commit(Repo, "base moves");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        File.WriteAllText(Path.Combine(plane, "mine.txt"), "mine\n");
        Git(plane, "add", ".");
        Commit(plane, "mine");
        Git(plane, "fetch", "-q", "origin");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "--no-ff", "-q", "-m", "take main", "origin/main");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var old = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = old } : null });
        _github.Pr = _github.Pr! with { Head = old };
        // The ready card is the merge's own path; this look is the node's, which acts with none open.
        _state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "test", LegCommander.PrReady);
        return (F(flight.Code), old);
    }

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, legs: _legs);

    private static int MergesOver(string plane) =>
        int.Parse(Git(plane, "rev-list", "--merges", "--count", "origin/main..HEAD"), System.Globalization.CultureInfo.InvariantCulture);

    private void NoCardForThePerson() =>
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring || c.Bin == CardBin.ForYou && c.Kind != LegCommander.PrReady);

    [Fact]
    public async Task BetweenLegsTheNodeReshapesTheBranchWithinOneLookAndNothingRingsForThePerson()
    {
        var (flight, old) = await BlockedFlight("1");
        var plane = flight.Worktree;
        MergesOver(plane).ShouldBe(1);

        await Checks().LookAsync(Ct);

        var shaped = Git(plane, "rev-parse", "HEAD");
        shaped.ShouldNotBe(old);
        MergesOver(plane).ShouldBe(0);
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(shaped);
        Git(plane, "rev-parse", $"refs/pilot/laid/{flight.Code}").ShouldBe(old);
        F(flight.Code).Review!.ReviewedHead.ShouldBe(shaped);
        F(flight.Code).RebaseOnly.ShouldBeNull();
        // GitHub reads the pushed head as replayable now.
        _github.CanBeRebased = true;
        _github.Pr = _github.Pr! with { Head = shaped };
        (await _github.PullRequestAsync(Repo, flight.Branch, Ct)).ShouldNotBeNull().Rebaseable.ShouldBe(true);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.ShapedKind && c.Bin == CardBin.Handled);
        NoCardForThePerson();
        // Once a head: the next look at the same head does nothing more.
        await Checks().LookAsync(Ct);
        Git(plane, "rev-parse", "HEAD").ShouldBe(shaped);
    }

    [Fact]
    public async Task WithALegFlyingNothingIsPushedAndTheFlightAndAHandledCardSayItLandsSquashedUntilTheLegEndsThenItReshapes()
    {
        var (flight, old) = await BlockedFlight("2");
        var plane = flight.Worktree;
        var leg = _state.Legs(flight.Code).Single();
        _state.PutLeg(leg with { State = LegState.Flying });
        var checks = Checks();

        await checks.LookAsync(Ct);

        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(old);
        Git(plane, "rev-parse", "HEAD").ShouldBe(old);
        MergesOver(plane).ShouldBe(1);
        F(flight.Code).RebaseOnly.ShouldNotBeNull().ShouldContain("Conflict (rebase only): known, lands squashed / laid as one commit at landing");
        var card = _state.Cards().Single(c => c.Kind == LegCommander.RebaseOnlyKind);
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldContain("lands squashed / laid as one commit at landing");
        NoCardForThePerson();

        // The leg ends: the next look reshapes and the flight's note goes.
        _state.PutLeg(leg with { State = LegState.Done });
        await checks.LookAsync(Ct);

        var shaped = Git(plane, "rev-parse", "HEAD");
        shaped.ShouldNotBe(old);
        MergesOver(plane).ShouldBe(0);
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(shaped);
        F(flight.Code).RebaseOnly.ShouldBeNull();
        Open().ShouldNotContain(c => c.Kind == LegCommander.RebaseOnlyKind);
        NoCardForThePerson();
    }

    [Fact]
    public async Task AConflictingPrKeepsTheConflictsLabelAndThePathOfTheConflict()
    {
        var (flight, _) = await BlockedFlight("3");
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };

        await Checks().LookAsync(Ct);

        F(flight.Code).RebaseOnly.ShouldBeNull();
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.RebaseOnlyKind || c.Kind == LegCommander.ShapedKind || c.Kind == LegCommander.LaidKind);
        F(flight.Code).Review!.LabelOverride.ShouldBe(PilotMarks.Conflicts);
        _github.Labels[_github.Pr!.Number].ShouldContain(PilotMarks.Conflicts);
    }

    [Fact]
    public async Task ASquashOnlyBaseGetsNoAction()
    {
        var (flight, old) = await BlockedFlight("4", baseMethod: "SQUASH");

        await Checks().LookAsync(Ct);

        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(old);
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(old);
        F(flight.Code).RebaseOnly.ShouldBeNull();
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.RebaseOnlyKind || c.Kind == LegCommander.ShapedKind || c.Kind == LegCommander.LaidKind);
        NoCardForThePerson();
    }
}
