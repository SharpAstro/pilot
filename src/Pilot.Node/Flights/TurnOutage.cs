using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's knowledge that sessions have stopped taking turns across the board (PIL791), as opposed to one session that is
/// stuck. It opens when the account's login changes (<see cref="AccountChanged"/>) or when two different subjects show no first
/// turn within <see cref="Window"/> of each other (<see cref="Observe"/>), raises one For-you card (silent, since the node has nothing for the person to do; its one button marks it read) however many sessions stall, and
/// ends when any session shows a turn later than the outage began (<see cref="Look"/>). While it is open the first-turn ladders
/// neither restart nor ring per session; the window it covered is kept in <see cref="LastWindow"/> for what redoes the work.
/// </summary>
// The outage is kept in memory only, on purpose: a node restart forgets it, and the next stalled sessions or account change open it again.
public sealed class TurnOutage(NodeState state)
{
    /// <summary>The kind of the one card an outage raises.</summary>
    public const string CardKind = "turns-outage";

    /// <summary>How close together two subjects' missing first turns must be to count as one outage rather than two stuck sessions.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>How often one held start (the oldest) is let through while the outage lasts, so the node finds out by itself that turns are back.</summary>
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _stalled = new(StringComparer.Ordinal);
    private DateTimeOffset? _since;
    private string? _cardId;
    private Dictionary<string, long>? _baseline;
    private (DateTimeOffset Since, DateTimeOffset Until)? _last;

    // The starts held by the outage, by the gate's key: when each first asked and last asked. A start that stopped asking is not waited for.
    private readonly Dictionary<string, (DateTimeOffset First, DateTimeOffset Last)> _waiting = new(StringComparer.Ordinal);
    private DateTimeOffset _probedAt;
    private string? _probeKey;

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

