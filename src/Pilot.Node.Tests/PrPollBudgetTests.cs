using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// pilot #1293, Want 1: one GitHub read per repo per look, not three per PR. A node in memory with four flights in two repos, each with
/// its PR open and its CI running, and a fake gh that counts every read per repo: a look reads each repo once (and a PR the batch does
/// not hold on its own), and a PR that merged, conflicts, went red or was queued is seen at the next look as it was before.
/// </summary>
public sealed class PrPollBudgetTests
{
    private const string RepoA = "/repos/a";

    private const string RepoB = "/repos/b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    }

    // Four flights, two per repo, each with a ready card and a PR (numbers 11 to 14) whose checks are still running.
    private sealed class Rig
    {
        public NodeState State { get; } = new(new InMemoryJournal(), new FixedTime());

        public FakeGitHub GitHub { get; } = new();

        public (string Code, string Repo, int Number)[] Flights { get; } = [("PBA1", RepoA, 11), ("PBA2", RepoA, 12), ("PBB1", RepoB, 13), ("PBB2", RepoB, 14)];

        public Rig(bool batch)
        {
            foreach (var (code, repo, number) in Flights)
            {
                var branch = $"flight/{code.ToLowerInvariant()}";
                State.PutFlight(new FlightRow
                {
                    Code = code, Repo = repo[^1..], RepoPath = repo, Issue = number, Branch = branch, Worktree = $"{repo}/w/{code}", Base = "main", State = FlightState.Flying,
                    CreatedAt = new FixedTime().GetUtcNow(), PullRequest = number,
                });
                State.RaiseCard(CardBin.ForYou, LegCommander.PrReady, Verbs.FlightTarget(code), $"{code}'s PR #{number} is ready to merge");
                GitHub.PrOf[branch] = GhAnswers.PullRequestByBranch(number: number, head: $"head{number}", url: $"https://github.com/o/r/pull/{number}", mergeable: "MERGEABLE");
                GitHub.SummaryOf[number] = GhAnswers.ChecksOf(ChoiceStatus.Pending, 1, 3);
                if (batch)
                {
                    GitHub.OpenBranches.GetOrAdd(repo, _ => new()).Enqueue(branch);
                }
            }
        }

        public PrChecks Checks() => new(State, GitHub, new FixedTime());

        public FlightRow Flight(string code) => State.Flights().Single(f => f.Code == code);

        public CardRow[] Cards() => State.Cards(openOnly: true).Where(c => c.Kind == LegCommander.PrReady).ToArray();
    }

    [Fact]
    public async Task ALookReadsEachRepoAtMostTwiceWhereItReadThreePerPr()
    {
        var before = new Rig(batch: false);
        await before.Checks().LookAsync(Ct);
        // Without the batch every PR costs its own PR, merge state and checks read: 3 per PR, 6 per repo of two flights.
        before.GitHub.ReadsOf(RepoA).ShouldBeGreaterThanOrEqualTo(6);
        before.GitHub.ReadsOf(RepoB).ShouldBeGreaterThanOrEqualTo(6);

        var after = new Rig(batch: true);
        await after.Checks().LookAsync(Ct);
        after.GitHub.ReadsOf(RepoA).ShouldBeLessThanOrEqualTo(2);
        after.GitHub.ReadsOf(RepoB).ShouldBeLessThanOrEqualTo(2);
        after.GitHub.Reads[(RepoA, nameof(IGitHub.OpenPullRequestsByHeadAsync))].ShouldBe(1);
        after.GitHub.Reads[(RepoB, nameof(IGitHub.OpenPullRequestsByHeadAsync))].ShouldBe(1);
        // Nothing was lost: every PR is still open with its card.
        after.Cards().Length.ShouldBe(4);
    }

