using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A changes verdict from pilot's review of a PR no flight owns starts a fixer on the PR's branch (#290): on the controller's
/// own PR in a repo the node tracks, by itself; on anyone else's, from a For-you card's Fix it; never for a design question,
/// an untracked repo, or a PR a flight owns. GitHub is a fake gh over bare repos; git is real.
/// </summary>
public abstract class PrFixTestsBase : IDisposable
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "p2f-" + Guid.NewGuid().ToString("N")[..8]);
    private protected readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private protected readonly FakeWorkers _workers = new();
    private protected readonly FakeGitHub _github = new();
    private protected readonly PrReviews _reviews;
    private protected readonly NodeOptions _options;

    /// <summary>acme/widget has a clone under the repo roots and a branch on its origin; acme/gadget has no clone.</summary>
    // The two origins, widget's branch and its clone, built once a run and copied into each test's folder, as PrReviewTests'
    // are: about twenty git processes a test otherwise, and the class took 115 s on CI's Windows runner (2026-10-27).
    private protected static readonly Lazy<(string Root, string WidgetHead, string GadgetHead)> Seed = new(() =>
    {
        var root = SeedRepo.NewTemplateRoot();
        var (widget, widgetHead) = PrOrigins.Make(root, "widget", 7);
        var (_, gadgetHead) = PrOrigins.Make(root, "gadget", 9);
        PrOrigins.Git(Path.Combine(root, "work-widget"), "push", "-q", "origin", "HEAD:refs/heads/feature/seven");
        var roots = Path.Combine(root, "repos");
        Directory.CreateDirectory(roots);
        PrOrigins.Git(roots, "clone", "-q", widget, "widget");
        var clone = Path.Combine(roots, "widget");
        PrOrigins.Git(clone, "config", "remote.origin.url", "https://github.com/acme/widget.git");
        PrOrigins.Git(clone, "config", $"url.{widget.Replace('\\', '/')}.insteadOf", "https://github.com/acme/widget.git");
        return (root, widgetHead, gadgetHead);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    protected PrFixTestsBase()
    {
        SeedRepo.CopyTree(Seed.Value.Root, _root);
        var (widgetHead, gadgetHead) = (Seed.Value.WidgetHead, Seed.Value.GadgetHead);
        var gadget = Path.Combine(_root, "github", "gadget.git");
        _github.Origins["acme/gadget"] = gadget;
        _github.Login = "ada";
        _github.Prs["acme/widget#7"] = Pr("acme/widget", 7, widgetHead, "ada") with { HeadRef = "feature/seven" };
        _github.Prs["acme/gadget#9"] = Pr("acme/gadget", 9, gadgetHead, "ada") with { HeadRef = "feature/nine" };

        var git = new GitCli();
        _options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1", RepoRootsGiven: [Roots]);
        _reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
    }

    private protected string Roots => Path.Combine(_root, "repos");

    private protected string WidgetClone => Path.Combine(Roots, "widget");

    private protected static PullRequest Pr(string repo, int number, string head, string author) =>
        new(number, "OPEN", head, $"https://github.com/{repo}/pull/{number}", Title: $"PR {number} of {repo}", Author: author, Base: "main");

    public void Dispose()
    {
        try
        {
            PrOrigins.Git(WidgetClone, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        PrOrigins.Delete(_root);
    }

    private protected Task<CommandResult> Run(string verb, string pr, string who, params string[] args) =>
        _reviews.RunAsync(new CommandRequest { Verb = verb, Target = $"pr/{pr}", Args = args, Who = who }, Ct);

    /// <summary>Reviews <paramref name="pr"/> and gives <paramref name="verdict"/> with these findings; the reviewer's own words.</summary>
    private protected async Task<PrReviewRow> Review(string pr, string verdict, params string[] findings)
    {
        (await Run(Verbs.ReviewPr, pr, "controller")).Error.ShouldBeNull();
        var row = _reviews.Of(PrRef.Parse(pr)!).ShouldNotBeNull();
        (await Run(Verbs.PrVerdict, pr, row.Reviewer, [verdict, "Two things are wrong.", .. findings])).Error.ShouldBeNull();
        // A review the node posted ends after the reviewer's grace, not with the verdict's answer: wait it out.
        for (var i = 0; i < 500 && _reviews.Of(PrRef.Parse(pr)!) is { PostedId: not null, State: not PrReviewState.Closed }; i++)
        {
            await Task.Delay(10, Ct);
        }
        return _reviews.Of(PrRef.Parse(pr)!).ShouldNotBeNull();
    }

    private protected WorkerStart[] Fixers => [.. _workers.Started.Where(s => s.Name.Contains("-fix-"))];

    private protected static readonly PrRef Seven = new("acme", "widget", 7);

    /// <summary>The fixer pushes a commit to the PR: its head on GitHub moves; returns the new head.</summary>
    private protected string FixerPushes(string what)
    {
        var head = PrOrigins.Push(Path.Combine(_root, "work-widget"), 7, what);
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Head = head };
        return head;
    }

    private protected string FixWorktree => Path.Combine(WidgetClone, ".claude", "worktrees", "fix-widget-7");

    private protected PrReviewRow Row => _reviews.Of(Seven).ShouldNotBeNull();

    /// <summary>One fresh review at <paramref name="head"/> after a fixer's push, its reviewer giving <paramref name="verdict"/>.</summary>
    private protected async Task Round(string head, string verdict, params string[] findings)
    {
        await _reviews.SweepAsync(Ct);
        Row.Head.ShouldBe(head);
        Row.State.ShouldBe(PrReviewState.Reviewing);
        (await Run(Verbs.PrVerdict, "acme/widget#7", Row.Reviewer, [verdict, "Round report.", .. findings])).Error.ShouldBeNull();
    }

    private protected static SessionRow Session(string name, SessionState state) => new()
    {
        Id = "abcd1234", SessionId = "abcd1234-0000", Name = name, Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Worker,
        ContextTokens = 0, ContextWindow = 1, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>Part 1 of PrFixTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class PrFixTests1 : PrFixTestsBase
{
    [Fact]
    public async Task AChangesVerdictOnAnOwnPrInATrackedRepoStartsOneFixerOnThePrsBranch()
    {
        var row = await Review("acme/widget#7", "changes", "README.md:1 hello is not a greeting", "README.md:1 and no test");

        var start = Fixers.ShouldHaveSingleItem();
        var worktree = Path.Combine(WidgetClone, ".claude", "worktrees", "fix-widget-7");
        (start.Name, start.RepoPath, start.Plane).ShouldBe(("du1-fix-widget-7", WidgetClone, "fix-widget-7"));
        (row.Fixer, row.FixWorktree, row.FixRound, row.FixerJob).ShouldBe(("du1-fix-widget-7", worktree, 1, "job00002"));
        PrOrigins.Git(worktree, "symbolic-ref", "--short", "HEAD").ShouldBe("feature/seven");
        start.Brief.ShouldContain("README.md:1 hello is not a greeting");
        start.Brief.ShouldContain("Two things are wrong.");
        start.Brief.ShouldContain("never rebase");
        start.Brief.ShouldContain("never with force");
        start.Brief.ShouldContain("gh pr comment 7 -R acme/widget");
        start.Brief.ShouldContain("End your turn once the push");
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.FixStartedKind && c.Bin == CardBin.Handled);

        // The review ends with its post, and takes neither the fixer nor its worktree.
        row.State.ShouldBe(PrReviewState.Closed);
        await _reviews.SweepAsync(Ct);
        var after = _reviews.Of(new PrRef("acme", "widget", 7)).ShouldNotBeNull();
        (after.State, after.Fixer, after.FixerJob).ShouldBe((PrReviewState.Closed, "du1-fix-widget-7", "job00002"));
        Directory.Exists(worktree).ShouldBeTrue();
        _workers.Stopped.ShouldNotContain("job00002");

        // Another verdict, or a sweep, starts no second.
        (await Run(Verbs.PrVerdict, "acme/widget#7", after.Reviewer, "changes", "Again.")).Error.ShouldNotBeNull();
        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("fixing it already");
        await _reviews.SweepAsync(Ct);
        Fixers.Length.ShouldBe(1);
    }

    [Fact]
    public async Task AFreshReviewKeepsTheFixerAndItsRepeatedChangesVerdictStartsNoSecond()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        await Review("acme/widget#7", "changes", "README.md:1 still wrong");

        Fixers.ShouldHaveSingleItem();
        _reviews.Of(new PrRef("acme", "widget", 7))!.Fixer.ShouldBe("du1-fix-widget-7");
    }

    [Fact]
    public async Task AnApproveOnAnOwnPrStartsNoFixer()
    {
        await Review("acme/widget#7", "approve");

        Fixers.ShouldBeEmpty();
    }

    [Fact]
    public async Task APrByAnotherLoginRaisesAForYouCardWhoseFixItStartsTheSameFixer()
    {
        _github.Login = "controller";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");

        Fixers.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixOfferKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.ForYou);
        var choice = card.Choices.ShouldHaveSingleItem();
        (choice.Label, choice.Verb, choice.Target).ShouldBe(("Fix it", Verbs.FixPr, "pr/acme/widget#7"));

        (await Run(choice.Verb, "acme/widget#7", "controller")).Error.ShouldBeNull();

        var start = Fixers.ShouldHaveSingleItem();
        (start.Name, start.RepoPath).ShouldBe(("du1-fix-widget-7", WidgetClone));
        _reviews.Of(new PrRef("acme", "widget", 7))!.FixRound.ShouldBe(1);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrReviews.FixOfferKind);
    }

    [Fact]
    public async Task TheWatchtowersFixerOnAutopilotKeepsTheGuardsTheNodesOwnFixerKeeps()
    {
        // The review of #470: review fix from the watchtower on autopilot must not start what the node's own fixer would not.
        Task<CommandResult> OnAutopilot(string pr) =>
            _reviews.RunAsync(new CommandRequest { Verb = Verbs.FixPr, Target = $"pr/{pr}", Args = [], Who = "du1-watchtower", Autopilot = true }, Ct);

        // Another author's PR, Fix it not chosen: refused, and the person's card stays.
        _github.Login = "controller";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        (await OnAutopilot("acme/widget#7")).Error.ShouldNotBeNull().ShouldContain("Fix it has not been chosen");
        Fixers.ShouldBeEmpty();
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrReviews.FixOfferKind);
    }

    [Fact]
    public async Task ADesignQuestionRingsAndStartsNoFixer()
    {
        await Review("acme/widget#7", "changes", "README.md:1 design: should this be a service at all?");

        Fixers.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldContain("should this be a service at all?");
        _reviews.Of(new PrRef("acme", "widget", 7))!.Fixer.ShouldBeNull();
    }
}

