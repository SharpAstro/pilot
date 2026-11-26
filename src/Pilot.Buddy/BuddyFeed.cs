using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>What a peek row's session is doing: asking comes first, then working, then idle.</summary>
public enum PeekState
{
    /// <summary>The session waits on the person.</summary>
    Asking,

    /// <summary>The session is taking a turn.</summary>
    Working,

    /// <summary>Running, its turn over.</summary>
    Idle,
}

/// <summary>One row of the peek card: which node, what it does, and how it stands. <paramref name="Link"/> is what a press hands the GUI.</summary>
public sealed record PeekRow(PeekState State, string Node, string What, string Status, string? Link);

/// <summary>The peek card's content: the header's counts and the rows, asking first.</summary>
public sealed record PeekView(int Nodes, int Sessions, IReadOnlyList<PeekRow> Rows)
{
    /// <summary>"Watchtower · 3 nodes · 4 sessions", as the design words it (singular for one).</summary>
    public string Header => $"Watchtower · {Nodes} {(Nodes == 1 ? "node" : "nodes")} · {Sessions} {(Sessions == 1 ? "session" : "sessions")}";
}

/// <summary>
/// What the buddy knows, from the node's own event stream (the one Pilot.Gui reads: a <see cref="SnapshotEvent"/> and its deltas, no polling). It folds
/// each event into a <see cref="NodeMirror"/>, then turns what changed into the <see cref="BuddyModel"/>'s notices: an open Ring card by its
/// <see cref="ToastTier"/> (urgent kinds are trouble, the rest asks), a PR merged or a flight landed as good news, a card that closed (or a failure that
/// went) clears its notice so the cat sleeps again. <see cref="BuddySettings"/> decide what wakes it, and a snooze holds everything back until it ends.
/// <para>Pure: no thread, no clock but the <see cref="TimeProvider"/>, no node. The host calls <see cref="Apply"/> from its stream's reader.</para>
/// </summary>
public sealed class BuddyFeed(BuddyModel model, TimeProvider time, Func<BuddySettings> settings)
{
    /// <summary>The notice id of the card <paramref name="cardId"/>; the model clears by it.</summary>
    public static string CardNoticeId(string cardId) => "card:" + cardId;

    private readonly NodeMirror _mirror = new();
    private readonly Dictionary<string, DateTimeOffset> _told = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _words = new(StringComparer.Ordinal);
    private readonly HashSet<string> _landed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _merged = new(StringComparer.Ordinal);
    private bool _primed;
    private string? _down;
    private bool _othersShowCards;
    private bool _windowVisible = true;
    private bool? _saidShowsCards;

    /// <summary>The copy of the node's state the feed reads.</summary>
    public NodeMirror Mirror => _mirror;

    /// <summary>The node's name for the peek rows (its machine code); the rows of other nodes come with the watchtower's own summary, not from here.</summary>
    public string NodeName { get; set; } = "node";

    /// <summary>How many nodes the peek header counts: this one, plus the peers the host reads.</summary>
    public int NodeCount { get; set; } = 1;

    /// <summary>
    /// True while another client (a GUI) shows the cards, as the node said on the stream (an <see cref="OthersShowCardsEvent"/>, from its PageViews): a
    /// normal card is then a count badge on the cat, not a bubble (#1357's notification rule). The buddy never guesses it.
    /// </summary>
    public bool OthersShowCards => _othersShowCards;

    /// <summary>
    /// False while the window is hidden or closing. With the snooze it decides <see cref="ShowsCards"/>. Set by the host; the feed says the change
    /// through <see cref="ShowsCardsChanged"/>.
    /// </summary>
    public bool WindowVisible
    {
        get => _windowVisible;
        set
        {
            _windowVisible = value;
            SayShowsCards();
        }
    }

    /// <summary>
    /// Whether the buddy shows the cards to the person: its window is visible and not snoozed. It is what the buddy says to the node (as Pilot.Gui does), so
    /// the node's Toaster mutes a normal card for it and toasts an urgent one regardless.
    /// </summary>
    public bool ShowsCards => _windowVisible && !settings().IsSnoozed(time.GetUtcNow());

    /// <summary>Called, with the gate held, when <see cref="ShowsCards"/> changes (and for the first time it is known): the node link says it over the stream.</summary>
    public Action<bool>? ShowsCardsChanged { get; set; }

