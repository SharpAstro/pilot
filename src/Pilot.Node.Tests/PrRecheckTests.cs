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
        _recheck = new PrRecheck(_state, _github, _reviews, catalog, TimeProvider.System) { Fleet = _fleet };
    }

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

    [Fact]
    public async Task APrWithAFlightOfThisNodeOrAReviewItMadeRoutesToItsOwnMethodThatSaysSo()
    {
        _state.PutFlight(Flight("DU1", own: true));

        await LookAsync();

        Review().ShouldBeNull();
        _workers.Started.ShouldBeEmpty();
        Handled().ShouldHaveSingleItem().ShouldBe("recheck of widget#7: DU1's PR: the recheck of a flight is not wired yet");
        _github.Labels[7].ShouldBeEmpty();

        // A PR it reviewed before.
        var flight = _state.Flights().Single();
        _state.PutFlight(flight with { State = FlightState.Landed });
        (await _reviews.RunAsync(new CommandRequest { Verb = Verbs.ReviewPr, Target = "pr/acme/widget#7", Who = "controller" }, Ct)).Error.ShouldBeNull();
        var reviewer = Review()!.Reviewer;
        _github.Labels[7].Add(PilotMarks.Recheck);
        await LookAsync();
        Handled().Length.ShouldBe(2);
        Handled().ShouldContain($"recheck of widget#7: reviewed by {reviewer}: the recheck of a reviewed PR is not wired yet");
        _workers.Started.Count.ShouldBe(1);
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
        Handled().ShouldContain(h => h.Contains("reviewed by du1-review-widget-7"));
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

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
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
