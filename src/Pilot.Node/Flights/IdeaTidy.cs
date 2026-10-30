using System.Text.Json;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>Formats an idea as short Markdown and fills its obvious gaps. Only on request, and it writes nothing: the person saves the proposal.</summary>
public interface IIdeaTidier
{
    /// <param name="repoPath">The idea's repo checkout, where the tidying may look for related issues and PRs.</param>
    /// <exception cref="FlightException">No tidy could be had.</exception>
    Task<IdeaText> TidyAsync(IdeaText current, string repoPath, CancellationToken cancellationToken);
}

/// <summary>The tidy as a one-shot <c>claude -p</c> run in the idea's repo, so it can look for related issues and PRs with <c>gh</c>.</summary>
public sealed class ClaudeIdeaTidier(Attachments? attachments = null) : IIdeaTidier
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public const string Model = "sonnet";

    /// <param name="copies">The local files of the idea's images the node holds, which Claude may Read.</param>
    public static string Prompt(IdeaText current, IReadOnlyList<string>? copies = null) => $$"""
        Tidy this idea, a GitHub issue in the repo you are in. It is an idea, not a plan: keep it short.
        - The title is one short line.
        - The body is short Markdown bullets, from a few lines to about ten bullets, at most one heading level, no "Done when", no walls of text.
        - Put names (files, types, commands, verbs) in code spans.
        - Fill obvious gaps: what it touches, a related issue or PR you can find (read-only gh, e.g. gh issue list --search, gh pr list --search), and an open question as a question.
        - Keep the person's meaning and words where they are clear; do not invent requirements. Change nothing in the repo or on GitHub.
        {{IdeaImagePrompt.ForTidy(current.Body, copies ?? [])}}Answer with one JSON object and nothing else: {"title": "...", "body": "..."}.

        === THE IDEA ===
        {{current.Joined}}
        """;

    public async Task<IdeaText> TidyAsync(IdeaText current, string repoPath, CancellationToken cancellationToken)
    {
        ClaudeRun run;
        var copies = IdeaImagePrompt.Copies(current.Body, attachments);
        try
        {
            run = await ClaudeCli.RunAsync(IdeaImagePrompt.Args(Model, copies, attachments), Prompt(current, copies), repoPath, Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException)
        {
            throw new FlightException($"Claude could not tidy the idea: {ex.Message}");
        }
        return run.ExitCode == 0 && Read(run.Output) is { } tidy
            ? tidy
            : throw new FlightException($"Claude could not tidy the idea: {(run.ExitCode != 0 ? run.Error.Trim() : "it did not answer as asked")}");
    }

    /// <summary>The proposal in Claude's answer: the JSON object in it, which may come in a fence; null when there is none or it has no title.</summary>
    public static IdeaText? Read(string answer)
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
            return ClaudeAgentsJson.Text(doc.RootElement, "title") is { Length: > 0 } title
                ? new IdeaText(title.Trim(), (ClaudeAgentsJson.Text(doc.RootElement, "body") ?? "").Trim())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
