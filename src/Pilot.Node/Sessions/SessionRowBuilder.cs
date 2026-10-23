using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>Where a session is now: from the agents row's <c>status</c> while it runs, else its <c>state</c>. Port of v1's <c>session_view()</c> status.</summary>
public static class SessionStates
{
    /// <param name="status">busy, idle or waiting: the agents row's, else the session record's.</param>
    /// <param name="state">A background session's: working, blocked, done, failed.</param>
    /// <param name="alive">Whether its process still runs (the agents row has a pid).</param>
    public static SessionState Map(string? status, string? state, bool alive = false)
    {
        if (state == "failed")
        {
            return SessionState.Failed;
        }

        // Claude Code calls a background session's job done once a turn ends, though its process lives on, idle: while
        // it reports a status the session is running, whatever its state says.
        switch (status)
        {
            case "busy":
                return SessionState.Working;
            case "waiting":
                return SessionState.Waiting;
            case "idle":
                return SessionState.Idle;
        }

        // Blocked: waiting on something it cannot ask for in the background (an approval). While its process runs it is
        // waiting, not ended, so it reads as waiting on a person and the leg watch rings for it rather than taking it for a
        // stopped session. Claude Code leaves a session it stopped marked blocked too, with no process: that one ended.
        return state switch
        {
            "working" => SessionState.Working,
            "blocked" when alive => SessionState.Waiting,
            _ => SessionState.Ended,
        };
    }

    /// <summary>What a blocked background session waits on, when nothing more precise says.</summary>
    public const string BlockedWaitingFor = "an approval Claude Code cannot ask for in the background (a project's MCP servers, a trust prompt); open the session to answer it";
}

/// <summary>A session's row from its parts. Pure: the same parts always make the same row.</summary>
public static class SessionRowBuilder
{
    /// <param name="agent">The agents row.</param>
    /// <param name="transcripts">What its transcripts say.</param>
    /// <param name="record">Its session record, while its process lives.</param>
    /// <param name="repo">The repo its folder belongs to.</param>
    /// <param name="role">Its role, flight, leg and budget.</param>
    /// <param name="hook">The session's latest hook, which has the last word on a running session's state: it fired on
    /// the transition itself, where the listing is read every few seconds.</param>
    public static SessionRow Build(ClaudeAgentRow agent, TranscriptFacts transcripts, SessionRecord? record, string? repo, SessionRoleInfo role, HookState? hook = null)
    {
        var state = SessionStates.Map(agent.Status ?? record?.Status, agent.State, agent.Pid is not null);
        string? hookWaiting = null;
        // A live process is never ended, whatever a hook said: a background session resumed in place sends its SessionEnd
        // after the resumed process is up, and that once left every watchtower the node told something as ended while it
        // ran on, so the node started another (eleven alive at once on DE1, 2026-10-23).
        if (hook?.State is { } hooked && (agent.Pid is not null ? hooked != SessionState.Ended : hooked == SessionState.Ended))
        {
            state = hooked;
            hookWaiting = hook.WaitingFor;
        }
        // The transcript's record of an API stop, or the hook's; a later reply or turn clears both.
        var apiError = transcripts.Stop?.Error ?? (hook?.State is SessionState.Working ? null : hook?.ApiError);
        return new SessionRow
        {
            Id = agent.ShortId,
            SessionId = agent.SessionId,
            Name = agent.Name is { Length: > 0 } ? agent.Name : agent.ShortId,
            Kind = string.Equals(agent.Kind, "background", StringComparison.Ordinal) ? SessionKind.Background : SessionKind.Interactive,
            Cwd = agent.Cwd,
            Repo = repo,
            Role = role.Role,
            Flight = role.Flight,
            Leg = role.Leg,
            Model = transcripts.Model,
            Effort = transcripts.Effort,
            ContextTokens = transcripts.ContextTokens,
            ContextWindow = ContextWindows.For(transcripts.Model, transcripts.RequestedModel, transcripts.ContextTokens),
            LastResponseAt = transcripts.LastResponseAt,
            State = state,
            WaitingFor = state == SessionState.Waiting
                ? hookWaiting ?? agent.WaitingFor ?? record?.WaitingFor ?? transcripts.PendingQuestion ?? (agent.State == "blocked" ? SessionStates.BlockedWaitingFor : null)
                : null,
            SpendWeighted = transcripts.SpendWeighted,
            Subagents = transcripts.Subagents is { Count: > 0 } subagents ? [.. subagents] : null,
            ApiError = apiError,
            ApiErrorResetsAt = transcripts.Stop?.ResetsAt,
            Budget = role.Budget,
            RemoteControlUrl = record?.BridgeSessionId is { Length: > 0 } bridge ? $"https://claude.ai/code/{bridge}" : null,
            StartedAt = agent.StartedAt ?? DateTimeOffset.UnixEpoch,
        };
    }
}

/// <summary>The repo a folder belongs to: the git root of a checkout, or of the main checkout a linked worktree was made from.</summary>
public static class RepoOf
{
    /// <returns>The repo's folder name (<c>pilot</c>), or null outside any repo.</returns>
    public static string? Find(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd))
        {
            return null;
        }

        try
        {
            for (var dir = new DirectoryInfo(cwd); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(dotGit))
                {
                    return dir.Name;
                }
                if (File.Exists(dotGit))
                {
                    return MainCheckout(File.ReadAllText(dotGit)) ?? dir.Name;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return null;
    }

    /// <summary>A linked worktree's <c>.git</c> file says <c>gitdir: &lt;main&gt;/.git/worktrees/&lt;name&gt;</c>: the folder holding that <c>.git</c> is the repo.</summary>
    private static string? MainCheckout(string dotGitFile)
    {
        const string prefix = "gitdir:";
        var line = dotGitFile.Split('\n', 2)[0].Trim();
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var gitDir = line[prefix.Length..].Trim().Replace('\\', '/');
        var marker = gitDir.LastIndexOf("/.git/worktrees/", StringComparison.Ordinal);
        if (marker <= 0)
        {
            return null;
        }
        var name = gitDir[..marker].Split('/')[^1];
        return name.Length > 0 ? name : null;
    }
}