    /// <summary>
    /// A start asks to go while the outage lasts (<paramref name="key"/> is the gate's key for it). False holds it. True lets it through as the
    /// probe: the oldest start still asking, once per <see cref="ProbeEvery"/> (the outage's own opening counts as the last), and every start
    /// when no outage is open. The start the probe went to is let through again when it asks again within the period, as one start asks the gate
    /// at more than one step (the sweep's, then the command's own). A turn of the probe's session, or of any other, is what ends the outage.
    /// </summary>
    public bool Probe(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_since is null)
            {
                return true;
            }
            if (_limitUntil is not null)
            {
                // The node knows when turns come back: nothing is let through to find out.
                return false;
            }
            if (_probeKey == key && now - _probedAt < ProbeEvery)
            {
                return true;
            }
            _waiting[key] = _waiting.TryGetValue(key, out var asked) ? (asked.First, now) : (now, now);
            foreach (var gone in _waiting.Where(p => now - p.Value.Last > ProbeEvery).Select(p => p.Key).ToList())
            {
                _waiting.Remove(gone);
            }
            if (now - _probedAt < ProbeEvery
                || _waiting.OrderBy(p => p.Value.First).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key != key)
            {
                return false;
            }
            _probedAt = now;
            _probeKey = key;
            _waiting.Remove(key);
            return true;
        }
    }

    /// <summary>The account's login changed at <paramref name="at"/>: no session can be taking turns on the old one, so an outage opens.</summary>
    /// <remarks>
    /// An open account limit belonged to the old login, so it ends here (its window is kept for the redo and the held says, as at the reset),
    /// and rows that began before the change no longer count toward a limit (<see cref="LoginChangedAt"/>).
    /// </remarks>
    public void AccountChanged(DateTimeOffset at)
    {
        var limitEnded = false;
        lock (_gate)
        {
            _loginChangedAt = at;
            if (_since is { } since && _limitUntil is not null)
            {
                Close(since, at, "the account changed, so the old login's usage limit no longer holds");
                limitEnded = true;
            }
        }
        if (limitEnded)
        {
            LimitEnded?.Invoke();
            return;
        }
        Open(at, "the account changed");
    }

    private DateTimeOffset? _loginChangedAt;

    /// <summary>When the login last changed, or null; a limit read from a session that began before it is the old login's.</summary>
    public DateTimeOffset? LoginChangedAt
    {
        get { lock (_gate) { return _loginChangedAt; } }
    }

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

    /// <summary>A subject showed a first turn after all: it no longer counts toward an outage.</summary>
    public void Turned(string subject)
    {
        lock (_gate) { _stalled.Remove(subject); }
    }

    /// <summary>One look at the sessions: a turn later than the outage's start (a response, or more context than at the first look) ends it, closes its card and keeps its window.</summary>
    public void Look(SessionRow[] sessions, DateTimeOffset now)
    {
        var limitEnded = false;
        lock (_gate)
        {
            limitEnded = LookLocked(sessions, now);
        }
        if (limitEnded)
        {
            // Outside the lock: what waited for the reset (the node's held says) runs its own code.
            LimitEnded?.Invoke();
        }
    }

    /// <summary>Raised once when an open account limit ends (the first look at or after its reset), after the outage is closed.</summary>
    public event Action? LimitEnded;

    private bool LookLocked(SessionRow[] sessions, DateTimeOffset now)
    {
        {
            if (_since is not { } since)
            {
                return false;
            }
            if (_limitUntil is { } until)
            {
                // A stray turn before the reset does not end a limit; the first look at or after the reset does.
                if (now >= until)
                {
                    Close(since, now, $"the usage limit ended at {until.ToLocalTime():HH:mm}");
                    return true;
                }
                return false;
            }
            var first = _baseline is null;
            _baseline ??= sessions.ToDictionary(s => s.Id, s => (long)s.ContextTokens, StringComparer.Ordinal);
            var turned = sessions.Any(s => s.LastResponseAt is { } at && at > since)
                || (!first && sessions.Any(s => !s.Gone && s.ContextTokens > _baseline.GetValueOrDefault(s.Id, 0)));
            if (!turned)
            {
                return false;
            }
            Close(since, now, $"sessions take turns again since {now.ToLocalTime():HH:mm}");
            return false;
        }
    }

    private void Close(DateTimeOffset since, DateTimeOffset now, string why)
    {
        _last = (since, now);
        _since = null;
        _baseline = null;
        _limitUntil = null;
        _stalled.Clear();
        _waiting.Clear();
        _probeKey = null;
        if (_cardId is { } id)
        {
            state.CloseCard(id, why, LegWatch.Who);
            _cardId = null;
        }
    }

    /// <summary>The kind of the one card an account limit raises.</summary>
    public const string LimitCardKind = "account-limit";

    private DateTimeOffset? _limitUntil;

    /// <summary>When the open account limit resets, or null when none is open.</summary>
    public DateTimeOffset? LimitUntil
    {
        get { lock (_gate) { return _limitUntil; } }
    }

    /// <summary>Why a start waits under the open limit, with the reset; null when no limit is open.</summary>
    public string? LimitSay
    {
        get { lock (_gate) { return _limitUntil is { } u ? $"the account is on its usage limit until {u.ToLocalTime():HH:mm}, so it waits until then" : null; } }
    }

    /// <summary>
    /// The account is on a usage or spend limit until <paramref name="until"/>, seen at <paramref name="at"/>. Opens the outage with a known end
    /// (or extends an open limit to a later end); an open plain outage is turned into the limit. A reset not after <paramref name="at"/> opens nothing.
    /// </summary>
    public void OpenLimit(DateTimeOffset until, string why, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (until <= at)
            {
                return;
            }
            if (_limitUntil is { } current)
            {
                if (until > current)
                {
                    _limitUntil = until;
                    RaiseLimitCard(at, until, why);
                }
                return;
            }
            var since = _since ?? at;
            if (_cardId is { } old)
            {
                state.CloseCard(old, "it is the usage limit", LegWatch.Who);
                _cardId = null;
            }
            _since = since;
            _limitUntil = until;
            _baseline = null;
            _stalled.Clear();
            _waiting.Clear();
            _probeKey = null;
            RaiseLimitCard(at, until, why);
        }
    }

    private void RaiseLimitCard(DateTimeOffset at, DateTimeOffset until, string why)
    {
        if (_cardId is { } prior)
        {
            state.CloseCard(prior, $"the limit now lasts until {until.ToLocalTime():HH:mm}", LegWatch.Who);
        }
        var card = state.RaiseCard(CardBin.ForYou, LimitCardKind, Verbs.NodeTarget, $"On the usage limit until {until.ToLocalTime():HH:mm}",
            $"limit seen at {at:O}: {why}; resets {until:O}; the node starts no session of its own and rings nothing for a session that took no turn until then",
            [new CardChoice { Label = "Mark it read", Verb = Verbs.Dismiss, Target = "" }],
            summary: $"The account is on its usage limit until {until.ToLocalTime():HH:mm} ({why}). The node starts nothing and rings nothing for a session that took no turn; what waited starts then, and this card closes by itself.");
        _cardId = card.Id;
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
            _waiting.Clear();
            _probedAt = at;
            _probeKey = null;
            var card = state.RaiseCard(CardBin.ForYou, CardKind, Verbs.NodeTarget, $"Sessions are not taking turns since {at.ToLocalTime():HH:mm}",
                $"outage opened at {at:O}: {why}; first-turn restarts and rings are held until a session takes a turn",
                [new CardChoice { Label = "Mark it read", Verb = Verbs.Dismiss, Target = "" }],
                summary: $"No session has taken a turn since {at.ToLocalTime():HH:mm} ({why}). The node holds its restarts and rings for first turns and closes this card by itself once any session takes a turn.");
            _cardId = card.Id;
        }
    }
}
