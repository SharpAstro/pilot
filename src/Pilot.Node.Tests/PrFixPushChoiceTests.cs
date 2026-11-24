using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The ended card of a fixer that left commits in its kept worktree offers Push its commits beside Fix it (pilot #1347): a plain push of the
/// worktree's HEAD to the PR's branch, then a review of the moved head. Each test runs on real git, since a push and a commit graph are git's.
/// </summary>
public sealed class PrFixPushChoiceTests : PrFixTestsBase
{
    private string Origin => Path.Combine(_root, "github", "widget.git");

    /// <summary>A fixer that ended with the PR head unmoved; the sweep raises its ended card.</summary>
    private async Task<CardRow> EndedCardAsync(Action? leave = null)
    {
        RealGit();
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        leave?.Invoke();
        _state.PutSession(FixerSession(SessionState.Ended));
        await _reviews.SweepAsync(Ct);
        return _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixEndedKind).ShouldHaveSingleItem();
    }

    private void CommitInWorktree(string message) =>
        PrOrigins.Git(FixWorktree, "-c", "user.name=fixer", "-c", "user.email=fixer@example.com", "commit", "-q", "--allow-empty", "-m", message);

    [Fact]
    [Trait("Category", "Slow")] // real git: the worktree's commits past the PR head
    public async Task TheEndedCardOffersPushItsCommitsBesideFixItWhenTheWorktreeHoldsCommits()
    {
        var card = await EndedCardAsync(() => CommitInWorktree("the fix"));

        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([(PrReviews.FixLabel, Verbs.FixPr), (PrReviews.PushFixLabel, Verbs.PushFix)]);
        card.Detail.ShouldNotBeNull().ShouldContain(PrReviews.FixLeftWorkMark);
        card.Detail.ShouldContain(PrReviews.PushFixLabel);
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: uncommitted files in the kept worktree
    public async Task TheEndedCardOffersNoPushWhenTheWorktreeHoldsOnlyUncommittedFiles()
    {
        var card = await EndedCardAsync(() => File.WriteAllText(Path.Combine(FixWorktree, "scratch.txt"), "unfinished"));

        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.FixPr);
        card.Detail.ShouldNotBeNull().ShouldContain(PrReviews.FixLeftWorkMark);
        card.Detail.ShouldNotContain(PrReviews.PushFixLabel);
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the worktree's commits past the PR head
    public async Task TheEndedCardOffersNoPushForAPrFromAFork()
    {
        var card = await EndedCardAsync(() =>
        {
            CommitInWorktree("the fix");
            _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Fork = true };
        });

        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.FixPr);
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: a push to a bare remote and its fast-forward
    public async Task PushingLandsTheWorktreesHeadOnThePrBranchAsAFastForwardAndStartsAReview()
    {
        await EndedCardAsync(() => CommitInWorktree("the fix"));
        var commit = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        var oldHead = _github.Prs["acme/widget#7"].Head;
        // GitHub reads the branch's head as the origin has it, so the read right after the push is the pushed commit.
        _github.OnPullRequestRead = key =>
        {
            if (key == "acme/widget#7")
            {
                _github.Prs[key] = _github.Prs[key] with { Head = PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven") };
            }
        };
        var reviewsBefore = _workers.Started.Count(s => s.Name.Contains("-review-"));

        (await Run(Verbs.PushFix, "acme/widget#7", "person")).Error.ShouldBeNull();

        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven").ShouldBe(commit);
        PrOrigins.Git(Origin, "merge-base", "--is-ancestor", oldHead, commit);
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(reviewsBefore + 1);
        Row.Head.ShouldBe(commit);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrReviews.PushedUnreviewedKind);
        Directory.Exists(FixWorktree).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: a push to a bare remote
    public async Task WhenGitHubStillReadsTheOldHeadAfterThePushThePushedUnreviewedCardOffersTheReview()
    {
        await EndedCardAsync(() => CommitInWorktree("the fix"));
        var commit = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        var reviewsBefore = _workers.Started.Count(s => s.Name.Contains("-review-"));

        // The fake still serves the head the review read: GitHub has not shown the push yet.
        (await Run(Verbs.PushFix, "acme/widget#7", "person")).Error.ShouldBeNull();

        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven").ShouldBe(commit);
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(reviewsBefore);
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.PushedUnreviewedKind).ShouldHaveSingleItem();
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.ReviewPr);
        card.Detail.ShouldNotBeNull().ShouldContain("GitHub still read");
    }

    /// <summary>A refused push-fix leaves the PR's branch on the origin and the worktree's HEAD as they were.</summary>
    private async Task RefusedAndUntouchedAsync(string expected)
    {
        var head = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        var branch = PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven");

        var refused = await Run(Verbs.PushFix, "acme/widget#7", "person");

        refused.Error.ShouldNotBeNull().ShouldContain(expected);
        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven").ShouldBe(branch);
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(head);
        Directory.Exists(FixWorktree).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the origin's branch is read to show nothing was pushed
    public async Task PushingIsRefusedForAPrFromAFork()
    {
        await EndedCardAsync(() => CommitInWorktree("the fix"));
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Fork = true };

        await RefusedAndUntouchedAsync("fork");
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the origin's branch is read to show nothing was pushed
    public async Task PushingIsRefusedForAPrThatIsNotOpen()
    {
        await EndedCardAsync(() => CommitInWorktree("the fix"));
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "CLOSED" };

        await RefusedAndUntouchedAsync("not open");
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the origin's branch is read to show nothing was pushed
    public async Task PushingIsRefusedWhileAFixerIsLive()
    {
        await EndedCardAsync(() => CommitInWorktree("the fix"));
        _state.PutSession(Session("du1-fix-widget-7", SessionState.Working, "efgh5678") with { Role = SessionRole.Fixer, Pr = 7, Repo = "widget" });

        await RefusedAndUntouchedAsync("still working");
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the PR head is not an ancestor of the worktree's HEAD
    public async Task PushingIsRefusedWhenThePrHeadMovedAndTheWorktreeIsUntouched()
    {
        await EndedCardAsync(() => CommitInWorktree("the fix"));
        var before = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        var branchBefore = PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven");
        SomeoneElsePushes("someone else's change");
        PrOrigins.Git(FixWorktree, "fetch", "-q", "origin", "refs/pull/7/head");

        var refused = await Run(Verbs.PushFix, "acme/widget#7", "person");

        refused.Error.ShouldNotBeNull().ShouldContain("not an ancestor");
        refused.Error.ShouldContain("work stays");
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(before);
        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven").ShouldBe(branchBefore);
        Directory.Exists(FixWorktree).ShouldBeTrue();
    }
}
