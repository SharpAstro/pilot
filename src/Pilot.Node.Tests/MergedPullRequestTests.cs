using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR the node itself saw merge leaves the Pull requests row at once and no put row brings it back inside the window (pilot #1002):
/// the flight that landed after its merge, a PR review merged on autopilot, and the window's ends.
/// </summary>
public sealed class MergedPullRequestTests : LegCommanderTestsBase
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static PullRequestRow Pr(string repo, int number) => new()
    {
        Repo = repo, Number = number, Title = $"PR {number}", Url = $"https://github.com/{repo}/pull/{number}", Kind = PullRequestKind.Ready, Label = "Ready",
    };

    private static PullRequestsRow Row(DateTimeOffset at, params PullRequestRow[] prs) => new() { ReadAt = at, PullRequests = prs };

    private static int[] Numbers(NodeState state) => [.. state.PullRequests()!.PullRequests.Select(p => p.Number).Order()];

    [Fact]
    public async Task AFlightLandingAfterItsMergeDropsItsPrRowWithNoReadRun()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _state.PutFlight(_state.Flights().Single() with { PullRequest = 63, PullRequestUrl = "https://github.com/SharpAstro/tianwen/pull/63" });
        _state.PutPullRequests(Row(DateTimeOffset.UtcNow, Pr("SharpAstro/tianwen", 63), Pr("SharpAstro/tianwen", 64)));
        Numbers(_state).ShouldBe([63, 64]);

        var head = Git(_state.Flights().Single().Worktree, "rev-parse", "HEAD");
        _github.Pr = new PullRequest(63, "MERGED", head, "https://github.com/SharpAstro/tianwen/pull/63");
        await _flights.LandAfterMergeAsync("TIA1", head, Ct);

        Numbers(_state).ShouldBe([64]);
        // A lagging read that still lists it does not bring it back.
        _state.PutPullRequests(Row(DateTimeOffset.UtcNow, Pr("SharpAstro/tianwen", 63), Pr("SharpAstro/tianwen", 64)));
        Numbers(_state).ShouldBe([64]);
    }

    [Fact]
    public void APrTheNodeSawMergeIsDroppedFromEveryRowPutInTheWindow()
    {
        var clock = new Clock(Noon);
        var state = new NodeState(new InMemoryJournal(), clock);
        state.PutPullRequests(Row(Noon, Pr("acme/widget", 7), Pr("acme/widget", 8)));

        state.ForgetPullRequest("acme/widget", 7, Noon);
        Numbers(state).ShouldBe([8]);

        clock.Advance(NodeState.MergedForgetWindow - TimeSpan.FromSeconds(1));
        state.PutPullRequests(Row(clock.GetUtcNow(), Pr("acme/widget", 7), Pr("acme/widget", 8), Pr("acme/gadget", 7)));
        Numbers(state).ShouldBe([7, 8]);
        state.PullRequests()!.PullRequests.ShouldNotContain(p => p.Repo == "acme/widget" && p.Number == 7);
    }

    [Fact]
    public void AfterTheWindowAnOpenPrOfThatNumberIsShownAgain()
    {
        var clock = new Clock(Noon);
        var state = new NodeState(new InMemoryJournal(), clock);
        state.PutPullRequests(Row(Noon, Pr("acme/widget", 7)));
        state.ForgetPullRequest("acme/widget", 7, Noon);
        Numbers(state).ShouldBeEmpty();

        clock.Advance(NodeState.MergedForgetWindow + TimeSpan.FromSeconds(1));
        state.PutPullRequests(Row(clock.GetUtcNow(), Pr("acme/widget", 7)));
        Numbers(state).ShouldBe([7]);
    }
}

/// <summary>A PR review merged on autopilot drops its row from the Pull requests page at once.</summary>
public sealed class MergedPullRequestReviewTests : PrReviewTestsBase
{
    [Fact]
    public async Task APrReviewMergedOnAutopilotDropsItsRow()
    {
        _reviews.Models = new RoleModels(new NodeSettings { Autopilot = true, MergeMethod = "squash" });
        _state.PutPullRequests(new PullRequestsRow
        {
            ReadAt = DateTimeOffset.UtcNow,
            PullRequests = [new PullRequestRow { Repo = "acme/widget", Number = 7, Title = "w", Url = "https://github.com/acme/widget/pull/7", Kind = PullRequestKind.Ready, Label = "Ready" }],
        });

        await ReviewedWidget("approve", queue: false);

        _github.Merged.ShouldBe([(7, _widgetHead, "squash")]);
        _state.PullRequests()!.PullRequests.ShouldBeEmpty();
    }
}
