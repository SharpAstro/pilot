using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>What Claude Code's own record of a background job says: its <c>state</c> and, when it has one, its <c>detail</c>.</summary>
public sealed record ClaudeJobRecord(string State, string? Detail)
{
    public bool Failed => State.Equals("failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads <c>&lt;profile&gt;/jobs/&lt;id&gt;/state.json</c>, Claude Code's record of a job it started with <c>--bg</c>. It is
/// the only place a job that never came up says why (DE1 2026-10-29: nine watchtowers failed with ENAMETOOLONG while
/// node.log said nothing).
/// </summary>
public sealed class ClaudeJobState(string jobsFolder)
{
    public string JobsFolder { get; } = jobsFolder;

    /// <summary>The record of job <paramref name="job"/>; null when it has none or it cannot be read.</summary>
    public ClaudeJobRecord? Read(string job)
    {
        if (job.Length == 0 || job.AsSpan().IndexOfAny('/', '\\') >= 0 || job.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            using var stream = new FileStream(Path.Combine(JobsFolder, job, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return new ClaudeJobRecord(state.GetString() ?? "", detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
