using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;

namespace Pilot.Node.Tests;

/// <summary>The JSON of one PR as the batch query and the single reads answer it, for <see cref="GhBatchParseTests"/>. Kept out of that class so the filter reader, which scans test source, has only tests to read there.</summary>
internal static class GhBatchFixtures
{
    internal const string Started = "2026-11-23T09:00:00Z";

    internal const string Ended = "2026-11-23T09:03:00Z";

    internal sealed record Check(string Name, string Status, string? Conclusion, string Bucket);

    internal sealed record Scenario(
        string State = "OPEN", bool Draft = false, string Merge = "MERGEABLE", string MergeStatus = "CLEAN", string? Review = null, bool Armed = false,
        bool QueueEnabled = true, bool InQueue = false, int? Position = null, string? Removed = null, string? RemovedReason = null, Check[]? Checks = null, string[]? Labels = null, int Number = 7);

    internal static Check Passed(string name = "build") => new(name, "COMPLETED", "SUCCESS", "pass");

    internal static Check Running(string name = "test") => new(name, "IN_PROGRESS", null, "pending");

    internal static Check Red(string name = "build") => new(name, "COMPLETED", "FAILURE", "fail");

    internal static string Q(string? text) => text is null ? "null" : $"\"{text}\"";

    // The PR as the batch query answers it, wrapped as the whole answer with the cost.
    internal static string Batch(Scenario s, int cost = 2)
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
    internal static string ViewPullRequest(Scenario s) =>
        $"{{\"number\":{s.Number},\"state\":\"{s.State}\",\"headRefOid\":\"abc123\",\"url\":\"https://github.com/o/r/pull/{s.Number}\",\"mergeable\":\"{s.Merge}\","
        + $"\"labels\":[{string.Join(",", (s.Labels ?? []).Select(l => $"{{\"name\":\"{l}\"}}"))}],\"mergeCommit\":null,\"mergedAt\":null,\"isDraft\":{(s.Draft ? "true" : "false")}}}";

    // gh pr view's MergeStateFields.
    internal static string ViewMergeState(Scenario s) =>
        $"{{\"state\":\"{s.State}\",\"isDraft\":{(s.Draft ? "true" : "false")},\"mergeStateStatus\":\"{s.MergeStatus}\",\"mergeable\":\"{s.Merge}\",\"reviewDecision\":{Q(s.Review)},"
        + $"\"autoMergeRequest\":{(s.Armed ? "{\"enabledAt\":\"2026-11-23T09:01:00Z\"}" : "null")},\"statusCheckRollup\":[{string.Join(",", (s.Checks ?? []).Select(c =>
            $"{{\"__typename\":\"CheckRun\",\"name\":\"{c.Name}\",\"workflowName\":\"CI\",\"status\":\"{c.Status}\",\"conclusion\":{Q(c.Conclusion)},\"detailsUrl\":\"https://github.com/o/r/actions/runs/{c.Name}\",\"startedAt\":\"{Started}\",\"completedAt\":{Q(c.Conclusion is null ? null : Ended)}}}"))}]}}";

    // gh pr checks --json bucket,name,link.
    internal static string ViewChecks(Scenario s) =>
        $"[{string.Join(",", (s.Checks ?? []).Select(c => $"{{\"bucket\":\"{c.Bucket}\",\"name\":\"{c.Name}\",\"link\":\"https://github.com/o/r/actions/runs/{c.Name}\"}}"))}]";

    // QueueStateQuery's answer for the PR.
    internal static string QueueAnswer(Scenario s) =>
        $"{{\"data\":{{\"repository\":{{\"pullRequest\":{{\"state\":\"{s.State}\",\"headRefOid\":\"abc123\",\"isMergeQueueEnabled\":{(s.QueueEnabled ? "true" : "false")},\"isInMergeQueue\":{(s.InQueue ? "true" : "false")},"
        + $"\"mergeQueueEntry\":{(s.Position is { } p ? $"{{\"position\":{p}}}" : "null")},\"timelineItems\":{{\"nodes\":[{(s.Removed is { } at ? $"{{\"__typename\":\"RemovedFromMergeQueueEvent\",\"createdAt\":\"{at}\",\"reason\":{Q(s.RemovedReason)}}}" : "")}]}}}}}}}}}}";

    internal static OpenPullRequest One(Scenario s) => GhCli.ParseOpenByHead(Batch(s)).PullRequests.ShouldNotBeNull().ShouldHaveSingleItem();
}
