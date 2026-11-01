using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight's PR has one review, the flight's (PIL483): <c>review-pr</c> on it is refused naming <c>flight review</c>, and a PR
/// review's approve at the flight's head is the flight's verdict. In-memory node, a fake GitHub, a real plane for the flight.
/// </summary>
public sealed class PrFlightReviewTests : ReviewTestsBase
{
    private const string Head = "cafe0000cafe0000";
    private const string PrTarget = "pr/x/tianwen#71";

    private readonly PrReviews _reviews;

    public PrFlightReviewTests()
    {
        // The repo names GitHub as its origin, as a real clone does, so a PR is matched to the flight by its head ref too.
        var origin = Path.Combine(_root, "origin.git").Replace('\\', '/');
        Git(Repo, "config", "remote.origin.url", "https://github.com/x/tianwen.git");
        Git(Repo, "config", $"url.{origin}.insteadOf", "https://github.com/x/tianwen.git");
        var git = new GitCli();
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1", RepoRootsGiven: [_root]);
        _reviews = new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, options), options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            PostsItself = false,
            TakeFlightVerdict = _legs.TakePrVerdictAsync,
        };
    }

    private Task<CommandResult> RunPr(string verb, string who, params string[] args) =>
        _reviews.RunAsync(new CommandRequest { Verb = verb, Target = PrTarget, Args = args, Who = who }, Ct);

    /// <summary>A flight with its PR open (number 71, at <see cref="Head"/>) whose review asked for changes.</summary>
    private async Task<FlightRow> AtChanges()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "fix the null check")).Error.ShouldBeNull();
        var pr = _github.Pr.ShouldNotBeNull();
        _github.Pr = pr with { Head = Head, HeadRef = F(flight.Code).Branch, Base = "main" };
        _github.Prs[$"x/tianwen#{pr.Number}"] = _github.Pr;
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        return F(flight.Code);
    }

    private PrReviewRow Reviewing(FlightRow flight, string head) => new()
    {
        Repo = "x/tianwen",
        Number = flight.PullRequest!.Value,
        Url = flight.PullRequestUrl!,
        Base = "main",
        Head = head,
        ClonePath = Repo,
        Worktree = flight.Worktree,
        Reviewer = "du1-review-tianwen-71",
        State = PrReviewState.Reviewing,
        StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ReviewPrOnAFlightsPrByUrlIsRefusedNamingTheFlightsReview()
    {
        var flight = await AtChanges();

        var result = await RunPr(Verbs.ReviewPr, "controller");

        result.Error.ShouldNotBeNull().ShouldContain($"flight review {flight.Code}");
        _reviews.Of(new PrRef("x", "tianwen", 71)).ShouldBeNull();
    }

    [Fact]
    public async Task ReviewPrOnAPrWhoseHeadBranchIsAFlightsIsRefusedToo()
    {
        var flight = await AtChanges();
        // The flight does not know its PR by number or URL, only by branch: the PR's head ref is what says whose it is.
        _state.PutFlight(F(flight.Code) with { PullRequest = null, PullRequestUrl = null });

        var result = await RunPr(Verbs.ReviewPr, "controller");

        result.Error.ShouldNotBeNull().ShouldContain($"flight review {flight.Code}");
    }

    [Fact]
    public async Task AnApproveAtTheFlightsHeadPassesItsReviewAndMarksItsPrReady()
    {
        var flight = await AtChanges();
        _state.PutPrReview(Reviewing(flight, Head));
        _github.DraftCalls.Clear();

        (await RunPr(Verbs.PrVerdict, "du1-review-tianwen-71", "approve", "Reads fine.")).Error.ShouldBeNull();

        var after = F(flight.Code);
        after.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        _github.DraftCalls.ShouldContain((71, false));
        PilotLabels(_github.Labels[71]).ShouldBe(["pilot: passed"]);
        _github.Statuses.Last().State.ShouldBe("success");
        Open().ShouldContain(c => c.Kind == LegCommander.PrReady && c.Subject == Verbs.FlightTarget(flight.Code));
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
    }

    [Fact]
    public async Task AnApproveAtAnOlderHeadChangesNothing()
    {
        var flight = await AtChanges();
        _state.PutPrReview(Reviewing(flight, "0123456789abcdef0123456789abcdef01234567"));
        _github.DraftCalls.Clear();

        (await RunPr(Verbs.PrVerdict, "du1-review-tianwen-71", "approve", "Reads fine.")).Error.ShouldBeNull();

        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        _github.DraftCalls.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);
    }
}
