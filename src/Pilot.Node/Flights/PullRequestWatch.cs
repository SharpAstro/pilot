using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps the node's <see cref="PullRequestsRow"/>: the open PRs of the repos pilot works in, no more (<see cref="TrackedRepos"/>:
/// the <see cref="RepoCatalog"/>'s clones with a GitHub origin, and any repo a flight or PR review of this node has been in),
/// read in as few batched GitHub searches as the query length limit allows (<see cref="IGitHub.OpenPullRequestsInAsync"/>) every
/// <see cref="Interval"/> and on a <see cref="Verbs.RefreshPullRequests"/>, and put on the event stream for every client. The row says
/// what the read cost in GraphQL points, and the log line of each read too. A batch that fails keeps the PRs last read of its repos
/// and says so for each of its orgs in <see cref="PullRequestsRow.Errors"/>, never failing the rest; a read GitHub's rate limit stops
/// keeps what was read before, says so in <see cref="PullRequestsRow.Note"/>, and nothing is read again until the limit resets. One
/// refresh runs at a time, and one asked for while another runs gets that one's answer.
/// </summary>
public sealed class PullRequestWatch(NodeState state, RepoCatalog repos, IGitHub github, TimeProvider time) : BackgroundService
{
    /// <summary>How long a rate-limited watch waits when GitHub does not say when its points come back.</summary>
    public static readonly TimeSpan UnknownReset = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private Task<PullRequestsRow?>? _running;
    private bool _runningAsked;
    private string? _viewer;
    private DateTimeOffset? _limitedUntil;
    private DateTimeOffset? _lastRead;
    private bool _opened;
    private CancellationTokenSource? _sleeping;
    private PageViews _views = new();

    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>This node's machine name: the <see cref="PullRequestRepoRead.By"/> of each repo it reads.</summary>
    public string Machine { get; init; } = "";

    /// <summary>
    /// The other nodes of the fleet (#834): before each read the watch asks each reachable peer for its PR read and takes the repos
    /// <see cref="PrReadShare"/> says it may, searching GitHub only for the rest. Null: this node reads every repo itself.
    /// </summary>
    public IFleet? Fleet { get; init; }

    /// <summary>The peers' latest PR reads kept from their event streams (#834): taken first, so only a peer it holds nothing of is asked at read time. Null: every peer is asked.</summary>
    public PeerPullRequests? Held { get; init; }

    /// <summary>The repos (<c>owner/name</c>) a flight in the flying state is in: the ones this node flies in.</summary>
    public static HashSet<string> FlyingRepos(FlightRow[] flights) =>
        new(flights.Where(f => f.State == FlightState.Flying).Select(f => RepoCatalog.GitHubOf(Path.Combine(f.RepoPath, ".git"))).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Said after each read that asked GitHub: the repos that read answered (a repo whose batch failed or was stopped is not among them) and their open PRs, labels included. Listeners must not block.</summary>
    public event Action<string[], PullRequestRow[]>? Looked;

    /// <summary>How long it waits between reads while a client shows the Pull requests page.</summary>
    public TimeSpan Interval { get; init; } = DefaultInterval;

    /// <summary>The default <see cref="Interval"/>.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long it waits between reads while no client does (<see cref="NodeSettings.PrWatchIdleKey"/>); asked at each wait, so a saved setting counts. Unset, it is <see cref="Interval"/>: the node sets it.</summary>
    public Func<TimeSpan>? Idle { get; init; }

    /// <summary>The pages the node's clients show: one showing <see cref="ClientPages.PullRequests"/> makes the watch read every <see cref="Interval"/>, and one opening it reads at once.</summary>
    public PageViews Views
    {
        get => _views;
        init
        {
            _views = value;
            value.Changed += (page, shown) =>
            {
                if (page == ClientPages.PullRequests)
                {
                    PageChanged(shown);
                }
            };
        }
    }

    /// <summary>Reads the repos afresh and puts the row on the stream; joins the refresh already running, if one is; asks GitHub nothing while its rate limit holds. Null when the node has no GitHub repo to read.</summary>
    public async Task<PullRequestsRow?> RefreshAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<PullRequestsRow?> joined;
            Task<PullRequestsRow?>? mine = null;
            lock (_gate)
            {
                // A read already running for a person is joined. One on the watch's pace may have taken a peer's read or left a repo to its
                // reader, which a person who asked for fresh PRs must not get, so it is waited out and a read of the person's own follows.
                if (_running is null || _runningAsked)
                {
                    mine = RunAsync("refresh asked", asked: true);
                }
                joined = mine ?? _running!;
            }
            if (mine is not null)
            {
                return await mine.WaitAsync(cancellationToken);
            }
            try
            {
                await joined.WaitAsync(cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The pace's read failed; the person's read is its own.
            }
        }
    }

