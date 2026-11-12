using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The conversation with the watchtower as one chat feed (<see cref="ChatPage"/>, <c>GET /api/v1/chat</c>): the transcript of
/// every watchtower there has been (<see cref="Watchtower.Generations"/>), oldest first, so the chat survives a fresh
/// watchtower and a restarted node. Their entries are numbered as one run, and a <see cref="TranscriptKind.Divider"/> entry
/// stands where a fresh watchtower began, so a client asks for what came after the last index it has, whichever watchtower
/// said it.
/// </summary>
public sealed class WatchtowerChat(Watchtower tower, TranscriptBook transcripts, SendLog? sends = null)
{
    /// <summary>
    /// The entries after <paramref name="after"/> (the feed's last <see cref="TranscriptBook.FirstRead"/> for a negative one, at most
    /// <see cref="TranscriptBook.Page"/> otherwise), whether the watchtower is working, and the question it waits on.
    /// </summary>
    public ChatPage Page(int after)
    {
        // The sessions with a transcript to read, in the order they began: a watchtower that never got to say anything leaves no trace.
        var segments = new List<Segment>();
        TranscriptBook.TranscriptWindow? latest = null;
        var total = 0;
        foreach (var generation in tower.Generations())
        {
            if (generation.SessionId is not { } session || segments.Any(s => s.Session == session) || transcripts.Window(session, 0, 0) is not { } window)
            {
                // (A compaction that kept its session id is one transcript, read once.)
                continue;
            }
            var divider = segments.Count > 0;
            segments.Add(new Segment(generation, session, total, divider, window.Total));
            total += (divider ? 1 : 0) + window.Total;
            latest = window;
        }

        var from = after < 0 ? Math.Max(0, total - TranscriptBook.FirstRead) : (int)Math.Min(total, (long)after + 1);
        var take = after < 0 ? TranscriptBook.FirstRead : TranscriptBook.Page;
        var entries = new List<TranscriptEntry>();
        foreach (var segment in segments)
        {
            var first = segment.Offset;
            if (segment.Divider)
            {
                if (first >= from && entries.Count < take && first < from + take)
                {
                    entries.Add(DividerOf(segment.Generation, segment.Session, first));
                }
                first++;
            }
            // The part of the window that falls in this segment's own entries.
            var local = Math.Max(0, from - first);
            var want = Math.Min(take - entries.Count, segment.Count - local);
            if (want > 0 && transcripts.Window(segment.Session, local, want) is { } read)
            {
                entries.AddRange(read.Entries.Select((entry, i) => entry with { Index = first + local + i, Id = $"{segment.Session}:{entry.Id ?? $"i{local + i}"}" }));
            }
        }

        // The stream session is the authority on what is held and what runs now; a watchtower that is not one has the transcript's say.
        var stream = tower.CurrentStream();
        // Written mid-turn and not yet read (it reads them at its next step) come first, then any held while its process starts.
        // The node's own requests of its brain (PIL855) are not the person's messages: they are not shown as waiting.
        var midTurn = stream?.ReadAtNextStep.Count(l => !BrainClient.IsRequest(l)) ?? 0;
        string[] queuedLines = stream is not null ? [.. stream.ReadAtNextStep.Where(l => !BrainClient.IsRequest(l)), .. stream.Queued.Where(l => !BrainClient.IsRequest(l))] : latest?.Queued ?? [];
        var stamps = Stamps(segments, queuedLines);
        for (var i = 0; i < entries.Count; i++)
        {
            if (stamps.Entries.TryGetValue(entries[i].Index, out var sendId))
            {
                entries[i] = entries[i] with { SendId = sendId };
            }
        }

        return new ChatPage
        {
            Entries = [.. entries],
            Total = total,
            Working = tower.Current() is { State: SessionState.Working },
            // A question the stream waits on is shown by Asks, with its own answers; the transcript's reading of the same call is left out.
            PendingQuestion = stream is { Pending.Count: > 0 } ? null : latest?.Pending?.Question,
            Choices = stream is { Pending.Count: > 0 } ? null : latest?.Pending is { Choices.Length: > 0 } pending ? pending.Choices : null,
            Queued = queuedLines.Length > 0 ? queuedLines : null,
            QueuedIds = stamps.Queued is { } ids && ids.Any(id => id is not null) ? ids : null,
            // The person's part of the turn as it comes: the brain's answer to the node, and anything after it, is not written in their chat.
            Partial = stream is { Working: true } && BrainClient.ForPerson(stream.PartialText) is { Length: > 0 } words ? words : null,
            Doing = stream is { Working: true } ? stream.Doing : null,
            QueuedBehind = stream is not null && queuedLines.Length > 0 ? BehindOf(midTurn, queuedLines.Length) : null,
            // The person's turns only: a turn that answered the node's brain request (a verdict, a wording, a summary) is not how fast it answers them.
            Timings = stream?.Timings.Where(t => !t.ForNode).TakeLast(ChatTimings.Kept).Select(TimingOf).ToArray() is { Length: > 0 } timings ? timings : null,
            Asks = stream is { Pending.Count: > 0 } ? [.. stream.Pending.Select(AskOf)] : null,
        };
    }

