namespace Pilot.Buddy;

/// <summary>What a notice is: an ask (a session waits on the person), trouble (CI failed or something is stuck) or good news (a PR merged, a flight landed).</summary>
public enum NoticeKind
{
    /// <summary>A watchtower session needs the person: amber bubble, "Open the chat" and "Later".</summary>
    Ask,

    /// <summary>CI failed or a leg is stuck: red bubble with the error icon, "Open the run" and "Later".</summary>
    Trouble,

    /// <summary>A PR merged or a flight landed: a bubble with no buttons for <see cref="BuddyModel.GoodNewsFor"/>, then the cat sleeps by itself.</summary>
    GoodNews,
}

/// <summary>One thing the cat tells. <paramref name="Id"/> is the key the host clears it by (a trouble clears when its failure does).</summary>
public sealed record BuddyNotice(string Id, NoticeKind Kind, string Title, string Body);

/// <summary>The cat's moods, as the model reports them (DSN-2 v5): what the window draws follows from this and <see cref="BuddyModel.GrumpyFace"/>.</summary>
public enum BuddyMood
{
    /// <summary>Curled up on its cushion, saying nothing.</summary>
    Asleep,

    /// <summary>Hovered while asleep: one eye open, the one-line summary in a bubble.</summary>
    OneEyeOpen,

    /// <summary>An ask is showing: wide eyes, a little "o", a yellow "!".</summary>
    Attention,

    /// <summary>Good news is showing: blush, a tiny smile, "...fine.".</summary>
    GoodNews,

    /// <summary>Trouble is showing: furious, red bubble.</summary>
    Trouble,
}

/// <summary>Whether the cat is here, walking off, away (only a badge at the edge) or walking back (#1381 section 2).</summary>
public enum AwayPhase
{
    /// <summary>On its cushion as ever.</summary>
    Present,

    /// <summary>The one-off leaving animation, after the long quiet sleep.</summary>
    Leaving,

    /// <summary>Away: the window shows only the clickable badge, and no frame is asked for.</summary>
    Gone,

    /// <summary>The one-off return animation, after a wake.</summary>
    Returning,
}

/// <summary>
/// The buddy's state, pure and driven by a <see cref="TimeProvider"/>: no thread, no timer, no frame loop. The host asks <see cref="Mood"/> and
/// the other reads whenever it draws, and sleeps until <see cref="NextWake"/> (the end of good news, the next breath) rather than ticking.
/// <para>
/// One bubble shows at a time; more notices queue behind it (trouble ahead of asks ahead of good news, each kind in the order it came). "Later"
/// puts the cat back to sleep for that notice only: the next one in the queue shows, and a trouble stays <em>open</em> after it, so the cat keeps
/// its grumpy face (<see cref="GrumpyFace"/>) while it sleeps until <see cref="Clear"/> says the failure is gone.
/// </para>
/// </summary>
public sealed class BuddyModel(TimeProvider time)
{
    /// <summary>How long good news shows before the cat goes back to sleep by itself.</summary>
    public static readonly TimeSpan GoodNewsFor = TimeSpan.FromSeconds(10);

    /// <summary>How long one breathing pose lasts: the body grows by two pixels for a step and back for the next.</summary>
    public static readonly TimeSpan BreathStep = TimeSpan.FromSeconds(2.5);

    private readonly List<BuddyNotice> _waiting = [];
    private readonly HashSet<string> _openFailures = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _born = time.GetUtcNow();
    private readonly HashSet<string> _badged = new(StringComparer.Ordinal);
    private BuddyNotice? _current;
    private DateTimeOffset _shownAt;
    private bool _hover;
    private bool _engaged;
    private AwayPhase _phase;
    private DateTimeOffset _phaseAt;
    private DateTimeOffset? _quietSince;
    private TimeSpan? _longSleep;

    /// <summary>How long the leaving animation plays, and the return: each a second or two of frames, once.</summary>
    public static readonly TimeSpan LeaveFor = TimeSpan.FromSeconds(1.6), ReturnFor = TimeSpan.FromSeconds(1.2);

