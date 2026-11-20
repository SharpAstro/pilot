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
// The one thing that is not: when the login changed (or a person lifted a limit) is stored with the usage (#1123), since a restart that
// forgot it took the old login's usage limit for this one's and held every start until that limit's reset.
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
        state.PutLoginChanged(at);
        if (limitEnded)
        {
            LimitEnded?.Invoke();
            return;
        }
        Open(at, "the account changed");
    }

    /// <summary>
    /// A person says the open usage limit does not hold (#1123): it is another login's, read from a session that began before the login
    /// changed and that the node no longer knows to be older (the node restarted since). The limit ends as at an account change: its
    /// window is kept for the redo, the held says go, and rows that began before <paramref name="at"/> no longer count toward a limit.
    /// False when no limit is open, and then nothing changes.
    /// </summary>
    public bool Lift(DateTimeOffset at, string who, string? why)
    {
        lock (_gate)
        {
            if (_since is not { } since || _limitUntil is null)
            {
                return false;
            }
            _loginChangedAt = at;
            Close(since, at, $"lifted by {who}: {(string.IsNullOrWhiteSpace(why) ? "the limit is not this login's" : why.Trim())}");
        }
        state.PutLoginChanged(at);
        LimitEnded?.Invoke();
        return true;
    }

    private DateTimeOffset? _loginChangedAt;

    /// <summary>
    /// When the login last changed or a person lifted a limit, or null; a limit read from a session that began before it is the old
    /// login's. The later of what this run saw and what is stored with the usage, so it outlives a restart of the node.
    /// </summary>
    public DateTimeOffset? LoginChangedAt
    {
        get
        {
            DateTimeOffset? seen;
            lock (_gate)
            {
                seen = _loginChangedAt;
            }
            return UsageRow.Later(seen, state.Usage()?.LoginChangedAt);
        }
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
    public void Look(SessionRow[] sessions, DateTimeOffset now, UsageRow? usage = null)
    {
        var limitEnded = false;
        lock (_gate)
        {
            limitEnded = LookLocked(sessions, now, usage);
        }
        if (limitEnded)
        {
            // Outside the lock: what waited for the reset (the node's held says) runs its own code.
            LimitEnded?.Invoke();
        }
    }

    /// <summary>Raised once when an open account limit ends (the first look at or after its reset), after the outage is closed.</summary>
    public event Action? LimitEnded;

    private bool LookLocked(SessionRow[] sessions, DateTimeOffset now, UsageRow? usage)
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
                // The account's own figures end it (#1141): fresh figures for the window the limit belongs to, of the account it was seen
                // under, read after it was seen, that show the window was reset (a later reset) or read under 90%.
                if (FiguresEnd(usage, until) is { } figures)
                {
                    var age = Math.Max(0, (int)(now - figures.ReadAt).TotalMinutes);
                    _loginChangedAt = now;
                    _evidenceEndedAt = now;
                    Close(since, now, $"the account's usage reads {figures.UsedPercent:0}% since {figures.ReadAt.ToLocalTime():HH:mm} (figures {age} min old), so the usage limit no longer holds");
                    state.PutLoginChanged(now);
                    return true;
                }
                // Evidence ends it (#1123): a session of the limit's account and family whose turn was answered after the limit was seen,
                // with no limit error (it may have begun before the stop). Another login's row proves nothing; a row with no account is
                // taken only when it began after the limit was seen. A row of unknown family proves nothing either.
                if (_limitFamily is { } family && _limitSeenAt is { } seen
                    && sessions.FirstOrDefault(s => s.ApiError is null && s.LastResponseAt is { } r && r > seen
                        && (s.Account is { } who ? string.Equals(who, _limitAccount, StringComparison.OrdinalIgnoreCase) : s.StartedAt > seen)
                        && string.Equals(Verbs.ModelFamily(s.Model), family, StringComparison.Ordinal)) is { } proof)
                {
                    // The end moves the cut as a lift does (PutLoginChanged), so the rows that opened the limit do not reopen it.
                    _loginChangedAt = now;
                    _evidenceEndedAt = now;
                    Close(since, now, $"{proof.Name} answered a turn on {family} after the limit was seen, so the usage limit no longer holds");
                    state.PutLoginChanged(now);
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

    /// <summary>How far a session error's reset and the status line's resets_at may differ and still be the same window's (they are written separately).</summary>
    public static readonly TimeSpan WindowTolerance = TimeSpan.FromMinutes(10);

    /// <summary>The usage share under which a window no longer holds a limit.</summary>
    public const double LimitEndsBelow = 90;

    private static bool? FiveHourWindowOf(UsageRow? usage, DateTimeOffset until)
    {
        if (usage?.FiveHour is { ResetsAt: { } five } && (five - until).Duration() <= WindowTolerance)
        {
            return true;
        }
        if (usage?.SevenDay is { ResetsAt: { } seven } && (seven - until).Duration() <= WindowTolerance)
        {
            return false;
        }
        return null;
    }

    private (double UsedPercent, DateTimeOffset ReadAt)? FiguresEnd(UsageRow? usage, DateTimeOffset until)
    {
        // The write time of the figures, rewrites included: the held row's ReadAt moves only when they change.
        var written = state.UsageWrittenAt() ?? usage?.ReadAt;
        if (_limitWindowFiveHour is not { } fiveHour || _limitSeenAt is not { } seen || _limitAccount is not { } account
            || usage is not { HasFigures: true, Account: { } who } || written is not { } wroteAt
            || !string.Equals(who.Email, account, StringComparison.OrdinalIgnoreCase) || wroteAt <= seen)
        {
            return null;
        }
        var window = fiveHour ? usage.FiveHour : usage.SevenDay;
        if (window is null || !(window.UsedPercent < LimitEndsBelow || window.ResetsAt > until + WindowTolerance))
        {
            return null;
        }
        return (window.UsedPercent, wroteAt);
    }

    private void NoteWindow(DateTimeOffset until)
    {
        var usage = state.Usage();
        _limitWindowFiveHour = FiveHourWindowOf(usage, until);
        _limitAccount = usage?.Account?.Email;
    }

    // The window the open limit belongs to (true the 5-hour, false the 7-day, null none: a spend or individual limit the figures never
    // end), and the account it was seen under (#1141). Memory only, like the limit itself.
    private bool? _limitWindowFiveHour;
    private string? _limitAccount;

    private void Close(DateTimeOffset since, DateTimeOffset now, string why)
    {
        _last = (since, now);
        _since = null;
        _baseline = null;
        _limitUntil = null;
        _limitFamily = null;
        _limitSeenAt = null;
        _limitWindowFiveHour = null;
        _limitAccount = null;
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
    private DateTimeOffset? _evidenceEndedAt;

    /// <summary>When a limit last ended by evidence (a later session answered a turn on the reporting family), this run only; rows that began before it open no limit.</summary>
    public DateTimeOffset? LimitEvidenceEndedAt
    {
        get { lock (_gate) { return _evidenceEndedAt; } }
    }

    private string? _limitFamily;
    private DateTimeOffset? _limitSeenAt;

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
    /// <summary>
    /// <see cref="OpenLimit"/> for a limit judged from the session rows against
    /// <paramref name="loginChangedWhenJudged"/>: when the login changed, or a person lifted the limit, between that judgement and now,
    /// the judgement is stale (it counted rows that no longer count) and nothing opens. Without this a look already under way reopened a
    /// limit a moment after it was lifted, and it then held until its reset (#1123).
    /// </summary>
    public void OpenJudgedLimit(DateTimeOffset until, string why, DateTimeOffset at, DateTimeOffset? loginChangedWhenJudged, string? family = null)
    {
        lock (_gate)
        {
            if (UsageRow.Later(_loginChangedAt, state.Usage()?.LoginChangedAt) != loginChangedWhenJudged)
            {
                return;
            }
            OpenLimit(until, why, at, family);
        }
    }

    public void OpenLimit(DateTimeOffset until, string why, DateTimeOffset at, string? family = null)
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
                    _limitFamily ??= family;
                    // The window is judged again for the later reset; _limitSeenAt stays at the first sighting, so figures written since still count.
                    NoteWindow(until);
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
            _limitFamily = family;
            _limitSeenAt = at;
            NoteWindow(until);
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
            [
                new CardChoice { Label = "Mark it read", Verb = Verbs.Dismiss, Target = "" },
                new CardChoice { Label = "Lift the limit", Verb = Verbs.LiftLimit, Target = Verbs.NodeTarget },
            ],
            summary: $"The account is on its usage limit until {until.ToLocalTime():HH:mm} ({why}). {FiguresSay(at)} The node starts nothing and rings nothing for a session that took no turn; what waited starts at the reset, or sooner when a session of the account takes a turn or fresh figures show the window was reset, and this card closes then. Marking it read leaves the hold. If the limit is another login's (you logged in again and sessions take turns), lift it.");
        _cardId = card.Id;
    }

    // When the figures the node holds were last read and where from, for a card's summary: said as of an age once past the gate's fresh time.
    private string FiguresSay(DateTimeOffset now)
    {
        if (state.Usage() is not { HasFigures: true } usage)
        {
            return "The node holds no usage figures.";
        }
        var from = usage.Source is { Length: > 0 } src ? $" from the {src}" : "";
        var age = now - usage.ReadAt;
        var when = age >= StartGate.FreshFor ? $"as of {(int)age.TotalMinutes} min ago ({usage.ReadAt.ToLocalTime():HH:mm})" : $"read {usage.ReadAt.ToLocalTime():HH:mm}";
        return $"The usage figures were {when}{from}.";
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
