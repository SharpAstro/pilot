using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;
using static Pilot.Node.Tests.GhBatchFixtures;

namespace Pilot.Node.Tests;

/// <summary>
/// pilot #1293: the batch read of a repo's open PRs (<see cref="GhCli.ParseOpenByHead"/>) is what every flight-PR read of a look is answered from,
/// so for one PR it must say what the single reads (<c>gh pr view</c>, <c>gh pr checks</c>, the queue's GraphQL) say of it. Each case is one PR as
/// <see cref="GhCli.OpenPullRequestsByHeadQuery"/> answers it, with the same PR's single-read JSON beside it; the two paths give the same
/// <see cref="PullRequest"/>, <see cref="MergeState"/>, <see cref="ChecksSummary"/>, auto-merge flag and <see cref="MergeQueueState"/>.
/// </summary>
public sealed class GhBatchParseTests
{
    public static TheoryData<string, string?> Cases => new()
    {
        { "pending", MergeKinds.Pending },
        { "red", MergeKinds.Blocked },
        { "conflicting", MergeKinds.Conflicts },
        { "behind", MergeKinds.Behind },
        { "draft", MergeKinds.Blocked },
        { "armed", MergeKinds.Armed },
        { "queued", MergeKinds.Queued },
        { "dropped", MergeKinds.Ready },
        { "noqueue", MergeKinds.Ready },
        { "unknown", null },
    };

    private static Scenario Of(string name) => name switch
    {
        "pending" => new Scenario(MergeStatus: "BLOCKED", Checks: [Passed(), Running()], Labels: ["pilot: recheck"]),
        "red" => new Scenario(MergeStatus: "BLOCKED", Checks: [Red(), Running()]),
        "conflicting" => new Scenario(Merge: "CONFLICTING", MergeStatus: "DIRTY", Checks: [Passed()]),
        "behind" => new Scenario(MergeStatus: "BEHIND", Checks: [Passed()]),
        "draft" => new Scenario(Draft: true, MergeStatus: "DRAFT", Checks: [Passed()]),
        "armed" => new Scenario(Armed: true, MergeStatus: "BLOCKED", Checks: [Passed(), Running()]),
        "queued" => new Scenario(InQueue: true, Position: 2, Checks: [Passed()]),
        "dropped" => new Scenario(Removed: "2026-11-23T09:30:00Z", RemovedReason: "CI_FAILURE", Checks: [Passed()]),
        "noqueue" => new Scenario(QueueEnabled: false, Checks: [Passed()]),
        "unknown" => new Scenario(MergeStatus: "UNKNOWN", Checks: [Passed()]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheBatchAnswerSaysWhatTheSingleReadsSayOfTheSamePr(string name, string? kind)
    {
        var s = Of(name);
        var batch = One(s);

        var pull = GhCli.ParsePullRequestByBranch(ViewPullRequest(s));
        (batch.Pull.Number, batch.Pull.State, batch.Pull.Head, batch.Pull.Url, batch.Pull.Mergeable, batch.Pull.Draft).ShouldBe((pull.Number, pull.State, pull.Head, pull.Url, pull.Mergeable, pull.Draft));
        (batch.Pull.Labels ?? []).ShouldBe(pull.Labels ?? []);
        batch.HeadBranch.ShouldBe("flight/x");

        var queue = GhCli.ParseMergeQueue(QueueAnswer(s));
        batch.Queue.ShouldBe(queue);
        var merge = GhCli.ParseMergeState(ViewMergeState(s), queue);
        batch.Merge.ShouldBe(merge);
        batch.Merge?.Kind.ShouldBe(kind);
        (batch.Merge is null).ShouldBe(kind is null);

        batch.Checks.ShouldBe(s.Checks is null ? null : GhCli.ParseChecks(ViewChecks(s)));
        ((bool?)batch.AutoMergeArmed).ShouldBe(GhCli.ParseAutoMergeArmed(ViewMergeState(s)));
    }

    [Fact]
    public void TheShapesOfTheCasesAreWhatTheirNamesSay()
    {
        One(Of("pending")).Checks!.Status.ShouldBe(ChoiceStatus.Pending);
        One(Of("pending")).Pull.Labels.ShouldBe(["pilot: recheck"]);
        One(Of("red")).Checks!.Status.ShouldBe(ChoiceStatus.Failing);
        One(Of("red")).Merge!.FailedName.ShouldBe("build");
        One(Of("draft")).Pull.Draft.ShouldBeTrue();
        One(Of("armed")).AutoMergeArmed.ShouldBeTrue();
        One(Of("queued")).Queue!.Position.ShouldBe(2);
        One(Of("dropped")).Queue!.Queued.ShouldBeFalse();
        One(Of("dropped")).Merge!.DroppedFromQueue.ShouldNotBeNull();
        One(Of("noqueue")).Queue.ShouldBeNull();
        One(Of("pending")).AutoMergeArmed.ShouldBeFalse();
    }

    [Fact]
    public void ARunningCheckGivesTheStartOfTheOldestAndACompletedOneTheLongestRunLength()
    {
        var running = One(Of("pending"));
        running.CiStartedAt.ShouldBe(DateTimeOffset.Parse(Started, System.Globalization.CultureInfo.InvariantCulture));
        running.CiRunLength.ShouldBeNull("a check still runs: no length yet");

        var done = One(Of("noqueue"));
        done.CiStartedAt.ShouldBeNull();
        done.CiRunLength.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void ANullOrGarbledAnswerIsNullAndTheCostIsRead()
    {
        GhCli.ParseOpenByHead("not json").PullRequests.ShouldBeNull();
        GhCli.ParseOpenByHead("{}").PullRequests.ShouldBeNull();
        GhCli.ParseOpenByHead("{\"data\":{\"repository\":null}}").PullRequests.ShouldBeNull();
        GhCli.ParseOpenByHead("{\"data\":{\"repository\":{\"pullRequests\":{\"nodes\":[\"x\",{\"number\":\"no\"}]}}}}").PullRequests.ShouldBeEmpty();

        var answer = GhCli.ParseOpenByHead(Batch(Of("pending"), cost: 5));
        answer.Cost.ShouldBe(5);
        answer.PullRequests.ShouldHaveSingleItem();
        GhCli.ParseOpenByHead("{\"data\":{\"repository\":{\"pullRequests\":{\"nodes\":[]}}}}").Cost.ShouldBe(0);
    }

    [Fact]
    public void ANodeWithNoChecksHasNullChecksAndALabelBeyondTwentyIsKept()
    {
        var many = Enumerable.Range(1, 30).Select(i => $"l{i}").Append("pilot: hold").ToArray();
        var s = new Scenario(Checks: null, Labels: many);
        var one = One(s);
        one.Checks.ShouldBeNull();
        one.Pull.Labels.ShouldNotBeNull().ShouldContain("pilot: hold");
        GhCli.OpenPullRequestsByHeadQuery.ShouldContain("labels(first:100)");
    }
}