    internal static ChatAsk AskOf(PendingAsk ask) => new()
    {
        Id = ask.Id,
        Tool = ask.Tool,
        Summary = ask.IsQuestion ? ask.Questions is { Count: > 0 } questions ? questions[0].Question : "a question" : PermissionAsks.Describe(ask.Tool, ask.Summary),
        Input = ask.IsQuestion || ask.Input.ValueKind != System.Text.Json.JsonValueKind.Object ? null : ask.Input.GetRawText(),
        Questions = ask.Questions is { Count: > 0 } all
            ? [.. all.Select(q => new ChatAskQuestion { Question = q.Question, Header = q.Header, MultiSelect = q.MultiSelect, Options = [.. q.Options.Select(o => new ChatAskOption { Label = o.Label, Description = o.Description })] })]
            : null,
        At = ask.At,
    };

    /// <summary>
    /// What each of <paramref name="count"/> unread lines waits for: the first <paramref name="midTurn"/> were written to the watchtower
    /// while it worked and it reads them at its next step (<see cref="ChatPage.ReadAtNextStep"/>, not "queued behind": it has them); the
    /// rest were held while its process starts (<see cref="ChatPage.QueuedBehindStart"/>).
    /// </summary>
    internal static string[] BehindOf(int midTurn, int count)
    {
        var behind = new string[count];
        for (var i = 0; i < count; i++)
        {
            behind[i] = i < midTurn ? ChatPage.ReadAtNextStep : ChatPage.QueuedBehindStart;
        }
        return behind;
    }

    private static ChatTiming TimingOf(TurnTiming turn) => new()
    {
        StartedAt = turn.StartedAt,
        FirstWordMs = turn.FirstWord is { } first ? (long)first.TotalMilliseconds : null,
        TurnMs = (long)turn.Length.TotalMilliseconds,
        WaitedMs = turn.Waited > TimeSpan.Zero ? (long)turn.Waited.TotalMilliseconds : null,
    };

    /// <summary>
    /// Cancels the turn the watchtower is on so that the message with <paramref name="sendId"/>, written to it mid-turn and not read yet, is
    /// answered next (PIL855), the <see cref="Verbs.ChatNow"/> verb. Null when it was done; otherwise why it was refused: nothing is running
    /// (it is idle, or not a stream watchtower), the send id is not waiting, or other messages are ahead of it (the process reads them in
    /// order, so only the first can be answered now).
    /// </summary>
    public async Task<string?> AnswerNowAsync(string sendId, CancellationToken cancellationToken)
    {
        if (tower.CurrentStream() is not { Working: true } stream)
        {
            return "nothing is running: the watchtower is idle, so there is no turn to cancel and a message sent to it is answered at once";
        }
        var page = Page(-1);
        var index = page.QueuedIds is { } ids ? Array.IndexOf(ids, sendId) : -1;
        if (index < 0 || page.Queued is not { } queued || index >= queued.Length)
        {
            return $"send {sendId} is not queued: it was taken already, or never reached the watchtower";
        }
        if (index > 0)
        {
            return $"send {sendId} comes after {index} other unread {(index == 1 ? "message" : "messages")} and the watchtower reads them in order: answer-now takes only the first one";
        }
        if (!await stream.InterruptBeforeAsync(queued[0], cancellationToken))
        {
            return $"send {sendId} was taken as the interrupt was written: nothing was cancelled";
        }
        return null;
    }

