using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A transcript's entries as rows, shared by the Sessions page's <see cref="TranscriptPane"/> and the Chat page's
/// <see cref="ChatView{TSurface}"/>: a prompt, the assistant's words as Markdown, each tool call in one line with what it
/// gave back folded under it, errors in red, and where a fresh watchtower began a divider. It holds what a reader opened
/// (an entry past its first lines, a call's output) and the Markdown trees it parsed, so the owner builds the rows every
/// frame without parsing again; the owner decides what surrounds them (the follow loop, the scroll, the head).
/// </summary>
internal sealed class TranscriptEntryView
{
    internal static RGBAColor32 Bright => Palette.Current.Bright;
    internal static RGBAColor32 Body => Palette.Current.Body;
    internal static RGBAColor32 Dim => Palette.Current.Dim;
    internal static RGBAColor32 Faint => Palette.Current.Faint;
    internal static RGBAColor32 Prompt => Palette.Current.Held;
    internal static RGBAColor32 Tool => Palette.Current.Landed;
    internal static RGBAColor32 Failed => Palette.Current.Failed;
    internal static RGBAColor32 Ok => Palette.Current.Ok;
    internal static RGBAColor32 Running => Palette.Current.Running;
    internal static RGBAColor32 Button => Palette.Current.Action;
    internal static RGBAColor32 ButtonHover => Palette.Current.ActionHover;

    // Unfolded command and output sit in a shaded block, on colours whose contrast against both the block and the pane's
    // ground stays above WCAG's 4.5:1 floor (the tests check it).
    internal static RGBAColor32 OutputShade => Palette.Current.OutputShade;
    internal static RGBAColor32 OutputText => Palette.Current.OutputText;
    internal static RGBAColor32 OutputFailed => Palette.Current.OutputFailed;

    // The assistant's words and a subagent's report as Markdown at full contrast: they are the body of the page, the tool
    // lines around them are the furniture. Its thinking is read as much as its words (what it is about to do and why), so
    // it is body text a step below them, not furniture: it was the faintest grey, below the tool lines (DE1, 2026-10-25).
    internal static Layout.MarkdownStyle Prose => new()
    {
        Text = Bright, Strong = Palette.Current.Strong, Emphasis = Body, Link = Palette.Current.LinkSoft, Muted = Body, Code = OutputText, CodeBackground = OutputShade,
    };
    internal static Layout.MarkdownStyle Thought => new()
    {
        Text = Body, Strong = Bright, Emphasis = Body, Code = OutputText, Link = Palette.Current.LinkSoft, Muted = Dim, CodeBackground = OutputShade,
    };

    // The label column: wide enough for a tool's name.
    private const float LabelWidth = 96f;

    // The time column down the right of every entry and tool line: hh:mm:ss, wide enough for any. A page too narrow to give it its
    // seconds (TimeMin) drops it; the stamp stays on the entry's hover. The columns of a row share the width by these weights, the
    // body taking most, so a narrow page narrows the label and the time before the words and never runs a column past the page (PIL380).
    private const float TimeWidth = 74f;
    private const float TimeMin = 64f;
    private const float LabelWeight = 2f;
    private const float BodyWeight = 8f;

    // Kept clear right of the time: a feed's scrollbar is drawn over the rows' right edge, and took the seconds' last digit
    // ("20:40:3"). The bar's width with a pixel either side; gone once DIR.Lib reserves the bar's column: SharpAstro/DIR.Lib#161.
    internal const float ScrollGutter = ListScrollController.ScrollBarBaseWidthPx + 4f;

    // A call's line: the tool's column, the caret's column the unfolded body is indented by, and how many of a failed
    // call's last lines show while it is folded.
    private const float ToolWidth = 84f;
    private const float CaretWidth = 16f;
    private const int FailedLines = 3;

    /// <summary>The gap between consecutive rows: tool lines sit close together.</summary>
    internal const float ToolGap = 2f;

    /// <summary>The space above and below Claude's words and a prompt, more than <see cref="ToolGap"/> so they stand apart from the tool lines.</summary>
    internal const float ProseSpace = 10f;

    // The entries opened past their first lines, by index: a click on "+N lines" opens one, another closes it.
    private readonly HashSet<int> _expanded = [];

    // Each Markdown entry's tree, by its index and whether it is opened.
    private readonly Dictionary<(int Index, bool Open, bool Night), Layout.Node> _markdown = [];

