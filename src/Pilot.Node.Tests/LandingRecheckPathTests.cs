using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The recheck's merge of a passed, green flight PR (PrRecheck) through the one landing routine (pilot #691), times each shape of
/// GitHub's answer (<see cref="LandingAnswers"/>): merged or queued, retried with the merge left armed, brought up to date, or one
/// card holding GitHub's whole text.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LandingRecheckPathTests : PrRecheckTestsBase
{
    public static TheoryData<string, LandingAnswers.Shape> Cases()
    {
        var data = new TheoryData<string, LandingAnswers.Shape>();
        foreach (var (text, shape) in LandingAnswers.All)
        {
            data.Add(text, shape);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheRechecksMergeEndsMergedRetriedOrInOneCardWithGitHubsWholeText(string answer, LandingAnswers.Shape shape)
    {
        var flight = await PassedFlightAsync();
        _github.Checks = ChoiceStatus.Passing;
        _github.MergeScript.Enqueue(answer);

        await LookAsync();

        switch (shape)
        {
            case LandingAnswers.Shape.Done:
                LandingAnswers.NothingAsks(_state);
                Handled().ShouldHaveSingleItem().ShouldMatch("passed and green, (merged|queued for merge) at ");
                break;
            case LandingAnswers.Shape.Retried:
                LandingAnswers.NothingAsks(_state);
                _github.Merged.ShouldBeEmpty();
                Handled().ShouldHaveSingleItem().ShouldContain("the merge waits at");
                _state.Flights().Single(f => f.Code == flight.Code).MergeWhenGreen.ShouldNotBeNull();
                break;
            case LandingAnswers.Shape.Refused:
                _github.Merged.ShouldBeEmpty();
                if (LandingAnswers.UpdatesTheBranch(answer))
                {
                    LandingAnswers.NothingAsks(_state);
                    Handled().ShouldHaveSingleItem().ShouldContain("brought up to date");
                    break;
                }
                LandingAnswers.OneCardHoldsTheWholeText(_state, answer).Kind.ShouldBe(PrRecheck.FailedKind);
                break;
        }
        LandingAnswers.EveryShaIsALink(_state);
    }
}
