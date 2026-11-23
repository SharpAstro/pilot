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
        public NodeState State { get; }

        public FakeGitHub GitHub { get; } = new();

        public (string Code, string Repo, int Number)[] Flights { get; } = [("PBA1", RepoA, 11), ("PBA2", RepoA, 12), ("PBB1", RepoB, 13), ("PBB2", RepoB, 14)];

        public Rig(bool batch, TimeProvider? time = null)
        {
            time ??= new FixedTime();
            State = new NodeState(new InMemoryJournal(), time);
            foreach (var (code, repo, number) in Flights)
            {
                var branch = $"flight/{code.ToLowerInvariant()}";
                State.PutFlight(new FlightRow
                {
                    Code = code, Repo = repo[^1..], RepoPath = repo, Issue = number, Branch = branch, Worktree = $"{repo}/w/{code}", Base = "main", State = FlightState.Flying,
                    CreatedAt = time.GetUtcNow(), PullRequest = number,
                });
                // The card's Merge is what a look mirrors the PR's checks onto.
                State.RaiseCard(CardBin.ForYou, LegCommander.PrReady, Verbs.FlightTarget(code), $"{code}'s PR #{number} is ready to merge",
                    choices: [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = [$"head{number}"] }]);
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
        var time = new ManualTime();
        var rig = new Rig(batch: true, time);
        var checks = new PrChecks(rig.State, rig.GitHub, time);
        await checks.LookAsync(Ct);
        rig.Cards().Length.ShouldBe(4);

        // PBA1 merged on GitHub, PBB1 now conflicts with its base: both are read once their PRs are due (CI running: the usual run length), not remembered from the first.
        rig.GitHub.PrOf["flight/pba1"] = rig.GitHub.PrOf["flight/pba1"] with { State = "MERGED" };
        rig.GitHub.PrOf["flight/pbb1"] = rig.GitHub.PrOf["flight/pbb1"] with { Mergeable = "CONFLICTING" };
        time.Advance(PrPollSchedule.UsualCiRun);
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

    // A clock a test moves by hand: a look is at the moment the test says, never at the wall clock.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private static CardChoice? MergeChoice(Rig rig, string code) =>
        rig.Cards().FirstOrDefault(c => c.Subject == Verbs.FlightTarget(code))?.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge);

    private sealed class HourRig
    {
        public ManualTime Time { get; } = new();

        public Rig Rig { get; }

        public PrChecks Checks { get; }

        public HourRig()
        {
            Rig = new Rig(batch: true, Time);
            Checks = new PrChecks(Rig.State, Rig.GitHub, Time);
        }

        public async Task LookAsync() => await Checks.LookAsync(Ct);

        /// <summary>Looks every 30 s (the loop's tick) until <paramref name="seen"/> holds; the time it took, or null once <paramref name="bound"/> passed.</summary>
        public async Task<TimeSpan?> SeenWithinAsync(Func<bool> seen, TimeSpan bound)
        {
            for (var waited = TimeSpan.Zero; waited <= bound; waited += Tick)
            {
                await LookAsync();
                if (seen())
                {
                    return waited;
                }
                Time.Advance(Tick);
            }
            return null;
        }
    }

    [Fact]
    public async Task AnHourOfRunningCiCostsAtMostSixtyReadsAndTwoPerRepoPerInterval()
    {
        var hour = new HourRig();
        // A flight waiting on a connected flight, with its PR and card: it reads nothing, not its PR, nor its issue's fields or labels.
        hour.Rig.State.PutFlight(new FlightRow
        {
            Code = "PBH1", Repo = "a", RepoPath = RepoA, Issue = 15, Branch = "flight/pbh1", Worktree = $"{RepoA}/w/PBH1", Base = "main", State = FlightState.Held,
            CreatedAt = hour.Time.GetUtcNow(), PullRequest = 15,
        });
        hour.Rig.State.RaiseCard(CardBin.ForYou, LegCommander.PrReady, Verbs.FlightTarget("PBH1"), "PBH1's PR #15 is ready to merge");
        hour.Rig.GitHub.PrOf["flight/pbh1"] = GhAnswers.PullRequestByBranch(number: 15, head: "head15", url: "https://github.com/o/r/pull/15", mergeable: "MERGEABLE");
        hour.Rig.GitHub.OpenBranches[RepoA].Enqueue("flight/pbh1");

        // The loop's tick is 30 s: 120 looks in the hour, with the clock moved by hand between them.
        var perTick = new List<(int A, int B)>();
        var before = (A: 0, B: 0);
        for (var i = 0; i < 120; i++)
        {
            await hour.LookAsync();
            var now = (A: hour.Rig.GitHub.ReadsOf(RepoA), B: hour.Rig.GitHub.ReadsOf(RepoB));
            perTick.Add((now.A - before.A, now.B - before.B));
            before = now;
            hour.Time.Advance(Tick);
        }

        // 1,440 before (4 flights, 3 reads, 120 looks): now one batch per repo whenever one of its PRs is due.
        var total = hour.Rig.GitHub.ReadsOf(RepoA) + hour.Rig.GitHub.ReadsOf(RepoB);
        total.ShouldBeLessThanOrEqualTo(60);
        total.ShouldBeGreaterThan(0);
        // At most 2 reads per repo in any one interval (the least wait while CI runs, 4 ticks).
        var window = (int)(PrPollSchedule.MinCiWait / Tick);
        for (var i = 0; i + window <= perTick.Count; i++)
        {
            perTick.Skip(i).Take(window).Sum(t => t.A).ShouldBeLessThanOrEqualTo(2);
            perTick.Skip(i).Take(window).Sum(t => t.B).ShouldBeLessThanOrEqualTo(2);
        }
        // The held flight's PR was never read, and nor were its issue's labels; its card is as it was.
        hour.Rig.GitHub.PrReads.Keys.ShouldNotContain(k => k.EndsWith("#15"));
        hour.Rig.GitHub.IssueLabelReads.ShouldBe(0);
        hour.Rig.Cards().Select(c => c.Subject).ShouldContain(Verbs.FlightTarget("PBH1"));
    }

    [Fact]
    public async Task AMergedAConflictingARedAndAQueuedPrAreEachSeenWithinOneInterval()
    {
        var hour = new HourRig();
        var rig = hour.Rig;
        // An hour in: CI has run past its usual length, so the PRs are on their longest wait.
        for (var i = 0; i < 100; i++)
        {
            await hour.LookAsync();
            hour.Time.Advance(Tick);
        }
        rig.Cards().Length.ShouldBe(4);
        // The longest a PR waits is one interval, whatever the state it is read in.
        var interval = PrPollSchedule.CiOverrunWait;

        var redBefore = MergeChoice(rig, "PBA2");
        rig.GitHub.SummaryOf[12] = GhAnswers.ChecksOf(ChoiceStatus.Failing, 2, 3, "build", "https://github.com/o/r/actions/runs/1");
        // GitHub blocks the merge on the failed check, which is what the card shows.
        rig.GitHub.MergeOf[12] = GhAnswers.MergeState(MergeKinds.Blocked, done: 3, total: 3, failedName: "build", failedLink: "https://github.com/o/r/actions/runs/1");
        (await hour.SeenWithinAsync(() => MergeChoice(rig, "PBA2") != redBefore, interval)).ShouldNotBeNull("red CI is seen within one interval");

        // A queued merge is told by GitHub's merge state of the PR: seen once the node reads it again.
        var mergeReads = () => rig.GitHub.Reads.TryGetValue((RepoB, nameof(IGitHub.MergeStateAsync)), out var n) ? n : 0;
        var queuedBefore = mergeReads();
        rig.GitHub.MergeOf[14] = GhAnswers.MergeState(MergeKinds.Queued, position: 1);
        (await hour.SeenWithinAsync(() => mergeReads() > queuedBefore, interval)).ShouldNotBeNull("a queued PR is seen within one interval");

        rig.GitHub.PrOf["flight/pbb1"] = rig.GitHub.PrOf["flight/pbb1"] with { Mergeable = "CONFLICTING" };
        (await hour.SeenWithinAsync(() => rig.Cards().All(c => c.Subject != Verbs.FlightTarget("PBB1")), interval)).ShouldNotBeNull("a conflict is seen within one interval");

        rig.GitHub.PrOf["flight/pba1"] = rig.GitHub.PrOf["flight/pba1"] with { State = "MERGED" };
        (await hour.SeenWithinAsync(() => rig.Cards().All(c => c.Subject != Verbs.FlightTarget("PBA1")), interval)).ShouldNotBeNull("a merge is seen within one interval");
    }

    [Fact]
    public async Task APushTheNodeMakesMarksItsPrDueAtOnce()
    {
        var hour = new HourRig();
        for (var i = 0; i < 20; i++)
        {
            await hour.LookAsync();
            hour.Time.Advance(Tick);
        }
        // Between reads: the next look reads nothing ...
        var reads = hour.Rig.GitHub.ReadsOf(RepoA);
        await hour.LookAsync();
        hour.Rig.GitHub.ReadsOf(RepoA).ShouldBe(reads);
        // ... until the node pushes the branch: due within 30 s, whatever the wait it was on.
        hour.Rig.State.PrPolls.MarkDue(RepoA, "flight/pba1");
        hour.Time.Advance(Tick);
        await hour.LookAsync();
        hour.Rig.GitHub.ReadsOf(RepoA).ShouldBe(reads + 1);
    }
}
