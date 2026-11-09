using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A design card says what it asks (owner, 2026-11-09: "it doesn't even list the question"): its body is the question itself and the
/// recommended option, never only the kind's fixed words, and each option's button carries the option whole, never cut with "...".
/// </summary>
public sealed class DesignCardWordsTests
{
    private const string Question = "SiblingSource(job) exports UseLocalSiblings=true for a plane job of any repo, so a job in a repo with no sibling clones restores against folders that do not exist: should the variable be exported only when SiblingWatch.ReachedClones has the job's repo?";

    private static readonly DesignOption Recommended = new("export the variable only when SiblingWatch.ReachedClones(job.Repo) is true, and say so on the job's line in pilot status", "jobs in repos with no clones restore packages", true);
    private static readonly DesignOption Other = new("same, and also ring a card on the flight when the label is on a repo that has no sibling clones on this node at all", "one more card kind", false);

    private static DesignQuestion Asked() => new(null!, Question, [Other, Recommended]);

    [Fact]
    public void TheBodyIsTheQuestionWholeAndTheRecommendedOption()
    {
        var summary = PrReviews.DesignSummary([Asked()]);

        summary.ShouldContain(Question);
        summary.ShouldContain($"Recommended: {Recommended.Text}.");
        summary.ShouldNotContain("...");
    }

    [Fact]
    public void EveryOptionsButtonCarriesTheOptionWholeRecommendedFirst()
    {
        var choices = PrReviews.DesignChoices("pr/SharpAstro/pilot#888", [Asked()]);

        choices[0].Label.ShouldBe(Recommended.Text);
        choices[1].Label.ShouldBe(Other.Text);
        choices.ShouldAllBe(c => !c.Label.EndsWith("...", StringComparison.Ordinal));
    }
}
