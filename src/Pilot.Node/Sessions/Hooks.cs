using System.Collections.Concurrent;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// What a session's latest hook said: the state it puts the session in, what it waits on, the API error that stopped
/// it, and when. Null fields say nothing, and leave the row to what the agents listing and transcripts say.
/// </summary>
public sealed record HookState(string Event, DateTimeOffset At, SessionState? State, string? WaitingFor, string? ApiError);

/// <summary>
/// The hooks Claude Code posts to the node (<c>POST /api/v1/hooks</c>, by <c>pilot-node hook</c>), per session. A hook
/// fires on every transition the moment it happens, so a session that runs them is shown as it is, not as it was at
/// the last poll: working on a prompt or a tool call, waiting on a permission prompt (with its words), idle after a
/// turn, stopped by an API error (<c>StopFailure</c>) or a usage limit (<c>quota_auto_resume_*</c>).
/// </summary>
/// <remarks>
/// Event and field names are Claude Code's hooks reference (code.claude.com/docs/en/hooks). An event this does not
/// know changes nothing.
/// </remarks>
public sealed class HookBook
{
    private readonly ConcurrentDictionary<string, HookState> _bySession = new(StringComparer.Ordinal);

    /// <summary>The latest hook state of <paramref name="sessionId"/>, or null when none came.</summary>
    public HookState? Of(string sessionId) => _bySession.GetValueOrDefault(sessionId);

    /// <summary>Takes one hook's stdin, as Claude Code sends it; returns the session it was about, or null for one it cannot read.</summary>
    public string? Apply(JsonElement hook, DateTimeOffset at)
    {
        if (hook.ValueKind != JsonValueKind.Object
            || ClaudeAgentsJson.Text(hook, "session_id") is not { Length: > 0 } sessionId
            || ClaudeAgentsJson.Text(hook, "hook_event_name") is not { Length: > 0 } name)
        {
            return null;
        }

        // A subagent's hooks are its parent session's events, not the session's own turn.
        if (ClaudeAgentsJson.Text(hook, "agent_id") is { Length: > 0 } && name is not ("SubagentStart" or "SubagentStop"))
        {
            return sessionId;
        }

        var previous = Of(sessionId);
        HookState? next = name switch
        {
            // Hooks run async, so they can arrive out of order: a background session's UserPromptSubmit reached the
            // node before its SessionStart. A start says idle only of a session no hook has spoken for yet, or of one a
            // hook said ended: a session resumed in place starts again under the same id.
            "SessionStart" => previous is null or { State: SessionState.Ended } ? new HookState(name, at, SessionState.Idle, null, null) : null,
            "UserPromptSubmit" or "PreToolUse" or "PostToolUse" or "PostToolUseFailure" or "SubagentStart"
                => new HookState(name, at, SessionState.Working, null, null),
            "PermissionRequest" => new HookState(name, at, SessionState.Waiting,
                ClaudeAgentsJson.Text(hook, "tool_name") is { } tool ? $"permission to use {tool}" : "a permission", null),
            "Notification" => FromNotification(hook, name, at, previous),
            "Stop" => new HookState(name, at, SessionState.Idle, null, null),
            "StopFailure" => new HookState(name, at, SessionState.Idle, null, ClaudeAgentsJson.Text(hook, "error") ?? "unknown"),
            "SessionEnd" => new HookState(name, at, SessionState.Ended, null, null),
            _ => null,
        };
        if (next is not null)
        {
            _bySession[sessionId] = next;
        }
        return sessionId;
    }

    private static HookState? FromNotification(JsonElement hook, string name, DateTimeOffset at, HookState? previous)
    {
        var message = ClaudeAgentsJson.Text(hook, "message");
        return ClaudeAgentsJson.Text(hook, "notification_type") switch
        {
            "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input"
                => new HookState(name, at, SessionState.Waiting, message ?? "your answer", null),
            "idle_prompt" => new HookState(name, at, SessionState.Idle, null, previous?.ApiError),
            // Claude Code paused it at a usage limit and resumes it when the window resets: stopped until then.
            "quota_auto_resume_fired" or "quota_auto_resume_stale" or "quota_auto_resume_disabled"
                => new HookState(name, at, SessionState.Idle, null, "usage_limit_reached"),
            _ => null,
        };
    }
}
