using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>GitHub's Priority issue field, through GraphQL (<c>gh api graphql</c>): <c>issueFieldValues</c> to read, <c>setIssueFieldValue</c> to write.</summary>
public sealed partial class GhCli
{
    /// <summary>The field's name; an org's others (Effort, dates) are not read.</summary>
    public const string PriorityFieldName = "Priority";

    internal const string PriorityQuery = """
        query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) {
          issueFields(first: 30) { nodes { __typename ... on IssueFieldSingleSelect { id name options { id name } } } }
          issue(number: $number) { id issueFieldValues(first: 30) { nodes { __typename ... on IssueFieldSingleSelectValue { name field { __typename ... on IssueFieldSingleSelect { id } } } } } }
        } }
        """;

    private sealed record PriorityShape(string IssueId, string? FieldId, Dictionary<IssuePriority, string> Options, IssuePriority? Level);

    private async Task<PriorityShape?> ReadPriorityShapeAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-F", $"number={Text(number)}", "-f", $"query={PriorityQuery}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object
                || !repo.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            string? fieldId = null;
            var options = new Dictionary<IssuePriority, string>();
            if (repo.TryGetProperty("issueFields", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("nodes", out var nodes))
            {
                foreach (var node in nodes.EnumerateArray().Where(n => TextOf(n, "__typename") == "IssueFieldSingleSelect" && TextOf(n, "name") == PriorityFieldName))
                {
                    fieldId = TextOf(node, "id");
                    foreach (var option in node.GetProperty("options").EnumerateArray())
                    {
                        if (IssuePriorities.TryParse(TextOf(option, "name"), out var level) && TextOf(option, "id") is { } optionId)
                        {
                            options[level] = optionId;
                        }
                    }
                }
            }
            IssuePriority? current = null;
            if (fieldId is not null && issue.TryGetProperty("issueFieldValues", out var values) && values.ValueKind == JsonValueKind.Object && values.TryGetProperty("nodes", out var valueNodes))
            {
                foreach (var node in valueNodes.EnumerateArray())
                {
                    if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.Object
                        && TextOf(field, "id") == fieldId && IssuePriorities.TryParse(TextOf(node, "name"), out var level))
                    {
                        current = level;
                    }
                }
            }
            return new PriorityShape(TextOf(issue, "id") ?? "", fieldId, options, current);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    internal const string OpenPrioritiesQuery = """
        query($owner: String!, $name: String!, $after: String) { repository(owner: $owner, name: $name) {
          issues(states: OPEN, first: 100, after: $after) { pageInfo { hasNextPage endCursor }
            nodes { number issueFieldValues(first: 30) { nodes { __typename ... on IssueFieldSingleSelectValue { name field { __typename ... on IssueFieldSingleSelect { name } } } } } } }
        } }
        """;

