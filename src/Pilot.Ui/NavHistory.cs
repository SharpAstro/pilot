namespace Pilot.Ui;

/// <summary>
/// The back and forward stacks of the window's moves, over any kind of place: the place being left is pushed, and going
/// back (or forward) trades the place the window is at for the one it returns to. A new move after going back drops the
/// places forward of it, as a browser's does. Bounded: the oldest place falls off the back stack at <see cref="Capacity"/>.
/// </summary>
internal sealed class NavHistory<TPlace>(int capacity = NavHistory<TPlace>.DefaultCapacity)
{
    public const int DefaultCapacity = 100;

    private readonly List<TPlace> _back = [];
    private readonly List<TPlace> _forward = [];

    public int Capacity { get; } = Math.Max(1, capacity);

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public int BackCount => _back.Count;

    public int ForwardCount => _forward.Count;

    /// <summary>Records <paramref name="from"/>, the place a move leaves, and forgets the places forward of it.</summary>
    public void Push(TPlace from)
    {
        _forward.Clear();
        _back.Add(from);
        if (_back.Count > Capacity)
        {
            _back.RemoveAt(0);
        }
    }

    /// <summary>The place before, with <paramref name="current"/> kept to come forward to; false with nothing to go back to.</summary>
    public bool Back(TPlace current, out TPlace place)
    {
        if (_back.Count == 0)
        {
            place = default!;
            return false;
        }
        place = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(current);
        return true;
    }

    /// <summary>The place after, with <paramref name="current"/> kept to go back to; false with nothing to go forward to.</summary>
    public bool Forward(TPlace current, out TPlace place)
    {
        if (_forward.Count == 0)
        {
            place = default!;
            return false;
        }
        place = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(current);
        return true;
    }
}

/// <summary>A session transcript as the window had it open: which session (and subagent), and the moment it was opened at.</summary>
internal sealed record TranscriptPlace(SessionLine Line, DateTimeOffset? At, string? Agent);

/// <summary>A page's selection: its list, the row, and how many rows there were; for Sessions the row's key, which holds as the rows reorder.</summary>
internal sealed record SelectionPlace(string ListId, int Index, int? Rows, string? Key);

/// <summary>
/// Where the window is, as far as Back needs to bring it back: the page, the nodes picked, the page's selection, the open
/// transcript and the open flight page. The new-flight wizard is not part of it. Two places are the same place when they
/// differ in nothing but the selection, which moving in a list is not a move.
/// </summary>
internal sealed record NavPlace(PilotPage Page, IReadOnlyList<string> Picked, SelectionPlace? Selection, TranscriptPlace? Transcript, string? FlightCode)
{
    public bool SameAs(NavPlace other) =>
        Page == other.Page && FlightCode == other.FlightCode && Picked.SequenceEqual(other.Picked)
        && Transcript?.Line.Key == other.Transcript?.Line.Key && Transcript?.Agent == other.Transcript?.Agent && Transcript?.At == other.Transcript?.At;
}