    /// <summary>Says <see cref="ShowsCards"/> again through <see cref="ShowsCardsChanged"/>: the stream is new, and a node forgets what a client said with its stream.</summary>
    public void ShowsCardsAgain()
    {
        _saidShowsCards = null;
        SayShowsCards();
    }

    private void SayShowsCards()
    {
        var shows = ShowsCards;
        if (_saidShowsCards != shows)
        {
            _saidShowsCards = shows;
            ShowsCardsChanged?.Invoke(shows);
        }
    }

    /// <summary>Folds one event of the stream in, then updates the model from what the state now says.</summary>
    public void Apply(NodeEvent nodeEvent)
    {
        _down = null;
        _mirror.Apply(nodeEvent);
        if (nodeEvent is OthersShowCardsEvent others)
        {
            _othersShowCards = others.Shown;
        }
        Refresh();
    }

    /// <summary>The stream broke: the chat input is disabled with <paramref name="reason"/> until an event arrives again. What others show is unknown until the node says it again.</summary>
    public void NodeUnreachable(string reason)
    {
        _down = reason;
        _othersShowCards = false;
        Refresh();
    }

    /// <summary>Re-reads the state against the settings (after a settings change or a snooze that ended), without a new event.</summary>
    public void Refresh()
    {
        var s = settings();
        var snoozed = s.IsSnoozed(time.GetUtcNow());
        FoldCards(s, snoozed);
        FoldGoodNews(s, snoozed);
        _primed = true;
        SayShowsCards();
    }

    /// <summary>
    /// The watchtower has spoken of card <paramref name="cardId"/>: its words replace the card's title and summary in the bubble. A bubble already
    /// showing for the card is replaced in place.
    /// </summary>
    public void Spoke(string cardId, string words)
    {
        _words[cardId] = words;
        if (_told.ContainsKey(cardId) && !model.IsBadged(CardNoticeId(cardId)) && _mirror.Cards.TryGetValue(cardId, out var card) && card.State == CardState.Open)
        {
            model.Tell(NoticeOf(card));
        }
    }

    private void FoldCards(BuddySettings s, bool snoozed)
    {
        foreach (var card in _mirror.Cards.Values)
        {
            var id = CardNoticeId(card.Id);
            if (card.Bin != CardBin.Ring || card.State != CardState.Open)
            {
                if (_told.Remove(card.Id))
                {
                    model.Clear(id);
                }
                continue;
            }
            var urgent = ToastTier.Of(card.Kind) == ToastTier.Urgent;
            var wakes = urgent ? s.WakesForFailed : s.WakesForAsk;
            if (!wakes)
            {
                // Switched off after it showed: it goes, and a trouble's failure with it.
                if (_told.Remove(card.Id))
                {
                    model.Clear(id);
                }
                continue;
            }
            if (snoozed)
            {
                continue;
            }
            // #1357: while another client shows the cards, a normal card is the cat's count badge, no bubble (it reaches the person there, once). An urgent
            // card is the cat's alert as ever. A badge turns into the bubble when no other client shows the cards any more.
            var badge = !urgent && _othersShowCards;
            var rung = _told.TryGetValue(card.Id, out var rungAt) && rungAt == card.RungAt;
            var badged = model.IsBadged(id);
            if (rung && !(badged && !badge))
            {
                continue;
            }
            _told[card.Id] = card.RungAt;
            if (badge)
            {
                model.Badge(id);
                continue;
            }
            model.Unbadge(id);
            model.Tell(NoticeOf(card));
        }
        // A card the mirror no longer holds at all.
        foreach (var gone in _told.Keys.Where(k => !_mirror.Cards.ContainsKey(k)).ToList())
        {
            _told.Remove(gone);
            model.Clear(CardNoticeId(gone));
        }
    }

    private BuddyNotice NoticeOf(CardRow card)
    {
        var urgent = ToastTier.Of(card.Kind) == ToastTier.Urgent;
        var body = _words.TryGetValue(card.Id, out var words) ? words : card.Summary ?? card.Detail ?? string.Empty;
        return new BuddyNotice(CardNoticeId(card.Id), urgent ? NoticeKind.Trouble : NoticeKind.Ask, card.Title, body);
    }