    /// <summary>The read the watch makes on its pace (a minute while the page is viewed, else the idle interval), here for a test to ask at a time it sets: it may take a peer's read and leave a repo to its reader, as a refresh may not.</summary>
    public Task<PullRequestsRow?> ReadOnPaceAsync(CancellationToken cancellationToken) => RunAsync("on pace").WaitAsync(cancellationToken);

    /// <summary>Wakes the wait in progress to count again, as the idle interval was saved changed.</summary>
    public void PaceChanged() => Wake();

    // A read, or the one already running, which a later ask joins and keeps its reason.
    private Task<PullRequestsRow?> RunAsync(string why, bool asked = false)
    {
        lock (_gate)
        {
            // A background read even when a command asked for it: its gh starts share the budget (ProcessGate), never the command's priority.
            if (_running is null)
            {
                _runningAsked = asked;
                _running = ProcessGate.Run(() => ReadAsync(why, asked));
            }
            return _running;
        }
    }

    private void PageChanged(bool shown)
    {
        if (shown)
        {
            lock (_gate)
            {
                // A read in progress is the one it joins; only when none runs does opening the page ask for one.
                _opened = _running is null;
            }
        }
        Wake();
    }

    private void Wake()
    {
        CancellationTokenSource? sleeping;
        lock (_gate)
        {
            sleeping = _sleeping;
        }
        try
        {
            sleeping?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var viewed = Views.Count(ClientPages.PullRequests) > 0;
                bool opened;
                lock (_gate)
                {
                    opened = _opened;
                    _opened = false;
                }
                var wait = opened ? TimeSpan.Zero : WaitFor(viewed);
                if (wait <= TimeSpan.Zero)
                {
                    try
                    {
                        await RunAsync(opened ? "page opened" : _lastRead is null ? "node started" : viewed ? "page open" : "idle interval").WaitAsync(stopping);
                    }
                    catch (Exception ex) when (!stopping.IsCancellationRequested)
                    {
                        // A read that failed is not read again at once: the pace goes on from now.
                        _lastRead = time.GetUtcNow();
                        await Error.WriteLineAsync($"pull request watch: one read failed, reading again: {ex.Message}");
                    }
                    continue;
                }
                using var sleeping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                lock (_gate)
                {
                    _sleeping = sleeping;
                    // Opened between the check above and here: it is not slept through.
                    if (_opened)
                    {
                        // Not left for Wake to cancel once the using above has disposed it.
                        _sleeping = null;
                        continue;
                    }
                }
                try
                {
                    await Task.Delay(wait, time, sleeping.Token);
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                    // Woken: the page opened or closed, or the idle interval was saved; what is due is counted again.
                }
                finally
                {
                    lock (_gate)
                    {
                        _sleeping = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    // How long until the next read: a minute (or the idle interval) after the last, and no sooner than a rate limit resets.
    private TimeSpan WaitFor(bool viewed)
    {
        var now = time.GetUtcNow();
        var due = _lastRead is { } last ? last + (viewed ? Interval : Idle?.Invoke() ?? Interval) : now;
        if (_limitedUntil is { } until && until > due)
        {
            due = until;
        }
        return due - now;
    }

    /// <summary>
    /// The GitHub <c>owner/name</c> of every repo pilot works in, sorted, each once: the repo of every flight, landed ones too
    /// (its legs flew in it), and every repo a PR review was run on. Nothing org-wide, and not every clone under the repo roots:
    /// a root that holds other people's clones (WebAssembly's wabt and wasi-libc, a work org's repos) listed all their PRs,
    /// which pilot never touched (the owner, 2026-10-29: "why do we have web assembly in here?").
    /// </summary>
    public static string[] TrackedRepos(FlightRow[] flights, PrReviewRow[] reviews) =>
    [
        .. flights.Select(f => f.RepoPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => RepoCatalog.GitHubOf(Path.Combine(p, ".git")))
            .Concat(reviews.Select(r => r.Repo))
            .OfType<string>()
            .Where(r => OwnerOf(r) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    private async Task<PullRequestsRow?> ReadAsync(string why, bool asked)
    {
        try
        {
            var flights = state.Flights();
            var catalog = repos.Repos();
            // The repos flown and reviewed in, and the ones the catalog tracks besides (an idea, a PILOT.md): one rule with the repo read.
            var tracked = TrackedRepos(flights, state.PrReviews())
                .Concat(repos.Tracked().Select(r => r.GitHub).OfType<string>().Where(r => OwnerOf(r) is not null))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var orgs = tracked.Select(r => OwnerOf(r)!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var held = state.PullRequests();
            // A node with no GitHub repo has nothing to say, and says nothing until it has.
            if (tracked.Length == 0 && held is null)
            {
                return null;
            }
            // What was read of a repo before stands where it is not read now; a repo no longer tracked is gone with its PRs.
            var scope = new HashSet<string>(tracked, StringComparer.OrdinalIgnoreCase);
            var heldPrs = (held?.PullRequests ?? []).Where(p => scope.Contains(p.Repo)).ToArray();
            var now = time.GetUtcNow();

            if (_limitedUntil is { } until && now < until && held is not null)
            {
                // Rate-limited and not reset yet: GitHub is asked nothing, and the row shows what it held (less any repo no longer
                // tracked), as old as it is.
                var waiting = held with { Orgs = orgs, PullRequests = [.. heldPrs.Select(p => p with { Flight = FlightOf(p, catalog, flights) })], Cost = 0 };
                state.PutPullRequests(waiting);
                return waiting;
            }
            _limitedUntil = null;

            var prs = new List<PullRequestRow>();
            var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<PullRequestsError>();
            var gone = new HashSet<(string Repo, int Number)>(GoneComparer.Instance);
            var cost = 0;
            int? remaining = null;
            DateTimeOffset? limitedUntil = null;
            // The fleet shares the read (#834): a repo a peer read freshly is taken from its row, one left to its chosen reader keeps what
            // was held, and only the rest is searched. Merges, labels and a flight's own reads never come through here.
            // Known means read before, its PRs or its stamp showing it: a repo with no open PRs has a stamp only.
            var known = new HashSet<string>(heldPrs.Select(p => p.Repo).Concat((held?.RepoReads ?? []).Select(s => s.Repo).Where(scope.Contains)), StringComparer.OrdinalIgnoreCase);
            var plans = await PlanAsync(tracked, flights, now, known, asked);
            var taken = plans.OfType<PrReadPlan.Taken>().ToArray();
            var readHere = plans.OfType<PrReadPlan.ReadHere>().Select(p => p.Repo).ToArray();
            var searches = PullRequestSearch.Batches(readHere);
            foreach (var batch in searches)
            {
                // The points are spent: the batches left wait for them, rather than being refused one by one.
                if (limitedUntil is not null)
                {
                    break;
                }
                try
                {
                    var answer = await github.OpenPullRequestsInAsync(batch, CancellationToken.None);
                    prs.AddRange(answer.PullRequests);
                    gone.UnionWith(answer.Gone.Select(g => (g.Repo, g.Number)));
                    read.UnionWith(batch);
                    cost += answer.Cost;
                    remaining = answer.Remaining ?? remaining;
                    if (answer.Remaining == 0)
                    {
                        limitedUntil = answer.ResetAt ?? now + UnknownReset;
                    }
                }
                catch (RateLimitedException ex)
                {
                    limitedUntil = ex.ResetAt ?? now + UnknownReset;
                    remaining = 0;
                    await Error.WriteLineAsync($"pull request watch: rate-limited, not reading again until {limitedUntil:u}: {ex.Message}");
                }
                catch (Exception ex) when (ex is FlightException or HttpRequestException or IOException or System.ComponentModel.Win32Exception)
                {
                    errors.AddRange(batch.Select(r => OwnerOf(r)!).Distinct(StringComparer.OrdinalIgnoreCase).Select(org => new PullRequestsError { Org = org, Message = ex.Message }));
                    await Error.WriteLineAsync($"pull request watch: a search of {batch.Length} repos failed: {ex.Message}");
                }
            }
            _limitedUntil = limitedUntil;
            foreach (var share in taken)
            {
                prs.AddRange(share.PullRequests);
                read.Add(share.Repo);
            }
            // A PR the search listed but whose own state is merged or closed is not open: out of this read, out of a peer's taken row
            // (which may predate the merge), and out of what is held for a repo not read.
            var dropped = prs.RemoveAll(p => gone.Contains((p.Repo, p.Number)));
            var fresh = prs.ToArray();
            // Not read this time (its batch failed, or the limit stopped it): what was read of it before stands, under the error or note.
            var kept = heldPrs.Where(p => !read.Contains(p.Repo)).ToArray();
            dropped += kept.Count(p => gone.Contains((p.Repo, p.Number)));
            prs.AddRange(kept.Where(p => !gone.Contains((p.Repo, p.Number))));

            // Each repo read now is stamped by this node; one kept from before keeps its old stamp.
            var flying = FlyingRepos(flights);
            var stamps = (held?.RepoReads ?? []).Where(s => scope.Contains(s.Repo) && !read.Contains(s.Repo))
                .Concat(read.Select(r => taken.FirstOrDefault(t => string.Equals(t.Repo, r, StringComparison.OrdinalIgnoreCase)) is { } share
                    ? new PullRequestRepoRead(r, share.ReadAt, share.By, flying.Contains(r))
                    : new PullRequestRepoRead(r, now, Machine, flying.Contains(r))))
                .OrderBy(s => s.Repo, StringComparer.OrdinalIgnoreCase).ToArray();
            var row = new PullRequestsRow
            {
                RepoReads = stamps,
                Orgs = orgs,
                PullRequests = [.. prs.Select(p => p with { Flight = FlightOf(p, catalog, flights) })],
                Errors = [.. errors.Distinct()],
                Cost = cost,
                RateRemaining = remaining,
                Note = limitedUntil is { } reset ? $"Rate-limited by GitHub until {reset.UtcDateTime:HH:mm} UTC: showing what was last read, reading again then" : null,
                ReadAt = now,
                Viewer = await ViewerAsync(),
            };
            await Error.WriteLineAsync($"pull request watch: {(taken.Length > 0 ? $"{taken.Length} {(taken.Length == 1 ? "repo" : "repos")} from peers, " : "")}{readHere.Length} read here in "
                + $"{searches.Length} {(searches.Length == 1 ? "search" : "searches")}, {cost} GraphQL {(cost == 1 ? "point" : "points")}"
                + $"{(dropped > 0 ? $", {dropped} merged or closed dropped" : "")}"
                + $"{(remaining is { } left ? $", {left} left" : "")} ({why})");
            state.PutPullRequests(row);
            try
            {
                Looked?.Invoke([.. read], fresh);
            }
            catch (Exception ex)
            {
                await Error.WriteLineAsync($"pull request watch: a listener of the read failed: {ex.Message}");
            }
            return row;
        }
        finally
        {
            lock (_gate)
            {
                _lastRead = time.GetUtcNow();
                _running = null;
                _runningAsked = false;
            }
        }
    }

    // What each repo's read is to be (#834): the peers asked in parallel, each within the fleet's read budget; one that fails is not counted.
    // A read a person asked for (a refresh) takes only a peer's read a few seconds old, and leaves nothing to a reader.
    private async Task<PrReadPlan[]> PlanAsync(string[] tracked, FlightRow[] flights, DateTimeOffset now, HashSet<string> known, bool asked)
    {
        var peers = Fleet?.Peers().Where(p => p.HasGrant).ToList() ?? [];
        if (peers.Count == 0)
        {
            return [.. tracked.Select(r => (PrReadPlan)new PrReadPlan.ReadHere(r))];
        }
        var answers = await Task.WhenAll(peers.Select(async peer =>
        {
            try
            {
                if (Held?.RowOf(peer.Name) is { } kept)
                {
                    return new PrReadPeer(peer.Name, kept, [.. kept.RepoReads.Where(s => s.Flying).Select(s => s.Repo)]);
                }
                using var budget = new CancellationTokenSource(HeldTokenFleet.ReadBudget, time);
                var row = (await peer.LoadAsync(budget.Token).WaitAsync(budget.Token)).PullRequests;
                return new PrReadPeer(peer.Name, row, [.. (row?.RepoReads ?? []).Where(s => s.Flying).Select(s => s.Repo)]);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Error.WriteLineAsync($"pull request watch: peer {peer.Name} did not answer, not counted: {ex.Message}");
                return null;
            }
        }));
        if (asked)
        {
            // Only a peer's read younger than FreshForRefresh is taken; nothing is left to a reader, the rest is searched here.
            return [.. PrReadShare.Plan(Machine, tracked, FlyingRepos(flights), FreshForRefresh, now, [.. answers.OfType<PrReadPeer>()], [])
                .Select(p => p is PrReadPlan.LeftTo l ? new PrReadPlan.ReadHere(l.Repo) : p)];
        }
        return PrReadShare.Plan(Machine, tracked, FlyingRepos(flights), Pace(), now, [.. answers.OfType<PrReadPeer>()], known);
    }

    /// <summary>How old a peer's read may be for a refresh a person asked for to take it instead of reading GitHub.</summary>
    public static readonly TimeSpan FreshForRefresh = TimeSpan.FromSeconds(5);

    /// <summary>One pace for the watch: a minute while a client views the page, else the idle interval. The peers' held rows age by it too.</summary>
    public TimeSpan Pace() => Views.Count(ClientPages.PullRequests) > 0 ? Interval : Idle?.Invoke() ?? Interval;

    // The login gh reads as, asked until it answers and kept: it does not change while the node runs.
    private async Task<string?> ViewerAsync()
    {
        try
        {
            return _viewer ??= await github.LoginAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is FlightException or HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // The flight whose plane's branch is the PR's head, in the clone of the repo the PR is in.
    private static string? FlightOf(PullRequestRow pr, RepoInfo[] catalog, FlightRow[] flights)
    {
        if (pr.Head is not { Length: > 0 } head
            || catalog.FirstOrDefault(r => string.Equals(r.GitHub, pr.Repo, StringComparison.OrdinalIgnoreCase)) is not { } repo)
        {
            return null;
        }
        return flights.FirstOrDefault(f => f.Branch == head && SamePath(f.RepoPath, repo.Path))?.Code;
    }

    private sealed class GoneComparer : IEqualityComparer<(string Repo, int Number)>
    {
        public static readonly GoneComparer Instance = new();

        public bool Equals((string Repo, int Number) a, (string Repo, int Number) b) => a.Number == b.Number && string.Equals(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Repo, int Number) key) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Repo), key.Number);
    }

    private static string? OwnerOf(string? repo) => repo?.IndexOf('/') is > 0 and var slash ? repo[..slash] : null;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
