using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>An open PR of a repo, in the words the Repos page draws it in.</summary>
internal sealed record PullLine
{
    public required PullRequestStatus Pr { get; init; }

    /// <summary><c>#154 A repos tab</c>.</summary>
    public string Text => $"#{Pr.Number} {Pr.Title}";

    /// <summary><c>draft</c> or <c>ready</c>.</summary>
    public string Readiness => Pr.Draft ? "draft" : "ready";

    /// <summary>The checks: <c>passing</c>, <c>pending</c>, <c>failing</c>, or <c>no checks</c>.</summary>
    public string Checks => Pr.Checks ?? "no checks";

    public string ToDumpLine() => $"#{Pr.Number} | {Pr.Title} | {Readiness} | {Checks}";

    public static PullLine From(PullRequestStatus pr) => new() { Pr = pr };
}

/// <summary>
/// One worktree of a repo as the Repos page shows it: its branch and diff stat from the node's <see cref="RepoStatus"/>, joined
/// with what the feed already knows (its flight, the session working in it, the cards and legs of that flight), so none of
/// the last three is a call of its own.
/// </summary>
internal sealed record WorktreeLine
{
    public required WorktreeStatus Status { get; init; }

    /// <summary>Its branch, or <c>detached</c>.</summary>
    public required string Branch { get; init; }

    /// <summary><c>3 files +120 -8</c> against the default branch, or <c>no changes</c>.</summary>
    public required string DiffStat { get; init; }

    /// <summary>The flight whose plane it is, when the feed has it; null for any other worktree.</summary>
    public FlightRow? Flight { get; init; }

    /// <summary>The code of its flight, also when the feed does not hold the flight row itself.</summary>
    public string? FlightCode => Flight?.Code ?? Status.Flight;

    /// <summary>The live session working in it (its folder, or its flight's), the busiest first; null with none.</summary>
    public SessionLine? Session { get; init; }

    /// <summary><c>working</c>, <c>waiting</c> (on you), <c>idle</c>, or <c>none</c>.</summary>
    public required string Activity { get; init; }

    /// <summary>What its session last said, in a line: what it waits on, its API error, or when it last responded; empty with no session.</summary>
    public string LastMessage { get; init; } = "";

    /// <summary><c>card waiting: ...</c>, <c>leg parser flying</c>, or <c>nothing</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Whether a card waits on the controller for this flight.</summary>
    public bool CardWaiting { get; init; }

    /// <summary>Its open PR (the one from its branch), when it has one.</summary>
    public PullLine? Pull { get; init; }

    public NodeMark? Node { get; init; }

    /// <summary>Its folder's name.</summary>
    public string Name => System.IO.Path.GetFileName(Status.Path.TrimEnd('/', '\\'));

    public string ToDumpLine() => string.Join(" | ", "  " + Branch, DiffStat, FlightCode ?? "no flight", Session is null ? Activity : $"{Activity} ({Session.Name}: {LastMessage})", Action,
        Pull is { } pull ? $"PR #{pull.Pr.Number}" : "no PR");
}

/// <summary>One tracked (or, with All repos, any) repo as the Repos page shows it, its worktrees under it.</summary>
internal sealed record RepoLine
{
    public required RepoStatus Status { get; init; }

    /// <summary>The main checkout in words: <c>main · clean</c>, <c>feature · dirty · 2 ahead · 1 behind</c>.</summary>
    public required string Main { get; init; }

    public IReadOnlyList<PullLine> Pulls { get; init; } = [];

    public IReadOnlyList<WorktreeLine> Worktrees { get; init; } = [];

    /// <summary>The node it is on, when the page merges several; null on a page of one node, this window's.</summary>
    public NodeMark? Node { get; init; }

    public string Name => Status.Name;

    /// <summary>What the selection and the expansion follow: its path, prefixed with its node's key for any node but this window's.</summary>
    public string Key => Node is null or { Here: true } ? Status.Path : $"{Node.Key}/{Status.Path}";

    /// <summary><c>3 worktrees · 1 working</c>, or <c>no worktrees</c>.</summary>
    public string WorktreesText
    {
        get
        {
            if (Worktrees.Count == 0)
            {
                return "no worktrees";
            }
            var working = Worktrees.Count(w => w.Activity == "working");
            var text = $"{Worktrees.Count} {(Worktrees.Count == 1 ? "worktree" : "worktrees")}";
            return working > 0 ? $"{text} · {working} working" : text;
        }
    }

    public string ToDumpLine()
    {
        var line = string.Join(" | ", Name + (Status.Tracked ? "" : " (untracked)"), Main, Pulls.Count == 0 ? "no open PRs" : string.Join("; ", Pulls.Select(p => p.ToDumpLine())), WorktreesText);
        return Node is { } node ? $"{node.Name} | {line}" : line;
    }