    [Fact]
    public async Task EachLookReadsAgainSoAMergedAndAConflictingPrAreSeenAtTheNextLook()
    {
        var rig = new Rig(batch: true);
        var checks = rig.Checks();
        await checks.LookAsync(Ct);
        rig.Cards().Length.ShouldBe(4);

        // PBA1 merged on GitHub, PBB1 now conflicts with its base: both are read at the next look, not remembered from the first.
        rig.GitHub.PrOf["flight/pba1"] = rig.GitHub.PrOf["flight/pba1"] with { State = "MERGED" };
        rig.GitHub.PrOf["flight/pbb1"] = rig.GitHub.PrOf["flight/pbb1"] with { Mergeable = "CONFLICTING" };
        await checks.LookAsync(Ct);

        rig.Cards().Select(c => c.Subject).ShouldNotContain(Verbs.FlightTarget("PBA1"));
        rig.Cards().Select(c => c.Subject).ShouldNotContain(Verbs.FlightTarget("PBB1"));
        rig.Cards().Length.ShouldBe(2);
        rig.GitHub.Reads[(RepoA, nameof(IGitHub.OpenPullRequestsByHeadAsync))].ShouldBe(2);
    }

    [Fact]
    public async Task ARedCiAndAQueuedPrAreSeenAtTheNextLook()
    {
        var rig = new Rig(batch: true);
        var github = LookGitHub.Of(rig.GitHub);

        async Task<(MergeState? Merge, ChecksSummary? Checks)> Look(int number, string repo)
        {
            using var look = LookSnapshot.Begin();
            return (await github.MergeStateAsync(repo, number, Ct), await github.ChecksSummaryAsync(repo, number, Ct));
        }

        (await Look(12, RepoA)).Merge!.Kind.ShouldBe(MergeKinds.Pending);

        rig.GitHub.SummaryOf[12] = GhAnswers.ChecksOf(ChoiceStatus.Failing, 2, 3, "build", "https://github.com/o/r/actions/runs/1");
        var red = await Look(12, RepoA);
        red.Checks!.Status.ShouldBe(ChoiceStatus.Failing);
        red.Merge!.Kind.ShouldBe(MergeKinds.Blocked);
        red.Merge.FailedName.ShouldBe("build");

        rig.GitHub.MergeOf[14] = GhAnswers.MergeState(MergeKinds.Queued, position: 1);
        (await Look(14, RepoB)).Merge!.Kind.ShouldBe(MergeKinds.Queued);

        // One batch per look and repo (two looks of repo A, one of B), never one read per question.
        rig.GitHub.Reads[(RepoA, nameof(IGitHub.OpenPullRequestsByHeadAsync))].ShouldBe(2);
        rig.GitHub.Reads[(RepoB, nameof(IGitHub.OpenPullRequestsByHeadAsync))].ShouldBe(1);
        rig.GitHub.Reads.Where(r => r.Key.Call != nameof(IGitHub.OpenPullRequestsByHeadAsync)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AWriteDropsWhatTheLookReadOfThePrAndReadsOutsideALookGoToGitHub()
    {
        var rig = new Rig(batch: true);
        var github = LookGitHub.Of(rig.GitHub);

        // Outside a look nothing is answered from a snapshot.
        (await github.PullRequestAsync(RepoA, "flight/pba1", Ct))!.Number.ShouldBe(11);
        rig.GitHub.Reads[(RepoA, nameof(IGitHub.PullRequestAsync))].ShouldBe(1);
        rig.GitHub.Reads.ContainsKey((RepoA, nameof(IGitHub.OpenPullRequestsByHeadAsync))).ShouldBeFalse();

        using (LookSnapshot.Begin())
        {
            (await github.MergeStateAsync(RepoA, 11, Ct))!.Kind.ShouldBe(MergeKinds.Pending);
            rig.GitHub.SummaryOf[11] = GhAnswers.ChecksOf(ChoiceStatus.Passing, 3, 3);
            // Still the look's reading ...
            (await github.ChecksSummaryAsync(RepoA, 11, Ct))!.Status.ShouldBe(ChoiceStatus.Pending);
            // ... until a write to the PR: its next read is GitHub's.
            await github.SetDraftAsync(RepoA, 11, true, Ct);
            (await github.ChecksSummaryAsync(RepoA, 11, Ct))!.Status.ShouldBe(ChoiceStatus.Passing);
            // Another PR of the repo is still read from the look.
            (await github.ChecksSummaryAsync(RepoA, 12, Ct))!.Status.ShouldBe(ChoiceStatus.Pending);
        }
        rig.GitHub.Reads[(RepoA, nameof(IGitHub.OpenPullRequestsByHeadAsync))].ShouldBe(1);
    }
}
