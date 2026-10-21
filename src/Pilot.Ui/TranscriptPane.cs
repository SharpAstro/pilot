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

    /// <summary>The open session's row id, or null with none open.</summary>
    public string? RowId { get; private set; }

    /// <summary>The open session as the table showed it: its role and its name.</summary>
    public string? Title { get; private set; }

    public bool IsOpen => RowId is not null;

    /// <summary>Reads a session's transcript after an entry (or its last entries, for -1): the node's transcript route.</summary>
    public Func<string, int, CancellationToken, Task<TranscriptPage>>? Load { get; set; }

    /// <summary>Called when what it shows changed, from whichever thread: the host redraws.</summary>
    public Action? Changed { get; set; }

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

    /// <summary>Opens <paramref name="line"/>'s transcript and follows it until <see cref="Close"/> or another opens.</summary>
    public void Open(SessionLine line)
    {
        Close();
        RowId = line.Row.Id;
        Title = $"{line.Role} · {line.Name}";
        lock (_lock)
        {
            _entries.Clear();
            _total = 0;
            _problem = null;
        }
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

    /// <summary>The pane as a tree, in the space the table takes otherwise.</summary>
    internal Layout.Node Tree()
    {
        List<TranscriptEntry> entries;
        int total;
        string? problem;
        lock (_lock)
        {
            entries = [.. _entries];
            total = _total;
            problem = _problem;
        }
        var shown = entries.Count < total ? $"its last {entries.Count} of {total}" : $"{total} {(total == 1 ? "entry" : "entries")}";
        var head = Layout.Builder.HStack(
                Layout.Builder.Text("Back to the sessions", 13f, Bright, TextAlign.Center).Pad(12f, 5f).WAuto().Bg(Button).BgHover(ButtonHover)
                    .Clickable(new HitResult.ButtonHit("transcript:back"), _ => Close()),
                Layout.Builder.Text(Title ?? "", 16f, Bright).WAuto(),
                Layout.Builder.Text(problem is null ? shown : $"{shown} · {problem}", 12f, problem is null ? Dim : Failed).WStar())
            .WithGap(12f);
        var rows = entries.Select(Row).ToArray();
        var list = rows.Length == 0
            ? Layout.Builder.Text(problem ?? "reading the transcript…", 13f, Dim).Stretch()
            : Layout.Builder.VStack(rows).WithGap(6f).WithScroll(_scroll).Stretch();
        return Layout.Builder.VStack(head, list).WithGap(8f).Pad(16f, 8f).Stretch();
    }

    private static Layout.Node Row(TranscriptEntry entry)
    {
        var (label, labelColor, textColor, lines) = entry.Kind switch
        {
            TranscriptKind.User => ("prompt", Prompt, Bright, 12),
            TranscriptKind.Assistant => ("claude", Body, Body, 40),
            TranscriptKind.Thinking => ("thinking", Faint, Faint, 2),
            TranscriptKind.ToolUse => (entry.Tool ?? "tool", Tool, Dim, 1),
            TranscriptKind.ToolResult => ("", Faint, Faint, 3),
            _ => ("error", Failed, Failed, 4),
        };
        var text = entry.Text.ReplaceLineEndings("\n").Split('\n');
        var body = text.Take(lines).Select(l => Layout.Builder.Text(l, 13f, textColor)).ToList();
        if (text.Length > lines)
        {
            body.Add(Layout.Builder.Text($"… {text.Length - lines} more {(text.Length - lines == 1 ? "line" : "lines")}", 11f, Faint));
        }
        return Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, labelColor).WFixed(LabelWidth),
                Layout.Builder.VStack([.. body]).WStar())
            .WithGap(10f);
    }
}
