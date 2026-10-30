using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>Claude's judgement of an issue: ready to become a flight or not, and the reasons for either.</summary>
public sealed record ReadinessAnswer(bool Ready, string[] Reasons);

/// <summary>Judges whether an issue or idea is ready to fly as a flight. Only on request, and it writes nothing to GitHub.</summary>
public interface IIssueReadinessChecker
{
    /// <exception cref="FlightException">No judgement could be had.</exception>
    Task<ReadinessAnswer> CheckAsync(IdeaText issue, string repoPath, CancellationToken cancellationToken);
}

/// <summary>The check as a one-shot <c>claude -p</c> run, given the issue's text. It may read the repo, which is its working folder.</summary>
public sealed class ClaudeIssueReadinessChecker : IIssueReadinessChecker
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public const string Model = "sonnet";

    public static string Prompt(IdeaText issue) => $$"""
        Judge whether this GitHub issue is ready to become a flight: one planned piece of work that is flown leg by leg. Nothing is changed: you only judge.
        It is ready when all of these hold:
        - it has one goal that fits a single leg (a few files, one reviewable change), not several goals or an open-ended theme;
        - the tests that would show it done can be named from the text;
        - no question is left open that someone must answer before work starts.
        Say, in a line each, what makes it ready, or which of these is missing and what would fix it.
        Answer with one JSON object and nothing else: {"ready": true or false, "reasons": ["...", "..."]}.

        === THE ISSUE ===
        {{issue.Joined}}
        """;

    public async Task<ReadinessAnswer> CheckAsync(IdeaText issue, string repoPath, CancellationToken cancellationToken)
    {
        ClaudeRun run;
        try
        {
            run = await ClaudeCli.RunAsync(["-p", "--model", Model], Prompt(issue), repoPath, Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException)
        {
            throw new FlightException($"Claude could not check the issue: {ex.Message}");
        }
        return run.ExitCode == 0 && Read(run.Output) is { } answer
            ? answer
            : throw new FlightException($"Claude could not check the issue: {(run.ExitCode != 0 ? run.Error.Trim() : "it did not answer with ready or not ready")}");
    }

    /// <summary>The judgement in Claude's answer: the JSON object in it, which may come in a fence. Null when there is none or it says nothing of ready.</summary>
    public static ReadinessAnswer? Read(string answer)
    {
        var from = answer.IndexOf('{');
        var to = answer.LastIndexOf('}');
        if (from < 0 || to <= from)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(answer[from..(to + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ready", out var ready) || ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }
            var reasons = root.TryGetProperty("reasons", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!.Trim()).Where(r => r.Length > 0).ToArray()
                : [];
            return new ReadinessAnswer(ready.GetBoolean(), reasons);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>An issue Claude weighs for a milestone: its number and text.</summary>
public sealed record SuggestItem(int Number, IdeaText Text);

/// <summary>Where Claude would put one issue: an open milestone by title or a new one by title (made by the node only once accepted), and why.</summary>
public sealed record SuggestedItem(int Number, string? Milestone, string? NewMilestone, string Reason);

/// <summary>A milestone Claude proposes to make: a title and one line of goal.</summary>
public sealed record SuggestedNew(string Title, string Description);

/// <summary>Claude's suggestion for a set of issues: one per issue it answered for, and the new milestones those name.</summary>
public sealed record SuggestAnswer(SuggestedItem[] Items, SuggestedNew[] New);

/// <summary>Suggests a milestone for each of a few issues at once, so what belongs together can share a new milestone. Writes nothing.</summary>
public interface IMilestoneSuggester
{
    /// <exception cref="FlightException">No suggestion could be had.</exception>
    Task<SuggestAnswer> SuggestAsync(SuggestItem[] items, MilestoneContext[] open, string repoPath, CancellationToken cancellationToken);
}

/// <summary>The suggestion as a one-shot <c>claude -p</c> run, given the issues and each open milestone with a few of its issues.</summary>
public sealed class ClaudeMilestoneSuggester : IMilestoneSuggester
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public const string Model = "sonnet";

    /// <summary>How many characters of an issue's body Claude is shown.</summary>
    public const int BodyShown = 1200;

    public static string Prompt(SuggestItem[] items, MilestoneContext[] open)
    {
        var issues = new StringBuilder();
        foreach (var (number, text) in items)
        {
            issues.Append("- #").Append(number.ToString(CultureInfo.InvariantCulture)).Append(": ").AppendLine(text.Title);
            if (text.Body.Trim().Length > 0)
            {
                var body = text.Body.Trim().ReplaceLineEndings(" ");
                issues.Append("    ").AppendLine(body.Length > BodyShown ? body[..BodyShown] + "..." : body);
            }
        }
        var listing = ClaudeIdeaMilestoner.Listing(open);
        return $$"""
            Decide which milestone of a GitHub repo each of these issues belongs in. Nothing is changed: you only suggest.
            - Choose one of the open milestones below when an issue fits its theme, by its exact title.
            - When none fits, suggest a new milestone: a short title and one line of goal. Issues that belong together share one new milestone: give it the same title for each.
            - Every issue gets an answer, with its reason in one line.
            Answer with one JSON object and nothing else:
            {"items": [{"issue": 12, "milestone": "<exact title>", "reason": "..."}, {"issue": 13, "newMilestone": "<new title>", "reason": "..."}], "newMilestones": [{"title": "<new title>", "description": "<one line goal>"}]}

            === THE ISSUES ===
            {{issues}}
            === THE OPEN MILESTONES ===
            {{(listing.Length > 0 ? listing : "(none)")}}
            """;
    }

    public async Task<SuggestAnswer> SuggestAsync(SuggestItem[] items, MilestoneContext[] open, string repoPath, CancellationToken cancellationToken)
    {
        ClaudeRun run;
        try
        {
            run = await ClaudeCli.RunAsync(["-p", "--model", Model], Prompt(items, open), repoPath, Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException)
        {
            throw new FlightException($"Claude could not suggest milestones: {ex.Message}");
        }
        return run.ExitCode == 0 && Read(run.Output, [.. items.Select(i => i.Number)], [.. open.Select(o => o.Milestone.Title)]) is { } answer
            ? answer
            : throw new FlightException($"Claude could not suggest milestones: {(run.ExitCode != 0 ? run.Error.Trim() : "it did not answer with a milestone for the issues")}");
    }

    /// <summary>
    /// The suggestion in Claude's answer: the JSON object in it, which may come in a fence. An item for an issue that was not asked about is
    /// dropped, one naming a milestone that is not among <paramref name="openTitles"/> keeps no milestone (so it is told apart from one that
    /// has none), and a new milestone that is an open one's title is that one. Null when there is no object or no item in it.
    /// </summary>
    public static SuggestAnswer? Read(string answer, IReadOnlyCollection<int> numbers, IReadOnlyCollection<string> openTitles)
    {
        var from = answer.IndexOf('{');
        var to = answer.LastIndexOf('}');
        if (from < 0 || to <= from)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(answer[from..(to + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var listed) || listed.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            string? Open(string title) => openTitles.FirstOrDefault(t => string.Equals(t, title.Trim(), StringComparison.OrdinalIgnoreCase));
            var described = new Dictionary<string, SuggestedNew>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("newMilestones", out var made) && made.ValueKind == JsonValueKind.Array)
            {
                foreach (var one in made.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
                {
                    if (ClaudeAgentsJson.Text(one, "title")?.Trim() is { Length: > 0 } title)
                    {
                        described[title] = new SuggestedNew(title, (ClaudeAgentsJson.Text(one, "description") ?? "").Trim());
                    }
                }
            }
            var items = new List<SuggestedItem>();
            var news = new Dictionary<string, SuggestedNew>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in listed.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                if (!item.TryGetProperty("issue", out var issue) || !issue.TryGetInt32(out var number) || !numbers.Contains(number) || items.Any(i => i.Number == number))
                {
                    continue;
                }
                var reason = (ClaudeAgentsJson.Text(item, "reason") ?? "").Trim();
                if (ClaudeAgentsJson.Text(item, "milestone") is { Length: > 0 } named)
                {
                    items.Add(new SuggestedItem(number, Open(named), null, reason));
                }
                else if (ClaudeAgentsJson.Text(item, "newMilestone")?.Trim() is { Length: > 0 } fresh)
                {
                    if (Open(fresh) is { } existing)
                    {
                        items.Add(new SuggestedItem(number, existing, null, reason));
                        continue;
                    }
                    if (!news.ContainsKey(fresh))
                    {
                        news[fresh] = described.TryGetValue(fresh, out var known) ? known : new SuggestedNew(fresh, "");
                    }
                    items.Add(new SuggestedItem(number, null, news[fresh].Title, reason));
                }
            }
            return items.Count > 0 ? new SuggestAnswer([.. items], [.. news.Values]) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
