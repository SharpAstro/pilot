using System.Globalization;
using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>Where a flight's work item lives. GitHub is first so a flight journaled before trackers existed reads as one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackerKind>))]
public enum TrackerKind
{
    [JsonStringEnumMemberName("github")]
    GitHub,

    [JsonStringEnumMemberName("shortcut")]
    Shortcut,
}

/// <summary>A work item: a tracker and the item's number there (an issue's, or a Shortcut story's).</summary>
public readonly record struct WorkItem(TrackerKind Tracker, int Id)
{
    /// <summary>The item as a person reads it: <c>#254</c> for an issue, <c>sc-101645</c> for a story.</summary>
    public string Display => Tracker == TrackerKind.Shortcut ? $"sc-{Id.ToString(CultureInfo.InvariantCulture)}" : $"#{Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// What a flight's branch carries after its code so the tracker links it: <c>-sc-101645</c> for a story (Shortcut's GitHub
    /// integration reads the story id from the branch name), nothing for an issue, which the PR body's Closes links.
    /// </summary>
    public string BranchSuffix => Tracker == TrackerKind.Shortcut ? "-" + Display : "";

    /// <summary>What the item is called in a sentence:<c>issue #254</c>, <c>story sc-101645</c>.</summary>
    public string Noun => Tracker == TrackerKind.Shortcut ? $"story {Display}" : $"issue {Display}";

    /// <summary>
    /// Reads what a create-flight was given: <c>sc-N</c> or <c>SC-N</c>, a Shortcut story URL
    /// (<c>https://app.shortcut.com/&lt;org&gt;/story/N[/slug]</c>), or a bare number (<c>254</c>, <c>#254</c>), which is a GitHub issue.
    /// Returns null, and why, for anything else.
    /// </summary>
    public static (WorkItem? Item, string? Error) Parse(string? text)
    {
        var given = (text ?? "").Trim();
        const string Shape = "a work item is a number (a GitHub issue), sc-N or a https://app.shortcut.com/<org>/story/N URL (a Shortcut story)";
        if (given.Length == 0)
        {
            return (null, Shape);
        }
        if (given.StartsWith("sc-", StringComparison.OrdinalIgnoreCase))
        {
            return Number(given[3..], TrackerKind.Shortcut) is { } story ? (story, null) : (null, $"{given} is not a Shortcut story: {Shape}");
        }
        if (Uri.TryCreate(given, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (!string.Equals(uri.Host, "app.shortcut.com", StringComparison.OrdinalIgnoreCase))
            {
                return (null, $"{uri.Host} is not a tracker pilot knows: {Shape}");
            }
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var at = Array.FindIndex(parts, p => string.Equals(p, "story", StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < parts.Length && Number(parts[at + 1], TrackerKind.Shortcut) is { } linked
                ? (linked, null)
                : (null, $"{given} names no Shortcut story: {Shape}");
        }
        return Number(given.TrimStart('#'), TrackerKind.GitHub) is { } issue ? (issue, null) : (null, $"{given} is not a work item: {Shape}");
    }

    private static WorkItem? Number(string text, TrackerKind tracker) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? new WorkItem(tracker, id) : null;
}
