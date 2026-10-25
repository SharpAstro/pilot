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
public sealed class WatchtowerChat(Watchtower tower, TranscriptBook transcripts)
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
                    entries.Add(DividerOf(segment.Generation, first));
                }
                first++;
            }
            // The part of the window that falls in this segment's own entries.
            var local = Math.Max(0, from - first);
            var want = Math.Min(take - entries.Count, segment.Count - local);
            if (want > 0 && transcripts.Window(segment.Session, local, want) is { } read)
            {
                entries.AddRange(read.Entries.Select((entry, i) => entry with { Index = first + local + i }));
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
        };
    }

    private static TranscriptEntry DividerOf(Watchtower.Generation generation, int index) => new()
    {
        Index = index,
        Kind = TranscriptKind.Divider,
        At = generation.StartedAt,
        Text = generation.Why is { Length: > 0 } why ? $"A fresh watchtower started: {why}" : "A fresh watchtower started",
    };

    private sealed record Segment(Watchtower.Generation Generation, string Session, int Offset, bool Divider, int Count);
}
