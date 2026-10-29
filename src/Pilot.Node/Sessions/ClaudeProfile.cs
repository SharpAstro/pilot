using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>
/// What <c>~/.claude/sessions/&lt;pid&gt;.json</c> says about a live session: Claude Code's own record, which carries
/// the Remote Control id. Port of v1's <c>session_record()</c>.
/// </summary>
public sealed record SessionRecord(string? Status, string? WaitingFor, string? BridgeSessionId);

/// <summary>
/// Where a live session listens for cross-session messages, from its record and its key file: the endpoint
/// (<c>messagingSocketPath</c>), the wire's version (<c>peerProtocol</c>) and the token a sender must present first.
/// </summary>
/// <param name="Pid">The session's process.</param>
/// <param name="SessionId">The session it is, which a message names so a recycled endpoint cannot take it.</param>
/// <param name="SocketPath">A named pipe (<c>\\.\pipe\LOCAL\cc-msg-...</c>) on Windows, a unix socket elsewhere; null when the record has none.</param>
/// <param name="PeerProtocol">The version of the wire the endpoint speaks; null when the record has none.</param>
/// <param name="Token">The <c>peerToken</c> of the key file; null when there is none to read.</param>
public sealed record IntercomEndpoint(int Pid, string SessionId, string? SocketPath, int? PeerProtocol, string? Token);

/// <summary>
/// Claude Code's profile folder (<c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>), read and never written: the session
/// records, the transcripts under <c>projects</c> and their subagent files.
/// </summary>
public sealed class ClaudeProfile(string root)
{
    public string Root { get; } = root;

    public static ClaudeProfile Default()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return new ClaudeProfile(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured);
    }

    /// <summary>A session id names a file, so only what a UUID or a short id is made of may become part of a path.</summary>
    public static bool IsSafeId(string id) => id.Length is > 0 and <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The session record of process <paramref name="pid"/>, or null when there is none or it is unreadable.</summary>
    public SessionRecord? ReadSessionRecord(int pid)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(Root, "sessions", $"{pid}.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var record = doc.RootElement;
            return record.ValueKind == JsonValueKind.Object
                ? new SessionRecord(ClaudeAgentsJson.Text(record, "status"), ClaudeAgentsJson.Text(record, "waitingFor"), ClaudeAgentsJson.Text(record, "bridgeSessionId"))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The intercom endpoint of the live session <paramref name="sessionId"/>: the record under <c>sessions</c> that names it
    /// and whose process runs (the newest, when a resume left more), with the token of its <c>&lt;pid&gt;.&lt;hash&gt;.key</c>
    /// file. Null when no live process has the session.
    /// </summary>
    public IntercomEndpoint? FindIntercom(string sessionId)
    {
        var folder = Path.Combine(Root, "sessions");
        IntercomEndpoint? best = null;
        long bestAt = long.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var pid))
                {
                    continue;
                }
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = JsonDocument.Parse(stream);
                    var record = doc.RootElement;
                    if (record.ValueKind != JsonValueKind.Object || ClaudeAgentsJson.Text(record, "sessionId") != sessionId || !IsRunning(pid))
                    {
                        continue;
                    }
                    var at = record.TryGetProperty("updatedAt", out var updated) && updated.TryGetInt64(out var ms) ? ms : 0;
                    if (best is not null && at < bestAt)
                    {
                        continue;
                    }
                    var protocol = record.TryGetProperty("peerProtocol", out var version) && version.TryGetInt32(out var v) ? v : (int?)null;
                    best = new IntercomEndpoint(pid, sessionId, ClaudeAgentsJson.Text(record, "messagingSocketPath"), protocol, ReadPeerToken(folder, pid, ClaudeAgentsJson.Text(record, "pidDomain")));
                    bestAt = at;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return best;
    }

    /// <summary>
    /// The <c>peerToken</c> in <c>&lt;pid&gt;.&lt;sha256 of the socket path&gt;.key</c>. Claude Code names the file by a hash of the
    /// canonical path, which the node need not rebuild: a session has the one key it published for its own pid, the newest
    /// when a restart left more. Skipped when its <c>pidDomain</c> differs from the record's.
    /// </summary>
    private static string? ReadPeerToken(string folder, int pid, string? pidDomain)
    {
        string? token = null;
        var newest = DateTime.MinValue;
        foreach (var key in Directory.EnumerateFiles(folder, $"{pid}.*.key"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(key));
                var written = File.GetLastWriteTimeUtc(key);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && written >= newest
                    && ClaudeAgentsJson.Text(doc.RootElement, "peerToken") is { Length: > 0 } found
                    && (pidDomain is null || ClaudeAgentsJson.Text(doc.RootElement, "pidDomain") is not { } domain || domain == pidDomain))
                {
                    token = found;
                    newest = written;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        return token;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The transcript of a session, <c>projects/&lt;folder&gt;/&lt;sessionId&gt;.jsonl</c>, or null when none is there (yet).</summary>
    public string? FindTranscript(string sessionId)
    {
        if (!IsSafeId(sessionId))
        {
            return null;
        }

        var projects = Path.Combine(Root, "projects");
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(folder, sessionId + ".jsonl");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>The subagent transcripts of the session whose own is <paramref name="transcript"/>: <c>&lt;sessionId&gt;/subagents/agent-*.jsonl</c> beside it.</summary>
    public static IReadOnlyList<string> SubagentFiles(string transcript)
    {
        var folder = Path.Combine(Path.GetDirectoryName(transcript) ?? "", Path.GetFileNameWithoutExtension(transcript), "subagents");
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "agent-*.jsonl").Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
