using System.Globalization;
using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>Where an idea is: kept, taken up as a flight, or turned down.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdeaState>))]
public enum IdeaState
{
    /// <summary>Open in the repo's Ideas milestone, waiting for someone to plan it.</summary>
    [JsonStringEnumMemberName("noted")]
    Noted,

    /// <summary>A flight flies its issue (it is still in the Ideas milestone only until the move to a real one lands).</summary>
    [JsonStringEnumMemberName("planned")]
    Planned,

    /// <summary>Closed as not planned, with the reason as a comment.</summary>
    [JsonStringEnumMemberName("dropped")]
    Dropped,
}

/// <summary>
/// An idea: a GitHub issue in a repo's <see cref="IdeaIds.Milestone"/> milestone, id <c>I&lt;issue number&gt;</c>. The node keeps one
/// row for each, read as it reads issues, and carries them in its snapshot.
/// </summary>
public sealed record IdeaRow
{
    /// <summary><c>I381</c>: the issue's number, unique within its <see cref="Repo"/>.</summary>
    public required string Id { get; init; }

    /// <summary>The repo's folder name (<c>pilot</c>).</summary>
    public required string Repo { get; init; }

    /// <summary>The issue's title: the first line of what was noted.</summary>
    public required string Title { get; init; }

    /// <summary>The rest of what was noted; empty when it was one line.</summary>
    public string Body { get; init => field = value ?? ""; } = "";

    public required IdeaState State { get; init; }

    /// <summary>When the issue last changed on GitHub.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>How long since it was noted, as the node read it (<c>3d</c>, <c>5h</c>, <c>12m</c>).</summary>
    public string Age { get; init => field = value ?? ""; } = "";

    /// <summary>Who noted it: the issue's author on GitHub.</summary>
    public string Origin { get; init => field = value ?? ""; } = "";

    /// <summary>The issue's page.</summary>
    public string Url { get; init => field = value ?? ""; } = "";

    /// <summary>The issue's number, from <see cref="Id"/>; 0 when it reads as none.</summary>
    [JsonIgnore]
    public int Number => IdeaIds.TryNumber(Id, out var number) ? number : 0;

    /// <summary>The id as it is given for another repo: <c>pilot I381</c>.</summary>
    [JsonIgnore]
    public string Qualified => $"{Repo} {Id}";
}

/// <summary>An idea moved to another repo (a GitHub transfer): its old id, in its old repo, now points to <see cref="NewId"/> in <see cref="NewRepo"/>.</summary>
public sealed record IdeaMoveRow(string Repo, string Id, string NewRepo, string NewId);

/// <summary>An idea as a person or a tool names it: its repo when it said one (<c>tianwen I1829</c>), and the issue's number.</summary>
public readonly record struct IdeaRef(string? Repo, int Number)
{
    /// <summary><c>I381</c>.</summary>
    public string Id => IdeaIds.Id(Number);
}

/// <summary>Idea ids: how they are written, read and addressed.</summary>
public static class IdeaIds
{
    /// <summary>The milestone every repo keeps its ideas in; made on the first idea noted there.</summary>
    public const string Milestone = "Ideas";

    /// <summary>The repo an idea is noted in when none is named.</summary>
    public const string DefaultRepo = "pilot";

    /// <summary><c>I</c> and the number: <c>I381</c>.</summary>
    public static string Id(int number) => "I" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The number in <paramref name="text"/> when it is <c>I381</c> (any case of the I); false for anything else, <c>I0</c> and <c>I-3</c> included.</summary>
    public static bool TryNumber(string text, out int number)
    {
        number = 0;
        return text.Length > 1 && text[0] is 'I' or 'i'
            && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    /// <summary>
    /// Reads an id: <c>I381</c> (in whichever repo the caller means) or <c>tianwen I1829</c> (the repo, a space or a slash, the id).
    /// The error says what an id looks like, naming <paramref name="text"/>.
    /// </summary>
    public static (IdeaRef? Ref, string? Error) Parse(string text)
    {
        var parts = text.Split([' ', '/', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var id = parts.Length > 0 ? parts[^1] : "";
        if (parts.Length is 1 or 2 && TryNumber(id, out var number))
        {
            return (new IdeaRef(parts.Length == 2 ? parts[0] : null, number), null);
        }
        return (null, $"{(text.Trim().Length > 0 ? text.Trim() : "nothing")} is no idea id: an idea is I<issue number> (I381), or its repo and that (tianwen I1829) in another repo");
    }

    /// <summary><c>idea/&lt;repo&gt;/I381</c>: the target of an idea verb; <paramref name="repo"/> is the repo's folder name or its checkout's path.</summary>
    public static string Target(string repo, int number) => Verbs.IdeaTargetPrefix + repo + "/" + Id(number);

    /// <summary>The repo and number an <see cref="Target"/> names; null for any other target.</summary>
    public static (string Repo, int Number)? ReadTarget(string target)
    {
        if (!target.StartsWith(Verbs.IdeaTargetPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = target[Verbs.IdeaTargetPrefix.Length..];
        var cut = rest.LastIndexOf('/');
        return cut > 0 && TryNumber(rest[(cut + 1)..], out var number) ? (rest[..cut], number) : null;
    }

    /// <summary>How long ago <paramref name="since"/> was, from <paramref name="now"/>, in the one unit that fits: <c>45s</c>, <c>12m</c>, <c>5h</c>, <c>3d</c>.</summary>
    public static string Age(DateTimeOffset since, DateTimeOffset now)
    {
        var span = now - since;
        return span < TimeSpan.FromMinutes(1) ? $"{Math.Max(0, (int)span.TotalSeconds)}s"
            : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes}m"
            : span < TimeSpan.FromDays(1) ? $"{(int)span.TotalHours}h"
            : $"{(int)span.TotalDays}d";
    }

    /// <summary>
    /// Where the idea <paramref name="repo"/> <paramref name="number"/> is now: the repo and number it was moved to, however many times,
    /// or the ones given when it never moved.
    /// </summary>
    public static (string Repo, int Number) Follow(IEnumerable<IdeaMoveRow> moves, string repo, int number)
    {
        var all = moves.ToArray();
        // A chain is as long as the moves there are: no more steps than that, so a cycle cannot hold it.
        for (var step = 0; step <= all.Length; step++)
        {
            var id = Id(number);
            if (all.FirstOrDefault(m => string.Equals(m.Repo, repo, StringComparison.OrdinalIgnoreCase) && m.Id == id) is not { } move || !TryNumber(move.NewId, out var next))
            {
                break;
            }
            (repo, number) = (move.NewRepo, next);
        }
        return (repo, number);
    }

    /// <summary>
    /// The code of the flight made from idea <paramref name="repo"/> <paramref name="number"/> (through a move's old id too), or null when
    /// none was: a planned idea's id resolves to its flight, on the same issue.
    /// </summary>
    public static string? FlightOf(IEnumerable<FlightRow> flights, IEnumerable<IdeaMoveRow> moves, string repo, int number)
    {
        (repo, number) = Follow(moves, repo, number);
        var id = Id(number);
        return flights.FirstOrDefault(f => string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase) && f.FromIdea == id)?.Code;
    }

    /// <summary>The ideas as they are listed: the newest change first, the highest number first among equals, one repo after another by their newest.</summary>
    public static IEnumerable<IdeaRow> Order(IEnumerable<IdeaRow> ideas) =>
        ideas.OrderByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Number);
}
