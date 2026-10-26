using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Git for a PR review's tests: bare repos standing in for GitHub, each with a PR's head at <c>refs/pull/&lt;n&gt;/head</c>
/// as GitHub keeps it, so the node's fetch of it is a real one.
/// </summary>
internal static class PrOrigins
{
    public static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    /// <summary>A bare origin for <paramref name="name"/> under <paramref name="root"/>, its main one commit, and PR <paramref name="number"/> one past it; returns the origin and the PR's head.</summary>
    public static (string Origin, string Head) Make(string root, string name, int number)
    {
        var origin = Path.Combine(root, "github", name + ".git");
        var work = Path.Combine(root, "work-" + name);
        Directory.CreateDirectory(work);
        Git(root, "init", "-q", "--bare", "-b", "main", origin);
        Git(work, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
        Commit(work, "first");
        Git(work, "remote", "add", "origin", origin);
        Git(work, "push", "-q", "origin", "HEAD:main");
        return (origin, Push(work, number, "the change"));
    }

    /// <summary>One more commit on the PR, pushed to its head as GitHub keeps it; returns the new head.</summary>
    public static string Push(string work, int number, string what)
    {
        File.WriteAllText(Path.Combine(work, $"{Guid.NewGuid():N}.txt"), what + "\n");
        Commit(work, what);
        Git(work, "push", "-q", "-f", "origin", $"HEAD:refs/pull/{number}/head");
        return Git(work, "rev-parse", "HEAD");
    }

    private static void Commit(string work, string message)
    {
        Git(work, "add", ".");
        Git(work, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }

    /// <summary>Removes a test's folder, git's read-only objects and all.</summary>
    public static void Delete(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }
}

/// <summary>
/// A review of any PR (#113): pinned at the PR's head in a worktree of the node's own, made in the repo's clone under the
/// repo roots or in the node's clone cache, its verdict on a card, and its worktree gone once that card closes. GitHub is
/// a fake gh over bare repos; git is real.
/// </summary>
public sealed class PrReviewTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2p-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FakeGitHub _github = new();
    private readonly NodeOptions _options;
    private readonly PrReviews _reviews;

    /// <summary>acme/widget has a clone under the repo roots; acme/gadget has none.</summary>
    private readonly string _widgetHead;
    private readonly string _gadgetHead;

    public PrReviewTests()
    {
        var (widget, widgetHead) = PrOrigins.Make(_root, "widget", 7);
        var (gadget, gadgetHead) = PrOrigins.Make(_root, "gadget", 9);
        (_widgetHead, _gadgetHead) = (widgetHead, gadgetHead);

        // The clone under the roots names GitHub as its origin, as a real one does; git reads it from the bare repo.
        Directory.CreateDirectory(Roots);
        PrOrigins.Git(Roots, "clone", "-q", widget, "widget");
        PrOrigins.Git(WidgetClone, "config", "remote.origin.url", "https://github.com/acme/widget.git");
        PrOrigins.Git(WidgetClone, "config", $"url.{widget.Replace('\\', '/')}.insteadOf", "https://github.com/acme/widget.git");
        _github.Origins["acme/gadget"] = gadget;
        _github.Prs["acme/widget#7"] = Pr("acme/widget", 7, widgetHead, author: "ada");
        _github.Prs["acme/gadget#9"] = Pr("acme/gadget", 9, gadgetHead, author: "bob");

        var git = new GitCli();
        _options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1", RepoRootsGiven: [Roots]);
        _reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            // The Post path is what these tests are about; PostsItselfOnAnOpenPr covers the node posting it.
            PostsItself = false,
        };
    }

    private string Roots => Path.Combine(_root, "repos");

    private string WidgetClone => Path.Combine(Roots, "widget");

    private string GadgetCache => Path.Combine(_root, "data", "clones", "acme", "gadget");

    private static PullRequest Pr(string repo, int number, string head, string author, string state = "OPEN") =>
        new(number, state, head, $"https://github.com/{repo}/pull/{number}", Title: $"PR {number} of {repo}", Author: author, Base: "main");

    public void Dispose()
    {
        foreach (var clone in new[] { WidgetClone, GadgetCache }.Where(Directory.Exists))
        {
            try
            {
                PrOrigins.Git(clone, "worktree", "prune");
            }
            catch (InvalidOperationException)
            {
            }
        }
        PrOrigins.Delete(_root);
    }

