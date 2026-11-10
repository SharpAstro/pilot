using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps a <see cref="RepoStatus"/> for every repo the <see cref="RepoCatalog"/> lists, read from git and gh (no fetch, no
/// model) and cached with the time it was read, so <c>GET /api/v1/repos/status</c> never waits on them. Every read goes into
/// <see cref="NodeState"/> as a <see cref="ReposRow"/> on the event stream, and a node with no row yet puts one at boot with the
/// repos the catalog knows, each marked <see cref="RepoStatus.Unread"/>. Refreshed every <see cref="Interval"/> from boot, and on demand; never more than one refresh runs at once, and one asked
/// for while another runs gets that one's answer. A git or gh call that fails leaves its part of the repo's status empty
/// and says so in its <see cref="RepoStatus.Errors"/>, never failing the rest. The git half of a round is skipped for whatever
/// the files git keeps say has not moved since the last round (RepoWatch.Git.cs); the GitHub half is read every round.
/// </summary>
public sealed partial class RepoWatch(NodeState state, RepoCatalog repos, Planes planes, IGitHub github, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly object _gate = new();
    private Task<RepoStatus[]>? _running;

    // Whether the read running is an explicit refresh's, whose git status is no older than RefreshFresh (#914).
    private bool _runningStrict;
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
        return !refresh && _latest is { } cached ? Task.FromResult(cached) : RefreshAsync(cancellationToken, strict: refresh);
    }

    /// <summary>
    /// Reads every repo afresh; joins the refresh already running, if one is. <paramref name="strict"/> (an explicit
    /// <c>pilot repos --refresh</c>, a pull) re-reads every repo's git status unless it was read within <see cref="RefreshFresh"/>,
    /// where a background read keeps it for <see cref="StatusInterval"/> (#914: the 15-minute memory showed a dirty repo as clean).
    /// A strict refresh that finds a background read running waits for it, then reads strictly itself.
    /// </summary>
    public async Task<RepoStatus[]> RefreshAsync(CancellationToken cancellationToken, bool strict = false)
    {
        Task<RepoStatus[]> run;
        Task<RepoStatus[]>? lenient = null;
        lock (_gate)
        {
            if (strict && _running is { } running && !_runningStrict)
            {
                lenient = running;
            }
            // A background read even when a command asked for it: its git starts share the budget (ProcessGate), never the command's priority.
            if (_running is null)
            {
                _runningStrict = strict;
                _running = ProcessGate.Run(ReadAllAsync);
            }
            run = _running;
        }
        if (lenient is not null)
        {
            await lenient.WaitAsync(cancellationToken);
            return await RefreshAsync(cancellationToken, strict: true);
        }
        return await run.WaitAsync(cancellationToken);
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
        finally
        {
            // RefreshAsync's WaitAsync returns the moment the node stops, but the read it started (Task.Run, git children
            // in each repo folder) is still ending. Hosting's StopAsync returns when this method does, so wait for that read:
            // a test that deletes a repo folder after disposing the node found it in use (StopGateTests, run 41121644805).
            Task<RepoStatus[]>? running;
            lock (_gate)
            {
                running = _running;
            }
            if (running is not null)
            {
                try
                {
                    await running;
                }
                catch (Exception ex) when (ex is OperationCanceledException or AggregateException or InvalidOperationException)
                {
                }
            }
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
            // Every clone is read from git for the Repos page; only the repos pilot works in are read on GitHub too.
            var tracked = new HashSet<string>(repos.Tracked().Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var now = time.GetUtcNow();
            var statuses = new RepoStatus[catalog.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, catalog.Length),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism), CancellationToken = _stopping },
                async (i, token) => statuses[i] = await ReadAsync(catalog[i], flights, now, tracked.Contains(catalog[i].Path), token));
            // A clone no longer under a root keeps no memory of its git half.
            foreach (var gone in _memory.Keys.Where(k => !catalog.Any(r => r.Path == k)))
            {
                _memory.TryRemove(gone, out _);
            }
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

    private async Task<RepoStatus> ReadAsync(RepoInfo repo, FlightRow[] allFlights, DateTimeOffset now, bool onGitHub, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var flights = allFlights.Where(f => SamePath(f.RepoPath, repo.Path)).ToArray();
        var reads = new Reads(errors, cancellationToken);

        Task<T?> Ref<T>(string what, Func<Task<T?>> read) where T : class => reads.Ref(what, read);

        // The git half: kept from the last round while the files it read are as they were (RepoWatch.Git.cs).
        var side = await ReadGitAsync(repo, now, reads, cancellationToken);
        var entries = side.Entries;
        var defaultRef = side.DefaultRef;
        var dirty = side.Dirty;
        var sync = side.Sync;

        var worktrees = new List<WorktreeStatus>();
        foreach (var read in side.Worktrees)
        {
            worktrees.Add(new WorktreeStatus
            {
                Path = read.Entry.Path,
                Branch = read.Entry.Branch,
                Flight = flights.FirstOrDefault(f => SamePath(f.Worktree, read.Entry.Path))?.Code,
                Dirty = read.Dirty ?? false,
                Files = read.Diff?.Files ?? 0,
                Added = read.Diff?.Added ?? 0,
                Removed = read.Diff?.Removed ?? 0,
            });
        }

        var prs = new List<PullRequestStatus>();
        var ideas = new List<IdeaRow>();
        if (repo.GitHub is not null && !onGitHub)
        {
            // A clone pilot does not work in (RepoCatalog.Tracked) asks GitHub nothing, and what an earlier read kept of it goes.
            state.PutMilestones(repo.Name, []);
            state.PutIssues(repo.Name, []);
        }
        if (repo.GitHub is not null && onGitHub)
        {
            // Its Ideas milestone, as the issues are read: a read that fails leaves the node's rows of the repo as they were.
            if (await Ref("ideas", async () => await github.MilestoneIssuesAsync(repo.Path, IdeaIds.Milestone, cancellationToken)) is { } milestone)
            {
                ideas.AddRange(IdeaRows.Of(repo.Name, milestone, allFlights, now));
                state.PutIdeas(repo.Name, ideas);
            }

            // Beside it, the repo's open milestones and open issues (the ones with no milestone too), each read on its own, so one that
            // fails leaves its own rows as they were.
            if (await Ref("milestones", async () => await github.MilestonesAsync(repo.Path, cancellationToken)) is { } openMilestones)
            {
                state.PutMilestones(repo.Name, IssueRows.OfMilestones(repo.Name, openMilestones));
            }
            if (await Ref("open issues with their milestones", async () => await github.OpenMilestoneIssuesAsync(repo.Path, cancellationToken)) is { } openIssues)
            {
                state.PutIssues(repo.Name, IssueRows.OfIssues(repo.Name, openIssues, allFlights, now));
            }

            var listed = await Ref("open PRs", async () => await github.OpenPullRequestsAsync(repo.Path, cancellationToken));
            prs.AddRange((listed ?? []).Select(p => p with { Flight = flights.FirstOrDefault(f => f.Branch == p.Head)?.Code }));

            // This node's label on an open issue no flight of this node (flying or held) is on is stale, left by a flight that
            // went without landing: taken off. Another node's label is never touched. Shortcut stories are covered by landing.
            var issues = await Ref("open issues", async () => await github.IssuesAsync(repo.Path, cancellationToken));
            if (issues is not null)
            {
                await SweepHoldsAsync(repo, flights, issues, Ref<string[]>, cancellationToken);
            }
            if (Machine.Length > 0)
            {
                var own = PilotMarks.NodeLabel(Machine);
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

    /// <summary>
    /// The owner's hold on GitHub (#771): <c>pilot: hold</c> on a local flight's issue holds the flight (by the label, the why from the newest
    /// hold comment pilot wrote); a hold that had been labelled whose label is gone is released; a hold whose label write failed gets it again.
    /// Each change is confirmed on a fresh read of the issue's labels, so a hold or unhold that is mid-way is not undone by an older list.
    /// A read that fails leaves the rows as they were.
    /// </summary>
    private async Task SweepHoldsAsync(RepoInfo repo, FlightRow[] flights, IssueInfo[] issues,
        Func<string, Func<Task<string[]?>>, Task<string[]?>> read, CancellationToken cancellationToken)
    {
        foreach (var local in flights.Where(f => f.State != FlightState.Landed && f.Tracker == TrackerKind.GitHub))
        {
            if (issues.FirstOrDefault(i => i.Number == local.Issue) is not { } issue)
            {
                continue;
            }
            var listed = issue.Labels.Contains(PilotMarks.Hold, StringComparer.OrdinalIgnoreCase);
            var hold = state.Flights().FirstOrDefault(f => f.Code == local.Code)?.OnHold;
            if (!(listed ? hold is null or { Labelled: not true } : hold is { Labelled: not null }))
            {
                continue;
            }
            // Confirmed on a fresh read that throws when gh fails (the plain read answers no labels then, which would read as a label
            // taken off); one that fails leaves the row as it was.
            var fresh = await read($"labels of #{issue.Number}", async () => await github.IssueLabelsOrThrowAsync(repo.Path, issue.Number, cancellationToken));
            if (fresh is null)
            {
                continue;
            }
            var labelled = fresh.Contains(PilotMarks.Hold, StringComparer.OrdinalIgnoreCase);
            if (state.Flights().FirstOrDefault(f => f.Code == local.Code) is not { State: not FlightState.Landed } current)
            {
                continue;
            }
            var target = Verbs.FlightTarget(current.Code);
            if (labelled && current.OnHold is null)
            {
                // The comments come from a gh call; the hold is then put on the row as it stands, not on the copy read before it.
                var comments = await read($"comments of #{issue.Number}", async () => await LabelHold.CommentsAsync(github, repo.Path, issue.Number, cancellationToken));
                await LabelHold.ApplyAsync(state, github, time, current.Code, comments, cancellationToken);
            }
            else if (!labelled && current.OnHold is { Labelled: true } held)
            {
                if (state.UpdateFlight(current.Code, f => f.OnHold is { Labelled: true } ? f with { OnHold = null } : null) is not null)
                {
                    state.CloseCardsAbout(target, $"{PilotMarks.Hold} was taken off {current.Item.Display}", LegCommander.OnHoldKind);
                    state.LiftHoldFromCards(current.Code);
                    state.RaiseCard(CardBin.Handled, LegCommander.OnHoldKind, target, $"{current.Code} is off hold",
                        $"{PilotMarks.Hold} was taken off {current.Item.Noun} {current.Item.Display} on GitHub, so the hold by {held.By} ({held.Reason}) ends. The node takes its next due step.");
                }
            }
            else if (!labelled && current.OnHold is { Labelled: false } retry)
            {
                // The label write failed when the flight was put on hold: the hold stands, and the label is tried again.
                await read($"{PilotMarks.Hold} on #{issue.Number}", async () =>
                {
                    await github.AddIssueLabelAsync(repo.Path, issue.Number, PilotMarks.Hold, cancellationToken);
                    return Array.Empty<string>();
                });
                if (state.Flights().FirstOrDefault(f => f.Code == local.Code) is { OnHold: { } now } && now == retry
                    && (await read($"labels of #{issue.Number}", async () => await github.IssueLabelsOrThrowAsync(repo.Path, issue.Number, cancellationToken))) is { } again
                    && again.Contains(PilotMarks.Hold, StringComparer.OrdinalIgnoreCase))
                {
                    state.UpdateFlight(local.Code, f => f.OnHold == retry ? f with { OnHold = retry with { Labelled = true } } : null);
                }
            }
            else if (labelled && current.OnHold is { Labelled: not true } known)
            {
                state.UpdateFlight(local.Code, f => f.OnHold == known ? f with { OnHold = known with { Labelled = true } } : null);
            }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