    /// <summary>One step of the leave and return animations: a frame each, and none between them.</summary>
    public static readonly TimeSpan AnimStep = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest a gone cat's wake time is ahead: a deadline for the host's wait, not a poll (it draws nothing when it comes).</summary>
    public static readonly TimeSpan LongestIdle = TimeSpan.FromMinutes(1);

    /// <summary>The quiet sleep after which the cat goes away; null is never. The host sets it from the settings.</summary>
    public TimeSpan? LongSleep
    {
        get => _longSleep;
        set
        {
            _longSleep = value;
            // Never while the cat is leaving or gone: nothing would ever bring it back by itself, so it returns now.
            if (value is null && _phase is AwayPhase.Leaving or AwayPhase.Gone)
            {
                Wake();
            }
            Settle();
        }
    }

    /// <summary>Where the cat is now: here, leaving, gone or returning. Reading it lets a finished animation or an elapsed long sleep take effect.</summary>
    public AwayPhase Phase
    {
        get
        {
            Settle();
            return _phase;
        }
    }

    /// <summary>How far the animation of <see cref="Phase"/> is, 0 to 1 (Leaving: 0 on its cushion, 1 away; Returning: 0 away, 1 back). 0 for Present, 1 for Gone.</summary>
    public float AwayProgress
    {
        get
        {
            Settle();
            var spent = (time.GetUtcNow() - _phaseAt).TotalSeconds;
            return _phase switch
            {
                AwayPhase.Leaving => (float)Math.Clamp(spent / LeaveFor.TotalSeconds, 0, 1),
                AwayPhase.Returning => (float)Math.Clamp(spent / ReturnFor.TotalSeconds, 0, 1),
                AwayPhase.Gone => 1f,
                _ => 0f,
            };
        }
    }

    /// <summary>The animation's frame, one per <see cref="AnimStep"/> while leaving or returning and 0 otherwise: what the frame policy compares.</summary>
    public int AnimFrame
    {
        get
        {
            Settle();
            return _phase is AwayPhase.Leaving or AwayPhase.Returning ? (int)((time.GetUtcNow() - _phaseAt).Ticks / AnimStep.Ticks) : 0;
        }
    }

    /// <summary>A card (the peek, the settings, the chat) is open: the cat is not quietly asleep then, so it does not go away.</summary>
    public void Engaged(bool engaged)
    {
        _engaged = engaged;
        Settle();
    }

    /// <summary>Brings the cat back (a click on the badge): the return animation, then asleep or speaking. A no-op when it is here.</summary>
    public void Return() => Wake();

    private void Wake()
    {
        Settle();
        if (_phase is AwayPhase.Leaving or AwayPhase.Gone)
        {
            // A wake while still leaving walks back from where the cat is (the smoothstep is symmetric: leave progress p is the return's 1 - p), never from out of the window.
            var from = _phase == AwayPhase.Leaving ? 1 - Math.Clamp((time.GetUtcNow() - _phaseAt).TotalSeconds / LeaveFor.TotalSeconds, 0, 1) : 0;
            _phase = AwayPhase.Returning;
            _phaseAt = time.GetUtcNow() - TimeSpan.FromSeconds(from * ReturnFor.TotalSeconds);
        }
        _quietSince = null;
        Settle();
    }

    private bool Quiet => _current is null && _badged.Count == 0 && !_hover && !_engaged;

