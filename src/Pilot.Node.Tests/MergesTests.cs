using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's merge queue (pilot #395): a merge asked for is an entry per flight in its repo's queue, taken in order and one at a
/// time per repo, and an entry merges only with a passed review at its head and green checks. A conflict or a failed check takes
/// it out with its why, and a merge taken back removes it.
/// </summary>
public sealed class MergesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";

    private static FlightRow Flight(NodeHarness node, string code, string repo = "tianwen", ReviewState? review = ReviewState.Passed)
    {
        var row = new FlightRow
        {
            Code = code, Repo = repo, RepoPath = Path.Combine(node.Folder, repo), Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = Path.Combine(node.Folder, $"plane-{code}"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = review is { } state ? new ReviewRow { State = state, Reviewer = "du1-review", StartedAt = DateTimeOffset.UtcNow, ReviewedHead = PrHead } : null,
        };
        node.State.PutFlight(row);
        node.State.PutLeg(new LegRow { Flight = code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return row;
    }

    private static Task<CommandResult> Merge(NodeHarness node, string code, string by = "tests") =>
        node.Client.SendAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = [PrHead], Who = by }, Ct).ContinueWith(t => t.Result.Response.ShouldNotBeNull(), Ct);

    private static MergeRow Entry(NodeHarness node, string code) => node.State.Merges().Single(m => m.Flight == code);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }
        condition().ShouldBeTrue(what);
    }

    [Fact]
    public async Task AMergeAddsAnEntryNamingItsFlightPrRepoHeadAndWhereItStands()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;

        (await Merge(node, "TIA1")).Error.ShouldBeNull();

        var entry = Entry(node, "TIA1");
        (entry.Repo, entry.Pr, entry.Head, entry.Method, entry.By).ShouldBe(("tianwen", 63, PrHead, "rebase", MergeBy.Hand));
        entry.State.ShouldBe(MergeEntryState.Merging);
        entry.Place.ShouldBe(1);
        entry.Why.ShouldNotBeNull().ShouldContain("merge queue");
        node.GitHub.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AFlightsMergeWithoutAPassedReviewIsRefusedAndJoinsNoQueue()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1", review: ReviewState.Reviewing);

        var result = await Merge(node, "TIA1");

        result.Error.ShouldNotBeNull().ShouldContain("not passed");
        node.State.Merges().ShouldBeEmpty();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReposEntriesAreTakenInOrderOneAtATimeWhileAnotherReposGoesThroughAtOnce()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        Flight(node, "TIA2");
        Flight(node, "MOO3", repo: "moons");
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        node.GitHub.OnCall = async call =>
        {
            if (call == nameof(IGitHub.MergeAsync) && Interlocked.Increment(ref first) == 1)
            {
                reached.TrySetResult();
                await open.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };

        var one = Merge(node, "TIA1");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var two = Merge(node, "TIA2");
        await Until(() => node.State.Merges().Any(m => m.Flight == "TIA2"), "TIA2 joined the queue");

        // TIA1 is at GitHub, so TIA2 stands behind it: second in its repo's queue, nothing of it asked of GitHub yet.
        (Entry(node, "TIA1").State, Entry(node, "TIA1").Place).ShouldBe((MergeEntryState.Merging, 1));
        (Entry(node, "TIA2").State, Entry(node, "TIA2").Place).ShouldBe((MergeEntryState.Waiting, 2));
        node.GitHub.Merged.ShouldBeEmpty();
        two.IsCompleted.ShouldBeFalse();

        // Another repo's queue is its own: its entry goes through while TIA1 is held (its GitHub call is the second, and not held).
        (await Merge(node, "MOO3")).Error.ShouldBeNull();
        Entry(node, "MOO3").Place.ShouldBe(1);
        two.IsCompleted.ShouldBeFalse();

        open.TrySetResult();
        (await one).Error.ShouldBeNull();
        (await two).Error.ShouldBeNull();
        node.GitHub.Merged.Count.ShouldBe(3);
        Entry(node, "TIA2").State.ShouldBe(MergeEntryState.Merging);
        Entry(node, "TIA1").AddedAt.ShouldBeLessThan(Entry(node, "TIA2").AddedAt);
    }

    [Fact]
    public async Task AMergeWhileChecksRunWaitsInTheQueueForThemAndAsksAgainAtGreenKeepingItsPlace()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);

        (await Merge(node, "TIA1")).Error.ShouldBeNull();

        var waiting = Entry(node, "TIA1");
        waiting.State.ShouldBe(MergeEntryState.Checks);
        waiting.Why.ShouldNotBeNull().ShouldContain("checks pass");
        node.GitHub.Merged.ShouldBeEmpty();

        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;
        (await Merge(node, "TIA1", by: "node")).Error.ShouldBeNull();

        // One entry per flight: the same row, at its place, now merging, and still the person's merge though the node asked it again.
        var merging = Entry(node, "TIA1");
        merging.State.ShouldBe(MergeEntryState.Merging);
        merging.AddedAt.ShouldBe(waiting.AddedAt);
        merging.By.ShouldBe(MergeBy.Hand);
        node.GitHub.Merged.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("closed")]
    public async Task AMergingEntryEndsWithItsWhyWhenGitHubsQueueLetsItsPrGoUnmergedAndTheReposLaterEntriesMoveUp(string how)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        Flight(node, "TIA2", review: ReviewState.Reviewing);
        var at = DateTimeOffset.UtcNow;
        // TIA1 was handed to GitHub's merge queue and its command has ended; TIA2 stands behind it, waiting for its review.
        node.State.PutMerge(new MergeRow { Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Merging, Why = LegCommander.QueuedLine("TIA1"), AddedAt = at });
        node.State.PutMerge(new MergeRow { Flight = "TIA2", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, AwaitsReview = true, AddedAt = at.AddSeconds(1) });
        node.State.Renumber("tianwen");
        Entry(node, "TIA2").Place.ShouldBe(2);
        node.GitHub.Queue = how == "closed"
            ? new MergeQueueState("CLOSED", PrHead, Queued: false)
            : new MergeQueueState("OPEN", PrHead, Queued: false, RemovedAt: at.AddMinutes(1), RemovedReason: "a required check failed");

        await node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);

        var left = Entry(node, "TIA1");
        (left.State, left.Place).ShouldBe((MergeEntryState.Dropped, null));
        left.Why.ShouldNotBeNull().ShouldContain(how == "closed" ? "closed unmerged" : "out of the merge queue unmerged: a required check failed");
        (Entry(node, "TIA2").State, Entry(node, "TIA2").Place).ShouldBe((MergeEntryState.Checks, 1));
    }

    [Fact]
    public async Task AMergingEntryStillInGitHubsQueueStays()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        node.State.PutMerge(new MergeRow { Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Merging, AddedAt = DateTimeOffset.UtcNow });
        node.GitHub.Queue = new MergeQueueState("OPEN", PrHead, Queued: true, Position: 2);

        await node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);

        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Merging);
    }

    [Fact]
    public async Task AnEntryWaitsForAPassedReviewAtItsHeadAndMergesOnlyOnceItHasOne()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var flight = Flight(node, "TIA1", review: ReviewState.Reviewing);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;
        node.State.PutMerge(new MergeRow
        {
            Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "squash", State = MergeEntryState.Checks, AwaitsReview = true,
            Why = "waits for a fresh review at its head", AddedAt = DateTimeOffset.UtcNow,
        });
        var legs = node.App.Services.GetRequiredService<LegCommander>();

        await legs.LookAtMergesAsync(Ct);

        node.GitHub.Merged.ShouldBeEmpty();
        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Checks);

        node.State.PutFlight(flight with { Review = flight.Review! with { State = ReviewState.Passed, ReviewedHead = PrHead } });
        await legs.LookAtMergesAsync(Ct);

        node.GitHub.Merged.ShouldHaveSingleItem();
        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Merging);
    }

    [Fact]
    public async Task AReviewThatAsksForChangesTakesAnEntryWaitingForItOutWithItsWhy()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var flight = Flight(node, "TIA1", review: ReviewState.Changes);
        node.State.PutMerge(new MergeRow
        {
            Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "squash", State = MergeEntryState.Checks, AwaitsReview = true, AddedAt = DateTimeOffset.UtcNow,
        });

        await node.App.Services.GetRequiredService<LegCommander>().LookAtMergesAsync(Ct);

        var entry = Entry(node, flight.Code);
        entry.State.ShouldBe(MergeEntryState.Dropped);
        entry.Why.ShouldNotBeNull().ShouldContain("changes");
        entry.Place.ShouldBeNull();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedCheckTakesTheEntryOutWithItsWhy()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3);

        (await Merge(node, "TIA1")).Error.ShouldNotBeNull().ShouldContain("checks fail");

        var entry = Entry(node, "TIA1");
        entry.State.ShouldBe(MergeEntryState.Dropped);
        entry.Why.ShouldNotBeNull().ShouldContain("checks fail");
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task APrThatConflictsLeavesTheQueueWithItsWhyAndLetsTheNextEntryGoOn()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        Flight(node, "TIA2");
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Pr = node.GitHub.Pr! with { Mergeable = "CONFLICTING" };

        (await Merge(node, "TIA1")).Error.ShouldNotBeNull().ShouldContain("conflicts");

        // The plane is the watchtower's to resolve; the entry says it left and why, and holds nothing of the repo's turn.
        var entry = Entry(node, "TIA1");
        entry.State.ShouldBe(MergeEntryState.Dropped);
        entry.Why.ShouldNotBeNull();
        node.GitHub.Pr = node.GitHub.Pr! with { Mergeable = "MERGEABLE" };
        node.GitHub.Queues = true;
        (await Merge(node, "TIA2")).Error.ShouldBeNull();
        Entry(node, "TIA2").Place.ShouldBe(1);
    }

    [Fact]
    public async Task ATakenBackMergeRemovesItsEntry()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        (await Merge(node, "TIA1")).Error.ShouldBeNull();
        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Checks);

        var plan = await node.Client.SendAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = Verbs.FlightTarget("TIA1"), Args = ["name=render", "goal=Draw", "done-when=It draws"], Who = "tests",
        }, Ct);

        plan.Response.ShouldNotBeNull().Error.ShouldBeNull();
        node.State.Merges().ShouldBeEmpty();
    }

    [Fact]
    public async Task ANodeStartedOnEntriesNothingDrivesEndsThemWithWhyAndLeavesAMergingOne()
    {
        // A journal a node that went away left: entries open in every state, and no merge command behind any of them.
        var folder = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);
        using (var journal = new FileJournal(Path.Combine(folder, "data", "store")))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var place = 0;
            foreach (var (code, entry) in new[] { ("TIA1", MergeEntryState.Merging), ("TIA2", MergeEntryState.Updating), ("TIA3", MergeEntryState.Batched), ("TIA4", MergeEntryState.Waiting), ("TIA5", MergeEntryState.Checks) })
            {
                place++;
                state.PutMerge(new MergeRow { Flight = code, Repo = "tianwen", Pr = 60 + place, Head = PrHead, Method = "rebase", State = entry, Place = place, AddedAt = at.AddSeconds(place) });
            }
        }

        await using var node = await NodeHarness.StartAsync(Ct, folder: folder);
        await Until(() => node.State.Merges().Count(m => m.Open) == 1, "every entry nothing drives ended");

        foreach (var code in new[] { "TIA2", "TIA3", "TIA4" })
        {
            (Entry(node, code).State, Entry(node, code).Place).ShouldBe((MergeEntryState.Dropped, null));
            Entry(node, code).Why.ShouldNotBeNull().ShouldContain("node restarted");
        }
        // Waiting for checks with no merge armed on its flight: nothing would ever take it.
        Entry(node, "TIA5").State.ShouldBe(MergeEntryState.Dropped);
        Entry(node, "TIA5").Why.ShouldNotBeNull().ShouldContain("no longer armed");
        // GitHub's own queue may hold a merging one: the landing ends it, and it is first in its repo's queue now.
        (Entry(node, "TIA1").State, Entry(node, "TIA1").Place).ShouldBe((MergeEntryState.Merging, 1));
    }

    [Fact]
    public async Task AtEachLookAnEntryWhoseArmIsGoneEndsWhileAnArmedOneAndOneWaitingForItsReviewStay()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var armed = Flight(node, "TIA1");
        node.State.PutFlight(armed with { MergeWhenGreen = [PrHead, "rebase", LegCommander.ArmedByNode] });
        Flight(node, "TIA2", review: ReviewState.Reviewing);
        Flight(node, "TIA3");
        var at = DateTimeOffset.UtcNow;
        node.State.PutMerge(new MergeRow { Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, Why = "merging when checks pass", AddedAt = at });
        node.State.PutMerge(new MergeRow { Flight = "TIA2", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, AwaitsReview = true, AddedAt = at.AddSeconds(1) });
        // TIA3's arm was cleared (GitHub showed its PR behind its base), and its entry still says it merges when checks pass.
        node.State.PutMerge(new MergeRow { Flight = "TIA3", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, Why = "merging when checks pass", AddedAt = at.AddSeconds(2) });

        await node.App.Services.GetRequiredService<LegCommander>().LookAtMergesAsync(Ct);

        (Entry(node, "TIA1").State, Entry(node, "TIA1").Place).ShouldBe((MergeEntryState.Checks, 1));
        (Entry(node, "TIA2").State, Entry(node, "TIA2").Place).ShouldBe((MergeEntryState.Checks, 2));
        Entry(node, "TIA3").State.ShouldBe(MergeEntryState.Dropped);
        Entry(node, "TIA3").Why.ShouldNotBeNull().ShouldContain("no longer armed");
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnEntryArmedOnGitHubsAutoMergeEndsMergedWhenGitHubMergesItsPr()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // The landing asks git in the repo's folder.
        Directory.CreateDirectory(Flight(node, "TIA1").RepoPath);
        node.GitHub.AutoMergeAllowed = true;
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        (await Merge(node, "TIA1")).Error.ShouldBeNull();
        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Checks);

        // GitHub merges it: the look clears the arm and finds the entry with nothing armed before the flight lands.
        node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);
        await node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);

        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        (Entry(node, "TIA1").State, Entry(node, "TIA1").Place).ShouldBe((MergeEntryState.Merged, null));
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEntryWaitingForItsReviewWhosePrAPersonMergedEndsMerged()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Directory.CreateDirectory(Flight(node, "TIA1").RepoPath);
        node.State.PutMerge(new MergeRow
        {
            Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, AwaitsReview = true, AddedAt = DateTimeOffset.UtcNow,
        });
        node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);

        // The look's merge is refused (the PR is merged, not open), which drops the entry before the same look lands the flight.
        await node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);

        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        var entry = Entry(node, "TIA1");
        (entry.State, entry.Place).ShouldBe((MergeEntryState.Merged, null));
        entry.Why.ShouldBeNull();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMergeTakenBackTooLateEndsItsEntryMergedAndTheReposNextEntryMovesUp()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        Flight(node, "TIA2", review: ReviewState.Reviewing);
        var at = DateTimeOffset.UtcNow;
        // TIA1 was handed to GitHub's merge queue and its command has ended; TIA2 stands behind it.
        node.State.PutMerge(new MergeRow { Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Merging, Why = LegCommander.QueuedLine("TIA1"), AddedAt = at });
        node.State.PutMerge(new MergeRow { Flight = "TIA2", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, AwaitsReview = true, AddedAt = at.AddSeconds(1) });
        node.State.Renumber("tianwen");
        node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);

        // A leg planned now finds the PR merged already: the flight lets its PR go, so no look of the queues sees it again.
        var plan = await node.Client.SendAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = Verbs.FlightTarget("TIA1"), Args = ["name=render", "goal=Draw", "done-when=It draws"], Who = "tests",
        }, Ct);

        plan.Response.ShouldNotBeNull().Error.ShouldBeNull();
        node.State.Flights().Single(f => f.Code == "TIA1").MergedHead.ShouldBe(PrHead);
        var entry = Entry(node, "TIA1");
        (entry.State, entry.Place).ShouldBe((MergeEntryState.Merged, null));
        entry.Why.ShouldNotBeNull().ShouldContain("new PR");
        (Entry(node, "TIA2").State, Entry(node, "TIA2").Place).ShouldBe((MergeEntryState.Checks, 1));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AMergeCallThatNeverAnswersEndsItsMergingEntryAndGivesTheTurnBack(bool mergedMeanwhile, bool givenUp)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Flight(node, "TIA1");
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        // The merge call goes past its time budget (gh's own, an OperationCanceledException) or gh does not start at all; GitHub may
        // have merged the PR all the same.
        node.GitHub.OnCall = call =>
        {
            if (call != nameof(IGitHub.MergeAsync))
            {
                return Task.CompletedTask;
            }
            if (mergedMeanwhile)
            {
                node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);
            }
            return Task.FromException(givenUp ? new OperationCanceledException() : new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"));
        };
        var legs = node.App.Services.GetRequiredService<LegCommander>();

        var died = await Record.ExceptionAsync(() => legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget("TIA1"), Args = [PrHead], Who = "tests" }, Ct));

        died.ShouldNotBeNull().ShouldNotBeOfType<FlightException>();
        var entry = Entry(node, "TIA1");
        entry.Place.ShouldBeNull();
        if (mergedMeanwhile)
        {
            entry.State.ShouldBe(MergeEntryState.Merged);
        }
        else
        {
            entry.State.ShouldBe(MergeEntryState.Dropped);
            entry.Why.ShouldNotBeNull().ShouldContain(givenUp ? "given up before GitHub answered" : "failed before GitHub answered");
        }
        legs.TurnHeld("tianwen").ShouldBeFalse();
    }

    [Fact]
    public async Task AMergeWhoseEntryTheJournalCannotWriteGivesTheReposTurnBack()
    {
        var journal = new MergeRowsFail();
        await using var node = await NodeHarness.StartAsync(Ct, journal: journal);
        var flight = Flight(node, "TIA1");
        // Armed at an earlier merge: its entry is open, so the repo's turn would be held for as long as the flight counts as under way.
        node.State.PutFlight(flight with { MergeWhenGreen = [PrHead, "rebase", LegCommander.ArmedByNode] });
        node.State.PutMerge(new MergeRow { Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, Why = "merging when checks pass", AddedAt = DateTimeOffset.UtcNow });
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        var legs = node.App.Services.GetRequiredService<LegCommander>();

        journal.Fail = true;
        var died = await Record.ExceptionAsync(() => legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget("TIA1"), Args = [PrHead], Who = "tests" }, Ct));
        journal.Fail = false;

        died.ShouldBeOfType<IOException>();
        legs.TurnHeld("tianwen").ShouldBeFalse();
        // Not refused as being merged already: asked again once the journal writes, the merge is taken and armed again. Its checks
        // still run, so the node's own look (which merges an armed flight at green) does not merge it behind the test's back.
        (await Merge(node, "TIA1")).Error.ShouldBeNull();
        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Checks);
        legs.TurnHeld("tianwen").ShouldBeFalse();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    // A store that cannot write a merge entry (a full disk) while Fail is on, and keeps everything else.
    private sealed class MergeRowsFail : INodeJournal
    {
        private readonly InMemoryJournal _inner = new();

        public bool Fail { get; set; }

        public NodeHistory Load() => _inner.Load();

        public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
        {
            if (Fail && nodeEvent is MergeAddedEvent or MergeChangedEvent)
            {
                throw new IOException("There is not enough space on the disk.");
            }
            _inner.Append(nodeEvent, json);
        }
    }

    [Fact]
    public void TheMirrorKeepsTheEntriesLikeJobsAndALineSaysEachEntrysFields()
    {
        var at = DateTimeOffset.UtcNow;
        var row = new MergeRow { Flight = "TIA1", Repo = "tianwen", Pr = 63, Head = PrHead, Method = "squash", State = MergeEntryState.Checks, Why = "merging when checks pass", Place = 1, AddedAt = at };
        var mirror = new NodeMirror();
        mirror.Apply(new SnapshotEvent { Seq = 0, At = at, Stream = "s", Sessions = [], Notes = [], Merges = [row] });
        mirror.Merges["TIA1"].ShouldBe(row);

        mirror.Apply(new MergeChangedEvent { Seq = 1, At = at, Merge = row with { State = MergeEntryState.Merging, Why = null } });
        mirror.Merges["TIA1"].State.ShouldBe(MergeEntryState.Merging);
        mirror.Apply(new MergeAddedEvent { Seq = 2, At = at, Merge = row with { Flight = "TIA2", Place = 2, State = MergeEntryState.Waiting } });
        mirror.Merges.Count.ShouldBe(2);
        mirror.Apply(new MergeRemovedEvent { Seq = 3, At = at, Flight = "TIA1" });
        mirror.Merges.Keys.ShouldBe(["TIA2"]);

        PilotCommand.MergeLine(row).ShouldBe($"TIA1  #63  tianwen  1  {PrHead[..8]}  checks  · merging when checks pass");
    }
}
