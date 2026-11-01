using System.Text.RegularExpressions;

namespace Pilot.Contracts;

/// <summary>
/// An idea id found in a text, as what it leads to: its flight once planned (<see cref="FlightCode"/>), else the idea's issue page
/// (<see cref="Url"/>).
/// </summary>
/// <param name="Text">What to show: <c>I381</c>, <c>tianwen I1829</c> as written, or the flight's code for a planned one.</param>
public sealed record IdeaLink(string Text, string? FlightCode, string? Url);

/// <summary>Finds <c>I&lt;n&gt;</c> and <c>&lt;repo&gt; I&lt;n&gt;</c> in a card, a Handled line or a flight page, and says where each goes.</summary>
public static partial class IdeaLinks
{
    [GeneratedRegex(@"(?<![\w/#-])(?:(?<repo>[A-Za-z][\w.-]*) )?I(?<n>[1-9]\d{0,8})(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex Mention();

    /// <summary>
    /// The ideas <paramref name="text"/> names, each once, in order, that the node knows: an id the node has no idea, flight or move for is no
    /// link. A planned idea (or the old id of a moved one) leads to its flight.
    /// </summary>
    public static IReadOnlyList<IdeaLink> In(string text, IEnumerable<IdeaRow> ideas, IEnumerable<FlightRow> flights, IEnumerable<IdeaMoveRow> moves)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var rows = ideas as IReadOnlyCollection<IdeaRow> ?? [.. ideas];
        var made = flights as IReadOnlyCollection<FlightRow> ?? [.. flights];
        var moved = moves as IReadOnlyCollection<IdeaMoveRow> ?? [.. moves];
        var repos = rows.Select(i => i.Repo).Concat(made.Where(f => f.FromIdea is not null).Select(f => f.Repo))
            .Concat(moved.SelectMany(m => new[] { m.Repo, m.NewRepo })).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<IdeaLink>();
        foreach (Match match in Mention().Matches(text))
        {
            var number = int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var named = match.Groups["repo"].Success && repos.Contains(match.Groups["repo"].Value) ? match.Groups["repo"].Value : null;
            var written = named is null ? IdeaIds.Id(number) : $"{named} {IdeaIds.Id(number)}";
            var candidates = named is not null ? [named] : repos.OrderBy(r => !string.Equals(r, IdeaIds.DefaultRepo, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var repo in candidates)
            {
                var (nowRepo, nowNumber) = IdeaIds.Follow(moved, repo, number);
                var id = IdeaIds.Id(nowNumber);
                IdeaLink? link = null;
                if (IdeaIds.FlightOf(made, moved, nowRepo, nowNumber) is { } code)
                {
                    link = new IdeaLink(written, code, null);
                }
                else if (rows.FirstOrDefault(i => i.Id == id && string.Equals(i.Repo, nowRepo, StringComparison.OrdinalIgnoreCase)) is { Url.Length: > 0 } row)
                {
                    link = new IdeaLink(written, null, row.Url);
                }
                if (link is not null)
                {
                    if (!found.Any(f => f.Text == link.Text))
                    {
                        found.Add(link);
                    }
                    break;
                }
            }
        }
        return found;
    }
}
