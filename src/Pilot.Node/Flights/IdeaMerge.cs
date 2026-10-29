using System.Text.Json;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>An idea's text as the issue holds it: the title and the body.</summary>
public sealed record IdeaText(string Title, string Body)
{
    /// <summary>The text as a person writes it: the title, then the body on the lines after.</summary>
    public string Joined => Body.Length > 0 ? Title + "\n" + Body : Title;

    /// <summary>Reads text as <see cref="Joined"/> gives it: the first line is the title, the rest the body.</summary>
    public static IdeaText Of(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return text.IndexOf('\n') is var cut and >= 0 ? new IdeaText(text[..cut].Trim(), text[(cut + 1)..].Trim()) : new IdeaText(text, "");
    }

    /// <summary>
    /// Like <see cref="Of"/> for text a person typed or pasted: an image in the title line is no part of the title, so the images
    /// of the lines up to the title go first in the body, and the title is the first line that has words left once they are out.
    /// </summary>
    public static IdeaText Typed(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var lines = text.Split('\n');
        var images = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var clean = line;
            var spans = IdeaImages.Spans(line);
            images.AddRange(spans.Select(s => s.Raw));
            foreach (var span in spans.OrderByDescending(s => s.At))
            {
                clean = clean.Remove(span.At, span.Length);
            }
            clean = spans.Length > 0 ? string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : clean.Trim();
            if (clean.Length == 0 && i < lines.Length - 1)
            {
                continue;
            }
            if (images.Count == 0)
            {
                return Of(text);
            }
            var rest = string.Join("\n", lines.Skip(i + 1)).Trim();
            var body = string.Join("\n", images);
            return new IdeaText(clean, rest.Length > 0 ? body + "\n" + rest : body);
        }
        return Of(text);
    }
}

/// <summary>
/// What combining two versions of an idea came to: one text, or <see cref="Contradicts"/> when the two say opposite things so that no
/// combining is right, in which case <see cref="Merged"/> is only the merge proposed.
/// </summary>
public sealed record IdeaMerge(IdeaText Merged, bool Contradicts);

/// <summary>Combines the person's edit of an idea with the version GitHub has since got. Never last-writer-wins.</summary>
public interface IIdeaMerger
{
    /// <exception cref="FlightException">The merge could not be had: nothing is to be written then.</exception>
    Task<IdeaMerge> MergeAsync(IdeaText mine, IdeaText github, CancellationToken cancellationToken);
}

/// <summary>The merge as a one-shot <c>claude -p</c> on a light model.</summary>
public sealed class ClaudeIdeaMerger : IIdeaMerger
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public const string Model = "haiku";

    public static string Prompt(IdeaText mine, IdeaText github) => $$"""
        Two people edited the same idea, a GitHub issue: the first version is the one being saved now, the second is what GitHub has meanwhile.
        Combine the two versions into one. Change only what combining needs: never reword, expand or restructure either version, and keep each person's words as they are.
        When the two say opposite things, so that no combination keeps both, the verdict is "contradicts" and the merge is your proposal.
        Answer with one JSON object and nothing else: {"verdict": "merged" or "contradicts", "title": "...", "body": "..."}.

        === VERSION BEING SAVED ===
        {{mine.Joined}}

        === VERSION ON GITHUB ===
        {{github.Joined}}
        """;

    public async Task<IdeaMerge> MergeAsync(IdeaText mine, IdeaText github, CancellationToken cancellationToken)
    {
        ClaudeRun run;
        try
        {
            run = await ClaudeCli.RunAsync(["-p", "--model", Model], Prompt(mine, github), Path.GetTempPath(), Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException)
        {
            throw new FlightException($"Claude could not merge the two versions of the idea, so nothing is saved: {ex.Message}");
        }
        return run.ExitCode == 0 && Read(run.Output) is { } merge
            ? merge
            : throw new FlightException($"Claude could not merge the two versions of the idea, so nothing is saved: {(run.ExitCode != 0 ? run.Error.Trim() : "it did not answer as asked")}");
    }

    /// <summary>The merge in Claude's answer: the JSON object in it, which may come in a fence; null when there is none or it is incomplete.</summary>
    public static IdeaMerge? Read(string answer)
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
            if (ClaudeAgentsJson.Text(root, "verdict") is not { } verdict || ClaudeAgentsJson.Text(root, "title") is not { Length: > 0 } title)
            {
                return null;
            }
            var contradicts = string.Equals(verdict, "contradicts", StringComparison.OrdinalIgnoreCase);
            return contradicts || string.Equals(verdict, "merged", StringComparison.OrdinalIgnoreCase)
                ? new IdeaMerge(new IdeaText(title.Trim(), (ClaudeAgentsJson.Text(root, "body") ?? "").Trim()), contradicts)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
