using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The <c>pilot: recheck</c> label (#286): the node sees it on any open PR of a tracked repo, decides whether the PR is its
/// own to look at, and runs one recheck of it at a time. GitHub is a fake gh over bare repos; git is real.
/// </summary>
public sealed class PrRecheckTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FakeGitHub _github = new();
    private readonly FakeFleet _fleet = new();
    private readonly PrReviews _reviews;
    private readonly PrRecheck _recheck;
    private readonly string _head;

    public PrRecheckTests()
    {
        var (widget, head) = PrOrigins.Make(_root, "widget", 7);
        _head = head;
        Directory.CreateDirectory(Roots);
        PrOrigins.Git(Roots, "clone", "-q", widget, "widget");
        PrOrigins.Git(WidgetClone, "config", "remote.origin.url", "https://github.com/acme/widget.git");
        PrOrigins.Git(WidgetClone, "config", $"url.{widget.Replace('\\', '/')}.insteadOf", "https://github.com/acme/widget.git");
        _github.Prs["acme/widget#7"] = Pr("OPEN");
        _github.OpenPrs[WidgetClone] = [new PullRequestStatus { Number = 7, Title = "PR 7", Head = "feature/seven" }];
        _github.Labels[7] = [PilotMarks.Recheck];

        var git = new GitCli();
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1", RepoRootsGiven: [Roots]);
        var catalog = new RepoCatalog(_state, options);
        _reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), catalog, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            PostsItself = false,
        };
        // The node's own rebases make commits, and a CI runner has no identity of its own to make them with.
        PrOrigins.Git(WidgetClone, "config", "user.email", "t@example.com");
        PrOrigins.Git(WidgetClone, "config", "user.name", "t");
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System, _workers);
        _legs = new LegCommander(_state, _workers, new FakeChecks(), _github, git, new Planes(git), _flights, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
        _recheck = new PrRecheck(_state, _github, _reviews, catalog, TimeProvider.System, _legs) { Fleet = _fleet };
    }

    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    private string Roots => Path.Combine(_root, "repos");

    private string WidgetClone => Path.Combine(Roots, "widget");

    private PullRequest Pr(string state) =>
        new(7, state, _head, "https://github.com/acme/widget/pull/7", Title: "PR 7", Author: "ada", Base: "main");

    public void Dispose()
    {
        if (Directory.Exists(WidgetClone))
        {
            try
            {
                PrOrigins.Git(WidgetClone, "worktree", "prune");
            }
            catch (InvalidOperationException)
            {
            }
        }
        PrOrigins.Delete(_root);
    }

    private async Task LookAsync()
    {
        await _recheck.LookAsync(Ct);
        await _recheck.WhenIdleAsync();
    }

    private string[] Handled() => [.. _state.Cards().Where(c => c.Kind == PrRecheck.HandledKind && c.Bin == CardBin.Handled).Select(c => c.Title)];

    private PrReviewRow? Review() => _state.PrReviews().FirstOrDefault(r => r.Number == 7);

    [Fact]
    public async Task AnUntouchedLabelledPrStartsAReviewAndItsLabelBecomesReviewing()
    {
        await LookAsync();

        var review = Review().ShouldNotBeNull();
        (review.Repo, review.State, review.Head).ShouldBe(("acme/widget", PrReviewState.Reviewing, _head));
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-review-widget-7");
        _github.Labels[7].ShouldBe([PilotMarks.Reviewing]);
        Handled().ShouldBe(["recheck of widget#7: review started"]);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Bin == CardBin.ForYou);
    }

    [Fact]
    public async Task APrWithoutTheLabelIsLeftAlone()
    {
        _github.Labels[7] = ["pilot: passed"];

        await LookAsync();

        Review().ShouldBeNull();
        _workers.Started.ShouldBeEmpty();
        _github.Labels[7].ShouldBe(["pilot: passed"]);
    }

    [Theory]
    [InlineData("MERGED", "merged")]
    [InlineData("CLOSED", "closed")]
    public async Task APrMergedOrClosedMeanwhileLosesTheLabelAndAHandledLineSaysWhy(string state, string said)
    {
        _github.Prs["acme/widget#7"] = Pr(state);

        await LookAsync();

        _github.Labels[7].ShouldBeEmpty();
        _github.RemoveCalls.ShouldBe([(7, PilotMarks.Recheck)]);
        Handled().ShouldBe([$"recheck of widget#7: already {said}"]);
        Review().ShouldBeNull();
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task APrAlreadyInTheMergeQueueIsLeftWithTheQueuedLabelAlone()
    {
        _github.Queue = new MergeQueueState("OPEN", _head, Queued: true, Position: 1);

        await LookAsync();

        _github.Labels[7].ShouldBe([PilotMarks.Queued]);
        Handled().ShouldBe(["recheck of widget#7: already queued"]);
        Review().ShouldBeNull();
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARecheckThatCannotReadTheJobKeepsTheLabelAndRaisesAForYouCardOnceUntilTheLabelIsPutBack()
    {
        // No grant: gh cannot read the PR.
        _github.Prs.Clear();

        await LookAsync();

        _github.Labels[7].ShouldBe([PilotMarks.Recheck]);
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.ForYou, PrRecheck.FailedKind, "pr/acme/widget#7"));
        card.Title.ShouldBe("The recheck of widget#7 could not run");
        card.Detail.ShouldNotBeNull().ShouldContain("not found");
        Handled().ShouldBeEmpty();

        // Looked at again with the label still on: not tried every look.
        var cards = _state.Cards().Length;
        await LookAsync();
        await LookAsync();
        _state.Cards().Length.ShouldBe(cards);

        // Taken off and put back, with gh working again: it runs, and the card is closed.
        _github.Labels[7].Clear();
        await LookAsync();
        _github.Prs["acme/widget#7"] = Pr("OPEN");
        _github.Labels[7].Add(PilotMarks.Recheck);
        await LookAsync();
        Review().ShouldNotBeNull();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrRecheck.FailedKind);
    }

    [Fact]
    public async Task AReviewThatCannotStartKeepsTheLabelAndRaisesAForYouCard()
    {
        _workers.Fail = new FlightException("claude would not start");

        await LookAsync();

        _github.Labels[7].ShouldBe([PilotMarks.Recheck]);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrRecheck.FailedKind);
        (card.Bin, card.Detail).ShouldBe((CardBin.ForYou, card.Detail));
        card.Detail.ShouldNotBeNull().ShouldContain("claude would not start");
        _github.LabelCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task APrAnotherNodeHoldsAReviewOrAFlightOfIsLeftToThatNode()
    {
        _fleet.Peers.Add(new Peer("a.local:1") { Flights = [Flight("AA1", own: false)] });
        _fleet.Peers.Add(new Peer("b.local:1") { Unreachable = true });

        await LookAsync();

        Review().ShouldBeNull();
        _workers.Started.ShouldBeEmpty();
        _github.Labels[7].ShouldBe([PilotMarks.Recheck]);
        _state.Cards().ShouldBeEmpty();

        // Its review is as much its own as its flight.
        _fleet.Peers.Clear();
        _fleet.Peers.Add(new Peer("c.local:1") { PrReviews = [new PrReviewRow { Repo = "acme/widget", Number = 7, State = PrReviewState.Closed, Url = "u", Base = "main", Head = "h", ClonePath = "c", Worktree = "w", Reviewer = "r", StartedAt = DateTimeOffset.UtcNow }] });
        _github.Labels[7].Clear();
        await LookAsync();
        _github.Labels[7].Add(PilotMarks.Recheck);
        await LookAsync();
        Review().ShouldBeNull();
        _github.Labels[7].ShouldBe([PilotMarks.Recheck]);
    }

    [Fact]
    public async Task APeerThatHoldsNothingOfItOrDoesNotAnswerLeavesThePrToThisNode()
    {
        _fleet.Peers.Add(new Peer("a.local:1") { Flights = [Flight("AA1", own: false, number: 8)] });
        _fleet.Peers.Add(new Peer("b.local:1") { Unreachable = true });

        await LookAsync();

        Review().ShouldNotBeNull();
        _github.Labels[7].ShouldBe([PilotMarks.Reviewing]);
    }

    /// <summary>A PR reviewed with review-pr at its head, the verdict in (approve unless asked otherwise), the recheck label on it.</summary>
    private async Task<PrReviewRow> ReviewedAsync(string verdict = "approve")
    {
        (await _reviews.RunAsync(new CommandRequest { Verb = Verbs.ReviewPr, Target = "pr/acme/widget#7", Who = "controller" }, Ct)).Error.ShouldBeNull();
        var reviewer = Review()!.Reviewer;
        (await _reviews.RunAsync(new CommandRequest { Verb = Verbs.PrVerdict, Target = "pr/acme/widget#7", Args = [verdict, "Fine."], Who = reviewer }, Ct)).Error.ShouldBeNull();
        _github.LabelCalls.Clear();
        _github.Labels[7] = [PilotMarks.Recheck];
        return Review()!;
    }

    [Fact]
    public async Task AReviewedPrWhoseHeadMovedSinceTheReviewIsReviewedAfresh()
    {
        var review = await ReviewedAsync();
        var moved = PrOrigins.Push(Path.Combine(_root, "work-widget"), 7, "a later push");
        _github.Prs["acme/widget#7"] = Pr("OPEN") with { Head = moved };

        await LookAsync();

        var now = Review().ShouldNotBeNull();
        (now.State, now.Head).ShouldBe((PrReviewState.Reviewing, moved));
        now.Head.ShouldNotBe(review.Head);
        Handled().ShouldBe([$"recheck of widget#7: head {Short(moved)} was not the one reviewed ({Short(_head)}), review restarted"]);
        _github.Labels[7].ShouldBe([PilotMarks.Reviewing]);
    }

    [Fact]
    public async Task AReviewedPrWhoseHeadIsTheOneReviewedAndPassedAndGreenIsQueued()
    {
        await ReviewedAsync();
        _github.Checks = ChoiceStatus.Passing;
        _github.Queues = true;
        _github.Queue = new MergeQueueState("OPEN", _head, Queued: false);

        await LookAsync();

        _github.Merged.ShouldHaveSingleItem().ShouldBe((7, _head, _reviews.Models.MergeMethod));
        Review()!.QueuedAt.ShouldNotBeNull();
        Handled().ShouldBe([$"recheck of widget#7: passed and green, queued for merge at {Short(_head)}"]);
        _github.Labels[7].ShouldBe([PilotMarks.Queued]);
    }

    [Fact]
    public async Task AReviewedPrThatConflictsGetsAForYouCardAndTheConflictsLabel()
    {
        await ReviewedAsync();
        _github.Prs["acme/widget#7"] = Pr("OPEN") with { Mergeable = "CONFLICTING" };

        await LookAsync();

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrRecheck.ConflictKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "pr/acme/widget#7"));
        card.Detail.ShouldNotBeNull().ShouldContain("not pilot's branch to update");
        Handled().ShouldBe(["recheck of widget#7: conflicts with its base, not pilot's branch to update: a card asks you"]);
        _github.Labels[7].ShouldBe([PilotMarks.Conflicts]);
        _github.Merged.ShouldBeEmpty();
    }

    private string Origin => Path.Combine(_root, "github", "widget.git");

    /// <summary>
    /// PR 7 as a branch of the base repo, <c>feature/seven</c>, one commit past main (which edits README.md when
    /// <paramref name="touchesReadme"/>); main then moves on (with its own README.md edit when <paramref name="mainEditsReadme"/>).
    /// The fake's PR 7 is the controller's, at that head. Returns the head and main's new tip.
    /// </summary>
    private (string Head, string Main) OwnBranch(bool touchesReadme, bool mainEditsReadme)
    {
        string[] who = ["-c", "user.email=t@example.com", "-c", "user.name=t"];
        var work = Path.Combine(_root, "work-own");
        PrOrigins.Git(_root, "clone", "-q", Origin, work);
        PrOrigins.Git(work, "checkout", "-q", "-b", "feature/seven");
        File.WriteAllText(Path.Combine(work, touchesReadme ? "README.md" : "seven.txt"), "seven\n");
        PrOrigins.Git(work, "add", ".");
        PrOrigins.Git(work, [.. who, "commit", "-q", "-m", "seven"]);
        var head = PrOrigins.Git(work, "rev-parse", "HEAD");
        PrOrigins.Git(work, "push", "-q", "-f", "origin", "HEAD:refs/heads/feature/seven", "HEAD:refs/pull/7/head");

        PrOrigins.Git(work, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(work, mainEditsReadme ? "README.md" : "main.txt"), "main moved\n");
        PrOrigins.Git(work, "add", ".");
        PrOrigins.Git(work, [.. who, "commit", "-q", "-m", "main moves"]);
        PrOrigins.Git(work, "push", "-q", "origin", "HEAD:main");
        _github.Prs["acme/widget#7"] = new PullRequest(7, "OPEN", head, "https://github.com/acme/widget/pull/7", Title: "PR 7", Author: "controller", Base: "main", HeadRef: "feature/seven");
        return (head, PrOrigins.Git(work, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task AReviewedPrOnTheControllersOwnBranchThatConflictsGetsItsBaseMergedInAndIsReviewedAfresh()
    {
        var (head, main) = OwnBranch(touchesReadme: false, mainEditsReadme: false);
        await ReviewedAsync();
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Mergeable = "CONFLICTING" };

        await LookAsync();

        var updated = PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven");
        updated.ShouldNotBe(head);
        PrOrigins.Git(Origin, "merge-base", "--is-ancestor", main, updated).ShouldBeEmpty();
        // The base was merged in, not rebased onto: the head read is kept, and the push was a plain fast-forward of it.
        PrOrigins.Git(Origin, "merge-base", "--is-ancestor", head, updated).ShouldBeEmpty();
        Handled().ShouldBe(["recheck of widget#7: main merged in, review restarted"]);
        _github.Labels[7].ShouldBe([PilotMarks.Reviewing]);
        Review().ShouldNotBeNull().State.ShouldBe(PrReviewState.Reviewing);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrRecheck.ConflictKind);
        Directory.Exists(Path.Combine(WidgetClone, ".claude", "worktrees", "update-widget-7")).ShouldBeFalse();
    }

    [Fact]
    public async Task AReviewedPrOnTheControllersOwnBranchWhoseBaseMergeConflictsGetsTheCardAndTheConflictsLabel()
    {
        var (head, _) = OwnBranch(touchesReadme: true, mainEditsReadme: true);
        await ReviewedAsync();
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Mergeable = "CONFLICTING" };

        await LookAsync();

        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven").ShouldBe(head);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrRecheck.ConflictKind);
        (card.Bin, card.Subject).ShouldBe((CardBin.ForYou, "pr/acme/widget#7"));
        card.Detail.ShouldNotBeNull().ShouldContain("conflicts");
        Handled().ShouldHaveSingleItem().ShouldStartWith("recheck of widget#7: conflicts with its base, its base not merged in (merging main into feature/seven conflicts");
        _github.Labels[7].ShouldBe([PilotMarks.Conflicts]);
    }

    [Fact]
    public async Task AReviewedPrWhoseChangesNobodyIsFixingStartsItsFixerAndKeepsTheChangesLabel()
    {
        // tianwen #1857: its fixer could not start, and once that was cleared the recheck took the label off with nothing started.
        _github.Login = "ada";
        await ReviewedAsync("changes");
        Review()!.Fixer.ShouldBeNull();
        _workers.Started.Clear();
        // The PR's branch, which the fixer works on: in the origin, and said by gh.
        PrOrigins.Git(Path.Combine(_root, "work-widget"), "push", "-q", "origin", "HEAD:refs/heads/feature/seven");
        _github.Prs["acme/widget#7"] = Pr("OPEN") with { HeadRef = "feature/seven" };

        await LookAsync();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-fix-widget-7");
        Review()!.Fixer.ShouldBe("du1-fix-widget-7");
        Handled().ShouldBe([$"recheck of widget#7: its review asks for changes at {Short(_head)}, fixer started"]);
        _github.Labels[7].ShouldBe([PilotMarks.Changes]);
    }

    /// <summary>A changes review the recheck would start a fixer on, the PR's own and its branch in place, then changed by <paramref name="shape"/>.</summary>
    private async Task NoFixerAsync(Func<PrReviewRow, PrReviewRow> shape, string why, string login = "ada")
    {
        _github.Login = login;
        await ReviewedAsync("changes");
        PrOrigins.Git(Path.Combine(_root, "work-widget"), "push", "-q", "origin", "HEAD:refs/heads/feature/seven");
        _github.Prs["acme/widget#7"] = Pr("OPEN") with { HeadRef = "feature/seven" };
        _state.PutPrReview(shape(Review()!));
        _workers.Started.Clear();

        await LookAsync();

        _workers.Started.ShouldBeEmpty();
        Review()!.Fixer.ShouldBeNull();
        Handled().ShouldBe([$"recheck of widget#7: nothing to do at {Short(_head)}: its review asks for changes, and no fixer starts: {why}"]);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrRecheck.FailedKind);
        _github.Labels[7].ShouldBe([PilotMarks.Changes]);
    }

    [Fact]
    public async Task ARecheckStartsNoFixerOnAReviewThatAsksAQuestionOfDesign() =>
        await NoFixerAsync(r => r with { Findings = [new PrFinding { Path = "README.md", Line = 1, Body = "design: is this wanted?", Inline = false }] },
            "its review asks a question of design, which is the author's to decide");

    [Fact]
    public async Task ARecheckStartsNoFixerOnceTheRoundsAreCapped() =>
        await NoFixerAsync(r => r with { FixRound = PrReviews.MaxFixRounds },
            $"{PrReviews.MaxFixRounds} rounds of fixes have pushed and it still asks for changes, so no more start");

    [Fact]
    public async Task ARecheckStartsNoFixerOnSomeoneElsesPrUntilFixItIsChosen() =>
        await NoFixerAsync(r => r, "it is ada's PR, and Fix it has not been chosen", login: "grace");

    private CardRow[] NoFixerCards(bool openOnly = false) => [.. _state.Cards(openOnly: openOnly).Where(c => c.Kind == PrReviews.NoFixerKind)];

    private static PrReviewRow AsksDesign(PrReviewRow r) =>
        r with { Findings = [new PrFinding { Path = "README.md", Line = 1, Body = "design: is this wanted?", Inline = false }] };

    [Fact]
    public async Task ARecheckOfAChangesReviewWithNoFixerRingsTheOwnerOnceAndADismissalSticksUntilTheHeadMoves()
    {
        await NoFixerAsync(AsksDesign, "its review asks a question of design, which is the author's to decide");

        var ring = NoFixerCards(openOnly: true).ShouldHaveSingleItem();
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Detail.ShouldNotBeNull().ShouldContain("design: is this wanted?");
        ring.Detail.ShouldContain("the author's to decide");

        // A sweep, and the same recheck again, leave it open and add no second.
        await _reviews.SweepAsync(Ct);
        _github.Labels[7] = [PilotMarks.Recheck];
        await LookAsync();
        NoFixerCards(openOnly: true).ShouldHaveSingleItem().Id.ShouldBe(ring.Id);

        // Dismissed, the label put back: a recheck, a sweep and another recheck raise nothing again.
        _state.CloseCard(ring.Id, "dismissed", "controller");
        _github.Labels[7] = [PilotMarks.Recheck];
        await LookAsync();
        await _reviews.SweepAsync(Ct);
        _github.Labels[7] = [PilotMarks.Recheck];
        await LookAsync();
        NoFixerCards().ShouldHaveSingleItem().Outcome.ShouldBe("dismissed");

        // Nothing the node closed on its own says a bare "handled" (the Handled lines are born closed, as records).
        _state.Cards().Where(c => c.Bin != CardBin.Handled && c.State == CardState.Closed && c.ClosedBy == "node")
            .ShouldNotContain(c => c.Outcome == "handled");
    }

    [Fact]
    public async Task TheNoFixerRingClosesNamingTheHeadThatMovedPastIt()
    {
        await NoFixerAsync(AsksDesign, "its review asks a question of design, which is the author's to decide");
        var ring = NoFixerCards(openOnly: true).ShouldHaveSingleItem();
        var moved = PrOrigins.Push(Path.Combine(_root, "work-widget"), 7, "a later push");
        _github.Prs["acme/widget#7"] = Pr("OPEN") with { Head = moved, HeadRef = "feature/seven" };
        _github.Labels[7] = [PilotMarks.Recheck];

        await LookAsync();

        var closed = NoFixerCards().ShouldHaveSingleItem();
        closed.Id.ShouldBe(ring.Id);
        (closed.State, closed.ClosedBy).ShouldBe((CardState.Closed, "node"));
        closed.Outcome.ShouldNotBeNull().ShouldContain("the head moved past");
        closed.Outcome.ShouldContain(Short(moved));
    }

    [Fact]
    public async Task TheFixersPushWhoseReviewCouldNotStartStaysAnOpenCardAfterTheSweep()
    {
        _github.Login = "ada";
        await ReviewedAsync("changes");
        PrOrigins.Git(Path.Combine(_root, "work-widget"), "push", "-q", "origin", "HEAD:refs/heads/feature/seven");
        _github.Prs["acme/widget#7"] = Pr("OPEN") with { HeadRef = "feature/seven" };
        (await _reviews.RunAsync(new CommandRequest { Verb = Verbs.FixPr, Target = "pr/acme/widget#7", Who = "controller" }, Ct)).Error.ShouldBeNull();
        Review()!.Fixer.ShouldNotBeNull();

        // The fixer pushed, and the review of the pushed head cannot start.
        var moved = PrOrigins.Push(Path.Combine(_root, "work-widget"), 7, "the fixer's push");
        _github.Prs["acme/widget#7"] = Pr("OPEN") with { Head = moved, HeadRef = "feature/seven" };
        foreach (var f in new DirectoryInfo(WidgetClone).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            f.Attributes = FileAttributes.Normal;
        }
        Directory.Delete(WidgetClone, recursive: true);

        await _reviews.SweepAsync(Ct);

        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrReviews.PushedUnreviewedKind);
    }

    [Fact]
    public async Task TheFixerGuardSaysNoForARepoTheNodeDoesNotTrack()
    {
        // A recheck only looks at tracked repos, so this is the guard itself: a repo that left the roots between look and fix gets none.
        _github.Login = "ada";
        var review = await ReviewedAsync("changes");

        (await _reviews.WhyNoFixerAsync(new PrRef("acme", "gadget", 7), review, Ct)).ShouldBe("acme/gadget is not a repo the node tracks");
        (await _reviews.WhyNoFixerAsync(new PrRef("acme", "widget", 7), review, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task AReviewedPrWithNothingToDoSaysWhereItStands()
    {
        await ReviewedAsync();
        _github.Checks = ChoiceStatus.Pending;

        await LookAsync();

        _github.Merged.ShouldBeEmpty();
        Handled().ShouldBe([$"recheck of widget#7: nothing to do at {Short(_head)}: its checks are still running"]);
        _github.Labels[7].ShouldBe([PilotMarks.Passed]);
    }

    /// <summary>A flight of this node's on PR 7 (the recheck's PR), its leg done and pushed, reviewed and passed; the recheck label on the PR.</summary>
    private async Task<FlightRow> PassedFlightAsync(string? readme = null)
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(WidgetClone), Args = ["1", "Flight 1"], Who = "controller" }, Ct)).Error.ShouldBeNull();
        var flight = _state.Flights().Single();
        async Task Run(string verb, string target, string who, params string[] args) =>
            (await _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct)).Error.ShouldBeNull();
        await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", "name=parser", "goal=Do it", "done-when=It is done", "item=The step");
        await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller");
        var worker = _state.Legs(flight.Code).Single().Worker!;
        File.WriteAllText(Path.Combine(flight.Worktree, readme is null ? "parser.txt" : "README.md"), readme ?? "work\n");
        PrOrigins.Git(flight.Worktree, "add", ".");
        PrOrigins.Git(flight.Worktree, "commit", "-q", "-m", "parser work");
        await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1");
        await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "did it");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");

        // The fake opens a PR of its own number; this one is PR 7 of acme/widget, at the head the plane pushed.
        var head = PrOrigins.Git(flight.Worktree, "rev-parse", "HEAD");
        _github.Pr = Pr("OPEN") with { Head = head };
        _state.PutFlight(_state.Flights().Single() with { PullRequest = 7, PullRequestUrl = "https://github.com/acme/widget/pull/7" });
        _github.Prs["acme/widget#7"] = _github.Pr;
        await Run(Verbs.Review, "flight/" + flight.Code, _state.Flights().Single().Review!.Reviewer, "pass", "fine");
        _github.Labels[7] = [PilotMarks.Recheck];
        return _state.Flights().Single();
    }

    private static string Short(string sha) => sha[..8];

    /// <summary>Main moves on after the flight was cut, as it does while a PR waits.</summary>
    private void MainMoves(string file = "later.txt", string text = "later\n")
    {
        var work = Path.Combine(_root, "work-widget");
        PrOrigins.Git(work, "fetch", "-q", "origin", "main");
        PrOrigins.Git(work, "reset", "-q", "--hard", "origin/main");
        File.WriteAllText(Path.Combine(work, file), text);
        PrOrigins.Git(work, "add", ".");
        PrOrigins.Git(work, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "later on main");
        PrOrigins.Git(work, "push", "-q", "origin", "HEAD:main");
    }

    [Fact]
    public async Task AFlightsPrBehindItsBaseHasItMergedInAndItsPassedReviewCarriedToTheNewHead()
    {
        var flight = await PassedFlightAsync();
        MainMoves();
        _github.Merge = new MergeState(MergeKinds.Behind);

        await LookAsync();

        var now = _state.Flights().Single();
        PrOrigins.Git(now.Worktree, "merge-base", "--is-ancestor", "origin/main", "HEAD");
        var review = now.Review.ShouldNotBeNull();
        // Only a clean merge of the base moved the head: the pass goes with it.
        review.State.ShouldBe(ReviewState.Passed);
        var read = review.ReviewedHead.ShouldNotBeNull();
        read.ShouldNotBe(flight.Review!.ReviewedHead);
        read.ShouldBe(PrOrigins.Git(now.Worktree, "rev-parse", "HEAD").Trim());
        Handled().ShouldBe([$"recheck of widget#7: brought up to date with main, its review carried over to {Short(read)}"]);
        _github.Labels[7].ShouldBe([PilotMarks.Passed]);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightsPrWhoseBaseMergeConflictsIsHandedToTheWatchtowerAndTheHandledLineSaysSo()
    {
        await PassedFlightAsync(readme: "theirs\n");
        MainMoves("README.md", "ours\n");
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };

        await LookAsync();

        // No go-around: the review stays passed and the watchtower is told by a ring card.
        _state.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        var said = Handled().ShouldHaveSingleItem();
        said.ShouldStartWith("recheck of widget#7: merging ");
        said.ShouldContain("conflicts");
        said.ShouldContain("the watchtower is told");
        _state.Legs(_state.Flights().Single().Code).ShouldAllBe(l => l.GoArounds == 0);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == LegCommander.PrConflictsKind && c.Bin == CardBin.Ring);
    }

    [Fact]
    public async Task AFlightsPrWithAFailingCheckIsSentBackAsChangesEvenWhenTheTimedLookTriedThatHeadAlready()
    {
        await PassedFlightAsync();
        _github.Checks = ChoiceStatus.Failing;
        var timed = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await timed.LookAsync(Ct);
        await timed.LookAsync(Ct);
        _state.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);

        // The head is the same and the look would not send it back again; the person asking does. The go-around that followed
        // is taken as done and passed again, the checks still red.
        _state.PutFlight(_state.Flights().Single() with { Review = _state.Flights().Single().Review! with { State = ReviewState.Passed } });
        _github.DraftCalls.Clear();
        _github.Labels[7] = [PilotMarks.Recheck];
        await timed.LookAsync(Ct);
        _state.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);

        await LookAsync();

        _state.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        _github.DraftCalls.ShouldContain((7, true));
        Handled().ShouldBe([$"recheck of widget#7: checks failed at {Short(_github.Pr!.Head)}, sent back as changes"]);
        _github.Labels[7].ShouldBe([PilotMarks.Changes]);
    }

    [Fact]
    public async Task AFlightsPrWhoseHeadTheReviewNeverReadIsReviewedAfresh()
    {
        await PassedFlightAsync();
        const string moved = "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";
        _github.Pr = _github.Pr! with { Head = moved };
        var started = _workers.Started.Count;

        await LookAsync();

        _workers.Started.Count.ShouldBe(started + 1);
        var review = _state.Flights().Single().Review.ShouldNotBeNull();
        review.State.ShouldBe(ReviewState.Reviewing);
        Handled().ShouldBe([$"recheck of widget#7: head ab12cd34 was not the one reviewed, review restarted at {Short(review.ReviewedHead!)}"]);
        _github.Labels[7].ShouldBe([PilotMarks.Reviewing]);
    }

    [Fact]
    public async Task AFlightsPassedGreenCleanPrGoesToTheMergeQueue()
    {
        await PassedFlightAsync();
        _github.Checks = ChoiceStatus.Passing;
        _github.Queues = true;

        await LookAsync();

        var head = _github.Pr!.Head;
        _github.Merged.ShouldHaveSingleItem().ShouldBe((7, head, _legs.Models.MergeMethod));
        Handled().ShouldBe([$"recheck of widget#7: passed and green, queued for merge at {Short(head)}"]);
        _github.Labels[7].ShouldBe([PilotMarks.Queued]);
    }

    [Fact]
    public async Task AFlightsPrWithNothingToDoSaysSoAndLeavesTheLabelForWhereItStands()
    {
        await PassedFlightAsync();
        _github.Checks = ChoiceStatus.Pending;
        var started = _workers.Started.Count;

        await LookAsync();

        _github.Merged.ShouldBeEmpty();
        _workers.Started.Count.ShouldBe(started);
        _state.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        Handled().ShouldBe([$"recheck of widget#7: nothing to do at {Short(_github.Pr!.Head)}: its checks are still running"]);
        _github.Labels[7].ShouldBe([PilotMarks.Passed]);
    }

    [Fact]
    public async Task ALabelPutBackWhileARecheckRunsRunsItOnceMoreAfterNeverTwiceAtOnce()
    {
        var gate = new TaskCompletionSource();
        _workers.Hold = gate.Task;

        // The first recheck waits on its worker's start.
        await _recheck.LookAsync(Ct);
        await WaitFor(() => _state.PrReviews().Length == 0 && _workers.Hold is null);
        _workers.Started.ShouldBeEmpty();

        // The label still there at the next looks: no second run beside it.
        await _recheck.LookAsync(Ct);
        await _recheck.LookAsync(Ct);

        // Taken off and put back while it runs.
        _github.Labels[7].Clear();
        await _recheck.LookAsync(Ct);
        _github.Labels[7].Add(PilotMarks.Recheck);
        await _recheck.LookAsync(Ct);
        _workers.Started.ShouldBeEmpty();

        gate.SetResult();
        await _recheck.WhenIdleAsync();

        // Once, then once more: the second finds the review the first started, which is its own now.
        _workers.Started.Count.ShouldBe(1);
        Handled().ShouldContain("recheck of widget#7: review started");
        Handled().ShouldContain(h => h.Contains("its review is still reading it"));
        Handled().Length.ShouldBe(2);
        _github.Labels[7].ShouldBe([PilotMarks.Reviewing]);
    }

    [Fact]
    public async Task ALabelSeenAgainAfterARecheckEndedRunsItOnceMore()
    {
        _github.Queue = new MergeQueueState("OPEN", _head, Queued: true, Position: 1);
        await LookAsync();
        Handled().ShouldBe(["recheck of widget#7: already queued"]);

        // Out of the queue, and a person puts the label back on the PR that was rechecked.
        _github.Queue = null;
        _github.Labels[7].Add(PilotMarks.Recheck);
        await LookAsync();

        _workers.Started.ShouldHaveSingleItem();
        _github.Labels[7].ShouldBe([PilotMarks.Reviewing]);
        Handled().Length.ShouldBe(2);
    }

    private FlightRow Flight(string code, bool own, int number = 7) => new()
    {
        Code = code,
        Repo = "widget",
        RepoPath = own ? WidgetClone : "/elsewhere/widget",
        Issue = 1,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/w",
        Base = "main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UtcNow,
        PullRequest = number,
        PullRequestUrl = $"https://github.com/acme/widget/pull/{number}",
    };

    // Up to 10 s: what a passing wait takes is unchanged, and 2 s failed on a machine busy with its flights' builds (SE1, 2026-10-27).
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().ShouldBeTrue();
    }

    private sealed class FakeFleet : IFleet
    {
        public List<IFleetPeer> Peers { get; } = [];

        IReadOnlyList<IFleetPeer> IFleet.Peers() => [.. Peers];
    }

    private sealed class Peer(string endpoint) : IFleetPeer
    {
        public string Endpoint => endpoint;

        public bool HasGrant => true;

        public bool Unreachable { get; init; }

        public FlightRow[] Flights { get; init; } = [];

        public PrReviewRow[] PrReviews { get; init; } = [];

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) =>
            Unreachable ? Task.FromException<FleetLoad>(new HttpRequestException("connection refused")) : Task.FromResult(new FleetLoad(null, Flights, PrReviews));

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
