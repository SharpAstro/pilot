using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's knowledge that sessions have stopped taking turns across the board (PIL791), as opposed to one session that is
/// stuck. It opens when the account's login changes (<see cref="AccountChanged"/>) or when two different subjects show no first
/// turn within <see cref="Window"/> of each other (<see cref="Observe"/>), raises one Ring card however many sessions stall, and
/// ends when any session shows a turn later than the outage began (<see cref="Look"/>). While it is open the first-turn ladders
/// neither restart nor ring per session; the window it covered is kept in <see cref="LastWindow"/> for what redoes the work.
/// </summary>
public sealed class TurnOutage(NodeState state)
{
    /// <summary>The kind of the one card an outage raises.</summary>
    public const string CardKind = "turns-outage";

    /// <summary>How close together two subjects' missing first turns must be to count as one outage rather than two stuck sessions.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _stalled = new(StringComparer.Ordinal);
    private DateTimeOffset? _since;
    private string? _cardId;
    private Dictionary<string, long>? _baseline;
    private (DateTimeOffset Since, DateTimeOffset Until)? _last;

    /// <summary>Whether an outage is open now.</summary>
    public bool IsOpen
    {
        get { lock (_gate) { return _since is not null; } }
    }

    /// <summary>When the open outage began, or null when none is open.</summary>
    public DateTimeOffset? Since
    {
        get { lock (_gate) { return _since; } }
    }

    /// <summary>The window of the outage that ended last (since, until), or null when none has ended.</summary>
    public (DateTimeOffset Since, DateTimeOffset Until)? LastWindow
    {
        get { lock (_gate) { return _last; } }
    }

    /// <summary>The account's login changed at <paramref name="at"/>: no session can be taking turns on the old one, so an outage opens.</summary>
    public void AccountChanged(DateTimeOffset at) => Open(at, "the account changed");

    /// <summary>
    /// A subject (a leg's worker, a validator, a reviewer, a flight's planner) showed no first turn in time, seen at <paramref name="at"/>.
    /// A second, different subject within <see cref="Window"/> opens an outage; one alone is only a stuck session.
    /// </summary>
    public void Observe(string subject, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_since is not null)
            {
                return;
            }
            _stalled[subject] = at;
            foreach (var old in _stalled.Where(p => at - p.Value > Window).Select(p => p.Key).ToList())
            {
                _stalled.Remove(old);
            }
            if (_stalled.Count >= 2)
            {
                Open(at, $"{_stalled.Count} sessions showed no first turn within {(int)Window.TotalMinutes} minutes");
            }
        }
    }

    /// <summary>One look at the sessions: a turn later than the outage's start (a response, or more context than at the first look) ends it, closes its card and keeps its window.</summary>
    public void Look(SessionRow[] sessions, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_since is not { } since)
            {
                return;
            }
            var first = _baseline is null;
            _baseline ??= sessions.ToDictionary(s => s.Id, s => (long)s.ContextTokens, StringComparer.Ordinal);
            var turned = sessions.Any(s => s.LastResponseAt is { } at && at > since)
                || (!first && sessions.Any(s => !s.Gone && s.ContextTokens > _baseline.GetValueOrDefault(s.Id, 0)));
            if (!turned)
            {
                return;
            }
            _last = (since, now);
            _since = null;
            _baseline = null;
            _stalled.Clear();
            if (_cardId is { } id)
            {
                state.CloseCard(id, $"sessions take turns again since {now.ToLocalTime():HH:mm}", LegWatch.Who);
                _cardId = null;
            }
        }
    }

    private void Open(DateTimeOffset at, string why)
    {
        lock (_gate)
        {
            if (_since is not null)
            {
                return;
            }
            _since = at;
            _baseline = null;
            _stalled.Clear();
            var card = state.RaiseCard(CardBin.Ring, CardKind, Verbs.NodeTarget, $"Sessions are not taking turns since {at.ToLocalTime():HH:mm}: {why}",
                $"outage opened at {at:O}: {why}; first-turn restarts and rings are held until a session takes a turn",
                summary: $"No session has taken a turn since {at.ToLocalTime():HH:mm} ({why}). The node holds its restarts and rings for first turns and closes this card by itself once any session takes a turn.");
            _cardId = card.Id;
        }
    }
}
