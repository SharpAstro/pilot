using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A Go around answered from an open PR-ready or merge-failed card, through the node's <c>answer</c> verb as the GUI sends it: the
/// leg is done under a passed review, which only an open card offering the go-around lets go around, and answering closes cards
/// (pilot #244). ReviewTests runs the verb directly, which never passes through the answer.
/// </summary>
public sealed class GoAroundFromCardTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string CheckLink = "https://github.com/x/tianwen/actions/runs/123/job/456";
    private const string Subject = "flight/TIA1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8]);

    // The repo is on the in-memory git model (#952), which the node runs on too; real git only under PILOT_TEST_REAL_GIT=1.
    private readonly SeededRepos _repos;

    public GoAroundFromCardTests() => _repos = new SeededRepos(_root);

    private string Repo => Path.Combine(_root, "tianwen");

    public void Dispose() => _repos.Dispose();

    /// <summary>
    /// TIA1 with its one leg done, its review passed and PR #63 open at <see cref="PrHead"/>, in a real repo whose branch has a
    /// commit past main, and a PR-ready card offering what it is given.
    /// </summary>
    private CardRow Ready(NodeHarness node, string kind, params CardChoice[] choices)
    {
        _repos.Seed("tianwen");
        _repos.Run(Repo, "branch", "flight/tia1");
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Repo, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(_root, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return node.State.RaiseCard(CardBin.Ring, kind, Subject, "TIA1's PR #63", "Planets: 1 leg done.", choices, link: PrUrl);
    }

    private static async Task<CommandResult> Answer(NodeHarness node, CardRow card, string choice) =>
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = "gui" }, Ct)).Response.ShouldNotBeNull();

    private static void ShouldHaveGoneAround(NodeHarness node, CardRow card, string brief)
    {
        // The leg is flying again with a worker told why, the PR sent back as the node's own sends are, and the card closed.
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Flying);
        node.Workers.Started.Last().Brief.ShouldContain(brief);
        node.GitHub.DraftCalls.ShouldContain((63, true));
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Changes);
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards(openOnly: true).ShouldBeEmpty();
    }

    [Fact]
    public async Task AGoAroundAnsweredFromTheMergeFailedCardRunsAndTheLegFliesAgainUnderAChangesReview()
    {
        await using var node = await NodeHarness.StartAsync(Ct, git: _repos.Git);
        var ready = Ready(node, LegCommander.PrReady, new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [PrHead] });
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", CheckLink);
        await node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);
        var failed = node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        failed.Kind.ShouldBe(LegCommander.MergeFailed);

        var result = await Answer(node, failed, failed.Choices.Single(c => c.Verb == Verbs.GoAround).Label);

        result.Error.ShouldBeNull();
        ShouldHaveGoneAround(node, failed, "build (linux)");
    }

    [Theory]
    [InlineData("conflicts with its base")]
    [InlineData("is behind its base")]
    public async Task AGoAroundAnsweredFromAConflictsOrBehindCardRunsAndTheLegFliesAgainUnderAChangesReview(string what)
    {
        await using var node = await NodeHarness.StartAsync(Ct, git: _repos.Git);
        // The card as PrChecks leaves it when the node's own rebase of a passed review did not send it back: the go-around is all it offers.
        var why = $"the PR {what}: rebase the plane onto its base, resolving every conflict so both sides' work stays, rerun the check, and push";
        var card = Ready(node, LegCommander.PrReady, new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = Verbs.LegTarget("TIA1", "parser"), Args = [why] });

        var result = await Answer(node, card, "Redo leg parser");

        result.Error.ShouldBeNull();
        ShouldHaveGoneAround(node, card, "rebase the plane onto its base");
    }

    [Fact]
    public async Task AGoAroundRefusedLeavesItsCardOpen()
    {
        await using var node = await NodeHarness.StartAsync(Ct, git: _repos.Git);
        var ready = Ready(node, LegCommander.PrReady, new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = Verbs.LegTarget("TIA1", "parser"), Args = [" "] });

        // A go-around needs a why: the card stays for the person to try again, and nothing was sent back.
        (await Answer(node, ready, "Redo leg parser")).Error.ShouldNotBeNull().ShouldContain("needs why");

        node.State.Cards().Single(c => c.Id == ready.Id).State.ShouldBe(CardState.Open);
        node.GitHub.DraftCalls.ShouldBeEmpty();
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Passed);
    }
}
