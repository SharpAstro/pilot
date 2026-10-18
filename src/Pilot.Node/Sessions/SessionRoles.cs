using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>A worker v1 registered (<c>&lt;state&gt;/workers/&lt;leg&gt;.json</c>): its leg, the session name it flies under and its budget.</summary>
public sealed record V1Worker(string Leg, string? Agent, long? Budget);

/// <summary>
/// What v1's state folder says about sessions, read and never written: its registered workers, by every session each has
/// run as, and the sessions its flight plans name as their pilot.
/// </summary>
public sealed class V1Registry
{
    public static readonly V1Registry Empty = new V1Registry();

    private readonly Dictionary<string, V1Worker> _bySession = new Dictionary<string, V1Worker>(StringComparer.Ordinal);
    private readonly Dictionary<string, V1Worker> _byAgent = new Dictionary<string, V1Worker>(StringComparer.Ordinal);
    private readonly HashSet<string> _pilots = new HashSet<string>(StringComparer.Ordinal);

    public V1Worker? WorkerOfSession(string sessionId) => _bySession.GetValueOrDefault(sessionId);

    public V1Worker? WorkerNamed(string agentName) => _byAgent.GetValueOrDefault(agentName);

    public bool IsPilot(string sessionId) => _pilots.Contains(sessionId);

    /// <summary>Reads <paramref name="stateFolder"/> (v1's <c>PILOT_STATE</c>, else <c>%LOCALAPPDATA%\pilot</c>); a folder that is not there is an empty registry.</summary>
    public static V1Registry Load(string stateFolder)
    {
        var registry = new V1Registry();
        foreach (var file in Files(Path.Combine(stateFolder, "workers"), "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                var w = doc.RootElement;
                if (w.ValueKind != JsonValueKind.Object || ClaudeAgentsJson.Text(w, "name") is not { Length: > 0 } leg)
                {
                    continue;
                }

                var agent = ClaudeAgentsJson.Text(w, "agent");
                var budget = w.TryGetProperty("budget", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var n) ? n : (long?)null;
                var worker = new V1Worker(leg, agent, budget);
                if (agent is not null)
                {
                    registry._byAgent[agent] = worker;
                }
                if (ClaudeAgentsJson.Text(w, "sessionId") is { } own)
                {
                    registry._bySession[own] = worker;
                }
                // One session per `say`: the worker's later sessions are its own too.
                if (w.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sessions.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.Object && ClaudeAgentsJson.Text(s, "sessionId") is { } sid)
                        {
                            registry._bySession[sid] = worker;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        foreach (var file in Files(Path.Combine(stateFolder, "plans"), "*.md"))
        {
            try
            {
                // `Pilot session:` sits in the plan's header; no plan needs reading past it.
                foreach (var line in File.ReadLines(file).Take(60))
                {
                    if (line.StartsWith("Pilot session:", StringComparison.Ordinal))
                    {
                        var id = line["Pilot session:".Length..].Trim();
                        if (ClaudeProfile.IsSafeId(id))
                        {
                            registry._pilots.Add(id);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return registry;
    }

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Where the tracker finds what it reads, and how often it looks.</summary>
/// <param name="Profile">Claude Code's profile folder.</param>
/// <param name="V1StateFolder">v1's state folder.</param>
/// <param name="ActiveInterval">The poll interval while a client is connected.</param>
/// <param name="IdleInterval">The poll interval while none is.</param>
/// <param name="EndedRetention">How long an ended session stays listed.</param>
public sealed record SessionTrackerOptions(
    ClaudeProfile Profile,
    string V1StateFolder,
    TimeSpan ActiveInterval,
    TimeSpan IdleInterval,
    TimeSpan EndedRetention)
{
    public static readonly TimeSpan DefaultActiveInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan DefaultIdleInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan DefaultEndedRetention = TimeSpan.FromHours(24);

    public static SessionTrackerOptions FromEnvironment() => new SessionTrackerOptions(
        ClaudeProfile.Default(),
        DefaultV1StateFolder(),
        DefaultActiveInterval,
        DefaultIdleInterval,
        DefaultEndedRetention);

    /// <summary>v1's <c>STATE</c>: <c>PILOT_STATE</c>, else <c>%LOCALAPPDATA%\pilot</c>, else <c>~/.local/state/pilot</c>.</summary>
    public static string DefaultV1StateFolder()
    {
        if (Environment.GetEnvironmentVariable("PILOT_STATE") is { Length: > 0 } configured)
        {
            return configured;
        }
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return Path.Combine(string.IsNullOrEmpty(local) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state") : local, "pilot");
    }
}

/// <summary>What a session is for, and which flight and leg it flies: from its name and v1's registry.</summary>
public sealed record SessionRoleInfo(SessionRole Role, string? Flight, string? Leg, long? Budget)
{
    public static readonly SessionRoleInfo AdHoc = new SessionRoleInfo(SessionRole.AdHoc, null, null, null);
}

public static partial class SessionRoles
{
    /// <summary>v1's session names that do the watchtower's work: the autopilot, and the dashboard's intercom relay.</summary>
    private static readonly string[] WatchtowerNames = ["autopilot", "pilot-dashboard"];

    /// <summary>A worker's session name, <c>&lt;FLIGHT&gt; &lt;leg&gt;</c> (<c>PIL5 node</c>, <c>TIA1611a node</c>): a flight code is capitals and a number.</summary>
    [GeneratedRegex(@"^([A-Z]{2,}[0-9]+[a-z]?) (\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerName();

    /// <summary>
    /// The role of a session. The watchtower is a session named <c>*-watchtower</c> (or v1's autopilot or relay); a
    /// worker is one v1 registered, or named like one; a pilot is the session a v1 plan names. A reviewer or co-pilot is a
    /// subagent, so is no row. Everything else is ad hoc.
    /// </summary>
    public static SessionRoleInfo Resolve(string? name, string sessionId, V1Registry registry)
    {
        if (name is { Length: > 0 } && (name.EndsWith("-watchtower", StringComparison.Ordinal) || name == "watchtower" || WatchtowerNames.Contains(name)))
        {
            return new SessionRoleInfo(SessionRole.Watchtower, null, null, null);
        }

        var registered = registry.WorkerOfSession(sessionId) ?? (name is { Length: > 0 } ? registry.WorkerNamed(name) : null);
        var match = name is { Length: > 0 } ? WorkerName().Match(name) : Match.Empty;
        if (registered is not null)
        {
            // A worker whose plan had no flight number flies as `w-<leg>`: its leg is the registered name.
            return match.Success
                ? new SessionRoleInfo(SessionRole.Worker, match.Groups[1].Value, match.Groups[2].Value, registered.Budget)
                : new SessionRoleInfo(SessionRole.Worker, null, registered.Leg, registered.Budget);
        }
        if (registry.IsPilot(sessionId))
        {
            return new SessionRoleInfo(SessionRole.Pilot, null, null, null);
        }
        return match.Success
            ? new SessionRoleInfo(SessionRole.Worker, match.Groups[1].Value, match.Groups[2].Value, null)
            : SessionRoleInfo.AdHoc;
    }
}
