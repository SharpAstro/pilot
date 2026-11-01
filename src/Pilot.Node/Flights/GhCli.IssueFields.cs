using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>GitHub's Priority issue field, through GraphQL (<c>gh api graphql</c>): <c>issueFieldValues</c> to read, <c>setIssueFieldValue</c> to write.</summary>
public sealed partial class GhCli
{
    /// <summary>The field's name; an org's others (Effort, dates) are not read.</summary>
    public const string PriorityFieldName = "Priority";

    private const string PriorityQuery = """
        query($number: Int!) { repository(owner: "{owner}", name: "{repo}") {
          issueFields(first: 30) { nodes { __typename ... on IssueFieldSingleSelect { id name options { id name } } } }
          issue(number: $number) { id issueFieldValues(first: 30) { nodes { __typename ... on IssueFieldSingleSelectValue { name field { __typename ... on IssueFieldSingleSelect { id } } } } } }
        } }
        """;

    private sealed record PriorityShape(string IssueId, string? FieldId, Dictionary<IssuePriority, string> Options, IssuePriority? Level);

    private async Task<PriorityShape?> ReadPriorityShapeAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", "graphql", "-f", $"query={PriorityQuery}", "-F", $"number={Text(number)}"], repoPath, cancellationToken);
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
}
