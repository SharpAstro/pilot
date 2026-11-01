namespace Pilot.Contracts;

/// <summary>
/// The value of GitHub's Priority issue field, most urgent first. Unset is null where a level is carried. Urgent and High make
/// a flight a priority one (<see cref="IssuePriorities.IsPriority"/>); the order of the queue is <see cref="IssuePriorities.Rank"/>.
/// </summary>
public enum IssuePriority
{
    Urgent,
    High,
    Medium,
    Low,
}

/// <summary>What the Priority field says, and how admission reads it.</summary>
public static class IssuePriorities
{
    /// <summary>The label that marks a priority flight where the repo or org has no Priority field.</summary>
    public const string Label = "pilot: priority";

    /// <summary>Whether a comma-joined list of labels carries <see cref="Label"/>.</summary>
    public static bool HasLabel(string? labels) =>
        (labels ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(Label, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the flight is a priority one: where the repo has the field, its Urgent or High (a stray label is ignored); otherwise
    /// the label, as <paramref name="labels"/> (the PR's) or <paramref name="issueLabel"/> (the issue's, read at a start) says.
    /// </summary>
    public static bool IsPriority(bool hasField, IssuePriority? level, string? labels, bool issueLabel = false) =>
        hasField ? level is IssuePriority.Urgent or IssuePriority.High : issueLabel || HasLabel(labels);

    /// <summary>The place in the queue: Urgent, High, Medium, unset, Low.</summary>
    public static int Rank(IssuePriority? level) => level switch
    {
        IssuePriority.Urgent => 0,
        IssuePriority.High => 1,
        IssuePriority.Medium => 2,
        null => 3,
        _ => 4,
    };

    /// <summary>The field's option name for a level.</summary>
    public static string Name(IssuePriority level) => level.ToString();

    public static bool TryParse(string? name, out IssuePriority level) => Enum.TryParse(name?.Trim(), ignoreCase: true, out level) && Enum.IsDefined(level);
}
