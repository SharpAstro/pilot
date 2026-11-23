using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// pilot #1293: the batch read of a repo's open PRs (<see cref="GhCli.ParseOpenByHead"/>) is what every flight-PR read of a look is answered from,
/// so for one PR it must say what the single reads (<c>gh pr view</c>, <c>gh pr checks</c>, the queue's GraphQL) say of it. Each case is one PR as
/// <see cref="GhCli.OpenPullRequestsByHeadQuery"/> answers it, with the same PR's single-read JSON beside it; the two paths give the same
/// <see cref="PullRequest"/>, <see cref="MergeState"/>, <see cref="ChecksSummary"/>, auto-merge flag and <see cref="MergeQueueState"/>.
/// </summary>
public sealed class GhBatchParseTests
{
    private const string Started = "2026-11-23T09:00:00Z";

    private const string Ended = "2026-11-23T09:03:00Z";

    private sealed record Check(string Name, string Status, string? Conclusion, string Bucket);

    private sealed record Scenario(
        string State = "OPEN", bool Draft = false, string Merge = "MERGEABLE", string MergeStatus = "CLEAN", string? Review = null, bool Armed = false,
        bool QueueEnabled = true, bool InQueue = false, int? Position = null, string? Removed = null, string? RemovedReason = null, Check[]? Checks = null, string[]? Labels = null, int Number = 7);

    private static Check Passed(string name = "build") => new(name, "COMPLETED", "SUCCESS", "pass");

    private static Check Running(string name = "test") => new(name, "IN_PROGRESS", null, "pending");

    private static Check Red(string name = "build") => new(name, "COMPLETED", "FAILURE", "fail");

    private static string Q(string? text) => text is null ? "null" : $"\"{text}\"";

    // The PR as the batch query answers it, wrapped as the whole answer with the cost.
    private static string Batch(Scenario s, int cost = 2)
    {
        var contexts = string.Join(",", (s.Checks ?? []).Select(c =>
            $"{{\"__typename\":\"CheckRun\",\"name\":\"{c.Name}\",\"status\":\"{c.Status}\",\"conclusion\":{Q(c.Conclusion)},\"detailsUrl\":\"https://github.com/o/r/actions/runs/{c.Name}\",\"startedAt\":\"{Started}\",\"completedAt\":{Q(c.Conclusion is null ? null : Ended)},\"checkSuite\":{{\"workflowRun\":{{\"workflow\":{{\"name\":\"CI\"}}}}}}}}"));
        var rollup = s.Checks is null ? "null" : $"{{\"contexts\":{{\"nodes\":[{contexts}]}}}}";
        var labels = string.Join(",", (s.Labels ?? []).Select(l => $"{{\"name\":\"{l}\"}}"));
        return $"{{\"data\":{{\"rateLimit\":{{\"cost\":{cost},\"remaining\":4990}},\"repository\":{{\"pullRequests\":{{\"nodes\":[{{"
            + $"\"number\":{s.Number},\"state\":\"{s.State}\",\"url\":\"https://github.com/o/r/pull/{s.Number}\",\"isDraft\":{(s.Draft ? "true" : "false")},\"headRefName\":\"flight/x\",\"headRefOid\":\"abc123\","
            + $"\"labels\":{{\"nodes\":[{labels}]}},\"mergeable\":\"{s.Merge}\",\"mergeStateStatus\":\"{s.MergeStatus}\",\"reviewDecision\":{Q(s.Review)},"
            + $"\"isMergeQueueEnabled\":{(s.QueueEnabled ? "true" : "false")},\"isInMergeQueue\":{(s.InQueue ? "true" : "false")},\"mergeQueueEntry\":{(s.Position is { } p ? $"{{\"position\":{p}}}" : "null")},"
            + $"\"autoMergeRequest\":{(s.Armed ? "{\"enabledAt\":\"2026-11-23T09:01:00Z\"}" : "null")},"
            + $"\"timelineItems\":{{\"nodes\":[{(s.Removed is { } at ? $"{{\"__typename\":\"RemovedFromMergeQueueEvent\",\"createdAt\":\"{at}\",\"reason\":{Q(s.RemovedReason)}}}" : "")}]}},"
            + $"\"commits\":{{\"nodes\":[{{\"commit\":{{\"statusCheckRollup\":{rollup}}}}}]}}}}]}}}}}}}}";
    }

    // The same PR as gh pr view says it (the fields of PullRequestByBranchFields).
    private static string ViewPullRequest(Scenario s) =>
        $"{{\"number\":{s.Number},\"state\":\"{s.State}\",\"headRefOid\":\"abc123\",\"url\":\"https://github.com/o/r/pull/{s.Number}\",\"mergeable\":\"{s.Merge}\","
        + $"\"labels\":[{string.Join(",", (s.Labels ?? []).Select(l => $"{{\"name\":\"{l}\"}}"))}],\"mergeCommit\":null,\"mergedAt\":null,\"isDraft\":{(s.Draft ? "true" : "false")}}}";

    // gh pr view's MergeStateFields.
    private static string ViewMergeState(Scenario s) =>
        $"{{\"state\":\"{s.State}\",\"isDraft\":{(s.Draft ? "true" : "false")},\"mergeStateStatus\":\"{s.MergeStatus}\",\"mergeable\":\"{s.Merge}\",\"reviewDecision\":{Q(s.Review)},"
        + $"\"autoMergeRequest\":{(s.Armed ? "{\"enabledAt\":\"2026-11-23T09:01:00Z\"}" : "null")},\"statusCheckRollup\":[{string.Join(",", (s.Checks ?? []).Select(c =>
            $"{{\"__typename\":\"CheckRun\",\"name\":\"{c.Name}\",\"workflowName\":\"CI\",\"status\":\"{c.Status}\",\"conclusion\":{Q(c.Conclusion)},\"detailsUrl\":\"https://github.com/o/r/actions/runs/{c.Name}\",\"startedAt\":\"{Started}\",\"completedAt\":{Q(c.Conclusion is null ? null : Ended)}}}"))}]}}";

    // gh pr checks --json bucket,name,link.
    private static string ViewChecks(Scenario s) =>
        $"[{string.Join(",", (s.Checks ?? []).Select(c => $"{{\"bucket\":\"{c.Bucket}\",\"name\":\"{c.Name}\",\"link\":\"https://github.com/o/r/actions/runs/{c.Name}\"}}"))}]";

    // QueueStateQuery's answer for the PR.
    private static string QueueAnswer(Scenario s) =>
        $"{{\"data\":{{\"repository\":{{\"pullRequest\":{{\"state\":\"{s.State}\",\"headRefOid\":\"abc123\",\"isMergeQueueEnabled\":{(s.QueueEnabled ? "true" : "false")},\"isInMergeQueue\":{(s.InQueue ? "true" : "false")},"
        + $"\"mergeQueueEntry\":{(s.Position is { } p ? $"{{\"position\":{p}}}" : "null")},\"timelineItems\":{{\"nodes\":[{(s.Removed is { } at ? $"{{\"__typename\":\"RemovedFromMergeQueueEvent\",\"createdAt\":\"{at}\",\"reason\":{Q(s.RemovedReason)}}}" : "")}]}}}}}}}}}}";

    private static OpenPullRequest One(Scenario s) => GhCli.ParseOpenByHead(Batch(s)).PullRequests.ShouldNotBeNull().ShouldHaveSingleItem();

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