    private void FoldGoodNews(BuddySettings s, bool snoozed)
    {
        foreach (var flight in _mirror.Flights.Values)
        {
            if (flight.State == FlightState.Landed && _landed.Add(flight.Code) && _primed && s.WakesForMerged && !snoozed)
            {
                model.Tell(new BuddyNotice("landed:" + flight.Code, NoticeKind.GoodNews, $"Flight {flight.Code} landed", flight.Title ?? flight.Repo));
            }
        }
        foreach (var merge in _mirror.Merges.Values)
        {
            if (merge.State == MergeEntryState.Merged && _merged.Add(merge.Flight) && _primed && s.WakesForMerged && !snoozed)
            {
                var what = merge.Pr is { } pr ? $"PR #{pr} merged in {merge.Repo}" : $"Flight {merge.Flight} merged in {merge.Repo}";
                model.Tell(new BuddyNotice("merged:" + merge.Flight, NoticeKind.GoodNews, what, $"Flight {merge.Flight} landed. Back to sleep in {(int)BuddyModel.GoodNewsFor.TotalSeconds} s."));
            }
        }
    }

    /// <summary>The one-line summary the hover shows: the first line of the fleet summary the watchtower keeps, or null before it has one.</summary>
    public string? Summary
    {
        get
        {
            var row = _mirror.Summaries.TryGetValue(SummaryRow.FleetScope, out var fleet) ? fleet : _mirror.Summaries.Values.FirstOrDefault();
            return row?.Markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.TrimStart('#', ' ', '*', '-');
        }
    }

    /// <summary>
    /// Why the chat input is disabled, in plain words, or null when it can send: the node is not answering, or the watchtower is not running. The
    /// input stays on screen with this as its text.
    /// </summary>
    public string? ChatDisabledReason
    {
        get
        {
            if (_down is not null)
            {
                return $"The node is not answering: {_down}";
            }
            if (_mirror.Stream is null)
            {
                return "Connecting to the node.";
            }
            if (_mirror.Node?.Watchtower is { State: SessionState.Ended or SessionState.Failed } health)
            {
                return health.LastEndedBecause is { Length: > 0 } because ? $"The watchtower is not running: {because}" : "The watchtower is not running.";
            }
            return null;
        }
    }

    /// <summary>The peek card's rows: sessions that ask first, then working, then idle, each group in the order of how long it has been so.</summary>
    public PeekView Peek()
    {
        var now = time.GetUtcNow();
        var rows = new List<(PeekRow Row, DateTimeOffset Since)>();
        foreach (var session in _mirror.Sessions.Values)
        {
            if (session.Archived || session.Gone || session.State is SessionState.Ended or SessionState.Failed)
            {
                continue;
            }
            var asking = session.State == SessionState.Waiting || session.Asking is { Length: > 0 };
            var state = asking ? PeekState.Asking : session.State == SessionState.Working ? PeekState.Working : PeekState.Idle;
            var since = session.WaitingSince ?? session.LastRequestAt ?? session.StartedAt;
            var status = state switch
            {
                PeekState.Asking => "asks you",
                PeekState.Working => "working " + Duration(now - since),
                _ => "idle",
            };
            rows.Add((new PeekRow(state, NodeName, What(session), status, LinkOf(session)), since));
        }
        var ordered = rows.OrderBy(r => r.Row.State).ThenBy(r => r.Since).Select(r => r.Row).ToList();
        return new PeekView(NodeCount, ordered.Count, ordered);
    }

    private string? LinkOf(SessionRow session)
    {
        // The open Ring card about this session is what a toast would have opened.
        var card = _mirror.Cards.Values.FirstOrDefault(c => c.State == CardState.Open && c.Bin == CardBin.Ring && (c.Subject == session.Id || c.Subject == "session/" + session.Id));
        return card is null ? null : CardLink(card.Id);
    }

    /// <summary>The link a #1356 toast carries for a card (<c>pilot://card/&lt;id&gt;</c>).</summary>
    public static string CardLink(string cardId) => $"pilot://card/{Uri.EscapeDataString(cardId)}";

    private static string What(SessionRow session)
    {
        if (session.Flight is { Length: > 0 } flight)
        {
            return session.Leg is { Length: > 0 } leg ? $"flight {flight} leg {leg}" : $"flight {flight}";
        }
        if (session.Pr is { } pr && session.Role is SessionRole.Reviewer or SessionRole.Fixer)
        {
            return $"review of PR #{pr}";
        }
        return session.Name;
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalHours} h {span.Minutes} min";
}
