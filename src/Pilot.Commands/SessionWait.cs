using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// <c>pilot session wait</c>: holds until the node's session events show a session blocked, idle or ended, then says which and
/// what it last said. Built on the event stream, never on re-reading the session list: the stream is opened first and the row
/// read from its snapshot, so a change between a read and the subscribe cannot be missed.
/// </summary>
internal static class SessionWait
{
    internal const string Blocked = "blocked";
    internal const string Idle = "idle";
    internal const string Ended = "ended";

    /// <summary>What a row counts as, for the wait: its kind (<see cref="Blocked"/>, <see cref="Idle"/>, <see cref="Ended"/>, or null while it works) and a word for the row's own state.</summary>
    internal static (string? Kind, string Detail) Classify(SessionRow? row)
    {
        if (row is null)
        {
            return (Ended, "removed");
        }
        if (row.State is SessionState.Ended or SessionState.Failed || (row.Gone && row.State is SessionState.Waiting or SessionState.Idle))
        {
            return (Ended, row.Gone ? "gone" : row.State.ToString().ToLowerInvariant());
        }
        if (row.State == SessionState.Waiting)
        {
            return (Blocked, "waiting");
        }
        if (row.State == SessionState.Idle)
        {
            return row.ApiError is { Length: > 0 } error ? (Blocked, $"idle, stopped by {error}") : (Idle, "idle");
        }
        return (null, row.State.ToString().ToLowerInvariant());
    }

    /// <summary>Whether <paramref name="row"/> is in a state <paramref name="until"/> asks for (null: blocked or idle). Ended always counts.</summary>
    internal static bool Reached(SessionRow? row, string? until, out string kind, out string detail)
    {
        (var found, detail) = Classify(row);
        kind = found ?? "";
        return found switch
        {
            null => false,
            Ended => true,
            Blocked => until is null or Blocked,
            _ => until is null or Idle,
        };
    }

    /// <summary>Waits, and writes the result. Exit 0 once reached, <see cref="PilotCommand.ExitTimedOut"/> at the timeout, 1 for a session the node does not know.</summary>
    internal static async Task<int> RunAsync(NodeClient client, string session, string? until, TimeSpan timeout, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        var rows = new Dictionary<string, SessionRow>();
        SessionRow? watched = null;
        SessionRow? last = null;
        long? seq = null;
        string? stream = null;
        string? kind = null, detail = null;
        var done = false;

        try
        {
            while (!done)
            {
                await using var events = await client.OpenEventsAsync(seq, stream, budget.Token);
                while (!done && await events.ReadAsync(budget.Token) is { } e)
                {
                    seq = e.Seq;
                    switch (e)
                    {
                        case SnapshotEvent snapshot:
                            stream = snapshot.Stream;
                            rows = snapshot.Sessions.ToDictionary(r => r.Id);
                            if (watched is null)
                            {
                                watched = SessionNames.Find(snapshot.Sessions, session);
                                if (watched is null)
                                {
                                    await error.WriteLineAsync($"pilot: There is no session {session}: give its row id, or the name of one live session, as pilot sessions lists them");
                                    return 1;
                                }
                            }
                            else
                            {
                                watched = rows.GetValueOrDefault(watched.Id) ?? Follower(rows, watched);
                            }
                            break;
                        case SessionAddedEvent or SessionChangedEvent:
                            var row = e is SessionAddedEvent added ? added.Session : ((SessionChangedEvent)e).Session;
                            rows[row.Id] = row;
                            if (watched is not null && (row.Id == watched.Id || row.EarlierSessionIds?.Contains(watched.SessionId) == true))
                            {
                                watched = row;
                            }
                            break;
                        case SessionRemovedEvent removed:
                            rows.Remove(removed.Id);
                            if (watched is not null && watched.Id == removed.Id)
                            {
                                watched = Follower(rows, watched);
                            }
                            break;
                        default:
                            continue;
                    }
                    // A resume ends the old row and starts a copy: the copy is the session, whichever event came first.
                    if (watched is { State: SessionState.Ended or SessionState.Failed } && Follower(rows, watched) is { } copy)
                    {
                        watched = copy;
                    }
                    if (watched is not null)
                    {
                        last = watched;
                    }
                    // A row removed with no copy turning up is null here, and counts as ended.
                    if (Reached(watched, until, out var reached, out var why))
                    {
                        (kind, detail, done) = (reached, why, true);
                    }
                }
                // A closed stream is reopened from the last number, and the row looked at again from what it sends.
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var state = last is null ? "unknown" : Classify(last).Detail;
            await output.WriteLineAsync($"timed out after {FormatSpan(timeout)}; {last?.Name ?? session} is {state}");
            return PilotCommand.ExitTimedOut;
        }

        var shown = watched ?? last;
        var label = shown is null ? session : $"{shown.Id}  {shown.Name}";
        await output.WriteLineAsync($"{label}  {kind}{(detail is { Length: > 0 } && detail != kind ? $" ({detail})" : "")}");
        await output.WriteLineAsync(await LastWordsAsync(client, shown, cancellationToken));
        return 0;
    }

    // A row that went away, or a session another row continues: the row whose earlier ids hold the watched one's session id.
    private static SessionRow? Follower(Dictionary<string, SessionRow> rows, SessionRow watched) =>
        rows.Values.FirstOrDefault(r => r.Id != watched.Id && r.EarlierSessionIds?.Contains(watched.SessionId) == true);

    private static async Task<string> LastWordsAsync(NodeClient client, SessionRow? row, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            return "(no transcript: the session's row is gone)";
        }
        try
        {
            var envelope = await client.GetTranscriptAsync(row.Id, -1, cancellationToken);
            if (envelope is { Success: true, Response: { } page })
            {
                var words = page.Entries.LastOrDefault(en => en.Kind == TranscriptKind.Assistant && en.Text.Length > 0);
                return words is null ? "(it has said nothing yet)" : words.Text;
            }
            return $"(no transcript: {envelope?.Error ?? "the node gave none"})";
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return $"(no transcript: {ex.Message})";
        }
    }

    internal static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours) ? $"{(int)span.TotalHours}h"
        : span.TotalMinutes >= 1 && span.TotalMinutes == Math.Floor(span.TotalMinutes) ? $"{(int)span.TotalMinutes}m"
        : $"{span.TotalSeconds:0.#}s";
}
