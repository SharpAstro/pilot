using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// One landing routine for every merge path (pilot #691): each path times each shape of GitHub's answer ends merged, retried, or in
/// exactly one open card (or the refusal the caller shows) whose detail is GitHub's whole text. Done answers and a "can't be
/// rebased" that squashed ring nothing. FakeGitHub is scripted per call.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LandingPathTests : ReviewTestsBase
{
    private const string Marker = "marker-7431";

    public enum Path
    {
        /// <summary>The merge verb pressed by a person (the card's Merge, pilot flight merge).</summary>
        Hand,
        /// <summary>The node's own merge on autopilot (PrChecks.MergeOnAutopilotAsync).</summary>
        Autopilot,
        /// <summary>The routine itself, as the laid-head requeue and the reviewed-PR queue call it.</summary>
        Routine,
    }

    public enum Shape { Done, Retried, Refused }

    public static TheoryData<Path, string, Shape> Cases()
    {
        var answers = new (string Text, Shape Shape)[]
        {
            ($"GraphQL: Pull request is already in the queue {Marker}", Shape.Done),
            ($"Pull request is already merged {Marker}", Shape.Done),
            ($"GraphQL: Auto merge is already enabled {Marker}", Shape.Done),
            ($"GraphQL: This branch can't be rebased {Marker} (mergePullRequest)", Shape.Done),
            ($"HTTP 502 Bad Gateway {Marker}", Shape.Retried),
            ($"gh timed out {Marker}", Shape.Retried),
            ($"GraphQL: Base branch was modified {Marker}. Review and try the merge again.", Shape.Refused),
            ($"GraphQL: Pull request has a merge conflict {Marker}", Shape.Refused),
            ($"GraphQL: Head branch was modified {Marker}. Review and try the merge again.", Shape.Refused),
            ($"GraphQL: Repository rule violations found {Marker}\nA second line GitHub gave.", Shape.Refused),
        };
        var data = new TheoryData<Path, string, Shape>();
        foreach (var path in Enum.GetValues<Path>())
        {
            foreach (var (text, shape) in answers)
            {
                data.Add(path, text, shape);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryPathEndsMergedRetriedOrInOneCardWithGitHubsWholeText(Path path, string answer, Shape shape)
    {
        var (flight, _) = await Passed("1");
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
        }

        var cards = Open().Where(c => c.Kind != LegCommander.PrReady).ToArray();
        switch (shape)
        {
            case Shape.Done:
                error.ShouldBeNull();
                outcome?.Done.ShouldBeTrue();
                cards.Where(c => c.Bin is CardBin.Ring).ShouldBeEmpty();
                _github.Merged.Count.ShouldBeLessThanOrEqualTo(1);
                break;
            case Shape.Retried:
                error.ShouldBeNull();
                if (outcome is not null)
                {
                    outcome.Kind.ShouldBe(LandKind.RetryLater);
                    outcome.Said.ShouldNotBeNull().ShouldContain(Marker);
                }
                cards.Where(c => c.Bin is CardBin.Ring).ShouldBeEmpty();
                _github.Merged.ShouldBeEmpty();
                break;
            case Shape.Refused:
                _github.Merged.ShouldBeEmpty();
                switch (path)
                {
                    case Path.Hand:
                        error.ShouldNotBeNull().ShouldContain(Marker);
                        if (answer.Contains("second line", StringComparison.Ordinal))
                        {
                            error.ShouldContain("A second line GitHub gave.");
                        }
                        cards.Where(c => c.Bin is CardBin.Ring).ShouldBeEmpty();
                        break;
                    case Path.Autopilot:
                        if (answer.Contains("Base branch was modified", StringComparison.Ordinal))
                        {
                            // Behind: the node brings the branch up to date and offers the merge again, so nothing is refused to show.
                            cards.Where(c => c.Bin is CardBin.Ring).ShouldBeEmpty();
                            break;
                        }
                        var one = cards.ShouldHaveSingleItem();
                        $"{one.Title} {one.Detail}".ShouldContain(Marker);
                        break;
                    default:
                        outcome.ShouldNotBeNull().Kind.ShouldBe(LandKind.Refused);
                        outcome.Said.ShouldNotBeNull().ShouldContain(Marker);
                        break;
                }
                break;
        }
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
