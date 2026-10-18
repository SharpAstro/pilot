using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>Where a session is now: from the agents row's <c>status</c> while it runs, else its <c>state</c>. Port of v1's <c>session_view()</c> status.</summary>
public static class SessionStates
{
    /// <param name="status">busy, idle or waiting: the agents row's, else the session record's.</param>
    /// <param name="state">A background session's: working, blocked, done, failed.</param>
    public static SessionState Map(string? status, string? state)
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

        return state == "working" ? SessionState.Working : SessionState.Ended;
    }
}

/// <summary>A session's row from its parts. Pure: the same parts always make the same row.</summary>
public static class SessionRowBuilder
{
    /// <param name="agent">The agents row.</param>
    /// <param name="transcripts">What its transcripts say.</param>
    /// <param name="record">Its session record, while its process lives.</param>
    /// <param name="repo">The repo its folder belongs to.</param>
    /// <param name="role">Its role, flight, leg and budget.</param>
    public static SessionRow Build(ClaudeAgentRow agent, TranscriptFacts transcripts, SessionRecord? record, string? repo, SessionRoleInfo role)
    {
        var state = SessionStates.Map(agent.Status ?? record?.Status, agent.State);
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
            ContextWindow = ContextWindows.For(transcripts.Model, transcripts.RequestedModel),
            LastResponseAt = transcripts.LastResponseAt,
            State = state,
            WaitingFor = state == SessionState.Waiting ? agent.WaitingFor ?? record?.WaitingFor ?? transcripts.PendingQuestion : null,
            SpendWeighted = transcripts.SpendWeighted,
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
