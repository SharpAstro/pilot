using System.Globalization;
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
public sealed class DesignChat(DesignStore store, TranscriptBook transcripts)
{
    /// <summary>The entries after <paramref name="after"/> (all of them for a negative one), the feed's length and the designer's state in words.</summary>
    public ChatPage Page(string id, int after)
    {
        var row = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        var all = Feed(row);
        var from = after < 0 ? 0 : (int)Math.Min(all.Count, (long)after + 1);
        return new ChatPage
        {
            Entries = [.. all.Skip(from).Select((e, i) => e with { Index = from + i })],
            Total = all.Count,
            Working = row.DesignerState is DesignerStates.Working,
            Status = StatusOf(row),
        };
    }

    private List<TranscriptEntry> Feed(DesignRow row)
    {
        var items = new List<(DateTimeOffset At, int Order, TranscriptEntry Entry)>();
        var order = 0;
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
                if (entry.Kind is TranscriptKind.Assistant or TranscriptKind.Error && entry.At is { } at)
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

    /// <summary>What the designer is doing, in plain words.</summary>
    public static string StatusOf(DesignRow row)
    {
        switch (row.DesignerState)
        {
            case DesignerStates.Held:
                return row.DesignerHeldUntil is { } until ? $"held until {until.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC" : "held";
            case DesignerStates.Failed:
                return row.DesignerWhy is { Length: > 0 } why ? $"failed: {why}" : "failed";
            case DesignerStates.Working:
                return row.DesignerPhase switch
                {
                    DesignerPhases.Checking => $"checking: {row.CheckFindings ?? 0} finding{(row.CheckFindings == 1 ? "" : "s")} from the last design-check",
                    DesignerPhases.Rendering => "rendering",
                    DesignerPhases.Published when row.Current is { } v => $"published v{v.ToString(CultureInfo.InvariantCulture)}",
                    _ => "drafting",
                };
            default:
                return row.Current is { } current ? $"published v{current.ToString(CultureInfo.InvariantCulture)}" : "idle";
        }
    }
}
