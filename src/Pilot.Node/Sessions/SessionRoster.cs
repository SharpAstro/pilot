using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>
/// The sessions the node has started or resumed, as a small file (<c>node-sessions.json</c>, a list of job ids) in its data folder, so
/// a hook (<see cref="TestGuard"/>), which runs as its own process and never asks the node, can tell a node's session from a person's
/// by the <c>session_id</c> on its input. The session's environment (<c>PILOT_SESSION</c>, set on the claude call) says the same, but
/// <c>claude --bg</c> hands the session to a daemon, and a hook of that session may not see the call's environment: the roster is the
/// second witness, and a session is the node's when either says so. A job id is the first eight characters of the session id.
/// </summary>
public static class SessionRoster
{
    /// <summary>The most ids kept; the oldest go first.</summary>
    private const int Capacity = 2000;

    private static readonly object Lock = new();

    /// <summary>The roster's file, in <paramref name="dataFolder"/>.</summary>
    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, "node-sessions.json");

    /// <summary>The key a session is kept under: the first eight characters of its id, in lower case.</summary>
    public static string Key(string sessionId) => sessionId.Trim()[..Math.Min(8, sessionId.Trim().Length)].ToLowerInvariant();

    /// <summary>Adds the session to the roster in <paramref name="file"/>; never throws (a roster that cannot be written leaves the environment as the one witness).</summary>
    public static void Mark(string? file, string? sessionId)
    {
        if (file is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }
        try
        {
            lock (Lock)
            {
                var ids = Read(file);
                var key = Key(sessionId);
                if (ids.Contains(key))
                {
                    return;
                }
                ids.Add(key);
                if (ids.Count > Capacity)
                {
                    ids.RemoveRange(0, ids.Count - Capacity);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                var temp = file + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var json = new Utf8JsonWriter(stream))
                {
                    json.WriteStartArray();
                    foreach (var id in ids)
                    {
                        json.WriteStringValue(id);
                    }
                    json.WriteEndArray();
                }
                File.Move(temp, file, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // See above: the environment is still a witness.
        }
    }

    /// <summary>Whether the roster in <paramref name="file"/> has the session; false when there is no file, or it cannot be read.</summary>
    public static bool Contains(string? file, string? sessionId)
    {
        if (file is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        try
        {
            return Read(file).Contains(Key(sessionId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // By hand, not the serializer: the node is published native AOT.
    private static List<string> Read(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
    }
}
