using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's answer to a PermissionRequest hook: a node-started session in a flight's plane that asks leave for a call which only
/// acts inside that plane (entering the plane itself, reading or editing a file under it) is allowed at once, so the person is
/// never asked for what could do no harm. Every other call, and every other session, gets no answer, and the prompt stays with the person.
/// </summary>
/// <remarks>
/// Like <see cref="StopGate"/> it counts only a plane the node flies and only a top-level session (a subagent's call is its parent's
/// business). A path is read as written, joined to the session's folder and normalised, so a <c>..</c> that climbs out of the plane is
/// outside it; a link inside the plane that points elsewhere is not followed. A hook it cannot read gets no answer.
/// </remarks>
public sealed class PermissionGate(NodeState state)
{
    /// <summary>The tools that only read or write the file they name (their <c>file_path</c>, <c>notebook_path</c> or <c>path</c>).</summary>
    private static readonly string[] FileTools = ["Read", "Edit", "Write", "MultiEdit", "NotebookEdit"];

    /// <summary>The tools that search under a folder (their <c>path</c>, else the session's own folder).</summary>
    private static readonly string[] SearchTools = ["Glob", "Grep"];

    /// <summary>
    /// The PermissionRequest decision for <paramref name="hook"/>, as Claude Code reads it on stdout
    /// (<c>{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}</c>), or null to leave the prompt.
    /// </summary>
    public Task<string?> DecideAsync(JsonElement hook, CancellationToken cancellationToken) => Task.FromResult(Decide(hook));

    /// <summary>The allow answer for <paramref name="hook"/>, or null when the node would not allow it (see <see cref="DecideAsync"/>).</summary>
    public string? Decide(JsonElement hook)
    {
        if (hook.ValueKind != JsonValueKind.Object
            || ClaudeAgentsJson.Text(hook, "hook_event_name") != "PermissionRequest"
            || ClaudeAgentsJson.Text(hook, "tool_name") is not { Length: > 0 } tool
            || ClaudeAgentsJson.Text(hook, "cwd") is not { Length: > 0 } cwd
            || ClaudeAgentsJson.Text(hook, "agent_id") is { Length: > 0 }
            || !hook.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        FlightRow? flight;
        try
        {
            flight = StopGate.PlaneAt(state, cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return flight is not null && SafeInPlane(flight.Worktree, cwd, tool, input) ? Allow() : null;
    }

    /// <summary>
    /// Whether a call of <paramref name="tool"/> with <paramref name="input"/>, made by a session in <paramref name="cwd"/> (in the plane),
    /// only acts inside <paramref name="plane"/>: <c>EnterWorktree</c> into that same plane, or a file tool on a path under it.
    /// </summary>
    public static bool SafeInPlane(string plane, string cwd, string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (tool == "EnterWorktree")
        {
            return ClaudeAgentsJson.Text(input, "path") is { Length: > 0 } to && Under(plane, cwd, to, within: false);
        }
        if (SearchTools.Contains(tool))
        {
            // A Glob pattern that is a path of its own (rooted, or climbing out with ..) searches elsewhere, whatever its folder is.
            if (tool == "Glob" && ClaudeAgentsJson.Text(input, "pattern") is { } pattern && (Path.IsPathRooted(pattern) || pattern.Contains("..", StringComparison.Ordinal)))
            {
                return false;
            }
            return Under(plane, cwd, ClaudeAgentsJson.Text(input, "path") is { Length: > 0 } folder ? folder : cwd, within: true);
        }
        if (!FileTools.Contains(tool))
        {
            return false;
        }
        var path = ClaudeAgentsJson.Text(input, "file_path") ?? ClaudeAgentsJson.Text(input, "notebook_path") ?? ClaudeAgentsJson.Text(input, "path");
        return path is { Length: > 0 } && Under(plane, cwd, path, within: true);
    }

    /// <summary>
    /// The same question for a call known only by the line the session kept of it (<see cref="SessionRow.Asking"/>: the path of an
    /// <c>EnterWorktree</c> or of a file tool; a <c>Glob</c> or <c>Grep</c> keeps its pattern, which says nothing of its folder, so it is no
    /// match). The session is taken to be in the plane itself.
    /// </summary>
    public static bool SafeInPlane(string plane, string tool, string? path) =>
        path is { Length: > 0 } && (tool == "EnterWorktree" || FileTools.Contains(tool))
        && Under(plane, plane, path, within: tool != "EnterWorktree");

    /// <summary>
    /// Whether <paramref name="path"/> (relative to <paramref name="cwd"/>) is <paramref name="plane"/> itself, or, with
    /// <paramref name="within"/>, a file or folder under it. Never throws: a path that cannot be read is outside.
    /// </summary>
    private static bool Under(string plane, string cwd, string path, bool within)
    {
        try
        {
            var root = StopGate.Normal(plane);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Path.GetFullPath(cwd)));
            return full.Equals(root, StopGate.PathComparison)
                || within && full.StartsWith(root + Path.DirectorySeparatorChar, StopGate.PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Written through HookCommands.Write, as MergeGuard's deny is: no serializer metadata.
    private static string Allow() =>
        HookCommands.Write(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject { ["behavior"] = "allow" },
            },
        });
}