    /// <summary>Called when what a click opened or closed changed: the owner redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>Puts text on the clipboard: a refused command's Copy. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Opens a link in what the assistant wrote. Null leaves links as coloured text.</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>The subagents of the session the entries are from, as its row lists them now; none for a chat.</summary>
    public IReadOnlyList<SubagentRow> Subagents { get; set; } = [];

    /// <summary>Opens a subagent's transcript, which a call that started it and a report from it link to. Null leaves those as plain words.</summary>
    public Action<SubagentRow>? OpenAgent { get; set; }

    /// <summary>What the person's own prompts are labelled: the Sessions page's <c>prompt</c>, the chat's <c>you</c>.</summary>
    public string PromptLabel { get; set; } = "prompt";

    /// <summary>What the assistant's words are labelled.</summary>
    public string AssistantLabel { get; set; } = "claude";

    /// <summary>How many source lines of the assistant's reply show before a "+N lines" fold; the chat shows a reply whole.</summary>
    public int AssistantLines { get; set; } = 40;

    /// <summary>Forgets what was opened and parsed: another transcript is about to show.</summary>
    public void Reset()
    {
        _expanded.Clear();
        _markdown.Clear();
        _rows.Clear();
    }

    /// <summary>
    /// A subagent's name without its plugin prefix (<c>pilot:reviewer</c> is <c>reviewer</c>), or <c>subagent</c> where its
    /// record does not say.
    /// </summary>
    internal static string TypeName(string? type)
    {
        if (type is not { Length: > 0 })
        {
            return "subagent";
        }
        var name = type[(type.LastIndexOf(':') + 1)..];
        return name.Length > 0 ? name : type;
    }

    // What a chip's label says of what the subagent is about: its description, else its prompt's first line, clipped.
    private const int AboutChars = 28;

