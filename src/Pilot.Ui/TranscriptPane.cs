using DIR.Lib;
using Pilot.Contracts;
using static Pilot.Ui.TranscriptEntryView;

namespace Pilot.Ui;

/// <summary>
/// A session's transcript, on the Sessions page in place of the table: what was asked, what the assistant said, each tool
/// call in a line and what it gave back, errors in red. It follows the transcript live, pinned to its end until scrolled
/// up (<see cref="ScrollAnchor.Bottom"/>), reading only what came after what it has, every <see cref="Every"/>. The say
/// bar stays under it, so the session being read is the one being told. Its data comes through the host's
/// <see cref="Load"/>, so the window and the terminal show the same pane.
/// </summary>
internal sealed class TranscriptPane
{
    /// <summary>How often an open pane asks for what came after.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    /// <summary>The pane's background: the Sessions page's ground, which the transcript is drawn on.</summary>
    internal static RGBAColor32 Ground => Palette.Current.Ground;

    private static RGBAColor32 Marked => Palette.Current.Marked;

    private const int TitleLines = 2;

    // The banner's words scroll in what is left; they keep at least this much.
    private const float BannerMinHeight = 40f;
    private readonly ListScrollController _bannerScroll = new() { Mode = ScrollBarMode.Interactive };

    // The head's row: the Back pill's height, so the pill and the words beside it share a middle.
    private const float HeadRowHeight = 28f;

