using DIR.Lib;
using Pilot.Contracts;

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

    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Faint = new(0x5c, 0x5f, 0x6c, 0xff);
    private static readonly RGBAColor32 Prompt = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 Tool = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly RGBAColor32 Failed = new(0xe0, 0x5a, 0x5a, 0xff);
    private static readonly RGBAColor32 Ok = new(0x5c, 0xb8, 0x6e, 0xff);
    private static readonly RGBAColor32 Running = new(0xe8, 0xb0, 0x4a, 0xff);
    private static readonly RGBAColor32 Marked = new(0x2a, 0x24, 0x18, 0xff);

    // The assistant's words and a subagent's report as Markdown, on the pane's colours; its thinking the same, dimmed.
    private static readonly Layout.MarkdownStyle Prose = new() { Text = Body, Strong = Bright, Link = Tool, Muted = Dim };
    private static readonly Layout.MarkdownStyle Thought = new()
    {
        Text = Faint, Strong = Dim, Emphasis = Faint, Code = Dim, Link = Dim, Muted = Faint, CodeBackground = new(0x18, 0x19, 0x20, 0xff),
    };
    private static readonly RGBAColor32 Button = new(0x2c, 0x35, 0x4a, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x3a, 0x46, 0x62, 0xff);

    // The label column: wide enough for a tool's name.
    private const float LabelWidth = 96f;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Bottom };
    private readonly List<TranscriptEntry> _entries = [];
    private readonly Lock _lock = new();
    private CancellationTokenSource? _following;
    private int _total;
    private string? _problem;

    private string[] _queued = [];

    // The entries opened past their first lines, by index: a click on "+N lines" opens one, another closes it.
    private readonly HashSet<int> _expanded = [];

    // Each Markdown entry's tree, by its index and whether it is opened.
    private readonly Dictionary<(int Index, bool Open), Layout.Node> _markdown = [];

    // Opened at a moment (a card's): the transcript from the first entry at or after it, that entry marked, held at the
    // top of a list that does not follow the end until Jump to end.
    private DateTimeOffset? _at;
    private readonly ListScrollController _atScroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Top };

    /// <summary>The open session's row id, or null with none open.</summary>
    public string? RowId { get; private set; }

    /// <summary>The open session as the table showed it: its role and its name.</summary>
    public string? Title { get; private set; }

    public bool IsOpen => RowId is not null;

    /// <summary>Reads a session's transcript after an entry (or its last entries, for -1): the node's transcript route.</summary>
    public Func<string, int, CancellationToken, Task<TranscriptPage>>? Load { get; set; }

    /// <summary>Called when what it shows changed, from whichever thread: the host redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>Puts text on the clipboard: a refused command's Copy. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Opens a link in what the assistant wrote. Null leaves links as coloured text.</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>The entries it holds now, oldest first.</summary>
    public IReadOnlyList<TranscriptEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="line"/>'s transcript and follows it until <see cref="Close"/> or another opens: at its end, or
    /// with <paramref name="at"/> from the first entry at or after that moment, marked, until Jump to end.
    /// </summary>
    public void Open(SessionLine line, DateTimeOffset? at = null)
    {
        Close();
        RowId = line.Row.Id;
        Title = $"{line.Role} · {line.Name}";
        lock (_lock)
        {
            _entries.Clear();
            _total = 0;
            _problem = null;
            _queued = [];
        }
        _expanded.Clear();
        _markdown.Clear();
        _at = at;
        _atScroll.AtomOffset = 0;
        _scroll.ScrollToEnd();
        if (Load is { } load)
        {
            var following = _following = new CancellationTokenSource();
            _ = FollowAsync(line.Row.Id, load, following.Token);
        }
    }

    public void Close()
    {
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
        RowId = null;
        Title = null;
    }

    private async Task FollowAsync(string rowId, Func<string, int, CancellationToken, Task<TranscriptPage>> load, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int after;
                lock (_lock)
                {
                    after = _entries.Count == 0 ? -1 : _entries[^1].Index;
                }
                var page = await load(rowId, after, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _entries.AddRange(page.Entries.Where(e => _entries.Count == 0 || e.Index > _entries[^1].Index));
                    _total = page.Total;
                    _queued = page.Queued ?? [];
                    _problem = null;
                }
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                lock (_lock)
                {
                    _problem = ex.Message;
                }
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
    /// so a call with nothing back yet is running rather than cut off.
    /// </summary>
    internal Layout.Node Tree(bool live = true)
    {
        List<TranscriptEntry> entries;
        int total;
        string? problem;
        string[] queued;
        lock (_lock)
        {
            entries = [.. _entries];
            total = _total;
            problem = _problem;
            queued = _queued;
        }
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
            Layout.Builder.Text("Back to the sessions", 13f, Bright, TextAlign.Center).Pad(12f, 5f).WAuto().Bg(Button).BgHover(ButtonHover)
                .Clickable(new HitResult.ButtonHit("transcript:back"), _ => Close()),
            Layout.Builder.Text(Title ?? "", 16f, Bright).WAuto(),
            Layout.Builder.Text(live ? "● live" : "session ended", 12f, live ? Ok : Faint).WAuto(),
            Layout.Builder.Text(problem is null ? shown : $"{shown} · {problem}", 12f, problem is null ? Dim : Failed).WStar(),
        };
        // Jump to end: while the reader is up in its history, or held at a moment.
        if (_at is not null || !_scroll.IsFollowingEnd)
        {
            head.Add(Layout.Builder.Text("Jump to end", 13f, Bright, TextAlign.Center).Pad(12f, 5f).WAuto().Bg(Button).BgHover(ButtonHover)
                .Clickable(new HitResult.ButtonHit("transcript:end"), _ => JumpToEnd()));
        }
        // Each row the pane's width: a paragraph wraps at the width it is given, and an unstretched row gives it none.
        var rows = Rows(entries, live).Select(r => r.WStar()).ToList();
        if (from >= 0 && rows.Count > 0)
        {
            // The entry the moment fell on, marked.
            rows[0] = Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(3f).Bg(Prompt), rows[0].WStar()).WithGap(6f).Bg(Marked).WStar();
        }
        if (live)
        {
            rows.AddRange(queued.Select(q => Waiting(q).WStar()));
        }
        var list = rows.Count == 0
            ? Layout.Builder.Text(problem ?? "reading the transcript…", 13f, Dim).Stretch()
            : Layout.Builder.VStack([.. rows]).WithGap(6f).WithScroll(from >= 0 ? _atScroll : _scroll).Stretch();
        return Layout.Builder.VStack(Layout.Builder.HStack([.. head]).WithGap(12f).WStar(), list).WithGap(8f).Pad(16f, 8f).Stretch();
    }

    // Back to the end of the whole transcript, following it again.
    private void JumpToEnd()
    {
        _at = null;
        _scroll.ScrollToEnd();
        Changed?.Invoke();
    }

    // A message handed to the session that it has not read yet: last, dim, and saying so.
    private static Layout.Node Waiting(string text) =>
        Layout.Builder.HStack(
                Layout.Builder.Text("queued", 12f, Faint).WFixed(LabelWidth),
                Layout.Builder.VStack([
                    Layout.Builder.Text("not read yet: it reads this at its next step", 11f, Faint),
                    .. text.ReplaceLineEndings("\n").Split('\n').Take(6).Select(l => Layout.Builder.Text(l, 13f, Dim)),
                ]).WStar())
            .WithGap(10f);

    // The entries as rows: a tool call and what it gave back as one, the result folded under the call; a result whose call
    // came before the entries held, on its own.
    private IEnumerable<Layout.Node> Rows(List<TranscriptEntry> entries, bool live)
    {
        var results = new Dictionary<string, TranscriptEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Kind is TranscriptKind.ToolResult or TranscriptKind.Error && entry.ToolUseId is { } id)
            {
                results.TryAdd(id, entry);
            }
        }
        var calls = entries.Where(e => e.Kind == TranscriptKind.ToolUse && e.ToolUseId is not null).Select(e => e.ToolUseId!).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Kind == TranscriptKind.ToolUse)
            {
                yield return Call(entry, entry.ToolUseId is { } id && results.TryGetValue(id, out var result) ? result : null, live);
            }
            else if (entry.Kind is TranscriptKind.ToolResult or TranscriptKind.Error && entry.ToolUseId is { } id && calls.Contains(id))
            {
                // Shown under its call.
            }
            else
            {
                yield return Row(entry);
            }
        }
    }

    // A tool call with its state as a dot (running, done, failed or refused, or cut off where the session ended first),
    // then what it gave back: its first lines, the rest a click away.
    private Layout.Node Call(TranscriptEntry call, TranscriptEntry? result, bool live)
    {
        var failed = result is { Kind: TranscriptKind.Error } || result?.Denied is not null;
        var dot = result is null ? (live ? Running : Faint) : failed ? Failed : Ok;
        var body = new List<Layout.Node> { Layout.Builder.Text(call.Text, 13f, Dim) };
        if (result?.Denied is { } denied)
        {
            body.Add(Layout.Builder.Text($"Denied · {denied}", 13f, Failed));
            var command = call.Full ?? call.Text;
            if (command.Length > 0)
            {
                // The call's line says a one-line command already; one over several lines is shown as it was asked.
                if (call.Full is { } full)
                {
                    body.AddRange(full.ReplaceLineEndings("\n").Split('\n').Select(l => Layout.Builder.Text(l, 13f, Bright)));
                }
                if (CopyText is { } copy)
                {
                    body.Add(Layout.Builder.HStack(
                            Layout.Builder.Text("Copy the command", 12f, Bright, TextAlign.Center).Pad(10f, 3f).WAuto().Bg(Button).BgHover(ButtonHover)
                                .Clickable(new HitResult.ButtonHit($"transcript:copy:{call.Index}"), _ => copy(command)),
                            Layout.Builder.Spacer().WStar()));
                }
            }
        }
        else if (result is not null)
        {
            if (call.Tool == "Read" && result.Kind == TranscriptKind.ToolResult)
            {
                // A file read in full says nothing a reader wants in the transcript: how much was read is enough.
                var count = result.Text.Length == 0 ? 0 : result.Text.ReplaceLineEndings("\n").Split('\n').Length;
                body.Add(Layout.Builder.Text($"└ Read {count} {(count == 1 ? "line" : "lines")}", 12f, Faint));
            }
            else
            {
                body.AddRange(Folded(result, result.Text.Length == 0 ? "(no output)" : result.Text, failed ? Failed : Faint, 3, "└ "));
            }
        }
        return Layout.Builder.HStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text("●", 12f, dot).WAuto(),
                        Layout.Builder.Text(call.Tool ?? "tool", 12f, Tool).WStar())
                    .WithGap(6f)
                    .WFixed(LabelWidth),
                Layout.Builder.VStack([.. body]).WStar())
            .WithGap(10f);
    }

    private Layout.Node Row(TranscriptEntry entry)
    {
        var (label, labelColor, textColor, lines) = entry switch
        {
            { Kind: TranscriptKind.User, From: { } from } => (from, Tool, Body, 12),
            { Kind: TranscriptKind.User, Note: true } => ("", Faint, Faint, 2),
            { Kind: TranscriptKind.User } => ("prompt", Prompt, Bright, 12),
            { Kind: TranscriptKind.Assistant } => ("claude", Body, Body, 40),
            { Kind: TranscriptKind.Thinking } => ("thinking", Faint, Faint, 2),
            { Kind: TranscriptKind.ToolUse } => (entry.Tool ?? "tool", Tool, Dim, 1),
            { Kind: TranscriptKind.ToolResult } => ("└", Faint, Faint, 3),
            _ => ("error", Failed, Failed, 4),
        };
        var body = new List<Layout.Node>();
        if (entry.From is not null)
        {
            body.Add(Layout.Builder.Text("reported", 12f, Dim));
        }
        if (entry.Denied is { } denied)
        {
            body.Add(Layout.Builder.Text($"Denied · {denied}", 13f, Failed));
        }
        // What the assistant wrote, and what a subagent reported, is Markdown; prompts and notes are said as typed.
        body.AddRange(entry is { Kind: TranscriptKind.Assistant or TranscriptKind.Thinking } or { From: not null }
            ? FoldedMarkdown(entry, entry.Kind == TranscriptKind.Thinking ? Thought : Prose, lines)
            : Folded(entry, entry.Text, textColor, lines, ""));
        return Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, labelColor).WFixed(LabelWidth),
                Layout.Builder.VStack([.. body]).WStar())
            .WithGap(10f);
    }

    // An entry's text, its first lines only until it is opened: the line saying how many more is what opens it, and
    // closes it again.
    private IEnumerable<Layout.Node> Folded(TranscriptEntry entry, string text, RGBAColor32 color, int lines, string lead)
    {
        var all = text.ReplaceLineEndings("\n").Split('\n');
        var open = _expanded.Contains(entry.Index);
        var shown = open ? all : all.Take(lines);
        foreach (var (line, i) in shown.Select((l, i) => (l, i)))
        {
            yield return Layout.Builder.Text(i == 0 ? lead + line : lead.Length > 0 ? "  " + line : line, 13f, color);
        }
        if (Fold(entry, all.Length, lines) is { } fold)
        {
            yield return fold;
        }
    }

    // An entry's Markdown, drawn from its first source lines until it is opened, as Folded draws plain text.
    private IEnumerable<Layout.Node> FoldedMarkdown(TranscriptEntry entry, Layout.MarkdownStyle style, int lines)
    {
        var all = entry.Text.ReplaceLineEndings("\n").Split('\n');
        var open = _expanded.Contains(entry.Index);
        // Parsed once per entry, folded or opened: the tree is rebuilt every frame, and a node is immutable.
        if (!_markdown.TryGetValue((entry.Index, open), out var document))
        {
            var source = open || all.Length <= lines ? entry.Text : string.Join("\n", all.Take(lines));
            _markdown[(entry.Index, open)] = document = Layout.Builder.Markdown(source, style with { OnLink = OpenLink is null ? null : Follow });
        }
        yield return document;
        if (Fold(entry, all.Length, lines) is { } fold)
        {
            yield return fold;
        }
    }

    // A link in the transcript, opened only when it is a web page: the opener runs whatever it is handed, and a session's
    // words are no reason to run a file on this machine.
    private void Follow(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            OpenLink?.Invoke(uri.AbsoluteUri);
        }
    }

    // The line under a folded entry that opens it and closes it again, or null for one short enough to show whole.
    private Layout.Node? Fold(TranscriptEntry entry, int count, int lines)
    {
        if (count <= lines)
        {
            return null;
        }
        var more = count - lines;
        var label = _expanded.Contains(entry.Index) ? "(click to collapse)" : $"… +{more} {(more == 1 ? "line" : "lines")} (click to expand)";
        return Layout.Builder.Text(label, 11f, Dim).WAuto().BgHover(ButtonHover)
            .Clickable(new HitResult.ButtonHit($"transcript:fold:{entry.Index}"), _ => Toggle(entry.Index));
    }

    private void Toggle(int index)
    {
        if (!_expanded.Remove(index))
        {
            _expanded.Add(index);
        }
        Changed?.Invoke();
    }
}