    // Moves the away lifecycle on to now: quiet long enough leads to leaving and on to gone, a finished return to present. Called on every read and write,
    // so the model needs no timer; the host sleeps until NextWake.
    private void Settle()
    {
        Advance();
        var now = time.GetUtcNow();
        if (_phase == AwayPhase.Returning && now - _phaseAt >= ReturnFor)
        {
            _phase = AwayPhase.Present;
            _quietSince = _phaseAt + ReturnFor;
        }
        if (_phase == AwayPhase.Present)
        {
            if (!Quiet)
            {
                _quietSince = null;
            }
            else
            {
                _quietSince ??= now;
                if (_longSleep is { } sleep && now - _quietSince >= sleep)
                {
                    _phase = AwayPhase.Leaving;
                    _phaseAt = _quietSince.Value + sleep;
                    _quietSince = null;
                }
            }
        }
        if (_phase == AwayPhase.Leaving && now - _phaseAt >= LeaveFor)
        {
            _phase = AwayPhase.Gone;
            _phaseAt += LeaveFor;
        }
    }

    /// <summary>The notice whose bubble shows now, or null. Reading it first lets a finished good news go.</summary>
    public BuddyNotice? Current
    {
        get
        {
            Advance();
            return _current;
        }
    }

    /// <summary>Notices behind the showing one (the peek lists them).</summary>
    public IReadOnlyList<BuddyNotice> Waiting => _waiting;

    /// <summary>True while a failure is open, shown or put off with "Later": the cat's face stays grumpy, asleep too.</summary>
    public bool GrumpyFace => _openFailures.Count > 0;

    /// <summary>The ids of the open failures.</summary>
    public IReadOnlyCollection<string> OpenFailures => _openFailures;

    /// <summary>Which of the five moods the cat is in now.</summary>
    public BuddyMood Mood => Current?.Kind switch
    {
        NoticeKind.Ask => BuddyMood.Attention,
        NoticeKind.Trouble => BuddyMood.Trouble,
        NoticeKind.GoodNews => BuddyMood.GoodNews,
        // A badged card (another client shows the cards, #1357) wakes the cat to one eye open too, with no bubble.
        _ => _hover || _badged.Count > 0 ? BuddyMood.OneEyeOpen : BuddyMood.Asleep,
    };

    /// <summary>True while the pointer is over the cat: only then is the one-line summary shown, never for a badge alone.</summary>
    public bool Hovering => _hover;

    /// <summary>How many normal cards are held as a count badge instead of a bubble (a GUI shows the cards and has them).</summary>
    public int BadgeCount => _badged.Count;

    /// <summary>
    /// A normal card that another client shows: the cat wakes to one eye open with a count badge, and no bubble or toast comes of it. The same id
    /// badged twice counts once; <see cref="Clear"/> or <see cref="Unbadge"/> takes it off.
    /// </summary>
    public void Badge(string id)
    {
        _badged.Add(id);
        Wake();
    }

    /// <summary>The ids held as a badge, a copy: the feed rebuilds the set from the mirror at each fold.</summary>
    public IReadOnlyList<string> BadgedIds => [.. _badged];

    /// <summary>Takes a card off the badge (it shows as a bubble now, or is gone); true when it was on it.</summary>
    public bool Unbadge(string id)
    {
        var was = _badged.Remove(id);
        Settle();
        return was;
    }

    /// <summary>True when <paramref name="id"/> is held as a badge.</summary>
    public bool IsBadged(string id) => _badged.Contains(id);

    /// <summary>The breathing pose, 0 or 1: a step on a timer of <see cref="BreathStep"/>, not a frame loop.</summary>
    public int BreathPose => Phase == AwayPhase.Gone ? 0 : (int)((time.GetUtcNow() - _born).Ticks / BreathStep.Ticks % 2);

    /// <summary>The pointer is over the cat. It only matters while the cat sleeps; a showing bubble is not replaced by it.</summary>
    public void Hover(bool over)
    {
        _hover = over;
        if (over)
        {
            // The pointer on the badge (the only thing of a gone cat the window holds) brings it back.
            Wake();
        }
        else
        {
            Settle();
        }
    }

