using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

public sealed partial class GhCli
{
    /// <summary>How gh is run for a merge call; null is the real gh. Tests script it.</summary>
    internal Func<IReadOnlyList<string>, string, CancellationToken, Task<(int Exit, string Output, string Error)>>? Runner { get; init; }

    /// <summary>The wait before a transient answer is retried; tests make it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    private const int MergeAttempts = 4;

    private async Task<(int Exit, string Output, string Error)> GhAsync(IReadOnlyList<string> args, string repoPath, CancellationToken cancellationToken)
    {
        if (Runner is { } run)
        {
            return await run(args, repoPath, cancellationToken);
        }
        try
        {
            return await RunAsync(args, repoPath, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // gh's own time budget ran out: a timeout the table treats as transient.
            return (-1, "", "gh timed out");
        }
    }

    // One merge-shaped call, sorted by MergeAnswers: a transient answer is asked again (3 retries after 1 s, 2 s, 4 s), anything
    // else comes back for the caller to act on.
    private async Task<MergeAnswer> AnswerAsync(IReadOnlyList<string> args, string repoPath, CancellationToken cancellationToken, bool leavingQueue = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (exit, output, error) = await GhAsync(args, repoPath, cancellationToken);
            var answer = MergeAnswers.Classify(exit, output, error, leavingQueue);
            if (answer.Kind != MergeAnswerKind.Transient || attempt >= MergeAttempts)
            {
                return answer;
            }
            await Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), cancellationToken);
        }
    }

    private static void Require(MergeAnswer answer, string what)
    {
        if (answer.Kind != MergeAnswerKind.Done)
        {
            throw new MergeRefusedException(answer.Kind, answer.Message, $"gh could not {what}: {answer.Message}");
        }
    }

    public async Task<bool> MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        // gh pr merge reaches a merge queue only through auto-merge, which the repos keep off: "Auto merge is not allowed for
        // this repository" (pilot#212, 2026-10-24). The queue is joined by the API itself, as its web page's button does.
        if (await QueuedPullRequestAsync(repoPath, number, cancellationToken) is { } id)
        {
            await EnqueueAsync(repoPath, number, id, head, cancellationToken);
            return true;
        }
        var answer = await AnswerAsync(
            ["pr", "merge", number.ToString(System.Globalization.CultureInfo.InvariantCulture), $"--{method}", "--match-head-commit", head], repoPath, cancellationToken);
        Require(answer, $"merge #{number}");
        return false;
    }

    private static readonly TimeSpan AutoMergeAllowedFor = TimeSpan.FromMinutes(10);

    // Per repo folder: what GitHub said and when, so a Merge pressed twice does not ask twice.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Allowed, DateTimeOffset At)> _autoMerge = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> AutoMergeAllowedAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (_autoMerge.TryGetValue(repoPath, out var known) && DateTimeOffset.UtcNow - known.At < AutoMergeAllowedFor)
        {
            return known.Allowed;
        }
        var (exit, output, _) = await RunAsync(["api", "repos/{owner}/{repo}", "--jq", ".allow_auto_merge"], repoPath, cancellationToken);
        var allowed = exit == 0 && ParseAllowAutoMerge(output);
        // A call that failed is no answer: not kept, so the next Merge asks again.
        if (exit == 0)
        {
            _autoMerge[repoPath] = (allowed, DateTimeOffset.UtcNow);
        }
        return allowed;
    }

    /// <summary><c>gh api repos/{owner}/{repo} --jq .allow_auto_merge</c>'s output: true only for <c>true</c>; <c>false</c>, <c>null</c> (a token that cannot see the setting) and nothing are off.</summary>
    internal static bool ParseAllowAutoMerge(string output) => output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (AllowedMergeMethods Methods, DateTimeOffset At)> _methods = new(StringComparer.OrdinalIgnoreCase);

    public async Task<AllowedMergeMethods?> AllowedMergeMethodsAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (_methods.TryGetValue(repoPath, out var known) && DateTimeOffset.UtcNow - known.At < AutoMergeAllowedFor)
        {
            return known.Methods;
        }
        var (exit, output, _) = await GhAsync(["api", "repos/{owner}/{repo}"], repoPath, cancellationToken);
        var methods = exit == 0 ? AllowedMergeMethods.Parse(output) : null;
        // A call that failed is no answer: not kept.
        if (methods is not null)
        {
            _methods[repoPath] = (methods, DateTimeOffset.UtcNow);
        }
        return methods;
    }

    // Not kept: it is asked only when a merge batch would form, and a rule added since must stop the next batch's push.
    public async Task<bool> BranchUnprotectedAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/branches/{branch}", "--jq", ".protected"], repoPath, cancellationToken);
        if (exit != 0 || !output.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var (rulesExit, rules, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/rules/branches/{branch}"], repoPath, cancellationToken);
        return rulesExit == 0 && ParseNoRules(rules);
    }

    /// <summary><c>gh api repos/{owner}/{repo}/rules/branches/{branch}</c>'s output: true only for an empty JSON array; any rule, or no JSON, is a rule.</summary>
    internal static bool ParseNoRules(string rulesJson)
    {
        try
        {
            using var rules = JsonDocument.Parse(rulesJson);
            return rules.RootElement is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<string?> MergeQueueMethodAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, rules, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/rules/branches/{branch}"], repoPath, cancellationToken);
        return exit == 0 ? ParseMergeQueueMethod(rules) : null;
    }

    /// <summary><c>gh api repos/{owner}/{repo}/rules/branches/{branch}</c>'s output: the <c>merge_queue</c> rule's <c>parameters.merge_method</c>, upper-cased; null with no such rule, no method, or no JSON.</summary>
    internal static string? ParseMergeQueueMethod(string rulesJson)
    {
        try
        {
            using var rules = JsonDocument.Parse(rulesJson);
            if (rules.RootElement is not { ValueKind: JsonValueKind.Array } array)
            {
                return null;
            }
            foreach (var rule in array.EnumerateArray())
            {
                if (rule.ValueKind == JsonValueKind.Object && TextOf(rule, "type") == "merge_queue"
                    && rule.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                    && TextOf(parameters, "merge_method") is { Length: > 0 } method)
                {
                    return method.ToUpperInvariant();
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>gh api repos/{owner}/{repo}</c>'s <c>default_branch</c>; null when it is no JSON or has none.</summary>
    internal static string? ParseDefaultBranch(string repoJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(repoJson);
            return TextOf(doc.RootElement, "default_branch");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Not `gh pr merge --auto`: gh merges at once, arming nothing, when GitHub calls the PR immediately mergeable (CLEAN, HAS_HOOKS
    // or UNSTABLE: running checks that are not required leave it so). enablePullRequestAutoMerge refuses such a PR instead, and
    // the state is read first so a refusal is said in the words GitHub uses for it.
    public async Task ArmAutoMergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        var numberText = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (exit, output, error) = await RunAsync(["pr", "view", numberText, "--json", "id,mergeStateStatus"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not read #{number} to arm auto-merge: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        var (id, status) = ParseArmable(output);
        if (id is null)
        {
            throw new FlightException($"gh could not read #{number}'s id to arm auto-merge");
        }
        if (ImmediatelyMergeable(status))
        {
            throw new FlightException($"GitHub will not arm auto-merge on #{number}: it is in {status?.ToLowerInvariant()} status, so it has no required check to wait for");
        }
        var armed = await AnswerAsync(
            ["api", "graphql", "-f", $"query={EnableAutoMergeMutation}", "-f", $"id={id}", "-f", $"method={method.ToUpperInvariant()}", "-f", $"head={head}"], repoPath, cancellationToken);
        Require(armed, $"arm auto-merge on #{number}");
    }

    private const string EnableAutoMergeMutation =
        "mutation($id: ID!, $method: PullRequestMergeMethod!, $head: GitObjectID!) { enablePullRequestAutoMerge(input: {pullRequestId: $id, mergeMethod: $method, expectedHeadOid: $head}) { pullRequest { number } } }";

    /// <summary>Whether <c>mergeStateStatus</c> says GitHub would merge the PR this moment (what <c>gh pr merge --auto</c> then does, instead of arming).</summary>
    internal static bool ImmediatelyMergeable(string? mergeStateStatus) =>
        mergeStateStatus is not null && mergeStateStatus.ToUpperInvariant() is "CLEAN" or "HAS_HOOKS" or "UNSTABLE";

    /// <summary><c>gh pr view --json id,mergeStateStatus</c>'s answer: the PR's node id and its merge state status; nulls when it is no JSON.</summary>
    internal static (string? Id, string? Status) ParseArmable(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string? Read(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Read("id"), Read("mergeStateStatus"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public async Task<bool?> AutoMergeArmedAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "autoMergeRequest"], repoPath, cancellationToken);
        return exit == 0 ? ParseAutoMergeArmed(output) : null;
    }

    /// <summary><c>gh pr view --json autoMergeRequest</c>'s answer: true with a request on the PR, false with <c>null</c>; null when it is no JSON.</summary>
    internal static bool? ParseAutoMergeArmed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("autoMergeRequest", out var auto) ? auto.ValueKind == JsonValueKind.Object : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task DisarmAutoMergeAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "merge", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--disable-auto"], repoPath, cancellationToken);
        // Not armed (or merged, or closed) already is the state wanted.
        if (exit != 0 && !(error + output).Contains("not enabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"gh could not turn auto-merge off on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    private const string QueueStateQuery ="query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){"
        + "state headRefOid isMergeQueueEnabled isInMergeQueue mergeQueueEntry{position} "
        + "timelineItems(last:10,itemTypes:[ADDED_TO_MERGE_QUEUE_EVENT,REMOVED_FROM_MERGE_QUEUE_EVENT]){nodes{__typename ... on AddedToMergeQueueEvent{createdAt} ... on RemovedFromMergeQueueEvent{createdAt reason}}}}}}";

    public async Task<MergeQueueState?> MergeQueueAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(
            ["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-F", $"number={number.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-f", $"query={QueueStateQuery}"],
            repoPath, cancellationToken);
        return exit == 0 ? ParseMergeQueue(output) : null;
    }

    /// <summary><see cref="QueueStateQuery"/>'s answer as a <see cref="MergeQueueState"/>; null when the base has no queue or the answer says nothing.</summary>
    internal static MergeQueueState? ParseMergeQueue(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!(doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                && repo.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object
                && pr.TryGetProperty("isMergeQueueEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True))
            {
                return null;
            }
            var queued = pr.TryGetProperty("isInMergeQueue", out var inQueue) && inQueue.ValueKind == JsonValueKind.True;
            int? position = pr.TryGetProperty("mergeQueueEntry", out var entry) && entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("position", out var at) && at.TryGetInt32(out var p) ? p : null;
            // The last event says whether it left the queue after it last joined.
            DateTimeOffset? removedAt = null;
            string? reason = null;
            if (pr.TryGetProperty("timelineItems", out var items) && items.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                && nodes.GetArrayLength() > 0 && nodes[nodes.GetArrayLength() - 1] is var last
                && TextOf(last, "__typename") == "RemovedFromMergeQueueEvent")
            {
                removedAt = TextOf(last, "createdAt") is { } stamp && DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when) ? when : null;
                reason = TextOf(last, "reason");
            }
            return new MergeQueueState(TextOf(pr, "state") ?? "", TextOf(pr, "headRefOid") ?? "", queued, position, removedAt, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TextOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private const string QueueQuery = "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){id isMergeQueueEnabled}}}";

    private const string EnqueueMutation = "mutation($id:ID!,$head:GitObjectID!){enqueuePullRequest(input:{pullRequestId:$id,expectedHeadOid:$head}){mergeQueueEntry{position}}}";

    // The PR's node id when its base has a merge queue, else null; a GitHub that will not say is no queue, and gh pr merge
    // then says what is wrong.
    private async Task<string?> QueuedPullRequestAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await GhAsync(
            ["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-F", $"number={number.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-f", $"query={QueueQuery}"],
            repoPath, cancellationToken);
        return exit == 0 ? QueuedPullRequest(output) : null;
    }

    /// <summary>The PR's node id from <see cref="QueueQuery"/>'s answer when its base has a merge queue, else null.</summary>
    internal static string? QueuedPullRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                && repo.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object
                && pr.TryGetProperty("isMergeQueueEnabled", out var queue) && queue.ValueKind == JsonValueKind.True
                && pr.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } nodeId
                ? nodeId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Joins the queue, refused by GitHub unless the PR's head is still the one reviewed (expectedHeadOid, as
    // --match-head-commit is for gh pr merge).
    private async Task EnqueueAsync(string repoPath, int number, string id, string head, CancellationToken cancellationToken) =>
        Require(await AnswerAsync(["api", "graphql", "-f", $"id={id}", "-f", $"head={head}", "-f", $"query={EnqueueMutation}"], repoPath, cancellationToken), $"queue #{number} to merge");

    private const string DequeueMutation = "mutation($id:ID!){dequeuePullRequest(input:{id:$id}){mergeQueueEntry{id}}}";

    public async Task DequeueAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // The PR's node id is found as joining the queue finds it; a base with no queue has nothing to leave.
        if (await QueuedPullRequestAsync(repoPath, number, cancellationToken) is not { } id)
        {
            return;
        }
        // Not queued (or merged, or closed) already is the state wanted: the table's leave-the-queue row, read for this call only.
        Require(await AnswerAsync(["api", "graphql", "-f", $"id={id}", "-f", $"query={DequeueMutation}"], repoPath, cancellationToken, leavingQueue: true), $"take #{number} out of the merge queue");
    }

    public async Task SetDraftAsync(string repoPath, int number, bool draft, CancellationToken cancellationToken)
    {
        var args = new List<string> { "pr", "ready", number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (draft)
        {
            args.Add("--undo");
        }
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not mark #{number} {(draft ? "a draft" : "ready for review")}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }
}
