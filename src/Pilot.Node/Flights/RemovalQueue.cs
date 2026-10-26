using System.Text.Json;

namespace Pilot.Node.Flights;

/// <summary>A folder waiting to be removed: what it belonged to, when it was queued, why, and how many starts have tried it.</summary>
public sealed record PendingRemoval(string Path, string OwnerKind, string Owner, DateTimeOffset AddedAt, string Why, int StartsTried);

/// <summary>
/// A folder Windows holds open is no reason to keep a review or a landed plane open with a card tried again every sweep
/// (#284): it goes in this queue, <c>pending-removals.json</c> in the node's data folder, and the thing it belonged to
/// finishes anyway. The queue is tried once at the node's start, before anything touches those paths, when whatever held
/// the folder has most likely let go.
/// </summary>
public sealed class RemovalQueue(string file, TimeProvider time)
{
    public const string PlaneKind = "plane";
    public const string ReviewKind = "review";
    public const string CacheKind = "cache";

    private readonly object _lock = new();

    /// <summary>The queue's file, in <paramref name="dataFolder"/>.</summary>
    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, "pending-removals.json");

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public IReadOnlyList<PendingRemoval> Entries()
    {
        lock (_lock)
        {
            return [.. Read()];
        }
    }

    public bool Contains(string path)
    {
        var full = Full(path);
        lock (_lock)
        {
            return Read().Any(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Queues <paramref name="path"/>; one already queued (the same full path, any case) stays as it is.</summary>
    public void Add(string path, string ownerKind, string owner, string why)
    {
        var full = Full(path);
        lock (_lock)
        {
            var entries = Read();
            if (entries.Any(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            entries.Add(new PendingRemoval(full, ownerKind, owner, time.GetUtcNow(), why, 0));
            Write(entries);
        }
    }

    /// <summary>Tries to remove <paramref name="path"/> if it is queued: dropped once it is gone, kept when still held.</summary>
    public bool TryOne(string path)
    {
        var full = Full(path);
        lock (_lock)
        {
            var entries = Read();
            if (entries.FindIndex(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)) is not (>= 0 and var at))
            {
                return true;
            }
            if (!TryDelete(full, out _))
            {
                return false;
            }
            entries.RemoveAt(at);
            Write(entries);
            return true;
        }
    }

    /// <summary>Tries every entry once: those removed are dropped, those still held have their starts raised and stay.</summary>
    public (IReadOnlyList<PendingRemoval> Removed, IReadOnlyList<(PendingRemoval Entry, string Why)> Held) TryAll()
    {
        lock (_lock)
        {
            var kept = new List<PendingRemoval>();
            var removed = new List<PendingRemoval>();
            var held = new List<(PendingRemoval, string)>();
            var entries = Read();
            foreach (var entry in entries)
            {
                if (TryDelete(entry.Path, out var why))
                {
                    removed.Add(entry);
                    continue;
                }
                var again = entry with { StartsTried = entry.StartsTried + 1 };
                kept.Add(again);
                held.Add((again, why));
            }
            if (entries.Count > 0)
            {
                Write(kept);
            }
            return (removed, held);
        }
    }

    /// <summary>Deletes a folder, read-only files cleared first, as a worktree's git objects are; true when it is gone.</summary>
    public static bool TryDelete(string folder, out string why)
    {
        why = "";
        if (!Directory.Exists(folder))
        {
            return true;
        }
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = ex.Message;
            return false;
        }
    }

    private List<PendingRemoval> Read()
    {
        var entries = new List<PendingRemoval>();
        try
        {
            if (!File.Exists(file))
            {
                return entries;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } p)
                {
                    entries.Add(new PendingRemoval(
                        p,
                        item.TryGetProperty("ownerKind", out var kind) ? kind.GetString() ?? "" : "",
                        item.TryGetProperty("owner", out var owner) ? owner.GetString() ?? "" : "",
                        item.TryGetProperty("addedAt", out var at) && at.TryGetDateTimeOffset(out var when) ? when : default,
                        item.TryGetProperty("why", out var why) ? why.GetString() ?? "" : "",
                        item.TryGetProperty("startsTried", out var tried) && tried.TryGetInt32(out var n) ? n : 0));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A broken or unreadable file is an empty queue: it is written afresh by the next change.
        }
        return entries;
    }

    private void Write(List<PendingRemoval> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temp = file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartArray();
                foreach (var e in entries)
                {
                    json.WriteStartObject();
                    json.WriteString("path", e.Path);
                    json.WriteString("ownerKind", e.OwnerKind);
                    json.WriteString("owner", e.Owner);
                    json.WriteString("addedAt", e.AddedAt);
                    json.WriteString("why", e.Why);
                    json.WriteNumber("startsTried", e.StartsTried);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"removal queue: could not write {file}: {ex.Message}");
        }
    }
}

/// <summary>Tries the removal queue once at the node's start, before the services that touch those paths run.</summary>
public sealed class RemovalQueueStart(RemovalQueue queue) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var (removed, held) = queue.TryAll();
        foreach (var entry in removed)
        {
            Console.Error.WriteLine($"removal queue: removed {entry.Path} ({entry.OwnerKind} {entry.Owner})");
        }
        foreach (var (entry, why) in held)
        {
            Console.Error.WriteLine($"removal queue: kept {entry.Path} ({entry.OwnerKind} {entry.Owner}), start {entry.StartsTried}: {why}");
        }
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
