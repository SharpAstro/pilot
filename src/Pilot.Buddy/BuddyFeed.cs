using Pilot.Ui;
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

/// <summary>One row of the hover list: an open card the badge counts. <paramref name="Link"/> is what a press hands the GUI.</summary>
public sealed record HoverRow(string Title, string Where, string Age, string? Link);

/// <summary>
/// What the hover says: <paramref name="Headline"/> ("2 need you" or "Nothing needs you right now"), <paramref name="Detail"/> (the working line, and Grump's
/// "Go away." with nothing open) and the <paramref name="Rows"/> the badge counts.
/// </summary>
public sealed record HoverView(string Headline, string Detail, IReadOnlyList<HoverRow> Rows)
{
    /// <summary>The whole hover as one string: the headline, a colon and the rows when there are some, else the headline and the detail.</summary>
    public string Text => Rows.Count == 0 ? $"{Headline}. {Detail}" : $"{Headline}: {string.Join("; ", Rows.Select(r => r.Title))}. {Detail}";
}

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

    private readonly NodeMirror _mirror = new() { Slim = true };
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

    /// <summary>
    /// The feed told the cat of card <paramref name="cardId"/> and still owns its notice: the card closing clears it. A card the feed did not tell
    /// (resolved already, or its kind of wake is off in the settings) is not the feed's to clear.
    /// </summary>
    public bool IsTold(string cardId) => _told.ContainsKey(cardId);

    /// <summary>The node's name for the peek rows (its machine code); the rows of other nodes come with the watchtower's own summary, not from here.</summary>
    public string NodeName { get; set; } = "node";

    /// <summary>
    /// How many nodes the peek header counts. The buddy reads only its own node's stream, so nothing outside the tests sets it and the header says one node:
    /// the design's multi-node peek (the peers' sessions in the list) is not built yet.
    /// </summary>
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
    public bool ShowsCards => _windowVisible && !settings().IsSnoozed(time.GetUtcNow()) && settings().WakesForAsk;

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
        // #1381 section 10: the badge is what the mirror's open Ring cards say now, rebuilt at each fold (and so on a reconnect snapshot), never only grown:
        // a badge for a card that is not open in the mirror (closed while disconnected, its wake switched off) is taken off.
        foreach (var id in model.BadgedIds)
        {
            var cardId = id.StartsWith("card:", StringComparison.Ordinal) ? id["card:".Length..] : id;
            if (!_mirror.Cards.TryGetValue(cardId, out var held) || held.Bin != CardBin.Ring || held.State != CardState.Open)
            {
                _told.Remove(cardId);
                model.Unbadge(id);
            }
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

    /// <summary>What the hover says when nothing is open, and Grump's line after it.</summary>
    public const string NothingOpen = "Nothing needs you right now", GoAway = "Go away.";

    /// <summary>
    /// The hover's text, built from the cat's own state (the open Ring cards the feed folds and the sessions it mirrors), never from the first line of the
    /// watchtower's summary (that is the heading "## Needs you", #1381 section 9). Never a bare heading.
    /// </summary>
    public string Summary => Hover().Text;

    /// <summary>
    /// The hover view: the headline and the list of what the count badge counts, one row per open Ring card (title, flight or leg, age), newest first.
    /// With none open it is "Nothing needs you right now", the working line and "Go away.".
    /// </summary>
    public HoverView Hover()
    {
        var now = time.GetUtcNow();
        var open = _mirror.Cards.Values.Where(c => c.Bin == CardBin.Ring && c.State == CardState.Open).OrderByDescending(c => c.RungAt).ToList();
        var working = Peek().Rows.Count(r => r.State == PeekState.Working);
        var working1 = working == 0 ? "No sessions are working" : $"{working} {(working == 1 ? "session" : "sessions")} working on {NodeCount} {(NodeCount == 1 ? "node" : "nodes")}";
        if (open.Count == 0)
        {
            return new HoverView(NothingOpen, working1 + ". " + GoAway, []);
        }
        var rows = open.Select(c => new HoverRow(c.Title, WhereOf(c.Subject), "since " + Duration(now - c.RungAt), CardLink(c.Id))).ToList();
        var headline = $"{open.Count} {(open.Count == 1 ? "needs" : "need")} you";
        return new HoverView(headline, working1 + ".", rows);
    }

    /// <summary>"flight TIA1611 leg parser" for a card's subject target (<c>flight/TIA1611/parser</c>); a session or other subject says its own kind.</summary>
    private static string WhereOf(string subject)
    {
        var parts = subject.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => string.Empty,
            1 => parts[0],
            2 => $"{parts[0]} {parts[1]}",
            _ => parts[0] == "flight" ? $"flight {parts[1]} leg {parts[2]}" : string.Join(' ', parts),
        };
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

    /// <summary>True when the buddy follows a node other than the machine's own, so a row's link carries <c>?node=</c> and the GUI shows that node first.</summary>
    public bool OtherNode { get; init; }

    private string? LinkOf(SessionRow session)
    {
        // The open Ring card about this session is what a toast would have opened.
        var card = _mirror.Cards.Values.FirstOrDefault(c => c.State == CardState.Open && c.Bin == CardBin.Ring && (c.Subject == session.Id || c.Subject == "session/" + session.Id));
        var node = OtherNode ? NodeName : null;
        if (card is not null)
        {
            return CardLink(card.Id) + (node is { Length: > 0 } ? "?node=" + Uri.EscapeDataString(node) : string.Empty);
        }
        // Else its flight (and leg), else the session itself.
        return session.Flight is { Length: > 0 } flight
            ? PilotLink.ForFlight(flight, session.Leg is { Length: > 0 } leg ? leg : null, node)
            : PilotLink.ForSession(session.Id, node);
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