    // The entries' colours, Markdown styles and spacing are the entry view's, shared with the Chat page; the tests reach them here.
    internal static RGBAColor32 OutputShade => TranscriptEntryView.OutputShade;
    internal static RGBAColor32 OutputText => TranscriptEntryView.OutputText;
    internal static RGBAColor32 OutputFailed => TranscriptEntryView.OutputFailed;
    internal static Layout.MarkdownStyle Prose => TranscriptEntryView.Prose;
    internal static Layout.MarkdownStyle Thought => TranscriptEntryView.Thought;
    internal const float ToolGap = TranscriptEntryView.ToolGap;
    internal const float ProseSpace = TranscriptEntryView.ProseSpace;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Bottom };
    // What the follow has read, as one immutable record swapped whole: the follow appends on the pool, the render thread
    // reads the record it finds, and neither ever waits on the other.
    private sealed record Held(System.Collections.Immutable.ImmutableList<TranscriptEntry> Entries, int Total, string? Problem, string[] Queued)
    {
        public static readonly Held Empty = new([], 0, null, []);
    }

    private Held _held = Held.Empty;
    private CancellationTokenSource? _following;

    // The session as it was opened: switching to one of its subagents' transcripts, or back, opens it again.
    private SessionLine? _line;

    // Its entries as rows, and what a reader opened of them; its subagents are the session's row's, as of the last tree.
    private readonly TranscriptEntryView _entries;

    public TranscriptPane()
    {
        _entries = new TranscriptEntryView
        {
            OpenAgent = subagent =>
            {
                if (_line is { } line)
                {
                    OpenAgent(line, subagent.Id);
                }
            },
        };
    }

    // Opened at a moment (a card's): the transcript from the first entry at or after it, that entry marked, held at the
    // top of a list that does not follow the end until Jump to end.
    private DateTimeOffset? _at;
    private readonly ListScrollController _atScroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Top };

    /// <summary>The open session's row id, or null with none open.</summary>
    public string? RowId { get; private set; }

    /// <summary>The open session as the table showed it: its role and its name.</summary>
    public string? Title { get; private set; }

    public bool IsOpen => RowId is not null;

    /// <summary>The open session's <see cref="SessionLine.Key"/>: its row id, with its node's stream for another node's.</summary>
    public string? Key { get; private set; }

    /// <summary>The node the open session runs on, whose transcript route <see cref="Load"/> reads; null is this window's.</summary>
    public NodeMark? Node { get; private set; }

    /// <summary>
    /// Reads a session's transcript, or with an agent id its subagent's, after an entry (or its last entries, for -1):
    /// the node's transcript route.
    /// </summary>
    public Func<string, string?, int, CancellationToken, Task<TranscriptPage>>? Load { get; set; }

    /// <summary>Called as a transcript is about to open, before what the window shows changes: the chrome records the place being left.</summary>
    public Action? Opening { get; set; }

    /// <summary>What the Back pill and Escape do: the chrome's Back, which returns to wherever the transcript was opened from. Null (a host with no history) closes it.</summary>
    public Action? OnBack { get; set; }

    /// <summary>Leaves the transcript the way Back does: through <see cref="OnBack"/>, or by closing it where there is none.</summary>
    public void GoBack()
    {
        if (OnBack is { } back)
        {
            back();
        }
        else
        {
            Close();
        }
    }

    /// <summary>The open session as the table showed it, to open it again; null with none open.</summary>
    internal SessionLine? Line => IsOpen ? _line : null;

    /// <summary>The moment the open transcript was opened at, or null for one opened at its end.</summary>
    internal DateTimeOffset? At => IsOpen ? _at : null;

    /// <summary>The subagent whose transcript is open, or null for the session's own.</summary>
    public string? Agent { get; private set; }

    /// <summary>Called when what it shows changed, from whichever thread: the host redraws.</summary>
    public Action? Changed
    {
        get => _entries.Changed;
        set
        {
            _entries.Changed = value;
            _links.Changed = value;
        }
    }

    /// <summary>Puts text on the clipboard: a refused command's Copy. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText
    {
        get => _entries.CopyText;
        set
        {
            _entries.CopyText = value;
            _links.CopyText = value;
        }
    }

    // The right-click menu of the link that leaves the app (Answer in the browser): Copy URL.
    private readonly LinkMenu _links = new();

    /// <summary>The links' menu: its open items and its notice.</summary>
    internal LinkMenu Links => _links;

    /// <summary>Opens a link in what the assistant wrote. Null leaves links as coloured text.</summary>
    public Action<string>? OpenLink
    {
        get => _entries.OpenLink;
        set => _entries.OpenLink = value;
    }

    /// <summary>The entries it holds now, oldest first.</summary>
    public IReadOnlyList<TranscriptEntry> Entries => Volatile.Read(ref _held).Entries;

    /// <summary>
    /// Opens <paramref name="line"/>'s transcript and follows it until <see cref="Close"/> or another opens: at its end, or
    /// with <paramref name="at"/> from the first entry at or after that moment, marked, until Jump to end.
    /// </summary>
    public void Open(SessionLine line, DateTimeOffset? at = null, string? agent = null)
    {
        Opening?.Invoke();
        Close();
        RowId = line.Row.Id;
        Key = line.Key;
        Node = line.Node;
        Agent = agent;
        Title = $"{line.Role} · {line.Name}";
        _line = line;
        Volatile.Write(ref _held, Held.Empty);
        _entries.Reset();
        _at = at;
        _atScroll.AtomOffset = 0;
        _scroll.ScrollToEnd();
        if (Load is { } load)
        {
            var following = _following = new CancellationTokenSource();
            // Started on the pool: its first stretch (finding the node's client, a first read) ran on the render thread.
            _ = Task.Run(() => FollowAsync(line.Row.Id, agent, load, following.Token));
        }
    }

    public void Close()
    {
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
        RowId = null;
        Key = null;
        Node = null;
        Agent = null;
        Title = null;
    }

    private async Task FollowAsync(string rowId, string? agent, Func<string, string?, int, CancellationToken, Task<TranscriptPage>> load, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var had = Volatile.Read(ref _held).Entries;
                var after = had.Count == 0 ? -1 : had[^1].Index;
                var page = await load(rowId, agent, after, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                System.Collections.Immutable.ImmutableInterlocked.Update(ref _held, held => new Held(
                    held.Entries.AddRange(page.Entries.Where(e => held.Entries.Count == 0 || e.Index > held.Entries[^1].Index)),
                    page.Total, null, page.Queued ?? []));
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                System.Collections.Immutable.ImmutableInterlocked.Update(ref _held, held => held with { Problem = ex.Message });
                Changed?.Invoke();
            }
            try
            {
                await Task.Delay(Every, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The pane as a tree, in the space the table takes otherwise. <paramref name="live"/>: the session is still running,
    /// so a call with nothing back yet is running rather than cut off. <paramref name="current"/>: the open session's line as
    /// the table has it now, whose wait the pinned banner under the entries says; <paramref name="now"/> is what "since" counts to.
    /// </summary>
    internal Layout.Node Tree(bool live = true, IReadOnlyList<SubagentRow>? subagents = null, SessionLine? current = null, DateTimeOffset? now = null)
    {
        _entries.Subagents = subagents ?? [];
        // A subagent's transcript is live while the session is and the subagent has not handed back.
        if (Agent is { } agent)
        {
            live &= _entries.Subagents.FirstOrDefault(s => s.Id == agent)?.Working == true;
        }
        var held = Volatile.Read(ref _held);
        List<TranscriptEntry> entries = [.. held.Entries];
        var total = held.Total;
        var problem = held.Problem;
        var queued = held.Queued;
        // Opened at a moment: from the first entry at or after it (the first held, if all of them are later).
        var from = -1;
        if (_at is { } at && entries.Count > 0)
        {
            from = Math.Max(0, entries.FindIndex(e => e.At >= at));
            entries = entries[from..];
        }
        var shown = entries.Count < total ? $"{(from >= 0 ? "from the moment" : "its last")} {entries.Count} of {total}" : $"{total} {(total == 1 ? "entry" : "entries")}";
        var head = new List<Layout.Node>
        {
            Layout.Builder.Text("← Back", 13f, Bright, TextAlign.Center).Pad(12f, 5f).WAuto().Bg(Button).BgHover(ButtonHover)
                .Follows(new HitResult.ButtonHit("transcript:back"), _ => GoBack()),
            // At most two lines, the whole title on hover: a long session name took the page's height from its entries.
            WrappedProse.Of(Title ?? "", 16f, Bright, TitleLines).WAuto().WithTooltip(Title ?? ""),
            Layout.Builder.Text(live ? "● live" : "session ended", 12f, live ? Ok : Faint).WAuto(),
            WrappedProse.Of(problem is null ? shown : $"{shown} · {problem}", 12f, problem is null ? Dim : Failed).WAuto(),
        };
        // Jump to end: while the reader is up in its history, or held at a moment.
        if (_at is not null || !_scroll.IsFollowingEnd)
        {
            head.Add(Layout.Builder.Text("Jump to end", 13f, Bright, TextAlign.Center).Pad(12f, 5f).WAuto().Bg(Button).BgHover(ButtonHover)
                .Follows(new HitResult.ButtonHit("transcript:end"), _ => JumpToEnd()));
        }
        // Each row the pane's width: a paragraph wraps at the width it is given, and an unstretched row gives it none.
        var rows = _entries.Rows(entries, live).Select(r => r.WStar()).ToList();
        if (from >= 0 && rows.Count > 0)
        {
            // The entry the moment fell on, marked.
            rows[0] = Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(3f).Bg(Prompt), rows[0].WStar()).WithGap(6f).Bg(Marked).WStar();
        }
        if (live)
        {
            rows.AddRange(queued.Select(q => TranscriptEntryView.Waiting(q).WStar()));
        }
        var list = rows.Count == 0
            ? Layout.Builder.Text(problem ?? "reading the transcript…", 13f, Dim).Stretch()
            : Layout.Builder.VStack([.. rows]).WithGap(ToolGap).WithScroll(from >= 0 ? _atScroll : _scroll).Stretch();
        // The banner is a row of the page, not of the list: it stays under the entries, whatever is scrolled.
        var banner = Banner(current, now ?? DateTimeOffset.UtcNow);
        // The head flows onto lines of its own where the pane is narrower than it is, as the top bar does.
        var top = FlowRow.Of(HeadRowHeight, 12f, head);
        List<Layout.Node> page = _entries.Subagents.Count == 0 ? [top, list] : [top, Agents(), list];
        if (banner is not null)
        {
            page.Add(banner);
        }
        return Layout.Builder.VStack([.. page]).WithGap(8f).Pad(16f, 8f).Stretch();
    }

    // What a session waiting on its person waits on, since when, and how to answer it: the session as the table has it now,
    // or null while it does not wait (a gone one is still shown, its last state being waiting).
    private Layout.Node? Banner(SessionLine? line, DateTimeOffset now)
    {
        if (line is null || line.Row.State != SessionState.Waiting || SessionLine.IsStopped(line.Row))
        {
            return null;
        }
        var body = new List<Layout.Node>();
        if (line.Gone)
        {
            body.Add(WrappedProse.Of("the session is gone; its last state was waiting", 13f, Failed));
        }
        else
        {
            var since = line.WaitingSince is { } at ? $" since {Waited.Say(now - at)}" : "";
            var headline = line.Asking is { } asking ? $"Waiting on a permission prompt{since}: {asking}"
                : line.WaitingFor is { } what ? $"Waiting on you{since}: {what}"
                : $"Waiting on you{since}";
            body.Add(WrappedProse.Of(headline, 13f, Bright, 4));
            if (line.AnswerUrl is { } url)
            {
                body.Add(Layout.Builder.HStack(
                    _links.OnLink(Layout.Builder.Text("Answer in the browser" + Followable.Away, 12f, Bright, TextAlign.Center).Pad(10f, 3f).WAuto().Bg(Button).BgHover(ButtonHover)
                        .Follows(new HitResult.ButtonHit("transcript:answer"), _ => _entries.Follow(url)), url),
                    Layout.Builder.Spacer().WStar()));
            }
            else
            {
                body.Add(WrappedProse.Of(line.Row.Kind == SessionKind.Background
                    ? "Remote Control is off and a background session has no terminal: it cannot be answered from here."
                    : "Remote Control is off: answer it in its terminal.", 12f, Dim));
            }
        }
        // Its words scroll inside it where the pane leaves it less than they need (the entries above keep what they have), as the chat's
        // question does, so a banner never runs past the window's foot.
        return Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(line.Gone ? Failed : Prompt).WFixed(3f).HStar(),
                Layout.Builder.VStack(Layout.Builder.VStack([.. body]).WithGap(4f).WStar().HAuto()).WithScroll(_bannerScroll).WStar().HStarUpToContent(1f, BannerMinHeight))
            .WithGap(8f)
            .Pad(8f, 6f)
            .Bg(Marked)
            .WStar()
            .HStarUpToContent(1f, BannerMinHeight + 12f);
    }

    /// <summary>
    /// How many characters of chip text a row of the pane holds before the rest go into a dropdown. The tree is built before it
    /// is laid out, so this counts text, not pixels: the engine has no overflow-to-dropdown for a row of chips to declare.
    /// </summary>
    internal int ChipRowChars { get; set; } = 135;


    // A chip takes its text plus its padding and the gap to the next, in characters.
    private const int ChipExtraChars = 4;
    private const float ChipGap = 6f;

    // The "+N more" chip's room, kept free in a row that overflows.
    private const int MoreChars = 14;

    // Where the row of chips sits in the pane, so a dropdown opened without a pointer (the keyboard) hangs under it.
    private const float ChipRowTop = 46f;
    private const float ChipHeight = 22f;

    // The chips past the first row, as a dropdown: opened from the "+N more" chip, hung where it was pressed.
    private readonly DropdownMenuState<string> _menu = new();
    private RectF32? _menuAt;

    /// <summary>The entries of the open dropdown of chips that did not fit on the row: the labels of the subagents in it.</summary>
    internal IReadOnlyList<DropdownItem<string>> MenuItems => _menu.IsOpen ? _menu.Items : [];

    // The session and each subagent it started, one chip each, main first: the open transcript lit, a click on another
    // opens that one. Chips that would not fit on a row go into a dropdown behind a "+N more" chip.
    private Layout.Node Agents()
    {
        var labels = _entries.Labels();
        var all = new List<(string Text, string? Id, bool Open)> { ("main", null, Agent is null) };
        all.AddRange(_entries.Subagents.Select((s, i) => ($"{(s.Working ? "●" : "○")} {labels[i]}", (string?)s.Id, Agent == s.Id)));
        static int Chars(string text) => text.Length + ChipExtraChars;
        var fit = all.Count;
        if (all.Sum(a => Chars(a.Text)) > ChipRowChars)
        {
            // Main is always on the row, then as many as leave room for the "+N more" chip.
            fit = 1;
            var used = Chars(all[0].Text) + MoreChars;
            while (fit < all.Count && used + Chars(all[fit].Text) <= ChipRowChars)
            {
                used += Chars(all[fit++].Text);
            }
        }
        var chips = all.Take(fit).Select(a => Chip(a.Text, a.Id, a.Open)).ToList();
        if (fit < all.Count)
        {
            var rest = all.Skip(fit).ToList();
            _menu.Items = [.. rest.Select(a => new DropdownItem<string>(a.Text, a.Id ?? "main")
            {
                Tooltip = _entries.Subagents.FirstOrDefault(s => s.Id == a.Id)?.Description,
                OnChoose = () =>
                {
                    if (_line is { } line)
                    {
                        OpenAgent(line, a.Id);
                    }
                },
            })];
            // Lit while the open transcript is one of those in it.
            var lit = rest.Any(a => a.Open);
            chips.Add(Layout.Builder.Text($"+{rest.Count} more ▾", 12f, lit ? Bright : Dim, TextAlign.Center).Pad(10f, 3f).WAuto().Bg(lit ? ButtonHover : Button)
                .BgHover(ButtonHover)
                .Clickable(new HitResult.ButtonHit("transcript:agents"), _ => _menu.Popover.Toggle())
                // The menu hangs where the chip was pressed; the press is left to the click.
                .Pressable(new HitResult.ButtonHit("transcript:agents"), press =>
                {
                    _menuAt = new RectF32(press.X, press.Y, 0f, 0f);
                    return null;
                }));
        }
        else
        {
            _menu.IsOpen = false;
        }
        return Layout.Builder.WrapH([.. chips]).WithGap(ChipGap).WithLineGap(4f).WStar();
    }

    /// <summary>
    /// <paramref name="page"/> with the dropdown of the chips that did not fit over it while it is open. <paramref name="top"/>
    /// is where the pane starts on the surface, for a menu opened without a pointer to hang under the chips.
    /// </summary>
    internal Layout.Node Over(Layout.Node page, float top) =>
        _links.Over(IsOpen && _menu.IsOpen
            ? Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_menuAt ?? new RectF32(16f, top + ChipRowTop, 0f, ChipHeight), _menu, 13f, Bright, OutputShade, ButtonHover))
            : page);

    private Layout.Node Chip(string text, string? agent, bool open)
    {
        var chip = Layout.Builder.Text(text, 12f, open ? Bright : Dim, TextAlign.Center).Pad(10f, 3f).WAuto().Bg(open ? ButtonHover : Button);
        var description = agent is null ? null : _entries.Subagents.FirstOrDefault(s => s.Id == agent)?.Description;
        chip = description is null ? chip : chip.WithTooltip(description);
        return open || _line is not { } line
            ? chip
            : chip.BgHover(ButtonHover).Follows(new HitResult.ButtonHit($"transcript:agent:{agent ?? "main"}"), _ => OpenAgent(line, agent));
    }

    /// <summary>Opens the open session's subagent <paramref name="agent"/> (or, null, the session's own transcript).</summary>
    internal void OpenAgent(SessionLine line, string? agent)
    {
        Open(line, agent: agent);
        Changed?.Invoke();
    }

    // Back to the end of the whole transcript, following it again.
    private void JumpToEnd()
    {
        _at = null;
        _scroll.ScrollToEnd();
        Changed?.Invoke();
    }
}
