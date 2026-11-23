using Pilot.Contracts;

namespace Pilot.Ui;

// The words of the map's stops: a leg airport's state, a destination's sign and status word, and the hover that says where its PR stands
// (pilot #1299 names rebase only apart from a conflict there). Kept apart from FlightsView.Map.cs, which places and paints them.
internal sealed partial class FlightsView<TSurface>
{
    private static string StateWord(AirportState state) => state switch
    {
        AirportState.Done => "done",
        AirportState.Flying => "flying",
        AirportState.Failed => "failed",
        _ => "planned",
    };

    internal static string StatusWord(DestinationStatus status) => status switch
    {
        DestinationStatus.NoPr => "no PR yet",
        DestinationStatus.Open => "open",
        DestinationStatus.Draft => "draft",
        DestinationStatus.Reviewing => "reviewing",
        DestinationStatus.Passed => "passed",
        DestinationStatus.Changes => "changes",
        DestinationStatus.Armed => "armed",
        DestinationStatus.Queued => "queued",
        DestinationStatus.Conflicts => "conflicts",
        // The sign is a leg's name wide; the whole words (PullRequestStates.RebaseOnlyLabel) are on its hover.
        DestinationStatus.RebaseOnly => "rebase only",
        _ => "merged",
    };

    /// <summary>What a destination's colour means: the palette's Failed only for trouble, amber for waiting, progress, neutral.</summary>
    internal enum MapTone { Failed, Progress, Waiting, Neutral, Landed }

    /// <summary>
    /// The destination's tone from the PR's status and the flight's phase (design-language section 1). Failed only for a conflict or a
    /// failed leg; a review asking for changes is work in progress, never red; Queued, Armed and the not-yet-reviewed are amber;
    /// Passed is progress. Each status keeps its word (<see cref="StatusWord"/>), so colour never carries it alone.
    /// </summary>
    internal static MapTone DestinationColour(DestinationStatus status, FlightPhase phase) => status switch
    {
        DestinationStatus.Merged => MapTone.Landed,
        DestinationStatus.Conflicts => MapTone.Failed,
        _ when phase == FlightPhase.Failed => MapTone.Failed,
        DestinationStatus.NoPr => MapTone.Neutral,
        DestinationStatus.Changes or DestinationStatus.Passed or DestinationStatus.RebaseOnly => MapTone.Progress,
        _ => MapTone.Waiting,
    };

    // A row's note as drawn: whole to MaxNoteChars, else cut with an ellipsis (the whole text is on its hover).
    private static string NoteText(MapRowNote note) => Fit(note.Text, FlightMap.MaxNoteChars);

    private static string Fit(string text, int chars) => text.Length <= chars ? text : text[..(chars - 1)] + "…";

    // The words on a destination's sign: its repo and PR number (tianwen #7), the repo cut where both do not fit (tia… #1896); as wide
    // as a leg's name, so a connection's drop out of the sign (FlightMap.LabelHalfWidth) passes clear of it. The repo alone
    // before there is a PR.
    private static string SignOf(string repo, int? pullRequest)
    {
        if (pullRequest is not { } pr)
        {
            return repo.Length > 0 ? Fit(repo, LabelChars) : "PR";
        }
        var number = $" #{pr}";
        return LabelChars - number.Length < 4 || repo.Length == 0 ? $"PR #{pr}" : Fit(repo, LabelChars - number.Length) + number;
    }

    // What a destination says on hover: its PR by repo and number and where it stands, then the flight's brief.
    private string DestinationTip(FlightLine line, MapDestination d)
    {
        var repo = line.Row.Repo;
        var sign = d.PullRequest is { } pr ? $"{repo} PR #{pr}" : $"{repo} PR";
        var first = !MapLayers.ShowPrStates ? sign : d.PullRequest is null ? $"{repo}: {StatusWord(d.Status)}" : $"{sign}: {StatusWord(d.Status)}";
        var why = !MapLayers.ShowPrStates ? null : d.Status switch
        {
            DestinationStatus.RebaseOnly => FlightMap.RebaseOnlyTip,
            DestinationStatus.Conflicts => "Conflicts with the base: it needs a fix (go around).",
            _ => null,
        };
        return first + (why is null ? "" : "\n" + why) + "\n" + ChipBrief.Text(ChipBrief.Flight(line));
    }
}