    /// <summary>
    /// Each subagent's label: its type, a number where that type was started more than once (in the order they started),
    /// and what it is about: "reviewer 3: TableHeader resize".
    /// </summary>
    internal string[] Labels()
    {
        var counts = Subagents.GroupBy(s => TypeName(s.Type), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return [.. Subagents.Select(s =>
        {
            var name = TypeName(s.Type);
            seen[name] = seen.GetValueOrDefault(name) + 1;
            var label = counts[name] > 1 ? $"{name} {seen[name]}" : name;
            var about = (s.Description is { Length: > 0 } d ? d : s.Prompt)?.ReplaceLineEndings(" ").Trim();
            return about is { Length: > 0 } ? $"{label}: {(about.Length > AboutChars ? about[..(AboutChars - 1)] + "…" : about)}" : label;
        })];
    }

    // The label a subagent has in the chips, for the link on the call that started it.
    private string LabelOf(SubagentRow subagent) => Labels()[IndexOf(subagent)];

    /// <summary>A message handed to the session that it has not read yet: last, dim, and saying so.</summary>
    internal static Layout.Node Waiting(string text) => Waiting(text, null, null, null);

    /// <summary>
    /// A message handed to the stream watchtower that it has not read yet (PIL855): what it waits for in plain words ("read at its next
    /// step" for a message sent while it works, "queued behind its start" for one held while it starts; the node's
    /// <see cref="ChatPage.QueuedBehind"/>), the message, <paramref name="aside"/> (the Answer now
    /// pill) beside it and <paramref name="refusal"/> (the node's reason for refusing it) under it. Without <paramref name="behind"/> it is
    /// <see cref="Waiting(string)"/>'s line. The words are <see cref="Dim"/> where there is something to act on: <see cref="Faint"/> is
    /// short of 4.5:1 on the page.
    /// </summary>
    internal static Layout.Node Waiting(string text, string? behind, Layout.Node? aside, Layout.Node? refusal)
    {
        var acting = behind is { Length: > 0 };
        var label = behind == ChatPage.ReadAtNextStep ? "sent" : "queued";
        List<Layout.Node> parts =
        [
            acting
                ? WrappedProse.Of(behind!, 12f, Dim)
                : Layout.Builder.Text("not read yet: it reads this at its next step", 11f, Faint).Selectable(),
            // With something to act on the message wraps whole (a long one is read in full, not cut at the edge); the plain line keeps its lines.
            .. text.ReplaceLineEndings("\n").Split('\n').Take(6).Select(l => acting && l.Length > 0
                ? WrappedProse.Of(l, 13f, Dim)
                : Layout.Builder.Text(l, 13f, Dim).Selectable()),
        ];
        // The pill sits under the words, not beside them: beside them it took width from the body, so a line with a pill and one without
        // started their words at different places.
        if (aside is not null)
        {
            parts.Add(Layout.Builder.HStack(aside, Layout.Builder.Spacer().WStar()).WithGap(0f));
        }
        if (refusal is not null)
        {
            parts.Add(refusal);
        }
        return Layout.Builder.HStack(
                // A message the watchtower has already (mid-turn) is "sent", read at its next step; only one held while it starts is "queued".
                Layout.Builder.Text(label, 12f, acting ? Dim : Faint).Selectable().WStar(LabelWeight, 0f, LabelWidth).Whole(label),
                Layout.Builder.VStack([.. parts]).WithGap(2f).WStar(BodyWeight))
            .WithGap(10f);
    }

    /// <summary>The least height of the reply in progress (<see cref="Streaming"/>): its first lines, so the words arriving never push the rows under it.</summary>
    internal const float StreamingMinHeight = 3 * 20f;

    private (string Text, bool Night, Layout.Node Node)? _streaming;

    /// <summary>
    /// The watchtower's reply as it is written (PIL855): the words so far as Markdown in the bubble a finished reply has, its label saying it
    /// is still writing, with room for <see cref="StreamingMinHeight"/> held so the rows after it do not jump as the first lines arrive. The
    /// tree is parsed again only when the words change.
    /// </summary>
    internal Layout.Node Streaming(string text)
    {
        var night = ReferenceEquals(Palette.Current, Palette.Night);
        if (_streaming is not { } cached || cached.Text != text || cached.Night != night)
        {
            _streaming = (text, night, SelectableTree.Of(Layout.Builder.Markdown(text, Prose with { OnLink = OpenLink is null ? null : Follow }), breakLongWords: true));
        }
        var body = Layout.Builder.HStack(
                _streaming.Value.Node.WStar(),
                Layout.Builder.Spacer().WFixed(0f).HFixed(StreamingMinHeight))
            .WithGap(0f);
        var label = Layout.Builder.VStack(
                Layout.Builder.Text(AssistantLabel, 12f, Bright).Selectable().Whole(AssistantLabel),
                Layout.Builder.Text("writing…", 11f, Dim).Selectable())
            .WStar(LabelWeight, 0f, LabelWidth);
        return Layout.Builder.HStack(
                label,
                body.WStar(BodyWeight),
                Layout.Builder.Spacer().WStar(1f, 0f, TimeWidth).CollapseBelow(TimeMin))
            .WithGap(10f)
            .Pad(0f, ProseSpace);
    }

    // A row built once and kept, with what it was built from. DIR.Lib's measure memo and scroll cull key on the node's REFERENCE, so a
    // row that is the same node frame after frame is measured once and, off screen, not laid out at all (DIR.Lib#170): a frame's cost
    // follows the rows in view, not the transcript's length (PIL907). Open is the entry's fold; Live counts only for a call with nothing
    // back yet; Link and LinkLabel are the subagent a call started or a report came from, and the label the chips give it.
    private sealed class KeptRow
    {
        public required TranscriptEntry Entry;
        public required TranscriptEntry? Result;
        public required bool Open;
        public required bool Live;
        public required SubagentRow? Link;
        public required string? LinkLabel;
        public required Layout.Node Node;
        public int Pass;
    }

    // The built rows by the entry's feed index, like _expanded and _markdown.
    private readonly Dictionary<int, KeptRow> _rows = [];
    private int _pass;

    // What a row's build reads that is not in its entry: the labels, the links' openers and the palette. A change of any of them
    // builds every row again.
    private (string Prompt, string Assistant, int Lines, Action<string>? OpenLink, Action<SubagentRow>? OpenAgent, Action<string>? Copy, Palette Palette)? _rowsBuiltWith;

    /// <summary>How many rows <see cref="Rows"/> built (rather than handed back from its cache) over its last call.</summary>
    internal int RowsBuilt { get; private set; }

    /// <summary>
    /// The entries as rows: a tool call and what it gave back as one, the result folded under the call; a result whose call
    /// came before the entries held, on its own. <paramref name="live"/>: the session is still running, so a call with
    /// nothing back yet is running rather than cut off. Each row is kept per entry and handed back as the same node (already
    /// stretched to the page's width) while its entry, its result, its fold, its live state, its subagent link and the palette
    /// are what it was built from; only a row one of them changed is built again, and a row whose entry is no longer listed goes.
    /// </summary>
    internal List<Layout.Node> Rows(List<TranscriptEntry> entries, bool live)
    {
        var built = (Prompt: PromptLabel, Assistant: AssistantLabel, Lines: AssistantLines, OpenLink, OpenAgent, Copy: CopyText, Palette: Palette.Current);
        if (_rowsBuiltWith != built)
        {
            _rows.Clear();
            _rowsBuiltWith = built;
        }
        var pass = ++_pass;
        var seen = 0;
        RowsBuilt = 0;
        var results = new Dictionary<string, TranscriptEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Kind is TranscriptKind.ToolResult or TranscriptKind.Error && entry.ToolUseId is { } id)
            {
                results.TryAdd(id, entry);
            }
        }
        var calls = entries.Where(e => e.Kind == TranscriptKind.ToolUse && e.ToolUseId is not null).Select(e => e.ToolUseId!).ToHashSet(StringComparer.Ordinal);
        string[]? labels = null;
        var nodes = new List<Layout.Node>(entries.Count);
        foreach (var entry in entries)
        {
            TranscriptEntry? result = null;
            SubagentRow? link = null;
            string? linkLabel = null;
            var isLive = false;
            if (entry.Kind == TranscriptKind.ToolUse)
            {
                result = entry.ToolUseId is { } id && results.TryGetValue(id, out var found) ? found : null;
                isLive = live && result is null;
                if (StartedBy(entry) is { } started)
                {
                    link = started;
                    labels ??= Labels();
                    linkLabel = labels[IndexOf(started)];
                }
            }
            else if (entry.Kind is TranscriptKind.ToolResult or TranscriptKind.Error && entry.ToolUseId is { } resultOf && calls.Contains(resultOf))
            {
                // Shown under its call.
                continue;
            }
            else if (entry.From is not null)
            {
                link = ReporterOf(entry);
            }
            var open = _expanded.Contains(entry.Index);
            if (Environment.GetEnvironmentVariable("PIL907_NOCACHE") is null && _rows.TryGetValue(entry.Index, out var kept) && ReferenceEquals(kept.Entry, entry) && ReferenceEquals(kept.Result, result)
                && kept.Open == open && kept.Live == isLive && Equals(kept.Link, link) && kept.LinkLabel == linkLabel)
            {
                kept.Pass = pass;
                nodes.Add(kept.Node);
                seen++;
                continue;
            }
            // Stretched here, inside the kept node: the owners add no wrapper of their own, whose reference would change every frame.
            var node = (entry.Kind == TranscriptKind.ToolUse ? Call(entry, result, live) : entry.Kind == TranscriptKind.Divider ? Divider(entry) : Row(entry)).WStar();
            _rows[entry.Index] = new KeptRow { Entry = entry, Result = result, Open = open, Live = isLive, Link = link, LinkLabel = linkLabel, Node = node, Pass = pass };
            nodes.Add(node);
            seen++;
            RowsBuilt++;
        }
        if (_rows.Count != seen)
        {
            // Entries that are not listed any more (the list is shorter, or another transcript is shown): their rows go.
            foreach (var gone in _rows.Where(r => r.Value.Pass != pass).Select(r => r.Key).ToList())
            {
                _rows.Remove(gone);
            }
        }
        return nodes;
    }

    // The subagent a call started, and the one a report is from: what a call's line and a report's header link to.
    private SubagentRow? StartedBy(TranscriptEntry call) =>
        Subagents.FirstOrDefault(s => s.ToolUseId is not null && s.ToolUseId == call.ToolUseId);

    private SubagentRow? ReporterOf(TranscriptEntry entry) => Subagents.FirstOrDefault(s => s.Id == entry.FromAgent);

    private int IndexOf(SubagentRow subagent)
    {
        for (var i = 0; i < Subagents.Count; i++)
        {
            if (ReferenceEquals(Subagents[i], subagent))
            {
                return i;
            }
        }
        return -1;
    }

    // Where a fresh watchtower began in the chat: a rule with what the node said of it (and when) in the middle.
    private static Layout.Node Divider(TranscriptEntry entry)
    {
        var rule = Layout.Builder.Spacer().WStar().HFixed(1f).Bg(Faint);
        var said = entry.At is { } at ? $"{entry.Text} · {at.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}" : entry.Text;
        return Layout.Builder.HStack(
                Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), rule, Layout.Builder.Spacer().HStar()).WStar(),
                Layout.Builder.Text(said, 12f, Dim, TextAlign.Center).Selectable().WStarUpToContent(BodyWeight).WithTooltip(said),
                Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), rule, Layout.Builder.Spacer().HStar()).WStar())
            .WithGap(10f)
            .Pad(0f, ProseSpace);
    }

    // A tool call as one line: a caret, its state (a check, a cross, or a dot while running or cut off where the session
    // ended first), the tool and what the call says it does (Claude Code's own description of it, else its short form). A
    // click on the line unfolds the command and what it gave back; folded, only a failure shows anything, its last lines.
    private Layout.Node Call(TranscriptEntry call, TranscriptEntry? result, bool live)
    {
        var failed = result is { Kind: TranscriptKind.Error } || result?.Denied is not null;
        var (mark, markColor) = result is null ? ("●", live ? Running : Faint) : failed ? ("✗", Failed) : ("✓", Ok);
        var open = _expanded.Contains(call.Index);
        var said = call.Description is { Length: > 0 } description ? description : call.Text;
        var line = Layout.Builder.HStack(
                Layout.Builder.Text(mark, 12f, markColor).Selectable().WAuto(),
                Layout.Builder.Text(call.Tool ?? "tool", 12f, Tool).Selectable().WStar(LabelWeight, 0f, ToolWidth).Whole(call.Tool ?? "tool"),
                Layout.Builder.Text(said, 13f, Dim).Selectable().WStar(BodyWeight),
                Stamp(call.At).CollapseBelow(TimeMin))
            .WithGap(6f)
            .BgHover(ButtonHover)
            .Follows(new HitResult.ButtonHit($"transcript:call:{call.Index}"), _ => Toggle(call.Index));
        var body = new List<Layout.Node>();
        // A call that started a subagent leads to its transcript.
        if (StartedBy(call) is { } started)
        {
            body.Add(AgentLink(started, $"open {LabelOf(started)} transcript", $"transcript:spawn:{call.Index}"));
        }
        // The command and its output as one shaded block, in the monospace face.
        var block = new List<Layout.Node>();
        var command = call.Full ?? call.Text;
        if (open && command.Length > 0 && command != said)
        {
            block.AddRange(command.ReplaceLineEndings("\n").Split('\n').Select(l => Mono(l, Bright)));
        }
        if (result?.Denied is { } denied)
        {
            body.Add(Layout.Builder.Text($"Denied · {denied}", 13f, Failed).Selectable());
            if (command.Length > 0 && CopyText is { } copy)
            {
                body.Add(Layout.Builder.HStack(
                        // Copy the command stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
                        Layout.Builder.Text("Copy the command", 12f, Bright, TextAlign.Center).Pad(10f, 3f).WAuto().Bg(Button).BgHover(ButtonHover)
                            .Follows(new HitResult.ButtonHit($"transcript:copy:{call.Index}"), _ => copy(command)),
                        Layout.Builder.Spacer().WStar()));
            }
        }
        else if (result is not null)
        {
            if (call.Tool == "Read" && result.Kind == TranscriptKind.ToolResult)
            {
                // A file read in full says nothing a reader wants in the transcript: how much was read is enough.
                var count = result.Text.Length == 0 ? 0 : result.Text.ReplaceLineEndings("\n").Split('\n').Length;
                body.Add(Layout.Builder.Text($"└ Read {count} {(count == 1 ? "line" : "lines")}", 12f, Faint).Selectable());
            }
            else if (open || failed)
            {
                // Unfolded, all of it; folded, a failure's last lines.
                var all = result.Text.Length == 0 ? ["(no output)"] : result.Text.ReplaceLineEndings("\n").Split('\n');
                var shown = open ? all : all[Math.Max(0, all.Length - FailedLines)..];
                if (block.Count > 0)
                {
                    block.Add(Layout.Builder.Spacer().WFixed(1f).HFixed(4f));
                }
                block.AddRange(shown.Select(l => Mono(l, failed ? OutputFailed : OutputText)));
            }
        }
        if (block.Count > 0)
        {
            body.Add(Layout.Builder.VStack([.. block]).Pad(8f, 6f).Bg(OutputShade).WStar());
        }
        var row = Layout.Builder.TreeRow(0, hasChildren: true, open, () => Toggle(call.Index), line, new Layout.TreeRowStyle(Dim));
        // Under a line of its own (a read's count) the call's line is stretched across: left at its natural width in the
        // stack, it took the caret's, its text none, and the time was painted over the path (DE1, 2026-10-25).
        return body.Count == 0
            ? row
            : Layout.Builder.VStack(row.WStar(), Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(CaretWidth), Layout.Builder.VStack([.. body]).WStar())).WithGap(2f);
    }

    // When an entry was written, in this machine's time, the day and zone on hover; blank for one the transcript gave no
    // time. Every entry is stamped by Claude Code, and a reader asks "when was that" of a long run (SE1, 2026-10-25).
    internal static Layout.Node Stamp(DateTimeOffset? at) =>
        at is { } when
            ? Layout.Builder.HStack(
                    Layout.Builder.Text(when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), 11f, Faint, TextAlign.Far).Selectable().WStar()
                        .WithTooltip(when.ToLocalTime().ToString("ddd d MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture), Layout.DockSide.Left),
                    Layout.Builder.Spacer().WFixed(ScrollGutter))
                .WStar(1f, 0f, TimeWidth)
            : Layout.Builder.Spacer().WStar(1f, 0f, TimeWidth);

    // A line of a command or its output. One helper for all of it, so a monospace face for a text leaf lands here once:
    // DIR.Lib has none yet (SharpAstro/DIR.Lib#152). An empty line keeps its height as a space.
    private static Layout.Node Mono(string line, RGBAColor32 color) =>
        Layout.Builder.Text(line.Length == 0 ? " " : line, 13f, color).Selectable();

    private Layout.Node Row(TranscriptEntry entry)
    {
        // A message relayed from another session (or from the person, through the node) is said as from its sender, without its envelope.
        if (entry is { Kind: TranscriptKind.User, From: null, Note: false })
        {
            // Another node's watchtower passing work on: said as that node's, dim, not as the person's.
            var words = CrossSessionMessage.Parse(entry.Text)?.Body ?? entry.Text.Trim();
            if (CrossSessionMessage.TryParseWatchtower(words) is { } tower)
            {
                return Said(entry, $"{tower.Node}'s watchtower", Tool, Dim, tower.Body);
            }
        }
        if (entry is { Kind: TranscriptKind.User, From: null, Note: false } && CrossSessionMessage.Parse(entry.Text) is { } relayed)
        {
            var mine = relayed.From == CrossSessionMessage.Controller;
            return Said(entry, mine ? PromptLabel : relayed.From, mine ? Prompt : Tool, mine ? Bright : Body, relayed.Body);
        }
        var (label, labelColor, textColor, lines) = entry switch
        {
            { Kind: TranscriptKind.User, From: { } from } => (from, Tool, Body, 12),
            { Kind: TranscriptKind.User, Note: true } => ("", Faint, Faint, 2),
            { Kind: TranscriptKind.User } => (PromptLabel, Prompt, Bright, 12),
            { Kind: TranscriptKind.Assistant } => (AssistantLabel, Bright, Bright, AssistantLines),
            { Kind: TranscriptKind.Thinking } => ("thinking", Dim, Body, 8),
            { Kind: TranscriptKind.ToolUse } => (entry.Tool ?? "tool", Tool, Dim, 1),
            { Kind: TranscriptKind.ToolResult } => ("└", Faint, Faint, 3),
            _ => ("error", Failed, Failed, 4),
        };
        var body = new List<Layout.Node>();
        if (entry.From is not null)
        {
            body.Add(ReporterOf(entry) is { } reporter
                ? Layout.Builder.HStack(Layout.Builder.Text("reported · ", 12f, Dim).WAuto(), AgentLink(reporter, "open its transcript", $"transcript:report:{entry.Index}"), Layout.Builder.Spacer().WStar())
                : Layout.Builder.Text("reported", 12f, Dim).Selectable());
        }
        if (entry.Denied is { } denied)
        {
            body.Add(Layout.Builder.Text($"Denied · {denied}", 13f, Failed).Selectable());
        }
        // What the assistant wrote, and what a subagent reported, is Markdown; prompts and notes are said as typed.
        body.AddRange(entry is { Kind: TranscriptKind.Assistant or TranscriptKind.Thinking } or { From: not null }
            ? FoldedMarkdown(entry, entry.Kind == TranscriptKind.Thinking ? Thought : Prose, lines)
            : Folded(entry, entry.Text, textColor, lines, ""));
        var row = Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, labelColor).Selectable().WStar(LabelWeight, 0f, LabelWidth).Whole(label),
                Layout.Builder.VStack([.. body]).WStar(BodyWeight),
                ListTable.Top(Stamp(entry.At)).WStar(1f, 0f, TimeWidth).CollapseBelow(TimeMin))
            .WithGap(10f);
        // Claude's words, its thinking and a prompt stand apart from the tool lines around them.
        return entry.Kind is TranscriptKind.Assistant or TranscriptKind.Thinking or TranscriptKind.User ? row.Pad(0f, ProseSpace) : row;
    }

    // A prompt row with its own label and words: a relayed message's sender and body.
    private Layout.Node Said(TranscriptEntry entry, string label, RGBAColor32 labelColor, RGBAColor32 textColor, string text) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, labelColor).Selectable().WStar(LabelWeight, 0f, LabelWidth).Whole(label),
                Layout.Builder.VStack([.. Folded(entry, text, textColor, 12, "")]).WStar(BodyWeight),
                ListTable.Top(Stamp(entry.At)).WStar(1f, 0f, TimeWidth).CollapseBelow(TimeMin))
            .WithGap(10f)
            .Pad(0f, ProseSpace);

    // An entry's text, its first lines only until it is opened: the line saying how many more is what opens it, and
    // closes it again.
    private IEnumerable<Layout.Node> Folded(TranscriptEntry entry, string text, RGBAColor32 color, int lines, string lead)
    {
        var all = text.ReplaceLineEndings("\n").Split('\n');
        var open = _expanded.Contains(entry.Index);
        var shown = open ? all : all.Take(lines);
        foreach (var (line, i) in shown.Select((l, i) => (l, i)))
        {
            // Wrapped, never cut: a long message (a choice's answer carries its question) ended in an ellipsis (SE1, 2026-10-28).
            var said = i == 0 ? lead + line : lead.Length > 0 ? "  " + line : line;
            yield return said.Length == 0 ? Layout.Builder.Text(" ", 13f, color).Selectable() : WrappedProse.Of(said, 13f, color, selectable: true);
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
        if (!_markdown.TryGetValue((entry.Index, open, ReferenceEquals(Palette.Current, Palette.Night)), out var document))
        {
            var source = open || all.Length <= lines ? entry.Text : string.Join("\n", all.Take(lines));
            // A long word of it (a path, a check's name) is cut after its separators: Markdown wraps at white space only (PIL380).
            _markdown[(entry.Index, open, ReferenceEquals(Palette.Current, Palette.Night))] = document = SelectableTree.Of(Layout.Builder.Markdown(source, style with { OnLink = OpenLink is null ? null : Follow }), breakLongWords: true);
        }
        yield return document;
        if (Fold(entry, all.Length, lines) is { } fold)
        {
            yield return fold;
        }
    }

    // A line that opens a subagent's transcript, in place of the session's.
    private Layout.Node AgentLink(SubagentRow subagent, string label, string action) =>
        OpenAgent is not { } open
            ? Layout.Builder.Text(label, 12f, Dim)
            : Layout.Builder.Text($"{label} ›", 12f, Tool).WAuto().BgHover(ButtonHover)
                .Follows(new HitResult.ButtonHit(action), _ => open(subagent));

    /// <summary>
    /// A link in the transcript, opened only when it is a web page: the opener runs whatever it is handed, and a session's
    /// words are no reason to run a file on this machine.
    /// </summary>
    internal void Follow(string url)
    {
        if (WebLink(url) is { } web)
        {
            OpenLink?.Invoke(web);
        }
    }

    /// <summary>The address of <paramref name="url"/> when it is a web page, else null: the one rule for a link in words a session wrote, here and on the flight page.</summary>
    internal static string? WebLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;

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
            .Follows(new HitResult.ButtonHit($"transcript:fold:{entry.Index}"), _ => Toggle(entry.Index));
    }

    internal void Toggle(int index)
    {
        if (!_expanded.Remove(index))
        {
            _expanded.Add(index);
        }
        Changed?.Invoke();
    }
}
