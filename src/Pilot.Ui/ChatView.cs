using System.Collections.Immutable;
using DIR.Lib;
using Pilot.Contracts;
using static Pilot.Ui.TranscriptEntryView;

namespace Pilot.Ui;

/// <summary>
/// The Chat page: the conversation with the node's watchtower as one chat, read from the node's chat feed (<see cref="Load"/>,
/// which spans every watchtower there has been) and followed live while the page is open, pinned to its end until scrolled up
/// (<see cref="ScrollAnchor.Bottom"/>), reading only what came after what it has. The person's messages and the replies (in
/// Markdown) are the body; each tool call is one folded line, as on the Sessions page, a divider says where a fresh
/// watchtower began, a working… line says it is at work, what was said and not yet taken waits dimmed, and a question it asked
/// has its choices as buttons, a click on one saying it. A box at the foot sends through <see cref="OnSend"/>
/// (the node's tell): Enter sends, Shift+Enter starts a new line. A refusal is said in the chat itself.
/// </summary>
internal sealed partial class ChatView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    /// <summary>How often an open page asks for what came after.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>How many notices (a choice that did not go, a refused restart) the chat keeps showing.</summary>
    internal const int NoticesKept = 3;

    private const float Gutter = 16f;

    // The status line under the box (how fast the watchtower answers): its text and the row it takes.
    private const float StatusSize = 11f;
    private const float StatusHeight = 16f;

    // The question's card and the feed share the page's height by these weights, the card taking its content up to its share
    // and never under AskMinHeight, so a long question with many choices on a short window cannot squeeze the feed to nothing
    // or push the box off the page: past its share the question's text scrolls on its own, the choices under it always in view.
    private const float AskWeight = 35f;
    private const float FeedWeight = 65f;
    private const float AskPadY = 6f;

    // The choices under the question's text, and the least the text keeps: a header and a line. The card's floor holds those
    // and two rows of choices, so a short window never draws a row of pills past the card.
    private const float ChoiceHeight = 24f;
    private const float ChoiceGap = 8f;
    private const float ChoiceLineGap = 4f;
    private const float AskTextMinHeight = 40f;
    private const float AskMinHeight = 2 * AskPadY + AskTextMinHeight + ChoiceGap + 2 * ChoiceHeight + ChoiceLineGap;

    private static RGBAColor32 PageBg => TranscriptPane.Ground;
    private static RGBAColor32 FootBg => Palette.Current.RowAltBg;
    private static RGBAColor32 Marked => Palette.Current.Marked;

    // What the follow has read, as one immutable record swapped whole: the follow appends on the pool, the render thread
    // reads the record it finds, and neither ever waits on the other.
    // Ids is the stable id (TranscriptEntry.Id) of each held entry and the feed index it was read at, kept beside Entries in the
    // same record so the two always agree: the follow keys on the id, since a feed index shifts when the feed gains a divider.
    // Epoch names the feed's indexing the entries were read under: a token swapped for a new one whenever the held list's indices
    // may all have moved (a reread, another node's feed). The render thread's row caches are keyed by feed index, so it forgets
    // them when the Epoch of the list it is about to draw is not the one it last drew. It travels with the list it belongs to, so
    // a render that still holds the old list neither takes the signal away from the next render nor draws new rows from old caches.
    private sealed record Held(ImmutableList<TranscriptEntry> Entries, ImmutableDictionary<string, int> Ids, int Total, string? Problem, bool Working, string? Question, string[] Choices, string[] Queued, string?[] QueuedIds, string? Partial, string? Doing, string[] QueuedBehind, ChatTiming[] Timings, ChatAsk[] Asks, object Epoch)
    {
        public static readonly Held Empty = Fresh();

        // An empty list under an epoch of its own.
        public static Held Fresh() => new([], ImmutableDictionary<string, int>.Empty, 0, null, false, null, [], [], [], null, null, [], [], [], new object());

        /// <summary>Whether a page of the feed has been read into it: until then the chat says it is loading, not that nothing was said.</summary>
        public bool Read { get; init; }
    }

    private Held _held = Held.Empty;
    private CancellationTokenSource? _following;

    // Completed to wake the follow's pause at once: a fresh one is swapped in at each look, so a wake during a read is not lost.
    private TaskCompletionSource _nudge = NewNudge();

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Bottom };
    private readonly TranscriptEntryView _entries = new() { PromptLabel = "you", AssistantLabel = "watchtower", AssistantLines = int.MaxValue };
    // The Held.Epoch _entries' index-keyed caches were last built under: a render clears them first when the list it draws has another.
    private object _rowsEpoch = Held.Empty.Epoch;
    private readonly ChatComposer _composer = new();

    // The question card's own scroll, fresh for each question so a new one starts at its top.
    // What the render reads of the held entries on every frame, kept for the list it was made from and carried on as entries are appended,
    // so a frame with nothing new asks nothing of the whole transcript (PIL907): the entries as the chat shows them (a thinking entry left
    // out, a card's quote folded in; each one the same record frame after frame, so its row is the same node), the ids and words the
    // outbox is matched against, and where the person and the watchtower last spoke. Read and changed on the render thread only.
    private sealed class Shown
    {
        public required object Epoch;
        public int Count;
        public readonly List<TranscriptEntry> Quoted = [];
        public readonly HashSet<string> SendIds = new(StringComparer.Ordinal);
        public readonly List<string> UnnamedUserTexts = [];
        public TranscriptEntry? LastAssistant;
        public int LastAssistantAt = -1;
        public int LastPersonAt = -1;
    }

    private Shown? _shown;

    // The held entries' derived view: the one kept when the epoch is the same (the held indices have not moved, so the list only grew)
    // and brought up to the entries appended since, else made anew.
    private Shown ShownOf(Held held)
    {
        var shown = _shown;
        if (shown is null || !ReferenceEquals(shown.Epoch, held.Epoch) || shown.Count > held.Entries.Count)
        {
            shown = _shown = new Shown { Epoch = held.Epoch };
        }
        for (var i = shown.Count; i < held.Entries.Count; i++)
        {
            var entry = held.Entries[i];
            if (entry.Kind != TranscriptKind.Thinking)
            {
                shown.Quoted.Add(AsQuoted(entry));
            }
            if (entry.Kind == TranscriptKind.Assistant)
            {
                shown.LastAssistant = entry;
                shown.LastAssistantAt = i;
            }
            if (entry is { Kind: TranscriptKind.User, From: null, Note: false })
            {
                shown.LastPersonAt = i;
            }
            if (entry.Kind == TranscriptKind.User)
            {
                if (entry.SendId is { } sendId)
                {
                    shown.SendIds.Add(sendId);
                }
                else
                {
                    shown.UnnamedUserTexts.Add(entry.Text);
                }
            }
        }
        shown.Count = held.Entries.Count;
        return shown;
    }

    private ListScrollController _askScroll = NewAskScroll();
    private string? _askedKey;

    // A choice that did not go or a restart that was refused, said in the chat in red until the next one goes (a typed message
    // that did not go stays in the outbox as a failed line). Swapped whole from the pool.
    private ImmutableList<string> _notices = [];

    /// <summary>The chat's node-bound buttons and sends: working at once, no second press, a refusal said in the chat (<see cref="Presses"/>).</summary>
    internal Presses Presses { get; } = new();

    // What the person sent that the feed may not show yet, in the order it was sent: queued behind the ones before it, sending while
    // the node has it, then kept (sent) until the feed carries it. Each entry holds the send it was made for, resolved at Enter, so
    // a later switch of node or quote does not redirect it. One drain delivers them one after another.
    private ImmutableList<Out> _outbox = [];
    private int _draining;

    private enum OutState { Queued, Sending, Sent, Failed }

    private sealed record Out(Guid Id, string Text, Func<string, Task<string?>> Send, string Key, string Hit, OutState State, TaskCompletionSource Done, Quoted? About = null, string? Reason = null, string? Where = null)
    {
        // The id this send goes to the node with (its request id), which the feed's entry and queued line for it carry back.
        public string SendId => Id.ToString("N");
    }

    private static TaskCompletionSource NewNudge() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ListScrollController NewAskScroll() => new() { Mode = ScrollBarMode.None };

    /// <summary>
    /// Reads the chat feed after an entry (its last entries, for -1): the node's chat route. Null leaves the page empty and
    /// read-only, as a snapshot file's is.
    /// </summary>
    public Func<int, CancellationToken, Task<ChatPage>>? Load { get; set; }

    /// <summary>
    /// Says a message to the watchtower: null when the node took it, else what it said in refusing it. Null leaves the page
    /// without its box and its buttons, as a snapshot file's is.
    /// </summary>
    public Func<string, Task<string?>>? OnSend { get; set; }

    /// <summary>
    /// Says words about a card to the watchtower (the node's <c>tell card/id</c>), given the card, its node (null: this window's)
    /// and the words: null when the node took it, else what it said in refusing it. Null sends a message about a card as a plain one.
    /// </summary>
    public Func<CardRow, NodeMark?, string, Task<string?>>? OnSendAboutCard { get; set; }

    /// <summary>
    /// Reads another node's chat feed, given that node: its client's chat route. Null leaves another node's chat unread
    /// (<see cref="SwitchTo"/> then shows a problem rather than this node's feed).
    /// </summary>
    public Func<NodeMark, int, CancellationToken, Task<ChatPage>>? LoadOn { get; set; }

    /// <summary>Says a message to another node's watchtower, given that node: null when it took it, else what it said in refusing it.</summary>
    public Func<NodeMark, string, Task<string?>>? OnSendOn { get; set; }

    /// <summary>
    /// Cancels the turn the watchtower is on so the queued message with the given send id is answered next (the node's <c>chat-now</c>):
    /// null when the node did it, else what it said in refusing it. Null leaves a queued line without its Answer now.
    /// </summary>
    public Func<string, Task<string?>>? OnAnswerNow { get; set; }

    /// <summary>Cancels the turn another node's watchtower is on for a queued message, given that node and the send id; as <see cref="OnAnswerNow"/>.</summary>
    public Func<NodeMark, string, Task<string?>>? OnAnswerNowOn { get; set; }

    // The node the conversation is with; null is this window's own. Swapped whole.
    private NodeMark? _node;

    /// <summary>The node whose watchtower the page talks to; null when it is this window's own node's.</summary>
    public NodeMark? Node => Volatile.Read(ref _node);

    /// <summary>
    /// Points the page at <paramref name="node"/>'s watchtower (null, or this window's own: this node's): what is held, the
    /// notices and a quoted card are dropped, and an open page reads that node's feed from its start.
    /// </summary>
    public void SwitchTo(NodeMark? node)
    {
        node = node is { Here: false } ? node : null;
        if (Volatile.Read(ref _node)?.Where == node?.Where)
        {
            return;
        }
        var open = _following is not null;
        Close();
        Volatile.Write(ref _node, node);
        // Another feed, another indexing: a fresh epoch, so no cache built for the old node's indices is drawn from.
        Volatile.Write(ref _held, Held.Fresh());
        Volatile.Write(ref _notices, []);
        // What was sent for the old node stays in the outbox and is delivered to it; its page draws only its own entries, so a
        // failed line is back with Retry and Edit when the person returns. Only what is done with (sent) goes.
        RemoveFromOutbox(m => m.State == OutState.Sent);
        Volatile.Write(ref _about, null);
        _confirmRestart = false;
        if (open)
        {
            Open();
        }
        Changed?.Invoke();
    }

    /// <summary>The name of the node whose watchtower the page talks to: the chosen node's, else this window's own (its mark, else its machine).</summary>
    internal string WatchtowerOwner => Node?.Name ?? Nodes?.Choices.FirstOrDefault(c => c.Here)?.Name ?? Environment.MachineName;

    /// <summary>The page's name: the fleet chat, held by this node's watchtower, else the chosen node's watchtower.</summary>
    internal string Title => Node is { } on ? $"watchtower · {on.Name}" : "fleet chat";

    /// <summary>Whether another node's chat can be read from here; null asks <see cref="LoadOn"/>. A node that cannot says so on its pill.</summary>
    public Func<NodeMark, bool>? CanRead { get; set; }

    /// <summary>Whether <paramref name="node"/>'s chat can be shown here: this node's always, another's as <see cref="CanRead"/> says.</summary>
    public bool Readable(NodeMark node) => node.Here || (CanRead?.Invoke(node) ?? LoadOn is not null);

    // The top bar's node pills on this page: one choice of watchtower, the current one lit; none while there is one node.
    private List<Layout.Node> NodePills()
    {
        var pills = new List<Layout.Node>();
        if (Nodes is not { Choices.Count: > 1 } nodes)
        {
            return pills;
        }
        var night = Night?.Palette;
        var lit = night is null ? NodeFilter.Lit : (night.RowFocusBg, night.ButtonHover);
        (RGBAColor32, RGBAColor32)? plain = night is null ? null : (night.Button, night.ButtonHover);
        var current = Node?.Where;
        foreach (var choice in nodes.Choices)
        {
            var chosen = choice.Here ? current is null : choice.Where == current;
            var down = nodes.Down.TryGetValue(choice.Key, out var since);
            var label = NodeFilter.Label(choice, since, down);
            if (!Readable(choice))
            {
                label += " · chat not readable";
            }
            var picked = choice;
            pills.Add(CardsView<TSurface>.HeaderPill(label, $"chat:node:{choice.Key}", () => SwitchTo(picked), chosen ? lit : plain, down || !Readable(choice) ? night?.Dim ?? Palette.Current.Dim : night?.Bright));
        }
        return pills;
    }

    // What the page reads and sends through now: this node's, or the chosen node's.
    private Func<int, CancellationToken, Task<ChatPage>>? CurrentLoad() => Node is { } node
        ? (after, ct) => LoadOn is { } on ? on(node, after, ct) : throw new InvalidOperationException($"{node.Name}'s chat cannot be read from here")
        : Load;

    private Func<string, Task<string?>>? CurrentSend() => Node is { } node
        ? (OnSendOn is { } on ? text => on(node, text) : null)
        : OnSend;

    private Func<string, Task<string?>>? CurrentAnswerNow() => Node is { } node
        ? (OnAnswerNowOn is { } on ? sendId => on(node, sendId) : null)
        : OnAnswerNow;

    // The card the next message is about, quoted above the box until it goes or is dropped; swapped whole.
    private sealed record Quoted(CardRow Card, NodeMark? Node);

    private Quoted? _about;

    /// <summary>The card the next message is about, as the quote above the box holds it; null when none.</summary>
    public CardRow? QuotedCard => Volatile.Read(ref _about)?.Card;

    /// <summary>
    /// Quotes <paramref name="card"/> (raised on <paramref name="node"/>) above the box, what is sent next being about it, and
    /// gives the box the keyboard. What is typed is kept.
    /// </summary>
    public void Attach(CardRow card, NodeMark? node)
    {
        Volatile.Write(ref _about, new Quoted(card, node));
        Ui.Focus.Focus(Input);
        Changed?.Invoke();
    }

    /// <summary>Drops the card the next message was about; it is then sent as a plain one.</summary>
    public void Detach()
    {
        Volatile.Write(ref _about, null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Stops the watchtower and starts a fresh one: null when the node took it, else what it said in refusing it. A stop-gap
    /// for a watchtower gone quiet when it should not have; the node keeps one running by itself. Null leaves the page
    /// without the button.
    /// </summary>
    public Func<Task<string?>>? OnRestart { get; set; }

    // The header's New watchtower was pressed and waits for its confirm: it stops what the watchtower is doing.
    private bool _confirmRestart;

    /// <summary>Runs the task a click starts where the host tracks it (its <c>tracker.Run</c>, drained at shutdown); null leaves it to run unobserved.</summary>
    public Action<Func<Task>, string>? Track { get; set; }

    /// <summary>Called when what it shows changed, from whichever thread: the host redraws.</summary>
    public Action? Changed
    {
        get => _entries.Changed;
        set
        {
            _entries.Changed = value;
            Presses.Changed = value;
        }
    }

    /// <summary>Opens a link in a reply. Null leaves links as coloured text.</summary>
    public Action<string>? OpenLink
    {
        get => _entries.OpenLink;
        set => _entries.OpenLink = value;
    }

    /// <summary>Puts text on the clipboard: a refused command's Copy. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText
    {
        get => _entries.CopyText;
        set => _entries.CopyText = value;
    }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }
    
    /// <summary>The header's Night switch, the same on every page.</summary>
    public NightSwitch? Night { get; set; }

    /// <summary>The ? card and the update light, in the top bar.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The window's node selection, whose pills the top bar shows.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The box the message is typed in: the host focuses it to give the page the keyboard.</summary>
    public TextEditorState Input => _composer.Input;

    /// <summary>Whether the page is open and following the feed.</summary>
    public bool IsOpen => _following is not null;

    /// <summary>The entries it holds now, oldest first.</summary>
    public IReadOnlyList<TranscriptEntry> Entries => Volatile.Read(ref _held).Entries;

    /// <summary>The words of the turn the watchtower is on, as the last read carried them (not as the list shows them while held under the pointer); null with none.</summary>
    internal string? Partial => Volatile.Read(ref _held).Partial;

    /// <summary>What a choice or restart was refused with, as the chat shows it, oldest first.</summary>
    public IReadOnlyList<string> Notices => Volatile.Read(ref _notices);

    /// <summary>
    /// Takes Shift+Enter as a new line in the box while it has the keyboard: true when it did. The host offers every key
    /// here ahead of the router, which would send on Enter.
    /// </summary>
    public bool HandleNewLine(InputEvent evt) => _composer.HandleKey(evt, Ui.Focus);

    /// <summary>
    /// Puts <paramref name="text"/> in the box, the caret at its end and the keyboard in it, for the person to finish and send:
    /// what was there is replaced (Plan a leg's start of a message).
    /// </summary>
    public void Prefill(string text)
    {
        _composer.Clear();
        Input.InsertText(text);
        Ui.Focus.Focus(Input);
    }

    /// <summary>Starts following the feed (after what it already holds) until <see cref="Close"/>; does nothing while it already is.</summary>
    public void Open()
    {
        if (_following is not null || CurrentLoad() is not { } load)
        {
            return;
        }
        var following = _following = new CancellationTokenSource();
        _scroll.ScrollToEnd();
        // Started on the pool: its first stretch (finding the node's client, a first read) ran on the render thread.
        _ = Task.Run(() => FollowAsync(load, following.Token));
    }

    /// <summary>Stops following: what was read stays, and the next <see cref="Open"/> reads on from it.</summary>
    public void Close()
    {
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
    }

    /// <summary>Asks the follow to read now rather than at its next look: after a message went.</summary>
    public void Refresh() => Volatile.Read(ref _nudge).TrySetResult();

    // Whether a page after the held last index shows the feed's indices moved under what is held (see FollowAsync). An entry
    // without an Id (an older node) cannot show it, and keeps the index rule.
    private static bool Shifted(Held held, ChatPage page)
    {
        if (page.Entries.Length == 0)
        {
            return false;
        }
        var known = 0;
        foreach (var entry in page.Entries)
        {
            if (entry.Id is { } id && held.Ids.TryGetValue(id, out var at))
            {
                if (at != entry.Index)
                {
                    return true;
                }
                known++;
            }
        }
        return known == page.Entries.Length && page.Total > held.Total;
    }

    // What the follow holds once the page is in. Appends only the entries whose Id is not held (one without an Id: whose Index is
    // past the held last, as before). With `whole` the page is the feed read again from its start: it is the held list, in the order
    // the feed gives, each id once, with every index as the feed has it now.
    private static Held Merge(Held held, ChatPage page, bool whole)
    {
        var entries = whole ? ImmutableList.CreateBuilder<TranscriptEntry>() : held.Entries.ToBuilder();
        var ids = whole ? ImmutableDictionary.CreateBuilder<string, int>() : held.Ids.ToBuilder();
        var last = whole || held.Entries.Count == 0 ? int.MinValue : held.Entries[^1].Index;
        foreach (var entry in page.Entries)
        {
            if (entry.Id is { } id)
            {
                if (ids.TryAdd(id, entry.Index))
                {
                    entries.Add(entry);
                }
            }
            else if (entry.Index > last)
            {
                entries.Add(entry);
            }
        }
        // A reread may have moved every index: a new epoch tells the render to forget its index-keyed caches.
        return new Held(entries.ToImmutable(), ids.ToImmutable(), page.Total, null, page.Working, page.PendingQuestion, page.Choices ?? [], page.Queued ?? [], page.QueuedIds ?? [],
            page.Partial, page.Doing, page.QueuedBehind ?? [], page.Timings ?? [], page.Asks ?? [], whole ? new object() : held.Epoch) { Read = true };
    }

    private async Task FollowAsync(Func<int, CancellationToken, Task<ChatPage>> load, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var nudge = NewNudge();
            Volatile.Write(ref _nudge, nudge);
            try
            {
                var had = Volatile.Read(ref _held);
                var after = had.Entries.Count == 0 ? -1 : had.Entries[^1].Index;
                var page = await load(after, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var renumbered = after >= 0 && page.Total <= after;
                // The feed shifted: an entry already held comes back at another index (a divider went in below it), or the page
                // after the held last index brings nothing but entries already held while the feed grew. Its new entries sit
                // somewhere else than after the held last index, so read it all again and merge by id.
                var reread = renumbered || (after >= 0 && Shifted(had, page));
                if (reread)
                {
                    // Renumbered: the feed is shorter than what is held (an earlier watchtower's transcript is gone, so every index
                    // dropped), and asking after the old index would skip the entries that now sit below it. Read it all again.
                    page = await load(-1, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                // A reread holds the newest TranscriptBook.FirstRead entries the feed gives, as a fresh open does: the entries before
                // that window are not asked for again, and the folds opened by index cannot be matched to the new indices, so they
                // close. That is accepted: a feed that shifted (or renumbered) has no older indexing to keep them under.
                // The row caches (parsed Markdown, opened folds) are keyed by feed index, and every held entry may now sit at another
                // one: the merged list carries a new epoch (see Held), so the render thread (which owns them) forgets them before it
                // builds rows from it, and no row is drawn with its neighbour's text.
                ImmutableInterlocked.Update(ref _held, held => Merge(held, page, reread));
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                ImmutableInterlocked.Update(ref _held, held => held with { Problem = ex.Message });
                Changed?.Invoke();
            }
            try
            {
                // Until the next look, or until a message just sent asks for one sooner.
                await Task.WhenAny(Task.Delay(Every, cancellationToken), nudge.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends what is in the box, as Enter does: the message and its quote move out of the box at once into the outbox, which delivers
    /// them one after another through <see cref="OnSend"/> (<see cref="OnSendAboutCard"/> with a card quoted), each to the target it
    /// was sent to. A refusal is said in the chat. Does nothing with nothing typed or no host to send. The task ends when this
    /// message was delivered, refused or dropped.
    /// </summary>
    public Task SendAsync()
    {
        if (_composer.IsEmpty || CurrentSend() is null)
        {
            return Task.CompletedTask;
        }
        var message = _composer.Message;
        var quoted = Volatile.Read(ref _about);
        Func<string, Task<string?>>? send = null;
        // A message about a card goes through the card's tell.
        if (quoted is not null && OnSendAboutCard is { } aboutCard)
        {
            // Another node's card, asked about in a chat that is not that node's (the fleet chat, when its chat cannot be read
            // here), goes to the shown watchtower as plain words naming the card and its node: it passes the work on with tell
            // --node. In the card's own node's chat it is that node's tell card/<id>, as this node's card is here.
            if (quoted.Node is { Here: false } other && Node?.Where != other.Where && CurrentSend() is { } toWatchtower)
            {
                send = words => toWatchtower(AboutOtherNodesCard(quoted.Card, other, words));
            }
            else
            {
                send = words => aboutCard(quoted.Card, quoted.Node, words);
            }
        }
        else
        {
            quoted = null;
        }
        var queued = Enqueue(message, send ?? CurrentSend()!, SendKey, SendKey, about: quoted);
        _composer.Clear();
        if (quoted is not null)
        {
            Interlocked.CompareExchange(ref _about, null, quoted);
        }
        return queued;
    }

    private const string SendKey = "chat:send";
    private const string ChoiceKey = "chat:choice";
    private const string RestartKey = "chat:restart";

    /// <summary>What is told this node's watchtower about a card of another node: the card, its node and its title, and then the words.</summary>
    internal static string AboutOtherNodesCard(CardRow card, NodeMark node, string words) =>
        $"About card {card.Id} of node {node.Name} ({card.Title.ReplaceLineEndings(" ")}): {words}";

    /// <summary>Says <paramref name="message"/> to the watchtower: a choice button's label, or what was typed.</summary>
    public Task SendAsync(string message) => CurrentSend() is { } send ? Enqueue(message, send, SendKey, SendKey) : Task.CompletedTask;

    // Puts a message at the back of the outbox and starts the drain when it is not running; ends when this message is done with.
    private Task Enqueue(string message, Func<string, Task<string?>> send, string key, string hit, string label = "chat send", Quoted? about = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return Task.CompletedTask;
        }
        var entry = new Out(Guid.NewGuid(), message, send, key, hit, OutState.Queued, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), about, null, Node?.Where);
        ImmutableInterlocked.Update(ref _outbox, o => o.Add(entry));
        _scroll.ScrollToEnd();
        StartDrain(label);
        Changed?.Invoke();
        return entry.Done.Task;
    }

    private void StartDrain(string label)
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            var run = DrainAsync();
            Track?.Invoke(() => run, label);
        }
    }

    // Delivers the queued messages one after another, each to its own send, until none is left.
    private async Task DrainAsync()
    {
        var finished = false;
        try
        {
            while (true)
            {
                var next = Volatile.Read(ref _outbox).FirstOrDefault(m => m.State == OutState.Queued);
                if (next is null)
                {
                    Volatile.Write(ref _draining, 0);
                    // One queued between the look and the release finds no drain: take the job back.
                    if (!Volatile.Read(ref _outbox).Any(m => m.State == OutState.Queued) || Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
                    {
                        finished = true;
                        return;
                    }
                    continue;
                }
                var going = false;
                ImmutableInterlocked.Update(ref _outbox, o =>
                {
                    going = o.Any(m => m.Id == next.Id && m.State == OutState.Queued);
                    return going ? Mark(o, next.Id, OutState.Sending) : o;
                });
                if (!going)
                {
                    continue;
                }
                Changed?.Invoke();
                string? refused;
                try
                {
                    refused = await Presses.PressAsync(next.Key, next.Hit, _ =>
                    {
                        // The send reads it before its first await: the request id is the echo's, so the feed's entry for it is the same row.
                        ChatCommands.SendId.Value = next.SendId;
                        return next.Send(next.Text);
                    });
                }
                catch (Exception ex)
                {
                    // Any throw, a connect timeout (a cancellation) included, is a failed delivery: the drain has no token of its own.
                    refused = ex.Message;
                }
                try
                {
                    // A typed message that did not go stays in the conversation, failed, with its reason; a choice's refusal is a notice.
                    var kept = refused is not null && next.Key == SendKey;
                    ImmutableInterlocked.Update(ref _outbox, o => refused is null ? Mark(o, next.Id, OutState.Sent)
                        : kept ? ImmutableList.CreateRange(o.Select(m => m.Id == next.Id ? m with { State = OutState.Failed, Reason = refused } : m))
                        : o.RemoveAll(m => m.Id == next.Id));
                    if (!kept)
                    {
                        ImmutableInterlocked.Update(ref _notices, notices => refused is null
                            ? []
                            : notices.Add($"{refused} · {Clip(next.Text)}") is var more && more.Count > NoticesKept ? more.RemoveAt(0) : more);
                    }
                    Refresh();
                    Changed?.Invoke();
                }
                finally
                {
                    next.Done.TrySetResult();
                }
            }
        }
        finally
        {
            if (!finished)
            {
                Volatile.Write(ref _draining, 0);
            }
        }
    }

    private static ImmutableList<Out> Mark(ImmutableList<Out> outbox, Guid id, OutState state) =>
        ImmutableList.CreateRange(outbox.Select(m => m.Id == id ? m with { State = state } : m));

    private void RemoveFromOutbox(Func<Out, bool> which)
    {
        var gone = new List<Out>();
        ImmutableInterlocked.Update(ref _outbox, o =>
        {
            gone.Clear();
            gone.AddRange(o.Where(which));
            return o.RemoveAll(m => which(m));
        });
        foreach (var m in gone)
        {
            m.Done.TrySetResult();
        }
    }

    /// <summary>The reasons of the typed messages that did not go and are still shown as failed, oldest first.</summary>
    internal IReadOnlyList<string> Failures => [.. Volatile.Read(ref _outbox).Where(m => m.State == OutState.Failed).Select(m => m.Reason ?? "")];

    /// <summary>Sends a failed message again, to the same target, at the end of the queue.</summary>
    internal void Retry(Guid id)
    {
        var found = false;
        ImmutableInterlocked.Update(ref _outbox, o =>
        {
            var failed = o.FirstOrDefault(m => m.Id == id && m.State == OutState.Failed);
            found = failed is not null;
            return failed is null ? o : o.RemoveAll(m => m.Id == id).Add(failed with { State = OutState.Queued, Reason = null });
        });
        if (!found)
        {
            return;
        }
        _scroll.ScrollToEnd();
        StartDrain("chat retry");
        Changed?.Invoke();
    }

    /// <summary>
    /// Takes a failed message back into the box (after what is typed there, on a new line) and its card back into the quote, and
    /// removes the failed line.
    /// </summary>
    internal void Edit(Guid id)
    {
        Out? failed = null;
        ImmutableInterlocked.Update(ref _outbox, o =>
        {
            failed = o.FirstOrDefault(m => m.Id == id && m.State == OutState.Failed);
            return failed is null ? o : o.RemoveAll(m => m.Id == id);
        });
        if (failed is null)
        {
            return;
        }
        var typed = _composer.IsEmpty ? "" : _composer.Input.Text;
        _composer.Clear();
        Input.InsertText(typed.Length == 0 ? failed.Text : typed + "\n" + failed.Text);
        if (failed.About is not null && Volatile.Read(ref _about) is null)
        {
            Volatile.Write(ref _about, failed.About);
        }
        Ui.Focus.Focus(Input);
        Changed?.Invoke();
    }

    /// <summary>Drops a message still queued, never sent; one the node has or has had stays.</summary>
    internal void Drop(Guid id)
    {
        RemoveFromOutbox(m => m.Id == id && m.State == OutState.Queued);
        Refresh();
        Changed?.Invoke();
    }

    /// <summary>Asks the node for a fresh watchtower; a refusal is said in the chat as a refused message is.</summary>
    public async Task RestartAsync()
    {
        if (OnRestart is not { } restart)
        {
            return;
        }
        if (Presses.PendingOf(RestartKey) is not null)
        {
            return;
        }
        var refused = await Presses.PressAsync(RestartKey, "chat:restart-yes", _ => restart());
        // The confirm stays up while the node has it, so its working label shows; it goes with the answer.
        _confirmRestart = false;
        ImmutableInterlocked.Update(ref _notices, notices => refused is null
            ? []
            : notices.Add($"{refused} · a new watchtower") is var more && more.Count > NoticesKept ? more.RemoveAt(0) : more);
        _scroll.ScrollToEnd();
        Refresh();
        Changed?.Invoke();
    }

    private static string Clip(string message)
    {
        var line = message.ReplaceLineEndings(" ");
        return line.Length > 60 ? line[..59] + "…" : line;
    }

    /// <summary>How many rows the last frame built rather than took from the row cache (PIL907): none when nothing changed.</summary>
    internal int RowsBuilt => _entries.RowsBuilt;

    internal override Action<string>? SelectionCopy => CopyText;

    internal override RectF32 PageSelectionViewport => _scroll.Viewport;

    internal override bool SelectionBlocked => base.SelectionBlocked || About is { IsOpen: true };

    public void Render(RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        NoteHeight(bounds);
        RenderLayout(Tree(), bounds);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree()
    {
        Palette.Use(Night);
        var held = Volatile.Read(ref _held);
        _composer.Input.OnCommit = _ => SendAsync();
        _composer.Input.Placeholder = Node is { } to ? $"Message {to.Name}'s watchtower" : "Message the fleet chat";
        var canSend = CurrentSend() is not null;
        var buttons = new List<Layout.Node>();
        if (!_scroll.IsFollowingEnd)
        {
            buttons.Add(CardsView<TSurface>.HeaderPill("Jump to end", "chat:end", () =>
            {
                _scroll.ScrollToEnd();
                Changed?.Invoke();
            }));
        }
        if (Node is not null)
        {
            buttons.Add(CardsView<TSurface>.HeaderPill("Fleet chat", "chat:back", () => SwitchTo(null)));
        }
        else if (OnRestart is not null && Load is not null)
        {
            if (_confirmRestart)
            {
                buttons.Add(Presses.Button(RestartKey, "chat:restart-yes",
                    () => CardsView<TSurface>.HeaderPill("Stop it and start fresh", "chat:restart-yes", () =>
                    {
                        var restart = RestartAsync();
                        Track?.Invoke(() => restart, "restart the watchtower");
                    }),
                    pressed => CardsView<TSurface>.DeadHeaderPill(pressed ? "Restarting…" : "Stop it and start fresh")));
                buttons.Add(CardsView<TSurface>.HeaderPill("Keep it", "chat:restart-no", () =>
                {
                    _confirmRestart = false;
                    Changed?.Invoke();
                }));
            }
            else
            {
                buttons.Add(CardsView<TSurface>.HeaderPill("New watchtower", "chat:restart", () =>
                {
                    _confirmRestart = true;
                    Changed?.Invoke();
                }));
            }
        }
        var top = TopBar.Build(new PageHeader(Title)
        {
            Buttons = buttons,
            Source = held.Problem ?? (CurrentLoad() is null ? "not live" : Node is { } at ? $"the conversation with {at.Name}'s watchtower" : $"held by {WatchtowerOwner}'s watchtower"),
        }, new TopBarParts(null, null, Autopilot, About, Night, NodePills()));

        if (!ReferenceEquals(Interlocked.Exchange(ref _rowsEpoch, held.Epoch), held.Epoch))
        {
            _entries.Reset();
        }
        var derived = ShownOf(held);
        // The rows come kept, one node an entry, already stretched to the page's width.
        var rows = _entries.Rows(derived.Quoted, held.Working);
        // What was said and the watchtower has not taken yet: last among the messages, dim, and saying so.
        // One send is one row, matched by its id and never by its words, so two sends of the same words are two rows: a queued
        // line whose id an entry carries is that entry's send already taken (a stale line), and shows no more.
        var entryIds = derived.SendIds;
        var queuedIds = held.Queued.Select((_, i) => i < held.QueuedIds.Length ? held.QueuedIds[i] : null).ToArray();
        // The watchtower's reply as it is written comes before what waits behind it, in the bubble a finished reply has.
        if (ShownPartial(held, derived.LastAssistant) is { } writing)
        {
            rows.Add(_entries.Streaming(writing).WStar());
        }
        var answerNow = CurrentAnswerNow();
        for (var i = 0; i < held.Queued.Length; i++)
        {
            if (queuedIds[i] is { } stale && entryIds.Contains(stale))
            {
                continue;
            }
            // "read at its next step" (written mid-turn) or "queued behind its start" is the node's word for it. Only the line the watchtower
            // reads next can be answered now (it reads in order), and only while a turn runs: elsewhere the pill could only be refused, so it is not drawn.
            var behind = i < held.QueuedBehind.Length ? held.QueuedBehind[i] : null;
            Layout.Node? pill = null;
            Layout.Node? refusal = null;
            if (queuedIds[i] is { Length: > 0 } sendId)
            {
                var key = AnswerNowKey(sendId);
                refusal = Presses.RefusalLine(key, 12f, Failed, 3);
                if (i == 0 && held.Working && behind is not null && answerNow is not null)
                {
                    pill = Presses.Button(key, key,
                        () => CardsView<TSurface>.Pill("Answer now", key, () => AnswerNow(sendId, answerNow)),
                        pressed => CardsView<TSurface>.Dead(pressed ? "Answering…" : "Answer now"));
                }
            }
            rows.Add(Waiting(held.Queued[i], behind, pill, refusal).WStar());
        }
        // What the person sent that the feed does not carry yet: once an entry or a queued line carries its id only the feed's copy
        // shows. The words are compared only with the feed's lines that have no id (an older node's).
        var queuedCarried = queuedIds.OfType<string>().ToHashSet(StringComparer.Ordinal);
        var unnamedQueued = held.Queued.Where((_, i) => queuedIds[i] is null).ToList();
        bool InFeed(Out m) => entryIds.Contains(m.SendId) || queuedCarried.Contains(m.SendId)
            || derived.UnnamedUserTexts.Any(t => t.Contains(m.Text, StringComparison.Ordinal)) || unnamedQueued.Any(t => t.Contains(m.Text, StringComparison.Ordinal));
        var here = Node?.Where;
        ImmutableInterlocked.Update(ref _outbox, o => o.RemoveAll(m => m.Where == here && m.State == OutState.Sent && InFeed(m)));
        var place = 0;
        foreach (var mine in Volatile.Read(ref _outbox).Where(m => m.Where == here && (m.State != OutState.Sent || !InFeed(m))))
        {
            var id = mine.Id;
            var row = mine.State switch
            {
                OutState.Sending => Sent(mine.Text, "you · sending…", null),
                OutState.Queued => Sent(mine.Text, $"you · queued {++place}", CardsView<TSurface>.Pill("Drop", $"chat:drop:{id:N}", () => Drop(id))),
                OutState.Failed => FailedLine(mine.Text, mine.Reason ?? "", CardsView<TSurface>.Pill("Retry", $"chat:retry:{id:N}", () => Retry(id)), CardsView<TSurface>.Pill("Edit", $"chat:edit:{id:N}", () => Edit(id))),
                _ => Sent(mine.Text, "you", null),
            };
            rows.Add(row.WStar());
        }
        foreach (var notice in Notices)
        {
            rows.Add(Notice(notice));
        }
        // The question the watchtower waits on sits under the feed, above the box, not in it: in the scroll it was drawn over
        // the reply it ends, cutting its last lines, and scrolled away with the feed. Here it always shows, and the feed ends
        // above it.
        Layout.Node? asked = null;
        // A message from the person after the watchtower's last words answers the ask: the card closes, and only a newer ask
        // (the watchtower speaking again) reopens it.
        // Only the person's own line counts: a node note or a hand-back (Note, or From set) leaves the ask pending.
        var answeredAlready = derived.LastPersonAt >= 0 && derived.LastPersonAt > derived.LastAssistantAt;
        // What the stream watchtower waits on the person for (a permission prompt, a question) comes first: it is answered here or on its card.
        if (canSend && held.Asks.Length > 0)
        {
            asked = WaitingOnYou(held.Asks);
        }
        else if (canSend && !answeredAlready && held.Question is { Length: > 0 } question)
        {
            // The reply the question ends already says it in the feed: the card then holds only the choices.
            var said = derived.LastAssistant?.Text;
            if (_askedKey != question)
            {
                _askedKey = question;
                _askScroll = NewAskScroll();
            }
            // A card whose body fits its viewport still reads one atom of overflow when the body's height is fractional (the
            // list counts whole surface units, the content rounded up and the viewport down), which drew a full-height
            // thumb on every card. The bar waits until the body overflows by more than that, as of the last frame.
            _askScroll.Mode = _askScroll.MaxOffset > 1f ? ScrollBarMode.Interactive : ScrollBarMode.None;
            // An older node sent no choices: read them from the question text with the shared parser.
            var choices = held.Choices;
            var shown = question;
            if (choices.Length == 0)
            {
                var (head, parsed) = Pilot.Contracts.AskChoices.Split(question);
                if (parsed.Length > 0)
                {
                    choices = parsed;
                    shown = head.Length > 0 ? head : question;
                }
            }
            var saidAlready = said is not null
                && (said.Contains(question, StringComparison.Ordinal) || said.Contains(shown, StringComparison.Ordinal));
            asked = Question(shown, saidAlready, choices);
        }
        var sending = Presses.AnyPending || Volatile.Read(ref _outbox).Any(m => m.Where == here && m.State is OutState.Queued or OutState.Sending);
        if (held.Working || sending)
        {
            // What it is doing, in the node's words ("running pilot status"), else the plain "working…".
            var doing = held.Doing is { Length: > 0 } now ? now.ReplaceLineEndings(" ") : "working…";
            // Wrapped, never cut: a long tool command is in what it is doing.
            rows.Add((sending ? Layout.Builder.Text("● sending…", 13f, Running).Selectable() : WrappedProse.Of($"● {doing}", 13f, Running)).Pad(0f, 4f).WStar());
        }
        var list = rows.Count == 0
            ? Layout.Builder.Text(held.Problem ?? (CurrentLoad() is null ? "The chat is read from a live node." : !held.Read ? "Loading the conversation…" : "Nothing said yet. Tell the watchtower what you want: “let's launch a flight for issue 12”."), 13f, Dim).Selectable().Stretch()
            : Layout.Builder.VStack([.. rows]).WithGap(ToolGap).WithScroll(_scroll).Stretch();

        var page = !canSend
            ? Layout.Builder.VStack(list.HStar())
            : asked is null
                ? Layout.Builder.VStack(list.HStar(), Foot())
                // The feed and the card take their content up to their shares; a short feed leaves the room to a spacer, so the
                // card and the box stay at the page's foot.
                : Layout.Builder.VStack(list.HStarUpToContent(FeedWeight), Layout.Builder.Spacer().HStar(), asked, Foot());
        var body = page.WithGap(8f).Pad(Gutter, 8f).Stretch();
        var whole = Layout.Builder.VStack(ScrollingHead(top), body.HStar(1f, BodyFloor)).Bg(PageBg).Stretch();
        whole = WithPageSelection(whole);
        return About?.Over(whole) ?? whole;
    }

    /// <summary>
    /// A person's message about a card (<see cref="CardContext"/>'s block and then their words) as the chat shows it: the card's
    /// short quote, then the words. Any other entry as it is.
    /// </summary>
    internal static TranscriptEntry AsQuoted(TranscriptEntry entry)
    {
        if (entry.Kind != TranscriptKind.User || entry.From is not null || !CardContext.TryParse(entry.Text, out var chat))
        {
            return entry;
        }
        var quote = chat.Quote.Length > 0 && chat.Quote != chat.Title ? $"\n> {chat.Quote}" : "";
        if (chat.Cut)
        {
            // The node kept the card's start only (its text limit): say so, so the quote is not read as the whole card.
            quote += quote.Length > 0 ? " (card cut)" : "\n> (card cut)";
        }
        return entry with { Text = $"> card {chat.CardId}: {chat.Title}{quote}\n{chat.Words}" };
    }

    // A typed message that did not go: its words, the node's reason, and Retry and Edit.
    private static Layout.Node Sent(string text, string label, Layout.Node? aside)
    {
        var parts = new List<Layout.Node>
        {
            Layout.Builder.Text(label, 12f, Dim).Selectable().WFixed(96f),
            Layout.Builder.VStack([.. text.ReplaceLineEndings("\n").Split('\n').Take(6).Select(l => Layout.Builder.Text(l, 13f, Dim).Selectable())]).WStar(),
        };
        if (aside is not null)
        {
            parts.Add(aside);
        }
        return Layout.Builder.HStack([.. parts]).WithGap(10f);
    }

    // A typed message the node did not take: its words, the node's reason under them, and the ways to go on.
    private static Layout.Node FailedLine(string text, string reason, Layout.Node retry, Layout.Node edit)
    {
        var body = Layout.Builder.VStack(
            [.. text.ReplaceLineEndings("\n").Split('\n').Take(6).Select(l => Layout.Builder.Text(l, 13f, Dim).Selectable()),
                WrappedProse.Of(reason, 12f, Failed, 3)]).WStar();
        return Layout.Builder.HStack(
                Layout.Builder.Text("you · failed", 12f, Failed).Selectable().WFixed(96f),
                body,
                Layout.Builder.HStack(retry, edit).WithGap(6f))
            .WithGap(10f);
    }

    private static Layout.Node Notice(string text) =>
        Layout.Builder.HStack(
                Layout.Builder.Text("pilot", 12f, Failed).Selectable().WFixed(96f),
                WrappedProse.Of(text, 13f, Failed, 3))
            .WithGap(10f)
            .WStar();

    // The question the watchtower waits on, with its choices as buttons: a click says the choice to it, as typing it would.
    private Layout.Node Question(string question, bool saidAlready, string[] choices)
    {
        var body = new List<Layout.Node>();
        if (saidAlready)
        {
            body.Add(Layout.Builder.Text(choices.Length > 0 ? "Pick one, or answer in your own words" : "Answer in your own words", 11f, Dim).Selectable());
        }
        else
        {
            body.Add(Layout.Builder.Text("The watchtower asks", 11f, Dim).Selectable());
            // Whole, never cut: a long question scrolls inside the capped card instead.
            body.Add(WrappedProse.Of(question, 14f, Bright));
        }
        // The text alone scrolls, in what the card's share leaves it above the choices; the bar beside it and the card's padding
        // stay put. A gutter keeps the text clear of the scrollbar drawn over its right edge.
        var text = Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.VStack([.. body]).WithGap(4f).WStar(),
                        Layout.Builder.Spacer().WFixed(TranscriptEntryView.ScrollGutter))
                    .WStar()
                    .HAuto())
            .WithScroll(_askScroll)
            .WStar()
            .HStarUpToContent(1f, AskTextMinHeight);
        var parts = new List<Layout.Node> { text };
        if (choices.Length > 0)
        {
            // The choices stay under the text, outside its scroll, so they are always in view and reachable.
            parts.Add(Layout.Builder.WrapH([.. choices.Select((choice, i) => Presses.Button(ChoiceKey, $"chat:choice:{i + 1}",
                    () => CardsView<TSurface>.Pill(choice, $"chat:choice:{i + 1}", () => Choose(question, choice, $"chat:choice:{i + 1}")),
                    pressed => CardsView<TSurface>.Dead(pressed ? $"{choice} …" : choice)))])
                .WithGap(ChoiceGap).WithLineGap(ChoiceLineGap).WStar());
        }
        var scrolling = Layout.Builder.VStack([.. parts]).WithGap(ChoiceGap).WStar().HStar();
        return Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(Prompt).WFixed(3f).HStar(),
                scrolling)
            .WithGap(8f)
            .Pad(8f, AskPadY)
            .Bg(Marked)
            .WStar()
            .HStarUpToContent(AskWeight, AskMinHeight);
    }

    // The press of one queued line's Answer now: a key (and hit) of its own, so its working state and its refusal belong to that line
    // and go with it once the node has taken it.
    private static string AnswerNowKey(string sendId) => $"chat:now:{sendId}";

    // Asks the node to cancel the turn it is on so this queued line is answered next. The node's answer sets the state: the pill reads
    // "Answering…" while it has it, a refusal is said under the line, and the line goes when the feed no longer carries it.
    private void AnswerNow(string sendId, Func<string, Task<string?>> ask)
    {
        var key = AnswerNowKey(sendId);
        if (Presses.PendingOf(key) is not null)
        {
            return;
        }
        var run = AnswerNowAsync(key, sendId, ask);
        Track?.Invoke(() => run, "chat answer now");
    }

    private async Task AnswerNowAsync(string key, string sendId, Func<string, Task<string?>> ask)
    {
        await Presses.PressAsync(key, key, _ => ask(sendId));
        // Read the feed at once: the line has gone from the queue if the node did it.
        Refresh();
        Changed?.Invoke();
    }

    // The words held while the pointer is over the list: the reply in progress as it stood when the pointer came, until it leaves, so
    // the rows under the pointer do not move as the words stream in. Null while the pointer is elsewhere.
    private string? _frozenPartial;

    // The reply in progress as the list shows it, or null when there is none. A turn that has ended carries its words as an entry and no
    // Partial; a reply already held as the last entry is not drawn a second time.
    private string? ShownPartial(Held held, TranscriptEntry? lastAssistant)
    {
        if (held.Partial is not { Length: > 0 } now
            || lastAssistant is { } last && last.Text.Trim() == now.Trim())
        {
            _frozenPartial = null;
            return null;
        }
        var view = _scroll.Viewport;
        var over = Pointer is { } p && p.X >= view.X && p.X < view.X + view.Width && p.Y >= view.Y && p.Y < view.Y + view.Height;
        if (!over)
        {
            _frozenPartial = null;
            return now;
        }
        return _frozenPartial ??= now;
    }

    // A choice answers the question: said to the watchtower as the person's message, through the same send as a typed one.
    private void Choose(string question, string choice, string hit)
    {
        // A choice already queued or with the node takes no second press.
        if (CurrentSend() is not { } send || Presses.PendingOf(ChoiceKey) is not null
            || Volatile.Read(ref _outbox).Any(m => m.Key == ChoiceKey && m.State != OutState.Sent))
        {
            return;
        }
        Enqueue(Answer(question, choice), send, ChoiceKey, hit, $"chat choice {choice}");
    }

    /// <summary>
    /// What a choice says: its words, and the question they answer. Said alone, "Leave it" left the watchtower guessing which
    /// of the things it had listed "it" was (SE1, 2026-10-27).
    /// </summary>
    internal static string Answer(string question, string choice)
    {
        var asked = question.ReplaceLineEndings(" ").Trim();
        return $"{choice} (my answer to: {(asked.Length > 300 ? asked[..299] + "…" : asked)})";
    }

    // The card the next message is about, quoted above the box: its id and title in one or two lines, and a way to drop it.
    private Layout.Node Quote(CardRow card, NodeMark? node) =>
        Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(Prompt).WFixed(3f).HStar(),
                WrappedProse.Of($"about card {card.Id}{(node is null ? "" : $" · {node.Name}")}: {card.Title}", 12f, Body, QuoteLines).WStar(),
                CardsView<TSurface>.Pill("Drop", "chat:drop-card", Detach))
            .WithGap(8f)
            .Pad(0f, 3f)
            .WStar()
            .HAuto();

    /// <summary>How many lines the quoted card takes above the box.</summary>
    internal const int QuoteLines = 2;

    // The box: DIR.Lib's multi-line editor, and a line saying how it sends.
    private Layout.Node Foot()
    {
        var lines = _composer.Input.LineCount;
        var rows = new List<Layout.Node>();
        if (OnSendAboutCard is not null && Volatile.Read(ref _about) is { } quoted)
        {
            rows.Add(Quote(quoted.Card, quoted.Node));
        }
        rows.Add(Layout.Builder.TextEditor(_composer.Input, 14f, colors: Palette.Input, focusOnOpen: true).WStar());
        if (ChatStatus.Line(Volatile.Read(ref _held).Timings) is { } status)
        {
            // How fast it answers, on one line under the box: cut with an ellipsis where the window is narrow, the whole of it on hover.
            var words = status.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => (Layout.Node)Layout.Builder.Text(w + " ", StatusSize, Dim).WAuto().Selectable())
                .ToArray();
            rows.Add(Layout.Builder.WrapH(words).WithMaxLines(1, ellipsis: true).WStar().RowH(StatusHeight).WithTooltip(status, Layout.DockSide.Top));
        }
        rows.Add(Layout.Builder.Text(lines > 1 ? $"{lines} lines · Enter sends · Shift+Enter a new line" : "Enter sends · Shift+Enter a new line", 10f, Dim).RowH(16f));
        return Layout.Builder.VStack([.. rows]).Pad(12f, 6f).Bg(FootBg).WStar().HAuto();
    }

    public override bool HandleInput(InputEvent evt) => false;
}
