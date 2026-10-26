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
internal sealed class ChatView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>How often an open page asks for what came after.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>How many of the messages that were refused the chat keeps showing.</summary>
    internal const int NoticesKept = 3;

    private const float Gutter = 16f;

    // The question's card is at most this share of the page's height, and never under this many design units, so a long
    // question with many choices on a short window cannot squeeze the feed to nothing or push the box off the page: past it
    // the question's text scrolls on its own, the choices under it always in view.
    private const float AskShare = 0.35f;
    private const float AskMinHeight = 96f;
    private const float AskPadY = 6f;

    // The choices under the question's text: a pill's height, the gaps between pills and between rows, an average glyph's
    // width and a pill's padding, for reckoning how tall the wrapped choices will be before they are laid out; and the least
    // the text keeps, a header and a line.
    private const float ChoiceHeight = 24f;
    private const float ChoiceGap = 8f;
    private const float ChoiceLineGap = 4f;
    private const float ChoiceGlyphWidth = 7f;
    private const float ChoicePadX = 20f;
    private const float AskTextMinHeight = 40f;

    // The page's size in design units as of the last render, for the card's cap; what the page is assumed to be before one.
    private const float AssumedPageHeight = 600f;
    private const float AssumedPageWidth = 1200f;

    private static readonly RGBAColor32 PageBg = TranscriptPane.Ground;
    private static readonly RGBAColor32 FootBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 Marked = new(0x2a, 0x24, 0x18, 0xff);

    // What the follow has read, as one immutable record swapped whole: the follow appends on the pool, the render thread
    // reads the record it finds, and neither ever waits on the other.
    private sealed record Held(ImmutableList<TranscriptEntry> Entries, int Total, string? Problem, bool Working, string? Question, string[] Choices, string[] Queued)
    {
        public static readonly Held Empty = new([], 0, null, false, null, [], []);
    }

    private Held _held = Held.Empty;
    private CancellationTokenSource? _following;

    // Completed to wake the follow's pause at once: a fresh one is swapped in at each look, so a wake during a read is not lost.
    private TaskCompletionSource _nudge = NewNudge();

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Bottom };
    private readonly TranscriptEntryView _entries = new() { PromptLabel = "you", AssistantLabel = "watchtower", AssistantLines = int.MaxValue };
    private readonly ChatComposer _composer = new();

    // The question card's own scroll, fresh for each question so a new one starts at its top; and the page height it is capped by.
    private ListScrollController _askScroll = NewAskScroll();
    private string? _askedKey;
    private float _pageHeight = AssumedPageHeight;
    private float _pageWidth = AssumedPageWidth;

    // A message that was refused or a choice that did not go, said in the chat in red until the next one goes; and how many
    // are on their way. Swapped whole from the pool.
    private ImmutableList<string> _notices = [];
    private int _sending;

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

    /// <summary>Runs the task a click starts where the host tracks it (its <c>tracker.Run</c>, drained at shutdown); null leaves it to run unobserved.</summary>
    public Action<Func<Task>, string>? Track { get; set; }

    /// <summary>Called when what it shows changed, from whichever thread: the host redraws.</summary>
    public Action? Changed
    {
        get => _entries.Changed;
        set => _entries.Changed = value;
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

    /// <summary>The ? card and the update light, in the top bar.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The window's node selection, whose pills the top bar shows.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The box the message is typed in: the host focuses it to give the page the keyboard.</summary>
    public TextInputState Input => _composer.Input;

    /// <summary>The lines typed before the one in the box, as shown above it.</summary>
    internal IReadOnlyList<string> Before => _composer.Before;

    /// <summary>Whether the page is open and following the feed.</summary>
    public bool IsOpen => _following is not null;

    /// <summary>The entries it holds now, oldest first.</summary>
    public IReadOnlyList<TranscriptEntry> Entries => Volatile.Read(ref _held).Entries;

    /// <summary>What was refused or did not go, as the chat shows it, oldest first.</summary>
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
        Input.Text = text;
        Input.CursorPos = text.Length;
        Ui.Focus.Focus(Input);
    }

    /// <summary>Starts following the feed (after what it already holds) until <see cref="Close"/>; does nothing while it already is.</summary>
    public void Open()
    {
        if (_following is not null || Load is not { } load)
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

    private async Task FollowAsync(Func<int, CancellationToken, Task<ChatPage>> load, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var nudge = NewNudge();
            Volatile.Write(ref _nudge, nudge);
            try
            {
                var had = Volatile.Read(ref _held).Entries;
                var after = had.Count == 0 ? -1 : had[^1].Index;
                var page = await load(after, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var renumbered = after >= 0 && page.Total <= after;
                if (renumbered)
                {
                    // The feed is shorter than what is held (an earlier watchtower's transcript is gone, so every index dropped):
                    // asking after the old index would skip the entries that now sit below it. Read it all again.
                    page = await load(-1, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                ImmutableInterlocked.Update(ref _held, held => new Held(
                    renumbered ? ImmutableList.CreateRange(page.Entries) : held.Entries.AddRange(page.Entries.Where(e => held.Entries.Count == 0 || e.Index > held.Entries[^1].Index)),
                    page.Total, null, page.Working, page.PendingQuestion, page.Choices ?? [], page.Queued ?? []));
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
    /// Sends what is in the box, as Enter does: the box is emptied at once, the message goes through <see cref="OnSend"/>, and
    /// a refusal is said in the chat. Does nothing with nothing typed or no host to send.
    /// </summary>
    public Task SendAsync()
    {
        if (_composer.IsEmpty || OnSend is null)
        {
            return Task.CompletedTask;
        }
        var message = _composer.Message;
        _composer.Clear();
        return SendAsync(message);
    }

    /// <summary>Says <paramref name="message"/> to the watchtower: a choice button's label, or what was typed.</summary>
    public async Task SendAsync(string message)
    {
        if (OnSend is not { } send || string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        Interlocked.Increment(ref _sending);
        _scroll.ScrollToEnd();
        Changed?.Invoke();
        string? refused;
        try
        {
            refused = await send(message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            refused = $"refused: {ex.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _sending);
        }
        ImmutableInterlocked.Update(ref _notices, notices => refused is null
            ? []
            : notices.Add($"{refused} · {Clip(message)}") is var more && more.Count > NoticesKept ? more.RemoveAt(0) : more);
        Refresh();
        Changed?.Invoke();
    }

    private static string Clip(string message)
    {
        var line = message.ReplaceLineEndings(" ");
        return line.Length > 60 ? line[..59] + "…" : line;
    }

    public void Render(RectF32 bounds)
    {
        BeginFrame();
        _pageHeight = bounds.Height / DpiScale;
        _pageWidth = bounds.Width / DpiScale;
        RenderLayout(Tree(), bounds);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree()
    {
        var held = Volatile.Read(ref _held);
        _composer.Input.OnCommit = _ => SendAsync();
        _composer.Input.Placeholder = "Message the watchtower";
        var buttons = new List<Layout.Node>();
        if (!_scroll.IsFollowingEnd)
        {
            buttons.Add(CardsView<TSurface>.HeaderPill("Jump to end", "chat:end", () =>
            {
                _scroll.ScrollToEnd();
                Changed?.Invoke();
            }));
        }
        var top = TopBar.Build(new PageHeader("chat")
        {
            Buttons = buttons,
            Source = held.Problem ?? (Load is null ? "not live" : "the conversation with the node's watchtower"),
        }, new TopBarParts(Nodes, null, Autopilot, About));

        var rows = _entries.Rows([.. held.Entries.Where(e => e.Kind != TranscriptKind.Thinking)], held.Working).Select(r => r.WStar()).ToList();
        // What was said and the watchtower has not taken yet: last among the messages, dim, and saying so.
        rows.AddRange(held.Queued.Select(q => Waiting(q).WStar()));
        foreach (var notice in Notices)
        {
            rows.Add(Notice(notice));
        }
        // The question the watchtower waits on sits under the feed, above the box, not in it: in the scroll it was drawn over
        // the reply it ends, cutting its last lines, and scrolled away with the feed. Here it always shows, and the feed ends
        // above it.
        Layout.Node? asked = null;
        if (OnSend is not null && held.Question is { Length: > 0 } question)
        {
            // The reply the question ends already says it in the feed: the card then holds only the choices.
            var said = held.Entries.LastOrDefault(e => e.Kind == TranscriptKind.Assistant)?.Text;
            if (_askedKey != question)
            {
                _askedKey = question;
                _askScroll = NewAskScroll();
            }
            // A card whose body fits its viewport still reads one atom of overflow when the body's height is fractional (the
            // list counts whole surface units, the content rounded up and the viewport down), which drew a full-height
            // thumb on every card. The bar waits until the body overflows by more than that, as of the last frame.
            _askScroll.Mode = _askScroll.MaxOffset > 1f ? ScrollBarMode.Interactive : ScrollBarMode.None;
            asked = Question(question, said is not null && said.Contains(question, StringComparison.Ordinal), held.Choices);
        }
        var sending = Volatile.Read(ref _sending) > 0;
        if (held.Working || sending)
        {
            rows.Add(Layout.Builder.Text(sending ? "● sending…" : "● working…", 13f, Running).Pad(0f, 4f).WStar());
        }
        var list = rows.Count == 0
            ? Layout.Builder.Text(held.Problem ?? (Load is null ? "The chat is read from a live node." : "Nothing said yet. Tell the watchtower what you want: “let's launch a flight for issue 12”."), 13f, Dim).Stretch()
            : Layout.Builder.VStack([.. rows]).WithGap(ToolGap).WithScroll(_scroll).Stretch();

        var page = OnSend is null
            ? Layout.Builder.VStack(list.HStar())
            : asked is null
                ? Layout.Builder.VStack(list.HStar(), Foot())
                : Layout.Builder.VStack(list.HStar(), asked, Foot());
        var body = page.WithGap(8f).Pad(Gutter, 8f).Stretch();
        var whole = Layout.Builder.VStack(top, body.HStar()).Bg(PageBg).Stretch();
        return About?.Over(whole) ?? whole;
    }

    // A message that did not go.
    private static Layout.Node Notice(string text) =>
        Layout.Builder.HStack(
                Layout.Builder.Text("pilot", 12f, Failed).WFixed(96f),
                WrappedProse.Of(text, 13f, Failed, 3))
            .WithGap(10f)
            .WStar();

    // The question the watchtower waits on, with its choices as buttons: a click says the choice to it, as typing it would.
    private Layout.Node Question(string question, bool saidAlready, string[] choices)
    {
        var body = new List<Layout.Node>();
        if (saidAlready)
        {
            body.Add(Layout.Builder.Text("Pick one, or answer in your own words", 11f, Dim));
        }
        else
        {
            body.Add(Layout.Builder.Text("The watchtower asks", 11f, Dim));
            // Whole, never cut: a long question scrolls inside the capped card instead.
            body.Add(WrappedProse.Of(question, 14f, Bright));
        }
        // The text alone scrolls, capped so the card leaves the feed its room and ends above the box; the bar beside it and the
        // card's padding stay put. A gutter keeps the text clear of the scrollbar drawn over its right edge.
        var cap = Math.Max(AskMinHeight, _pageHeight * AskShare) - 2 * AskPadY;
        var choicesHeight = ChoicesHeight(choices);
        var textCap = Math.Max(AskTextMinHeight, cap - choicesHeight - (choicesHeight > 0f ? ChoiceGap : 0f));
        var text = Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.VStack([.. body]).WithGap(4f).WStar(),
                        Layout.Builder.Spacer().WFixed(TranscriptEntryView.ScrollGutter))
                    .WStar()
                    .HAuto())
            .WithScroll(_askScroll)
            .WStar()
            .HClamp(0f, textCap);
        var parts = new List<Layout.Node> { text };
        if (choices.Length > 0)
        {
            // The choices stay under the text, outside its scroll, so they are always in view and reachable.
            parts.Add(Layout.Builder.WrapH([.. choices.Select((choice, i) => CardsView<TSurface>.Pill(choice, $"chat:choice:{i + 1}", () => Choose(choice)))])
                .WithGap(8f).WithLineGap(4f).WStar());
        }
        var scrolling = Layout.Builder.VStack([.. parts]).WithGap(ChoiceGap).WStar().HAuto();
        return Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(Prompt).WFixed(3f).HStar(),
                scrolling)
            .WithGap(8f)
            .Pad(8f, AskPadY)
            .Bg(Marked)
            .WStar()
            .HAuto();
    }

    // How tall the wrapped choices come out, reckoned from their labels and the card's width: the pills wrap as laid out, so
    // this is a close guess that errs tall (the glyph's width is generous), leaving the card a little under its cap.
    private float ChoicesHeight(string[] choices)
    {
        if (choices.Length == 0)
        {
            return 0f;
        }
        // The card's inner width: the page less its gutters, the card's padding, the bar's stripe and the gap after it.
        var width = _pageWidth - 2 * Gutter - 16f - 3f - 8f;
        var rows = 1;
        var used = 0f;
        foreach (var choice in choices)
        {
            var pill = choice.Length * ChoiceGlyphWidth + ChoicePadX;
            if (used > 0f && used + ChoiceGap + pill > width)
            {
                rows++;
                used = pill;
            }
            else
            {
                used += (used > 0f ? ChoiceGap : 0f) + pill;
            }
        }
        return rows * ChoiceHeight + (rows - 1) * ChoiceLineGap;
    }

    // A choice answers the question: said to the watchtower as the person's message, through the same send as a typed one.
    private void Choose(string choice)
    {
        if (Track is { } track)
        {
            track(() => SendAsync(choice), $"chat choice {choice}");
        }
        else
        {
            _ = SendAsync(choice);
        }
    }

    // The box: the lines typed above the one being edited, read-only, and the field under them.
    private Layout.Node Foot()
    {
        var before = _composer.Before;
        var rows = new List<Layout.Node>();
        var skipped = Math.Max(0, before.Count - ChatComposer.ShownLines);
        if (skipped > 0)
        {
            rows.Add(Layout.Builder.Text($"… {skipped} earlier {(skipped == 1 ? "line" : "lines")}", 11f, Dim).RowH(16f));
        }
        rows.AddRange(before.Skip(skipped).Select(line => Layout.Builder.Text(line.Length == 0 ? " " : line, 13f, Body).RowH(18f)));
        rows.Add(Layout.Builder.TextInput(_composer.Input, 14f, focusOnOpen: true).RowH(30f));
        rows.Add(Layout.Builder.Text(before.Count > 0 ? $"{before.Count + 1} lines · Enter sends · Backspace on an empty line goes back" : "Enter sends · Shift+Enter a new line", 10f, Dim).RowH(16f));
        return Layout.Builder.VStack([.. rows]).Pad(12f, 6f).Bg(FootBg).WStar().HAuto();
    }

    public override bool HandleInput(InputEvent evt) => false;
}
