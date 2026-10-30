using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What a section of the board holds.</summary>
internal enum BoardSectionKind
{
    /// <summary>The ideas still noted (open in an Ideas milestone), pinned on top of every view.</summary>
    Unsorted,

    /// <summary>The open issues in no milestone.</summary>
    NoMilestone,

    /// <summary>The open issues of one open milestone.</summary>
    Milestone,
}

/// <summary>
/// One open idea or issue as the board lists it, whichever row it came from: an idea (<see cref="Idea"/> is set) has its Markdown body shown, an
/// issue is a compact row. <see cref="Readiness"/> and <see cref="Flight"/> are the node's, read from the issue's row either way.
/// </summary>
internal sealed record BoardItem(string Repo, int Number, string Title, string Body, string Url, string Age, string Who, string? Flight, IssueReadiness? Readiness, DateTimeOffset UpdatedAt, IdeaRow? Idea)
{
    /// <summary>What names the item in the view's own state: <c>pilot#381</c>.</summary>
    public string Key => KeyOf(Repo, Number);

    /// <summary>The item as the issue it is: the target of <see cref="Verbs.SetMilestone"/>, <see cref="Verbs.CheckReady"/> and the others.</summary>
    public string Target => IssueIds.Target(Repo, Number);

    public static string KeyOf(string repo, int number) => repo + "#" + number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One section of the board: Unsorted (ideas), No milestone, or a milestone with its open issues and its counts. <see cref="Repo"/> is empty for
/// Unsorted, which holds the ideas of every repo.
/// </summary>
internal sealed record BoardSection(BoardSectionKind Kind, string Title, string Repo, MilestoneRow? Milestone, IReadOnlyList<BoardItem> Items)
{
    /// <summary>What names the section in the view's own state and in hit ids.</summary>
    public string Key => Kind switch
    {
        BoardSectionKind.Unsorted => "unsorted",
        BoardSectionKind.NoMilestone => "none",
        _ => "m:" + Repo + "/" + Title,
    };

    /// <summary>How many issues are open: the milestone's own count, or what the section lists.</summary>
    public int Open => Milestone?.OpenIssues ?? Items.Count;

    /// <summary>How many are closed (a milestone's; none for the other sections).</summary>
    public int Closed => Milestone?.ClosedIssues ?? 0;

    /// <summary>How far along it is, from 0 to 1: the closed share of all its issues; 0 for a section with none.</summary>
    public float Progress => Open + Closed == 0 ? 0f : (float)Closed / (Open + Closed);

    /// <summary>Whether an item dragged onto the section is moved into it: a milestone, or out of every milestone.</summary>
    public bool Accepts => Kind != BoardSectionKind.Unsorted;

    /// <summary>The repos its items belong to, the first seen first: what Suggest milestones is sent for.</summary>
    public IEnumerable<string> Repos => Items.Select(i => i.Repo).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The Ideas pane's model, which its three views share: the sections in the order they are shown, Unsorted first, then No milestone, then every
/// open milestone (earliest due first), each with its items. Built from what the node last sent, never from what a click did.
/// </summary>
internal sealed record IdeasBoard(IReadOnlyList<BoardSection> Sections)
{
    public static IdeasBoard Empty { get; } = new([]);

    /// <summary>The pinned section.</summary>
    public BoardSection Unsorted => Sections[0];

    /// <summary>The sections below it.</summary>
    public IEnumerable<BoardSection> Rest => Sections.Skip(1);

    /// <summary>The item named <paramref name="key"/> (<see cref="BoardItem.Key"/>), wherever it is; null for none.</summary>
    public BoardItem? Find(string key) => Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Key == key);

    /// <summary>The section that lists <paramref name="item"/>.</summary>
    public BoardSection? SectionOf(string itemKey) => Sections.FirstOrDefault(s => s.Items.Any(i => i.Key == itemKey));

    public static IdeasBoard Build(IdeasScreen screen)
    {
        IssueReadiness? ReadinessOf(string repo, int number) =>
            screen.Issues.FirstOrDefault(i => i.Number == number && string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase))?.Readiness;

        var unsorted = IdeaIds.Order(screen.Ideas).Where(i => i.State == IdeaState.Noted && i.Number > 0)
            .Select(i => new BoardItem(i.Repo, i.Number, i.Title, i.Body, i.Url, i.Age, i.Origin, null, ReadinessOf(i.Repo, i.Number), i.UpdatedAt, i))
            .ToList();
        var sections = new List<BoardSection> { new(BoardSectionKind.Unsorted, "Unsorted (Ideas)", "", null, unsorted) };

        BoardItem Of(IssueRow i) => new(i.Repo, i.Number, i.Title, i.Body, i.Url, i.Age, i.Author, i.Flight, i.Readiness, i.UpdatedAt, null);
        var inIdeas = (IssueRow i) => string.Equals(i.Milestone, IdeaIds.Milestone, StringComparison.OrdinalIgnoreCase);

        // Issues in no milestone, by repo then newest first: the node's own order.
        var none = IssueIds.OrderIssues(screen.Issues.Where(i => i.Milestone is null)).Select(Of).ToList();
        sections.Add(new BoardSection(BoardSectionKind.NoMilestone, "No milestone", "", null, none));

        foreach (var milestone in IssueIds.OrderMilestones(screen.Milestones.Where(m => !string.Equals(m.Title, IdeaIds.Milestone, StringComparison.OrdinalIgnoreCase))))
        {
            var items = IssueIds.OrderIssues(screen.Issues.Where(i => !inIdeas(i) && string.Equals(i.Repo, milestone.Repo, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(i.Milestone, milestone.Title, StringComparison.OrdinalIgnoreCase)))
                .Select(Of).ToList();
            sections.Add(new BoardSection(BoardSectionKind.Milestone, milestone.Title, milestone.Repo, milestone, items));
        }
        return new IdeasBoard(sections);
    }
}