    private Task<CommandResult> Run(string verb, string target, string who, params string[] args) =>
        _reviews.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);

    private PrReviewRow Row(string repo, int number) => _reviews.Of(new PrRef(repo.Split('/')[0], repo.Split('/')[1], number)).ShouldNotBeNull();

    [Fact]
    public async Task APrInARepoWithACloneIsReviewedInThatClonePinnedAtItsHead()
    {
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();

        var row = Row("acme/widget", 7);
        (row.State, row.ClonePath, row.Head, row.Reviewer).ShouldBe((PrReviewState.Reviewing, WidgetClone, _widgetHead, "du1-review-widget-7"));
        row.Worktree.ShouldBe(Path.Combine(WidgetClone, ".claude", "worktrees", "review-widget-7"));
        _github.Cloned.ShouldBeEmpty();

        // Pinned: the worktree is at the PR's head, on no branch.
        PrOrigins.Git(row.Worktree, "rev-parse", "HEAD").ShouldBe(_widgetHead);
        Should.Throw<InvalidOperationException>(() => PrOrigins.Git(row.Worktree, "symbolic-ref", "-q", "HEAD"));

        // A job of the node's, started from the clone into its review worktree, as a flight's reviewer is into its plane.
        var start = _workers.Started.ShouldHaveSingleItem();
        (start.Name, start.RepoPath, start.Plane).ShouldBe(("du1-review-widget-7", WidgetClone, "review-widget-7"));
        row.ReviewerJob.ShouldBe("job00001");
        start.Brief.ShouldContain("You review PR acme/widget#7: PR 7 of acme/widget, by ada");
        start.Brief.ShouldContain("git diff origin/main...HEAD");
        start.Brief.ShouldContain("against the tree");
        start.Brief.ShouldContain("Never assert a defect you cannot prove");
        start.Brief.ShouldContain("review <verdict>");
        start.Brief.ShouldContain("--finding \"<path:line> <what is wrong>\"");
        start.Brief.ShouldContain("the change");
        start.Brief.ShouldNotContain("GitHub takes only a comment");
        _state.Cards().ShouldHaveSingleItem().Kind.ShouldBe(PrReviews.StartedKind);
    }

    [Fact]
    public async Task APrInARepoWithNoCloneIsClonedIntoTheCacheOnceAndAFreshReviewEndsTheLast()
    {
        (await Run(Verbs.ReviewPr, "pr/acme/gadget#9", "controller")).Error.ShouldBeNull();

        var first = Row("acme/gadget", 9);
        _github.Cloned.ShouldBe([("acme/gadget", GadgetCache)]);
        first.ClonePath.ShouldBe(GadgetCache);
        PrOrigins.Git(first.Worktree, "rev-parse", "HEAD").ShouldBe(_gadgetHead);

        // The PR moves on: the review stays where it was pinned until it is asked for again.
        var moved = PrOrigins.Push(Path.Combine(_root, "work-gadget"), 9, "a fix");
        _github.Prs["acme/gadget#9"] = _github.Prs["acme/gadget#9"] with { Head = moved };
        PrOrigins.Git(first.Worktree, "rev-parse", "HEAD").ShouldBe(_gadgetHead);

        (await Run(Verbs.ReviewPr, "pr/acme/gadget#9", "controller")).Error.ShouldBeNull();
        var second = Row("acme/gadget", 9);
        _github.Cloned.Count.ShouldBe(1);
        _workers.Stopped.ShouldBe([first.ReviewerJob!]);
        (second.Head, second.ReviewerJob, second.State).ShouldBe((moved, "job00002", PrReviewState.Reviewing));
        PrOrigins.Git(second.Worktree, "rev-parse", "HEAD").ShouldBe(moved);
    }

    [Fact]
    public async Task PostsItselfOnAnOpenPr()
    {
        // #241's review rang with a Post button all night; on an open PR pilot posts its review itself (SE1, 2026-10-26).
        var ask = SelfPosting();
        (await ask(Verbs.ReviewPr, "controller")).Error.ShouldBeNull();
        var started = Row("acme/widget", 7);

        (await ask(Verbs.PrVerdict, started.Reviewer, "comment", "Reads well.")).Error.ShouldBeNull();

        _github.Posted.ShouldHaveSingleItem().Number.ShouldBe(7);
        Row("acme/widget", 7).PostedId.ShouldNotBeNull();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrReviews.ReadyKind);
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.PostedKind && c.Bin == CardBin.Handled);
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);

        // Over at once, as after a Post press: the worktree and the reviewer do not wait for the next sweep.
        Row("acme/widget", 7).State.ShouldBe(PrReviewState.Closed);
        Directory.Exists(started.Worktree).ShouldBeFalse();
        _workers.Stopped.ShouldContain(started.ReviewerJob!);
    }

    [Fact]
    public async Task APostGitHubRefusedOnAnOpenPrLeavesAForYouCardWithPostThatSaysWhy()
    {
        var ask = SelfPosting();
        (await ask(Verbs.ReviewPr, "controller")).Error.ShouldBeNull();
        _github.PostFails = new FlightException("GitHub refused the review of acme/widget#7: HTTP 422");

        (await ask(Verbs.PrVerdict, Row("acme/widget", 7).Reviewer, "comment", "A question.")).Error.ShouldBeNull();

        var row = Row("acme/widget", 7);
        row.PostedId.ShouldBeNull();
        row.PostingAt.ShouldBeNull();
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Id, card.Bin, card.Kind).ShouldBe((row.Card, CardBin.ForYou, PrReviews.ReadyKind));
        card.Detail.ShouldNotBeNull().ShouldStartWith("Not posted by the node: GitHub refused the review of acme/widget#7: HTTP 422\n");
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.PostReview);
        _state.Cards().Count(c => c.Kind == PrReviews.ReadyKind).ShouldBe(2);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.PostedKind);
        // Its worktree stays while the card is open, for the Post that is still to come.
        Directory.Exists(row.Worktree).ShouldBeTrue();

        // Put right, the Post press posts it.
        _github.PostFails = null;
        (await ask(Verbs.PostReview, "controller")).Error.ShouldBeNull();
        _github.Posted.ShouldHaveSingleItem().Number.ShouldBe(7);
    }

    [Fact]
    public async Task APostThatWentOutOnAnOpenPrAndNeverSaidBackLeavesAForYouCardWithNoPost()
    {
        var ask = SelfPosting();
        (await ask(Verbs.ReviewPr, "controller")).Error.ShouldBeNull();
        _github.PostFails = new IOException("the pipe broke");

        (await ask(Verbs.PrVerdict, Row("acme/widget", 7).Reviewer, "comment", "A question.")).Error.ShouldBeNull();

        var row = Row("acme/widget", 7);
        row.PostedId.ShouldBeNull();
        row.PostingAt.ShouldNotBeNull();
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Id, card.Bin, card.Kind).ShouldBe((row.Card, CardBin.ForYou, PrReviews.ReadyKind));
        card.Detail.ShouldNotBeNull().ShouldContain("did not come back (the pipe broke), so it may be there: look on https://github.com/acme/widget/pull/7");
        card.Choices.ShouldBeEmpty();

        // Pressed all the same, it is refused and nothing is posted again.
        (await ask(Verbs.PostReview, "controller")).Error.ShouldNotBeNull().ShouldContain("never said back");
        _github.Posted.ShouldBeEmpty();
    }

    private delegate Task<CommandResult> Asker(string verb, string who, params string[] args);

    /// <summary>Reviews as the node runs them, posting a verdict on an open PR itself; asks the verb of PR 7, as the one asking.</summary>
    private Asker SelfPosting()
    {
        var git = new GitCli();
        var reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
        return (verb, who, args) => reviews.RunAsync(new CommandRequest { Verb = verb, Target = "pr/acme/widget#7", Args = args, Who = who }, Ct);
    }

    [Fact]
    public async Task OnlyTheReviewersVerdictRaisesTheCardAndTheWorktreeGoesWhenTheCardCloses()
    {
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        var reviewer = Row("acme/widget", 7).Reviewer;

        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", "controller", "approve", "fine")).Error.ShouldBe("only du1-review-widget-7 gives acme/widget#7's verdict");
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "pass", "fine")).Error.ShouldNotBeNull().ShouldContain("approve, changes or comment");
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "changes")).Error.ShouldNotBeNull().ShouldContain("needs its report");
        (await Run(Verbs.PrVerdict, "pr/acme/gadget#9", reviewer, "approve", "fine")).Error.ShouldBe("acme/gadget#9 has no review; review-pr starts one");

        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "changes", "One defect.\nREADME.md:1 says hello to no one")).Error.ShouldBeNull();

        var row = Row("acme/widget", 7);
        (row.State, row.Verdict, row.Report).ShouldBe((PrReviewState.Reviewed, "changes", "One defect.\nREADME.md:1 says hello to no one"));
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Id, card.Bin, card.Kind, card.Subject).ShouldBe((row.Card, CardBin.ForYou, PrReviews.ReadyKind, "pr/acme/widget#7"));
        card.Title.ShouldBe("acme/widget#7: review ready, changes (PR 7 of acme/widget)");
        card.Detail.ShouldBe($"By ada, reviewed at {_widgetHead[..8]}.\nVerdict changes; posts as REQUEST_CHANGES under controller, 0 inline comments.\n"
            + $"The draft: {_reviews.DraftOf(new PrRef("acme", "widget", 7))}\nOne defect.\nREADME.md:1 says hello to no one");
        var post = card.Choices.ShouldHaveSingleItem();
        (post.Label, post.Verb, post.Target).ShouldBe(("Post", Verbs.PostReview, "pr/acme/widget#7"));
        card.Link.ShouldBe("https://github.com/acme/widget/pull/7");
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "approve", "again")).Error.ShouldBe("acme/widget#7's review is reviewed already");

        // The reviewer is stopped once it has had its grace to end its turn.
        await WaitFor(() => _workers.Stopped.Contains(row.ReviewerJob!));

        // Still there while the card is open; gone, and the review over, once it closes.
        await _reviews.SweepAsync(Ct);
        Directory.Exists(row.Worktree).ShouldBeTrue();
        _state.CloseCard(card.Id, "dismissed", "controller");
        await _reviews.SweepAsync(Ct);
        Directory.Exists(row.Worktree).ShouldBeFalse();
        PrOrigins.Git(WidgetClone, "worktree", "list").ShouldNotContain("review-widget-7");
        Row("acme/widget", 7).State.ShouldBe(PrReviewState.Closed);
    }

    [Fact]
    public async Task AChangesVerdictOnTheControllersOwnPrPostsCommentYetSetsFailureAndPilotChanges()
    {
        _github.Login = "ada";
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        _workers.Started.Single().Brief.ShouldContain("This PR is ada's own");
        var reviewer = Row("acme/widget", 7).Reviewer;

        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "changes", "One defect.")).Error.ShouldBeNull();
        var row = Row("acme/widget", 7);
        row.Verdict.ShouldBe("changes");
        _state.Cards(openOnly: true).ShouldHaveSingleItem().Detail.ShouldNotBeNull().ShouldContain("Verdict changes; posts as COMMENT under ada");
        _github.Statuses.Last().ShouldBe((_widgetHead, "failure", "changes: One defect.", "https://github.com/acme/widget/pull/7"));
        _github.LabelCalls.ShouldBe([(7, PilotMarks.Changes)]);

        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        using var doc = System.Text.Json.JsonDocument.Parse(_github.Posted.Single().Payload);
        doc.RootElement.GetProperty("event").GetString().ShouldBe("COMMENT");
        _github.LabelCalls.Last().ShouldBe((7, PilotMarks.Changes));
    }

    [Fact]
    public async Task ACleanCommentOnTheControllersOwnPrSetsSuccessAndPilotPassed()
    {
        _github.Login = "ada";
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        var reviewer = Row("acme/widget", 7).Reviewer;

        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "comment", "A question on README.md:1")).Error.ShouldBeNull();
        _github.Statuses.Last().State.ShouldBe("success");
        _github.LabelCalls.ShouldBe([(7, PilotMarks.Passed)]);
    }

    /// <summary>Reviews the widget PR with <paramref name="verdict"/> on a base with a merge queue and checks as <paramref name="checks"/>.</summary>
    private async Task ReviewedWidget(string verdict, string checks = ChoiceStatus.Passing, bool queue = true)
    {
        _github.Login = "ada";
        _github.Queue = queue ? new MergeQueueState("OPEN", _widgetHead, false) : null;
        _github.Summary = new ChecksSummary(checks, 1, 1);
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", Row("acme/widget", 7).Reviewer, verdict, "Fine.")).Error.ShouldBeNull();
        await _reviews.SweepAsync(Ct);
    }

    [Fact]
    public async Task ACleanCommentOnOnesOwnPrQueuesItAtTheReviewedHeadOnceAndSaysSo()
    {
        await ReviewedWidget("comment");
        _github.Merged.ShouldBe([(7, _widgetHead, "squash")]);
        Row("acme/widget", 7).QueuedAt.ShouldNotBeNull();
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.QueuedKind && c.Title.Contains("Queued acme/widget#7") && c.Detail!.Contains("Reviewed comment, checks green"));

        await _reviews.SweepAsync(Ct);
        _github.Merged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AChangesVerdictQueuesNothing()
    {
        await ReviewedWidget("changes");
        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueuedAt.ShouldBeNull();
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.ReadyKind);
    }

    [Fact]
    public async Task ANewHeadAfterTheReviewQueuesNothing()
    {
        await ReviewedWidget("approve", checks: ChoiceStatus.Pending);
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Head = new string('a', 40) };
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1);
        await _reviews.SweepAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueueNote.ShouldNotBeNull().ShouldContain("head moved");
    }

    [Fact]
    public async Task PendingChecksWaitThenQueueOnceGreenEvenAfterTheCardClosed()
    {
        await ReviewedWidget("approve", checks: ChoiceStatus.Pending);
        _github.Merged.ShouldBeEmpty();
        _state.CloseCard(Row("acme/widget", 7).Card!, "dismissed", "controller");
        await _reviews.SweepAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).State.ShouldBe(PrReviewState.Closed);

        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1);
        await _reviews.SweepAsync(Ct);
        _github.Merged.ShouldBe([(7, _widgetHead, "squash")]);
        await _reviews.SweepAsync(Ct);
        _github.Merged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FailedChecksQueueNothing()
    {
        await ReviewedWidget("approve", checks: ChoiceStatus.Failing);
        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueueNote.ShouldNotBeNull().ShouldContain("checks fail");
    }

    [Fact]
    public async Task ADraftQueuesNothing()
    {
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Draft = true };
        await ReviewedWidget("approve");
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ABaseWithoutAMergeQueueRaisesTheMergeCard()
    {
        await ReviewedWidget("approve", queue: false);
        _github.Merged.ShouldBeEmpty();
        var card = _state.Cards().Single(c => c.Kind == PrReviews.MergeKind);
        card.Choices.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Verb.ShouldBe(Verbs.Merge), c => c.Args.ShouldBe([_widgetHead]));
        await _reviews.SweepAsync(Ct);
        _state.Cards().Count(c => c.Kind == PrReviews.MergeKind).ShouldBe(1);
    }

    [Fact]
    public async Task AGreenReviewInANoAutoMergeRepoQueuesNothingAndRaisesTheMergeCard()
    {
        _reviews.Models = new RoleModels(new NodeSettings { NoAutoMergeRepos = ["widget"] });
        await ReviewedWidget("approve");
        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueuedAt.ShouldBeNull();
        Row("acme/widget", 7).QueueNote.ShouldNotBeNull().ShouldContain("no-auto-merge");
        var card = _state.Cards().Single(c => c.Kind == PrReviews.MergeKind);
        card.Choices.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Verb.ShouldBe(Verbs.Merge), c => c.Args.ShouldBe([_widgetHead]));
        await _reviews.SweepAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == PrReviews.MergeKind).ShouldBe(1);
    }

    /// <summary>The file the widget PR adds, the one file in its diff, one line long.</summary>
    private string Added() => PrOrigins.Git(WidgetClone, "show", "--name-only", "--format=", _widgetHead);

    [Fact]
    public async Task PostIsOneReviewWithAnInlineCommentOnEachFindingInTheDiffAndIsNeverPostedTwice()
    {
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        var reviewer = Row("acme/widget", 7).Reviewer;
        var added = Added();

        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "changes", "One defect.", "README.md")).Error.ShouldNotBeNull().ShouldContain("a finding is the file and line");
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "changes", "One defect, and a question.",
            $"./{added}:1: the change says nothing", "README.md:1 is this greeting still wanted?")).Error.ShouldBeNull();

        // The finding on the PR's line goes inline; the one on a line it does not touch goes in the body.
        var row = Row("acme/widget", 7);
        row.Findings.ShouldBe([
            new PrFinding { Path = added, Line = 1, Body = "the change says nothing", Inline = true },
            new PrFinding { Path = "README.md", Line = 1, Body = "is this greeting still wanted?", Inline = false },
        ]);
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("Verdict changes; posts as REQUEST_CHANGES under controller, 1 inline comment and 1 in its body, their lines not in the diff.");
        File.Exists(row.Draft.ShouldNotBeNull()).ShouldBeTrue();
        Path.IsPathFullyQualified(row.Draft).ShouldBeTrue();

        (await Run(Verbs.PostReview, "pr/acme/widget#7", reviewer)).Error.ShouldBe("the controller posts acme/widget#7's review, not its reviewer");
        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();

        var (repo, number, path, payload) = _github.Posted.ShouldHaveSingleItem();
        (repo, number, path).ShouldBe(("acme/widget", 7, row.Draft));
        using (var doc = System.Text.Json.JsonDocument.Parse(payload))
        {
            var root = doc.RootElement;
            root.GetProperty("commit_id").GetString().ShouldBe(_widgetHead);
            root.GetProperty("event").GetString().ShouldBe("REQUEST_CHANGES");
            root.GetProperty("body").GetString().ShouldBe("One defect, and a question.\n\nOn lines outside the diff:\n\n- `README.md:1`: is this greeting still wanted?");
            var comment = root.GetProperty("comments").EnumerateArray().ShouldHaveSingleItem();
            (comment.GetProperty("path").GetString(), comment.GetProperty("line").GetInt32(), comment.GetProperty("side").GetString(), comment.GetProperty("body").GetString())
                .ShouldBe((added, 1, "RIGHT", "the change says nothing"));
        }

        // The post is kept, and on its card, which links to it now.
        var posted = Row("acme/widget", 7);
        (posted.PostedId, posted.PostedState, posted.PostedUrl).ShouldBe((9001L, "CHANGES_REQUESTED", "https://github.com/acme/widget/pull/7#pullrequestreview-9001"));
        var closed = _state.Cards().Single(c => c.Id == card.Id);
        (closed.State, closed.Outcome, closed.Link).ShouldBe((CardState.Closed, "Post: review 9001, CHANGES_REQUESTED, under controller", posted.PostedUrl));

        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldBe(
            "acme/widget#7's review is posted already, as review 9001 (https://github.com/acme/widget/pull/7#pullrequestreview-9001); GitHub has it once");
        _github.Posted.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AReviewOfAnyPrSetsItsStatusPendingAtTheStartAndFromItsVerdictAndLinksThePostedReview()
    {
        const string page = "https://github.com/acme/widget/pull/7";
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        _github.Statuses.ShouldHaveSingleItem().ShouldBe((_widgetHead, "pending", "reviewing", page));
        var reviewer = Row("acme/widget", 7).Reviewer;

        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "changes", "One defect.\nREADME.md:1 says hello to no one")).Error.ShouldBeNull();
        _github.Statuses.Last().ShouldBe((_widgetHead, "failure", "changes: One defect.", page));

        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        _github.Statuses.Count.ShouldBe(3);
        _github.Statuses.Last().ShouldBe((_widgetHead, "failure", "changes: One defect.", "https://github.com/acme/widget/pull/7#pullrequestreview-9001"));
        _github.Statuses.ShouldAllBe(s => s.Sha == _widgetHead);
        // The label follows the verdict, at the verdict and again after the post.
        _github.LabelCalls.ShouldBe([(7, PilotMarks.Changes), (7, PilotMarks.Changes)]);
    }

    [Fact]
    public async Task AReviewOfAFlightsPrSetsItsStatusButLeavesTheFlightsLabel()
    {
        // acme/widget#7 is a flight's PR: its pilot: label (passed, conflicts, queued) is the flight's, which PrMarks would
        // not write again over one a review left.
        _state.PutFlight(new FlightRow
        {
            Code = "WID7", Repo = "widget", RepoPath = "/r/widget", Issue = 3, Branch = "flight/wid7", Worktree = "/r/widget/w", Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch, PullRequest = 7, PullRequestUrl = "https://github.com/acme/widget/pull/7",
        });
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", Row("acme/widget", 7).Reviewer, "changes", "One defect.")).Error.ShouldBeNull();

        _github.Statuses.Last().State.ShouldBe("failure");
        _github.LabelCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReviewOfALandedFlightsPrLabelsItAsAnyPrs()
    {
        // Landed, the flight keeps no label on its PR: a later review's label is the PR's.
        _state.PutFlight(new FlightRow
        {
            Code = "WID7", Repo = "widget", RepoPath = "/r/widget", Issue = 3, Branch = "flight/wid7", Worktree = "/r/widget/w", Base = "main",
            State = FlightState.Landed, CreatedAt = DateTimeOffset.UnixEpoch, LandedAt = DateTimeOffset.UnixEpoch, PullRequest = 7, PullRequestUrl = "https://github.com/acme/widget/pull/7",
        });
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", Row("acme/widget", 7).Reviewer, "changes", "One defect.")).Error.ShouldBeNull();

        _github.LabelCalls.ShouldContain((7, PilotMarks.Changes));
    }

    [Fact]
    public async Task AnApprovalIsSuccessAndAStatusGitHubRefusesIsACardNotAFailedReview()
    {
        _github.StatusFails = "GitHub refused the status: no permission";
        (await Run(Verbs.ReviewPr, "pr/acme/gadget#9", "controller")).Error.ShouldBeNull();
        var started = _state.Cards(openOnly: true).Single(c => c.Kind == PrMarks.RefusedKind);
        (started.Bin, started.Title, started.Subject).ShouldBe((CardBin.ForYou, "GitHub refused the pilot/review status on acme/gadget#9", "pr/acme/gadget#9"));
        started.Detail.ShouldNotBeNull().ShouldContain("no permission");
        Row("acme/gadget", 9).State.ShouldBe(PrReviewState.Reviewing);
        _github.Statuses.ShouldBeEmpty();

        _github.StatusFails = null;
        (await Run(Verbs.PrVerdict, "pr/acme/gadget#9", Row("acme/gadget", 9).Reviewer, "approve", "fine")).Error.ShouldBeNull();

        Row("acme/gadget", 9).State.ShouldBe(PrReviewState.Reviewed);
        // The status went through, so the refusal it left is over.
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrMarks.RefusedKind);
        _github.Statuses.ShouldHaveSingleItem().ShouldBe((_gadgetHead, "success", $"approved at {_gadgetHead[..8]}", "https://github.com/acme/gadget/pull/9"));
    }

    [Theory]
    [InlineData("approve", "bob", "APPROVE")]
    [InlineData("changes", "bob", "REQUEST_CHANGES")]
    [InlineData("comment", "bob", "COMMENT")]
    [InlineData("approve", "ada", "COMMENT")]
    [InlineData("changes", "ADA", "COMMENT")]
    public void TheEventFollowsTheVerdictAndWhosePrItIs(string verdict, string login, string expected) =>
        PrReviews.Event(verdict, "ada", login).ShouldBe(expected);

    [Fact]
    public async Task TheEventIsTheLoginsThatPostsNotTheOneThatGaveTheVerdict()
    {
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", Row("acme/widget", 7).Reviewer, "approve", "Right as it is.")).Error.ShouldBeNull();
        File.ReadAllText(Row("acme/widget", 7).Draft!).ShouldContain("\"event\": \"APPROVE\"");

        // gh is signed in as the PR's author by the time it is posted: GitHub would refuse an approval there.
        _github.Login = "ada";
        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();

        _github.Posted.ShouldHaveSingleItem().Payload.ShouldContain("\"event\": \"COMMENT\"");
        Row("acme/widget", 7).PostedState.ShouldBe("COMMENTED");
    }

    [Fact]
    public async Task APostGitHubRefusedMayGoAgainButOneThatNeverSaidBackDoesNot()
    {
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", Row("acme/widget", 7).Reviewer, "comment", "A question.")).Error.ShouldBeNull();

        _github.PostFails = new FlightException("GitHub refused the review of acme/widget#7: HTTP 422");
        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldBe("GitHub refused the review of acme/widget#7: HTTP 422");
        Row("acme/widget", 7).PostingAt.ShouldBeNull();

        _github.PostFails = new IOException("the pipe broke");
        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("did not come back (the pipe broke)");
        Row("acme/widget", 7).PostingAt.ShouldNotBeNull();

        _github.PostFails = null;
        (await Run(Verbs.PostReview, "pr/acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("never said back, so it may be there");
        _github.Posted.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("src/A.cs:42 the null check is gone", "src/A.cs", 42, "the null check is gone")]
    [InlineData("src\\A.cs:42: gone", "src/A.cs", 42, "gone")]
    [InlineData("./docs/a.md:3\nfirst\n\nsecond", "docs/a.md", 3, "first\n\nsecond")]
    public void AFindingIsAPathAndALineThenItsBody(string text, string path, int line, string body) =>
        PrFinding.Parse(text).ShouldBe(new PrFinding { Path = path, Line = line, Body = body });

    [Theory]
    [InlineData("README.md the line is missing")]
    [InlineData("README.md:0 zero")]
    [InlineData("README.md:1")]
    [InlineData("/etc/passwd:1 rooted")]
    [InlineData("C:/x.cs:1 a drive")]
    [InlineData("../x.cs:1 outside")]
    public void AnythingElseIsNoFinding(string text) => PrFinding.Parse(text).ShouldBeNull();

    [Fact]
    public void TheDiffsLinesAreReadHunkByHunkByTheirCounts()
    {
        var diff = string.Join('\n',
            "diff --git a/a.cs b/a.cs",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,3 +1,4 @@",
            " one",
            "-two",
            "+++ not a header, a line that starts with two pluses",
            "+2b",
            " three",
            "@@ -10 +11,0 @@",
            "-gone",
            "diff --git a/gone.cs b/gone.cs",
            "--- a/gone.cs",
            "+++ /dev/null",
            "@@ -1 +0,0 @@",
            "-x",
            "diff --git a/b c.md b/b c.md",
            "--- /dev/null",
            "+++ b/b c.md\t",
            "@@ -0,0 +1 @@",
            "+new",
            "\\ No newline at end of file",
            "");

        var lines = PrReviews.DiffLines(diff);

        lines.Keys.Order().ShouldBe(["a.cs", "b c.md"]);
        lines["a.cs"].ShouldBe([(1, 4)]);
        lines["b c.md"].ShouldBe([(1, 1)]);
    }

    [Fact]
    public async Task APrNotOpenOrNotOnGitHubIsRefusedAndNothingIsMade()
    {
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "MERGED" };

        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBe("acme/widget#7 is merged, not open");
        (await Run(Verbs.ReviewPr, "pr/acme/nothing#1", "controller")).Error.ShouldNotBeNull().ShouldContain("gh could not read acme/nothing#1");
        (await Run(Verbs.ReviewPr, "flight/TIA1", "controller")).Error.ShouldNotBeNull().ShouldContain("takes a PR target");
        _state.PrReviews().ShouldBeEmpty();
        _workers.Started.ShouldBeEmpty();
        Directory.Exists(Path.Combine(WidgetClone, ".claude")).ShouldBeFalse();
    }

    [Fact]
    public async Task AReviewerThatCannotStartLeavesNoWorktree()
    {
        _workers.Fail = new FlightException("claude --bg did not start du1-review-widget-7 (1): folder not trusted");

        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("not trusted");

        _state.PrReviews().ShouldBeEmpty();
        Directory.Exists(Path.Combine(WidgetClone, ".claude", "worktrees", "review-widget-7")).ShouldBeFalse();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }
}

