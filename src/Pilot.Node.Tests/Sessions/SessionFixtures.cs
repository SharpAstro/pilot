using System.Globalization;
using System.Text;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Tests.Sessions;

/// <summary>A <c>claude agents --json --all</c> that prints what a test says, or fails as a test says.</summary>
internal sealed class FakeClaudeAgents : IClaudeAgents
{
    private readonly TaskCompletionSource _firstListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _listings;

    public string Json { get; set; } = "[]";

    public Exception? Failure { get; set; }

    /// <summary>How many listings were asked for.</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <summary>Makes the first listing wait until <see cref="ReleaseFirstListing"/>: a tracker whose first poll runs late.</summary>
    public FakeClaudeAgents HoldFirstListing()
    {
        Held = true;
        return this;
    }

    private bool Held { get; set; }

    public void ReleaseFirstListing() => _firstListing.TrySetResult();

    public async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _listings);
        if (number == 1 && Held)
        {
            await _firstListing.Task.WaitAsync(cancellationToken);
        }
        return Failure is { } failure ? throw failure : Json;
    }
}

/// <summary>A clock a test moves.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>The zone its local times are in: UTC unless a test says otherwise, so no test depends on the machine's.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A Claude profile folder of a test's own, with made-up sessions in them: nothing here is a real
/// transcript, and nothing reads this machine's.
/// </summary>
internal sealed class TempProfile : IDisposable
{
    public TempProfile()
    {
        Folder = Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Folder, "claude", "projects", "proj-a"));
        Directory.CreateDirectory(Path.Combine(Folder, "claude", "sessions"));
        Profile = new ClaudeProfile(Path.Combine(Folder, "claude"));
    }

    public string Folder { get; }

    public ClaudeProfile Profile { get; }

    public SessionTrackerOptions Options(TimeSpan? retention = null) =>
        new SessionTrackerOptions(Profile, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15), retention ?? SessionTrackerOptions.DefaultEndedRetention);

    public string TranscriptPath(string sessionId) => Path.Combine(Folder, "claude", "projects", "proj-a", sessionId + ".jsonl");

    public string SubagentPath(string sessionId, string agent)
    {
        var folder = Path.Combine(Folder, "claude", "projects", "proj-a", sessionId, "subagents");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"agent-{agent}.jsonl");
    }

    /// <summary>Appends lines, each ended by a newline.</summary>
    public void Append(string path, params string[] lines) => File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public void WriteSessionRecord(int pid, string json) => File.WriteAllText(Path.Combine(Folder, "claude", "sessions", pid + ".json"), json);


    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scanner or a socket still closing; the temp folder is the OS's to clear.
        }
    }
}

/// <summary>Made-up transcript lines in the shape Claude Code writes them.</summary>
internal static class Lines
{
    public static string Assistant(
        string messageId,
        long input = 0,
        long cacheCreation = 0,
        long cacheRead = 0,
        long output = 0,
        string model = "claude-opus-5-5",
        string? requestedModel = null,
        string? effort = "medium",
        string at = "2026-10-18T09:00:00.000Z",
        bool sidechain = false,
        string content = "[{\"type\":\"text\",\"text\":\"made up\"}]")
    {
        var sb = new StringBuilder("{\"type\":\"assistant\",\"uuid\":\"u-").Append(messageId).Append("-").Append(at).Append('"');
        sb.Append(",\"isSidechain\":").Append(sidechain ? "true" : "false");
        sb.Append(",\"timestamp\":\"").Append(at).Append('"');
        if (requestedModel is not null)
        {
            sb.Append(",\"requestedModel\":\"").Append(requestedModel).Append('"');
        }
        if (effort is not null)
        {
            sb.Append(",\"effort\":\"").Append(effort).Append('"');
        }
        sb.Append(",\"message\":{\"id\":\"").Append(messageId).Append("\",\"model\":\"").Append(model).Append("\",\"content\":").Append(content);
        sb.Append(string.Create(CultureInfo.InvariantCulture, $",\"usage\":{{\"input_tokens\":{input},\"cache_creation_input_tokens\":{cacheCreation},\"cache_read_input_tokens\":{cacheRead},\"output_tokens\":{output}}}}}}}"));
        return sb.ToString();
    }

    public static string User(string text) => "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}";

    public static string AskQuestion(string messageId, string toolId, string question) =>
        Assistant(messageId, input: 1, content: "[{\"type\":\"tool_use\",\"id\":\"" + toolId + "\",\"name\":\"AskUserQuestion\",\"input\":{\"questions\":[{\"question\":\"" + question + "\",\"header\":\"h\"}]}}]");

    public static string ToolResult(string toolId) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"" + toolId + "\",\"content\":\"answered\"}]}}";
}

/// <summary>Made-up <c>claude agents --json --all</c> rows.</summary>
internal static class AgentRows
{
    public const string SessionA = "a1b2c3d4-0000-4000-8000-000000000001";
    public const string SessionB = "b2c3d4e5-0000-4000-8000-000000000002";
    public const string SessionC = "c3d4e5f6-0000-4000-8000-000000000003";

    public static string Interactive(string sessionId, int pid, string name, string status = "idle", string cwd = "C:\\\\work\\\\repo", long startedAt = 1792000000000) =>
        $"{{\"pid\":{pid},\"cwd\":\"{cwd}\",\"kind\":\"interactive\",\"startedAt\":{startedAt},\"sessionId\":\"{sessionId}\",\"name\":\"{name}\",\"status\":\"{status}\"}}";

    public static string Background(string sessionId, string name, string state, int? pid = null, string? status = null, string cwd = "C:\\\\work\\\\repo", long startedAt = 1792000000000) =>
        $"{{{(pid is { } p ? $"\"pid\":{p}," : "")}\"id\":\"{sessionId[..8]}\",\"cwd\":\"{cwd}\",\"kind\":\"background\",\"startedAt\":{startedAt},\"sessionId\":\"{sessionId}\",\"name\":\"{name}\",\"state\":\"{state}\"{(status is null ? "" : $",\"status\":\"{status}\"")}}}";

    public static string List(params string[] rows) => "[" + string.Join(",", rows) + "]";
}

/// <summary>An event sink that keeps what the node sends it, to count the deltas a poll made.</summary>
internal sealed class CollectingSink : INodeEventSink
{
    private readonly List<NodeEvent> _events = [];

    public int Capacity => 10_000;

    public IReadOnlyList<NodeEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public bool TryWrite(byte[] message)
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize(message, PilotJsonContext.Default.NodeEvent)!;
        lock (_events)
        {
            _events.Add(parsed);
        }
        return true;
    }

    public void Overflowed()
    {
    }
}
