using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// The MCP config every session the node starts loads, and nothing else (<c>--strict-mcp-config --mcp-config &lt;file&gt;</c>).
/// </summary>
/// <remarks>
/// What <c>claude --help</c> says of <c>--strict-mcp-config</c>: "Only use MCP servers from --mcp-config, ignoring all other
/// MCP configurations". So with no <c>--mcp-config</c> a session got no server at all (not even pilot's), and with one it
/// gets exactly the file's servers: the user's and the project's <c>.mcp.json</c> are skipped, which is what keeps a repo's
/// servers needing approval out of a background session. The claude.ai connectors are account-level and are not read from
/// those files; a session that still shows them is switched off on its environment, not here. Auto-compaction is set by
/// <c>--autocompact auto</c>, a start option like the rest, so it is kept in the recorded options and a resume passes it
/// again, with no dependence on <c>DISABLE_AUTO_COMPACT</c> being absent from the child's environment.
/// <para>
/// The file holds pilot's server (the <c>pilot</c> beside the node, <c>--node-socket &lt;socket&gt; mcp</c>) and, for a repo
/// that declares some, those servers of the repo's <c>.mcp.json</c> that its PILOT.md lists under a heading
/// <c>MCP servers for legs</c>, one name per bullet. The file is rewritten only when its contents change.
/// </para>
/// </remarks>
public static partial class SessionMcpConfig
{
    public const string ServerName = "pilot";

    public const string Heading = "MCP servers for legs";

    [GeneratedRegex(@"^#{1,6}\s*" + Heading + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    /// <summary>The server names the repo's PILOT.md declares for its legs: the bullets under the heading, backticks optional.</summary>
    public static IReadOnlyList<string> Declared(string repoPath)
    {
        try
        {
            var file = Path.Combine(repoPath, PilotMd.FileName);
            if (!File.Exists(file))
            {
                return [];
            }
            var names = new List<string>();
            var inside = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (HeadingLine().IsMatch(line))
                {
                    inside = true;
                }
                else if (line.StartsWith('#'))
                {
                    inside = false;
                }
                else if (inside && (line.StartsWith("- ") || line.StartsWith("* ")))
                {
                    var name = line[2..].Trim().Split([' ', ':', ','], 2)[0].Trim('`');
                    if (name.Length > 0 && name != ServerName)
                    {
                        names.Add(name);
                    }
                }
            }
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The config's text: pilot's server, then the declared extras the repo's <c>.mcp.json</c> defines.</summary>
    public static string Build(string pilotExe, string? socket, string repoPath)
    {
        var servers = new JsonObject
        {
            [ServerName] = new JsonObject
            {
                ["command"] = pilotExe,
                ["args"] = new JsonArray([.. (socket is null ? [] : new[] { "--node-socket", socket }).Append("mcp").Select(a => (JsonNode)JsonValue.Create(a))]),
            },
        };
        var declared = Declared(repoPath);
        if (declared.Count > 0)
        {
            try
            {
                var mcp = Path.Combine(repoPath, ".mcp.json");
                if (File.Exists(mcp)
                    && JsonNode.Parse(File.ReadAllText(mcp), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject root
                    && root["mcpServers"] is JsonObject repoServers)
                {
                    foreach (var name in declared.Where(n => repoServers[n] is not null && servers[n] is null))
                    {
                        servers[name] = repoServers[name]!.DeepClone();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable .mcp.json leaves the session with pilot's server alone.
            }
        }
        return new JsonObject { ["mcpServers"] = servers }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Writes the config for a session of the repo at <paramref name="repoPath"/> under <paramref name="folder"/> (pilot's own
    /// <c>mcp.json</c>, or <c>mcp-&lt;repo&gt;.json</c> when the repo declares extras) and returns its path. An unchanged
    /// file is left as it is; a file that cannot be written is still named, so claude says why.
    /// </summary>
    public static string Ensure(string folder, string pilotExe, string? socket, string repoPath)
    {
        var text = Build(pilotExe, socket, repoPath);
        var extras = Declared(repoPath).Count > 0;
        var repo = new string([.. Path.GetFileName(repoPath.TrimEnd('/', '\\')).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        var path = Path.Combine(folder, extras && repo.Length > 0 ? $"mcp-{repo}.json" : "mcp.json");
        try
        {
            Directory.CreateDirectory(folder);
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                File.WriteAllText(path, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return path;
    }
}
