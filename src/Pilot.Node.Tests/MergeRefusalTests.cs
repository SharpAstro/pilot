using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Every refusal a merge can answer with carries its class (NodeWait, Passing or Persons) and, for a person's, the one action to take
/// (PIL864). The class is given where the refusal is made, so each case drives the merge verb into one refusal and reads the class off the
/// command's result (what PrChecks reads), never off its words; the words are asserted only to prove each message kept them.
/// </summary>
public sealed class MergeRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";

    private static readonly MergeRefusal NodeWait = new(MergeRefusalClass.NodeWait);
    private static readonly MergeRefusal Passing = new(MergeRefusalClass.Passing);
    private static readonly MergeRefusal Conflicting = new(MergeRefusalClass.Passing, MergeAction.ResolveConflict);

    private static MergeRefusal Persons(MergeAction action) => new(MergeRefusalClass.Persons, action);

    /// <summary>
    /// TIA1 with its leg done and its review passed at <see cref="PrHead"/>, PR #63 open there. Its repo is the node's folder, which
    /// exists, so the landing after a merge that went through runs git there (as in MergeArmTests); no plane on disk, as a merge needs none.
    /// </summary>
    private static FlightRow Flight(NodeHarness node, ReviewState? review = ReviewState.Passed, LegState leg = LegState.Done, FlightHolding? holding = null)
    {
        var row = new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(node.Folder, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl, Holding = holding,
            Review = review is { } state ? new ReviewRow { State = state, Reviewer = "du1-review", StartedAt = DateTimeOffset.UtcNow, ReviewedHead = PrHead } : null,
        };
        node.State.PutFlight(row);
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = leg });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return row;
    }

    /// <summary>A plane on disk at <paramref name="worktree"/>: a repo of one commit, whose sha it returns as the head it holds.</summary>
    private static string Plane(string worktree)
    {
        Directory.CreateDirectory(worktree);
        Git(worktree, "init", "-q", "-b", "main");
        Git(worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", "first");
        return Git(worktree, "rev-parse", "HEAD");
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = TestEnvironment.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private static Task<CommandResult> Merge(NodeHarness node, params string[] args) =>
        node.App.Services.GetRequiredService<LegCommander>().RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget("TIA1"), Args = args.Length > 0 ? args : [PrHead], Who = "tests" }, Ct);

    private static void Refused(CommandResult result, MergeRefusal expected, string words)
    {
        result.Ok.ShouldBeFalse(result.Error);
        result.Error.ShouldNotBeNull().ShouldContain(words);
        result.Refusal.ShouldBe(expected);
        MergeRefusals.Of(result).ShouldBe(expected);
    }

    // The node's own waits: nobody is asked, the node goes on when the wait ends.

    [Fact]
    public async Task LegsNotDoneIsANodeWait()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, leg: LegState.Flying);

        Refused(await Merge(node), NodeWait, "has legs not done: parser");
    }

    [Fact]
    public async Task AFlightNotReviewedIsANodeWait()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, review: null);

        Refused(await Merge(node), NodeWait, "has not been reviewed");
    }

    [Fact]
    public async Task AReviewNotPassedIsANodeWait()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, review: ReviewState.Reviewing);

        Refused(await Merge(node), NodeWait, "review is reviewing, not passed");
    }

    [Fact]
    public async Task AHoldingPatternIsANodeWait()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, holding: new FlightHolding { Reason = "a leg is held", Since = DateTimeOffset.UtcNow, By = "node" });

        Refused(await Merge(node), NodeWait, "is in a holding pattern");
    }

    [Fact]
    public async Task AHeadNewerThanTheOneReviewedIsANodeWait()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var flight = Flight(node);
        // After this refusal the node reads the plane (HeadMovedAsync), so the flight has one, holding the reviewed head: the head moved
        // from outside it, and the merge is dropped saying so, with no reviewer started. The merge names a head that is not the reviewed
        // one, so the refusal is this one and not a re-pin's.
        var reviewed = Plane(flight.Worktree);
        node.State.PutFlight(flight with { Review = flight.Review! with { ReviewedHead = reviewed } });
        node.GitHub.Pr = new PullRequest(63, "OPEN", "def5678aaaabbbbccccdddd", PrUrl);

        Refused(await Merge(node), NodeWait, "is newer than the one you reviewed");
    }

    [Fact]
    public async Task AMergeUnderWayIsANodeWait()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.GitHub.OnCall = async call =>
        {
            if (call == nameof(IGitHub.MergeAsync))
            {
                entered.TrySetResult();
                await open.Task;
            }
        };
        var first = Merge(node);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Asked again while it runs, the merge answers ok with no news (the entry's own command drives it); no refusal at all.
        var again = await Merge(node);
        again.Ok.ShouldBeTrue(again.Error);
        again.Way.ShouldBe(MergeWays.InProgress);
        MergeRefusals.Of(again).ShouldBeNull();

        open.SetResult();
        (await first).Ok.ShouldBeTrue();
    }

    // "Being merged already" (a merge whose gh call never returns, asked again past StuckAfter) is driven for real in CoalesceGuardTests, whose
    // stuck case asserts the NodeWait class. The repo's turn held makes the next merge wait inside its command (WaitTurnAsync), which answers
    // nothing to refuse.

    [Fact]
    public async Task ChecksRunningIsAWaitTheMergeArmsOnAndNoRefusal()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);

        var result = await Merge(node);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(MergeWays.Armed);
        result.Refusal.ShouldBeNull();
        MergeRefusals.Of(result).ShouldBeNull();
    }

    // Passing: the node asks again or brings the branch up to date.

    [Fact]
    public async Task ABranchBehindItsBaseOnGitHubIsPassing()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Merge = new MergeState(MergeKinds.Behind);

        Refused(await Merge(node), Passing, "is behind its base");
    }

    [Fact]
    public async Task AMergeRefusedForTheBaseBranchBeingModifiedIsPassing()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.MergeAnswers = "GraphQL: Base branch was modified. Review and try the merge again. (mergePullRequest)";

        Refused(await Merge(node), Passing, "Base branch was modified");
    }

    [Fact]
    public async Task APrConflictingWithItsBaseIsPassing()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.MergeAnswers = "gh: Pull Request is not mergeable";

        Refused(await Merge(node), Conflicting, "Pull Request is not mergeable");
    }

    [Fact]
    public async Task APrGitHubReadsAsConflictingIsPassingNamingTheConflict()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl, Mergeable: "CONFLICTING");

        Refused(await Merge(node), Conflicting, "conflicts with its base");
    }

    [Theory]
    [InlineData(MergeAnswerKind.Transient)]
    [InlineData(MergeAnswerKind.Behind)]
    public void TheSortedAnswersThatPassAreClassedPassing(MergeAnswerKind kind) => MergeRefusals.Of(kind).ShouldBe(Passing);

    [Fact]
    public void AConflictsAnswerPassesWithTheConflictNamed() => MergeRefusals.Of(MergeAnswerKind.Conflicts).ShouldBe(Conflicting);

    [Fact]
    public void AHeadMovedAnswerIsANodeWaitForTheReviewOfTheNewHead() => MergeRefusals.Of(MergeAnswerKind.HeadMoved).ShouldBe(NodeWait);

    // Persons: each with the one thing to do.

    [Fact]
    public async Task ABranchProtectionApprovalIsApproveOnGitHub()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Merge = new MergeState(MergeKinds.Blocked, Reason: "at least 1 approving review is required");

        Refused(await Merge(node), Persons(MergeAction.ApproveOnGitHub), "is blocked on GitHub: at least 1 approving review is required");
    }

    [Fact]
    public async Task ChangesRequestedIsReadChanges()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Merge = new MergeState(MergeKinds.ChangesRequested);

        Refused(await Merge(node), Persons(MergeAction.ReadChanges), "has changes requested on GitHub");
    }

    [Fact]
    public async Task AFailedRequiredCheckIsRerunFailedCheck()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3);

        Refused(await Merge(node), Persons(MergeAction.RerunFailedCheck), "checks fail");
    }

    [Fact]
    public async Task AnOpenDesignQuestionIsAnswerDesignQuestion()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.State.RaiseCard(CardBin.ForYou, PrReviews.DesignKind, Verbs.PrTarget("x/tianwen", 63), "Which way?");

        Refused(await Merge(node), Persons(MergeAction.AnswerDesignQuestion), "has an open design question");
    }

    [Fact]
    public async Task AFlightWithNoPrIsOpenPrAgain()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Pr = null;

        Refused(await Merge(node), Persons(MergeAction.OpenPrAgain), "has no PR");
    }

    [Fact]
    public async Task CommitsUnpushedInThePlaneAreOpenPrAgain()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var flight = Flight(node);
        // The plane holds one commit past the PR's head, which is the head the review passed.
        var pushed = Plane(flight.Worktree);
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", "second");
        node.State.PutFlight(flight with { Review = flight.Review! with { ReviewedHead = pushed } });
        node.GitHub.Pr = new PullRequest(63, "OPEN", pushed, PrUrl);

        Refused(await Merge(node, pushed), Persons(MergeAction.OpenPrAgain), "plane holds 1 commit past #63's head");
    }

    [Fact]
    public async Task ADraftPrIsMarkReadyOnGitHubNotAnApproval()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Merge = new MergeState(MergeKinds.Blocked, Reason: "it is a draft", Draft: true);

        Refused(await Merge(node), Persons(MergeAction.MarkReadyOnGitHub), "it is a draft");
    }

    [Fact]
    public async Task AClosedPrIsAFactToReadWithNothingToPress()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.Pr = new PullRequest(63, "CLOSED", PrHead, PrUrl);

        Refused(await Merge(node), Persons(MergeAction.PrClosed), "is closed, not open");
    }

    [Fact]
    public async Task AMergeGitHubRefusesInItsOwnWordsIsAPersonsWithNothingToPress()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);
        node.GitHub.MergeAnswers = "gh: Required status check build is expected";

        Refused(await Merge(node), Persons(MergeAction.None), "Required status check build is expected");
    }

    [Fact]
    public async Task ABadAskIsAPersonsWithNothingToPress()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node);

        Refused(await Merge(node, "abc"), Persons(MergeAction.None), "7 characters");
    }

    // The fallback: nothing unclassified is hidden.

    [Fact]
    public void AnExceptionGivenNoClassIsPersonsWithNothingToPress()
    {
        MergeRefusals.Of(new FlightException("something new")).ShouldBe(Persons(MergeAction.None));
        MergeRefusals.Of(new InvalidOperationException("not even a flight's")).ShouldBe(Persons(MergeAction.None));
    }

    [Fact]
    public void AnUnclassifiedRefusalOfTheMergeVerbIsPersonsAndADoneOneIsNone()
    {
        MergeRefusals.Of(new CommandResult { Verb = "merge", Target = "flight/TIA1", Who = "node", Seq = 1, Ok = false, Error = "x" }).ShouldBe(Persons(MergeAction.None));
        MergeRefusals.Of(new CommandResult { Verb = "merge", Target = "flight/TIA1", Who = "node", Seq = 1, Ok = true }).ShouldBeNull();
    }

    [Fact]
    public void TheRefusalIsNodeInternalAndNeverOnTheWire()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new CommandResult { Verb = "merge", Target = "flight/TIA1", Who = "node", Seq = 1, Ok = false, Error = "x", Refusal = MergeRefusals.NeedsApproval }, PilotJsonContext.Default.CommandResult);
        json.ShouldNotContain("Refusal", Case.Insensitive);
    }
}
