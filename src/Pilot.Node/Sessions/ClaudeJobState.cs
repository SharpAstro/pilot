using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>
/// What Claude Code's own record of a background job says: its <c>state</c>, when it has one its <c>detail</c>, and when its
/// turn last reached a terminal state (<c>lastTerminalAt</c>, null while it never has).
/// </summary>
public sealed record ClaudeJobRecord(string State, string? Detail, DateTimeOffset? LastTerminalAt = null)
{
    public bool Failed => State.Equals("failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the job's turn is over: its state is done or blocked. Claude Code ends the process of such a <c>claude --bg</c> job a
    /// minute or two later (PIL815), so a job that reads so and is gone ended by design, not by a crash. Read off the state, not
    /// <see cref="LastTerminalAt"/>: a blocked job that never set a terminal time was ended the same way (SE1, 2026-11-06).
    /// </summary>
    public bool TurnFinished =>
        State.Equals("done", StringComparison.OrdinalIgnoreCase) || State.Equals("blocked", StringComparison.OrdinalIgnoreCase);
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
            var terminal = root.TryGetProperty("lastTerminalAt", out var t) && t.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null as DateTimeOffset?;
            return new ClaudeJobRecord(state.GetString() ?? "", detail, terminal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
