using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pilot.Node.Flights;

/// <summary>
/// The Claude Code settings a plane gets of its own: its <c>.claude/settings.local.json</c>, which git ignores. A repo's
/// <c>.mcp.json</c> (tianwen's debug inspectors) asks for its servers to be approved the first time a session starts
/// there, and a background session has no one to ask, so it sits "blocked" and dies. A worker starts with
/// <c>--strict-mcp-config</c>, but not everything that wakes one does: a copy Claude Code starts, a peer session's
/// message. Every server the repo's file names is listed as disabled here, so no session in the plane is asked.
/// </summary>
public static class PlaneSettings
{
    public const string FileName = "settings.local.json";

    /// <summary>Lists the plane's <c>.mcp.json</c> servers under <c>disabledMcpjsonServers</c>, keeping what else the file holds.</summary>
    /// <returns>The servers listed, none when the repo has no <c>.mcp.json</c> or it names none.</returns>
    public static IReadOnlyList<string> DisableRepoMcpServers(string plane)
    {
        try
        {
            var mcp = Path.Combine(plane, ".mcp.json");
            if (!File.Exists(mcp) || Parse(File.ReadAllText(mcp))?["mcpServers"] is not JsonObject servers || servers.Count == 0)
            {
                return [];
            }
            var names = servers.Select(s => s.Key).ToArray();
            var file = Path.Combine(plane, ".claude", FileName);
            var root = (File.Exists(file) ? Parse(File.ReadAllText(file)) : null) ?? new JsonObject();
            var disabled = root["disabledMcpjsonServers"] as JsonArray ?? new JsonArray();
            var have = disabled.Select(n => n is JsonValue v && v.TryGetValue<string>(out var name) ? name : null).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var name in names.Where(n => !have.Contains(n)))
            {
                disabled.Add((JsonNode)JsonValue.Create(name));
            }
            root["disabledMcpjsonServers"] = disabled;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var stream = File.Create(file))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                root.WriteTo(writer);
            }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A plane without these settings still flies: its workers start with --strict-mcp-config.
            return [];
        }
    }

    private static JsonObject? Parse(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
}
