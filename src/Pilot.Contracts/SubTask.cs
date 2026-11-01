namespace Pilot.Contracts;

/// <summary>
/// A leg's sub-task in its flight's work item: a GitHub sub-issue of the flight's issue, a task on the Shortcut story. The node is
/// the authority; the sync is one way, so this only records where the leg's line was written.
/// </summary>
public sealed record SubTask
{
    /// <summary>The tracker's id for it, as text: the sub-issue's number on GitHub, the task's id on Shortcut.</summary>
    public required string Id { get; init; }

    /// <summary>Where a person opens it; empty when the tracker gave none.</summary>
    public string Url { get; init; } = "";
}

/// <summary>One sub-task of a work item as the tracker lists it, whoever added it: a leg's or a person's.</summary>
/// <param name="Id">The tracker's id for it, as in <see cref="SubTask.Id"/>.</param>
/// <param name="Title">Its title (a Shortcut task's description).</param>
/// <param name="Done">Whether it is closed (GitHub) or complete (Shortcut).</param>
/// <param name="Url">Where a person opens it; empty when the tracker gave none.</param>
public sealed record SubTaskItem(string Id, string Title, bool Done, string Url);