    // How far before a send began an entry may be stamped by it: the transcript's clock and the node's are one machine's, but a
    // write is stamped when Claude Code makes it.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The send id of each user entry (by its feed index) and of each queued line that one of the node's recorded sends
    /// (<see cref="SendLog"/>) became. The sends are matched in order, each once, by their words (the relay's footer, the node's
    /// prefix and the whitespace left out), and an entry only by a send that began before it was written, so a person's
    /// message of the same words from long ago takes no later send's id. A queued line takes a send no entry took; one that
    /// every send of its words was taken by an entry for is a stale line of the oldest of them, which a client then hides
    /// beside that entry.
    /// </summary>
    private (Dictionary<int, string> Entries, string?[]? Queued) Stamps(List<Segment> segments, string[] queuedLines)
    {
        var taken = new Dictionary<int, string>();
        if (sends?.All() is not { Length: > 0 } all)
        {
            return (taken, null);
        }
        var free = all.Select(s => (Send: s, Key: TranscriptBook.Reader.Same(s.Text))).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (transcripts.Window(segment.Session, 0, segment.Count) is not { } whole)
            {
                continue;
            }
            var first = segment.Offset + (segment.Divider ? 1 : 0);
            for (var i = 0; i < whole.Entries.Length; i++)
            {
                var entry = whole.Entries[i];
                if (entry.Kind != TranscriptKind.User || entry.From is not null)
                {
                    continue;
                }
                var key = TranscriptBook.Reader.Same(entry.Text);
                var match = free.FirstOrDefault(f => !used.Contains(f.Send.Id) && IsSend(f.Key, key) && (entry.At is not { } at || at >= f.Send.At - Slack));
                if (match.Send is { } send)
                {
                    used.Add(send.Id);
                    taken[first + i] = send.Id;
                }
            }
        }
        if (queuedLines.Length == 0)
        {
            return (taken, null);
        }
        var queued = new string?[queuedLines.Length];
        var queuedUsed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < queued.Length; i++)
        {
            var key = TranscriptBook.Reader.Same(queuedLines[i]);
            var match = free.FirstOrDefault(f => !used.Contains(f.Send.Id) && !queuedUsed.Contains(f.Send.Id) && f.Key == key);
            if (match.Send is null)
            {
                match = free.FirstOrDefault(f => !queuedUsed.Contains(f.Send.Id) && f.Key == key);
            }
            if (match.Send is { } send)
            {
                queuedUsed.Add(send.Id);
                queued[i] = send.Id;
            }
        }
        return (taken, queued);
    }

    /// <summary>
    /// Whether an entry with the key <paramref name="entry"/> is the send with the key <paramref name="send"/>: the same words, or the
    /// send's words as the transcript cut them (<see cref="TranscriptBook.Reader.Clip"/>): its head when the cut is at the end, its
    /// head and its tail when the cut is inside a card's block. A cut entry matched no send, so it carried no id and the chat kept the
    /// person's echo of it for good.
    /// </summary>
    internal static bool IsSend(string send, string entry)
    {
        if (send == entry)
        {
            return true;
        }
        if (send.Length <= entry.Length)
        {
            return false;
        }
        if (entry.EndsWith(TranscriptBook.Reader.Clipped, StringComparison.Ordinal))
        {
            return send.StartsWith(entry[..^TranscriptBook.Reader.Clipped.Length].TrimEnd(), StringComparison.Ordinal);
        }
        var cut = entry.IndexOf(CutKey, StringComparison.Ordinal);
        return cut >= 0 && send.StartsWith(entry[..cut], StringComparison.Ordinal) && send.EndsWith(entry[(cut + CutKey.Length)..], StringComparison.Ordinal);
    }

    // A card block's cut line as a key holds it: between single spaces, as Same leaves every run of white space.
    private static readonly string CutKey = $" {CardContext.Cut.Trim()} ";

    private static TranscriptEntry DividerOf(Watchtower.Generation generation, string session, int index) => new()
    {
        Index = index,
        Id = $"divider:{session}",
        Kind = TranscriptKind.Divider,
        At = generation.StartedAt,
        Text = generation.IsCompaction ? "The watchtower compacted its conversation"
            : generation.Why is { Length: > 0 } why ? $"A fresh watchtower started: {why}" : "A fresh watchtower started",
    };

    private sealed record Segment(Watchtower.Generation Generation, string Session, int Offset, bool Divider, int Count);
}
