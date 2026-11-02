using System.Globalization;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>Splits one open issue or idea into distinct parts. Only on request, and it writes nothing: the person accepts the parts.</summary>
public interface IIssueSplitter
{
    /// <param name="issue">The issue's title and body as GitHub has them.</param>
    /// <param name="into">How many parts the person wishes for; null leaves it to Claude (at least two either way).</param>
    /// <param name="repoPath">The issue's repo checkout, where the splitting may look for related issues and PRs.</param>
    /// <returns>Two or more parts, the first of which stays on the original issue.</returns>
    /// <exception cref="FlightException">No split could be had.</exception>
    Task<BreakUpPart[]> SplitAsync(IdeaText issue, int? into, string repoPath, CancellationToken cancellationToken);
}

/// <summary>The split as a one-shot <c>claude -p</c> run in the issue's repo, so it can look at the code and for related issues with <c>gh</c>.</summary>
public sealed class ClaudeIssueSplitter(Attachments? attachments = null) : IIssueSplitter
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public const string Model = "sonnet";

    /// <param name="copies">The local files of the issue's images the node holds, which Claude may Read.</param>
    public static string Prompt(IdeaText issue, int? into, IReadOnlyList<string>? copies = null)
    {
        var count = into is { } n ? $"{n.ToString(CultureInfo.InvariantCulture)} distinct parts" : "two or more distinct parts (as many as it truly holds, usually two or three)";
        var images = IdeaImages.Spans(issue.Body).Length > 0
            ? "- The issue holds images, each a Markdown image line. Each goes verbatim in the part it belongs to (the first when unclear): never dropped or reworded.\n"
              + (copies is { Count: > 0 } ? "  The node holds local copies; Read the files to see what they show:\n" + string.Join('\n', copies.Select(c => $"  - {c}")) + "\n" : "")
            : "";
        return $$"""
            Break up this GitHub issue, in the repo you are in, into {{count}}.
            - Each part is one goal that stands alone and fits a single leg of work: a few files, one reviewable change. No part depends on another's wording.
            - Together the parts cover everything the issue asks for; nothing is dropped and nothing is invented.
            - Each part has a short one-line title and a short Markdown body (bullets, names in code spans). Keep the person's words where they are clear.
            - A question the text leaves open goes to the part it blocks, in that part's "open_questions".
            - You may look at the code and for related issues and PRs (read-only gh, e.g. gh issue list --search). Change nothing in the repo or on GitHub.
            {{images}}Answer with one JSON object and nothing else: {"parts": [{"title": "...", "body": "...", "open_questions": ["..."]}]}.

            === THE ISSUE ===
            {{issue.Joined}}
            """;
    }

    public async Task<BreakUpPart[]> SplitAsync(IdeaText issue, int? into, string repoPath, CancellationToken cancellationToken)
    {
        ClaudeRun run;
        var copies = IdeaImagePrompt.Copies(issue.Body, attachments);
        try
        {
            run = await ClaudeCli.RunAsync(IdeaImagePrompt.Args(Model, copies, attachments), Prompt(issue, into, copies), repoPath, Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException)
        {
            throw new FlightException($"Claude could not break the issue up: {ex.Message}");
        }
        return run.ExitCode == 0 && Read(run.Output) is { } parts
            ? KeepImages(issue.Body, parts)
            : throw new FlightException($"Claude could not break the issue up: {(run.ExitCode != 0 ? run.Error.Trim() : "it did not answer with two or more parts")}");
    }

    /// <summary>The parts in Claude's answer: the JSON object in it, which may come in a fence. Null when there is none or it has fewer than two parts, or a part has no title.</summary>
    public static BreakUpPart[]? Read(string answer)
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
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("parts", out var listed) || listed.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var parts = new List<BreakUpPart>();
            foreach (var part in listed.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || ClaudeAgentsJson.Text(part, "title")?.Trim() is not { Length: > 0 } title)
                {
                    return null;
                }
                var questions = part.TryGetProperty("open_questions", out var q) && q.ValueKind == JsonValueKind.Array
                    ? q.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(e => e.Length > 0).ToArray()
                    : [];
                parts.Add(new BreakUpPart(title, (ClaudeAgentsJson.Text(part, "body") ?? "").Trim(), questions));
            }
            return parts.Count >= 2 ? [.. parts] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The parts with every image of <paramref name="original"/> that none of them holds put back at the end of the first part.</summary>
    public static BreakUpPart[] KeepImages(string original, BreakUpPart[] parts)
    {
        var have = parts.SelectMany(p => IdeaImages.Spans(p.Body)).Select(s => s.Url).ToList();
        var lost = IdeaImages.Spans(original).Where(span => !have.Remove(span.Url)).Select(span => span.Raw).ToArray();
        return lost.Length == 0
            ? parts
            : [parts[0] with { Body = (parts[0].Body.Length > 0 ? parts[0].Body + "\n\n" : "") + string.Join("\n\n", lost) }, .. parts.Skip(1)];
    }
}
