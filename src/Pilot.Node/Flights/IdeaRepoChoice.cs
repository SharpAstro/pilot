using System.Text.Json;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>A repo the node tracks, as Claude is offered it when it chooses where an idea goes.</summary>
/// <param name="Type">What kind of project it is (<c>.NET</c>, <c>Node</c>); <c>unknown</c> when nothing says.</param>
public sealed record RepoOption(string Name, string Type, string Path);

/// <summary>Chooses the repo a new idea belongs in. It only chooses: the words are the person's and are never changed.</summary>
public interface IIdeaRepoChooser
{
    /// <summary>
    /// The repo of <paramref name="repos"/> that <paramref name="text"/> belongs in, by name; several when they fit equally and Claude cannot
    /// tell them apart; none when none fits. <paramref name="context"/> is the page or flight the person was on, when it is known.
    /// </summary>
    /// <exception cref="FlightException">Claude could not be asked or did not answer.</exception>
    Task<string[]> ChooseAsync(IdeaText text, IReadOnlyList<RepoOption> repos, string? context, CancellationToken cancellationToken);
}

/// <summary>What kind of project a checkout is, read from the files at its top and in its <c>src</c>.</summary>
public static class RepoTypes
{
    public static string Of(string path)
    {
        var kinds = new List<string>();
        foreach (var folder in new[] { path, System.IO.Path.Combine(path, "src") }.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder).Select(System.IO.Path.GetFileName).OfType<string>())
                {
                    var kind = file switch
                    {
                        _ when file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) => ".NET",
                        "package.json" => "Node",
                        "Cargo.toml" => "Rust",
                        "pyproject.toml" or "requirements.txt" => "Python",
                        "CMakeLists.txt" => "C/C++",
                        "go.mod" => "Go",
                        "action.yml" or "action.yaml" => "GitHub Action",
                        _ => null,
                    };
                    if (kind is not null && !kinds.Contains(kind))
                    {
                        kinds.Add(kind);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed says nothing about the type.
            }
        }
        return kinds.Count > 0 ? string.Join(", ", kinds) : "unknown";
    }
}

/// <summary>The choice as a one-shot <c>claude -p</c> on a light model.</summary>
public sealed class ClaudeIdeaRepoChooser(Attachments? attachments = null) : IIdeaRepoChooser
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public const string Model = "haiku";

    /// <param name="copies">The local files of the idea's images the node holds (at note time, the uploaded markers' copies), which Claude may Read.</param>
    public static string Prompt(IdeaText text, IReadOnlyList<RepoOption> repos, string? context, IReadOnlyList<string>? copies = null) => $$"""
        Choose the repo a new idea belongs in. Use the names, types and paths of the repos below, what the idea mentions, and the context the person was in.
        Answer with the one repo it belongs in. Only when two or more fit equally well and you cannot tell them apart, answer with those few, never more than four.
        Never guess: when none fits, answer with none. Never change the idea. Choose only among the repos listed, by their names exactly.
        {{IdeaImagePrompt.ForChoice(text.Body, copies ?? [])}}Answer with one JSON object and nothing else: {"repos": ["name", ...]}.

        === REPOS ===
        {{string.Join('\n', repos.Select(r => $"- {r.Name} ({r.Type}) {r.Path}"))}}

        === CONTEXT ===
        {{(string.IsNullOrWhiteSpace(context) ? "none given" : context.Trim())}}

        === THE IDEA ===
        {{text.Joined}}
        """;

    public async Task<string[]> ChooseAsync(IdeaText text, IReadOnlyList<RepoOption> repos, string? context, CancellationToken cancellationToken)
    {
        ClaudeRun run;
        var copies = IdeaImagePrompt.Copies(text.Body, attachments);
        try
        {
            run = await ClaudeCli.RunAsync(IdeaImagePrompt.Args(Model, copies, attachments), Prompt(text, repos, context, copies), System.IO.Path.GetTempPath(), Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException)
        {
            throw new FlightException($"Claude could not choose a repo for the idea, so nothing is filed: {ex.Message}");
        }
        return run.ExitCode == 0 && Read(run.Output, repos) is { } chosen
            ? chosen
            : throw new FlightException($"Claude could not choose a repo for the idea, so nothing is filed: {(run.ExitCode != 0 ? run.Error.Trim() : "it did not answer as asked")}");
    }

    /// <summary>
    /// The repos in Claude's answer: the JSON object in it, which may come in a fence, as <paramref name="repos"/> names them. A name that is
    /// no listed repo is dropped, never taken for another; null when there is no such object.
    /// </summary>
    public static string[]? Read(string answer, IReadOnlyList<RepoOption> repos)
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
            if (!doc.RootElement.TryGetProperty("repos", out var named) || named.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return [.. named.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => repos.FirstOrDefault(r => string.Equals(r.Name, n.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
