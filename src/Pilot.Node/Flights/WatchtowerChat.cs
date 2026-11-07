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
            if (generation.SessionId is not { } session || transcripts.Window(session, 0, 0) is not { } window)
            {
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

        var stamps = Stamps(segments, latest);
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
            PendingQuestion = latest?.Pending?.Question,
            Choices = latest?.Pending is { Choices.Length: > 0 } pending ? pending.Choices : null,
            Queued = latest is { Queued.Length: > 0 } ? latest.Queued : null,
            QueuedIds = stamps.Queued is { } ids && ids.Any(id => id is not null) ? ids : null,
        };
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
    private (Dictionary<int, string> Entries, string?[]? Queued) Stamps(List<Segment> segments, TranscriptBook.TranscriptWindow? latest)
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
                var match = free.FirstOrDefault(f => !used.Contains(f.Send.Id) && f.Key == key && (entry.At is not { } at || at >= f.Send.At - Slack));
                if (match.Send is { } send)
                {
                    used.Add(send.Id);
                    taken[first + i] = send.Id;
                }
            }
        }
        if (latest is not { Queued.Length: > 0 })
        {
            return (taken, null);
        }
        var queued = new string?[latest.Queued.Length];
        var queuedUsed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < queued.Length; i++)
        {
            var key = TranscriptBook.Reader.Same(latest.Queued[i]);
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

    private static TranscriptEntry DividerOf(Watchtower.Generation generation, string session, int index) => new()
    {
        Index = index,
        Id = $"divider:{session}",
        Kind = TranscriptKind.Divider,
        At = generation.StartedAt,
        Text = generation.Why is { Length: > 0 } why ? $"A fresh watchtower started: {why}" : "A fresh watchtower started",
    };

    private sealed record Segment(Watchtower.Generation Generation, string Session, int Offset, bool Divider, int Count);
}
