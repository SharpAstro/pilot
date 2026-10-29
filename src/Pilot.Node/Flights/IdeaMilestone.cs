using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>An open milestone with the titles of a few of its open issues, which is what Claude weighs an idea against.</summary>
public sealed record MilestoneContext(Milestone Milestone, string[] IssueTitles);

/// <summary>Claude's proposal: an open milestone (<see cref="Title"/>) or a new one (<see cref="NewTitle"/>, <see cref="NewDescription"/>), and why.</summary>
public sealed record MilestoneAnswer(string? Title, string? NewTitle, string? NewDescription, string Reason);

/// <summary>Proposes the milestone an idea belongs in. Only on request, and it writes nothing: the person plans.</summary>
public interface IIdeaMilestoner
{
    /// <exception cref="FlightException">No proposal could be had.</exception>
    Task<MilestoneAnswer> ProposeAsync(IdeaText idea, MilestoneContext[] open, string repoPath, CancellationToken cancellationToken);
}

/// <summary>The proposal as a one-shot <c>claude -p</c> run, given the idea and each open milestone with a few of its issues.</summary>
public sealed class ClaudeIdeaMilestoner : IIdeaMilestoner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public const string Model = "sonnet";

    /// <summary>How many open issues of a milestone Claude is shown.</summary>
    public const int IssuesShown = 8;

    public static string Prompt(IdeaText idea, MilestoneContext[] open)
    {
        var text = new StringBuilder();
        foreach (var (milestone, issues) in open)
        {
            text.Append("- ").Append(milestone.Title);
            if (milestone.DueOn is { } due)
            {
                text.Append(" (due ").Append(due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(')');
            }
            text.Append(", ").Append(milestone.OpenIssues.ToString(CultureInfo.InvariantCulture)).Append(" open issues");
            if (milestone.Description.Trim().Length > 0)
            {
                text.Append(": ").Append(milestone.Description.Trim().ReplaceLineEndings(" "));
            }
            text.AppendLine();
            foreach (var title in issues)
            {
                text.Append("    * ").AppendLine(title);
            }
        }
        return $$"""
            Decide which milestone of a GitHub repo this idea belongs in. Nothing is changed: you only propose.
            - Choose one of the open milestones below when the idea fits its theme, by its exact title.
            - When none fits, suggest a new milestone: a short title and one line of description.
            - Give the reason in one line.
            Answer with one JSON object and nothing else: {"milestone": "<exact title>", "reason": "..."} or {"newMilestone": {"title": "...", "description": "..."}, "reason": "..."}.

            === THE IDEA ===
            {{idea.Joined}}

            === THE OPEN MILESTONES ===
            {{(text.Length > 0 ? text.ToString() : "(none)")}}
            """;
    }

    public async Task<MilestoneAnswer> ProposeAsync(IdeaText idea, MilestoneContext[] open, string repoPath, CancellationToken cancellationToken)
    {
        ClaudeRun run;
        try
        {
            run = await ClaudeCli.RunAsync(["-p", "--model", Model], Prompt(idea, open), repoPath, Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException)
        {
            throw new FlightException($"Claude could not propose a milestone: {ex.Message}");
        }
        return run.ExitCode == 0 && Read(run.Output, [.. open.Select(o => o.Milestone.Title)]) is { } answer
            ? answer
            : throw new FlightException($"Claude could not propose a milestone: {(run.ExitCode != 0 ? run.Error.Trim() : "it did not answer with an open milestone or a new one")}");
    }

    /// <summary>
    /// The proposal in Claude's answer: the JSON object in it, which may come in a fence. Null when there is none, or it names a milestone that
    /// is not among <paramref name="openTitles"/>, or suggests a new one with no title.
    /// </summary>
    public static MilestoneAnswer? Read(string answer, IReadOnlyCollection<string> openTitles)
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var reason = (ClaudeAgentsJson.Text(root, "reason") ?? "").Trim();
            if (ClaudeAgentsJson.Text(root, "milestone") is { Length: > 0 } named)
            {
                return openTitles.FirstOrDefault(t => string.Equals(t, named.Trim(), StringComparison.OrdinalIgnoreCase)) is { } open
                    ? new MilestoneAnswer(open, null, null, reason)
                    : null;
            }
            if (root.TryGetProperty("newMilestone", out var made) && made.ValueKind == JsonValueKind.Object
                && ClaudeAgentsJson.Text(made, "title") is { } title && title.Trim().Length > 0)
            {
                // A "new" one that is an open one's title is that one.
                var trimmed = title.Trim();
                return openTitles.FirstOrDefault(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase)) is { } existing
                    ? new MilestoneAnswer(existing, null, null, reason)
                    : new MilestoneAnswer(null, trimmed, (ClaudeAgentsJson.Text(made, "description") ?? "").Trim(), reason);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