/// <summary>How a PR is named, and what its reviewer's session is: no git needed.</summary>
public sealed class PrRefTests
{
    [Theory]
    [InlineData("acme/widget#7", "acme", "widget", 7)]
    [InlineData("pr/acme/widget#7", "acme", "widget", 7)]
    [InlineData("https://github.com/DrawboardLtd/bullclip-backend/pull/2479", "DrawboardLtd", "bullclip-backend", 2479)]
    [InlineData("https://github.com/SharpAstro/pilot/pull/113/files", "SharpAstro", "pilot", 113)]
    [InlineData("SharpAstro/.github#4", "SharpAstro", ".github", 4)]
    public void APrIsNamedByOwnerRepoAndNumberOrByItsUrl(string text, string owner, string name, int number) =>
        PrRef.Parse(text).ShouldBe(new PrRef(owner, name, number));

    [Theory]
    [InlineData("acme/widget")]
    [InlineData("widget#7")]
    [InlineData("acme/widget#x")]
    [InlineData("a/b/c#1")]
    [InlineData("https://github.com/acme/widget/issues/7")]
    [InlineData("https://example.com/acme/widget/pull/7")]
    [InlineData("acme/../x#1")]
    public void AnythingElseNamesNoPr(string text) => PrRef.Parse(text).ShouldBeNull();

