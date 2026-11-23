using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Poll by state (pilot #1293): what the node writes itself marks the PR due, so no change of its own waits out a long interval to be
/// seen. A flight's PR is put on CI's long wait first (its next read minutes away); then a lease push (the branch laid on its base, or
/// rebased onto it before a rebase merge) or a <c>pilot/review</c> status set by <see cref="PrMarks"/> brings its next read within 30 s.
/// The node's state runs on the system clock here, so each test reads the schedule's next-read time against the moment it looked.
/// </summary>
public sealed class PrPollBudgetTestsOfTheNodesWrites : ReviewTestsBase
{
    private static void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-a", "-m", message);

    /// <summary>Reads the flight's PR into the schedule with its CI just started: its next read is the usual run length away, so it is not due.</summary>
    private void OnTheLongWait(FlightRow flight)
    {
        var now = DateTimeOffset.UtcNow;
        var pr = new OpenPullRequest(flight.Branch, _github.Pr!, null, GhAnswers.ChecksOf(ChoiceStatus.Pending, 1, 3), false, null, CiStartedAt: now);
        _state.PrPolls.Read(flight.RepoPath, [flight.Branch], [pr]);
        _state.PrPolls.Due(flight.RepoPath, [flight.Branch]).ShouldBeFalse("CI just started: the PR waits for it");
        var next = _state.PrPolls.NextRead(flight.RepoPath, flight.Branch);
        next.ShouldNotBeNull();
        (next.Value - now).ShouldBeGreaterThan(PrPollSchedule.JustChanged);
    }

    /// <summary>
    /// The PR's next-read time as the node's first status write on a head other than <paramref name="old"/> begins: after the lease push
    /// that moved the branch, before that write (or the merge after it) marks the PR due itself. The moment it was taken comes with it.
    /// </summary>
    private Func<(DateTimeOffset At, DateTimeOffset? Next)?> NextReadAtTheFirstWriteAfterThePush(FlightRow flight, string old)
    {
        (DateTimeOffset At, DateTimeOffset? Next)? seen = null;
        _github.OnStatus = sha =>
        {
            if (seen is null && !string.Equals(sha, old, StringComparison.OrdinalIgnoreCase))
            {
                seen = (DateTimeOffset.UtcNow, _state.PrPolls.NextRead(flight.RepoPath, flight.Branch));
            }
        };
        return () => seen;
    }

    private static void DueWithinJustChanged((DateTimeOffset At, DateTimeOffset? Next)? seen, string what)
    {
        seen.ShouldNotBeNull($"the review was carried to the {what} head, whose status the node set");
        var (at, next) = seen.Value;
        next.ShouldNotBeNull($"the {what} branch's PR has a next-read time");
        (next.Value - at).ShouldBeLessThanOrEqualTo(PrPollSchedule.JustChanged, $"the {what} branch's PR is read within 30 s");
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the branch is laid on its base (merge-tree, commit-tree) and lease-pushed to the seed's bare origin
    public async Task ALeasePushOfABranchLaidOnItsBaseMarksItsPrDue()
    {
        RealGit();
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        // main gains f.txt twice; the branch adds it differently, keeps its side in a keep-ours merge of main and edits it again: the merge
        // with main is clean, the rebase stops on the first commit's f.txt, so the branch is laid on main as one commit.
        var (flight, _) = await Passed("1");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "x\n");
        Git(Repo, "add", "f.txt");
        Commit(Repo, "f");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "side\n");
        Commit(Repo, "side");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        Git(plane, "fetch", "-q", "origin");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine1\n");
        Git(plane, "add", ".");
        Commit(plane, "mine 1");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "-s", "ours", "-m", "keep ours", "origin/main");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine2\n");
        Commit(plane, "mine 2");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var old = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = old } : null });
        OnTheLongWait(flight);
        var seen = NextReadAtTheFirstWriteAfterThePush(flight, old);

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head)).Error.ShouldBeNull();

        Git(plane, "rev-parse", $"refs/pilot/laid/{flight.Code}").ShouldBe(old, "the branch was laid");
        DueWithinJustChanged(seen(), "laid");
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: a branch holding a merge of its base is rebased onto it and lease-pushed to the seed's bare origin
    public async Task ALeasePushOfABranchRebasedOntoItsBaseMarksItsPrDue()
    {
        RealGit();
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        // The branch has its own commit, then merges origin/main (which moved on another file): on a rebase base it is pushed linear.
        var (flight, _) = await Passed("1");
        File.WriteAllText(Path.Combine(Repo, "base.txt"), "base\n");
        Git(Repo, "add", "base.txt");
        Commit(Repo, "base moves");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        File.WriteAllText(Path.Combine(plane, "mine.txt"), "mine\n");
        Git(plane, "add", ".");
        Commit(plane, "mine");
        Git(plane, "fetch", "-q", "origin");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "--no-ff", "-q", "-m", "take main", "origin/main");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var old = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = old } : null });
        OnTheLongWait(flight);
        var seen = NextReadAtTheFirstWriteAfterThePush(flight, old);

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head)).Error.ShouldBeNull();

        Git(plane, "rev-parse", "HEAD").ShouldNotBe(old, "the branch was rebased");
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.ShapedKind);
        DueWithinJustChanged(seen(), "rebased");
    }

    [Fact]
    public async Task APilotReviewStatusTheNodeSetsMarksThePrDue()
    {
        var (flight, _) = await Passed("70");
        var head = flight.Review.ShouldNotBeNull().ReviewedHead.ShouldNotBeNull();
        OnTheLongWait(flight);
        // A status GitHub refused earlier is set again by the node's retry: through the look-aware GitHub, which marks the repo's PRs due.
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review! with { StatusState = null, MarksWhy = "GitHub refused the status" } });
        var writes = _github.Statuses.Count;

        await _legs.RetryMarksAsync(Ct);
        var at = DateTimeOffset.UtcNow;

        _github.Statuses.Count.ShouldBe(writes + 1);
        _github.Statuses.Last().Sha.ShouldBe(head);
        var next = _state.PrPolls.NextRead(flight.RepoPath, flight.Branch);
        next.ShouldNotBeNull();
        (next.Value - at).ShouldBeLessThanOrEqualTo(PrPollSchedule.JustChanged, "the PR is read within 30 s of the status write");
    }
}
