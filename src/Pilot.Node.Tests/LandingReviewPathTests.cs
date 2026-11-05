using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The reviewed-PR merge paths of the one landing routine (pilot #691), each times each shape of GitHub's answer
/// (<see cref="LandingAnswers"/>): the automatic queueing of a reviewed, green PR (PrReviews.QueueWhenGreenAsync), and the Merge
/// choice of its card. Each ends queued, retried, or in one refusal (a card, or the error of the pressed choice) holding GitHub's
/// whole text.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LandingReviewPathTests : PrReviewTestsBase
{
    public enum Path
    {
        /// <summary>The node's own queueing at the sweep after the verdict.</summary>
        Queue,
        /// <summary>The Merge choice of the reviewed PR's card (a base with no merge queue).</summary>
        MergeChoice,
    }

    public static TheoryData<Path, string, LandingAnswers.Shape> Cases() => LandingAnswers.Cases<Path>();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryReviewPathEndsQueuedRetriedOrInOneRefusalWithGitHubsWholeText(Path path, string answer, LandingAnswers.Shape shape)
    {
        string? error = null;
        if (path == Path.Queue)
        {
            _github.MergeScript.Enqueue(answer);
            await ReviewedWidget("approve");
        }
        else
        {
            await ReviewedWidget("approve", queue: false);
            _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrReviews.MergeKind);
            _github.MergeScript.Enqueue(answer);
            error = (await Run(Verbs.Merge, "pr/acme/widget#7", "controller", _widgetHead)).Error;
        }
        var row = Row("acme/widget", 7);

        switch (shape)
        {
            case LandingAnswers.Shape.Done:
                error.ShouldBeNull();
                row.QueuedAt.ShouldNotBeNull();
                LandingAnswers.NothingAsks(_state);
                _state.Cards().ShouldContain(c => c.Kind == PrReviews.QueuedKind);
                break;
            case LandingAnswers.Shape.Retried:
                row.QueuedAt.ShouldBeNull();
                _github.Merged.ShouldBeEmpty();
                LandingAnswers.NothingAsks(_state);
                if (path == Path.Queue)
                {
                    // No note and no card: the next sweep asks again.
                    row.QueueNote.ShouldBeNull();
                }
                else
                {
                    error.ShouldNotBeNull().ShouldContain(LandingAnswers.Marker);
                    error.ShouldContain("press Merge again");
                }
                break;
            case LandingAnswers.Shape.Refused:
                row.QueuedAt.ShouldBeNull();
                _github.Merged.ShouldBeEmpty();
                if (path == Path.Queue)
                {
                    var card = LandingAnswers.OneCardHoldsTheWholeText(_state, answer);
                    card.Kind.ShouldBe(PrReviews.QueueRefusedKind);
                    card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Merge);
                }
                else
                {
                    foreach (var line in answer.Split('\n'))
                    {
                        error.ShouldNotBeNull().ShouldContain(line);
                    }
                    LandingAnswers.NothingAsks(_state);
                }
                break;
        }
        LandingAnswers.EveryShaIsALink(_state);
    }
}