/// <summary>Part 2 of PrFixTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class PrFixTests2 : PrFixTestsBase
{
    [Fact]
    public async Task AnUntrackedRepoGetsNoFixerAndNoCard()
    {
        await Review("acme/gadget#9", "changes", "README.md:1 wrong");

        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixOfferKind || c.Kind == PrReviews.DesignKind || c.Kind == PrReviews.FixFailedKind);
        (await Run(Verbs.FixPr, "acme/gadget#9", "controller")).Error.ShouldNotBeNull().ShouldContain("not a repo the node tracks");
    }

    [Fact]
    public async Task APrAFlightOwnsStartsNoFixer()
    {
        _state.PutFlight(new FlightRow
        {
            Code = "WID1", Repo = "widget", RepoPath = WidgetClone, Issue = 1, Branch = "feature/seven", Worktree = Path.Combine(_root, "plane-WID1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 7, PullRequestUrl = "https://github.com/acme/widget/pull/7",
        });

        // A flight's PR has one review, the flight's (PIL483): review-pr on it is refused, so no reviewer and no fixer start.
        (await Run(Verbs.ReviewPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("flight review WID1");
        _reviews.Of(new PrRef("acme", "widget", 7)).ShouldBeNull();

        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixOfferKind || c.Kind == PrReviews.DesignKind);
        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("flight review WID1");
    }

    [Fact]
    public async Task AFixersPushStopsItRemovesItsWorktreeAndStartsOneFreshReviewCarryingTheRound()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        Directory.Exists(FixWorktree).ShouldBeTrue();
        var head = FixerPushes("fixed");

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldContain("job00002");
        Directory.Exists(FixWorktree).ShouldBeFalse();
        var row = Row;
        (row.State, row.Head, row.Fixer, row.FixWorktree, row.FixRound).ShouldBe((PrReviewState.Reviewing, head, null, null, 1));
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(2);
        Fixers.Length.ShouldBe(1);
    }

    [Fact]
    public async Task AFreshReviewOfAFixersPushThatPassesWithGreenChecksQueuesThePr()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var head = FixerPushes("fixed");
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1);
        _github.Queue = new MergeQueueState("OPEN", head, false);

        await Round(head, "approve");
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBe([(7, head, "squash")]);
        Row.QueuedAt.ShouldNotBeNull();
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.QueuedKind && c.Bin == CardBin.Handled);
        Fixers.Length.ShouldBe(1);
    }

    [Fact]
    public async Task ChangesOnRoundTwoStartsTheNextFixerWithRoundTwoAndStatusSaysSo()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        await Round(FixerPushes("fixed once"), "changes", "README.md:1 still wrong");

        Fixers.Length.ShouldBe(2);
        Fixers[1].Brief.ShouldContain("README.md:1 still wrong");
        (Row.Fixer, Row.FixRound).ShouldBe(("du1-fix-widget-7", 2));
        var lines = StatusMarkdown.Build(_state.Snapshot()).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        lines[Array.FindIndex(lines, l => l.Contains("acme/widget#7")) + 1].ShouldContain("fixing, du1-fix-widget-7, round 2");
    }
}

