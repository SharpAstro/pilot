using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What the Ideas page shows: the node's ideas, the questions it asked about them, and the repos an idea can move to.</summary>
/// <param name="Ideas">Every idea of every repo the node reads.</param>
/// <param name="Asks">The open cards about ideas (<see cref="CardKinds.IdeaRepo"/>, <see cref="CardKinds.IdeaConflict"/>), shown inline with their choices.</param>
/// <param name="Repos">The repos the node tracks, by folder name: where Move to repo offers to send an idea.</param>
internal sealed record IdeasScreen(IReadOnlyList<IdeaRow> Ideas, IReadOnlyList<CardRow> Asks, IReadOnlyList<string> Repos, string Source, DateTimeOffset AsOf)
{
    public static IdeasScreen Empty { get; } = new([], [], [], "no node", DateTimeOffset.MinValue);

    /// <summary>The flights, so a planned idea's id leads to its flight.</summary>
    public IReadOnlyList<FlightRow> Flights { get; init; } = [];

    /// <summary>The open milestones of every repo the node reads, the Ideas milestone among them.</summary>
    public IReadOnlyList<MilestoneRow> Milestones { get; init; } = [];

    /// <summary>The open issues of every repo, in a milestone or in none; an idea's issue is one of them, and carries its readiness.</summary>
    public IReadOnlyList<IssueRow> Issues { get; init; } = [];

    /// <summary>The ideas moved to another repo: an old id keeps resolving.</summary>
    public IReadOnlyList<IdeaMoveRow> Moves { get; init; } = [];

    /// <summary>The ideas and flights <paramref name="text"/> names by id (<c>I381</c>, <c>tianwen I1829</c>), as links.</summary>
    public IReadOnlyList<IdeaLink> LinksIn(string text) => IdeaLinks.In(text, Ideas, Flights, Moves);

    /// <summary>How many ideas wait for someone to plan them: the rail's count.</summary>
    public int Noted => Ideas.Count(i => i.State == IdeaState.Noted);

    /// <summary>The cards the page answers itself: an idea's repo choice and a conflicting edit.</summary>
    public static bool IsAsk(CardRow card) => card.State == CardState.Open && card.Kind is CardKinds.IdeaRepo or CardKinds.IdeaConflict;
}
