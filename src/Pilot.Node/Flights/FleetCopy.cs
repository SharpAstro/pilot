using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What this node last read of one peer: its flights and legs, and when the peer last answered (#894).</summary>
/// <param name="Peer">The peer's machine prefix.</param>
/// <param name="LastHeard">When the peer last answered a read, by this node's clock.</param>
/// <param name="Flights">Every flight the peer listed in that read, landed ones included.</param>
/// <param name="Legs">Every leg of those flights.</param>
public sealed record PeerCopy(string Peer, DateTimeOffset LastHeard, FlightRow[] Flights, LegRow[] Legs);

/// <summary>
/// This node's last copy of each granted peer's flights and legs, with when that peer was last heard from, so the flights of a
/// node that has gone down can still be read: what <c>flight adopt</c> (#894) builds on. Every answered read replaces the peer's
/// copy and its heard-at; a read that was not answered changes nothing, so a peer gone silent keeps its rows and its old heard-at.
/// Kept as <c>fleet-copy.json</c> in the node's data folder and read back on start; a missing or broken file is an empty copy.
/// </summary>
public sealed class FleetCopy
{
    private readonly string? _file;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, PeerCopy> _peers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A copy kept in <paramref name="file"/> (read now); with no file it lives in memory only.</summary>
    public FleetCopy(string? file, TimeProvider time)
    {
        _file = file;
        _time = time;
        Load();
    }

    /// <summary>Where this says what it would have said on stderr: the node's log, nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The copy's file, in <paramref name="dataFolder"/>.</summary>
    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, "fleet-copy.json");

    /// <summary>Keeps <paramref name="load"/> as what <paramref name="peer"/> (its machine prefix) said just now.</summary>
    public void Record(string peer, FleetLoad load)
    {
        var copy = new PeerCopy(peer, _time.GetUtcNow(), load.Flights, load.Legs ?? []);
        lock (_lock)
        {
            _peers[peer] = copy;
            Save();
        }
    }

    /// <summary>Every peer copied, by name.</summary>
    public IReadOnlyList<PeerCopy> Peers()
    {
        lock (_lock)
        {
            return [.. _peers.Values.OrderBy(p => p.Peer, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>The copy of <paramref name="peer"/>; null when it was never heard.</summary>
    public PeerCopy? Of(string peer)
    {
        lock (_lock)
        {
            return _peers.GetValueOrDefault(peer);
        }
    }

    /// <summary>How long <paramref name="peer"/> has gone unheard, by this node's clock; null when it was never heard.</summary>
    public TimeSpan? Silence(string peer) => Of(peer) is { } copy ? _time.GetUtcNow() - copy.LastHeard : null;

    private void Load()
    {
        if (_file is null)
        {
            return;
        }
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }
            // Read with the document model and the source-generated contexts, so it is trim and AOT clean.
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("peers", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("peer", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 } peer
                    || !item.TryGetProperty("lastHeard", out var at) || !at.TryGetDateTimeOffset(out var heard))
                {
                    continue;
                }
                _peers[peer] = new PeerCopy(peer, heard, Items(item, "flights", PilotJsonContext.Default.FlightRow), Items(item, "legs", PilotJsonContext.Default.LegRow));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            // A broken or unreadable file is an empty copy: it is written afresh by the next read of a peer.
            _peers.Clear();
        }
    }

    private static T[] Items<T>(JsonElement item, string property, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
        => item.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(e => e.Deserialize(info)).OfType<T>()]
            : [];

    // Called under the lock. A temporary file moved over the old one, so a reader never sees half of it.
    private void Save()
    {
        if (_file is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var temp = _file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteStartArray("peers");
                foreach (var copy in _peers.Values.OrderBy(p => p.Peer, StringComparer.OrdinalIgnoreCase))
                {
                    json.WriteStartObject();
                    json.WriteString("peer", copy.Peer);
                    json.WriteString("lastHeard", copy.LastHeard);
                    json.WriteStartArray("flights");
                    foreach (var flight in copy.Flights)
                    {
                        JsonSerializer.Serialize(json, flight, PilotJsonContext.Default.FlightRow);
                    }
                    json.WriteEndArray();
                    json.WriteStartArray("legs");
                    foreach (var leg in copy.Legs)
                    {
                        JsonSerializer.Serialize(json, leg, PilotJsonContext.Default.LegRow);
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"fleet copy: could not write {_file}: {ex.Message}");
        }
    }
}

/// <summary>
/// The node's watch that keeps <see cref="FleetCopy"/> fresh: on a cycle it reads every granted peer, so a peer's flights are copied
/// while it is up and still there to read once it is down (#894). See <see cref="FlightCommander.CopyPeersAsync"/>.
/// </summary>
public sealed class FleetCopyWatch(FlightCommander flights, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log, nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>How often every granted peer is read.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await flights.CopyPeersAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Outlive anything one read throws: a dead watch leaves the copy to go stale, silently.
                await Error.WriteLineAsync($"fleet copy watch: one read failed, reading again: {ex.Message}");
                try
                {
                    await Task.Delay(Interval, time, stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
