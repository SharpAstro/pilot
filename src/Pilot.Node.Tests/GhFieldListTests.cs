using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A recording's keys against the <c>--json</c> list its reader passes to gh (pilot #1106): gh answers exactly the fields asked for, so a
/// field added to the list and not to the parser (or the other way) shows here as a unit test, not at run time. No gh runs.
/// </summary>
public sealed class GhFieldListTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh");

    /// <summary>Every method that reads <c>gh ... --json</c>: its folder under Fixtures/gh, its field list, and extra recordings kept beside the folders.</summary>
    public static TheoryData<string, string, string> Readers => new()
    {
        { "pull-request-by-branch", GhCli.PullRequestByBranchFields, "" },
        { "pull-request-by-number", GhCli.PullRequestByNumberFields, "" },
        { "merged-pull-requests", GhCli.MergedPullRequestFields, "" },
        { "open-pull-requests", GhCli.OpenPullRequestFields, "" },
        { "workflow-runs", GhCli.WorkflowRunFields, "" },
        { "checks", GhCli.ChecksFields, "" },
        // The earlier recordings sit directly in gh/ (MergeStateTests): every one of them but the merge queue's.
        { "merge-state", GhCli.MergeStateFields, "*.json" },
        { "auto-merge-armed", GhCli.AutoMergeArmedFields, "" },
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryRecordingHasExactlyTheFieldsItsReaderAsksFor(string folder, string fields, string alsoInRoot)
    {
        var files = Directory.GetFiles(Path.Combine(Root, folder), "*.json").AsEnumerable();
        if (alsoInRoot.Length > 0)
        {
            files = files.Concat(Directory.GetFiles(Root, alsoInRoot).Where(f => !Path.GetFileName(f).StartsWith("queue-", StringComparison.Ordinal)));
        }
        var recordings = files.Order().ToArray();
        recordings.ShouldNotBeEmpty($"no recording of {folder}");
        var differences = recordings
            .Select(f => (File: Path.GetRelativePath(Root, f), Difference: Compare(File.ReadAllText(f), fields)))
            .Where(r => r.Difference is not null)
            .Select(r => $"{r.File}: {r.Difference}")
            .ToArray();
        differences.ShouldBeEmpty();
    }

    [Fact]
    public void TheMergeQueueRecordingsAndItsQueryAgreeOnTheFieldsInBothDirections()
    {
        var queryFields = QueryFields(GhCli.QueueStateQuery);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Root, "queue-*.json").Order())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            CollectKeys(doc.RootElement, keys);
            // data, repository and pullRequest are the answer's wrapping, which the query writes as its own structure.
            keys.ExceptWith(["data", "repository", "pullRequest"]);
            keys.Except(queryFields).ShouldBeEmpty($"{Path.GetFileName(file)} has keys the query does not select");
            seen.UnionWith(keys);
        }
        // An idle PR has no queue position, so the query's fields are held against all the recordings together.
        queryFields.Except(seen).ShouldBeEmpty("the query selects fields no recording shows");
    }

    [Fact]
    public void AKeyTheListLacksAndAFieldTheRecordingLacksAreBothReported()
    {
        const string recording = """[{"number":1,"title":"t","extra":true}]""";

        // The recording has a key the list does not name.
        Compare(recording, "number,title").ShouldNotBeNull().ShouldContain("extra");
        // The list names a field the recording does not have.
        Compare(recording, "number,title,extra,url").ShouldNotBeNull().ShouldContain("url");
        // Both at once, and neither passes for the other.
        var both = Compare("""{"a":1,"b":2}""", "b,c").ShouldNotBeNull();
        both.ShouldContain("a");
        both.ShouldContain("c");
        // They agree, in any order, for a list and for one object.
        Compare(recording, "extra,title,number").ShouldBeNull();
        Compare("""{"b":2,"a":1}""", "a,b").ShouldBeNull();
        // An empty answer says nothing about the fields, so it is not a difference.
        Compare("[]", "number,title").ShouldBeNull();
    }

    /// <summary>
    /// How a recording and a <c>--json</c> list differ, or null when they agree. The keys are those of the answer's object, or of its first
    /// element for an array; an empty array has none to compare.
    /// </summary>
    internal static string? Compare(string recording, string fields)
    {
        using var doc = JsonDocument.Parse(recording);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
            {
                return null;
            }
            root = root[0];
        }
        var keys = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var listed = fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var unlisted = keys.Except(listed).Order().ToArray();
        var absent = listed.Except(keys).Order().ToArray();
        if (unlisted.Length == 0 && absent.Length == 0)
        {
            return null;
        }
        return (unlisted.Length > 0 ? $"the recording has {string.Join(", ", unlisted)}, which the field list lacks" : "")
            + (unlisted.Length > 0 && absent.Length > 0 ? "; " : "")
            + (absent.Length > 0 ? $"the field list has {string.Join(", ", absent)}, which the recording lacks" : "");
    }

    private static void CollectKeys(JsonElement element, HashSet<string> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    keys.Add(property.Name);
                    CollectKeys(property.Value, keys);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectKeys(item, keys);
                }
                break;
        }
    }

    /// <summary>The field names a GraphQL query selects: its words, less the operation, the variables, the arguments and the type conditions.</summary>
    private static HashSet<string> QueryFields(string query)
    {
        var withoutHead = query[query.IndexOf("){", StringComparison.Ordinal)..];
        var withoutArguments = Regex.Replace(withoutHead, @"\([^)]*\)", "");
        var withoutTypes = Regex.Replace(withoutArguments, @"\.\.\.\s*on\s+\w+", "");
        var words = Regex.Matches(withoutTypes, @"[A-Za-z_]\w*").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        words.ExceptWith(["repository", "pullRequest"]);
        return words;
    }
}
