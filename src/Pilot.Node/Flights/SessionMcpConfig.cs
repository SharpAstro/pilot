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
/// servers needing approval out of a background session. Claude Code also leaves the claude.ai connectors out under
/// <c>--strict-mcp-config</c> ("MCP servers are restricted to explicitly passed config"), so nothing else is needed for them.
/// <c>--autocompact auto</c> pins the auto-compact window to Claude Code's default; it does not switch compaction on (that is
/// the <c>autoCompactEnabled</c> setting, on by default, overridden by <c>DISABLE_AUTO_COMPACT</c> in the environment). It is a
/// start option like the rest, so it is kept in the recorded options and a resume passes it again.
/// <para>
/// The file holds pilot's server (the <c>pilot</c> beside the node, <c>--node-socket &lt;socket&gt; mcp</c>) and, for a repo
/// that declares some, those servers of the repo's <c>.mcp.json</c> that its PILOT.md lists under a heading
/// <c>MCP servers for legs</c>, one name per bullet. The file is rewritten only when its contents change.
/// </para>
/// </remarks>
public static partial class SessionMcpConfig
{
    public const string ServerName = "pilot";

    /// <summary>The name of the desktop cat's server (<c>pilot-buddy mcp</c>, #1367), given to the watchtower session only, in a file of its own so a worker's config never gains it.</summary>
    public const string BuddyServerName = "buddy";

    /// <summary>
    /// The <c>pilot-buddy</c> binary beside <paramref name="nodeFolder"/> (the folder the node runs from), or null when it is absent: a node
    /// with no buddy installed leaves the server out rather than naming a binary claude cannot start.
    /// </summary>
    public static string? BuddyBeside(string nodeFolder)
    {
        var exe = Path.Combine(nodeFolder, OperatingSystem.IsWindows() ? "pilot-buddy.exe" : "pilot-buddy");
        return File.Exists(exe) ? exe : null;
    }

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

    /// <summary>
    /// The config's text: pilot's server, then the declared extras the repo's <c>.mcp.json</c> defines. Pilot's server is
    /// <c>pilot mcp</c> over stdio, or with <paramref name="http"/> the node's own endpoint (#1385) and the session's token.
    /// </summary>
    public static string Build(string pilotExe, string? socket, string repoPath, string? buddyExe = null, McpHttpEntry? http = null)
    {
        var servers = new JsonObject
        {
            [ServerName] = http is { } endpoint
                ? new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = endpoint.Url,
                    ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {endpoint.Token}" },
                }
                : new JsonObject
                {
                    ["command"] = pilotExe,
                    ["args"] = new JsonArray([.. (socket is null ? [] : new[] { "--node-socket", socket }).Append("mcp").Select(a => (JsonNode)JsonValue.Create(a))]),
                },
        };
        if (buddyExe is { Length: > 0 })
        {
            servers[BuddyServerName] = new JsonObject { ["command"] = buddyExe, ["args"] = new JsonArray("mcp") };
        }
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
    public static string Ensure(string folder, string pilotExe, string? socket, string repoPath, string? buddyExe = null, McpHttpEntry? http = null)
    {
        var text = Build(pilotExe, socket, repoPath, buddyExe, http);
        var extras = Declared(repoPath).Count > 0;
        var repo = Safe(Path.GetFileName(repoPath.TrimEnd('/', '\\')));
        var name = extras && repo.Length > 0 ? $"mcp-{repo}" : "mcp";
        // The watchtower's file (with the buddy) is its own, so it never overwrites the file the workers load. A file with a
        // session's token (#1385) is that session's alone, under mcp-sessions/, as no other session may call as it.
        var file = buddyExe is { Length: > 0 } ? $"{name}-buddy.json" : $"{name}.json";
        var path = http is { } endpoint ? Path.Combine(folder, SessionsFolder, $"{Safe(endpoint.Session)}-{file}") : Path.Combine(folder, file);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    /// <summary>The folder, under the node's data folder, of the configs that hold a session's token.</summary>
    public const string SessionsFolder = "mcp-sessions";

    private static string Safe(string name) => new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
}

/// <summary>Pilot's MCP server as the node serves it over HTTP (#1385), for one session: the URL, the session's name and its token.</summary>
public sealed record McpHttpEntry(string Url, string Session, string Token);