    /// <summary>
    /// The Priority of every open issue that has one, read a hundred issues to a call (not one call per issue). A repo or org without the
    /// field, or a token that cannot read it, gives nothing and no error: the rows simply show no priority.
    /// </summary>
    internal async Task<Dictionary<int, IssuePriority>> OpenPrioritiesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var found = new Dictionary<int, IssuePriority>();
        string? after = null;
        for (var page = 0; page < 10; page++)
        {
            List<string> args = ["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-f", $"query={OpenPrioritiesQuery}"];
            if (after is not null)
            {
                args.AddRange(["-f", $"after={after}"]);
            }
            var (exit, output, _) = await RunAsync([.. args], repoPath, cancellationToken);
            if (exit != 0)
            {
                return found;
            }
            try
            {
                using var doc = JsonDocument.Parse(output);
                if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object
                    || !repo.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Object)
                {
                    return found;
                }
                foreach (var issue in issues.GetProperty("nodes").EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object))
                {
                    if (!issue.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n)
                        || !issue.TryGetProperty("issueFieldValues", out var values) || values.ValueKind != JsonValueKind.Object || !values.TryGetProperty("nodes", out var valueNodes))
                    {
                        continue;
                    }
                    foreach (var node in valueNodes.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object))
                    {
                        if (node.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.Object && TextOf(field, "name") == PriorityFieldName
                            && IssuePriorities.TryParse(TextOf(node, "name"), out var level))
                        {
                            found[n] = level;
                        }
                    }
                }
                var info = issues.GetProperty("pageInfo");
                if (!info.TryGetProperty("hasNextPage", out var more) || more.ValueKind != JsonValueKind.True || TextOf(info, "endCursor") is not { } cursor)
                {
                    return found;
                }
                after = cursor;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return found;
            }
        }
        return found;
    }

    /// <summary><paramref name="issues"/> with the Priority each open one has on GitHub.</summary>
    private async Task<MilestoneIssue[]> WithPrioritiesAsync(string repoPath, MilestoneIssue[] issues, CancellationToken cancellationToken)
    {
        if (!issues.Any(i => i.Open))
        {
            return issues;
        }
        var levels = await OpenPrioritiesAsync(repoPath, cancellationToken);
        return levels.Count == 0 ? issues : [.. issues.Select(i => levels.TryGetValue(i.Number, out var level) && i.Open ? i with { Priority = level } : i)];
    }

    public async Task<PriorityRead> IssuePriorityAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // A repo without the field, or a token that cannot read it, is "no field": the label stays the source there.
        var shape = await ReadPriorityShapeAsync(repoPath, number, cancellationToken);
        return shape is { FieldId: not null } ? new PriorityRead(true, shape.Level) : PriorityRead.NoField;
    }

    public async Task SetIssuePriorityAsync(string repoPath, int number, IssuePriority? level, CancellationToken cancellationToken)
    {
        var shape = await ReadPriorityShapeAsync(repoPath, number, cancellationToken);
        if (shape is not { FieldId: { } fieldId } || shape.IssueId.Length == 0)
        {
            throw new FlightException($"#{number} has no {PriorityFieldName} field to set, or gh could not read it");
        }
        string input;
        if (level is { } set)
        {
            if (!shape.Options.TryGetValue(set, out var optionId))
            {
                throw new FlightException($"the {PriorityFieldName} field has no option {set}");
            }
            input = $"{{fieldId: \"{fieldId}\", singleSelectOptionId: \"{optionId}\"}}";
        }
        else
        {
            input = $"{{fieldId: \"{fieldId}\", delete: true}}";
        }
        var mutation = $"mutation {{ setIssueFieldValue(input: {{issueId: \"{shape.IssueId}\", issueFields: [{input}]}}) {{ clientMutationId }} }}";
        var (exit, output, error) = await RunAsync(["api", "graphql", "-f", $"query={mutation}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not set the {PriorityFieldName} of #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    internal const string OwnerFieldsQuery = """
        query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) {
          owner { __typename id }
          issueFields(first: 30) { nodes { __typename ... on IssueFieldSingleSelect { name } } }
        } }
        """;

    public async Task<bool> EnsurePriorityFieldAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-f", $"query={OwnerFieldsQuery}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not read the issue fields of the repo: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        string? ownerId;
        string? ownerType;
        try
        {
            using var doc = JsonDocument.Parse(output);
            var repo = doc.RootElement.GetProperty("data").GetProperty("repository");
            ownerId = TextOf(repo.GetProperty("owner"), "id");
            ownerType = TextOf(repo.GetProperty("owner"), "__typename");
            if (repo.TryGetProperty("issueFields", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("nodes", out var nodes)
                && nodes.EnumerateArray().Any(n => n.ValueKind == JsonValueKind.Object && string.Equals(TextOf(n, "name"), PriorityFieldName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FlightException("gh answered the issue fields of the repo with something unreadable");
        }
        if (ownerType != "Organization" || ownerId is null)
        {
            throw new FlightException($"the repo is owned by {(ownerType ?? "someone")}, not an organization, and only an organization has issue fields");
        }
        var options = string.Join(", ", new[] { ("Urgent", "RED"), ("High", "ORANGE"), ("Medium", "YELLOW"), ("Low", "GREEN") }
            .Select((o, i) => $"{{name: \"{o.Item1}\", color: {o.Item2}, priority: {i + 1}}}"));
        var mutation = $"mutation {{ createIssueField(input: {{ownerId: \"{ownerId}\", name: \"{PriorityFieldName}\", dataType: SINGLE_SELECT, options: [{options}]}}) {{ clientMutationId }} }}";
        var (made, madeOutput, madeError) = await RunAsync(["api", "graphql", "-f", $"query={mutation}"], repoPath, cancellationToken);
        if (made != 0 || madeOutput.Contains("\"errors\"", StringComparison.Ordinal))
        {
            throw new FlightException($"GitHub refused to make the {PriorityFieldName} field: {(madeError.Trim().Length > 0 ? madeError : madeOutput).Trim()}");
        }
        return true;
    }

    public async Task<string[]> RepoLabelsAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["label", "list", "--limit", "1000", "--json", "name", "--jq", ".[].name"], repoPath, cancellationToken);
        return exit != 0 ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task DeleteRepoLabelAsync(string repoPath, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["label", "delete", label, "--yes"], repoPath, cancellationToken);
        if (exit != 0 && !(error + output).Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"GitHub refused to delete the label {label}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<int> OpenIssuesWithLabelAsync(string repoPath, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "list", "--state", "open", "--label", label, "--limit", "1000", "--json", "number", "--jq", "length"], repoPath, cancellationToken);
        return exit == 0 && int.TryParse(output.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? count
            : throw new FlightException($"gh could not count the open issues with the label {label}: {(error.Trim().Length > 0 ? error : output).Trim()}");
    }
}
