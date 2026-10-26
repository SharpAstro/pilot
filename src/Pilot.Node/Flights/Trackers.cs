using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// One tracker a flight's work item can live in. A further tracker is one more of these, added to <see cref="Trackers"/>:
/// it says how an item's title is read, and what is missing when it cannot be.
/// </summary>
public interface ITracker
{
    TrackerKind Kind { get; }

    /// <summary>The item's title, or null when it cannot be read (no access, no such item).</summary>
    Task<string?> TitleAsync(string repoPath, int id, CancellationToken cancellationToken);

    /// <summary>What a flight made with no title is refused with when <see cref="TitleAsync"/> found none; null where a title is optional.</summary>
    string? MissingTitle(WorkItem item);
}

/// <summary>How a brief tells a session to read a work item in one tracker. Written once per tracker, so Briefs holds no tracker branches.</summary>
public interface ITrackerReading
{
    /// <summary>The item's noun in a sentence: issue, story.</summary>
    string Noun { get; }

    /// <summary>How to read item <paramref name="id"/> with its description, comments and tasks; fits inside parentheses.</summary>
    string Read(int id);

    /// <summary>How to read another flight's item, whose tracker is that of its <c>status</c> display (#N or sc-N), in <c>repo</c>.</summary>
    string ReadOther { get; }

    /// <summary>One line telling a reviewer or validator where the item lives and how to read it; null where the brief says nothing of it.</summary>
    string? Reminder(WorkItem item);
}

public sealed class GitHubReading : ITrackerReading
{
    public string Noun => "issue";

    public string Read(int id) => $"`gh issue view {id} --comments`";

    public string ReadOther => "`gh issue view <number> -R <repo>`";

    public string? Reminder(WorkItem item) => null;
}

public sealed class ShortcutReading : ITrackerReading
{
    public string Noun => "story";

    public string Read(int id) =>
        $"the Shortcut MCP tool `stories-get-by-id` with id {id} and full detail when this session has it, else " +
        $"`curl -s -H \"Shortcut-Token: ${ShortcutTracker.TokenVariable}\" {ShortcutTracker.Endpoint}{id}`; read its description, comments and tasks";

    public string ReadOther =>
        "a Shortcut story (shown as sc-N) with `stories-get-by-id` (full detail) or the curl above on its number, a GitHub issue (shown as #N) " +
        "with `gh api repos/<owner>/<repo>/issues/<number>` and its `/comments`";

    public string? Reminder(WorkItem item) =>
        $"The flight's work item is Shortcut story {item.Display}: read it ({Read(item.Id)}), not a GitHub issue.";
}

/// <summary>The trackers pilot knows, by kind.</summary>
public sealed class Trackers(IEnumerable<ITracker> all)
{
    private static readonly GitHubReading GitHubWords = new();

    private static readonly ShortcutReading ShortcutWords = new();

    /// <summary>The brief wording for a tracker's items.</summary>
    public static ITrackerReading Reading(TrackerKind kind) => kind == TrackerKind.Shortcut ? ShortcutWords : GitHubWords;

    private readonly Dictionary<TrackerKind, ITracker> _byKind = all.ToDictionary(t => t.Kind);

    public ITracker? Of(TrackerKind kind) => _byKind.GetValueOrDefault(kind);
}

/// <summary>GitHub issues, through <c>gh</c>. A flight on an issue needs no title: it is made untitled when none is read.</summary>
public sealed class GitHubTracker(IGitHub github) : ITracker
{
    public TrackerKind Kind => TrackerKind.GitHub;

    public Task<string?> TitleAsync(string repoPath, int id, CancellationToken cancellationToken) => github.IssueTitleAsync(repoPath, id, cancellationToken);

    public string? MissingTitle(WorkItem item) => null;
}

/// <summary>
/// Shortcut stories, through the REST API (<c>GET /api/v3/stories/&lt;id&gt;</c>, the <c>Shortcut-Token</c> header). The token
/// is <c>SHORTCUT_API_TOKEN</c> or the node's <c>shortcut-token</c> setting, read at each call so a change is in force at once.
/// </summary>
public sealed class ShortcutTracker(HttpClient http, Func<string?> token) : ITracker
{
    public const string TokenVariable = "SHORTCUT_API_TOKEN";

    public const string Endpoint = "https://api.app.shortcut.com/api/v3/stories/";

    public TrackerKind Kind => TrackerKind.Shortcut;

    /// <summary>Whether a token is set, in the environment or in <paramref name="saved"/>.</summary>
    public static string? TokenFrom(string? saved) =>
        Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } env ? env.Trim() : saved is { Length: > 0 } ? saved.Trim() : null;

    public async Task<string?> TitleAsync(string repoPath, int id, CancellationToken cancellationToken)
    {
        if (token() is not { Length: > 0 } secret)
        {
            return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Shortcut-Token", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public string? MissingTitle(WorkItem item) =>
        token() is { Length: > 0 }
            ? $"Shortcut did not give a title for {item.Display}: check the story exists and the token may read it, or give a title as in create-flight repo/<path> {item.Display} <title>"
            : $"{item.Display} is a Shortcut story, and there is neither a token to read its title ({TokenVariable}, or pilot settings set shortcut-token) nor a title given: give a title as in create-flight repo/<path> {item.Display} <title>";
}
