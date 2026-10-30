using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps a <see cref="RepoStatus"/> for every repo the <see cref="RepoCatalog"/> lists, read from git and gh (no fetch, no
/// model) and cached with the time it was read, so <c>GET /api/v1/repos/status</c> never waits on them. Every read goes into
/// <see cref="NodeState"/> as a <see cref="ReposRow"/> on the event stream, and a node with no row yet puts one at boot with the
/// repos the catalog knows, each marked <see cref="RepoStatus.Unread"/>. Refreshed every <see cref="Interval"/> from boot, and on demand; never more than one refresh runs at once, and one asked
/// for while another runs gets that one's answer. A git or gh call that fails leaves its part of the repo's status empty
/// and says so in its <see cref="RepoStatus.Errors"/>, never failing the rest.
/// </summary>
public sealed class RepoWatch(NodeState state, RepoCatalog repos, Planes planes, IGitHub github, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly object _gate = new();
    private Task<RepoStatus[]>? _running;
    private RepoStatus[]? _latest;
    private CancellationToken _stopping;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>This node's code, whose label a look sweeps off issues it does not fly; empty sweeps nothing.</summary>
    public string Machine { get; init; } = "";

    /// <summary>How many repos are read at once.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>
    /// The cached statuses, or a first read when there is none yet; a fresh read when <paramref name="refresh"/>.
    /// </summary>
    public Task<RepoStatus[]> StatusAsync(bool refresh, CancellationToken cancellationToken)
    {
        return !refresh && _latest is { } cached ? Task.FromResult(cached) : RefreshAsync(cancellationToken);
    }

    /// <summary>Reads every repo afresh; joins the refresh already running, if one is.</summary>
    public Task<RepoStatus[]> RefreshAsync(CancellationToken cancellationToken)
    {
        Task<RepoStatus[]> run;
        lock (_gate)
        {
            run = _running ??= Task.Run(ReadAllAsync);
        }
        return run.WaitAsync(cancellationToken);
    }

    /// <remarks>The seed is put here, not in the background loop, so it is on the stream before anything starts after it.</remarks>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Seed();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        _stopping = stopping;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(stopping);
                }
                catch (Exception ex) when (!stopping.IsCancellationRequested)
                {
                    await Error.WriteLineAsync($"repo watch: one read failed, reading again: {ex.Message}");
                }
                await Task.Delay(Interval, time, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>Puts the repos the catalog knows, marked not read yet, when the node holds no row (a fresh node; a restored one keeps its last).</summary>
    private void Seed()
    {
        if (state.Repos() is not null)
        {
            return;
        }
        var known = repos.Repos();
        if (known.Length == 0)
        {
            return;
        }
        state.PutRepos(new ReposRow
        {
            Repos = [.. known.Select(r => new RepoStatus { Name = r.Name, Path = r.Path, GitHub = r.GitHub, Flights = r.Flights, Tracked = r.Flights > 0, Unread = true })],
            ReadAt = null,
            Since = time.GetUtcNow(),
        });
    }

    private async Task<RepoStatus[]> ReadAllAsync()
    {
        try
        {
            var flights = state.Flights();
            var catalog = repos.Repos();
            var now = time.GetUtcNow();
            var statuses = new RepoStatus[catalog.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, catalog.Length),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism), CancellationToken = _stopping },
                async (i, token) => statuses[i] = await ReadAsync(catalog[i], flights, now, token));
            _latest = statuses;
            state.PutRepos(new ReposRow { Repos = statuses, ReadAt = time.GetUtcNow() });
            return statuses;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }
        }
    }

    private async Task<RepoStatus> ReadAsync(RepoInfo repo, FlightRow[] allFlights, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var flights = allFlights.Where(f => SamePath(f.RepoPath, repo.Path)).ToArray();

        async Task<T?> Ref<T>(string what, Func<Task<T?>> read) where T : class
        {
            try
            {
                return await read();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        async Task<T?> Value<T>(string what, Func<Task<T?>> read) where T : struct
        {
            try
            {
                return await read() ?? Failed<T>(what);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        T? Failed<T>(string what) where T : struct
        {
            errors.Add($"git could not read {what}");
            return null;
        }

        var entries = await Ref("worktrees", () => planes.WorktreesAsync(repo.Path, cancellationToken));
        if (entries is null && errors.Count == 0)
        {
            errors.Add("git could not list the worktrees");
        }
        var defaultRef = await Ref("default branch", () => planes.DefaultRefAsync(repo.Path, cancellationToken));
        var dirty = await Value("main checkout's status", () => planes.IsDirtyAsync(repo.Path, cancellationToken, withoutPlanes: true));
        (int Ahead, int Behind)? sync = defaultRef is null ? null : await Value($"main checkout against {defaultRef}", () => planes.AheadBehindAsync(repo.Path, defaultRef, cancellationToken));

        var worktrees = new List<WorktreeStatus>();
        foreach (var entry in (entries ?? []).Skip(1).Where(e => !e.Prunable && Directory.Exists(e.Path)))
        {
            var wDirty = await Value($"{entry.Path}'s status", () => planes.IsDirtyAsync(entry.Path, cancellationToken));
            (int Files, int Added, int Removed)? diff = defaultRef is null ? null : await Value($"{entry.Path}'s diff", () => planes.DiffStatAsync(entry.Path, defaultRef, cancellationToken));
            worktrees.Add(new WorktreeStatus
            {
                Path = entry.Path,
                Branch = entry.Branch,
                Flight = flights.FirstOrDefault(f => SamePath(f.Worktree, entry.Path))?.Code,
                Dirty = wDirty ?? false,
                Files = diff?.Files ?? 0,
                Added = diff?.Added ?? 0,
                Removed = diff?.Removed ?? 0,
            });
        }

        var prs = new List<PullRequestStatus>();
        var ideas = new List<IdeaRow>();
        if (repo.GitHub is not null)
        {
            // Its Ideas milestone, as the issues are read: a read that fails leaves the node's rows of the repo as they were.
            if (await Ref("ideas", async () => await github.MilestoneIssuesAsync(repo.Path, IdeaIds.Milestone, cancellationToken)) is { } milestone)
            {
                ideas.AddRange(IdeaRows.Of(repo.Name, milestone, allFlights, now));
                state.PutIdeas(repo.Name, ideas);
            }

            var listed = await Ref("open PRs", async () => await github.OpenPullRequestsAsync(repo.Path, cancellationToken));
            prs.AddRange((listed ?? []).Select(p => p with { Flight = flights.FirstOrDefault(f => f.Branch == p.Head)?.Code }));

            // This node's label on an open issue no flight of this node (flying or held) is on is stale, left by a flight that
            // went without landing: taken off. Another node's label is never touched. Shortcut stories are covered by landing.
            if (Machine.Length > 0)
            {
                var own = PilotMarks.NodeLabel(Machine);
                var issues = await Ref("open issues", async () => await github.IssuesAsync(repo.Path, cancellationToken));
                foreach (var issue in (issues ?? []).Where(i => i.Labels.Contains(own, StringComparer.OrdinalIgnoreCase)
                             && !flights.Any(f => f.State != FlightState.Landed && f.Tracker == TrackerKind.GitHub && f.Issue == i.Number)))
                {
                    await Ref("node label sweep", async () =>
                    {
                        await github.RemoveIssueLabelAsync(repo.Path, issue.Number, own, cancellationToken);
                        return Array.Empty<int>();
                    });
                }
                // And put on where a flight of this node lacks it, its issue or its PR: flights made, or PRs opened, before the label
                // existed (#322) never got it, and only the other node's PRs showed whose they were (DE1's #373 and #374, 2026-10-28).
                var missing = flights.Where(f => f.State != FlightState.Landed && f.Tracker == TrackerKind.GitHub)
                    .SelectMany(f => (issues ?? []).Where(i => i.Number == f.Issue).Select(i => (i.Number, i.Labels))
                        .Concat(prs.Where(p => p.Flight == f.Code).Select(p => (p.Number, p.Labels))))
                    .Where(item => !item.Labels.Contains(own, StringComparer.OrdinalIgnoreCase)
                        && !item.Labels.Any(l => l.StartsWith(PilotMarks.NodeLabelPrefix, StringComparison.Ordinal)));
                foreach (var (number, _) in missing)
                {
                    await Ref("node label", async () =>
                    {
                        await github.AddIssueLabelAsync(repo.Path, number, own, cancellationToken);
                        return Array.Empty<int>();
                    });
                }
            }
        }

        return new RepoStatus
        {
            Name = repo.Name,
            Path = repo.Path,
            GitHub = repo.GitHub,
            Tracked = repo.Flights > 0 || worktrees.Any(w => w.Flight is not null) || prs.Any(p => p.Flight is not null),
            Flights = repo.Flights,
            DefaultBranch = defaultRef is null ? null : defaultRef.StartsWith("origin/", StringComparison.Ordinal) ? defaultRef["origin/".Length..] : defaultRef,
            Branch = entries?.FirstOrDefault()?.Branch,
            Dirty = dirty ?? false,
            Ahead = sync?.Ahead,
            Behind = sync?.Behind,
            Worktrees = [.. worktrees],
            PullRequests = [.. prs],
            Ideas = [.. IdeaIds.Order(ideas)],
            NotedIdeas = ideas.Count(i => i.State == IdeaState.Noted),
            Errors = [.. errors],
            UpdatedAt = now,
        };
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
