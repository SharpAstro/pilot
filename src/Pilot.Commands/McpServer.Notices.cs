using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// Change notices for the design resources (PIL1187). The node has no design event and the designer writes draft files directly, so the server
/// itself watches: while a client has subscribed to a design's resource, one loop reads each subscribed design's record and chat every
/// <c>Poll</c> and compares with what it saw last (the version count, a hash of the draft's DESIGN.md and prototype.json, the chat's last entry),
/// and sends <c>notifications/resources/updated</c> for the URI subscribed to; a design added or removed is <c>notifications/resources/list_changed</c>.
/// </summary>
internal sealed partial class McpServer
{
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(15);

    /// <summary>What was seen of a design: its version count, its draft's content hash and its chat's last entry.</summary>
    private sealed record Seen(int Versions, string Draft, string Chat);

    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly object _noticeLock = new();
    private readonly Dictionary<string, HashSet<string>> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _listed;
    private TextWriter? _output;
    private CancellationTokenSource? _run;
    private Task? _notices;

    /// <summary>How long the notice loop waits between reads; tests pass milliseconds.</summary>
    internal TimeSpan Poll { get; init; } = DefaultPoll;

    /// <summary>Writes a line to the client: replies and notices share the lock, so a notice never lands inside a reply.</summary>
    private async Task WriteAsync(TextWriter output, string line, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            await output.WriteLineAsync(line);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task<string> SubscribeAsync(JsonElement id, ResourceReadParams given, CancellationToken cancellationToken)
    {
        var uri = given.Uri;
        if (!McpDesigns.Is(uri))
        {
            throw new McpException(ResourceNotFound, $"there is no subscribable resource {uri}; the design resources are (pilot://designs and below)");
        }
        var key = McpDesigns.DesignOf(uri!);
        Seen? seen = null;
        HashSet<string>? list;
        try
        {
            var (target, here, unknown) = await ResolvedAsync(cancellationToken);
            if (unknown is not null)
            {
                throw new McpException(InternalError, unknown);
            }
            using var client = await NodeTargets.ConnectAsync(target, socket, cancellationToken, here: here);
            if (key is not null)
            {
                seen = await ObserveAsync(client, key, cancellationToken);
            }
            list = await ListIdsAsync(client, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or WebSocketException or ObjectDisposedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new McpException(InternalError, $"{Restarting} ({ex.Message})");
        }
        lock (_noticeLock)
        {
            var bucket = key ?? "";
            if (!_subscribed.TryGetValue(bucket, out var uris))
            {
                _subscribed[bucket] = uris = new HashSet<string>(StringComparer.Ordinal);
            }
            uris.Add(uri!);
            if (seen is not null)
            {
                _seen[bucket] = seen;
            }
            _listed ??= list;
        }
        StartNotices();
        return Reply(id, new EmptyResult(), McpJsonContext.Default.EmptyResult);
    }

    private string Unsubscribe(JsonElement id, ResourceReadParams given)
    {
        if (given.Uri is { } uri && McpDesigns.Is(uri))
        {
            lock (_noticeLock)
            {
                var key = McpDesigns.DesignOf(uri) ?? "";
                if (_subscribed.TryGetValue(key, out var uris) && uris.Remove(uri) && uris.Count == 0)
                {
                    _subscribed.Remove(key);
                    _seen.Remove(key);
                }
            }
        }
        return Reply(id, new EmptyResult(), McpJsonContext.Default.EmptyResult);
    }

    private void StartNotices()
    {
        if (_output is not { } output || _run is not { } run)
        {
            return;
        }
        lock (_noticeLock)
        {
            if (_notices is { IsCompleted: false })
            {
                return;
            }
            _notices = Task.Run(() => NoticesAsync(output, run.Token), run.Token);
        }
    }

    /// <summary>Ends the notice loop with the server.</summary>
    private async Task StopNoticesAsync()
    {
        Task? running;
        lock (_noticeLock)
        {
            running = _notices;
        }
        if (_run is { } run)
        {
            await run.CancelAsync();
        }
        if (running is not null)
        {
            try
            {
                await running;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>One loop while anything is subscribed: wait, read, compare, tell. A read that fails waits for the next tick.</summary>
    private async Task NoticesAsync(TextWriter output, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string[] keys;
            lock (_noticeLock)
            {
                if (_subscribed.Count == 0)
                {
                    return;
                }
            }
            await Task.Delay(Poll, cancellationToken);
            lock (_noticeLock)
            {
                keys = [.. _subscribed.Keys.Where(k => k.Length > 0)];
            }
            try
            {
                var (target, here, unknown) = await ResolvedAsync(cancellationToken);
                if (unknown is not null)
                {
                    continue;
                }
                using var client = await NodeTargets.ConnectAsync(target, socket, cancellationToken, here: here);
                var changed = new List<string>();
                foreach (var key in keys)
                {
                    Seen now;
                    try
                    {
                        now = await ObserveAsync(client, key, cancellationToken);
                    }
                    catch (McpException)
                    {
                        continue;
                    }
                    lock (_noticeLock)
                    {
                        if (_subscribed.TryGetValue(key, out var uris) && _seen.TryGetValue(key, out var before) && before != now)
                        {
                            _seen[key] = now;
                            changed.AddRange(uris);
                        }
                    }
                }
                var ids = await ListIdsAsync(client, cancellationToken);
                var listChanged = false;
                lock (_noticeLock)
                {
                    if (ids is not null)
                    {
                        listChanged = _listed is not null && !_listed.SetEquals(ids);
                        _listed = ids;
                    }
                }
                foreach (var uri in changed)
                {
                    await WriteAsync(output, $$"""{"jsonrpc":"2.0","method":"notifications/resources/updated","params":{"uri":"{{JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(uri)}}"}}""", cancellationToken);
                }
                if (listChanged)
                {
                    await WriteAsync(output, """{"jsonrpc":"2.0","method":"notifications/resources/list_changed"}""", cancellationToken);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or WebSocketException or ObjectDisposedException or JsonException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // The node is away or answered oddly: try again at the next tick.
            }
        }
    }

    private async Task<HashSet<string>?> ListIdsAsync(NodeClient client, CancellationToken cancellationToken) =>
        await McpDesigns.ListAsync(client, Who, cancellationToken) is { } rows ? [.. rows.Select(r => r.Id)] : null;

    /// <summary>What a design is at now; refuses (as a read does) a design the node does not have.</summary>
    private async Task<Seen> ObserveAsync(NodeClient client, string id, CancellationToken cancellationToken)
    {
        var detail = await McpDesigns.ShowAsync(client, Who, id, cancellationToken);
        var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.DesignChat, Target = Verbs.DesignTarget(detail.Row.Id), Who = Who }, cancellationToken);
        var chat = answer is { Success: true, Response: { Ok: true, DesignChat: { } page } }
            ? $"{page.Total}:{page.Entries.LastOrDefault()?.Index}"
            : "";
        return new Seen(detail.Versions.Length, await DraftHashAsync(DesignPaths.DraftFolder(detail), cancellationToken), chat);
    }

    private static async Task<string> DraftHashAsync(string? folder, CancellationToken cancellationToken)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return "";
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in new[] { DesignPaths.DocumentFile(folder), DesignPaths.PrototypeFile(folder) })
        {
            try
            {
                hash.AppendData(await File.ReadAllBytesAsync(file, cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Absent or being written: counts as empty this tick.
            }
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
