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

    /// <summary>Commits <paramref name="file"/> in the work repo (the other clone of origin) and pushes it to <paramref name="target"/>; returns the commit.</summary>
    private string WorkPushes(string file, string text, string target)
    {
        var work = Path.Combine(_root, "work-widget");
        File.WriteAllText(Path.Combine(work, file), text);
        PrOrigins.Git(work, "add", ".");
        PrOrigins.Git(work, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "landed as another commit");
        PrOrigins.Git(work, "push", "-q", "origin", $"HEAD:refs/heads/{target}");
        return PrOrigins.Git(work, "rev-parse", "HEAD");
    }

    [Fact]
    public async Task AFixerCommitWhosePatchIsOnMainUnderAnotherShaOpensNoPrAndLeavesOnlyAHandledLine()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        FixerCommits();
        WorkPushes("fixed.txt", "fixed\n", "main");
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpKind || c.Kind == PrReviews.FixFollowUpFailedKind);
        // The helper throws on git's non-zero exit, which for rev-parse --verify --quiet is the ref's absence.
        Should.Throw<InvalidOperationException>(() => PrOrigins.Git(Origin, "rev-parse", "--verify", "--quiet", "refs/heads/feature/seven-followup-7"));
        Row.Fixer.ShouldBeNull();
        // Not silent: a line to read says why no PR came.
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.FixFollowUpSettledKind && c.Bin == CardBin.Handled && c.Title.Contains("already on main"));
    }

    [Fact]
    [Trait("Category", "Slow")] // The quoting of a non-ASCII name is git's own: the in-memory model never quotes, so only real git can fail on the old code.
    public async Task AFixerCommitOfAFileWithANonAsciiNameStillOpensTheFollowUpPr()
    {
        RealGit();
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        File.WriteAllText(Path.Combine(FixWorktree, "café.txt"), "fixed\n");
        PrOrigins.Git(FixWorktree, "add", ".");
        PrOrigins.Git(FixWorktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the fix");
        var tip = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        // Main changed something else: git quotes the fixer's name in plain output, and a quoted name as a pathspec matches nothing, which once read as 'already on main'.
        WorkPushes("other.txt", "other\n", "main");
        CreatingMakesPr71(tip);
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldHaveSingleItem();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpSettledKind);
    }

    /// <summary>The PR was rebuilt and merged as <paramref name="mergedHead"/> (a commit on main); the fixer's worktree is still on the head it was reviewed at.</summary>
    private void MergedAsRebuiltAt(string mergedHead) =>
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "MERGED", Head = mergedHead };

    private void NoFollowUpPushed() =>
        Should.Throw<InvalidOperationException>(() => PrOrigins.Git(Origin, "rev-parse", "--verify", "--quiet", "refs/heads/feature/seven-followup-7"));

    [Fact]
    public async Task FixerCommitsOnAHeadFromBeforeThePrWasRebuiltThatMainAlreadyHoldOpenNoPr()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        FixerCommits();
        var landed = WorkPushes("fixed.txt", "fixed\n", "main");
        MergedAsRebuiltAt(landed);

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        NoFollowUpPushed();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpKind || c.Kind == PrReviews.FixFollowUpFailedKind || c.Kind == PrReviews.FixFollowUpConflictKind);
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.FixFollowUpSettledKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task FixerCommitsThatConflictWithMainRaiseAForYouCardAndPushNothing()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        var landed = WorkPushes("fixed.txt", "main wrote this differently\n", "main");
        MergedAsRebuiltAt(landed);

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        NoFollowUpPushed();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.FixFollowUpConflictKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Detail.ShouldNotBeNull().ShouldContain("the fix");
        card.Detail.ShouldContain("fixed.txt");
        card.Detail.ShouldContain("feature/seven");
        PrOrigins.Git(WidgetClone, "rev-parse", "refs/heads/feature/seven").ShouldBe(tip);
    }

    [Fact]
    public async Task FixerCommitsThatReallyChangeMainOpenTheFollowUpPrEvenWhenBuiltOnAnOldHead()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        var landed = WorkPushes("other.txt", "other\n", "main");
        MergedAsRebuiltAt(landed);
        CreatingMakesPr71(tip);

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldHaveSingleItem();
        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven-followup-7").ShouldBe(tip);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpConflictKind || c.Kind == PrReviews.FixFollowUpSettledKind);
    }

    [Fact]
    public async Task AFixerCommitOnlyPartlyOnMainStillOpensTheFollowUpPr()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        File.WriteAllText(Path.Combine(FixWorktree, "fixed.txt"), "fixed\n");
        File.WriteAllText(Path.Combine(FixWorktree, "second.txt"), "second\n");
        PrOrigins.Git(FixWorktree, "add", ".");
        PrOrigins.Git(FixWorktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the fix");
        var tip = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        WorkPushes("fixed.txt", "fixed\n", "main");
        CreatingMakesPr71(tip);
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldHaveSingleItem();
        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven-followup-7").ShouldBe(tip);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpFailedKind);
    }

    [Fact]
    public async Task AFollowUpBranchOnOriginThatHoldsTheCommitsChangesRaisesNoFailureCard()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        FixerCommits();
        // The follow-up branch is there with the same change under another commit, off main, so the push is refused and the work is already on origin.
        WorkPushes("fixed.txt", "fixed\n", "feature/seven-followup-7");
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpFailedKind);
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.FixFollowUpSettledKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task AFollowUpBranchOnOriginWithOtherWorkSaysTheBranchExists()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        FixerCommits();
        WorkPushes("other.txt", "other\n", "feature/seven-followup-7");
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.FixFollowUpFailedKind);
        card.Detail.ShouldNotBeNull().ShouldContain("already exists on origin");
        card.Detail.ShouldNotContain("could not push");
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

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergedPrWhoseFixerWorktreeFolderIsGoneEndsTheFixerWithoutThrowing()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        FixerCommits();
        Directory.Delete(FixWorktree, recursive: true);
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        Row.Fixer.ShouldBeNull();
    }

    /// <summary>git on Windows with a process in the worktree: the remove is refused.</summary>
    private sealed class RemoveRefusingGit(IGit inner) : IGit
    {
        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken) =>
            args is ["worktree", "remove", ..] ? Task.FromResult(new GitRun(1, "", "Permission denied")) : inner.RunAsync(args, cwd, cancellationToken);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWorktreeThatWillNotGoIsRetriedAloneAndTheFollowUpIsOpenedOnce()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        CreatingMakesPr71(tip);
        Merge();
        var git = new RemoveRefusingGit(_git);
        var reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };

        await reviews.SweepAsync(Ct);
        await reviews.SweepAsync(Ct);

        _github.Created.ShouldHaveSingleItem();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpFailedKind);
        _state.Cards().Count(c => c.Kind == PrReviews.FixFollowUpKind).ShouldBe(1);
    }

    /// <summary>The row of a fixer that has gone from it (tianwen#2470): no Fixer, the fix worktree left on disk at the derived path.</summary>
    private void FixerGoneFromTheRow() =>
        _state.PutPrReview(Row with { Fixer = null, FixerJob = null, FixWorktree = null, FixWorktreeBorrowed = null, FixDetached = null });

    private SessionRow LiveFixer() =>
        Session("du1-fix-widget-7", SessionState.Working, "feed1234") with { Role = SessionRole.Fixer, Pr = 7, Repo = "widget", Cwd = FixWorktree };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergedPrWithNoFixerOnItsRowAndACommitInTheDerivedWorktreeGetsAFollowUpWithNoForYouCard()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        FixerGoneFromTheRow();
        CreatingMakesPr71(tip);
        Merge();

        await _reviews.SweepAsync(Ct);

        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven-followup-7").ShouldBe(tip);
        _github.Created.ShouldHaveSingleItem().Base.ShouldBe("main");
        _reviews.Of(new PrRef("acme", "widget", 71)).ShouldNotBeNull();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Bin == CardBin.ForYou);
        _state.Cards().Count(c => c.Kind == PrReviews.FixFollowUpKind).ShouldBe(1);
        Directory.Exists(FixWorktree).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALiveFixerSessionOfAMergedPrIsStoppedByTheNodeThenItsCommitIsFollowedUp()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        FixerGoneFromTheRow();
        _state.PutSession(LiveFixer());
        CreatingMakesPr71(tip);
        Merge();

        await _reviews.SweepAsync(Ct);

        _workers.StopReasons.ShouldContain(r => r.Job == LiveFixer().Id);
        _github.Created.ShouldHaveSingleItem();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Bin == CardBin.ForYou);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFixWorktreeWithNothingPastTheMergedHeadMakesNothingAndIsRemoved()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        FixerGoneFromTheRow();
        Merge();

        await _reviews.SweepAsync(Ct);

        _github.Created.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixFollowUpKind || c.Kind == PrReviews.FixFollowUpFailedKind);
        Directory.Exists(FixWorktree).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFollowUpOfAMergedPrWithNoFixerHappensOncePerWorktree()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var tip = FixerCommits();
        FixerGoneFromTheRow();
        CreatingMakesPr71(tip);
        Merge();
        // A worktree that will not go, so the second sweep still finds it with its commit past the merged head: only the once-per-worktree
        // guard keeps that sweep from making a second follow-up PR.
        var git = new RemoveRefusingGit(_git);
        var reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };

        await reviews.SweepAsync(Ct);
        Directory.Exists(FixWorktree).ShouldBeTrue("the worktree is still there for the second sweep");
        await reviews.SweepAsync(Ct);

        Directory.Exists(FixWorktree).ShouldBeTrue();
        _github.Created.ShouldHaveSingleItem();
    }
}
