using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A fixer whose PR merged while it worked keeps its commits (#617): they go to a new branch and a follow-up PR against the same
/// base, reviewed as any PR; a push or create GitHub refuses is a For-you card naming the commit and the local branch holding it.
/// </summary>
public sealed class FixFollowUpTests : PrFixTestsBase
{
    private string Origin => Path.Combine(_root, "github", "widget.git");

    /// <summary>The fixer commits one file in its worktree, which the PR (already merged or not) does not have; returns that commit.</summary>
    private string FixerCommits()
    {
        File.WriteAllText(Path.Combine(FixWorktree, "fixed.txt"), "fixed\n");
        PrOrigins.Git(FixWorktree, "add", ".");
        PrOrigins.Git(FixWorktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the fix");
        return PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
    }

    /// <summary>The fake gh creates PR 71 as GitHub would: it exists, open, at the pushed head, once CreateAsync is called.</summary>
    private void CreatingMakesPr71(string tip)
    {
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.CreateAsync))
            {
                PrOrigins.Git(Origin, "update-ref", "refs/pull/71/head", tip);
                _github.Prs["acme/widget#71"] = Pr("acme/widget", 71, tip, "ada") with { HeadRef = "feature/seven-followup-7" };
            }
            return Task.CompletedTask;
        };
    }

    private void Merge() => _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "MERGED" };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergedPrsFixerCommitGoesToANewBranchAndAFollowUpPrThatIsReviewed()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        CreatingMakesPr71(tip);
        Merge();

        await _reviews.SweepAsync(Ct);

        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven-followup-7").ShouldBe(tip);
        var created = _github.Created.ShouldHaveSingleItem();
        (created.Branch, created.Base).ShouldBe(("feature/seven-followup-7", "main"));
        created.Title.ShouldContain("acme/widget#7");
        created.Body.ShouldContain("acme/widget#7");
        created.Body.ShouldContain("README.md:1 wrong");
        var card = _state.Cards().Single(c => c.Kind == PrReviews.FixFollowUpKind);
        (card.Bin, card.Link).ShouldBe((CardBin.Handled, _github.Pr!.Url));
        _reviews.Of(new PrRef("acme", "widget", 71)).ShouldNotBeNull();
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(2);
        Directory.Exists(FixWorktree).ShouldBeFalse();
        Row.Fixer.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheDesignAnswerIsNamedInTheFollowUpBody()
    {
        await Review("acme/widget#7", "changes", "README.md:1 design: should this be a service at all?");
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep it a library.")).Error.ShouldBeNull();
        var tip = FixerCommits();
        CreatingMakesPr71(tip);
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldHaveSingleItem().Body.ShouldContain("Keep it a library.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergedPrWhoseFixerCommittedNothingEndsAsBeforeWithNoFollowUp()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpKind || c.Kind == PrReviews.FixFollowUpFailedKind);
        Directory.Exists(FixWorktree).ShouldBeFalse();
        Row.Fixer.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AClosedUnmergedPrOpensNoFollowUp()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        FixerCommits();
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "CLOSED" };

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpKind);
        Directory.Exists(FixWorktree).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APushGitHubRefusesLeavesAForYouCardNamingTheCommitAndTheLocalBranch()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        // The follow-up branch exists on origin with another history, so a plain push is refused.
        var work = Path.Combine(_root, "work-widget");
        File.WriteAllText(Path.Combine(work, "other.txt"), "other\n");
        PrOrigins.Git(work, "add", ".");
        PrOrigins.Git(work, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "other");
        PrOrigins.Git(work, "push", "-q", "origin", "HEAD:refs/heads/feature/seven-followup-7");
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.FixFollowUpFailedKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Detail.ShouldNotBeNull().ShouldContain(tip);
        card.Detail.ShouldContain("feature/seven");
        PrOrigins.Git(WidgetClone, "rev-parse", "refs/heads/feature/seven").ShouldBe(tip);
        Directory.Exists(FixWorktree).ShouldBeFalse();
        Row.Fixer.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACreateGitHubRefusesLeavesAForYouCardNamingTheCommitAndTheLocalBranch()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        _github.OnCall = call => call == nameof(IGitHub.CreateAsync) ? throw new FlightException("gh pr create: refused") : Task.CompletedTask;
        Merge();

        await _reviews.SweepAsync(Ct);

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.FixFollowUpFailedKind);
        card.Detail.ShouldNotBeNull().ShouldContain(tip);
        card.Detail.ShouldContain("feature/seven");
        card.Detail.ShouldContain("refused");
        PrOrigins.Git(WidgetClone, "rev-parse", "refs/heads/feature/seven").ShouldBe(tip);
        Directory.Exists(FixWorktree).ShouldBeFalse();
    }
}