/// <summary>Part 3 of PrFixTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class PrFixTests3 : PrFixTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ChangesOnRoundThreeRingsTheRoundsAndStartsNoFourthFixer()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        await Round(FixerPushes("one"), "changes", "README.md:1 wrong again");
        await Round(FixerPushes("two"), "changes", "README.md:1 wrong a third time");
        Fixers.Length.ShouldBe(3);
        Row.FixRound.ShouldBe(3);

        await Round(FixerPushes("three"), "changes", "README.md:1 never right");

        Fixers.Length.ShouldBe(3);
        Row.Fixer.ShouldBeNull();
        var ring = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixCappedKind).ShouldHaveSingleItem();
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Title.ShouldContain("acme/widget#7");
        ring.Title.ShouldContain("3 rounds");
        ring.Detail.ShouldNotBeNull().ShouldContain("never right");
        ring.Link.ShouldNotBeNull().ShouldContain("/pull/7");
        // Nor does the watchtower on autopilot start a fourth (the review of #470): that is the person's.
        (await _reviews.RunAsync(new CommandRequest { Verb = Verbs.FixPr, Target = "pr/acme/widget#7", Args = [], Who = "du1-watchtower", Autopilot = true }, Ct))
            .Error.ShouldNotBeNull().ShouldContain("rounds of fixes");
        Fixers.Length.ShouldBe(3);
    }

    [Fact]
    public async Task AFixerThatEndsWithoutPushingLeavesACardWithFixItAndNoReview()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        _state.PutSession(Session("du1-fix-widget-7", SessionState.Ended));

        await _reviews.SweepAsync(Ct);

        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixEndedKind).ShouldHaveSingleItem();
        (card.Bin, card.Choices.ShouldHaveSingleItem().Label).ShouldBe((CardBin.ForYou, "Fix it"));
        Directory.Exists(FixWorktree).ShouldBeFalse();
        Row.State.ShouldBe(PrReviewState.Closed);
        Row.Fixer.ShouldBeNull();
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(1);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();
        Row.FixRound.ShouldBe(2);
    }

    [Fact]
    public async Task APrMergedMeanwhileEndsItsFixerAndWorktree()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "MERGED" };

        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldContain("job00002");
        Directory.Exists(FixWorktree).ShouldBeFalse();
        Row.Fixer.ShouldBeNull();
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(1);
    }

    [Fact]
    public async Task AReviewStartedByHandKeepsTheRoundCount()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        await Round(FixerPushes("one"), "changes", "README.md:1 wrong again");
        Row.FixRound.ShouldBe(2);

        (await Run(Verbs.ReviewPr, "acme/widget#7", "controller")).Error.ShouldBeNull();

        Row.FixRound.ShouldBe(2);
    }

    [Fact]
    public async Task StatusListsTheFixUnderTheReviewLineEvenOnceTheReviewClosed()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        await _reviews.SweepAsync(Ct);
        _reviews.Of(new PrRef("acme", "widget", 7))!.State.ShouldBe(PrReviewState.Closed);

        var lines = StatusMarkdown.Build(_state.Snapshot()).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var at = Array.FindIndex(lines, l => l.Contains("acme/widget#7"));
        at.ShouldBeGreaterThanOrEqualTo(0);
        lines[at + 1].ShouldContain("fixing, du1-fix-widget-7, round 1");
    }

    [Fact]
    public async Task AFixPrOnAPrWithNoReviewHereStartsAReviewFirstAndAFixPrOnAPassedReviewIsStillRefused()
    {
        var log = new StringWriter();
        _reviews.Error = log;
        _reviews.Of(Seven).ShouldBeNull();

        var made = await Run(Verbs.FixPr, "acme/widget#7", "controller");

        made.Error.ShouldBeNull();
        var row = Row;
        (row.State, row.Fixer).ShouldBe((PrReviewState.Reviewing, null));
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(1);
        Fixers.ShouldBeEmpty();
        log.ToString().ShouldContain($"fix-pr pr/acme/widget#7, asked by controller: no review of acme/widget#7 here, so a review was started first (started {row.Reviewer} at {row.Head[..8]})");

        // Its verdict then goes the usual way: an approve leaves nothing to fix, and a fix-pr on it is refused.
        (await Run(Verbs.PrVerdict, "acme/widget#7", row.Reviewer, "approve", "Fine.")).Error.ShouldBeNull();
        Fixers.ShouldBeEmpty();
        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("did not ask for changes");
        _workers.Started.Count(s => s.Name.Contains("-review-")).ShouldBe(1);
    }

    [Fact]
    public async Task AFixPrOnAPrWithNoReviewHereFixesItsFindingsWhenTheNewReviewAsksForChanges()
    {
        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();
        var reviewer = Row.Reviewer;

        (await Run(Verbs.PrVerdict, "acme/widget#7", reviewer, "changes", "Wrong.", "README.md:1 wrong")).Error.ShouldBeNull();

        Fixers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AFixPrWritesOneLineToTheLogStartedWhenItStartsAFixerAndRefusedWhenItDoesNot()
    {
        var log = new StringWriter();
        _reviews.Error = log;
        _github.Login = "controller";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        log.GetStringBuilder().Clear();

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();
        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull();

        var lines = log.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe($"fix-pr pr/acme/widget#7, asked by controller: started du1-fix-widget-7 at {Row.Head[..8]}");
        lines[1].ShouldStartWith("fix-pr pr/acme/widget#7, asked by controller: refused: ");
        lines[1].ShouldContain("fixing it already");
    }
}