    /// <summary>This line and its worktrees (and their sessions) marked with <paramref name="node"/>, for a page of several nodes.</summary>
    public RepoLine Marked(NodeMark? node) => node is null ? this : this with
    {
        Node = node,
        Worktrees = [.. Worktrees.Select(w => w with { Node = node, Session = w.Session is { } s ? s with { Node = node } : null })],
    };

    /// <summary>
    /// The lines for what the node read of its repos, joined with the feed's flights, sessions, legs and cards. Tracked repos
    /// first, then by name.
    /// </summary>
    public static IReadOnlyList<RepoLine> Build(IEnumerable<RepoStatus> repos, IEnumerable<FlightRow> flights, IEnumerable<SessionRow> sessions,
        IEnumerable<CardRow> cards, IEnumerable<LegRow> legs, DateTimeOffset now)
    {
        var flightList = flights.ToList();
        var live = sessions.Where(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
        var open = cards.Where(c => c.State == CardState.Open && c.Bin != CardBin.Handled).ToList();
        var flying = legs.Where(l => l.State == LegState.Flying).ToList();
        return Order(repos.Select(repo =>
        {
            var pulls = repo.PullRequests.Select(PullLine.From).ToList();
            return new RepoLine
            {
                Status = repo,
                Main = MainText(repo),
                Pulls = pulls,
                Worktrees = [.. repo.Worktrees.Select(w => Worktree(w, flightList, live, open, flying, pulls, now))],
            };
        }));
    }

    /// <summary>Lines in drawing order: tracked repos first, then by name.</summary>
    public static IReadOnlyList<RepoLine> Order(IEnumerable<RepoLine> lines) =>
        lines.OrderByDescending(l => l.Status.Tracked).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Node?.Name, StringComparer.Ordinal).ToList();

    private static string MainText(RepoStatus repo)
    {
        var parts = new List<string> { repo.Branch ?? "detached", repo.Dirty ? "dirty" : "clean" };
        if (repo.Ahead is > 0)
        {
            parts.Add($"{repo.Ahead} ahead");
        }
        if (repo.Behind is > 0)
        {
            parts.Add($"{repo.Behind} behind");
        }
        return string.Join(" · ", parts);
    }

    private static WorktreeLine Worktree(WorktreeStatus status, List<FlightRow> flights, List<SessionRow> live, List<CardRow> open, List<LegRow> flying,
        List<PullLine> pulls, DateTimeOffset now)
    {
        var flight = status.Flight is { } code ? flights.FirstOrDefault(f => f.Code == code) : null;
        var folder = Normal(status.Path);
        var session = live
            .Where(s => (status.Flight is not null && s.Flight == status.Flight && s.Role != SessionRole.Watchtower) || Within(Normal(s.Cwd), folder))
            .OrderBy(s => s.State switch { SessionState.Working => 0, SessionState.Waiting => 1, _ => 2 })
            .ThenByDescending(s => s.LastResponseAt)
            .FirstOrDefault();
        var line = session is null ? null : SessionLine.From(session, now);
        var target = status.Flight is null ? null : Verbs.FlightTarget(status.Flight);
        var card = target is null ? null : open.FirstOrDefault(c => c.Subject == target || c.Subject.StartsWith(target + "/", StringComparison.Ordinal));
        var leg = status.Flight is null ? null : flying.FirstOrDefault(l => l.Flight == status.Flight);
        return new WorktreeLine
        {
            Status = status,
            Branch = status.Branch ?? "detached",
            DiffStat = status is { Files: 0, Added: 0, Removed: 0 } ? "no changes" : $"{status.Files} {(status.Files == 1 ? "file" : "files")} +{status.Added} -{status.Removed}",
            Flight = flight,
            Session = line,
            Activity = session?.State switch { SessionState.Working => "working", SessionState.Waiting => "waiting", SessionState.Idle => "idle", _ => "none" },
            LastMessage = line is null ? "" : session!.WaitingFor ?? session.ApiError ?? (line.LastResponse == "none yet" ? "no response yet" : $"last response {line.LastResponse}"),
            Action = card is not null ? $"card waiting: {card.Title}" : leg is not null ? $"leg {leg.Name} flying" : "nothing",
            CardWaiting = card is not null,
            Pull = status.Branch is { } branch ? pulls.FirstOrDefault(p => p.Pr.Head == branch) : null,
        };
    }

    private static string Normal(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static bool Within(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the Repos page shows: the repos (every one under the roots; the view shows the tracked unless asked), and where they come from.</summary>
internal sealed record ReposScreen(IReadOnlyList<RepoLine> Lines, string Source, DateTimeOffset AsOf)
{
    public string Summary
    {
        get
        {
            var tracked = Lines.Where(l => l.Status.Tracked).ToList();
            var worktrees = tracked.Sum(l => l.Worktrees.Count);
            var pulls = tracked.Sum(l => l.Pulls.Count);
            return $"{tracked.Count} {(tracked.Count == 1 ? "repo" : "repos")} · {worktrees} {(worktrees == 1 ? "worktree" : "worktrees")} · {pulls} open {(pulls == 1 ? "PR" : "PRs")}";
        }
    }
}
