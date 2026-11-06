using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// One landing routine for every merge path (pilot #691): each flight path times each shape of GitHub's answer
/// (<see cref="LandingAnswers"/>) ends merged, retried, or in exactly one open card (or the refusal the caller shows) whose detail is
/// GitHub's whole text. Done answers and a "can't be rebased" that squashed ring nothing. The reviewed-PR paths are
/// <see cref="LandingReviewPathTests"/>, the recheck's <see cref="LandingRecheckPathTests"/>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LandingPathTests : ReviewTestsBase
{
    private const string Marker = LandingAnswers.Marker;

    public enum Path
    {
        /// <summary>The merge verb pressed by a person (the card's Merge, pilot flight merge).</summary>
        Hand,
        /// <summary>The node's own merge on autopilot (PrChecks.MergeOnAutopilotAsync).</summary>
        Autopilot,
        /// <summary>The routine itself (LegCommander.LandPrAsync), as every caller reaches it.</summary>
        Routine,
        /// <summary>A merge armed while checks ran, taken once they pass (PrChecks.MergeWhenGreenAsync).</summary>
        ArmedAtGreen,
        /// <summary>A PR the queue dropped, laid on its base and queued again at the laid head (LegCommander.Lay.cs).</summary>
        Laid,
    }

    public static TheoryData<Path, string, LandingAnswers.Shape> Cases() => LandingAnswers.Cases<Path>();

    private static void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-a", "-m", message);

    // As RebaseLayFlightTests: a branch whose merge with main is clean but which does not rebase, so a drop is laid as one commit.
    private async Task<(FlightRow Flight, string Old)> KeepOursFlight()
    {
        var (flight, _) = await Passed("1");
        File.WriteAllText(System.IO.Path.Combine(Repo, "f.txt"), "x\n");
        Git(Repo, "add", "f.txt");
        Commit(Repo, "f");
        File.WriteAllText(System.IO.Path.Combine(Repo, "f.txt"), "side\n");
        Commit(Repo, "side");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        Git(plane, "fetch", "-q", "origin");
        File.WriteAllText(System.IO.Path.Combine(plane, "f.txt"), "mine1\n");
        Git(plane, "add", ".");
        Commit(plane, "mine 1");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "-s", "ours", "-m", "keep ours", "origin/main");
        File.WriteAllText(System.IO.Path.Combine(plane, "f.txt"), "mine2\n");
        Commit(plane, "mine 2");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var old = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f.Review is { } r ? f with { Review = r with { ReviewedHead = old } } : f);
        return (F(flight.Code), old);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryPathEndsMergedRetriedOrInOneCardWithGitHubsWholeText(Path path, string answer, LandingAnswers.Shape shape)
    {
        FlightRow flight;
        if (path == Path.Laid)
        {
            // A PR laid on its base as one commit is built by rebase, merge-tree and commit-tree: git's own.
            RealGit();
            _github.MergeQueueMethod = "REBASE";
            _github.Queues = true;
            (flight, var old) = await KeepOursFlight();
            // The PR-ready card offers the merge itself while it is open; a dropped PR is looked at once it is closed.
            _state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "test", LegCommander.PrReady);
            _github.Queue = new MergeQueueState("OPEN", old, Queued: false, RemovedAt: DateTimeOffset.UtcNow, RemovedReason: "merge conflict");
        }
        else
        {
            (flight, _) = await Passed("1");
        }
        var head = _github.Pr!.Head;
        _github.MergeScript.Enqueue(answer);

        string? error = null;
        LandOutcome? outcome = null;
        switch (path)
        {
            case Path.Hand:
                error = (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head)).Error;
                break;
            case Path.Autopilot:
                _github.Checks = ChoiceStatus.Passing;
                _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
                await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);
                break;
            case Path.Routine:
                outcome = await LegCommander.LandPrAsync(_github, Repo, _github.Pr.Number, head, "squash", Ct);
                break;
            case Path.ArmedAtGreen:
                _state.UpdateFlight(flight.Code, f => f with { MergeWhenGreen = [head, "squash", LegCommander.ArmedByNode] });
                _github.Checks = ChoiceStatus.Passing;
                await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);
                break;
            case Path.Laid:
                await new PrChecks(_state, _github, TimeProvider.System, legs: _legs).LookAsync(Ct);
                break;
        }

        switch (shape)
        {
            case LandingAnswers.Shape.Done:
                error.ShouldBeNull();
                outcome?.Done.ShouldBeTrue();
                LandingAnswers.NothingAsks(_state);
                _github.Merged.Count.ShouldBeLessThanOrEqualTo(1);
                break;
            case LandingAnswers.Shape.Retried:
                error.ShouldBeNull();
                if (outcome is not null)
                {
                    outcome.Kind.ShouldBe(LandKind.RetryLater);
                    outcome.Said.ShouldNotBeNull().ShouldContain(Marker);
                }
                LandingAnswers.NothingAsks(_state);
                _github.Merged.ShouldBeEmpty();
                if (path != Path.Routine)
                {
                    // The merge waits on the flight and is asked again at the next look.
                    F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
                }
                break;
            case LandingAnswers.Shape.Refused:
                _github.Merged.ShouldBeEmpty();
                switch (path)
                {
                    case Path.Hand:
                        error.ShouldNotBeNull().ShouldContain(Marker);
                        if (answer.Contains(LandingAnswers.SecondLine, StringComparison.Ordinal))
                        {
                            error.ShouldContain(LandingAnswers.SecondLine);
                        }
                        Open().Where(c => c.Bin is CardBin.Ring && c.Kind != LegCommander.PrReady).ShouldBeEmpty();
                        break;
                    case Path.Routine:
                        outcome.ShouldNotBeNull().Kind.ShouldBe(LandKind.Refused);
                        outcome.Said.ShouldNotBeNull().ShouldContain(Marker);
                        break;
                    case Path.Autopilot or Path.ArmedAtGreen when LandingAnswers.UpdatesTheBranch(answer):
                        // Behind or conflicting: the node brings the branch up to date and offers the merge again, nothing to show.
                        LandingAnswers.NothingAsks(_state);
                        break;
                    default:
                        LandingAnswers.OneCardHoldsTheWholeText(_state, answer);
                        Open().Where(c => c.Bin is CardBin.Ring && c.Kind != LegCommander.PrReady && !$"{c.Detail}".Contains(Marker, StringComparison.Ordinal)).ShouldBeEmpty();
                        break;
                }
                break;
        }
        LandingAnswers.EveryShaIsALink(_state);
    }

    [Fact]
    public async Task EveryShaOnTheCardsAMergeRaisesIsALinkAndNoButtonHoldsOne()
    {
        var (flight, _) = await Passed("1");
        var head = _github.Pr!.Head;
        _github.Checks = ChoiceStatus.Passing;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        _github.MergeScript.Enqueue("GraphQL: Repository rule violations found");
        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);
        _github.MergeScript.Enqueue("GraphQL: Pull request has a merge conflict");
        await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head);

        _state.Cards().ShouldNotBeEmpty();
        LandingAnswers.EveryShaIsALink(_state);
    }

    [Fact]
    public async Task ARefusalCardKeepsEveryLineOfGitHubsMessageInItsDetailAndOnlyTheTitleIsShort()
    {
        var (_, _) = await Passed("1");
        _github.Checks = ChoiceStatus.Passing;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        var said = $"GraphQL: Repository rule violations found {Marker} and then a very long first line that goes on and on past what a card title should ever hold\nsecond line\nthird line";
        _github.MergeScript.Enqueue(said);

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        var card = Open().Single(c => c.Kind == PrChecks.AutopilotMergeRefused);
        card.Detail.ShouldNotBeNull().ShouldContain("second line");
        card.Detail.ShouldContain("third line");
        card.Detail.ShouldContain("goes on and on past what a card title should ever hold");
        card.Title.Length.ShouldBeLessThan(card.Detail.Length);
    }

    [Fact]
    public async Task ANotRebaseableMergeThatSquashesSaysHowOnTheFlightAndRingsNothing()
    {
        var outcome = await LegCommander.LandPrAsync(NotRebaseable(out var github), "repo", 5, "abc1234", "rebase", Ct);

        outcome.Kind.ShouldBe(LandKind.Merged);
        outcome.Method.ShouldBe("squash");
        github.Merged.ShouldHaveSingleItem().Method.ShouldBe("squash");
    }

    private static FakeGitHub NotRebaseable(out FakeGitHub github)
    {
        github = new FakeGitHub();
        github.MergeScript.Enqueue("GraphQL: This branch can't be rebased (mergePullRequest)");
        return github;
    }
}