    /// <summary>
    /// Tells the cat something. A trouble also opens its failure (until <see cref="Clear"/>). A notice with the id of one already showing or
    /// waiting replaces it in place (a card raised again), so the queue never holds the same ask twice.
    /// </summary>
    public void Tell(BuddyNotice notice)
    {
        Advance();
        Wake();
        if (notice.Kind == NoticeKind.Trouble)
        {
            _openFailures.Add(notice.Id);
        }
        if (_current?.Id == notice.Id)
        {
            _current = notice;
            return;
        }
        var i = _waiting.FindIndex(w => w.Id == notice.Id);
        if (i >= 0)
        {
            _waiting[i] = notice;
            return;
        }
        if (_current is null)
        {
            Show(notice);
            return;
        }
        // Behind the showing one: trouble ahead of asks ahead of good news, each kind in arrival order.
        var at = _waiting.Count;
        while (at > 0 && Rank(_waiting[at - 1].Kind) > Rank(notice.Kind))
        {
            at--;
        }
        _waiting.Insert(at, notice);
    }

    /// <summary>"Later" (or the button that opened it): this notice goes, the next one shows or the cat sleeps. A trouble's failure stays open.</summary>
    public BuddyNotice? Dismiss()
    {
        Advance();
        var gone = _current;
        _current = null;
        if (gone is { } said && said.Id.StartsWith("say:", StringComparison.Ordinal))
        {
            // A failure the watchtower opened by saying it has no card to close it: it ends with its notice.
            _openFailures.Remove(said.Id);
        }
        ShowNext();
        Settle();
        return gone;
    }

    /// <summary>The thing the notice was about is over: its notice goes wherever it is, and a failure stops being open.</summary>
    public void Clear(string id)
    {
        Advance();
        _openFailures.Remove(id);
        _badged.Remove(id);
        _waiting.RemoveAll(w => w.Id == id);
        if (_current?.Id == id)
        {
            _current = null;
            ShowNext();
        }
        Settle();
    }

    /// <summary>When the model next changes by itself (good news ends, the breath turns), for the host to sleep until instead of polling.</summary>
    public DateTimeOffset NextWake
    {
        get
        {
            Settle();
            var now = time.GetUtcNow();
            if (_phase is AwayPhase.Leaving or AwayPhase.Returning)
            {
                // The animation: the next step, or its end, whichever comes first.
                var next = _phaseAt + AnimStep * ((now - _phaseAt).Ticks / AnimStep.Ticks + 1);
                var end = _phaseAt + (_phase == AwayPhase.Leaving ? LeaveFor : ReturnFor);
                return next < end ? next : end;
            }
            if (_phase == AwayPhase.Gone)
            {
                // Nothing changes by itself while gone: the host sleeps to its own deadline until an event (a hover, a click, a card) wakes it.
                return now + LongestIdle;
            }
            var breath = _born + BreathStep * ((now - _born).Ticks / BreathStep.Ticks + 1);
            if (_current is { Kind: NoticeKind.GoodNews })
            {
                var ends = _shownAt + GoodNewsFor;
                breath = ends < breath ? ends : breath;
            }
            // The long quiet sleep ends here: the leaving animation starts then, so the host must be awake for it.
            if (_quietSince is { } since && _longSleep is { } sleep && since + sleep is var leaves && leaves < breath)
            {
                breath = leaves;
            }
            return breath;
        }
    }

    private static int Rank(NoticeKind kind) => kind switch
    {
        NoticeKind.Trouble => 0,
        NoticeKind.Ask => 1,
        _ => 2,
    };

    private void Advance()
    {
        // Good news is the only thing that ends by itself; an ask or a trouble waits for the person.
        if (_current is { Kind: NoticeKind.GoodNews } && time.GetUtcNow() - _shownAt >= GoodNewsFor)
        {
            _current = null;
            ShowNext();
        }
    }

    private void ShowNext()
    {
        if (_waiting.Count == 0)
        {
            return;
        }
        var next = _waiting[0];
        _waiting.RemoveAt(0);
        Show(next);
    }

    private void Show(BuddyNotice notice)
    {
        _current = notice;
        _shownAt = time.GetUtcNow();
    }
}
