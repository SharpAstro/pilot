using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>
/// What <c>~/.claude/sessions/&lt;pid&gt;.json</c> says about a live session: Claude Code's own record, which carries
/// the Remote Control id. Port of v1's <c>session_record()</c>.
/// </summary>
public sealed record SessionRecord(string? Status, string? WaitingFor, string? BridgeSessionId);

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
