namespace Pilot.Contracts;

/// <summary>A check only a person can make, filed as an issue by the node (#751): the issue's number and the check's title as the reviewer wrote it.</summary>
/// <param name="Number">The issue's number in the flight's repo.</param>
/// <param name="Title">The check's own title (the item's first line), which says whether a later report restates it.</param>
public sealed record HumanCheck(int Number, string Title);
