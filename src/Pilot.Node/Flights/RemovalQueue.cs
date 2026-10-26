using System.Text.Json;
using Pilot.Contracts;

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

/// <summary>
/// Tries the removal queue once at the node's start, before the services that touch those paths run, and again when the
/// node has had no working session for <see cref="IdleFor"/> (at most once per <see cref="IdleFor"/>; the PR review sweep
/// never tries it). After each try, a folder queued for more than <see cref="OlderThan"/> or <see cref="MoreStarts"/> starts
/// rings once on a For-you card naming it and, where Windows can say, what holds it; the card closes when none is left.
/// </summary>
public sealed class RemovalQueueStart(RemovalQueue queue, NodeState? state = null, TimeProvider? clock = null) : BackgroundService, IHostedLifecycleService
{
    /// <summary>What the For-you card is.</summary>
    public const string Kind = "removal-held";

    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private DateTimeOffset _lastWorking;
    private DateTimeOffset _lastTried;

    /// <summary>How long no session has been working before the queue is tried again, and the least between two tries.</summary>
    public TimeSpan IdleFor { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often it looks at whether the node is idle.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>A folder queued for longer than this rings.</summary>
    public TimeSpan OlderThan { get; init; } = TimeSpan.FromDays(1);

    /// <summary>A folder that has outlasted more starts than this rings.</summary>
    public int MoreStarts { get; init; } = 3;

    /// <summary>What names the processes holding a folder; a test's can say otherwise.</summary>
    public Func<string, IReadOnlyList<string>> Holders { get; init; } = folder => Updates.FileHolders.OfFolder(folder);

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _lastWorking = _lastTried = _time.GetUtcNow();
        Try();
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, stopping);
                Look();
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"removal queue: one look failed, looking again: {ex.Message}");
            }
        }
    }

    /// <summary>One look: tries the queue if no session has been working for <see cref="IdleFor"/> and it was not tried within it.</summary>
    public bool Look()
    {
        var now = _time.GetUtcNow();
        if (state is not null && state.Sessions().Any(r => r.State == SessionState.Working))
        {
            _lastWorking = now;
            return false;
        }
        if (now - _lastWorking < IdleFor || now - _lastTried < IdleFor)
        {
            return false;
        }
        _lastTried = now;
        Try();
        return true;
    }

    private void Try()
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
        if (state is null)
        {
            return;
        }
        var now = _time.GetUtcNow();
        var due = held.Select(h => h.Entry).Where(e => e.StartsTried > MoreStarts || now - e.AddedAt > OlderThan).ToList();
        if (due.Count == 0)
        {
            state.CloseCardsAbout(Verbs.NodeTarget, "the queued folders are gone or no longer overdue", Kind);
            return;
        }
        var lines = due.Select(e =>
        {
            var holders = Holders(e.Path);
            return $"{e.Path}: {e.OwnerKind} {e.Owner}, queued {e.AddedAt:u}, {e.StartsTried} starts tried, held by "
                + (holders.Count > 0 ? string.Join(", ", holders) : "holder unknown") + $" ({e.Why})";
        });
        state.RaiseCard(CardBin.ForYou, Kind, Verbs.NodeTarget,
            $"{due.Count} folder{(due.Count == 1 ? "" : "s")} Windows still holds open",
            string.Join('\n', lines));
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
