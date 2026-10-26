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
public sealed class PrFixTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2f-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FakeGitHub _github = new();
    private readonly PrReviews _reviews;
    private readonly NodeOptions _options;

    /// <summary>acme/widget has a clone under the repo roots and a branch on its origin; acme/gadget has no clone.</summary>
    public PrFixTests()
    {
        var (widget, widgetHead) = PrOrigins.Make(_root, "widget", 7);
        var (gadget, gadgetHead) = PrOrigins.Make(_root, "gadget", 9);
        PrOrigins.Git(Path.Combine(_root, "work-widget"), "push", "-q", "origin", "HEAD:refs/heads/feature/seven");
        Directory.CreateDirectory(Roots);
        PrOrigins.Git(Roots, "clone", "-q", widget, "widget");
        PrOrigins.Git(WidgetClone, "config", "remote.origin.url", "https://github.com/acme/widget.git");
        PrOrigins.Git(WidgetClone, "config", $"url.{widget.Replace('\\', '/')}.insteadOf", "https://github.com/acme/widget.git");
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

    private string Roots => Path.Combine(_root, "repos");

    private string WidgetClone => Path.Combine(Roots, "widget");

    private static PullRequest Pr(string repo, int number, string head, string author) =>
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

    private Task<CommandResult> Run(string verb, string pr, string who, params string[] args) =>
        _reviews.RunAsync(new CommandRequest { Verb = verb, Target = $"pr/{pr}", Args = args, Who = who }, Ct);

    /// <summary>Reviews <paramref name="pr"/> and gives <paramref name="verdict"/> with these findings; the reviewer's own words.</summary>
    private async Task<PrReviewRow> Review(string pr, string verdict, params string[] findings)
    {
        (await Run(Verbs.ReviewPr, pr, "controller")).Error.ShouldBeNull();
        var row = _reviews.Of(PrRef.Parse(pr)!).ShouldNotBeNull();
        (await Run(Verbs.PrVerdict, pr, row.Reviewer, [verdict, "Two things are wrong.", .. findings])).Error.ShouldBeNull();
        return _reviews.Of(PrRef.Parse(pr)!).ShouldNotBeNull();
    }

    private WorkerStart[] Fixers => [.. _workers.Started.Where(s => s.Name.Contains("-fix-"))];

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
        row.State.ShouldBe(PrReviewState.Reviewed);
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
    public async Task ADesignQuestionRingsAndStartsNoFixer()
    {
        await Review("acme/widget#7", "changes", "README.md:1 design: should this be a service at all?");

        Fixers.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldContain("should this be a service at all?");
        _reviews.Of(new PrRef("acme", "widget", 7))!.Fixer.ShouldBeNull();
    }

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

        await Review("acme/widget#7", "changes", "README.md:1 wrong");

        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixOfferKind || c.Kind == PrReviews.DesignKind);
        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("a flight owns");
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
}