    [Fact]
    public void APrsReviewerIsAReviewerOfNoFlight() =>
        Pilot.Node.Sessions.SessionRoles.Resolve("du1-review-bullclip-backend-2479").ShouldBe(new Pilot.Node.Sessions.SessionRoleInfo(SessionRole.Reviewer, null, null, null));
}

/// <summary>
/// The Done-when end to end, from the CLI against a real node: <c>pilot review pr</c> on a PR with no clone here, the
/// reviewer's verdict from its worktree, the card, and the worktree gone once the card is dismissed.
/// </summary>
public sealed class PrReviewCliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2q-" + Guid.NewGuid().ToString("N")[..8]);
    private NodeHarness _node = null!;
    private string _head = "";

    public async ValueTask InitializeAsync()
    {
        var (origin, head) = PrOrigins.Make(_root, "gadget", 9);
        _head = head;
        _node = await NodeHarness.StartAsync(Ct);
        _node.GitHub.Origins["acme/gadget"] = origin;
        _node.GitHub.Prs["acme/gadget#9"] = new PullRequest(9, "OPEN", head, "https://github.com/acme/gadget/pull/9", Title: "Gadgets", Author: "bob", Base: "main");
    }

    public async ValueTask DisposeAsync()
    {
        // The cache first: git's read-only objects would keep the node's own folder from going.
        await _node.App.StopAsync(Ct);
        PrOrigins.Delete(Path.Combine(_node.Options.DataFolder, "clones"));
        await _node.DisposeAsync();
        PrOrigins.Delete(_root);
    }

    private async Task<(int Exit, string Output, string Error)> Pilot(string cwd, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], cwd, output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task APrIsReviewedFromItsLinkAndItsVerdictIsPostedByTheNodeWhileThePrIsOpen()
    {
        var started = await Pilot(_root, "review", "pr", "https://github.com/acme/gadget/pull/9");
        started.Exit.ShouldBe(0, started.Output + started.Error);
        var review = _node.State.PrReviews().ShouldHaveSingleItem();
        review.ClonePath.ShouldBe(Path.Combine(_node.Options.DataFolder, "clones", "acme", "gadget"));
        PrOrigins.Git(review.Worktree, "rev-parse", "HEAD").ShouldBe(_head);
        (await Pilot(_root, "status")).Output.ShouldContain($"PR acme/gadget#9  Gadgets  (reviewing, {review.Reviewer}, at {_head[..8]})");

        // The reviewer, from its worktree: the folder says which PR, and the verdict goes as the reviewer's.
        var folder = Path.Combine(review.Worktree, "sub");
        Directory.CreateDirectory(folder);
        var verdict = await Pilot(folder, "review", "changes", "One defect, and the word pr in it.\nREADME.md:1 greets no one");
        verdict.Exit.ShouldBe(0, verdict.Output + verdict.Error);
        // Posted by the node, the review is over at once: its worktree, folder and all, is gone with no wait for a sweep.
        Directory.Exists(review.Worktree).ShouldBeFalse();

        // The PR is open, so the node posts it at once: nothing rings, a Handled card says what went out.
        var (number, payload) = (_node.GitHub.Posted.ShouldHaveSingleItem().Number, _node.GitHub.Posted[0].Payload);
        number.ShouldBe(9);
        payload.ShouldContain("README.md:1");
        _node.State.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrReviews.ReadyKind);
        _node.State.Cards().ShouldContain(c => c.Kind == PrReviews.PostedKind && c.Bin == CardBin.Handled);
        _node.State.PrReviews().Single().PostedId.ShouldNotBeNull();
    }

    [Fact]
    public async Task ThePostChoicePostsOneReviewWithItsInlineCommentsAndNotFromTheWatchtower()
    {
        (await Pilot(_root, "review", "pr", "acme/gadget#9")).Exit.ShouldBe(0);
        var review = _node.State.PrReviews().ShouldHaveSingleItem();
        var added = PrOrigins.Git(review.Worktree, "show", "--name-only", "--format=", "HEAD");

        // A finding that is no path and line is the reviewer's mistake, caught before it reaches the node.
        var wrong = await Pilot(review.Worktree, "review", "changes", "One defect.", "--finding", "the change is wrong");
        (wrong.Exit, wrong.Error.Contains("a finding is path:line")).ShouldBe((2, true));
        // Merged meanwhile: the node does not post a review to a PR no longer open, so it waits on its card's Post.
        _node.GitHub.Prs["acme/gadget#9"] = _node.GitHub.Prs["acme/gadget#9"] with { State = "MERGED" };
        var verdict = await Pilot(review.Worktree, "review", "changes", "One defect.", "--finding", $"{added}:1 says only the change", "--finding", "README.md:1 greets no one");
        verdict.Exit.ShouldBe(0, verdict.Output + verdict.Error);
        var card = _node.State.Cards(openOnly: true).ShouldHaveSingleItem();

        // Posting is the controller's: the watchtower's answer is refused and the card stays.
        var watchtower = $"{_node.Options.Machine.ToLowerInvariant()}-watchtower";
        var refused = await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = $"card/{card.Id}", Args = ["Post"], Who = watchtower }, Ct);
        refused.Response.ShouldNotBeNull().Error.ShouldBe("Post was refused: post-review is the controller's, not the watchtower's: leave its card open for them");
        _node.GitHub.Posted.ShouldBeEmpty();

        var post = await Pilot(_root, "answer", card.Id, "Post");
        post.Exit.ShouldBe(0, post.Output + post.Error);

        var (repo, number, path, payload) = _node.GitHub.Posted.ShouldHaveSingleItem();
        (repo, number, path).ShouldBe(("acme/gadget", 9, Path.Combine(Path.GetFullPath(_node.Options.DataFolder), "reviews", "acme", "gadget-9.json")));
        payload.ShouldContain("\"event\": \"REQUEST_CHANGES\"");
        payload.ShouldContain($"\"path\": \"{added}\"");
        payload.ShouldContain("- `README.md:1`: greets no one");
        _node.State.Cards().Single(c => c.Id == card.Id).Outcome.ShouldBe("Post: review 9001, CHANGES_REQUESTED, under controller");

        // Posted, its card closed, so the review is over and its worktree gone; a second answer finds the card closed.
        Directory.Exists(review.Worktree).ShouldBeFalse();
        (await Pilot(_root, "answer", card.Id, "Post")).Exit.ShouldBe(1);
        _node.GitHub.Posted.Count.ShouldBe(1);
    }

    [Fact]
    public void AVerdictWhoseWordsSayPrIsStillAVerdict()
    {
        var parsed = PilotCommand.Build(_root, TextWriter.Null, TextWriter.Null).Parse(["review", "changes", "fix", "the", "pr", "title"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandResult.Command.Name.ShouldBe("changes");
        parsed.GetValue<string[]>("report").ShouldBe(["fix", "the", "pr", "title"]);
    }

    [Fact]
    public async Task ANameThatIsNoPrIsAUsageMistake()
    {
        var (exit, _, error) = await Pilot(_root, "review", "pr", "acme/gadget");
        exit.ShouldBe(2);
        error.ShouldContain("a PR is owner/repo#n or its URL");
    }
}
