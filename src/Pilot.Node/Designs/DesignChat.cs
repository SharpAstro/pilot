using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Designs;

/// <summary>
/// A design's chat as one feed (PIL1151): the owner's messages (<c>chat.jsonl</c> in the design's folder), the designer's replies (the assistant entries of the
/// transcript of every designer session the design has had, <see cref="DesignRow.DesignerSessions"/>, as <see cref="Flights.WatchtowerChat"/> reads its
/// generations) and a line per published version, in the order they happened and numbered as one run, so a client asks for what came after its last index.
/// Everything the feed needs is in the design's folder and record, so a restarted node, or another node's client, reads the same chat. The answer is a
/// <see cref="ChatPage"/>, the watchtower chat's contract, with <see cref="ChatPage.Status"/> in plain words.
/// </summary>
public sealed partial class DesignChat(DesignStore store, TranscriptBook transcripts, Func<IEnumerable<SessionRow>>? sessions = null)
{
    /// <summary>
    /// The design's record as the designer's own session says it now (#1160): a record still Working whose designer session the tracker lists Idle or Ended has
    /// finished its turn, so it reads Idle at once, as <see cref="Designers.Sweep"/> will write it on its next tick, rather than waiting for that timer.
    /// </summary>
    private DesignRow Live(DesignRow row)
    {
        if (row.DesignerState is not DesignerStates.Working || row.DesignerName is not { Length: > 0 } name || sessions is null)
        {
            return row;
        }
        // The row Designers.AskAsync would take: a Gone or Failed one is not the designer, so a fresh start stays Working until its own row is listed.
        var listed = sessions().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && s.State is not SessionState.Failed && !s.Gone);
        // After a resume the previous turn's Stop is the latest hook until the new turn's prompt lands, so an Idle older than the turn's start is not its end.
        var ended = listed is { State: SessionState.Ended }
            || listed is { State: SessionState.Idle } && (row.DesignerTurnStartedAt is not { } turn || listed.LastResponseAt is { } last && last >= turn);
        if (!ended)
        {
            return row;
        }
        return row with
        {
            DesignerState = DesignerStates.Idle,
            DesignerTurnPublished = row.DesignerTurnStartedAt is { } began && row.History.Any(v => v.At >= began),
        };
    }

    /// <summary>The entries after <paramref name="after"/> (all of them for a negative one), the feed's length and the designer's state in words.</summary>
    public ChatPage Page(string id, int after)
    {
        var row = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        var live = Live(row);
        var all = Feed(row);
        var from = after < 0 ? 0 : (int)Math.Min(all.Count, (long)after + 1);
        return new ChatPage
        {
            Entries = [.. all.Skip(from).Select((e, i) => e with { Index = from + i })],
            Total = all.Count,
            Working = live.DesignerState is DesignerStates.Working,
            Status = StatusOf(live),
        };
    }

    private List<TranscriptEntry> Feed(DesignRow row)
    {
        var items = new List<(DateTimeOffset At, int Order, TranscriptEntry Entry)>();
        var order = 0;
        if (FirstBrief(row) is { Length: > 0 } first && row.At is { } made)
        {
            // The first brief is the owner's opening words, entry 0 (it is in BRIEF.md, not chat.jsonl, which holds the says after it).
            items.Add((made, order++, new TranscriptEntry { Index = 0, Id = "brief:0", At = made, Kind = TranscriptKind.User, Text = first }));
        }
        foreach (var line in store.Says(row.Id))
        {
            var text = line.Picture is { Length: > 0 } picture ? $"{line.Text}\n[picture: {picture}]" : line.Text;
            items.Add((line.At, order++, new TranscriptEntry { Index = 0, Id = $"say:{line.At.UtcTicks}", At = line.At, Kind = TranscriptKind.User, Text = text }));
        }
        foreach (var session in row.DesignerSessions)
        {
            if (transcripts.Window(session, 0, int.MaxValue) is not { } window)
            {
                continue;
            }
            for (var i = 0; i < window.Entries.Length; i++)
            {
                var entry = window.Entries[i];
                // The node's own prompts to the designer repeat what the owner said; its tool calls are its work, not the conversation.
                if (IsConversation(entry) && entry.At is { } at)
                {
                    items.Add((at, order++, entry with { Id = $"{session}:{entry.Id ?? $"i{i}"}" }));
                }
            }
        }
        foreach (var version in row.History)
        {
            if (version.At is { } at)
            {
                items.Add((at, order++, new TranscriptEntry
                {
                    Index = 0,
                    Id = $"version:{version.Number}",
                    At = at,
                    Kind = TranscriptKind.Divider,
                    Text = $"published v{version.Number.ToString(CultureInfo.InvariantCulture)}: {version.Why}",
                }));
            }
        }
        return [.. items.OrderBy(x => x.At).ThenBy(x => x.Order).Select(x => x.Entry)];
    }

    // The designer's words and a real session error (a crash, an API error) are the conversation; a tool result and a tool call's error (tool_use_error,
    // a hook's refusal, a refused design-check) are its work and show nowhere but, for a check, in the status.
    private static bool IsConversation(TranscriptEntry entry) =>
        entry.Kind is TranscriptKind.Assistant
        || entry.Kind is TranscriptKind.Error && entry.ToolUseId is null
            && entry.Text is not null && !entry.Text.Contains("tool_use_error", StringComparison.Ordinal) && !entry.Text.Contains("hook error", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^## \d{4}-\d\d-\d\dT[\d:]+Z\r?$", RegexOptions.Multiline)]
    private static partial Regex PieceHeading();

    /// <summary>The owner's first brief: the first piece of BRIEF.md without its time heading; null when the design has no brief file.</summary>
    private string? FirstBrief(DesignRow row)
    {
        var text = store.Detail(row.Id).Brief;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var heads = PieceHeading().Matches(text);
        if (heads.Count == 0)
        {
            return text.Trim();
        }
        var start = heads[0].Index + heads[0].Length;
        var end = heads.Count > 1 ? heads[1].Index : text.Length;
        return text[start..end].Trim();
    }

    /// <summary>What the designer is doing, in plain words.</summary>
    public static string StatusOf(DesignRow row)
    {
        switch (row.DesignerState)
        {
            case DesignerStates.Held:
                return row.DesignerHeldUntil is { } until ? $"held{(row.DesignerHeldProjected is true ? ", at the latest" : "")} until {until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}" : "held";
            case DesignerStates.Failed:
                return row.DesignerWhy is { Length: > 0 } why ? $"failed: {why}" : "failed";
            case DesignerStates.Working:
                return row.DesignerPhase switch
                {
                    DesignerPhases.Checking => $"checking: {row.CheckFindings ?? 0} finding{(row.CheckFindings == 1 ? "" : "s")}",
                    DesignerPhases.Rendering => "rendering",
                    DesignerPhases.Published when row.Current is { } v => $"published v{v.ToString(CultureInfo.InvariantCulture)}",
                    _ => "drafting",
                };
            default:
                return (row.Current, row.DesignerTurnPublished) switch
                {
                    ({ } current, false) => $"idle: the last turn published nothing, v{current.ToString(CultureInfo.InvariantCulture)} is current",
                    (_, false) => "idle: the last turn published nothing",
                    ({ } current, _) => $"published v{current.ToString(CultureInfo.InvariantCulture)}",
                    _ => "idle",
                };
        }
    }
}
