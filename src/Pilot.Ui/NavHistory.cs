namespace Pilot.Ui;

/// <summary>A session transcript as the window had it open: which session (and subagent), and the moment it was opened at.</summary>
internal sealed record TranscriptPlace(SessionLine Line, DateTimeOffset? At, string? Agent);

/// <summary>A page's selection: its list, the row, and how many rows there were; for Sessions the row's key, which holds as the rows reorder.</summary>
internal sealed record SelectionPlace(string ListId, int Index, int? Rows, string? Key);

/// <summary>
/// Where the window is, as far as Back needs to bring it back: the page, the nodes picked, the page's selection, the open
/// transcript and the open flight page. The new-flight wizard is not part of it. Two places are the same place when they
/// differ in nothing but the selection, which moving in a list is not a move. The order of places is DIR.Lib's
/// <see cref="DIR.Lib.NavigationHistory{TPlace}"/>.
/// </summary>
internal sealed record NavPlace(PilotPage Page, IReadOnlyList<string> Picked, SelectionPlace? Selection, TranscriptPlace? Transcript, string? FlightCode)
{
    public bool SameAs(NavPlace other) =>
        Page == other.Page && FlightCode == other.FlightCode && Picked.SequenceEqual(other.Picked)
        && Transcript?.Line.Key == other.Transcript?.Line.Key && Transcript?.Agent == other.Transcript?.Agent && Transcript?.At == other.Transcript?.At;
}
