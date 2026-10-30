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
    private string? _viewer;
    private DateTimeOffset? _limitedUntil;
    private DateTimeOffset? _lastRead;
    private bool _opened;
    private CancellationTokenSource? _sleeping;
    private PageViews _views = new();

    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>How long it waits between reads while a client shows the Pull requests page.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long it waits between reads while no client does (<see cref="NodeSettings.PrWatchIdleKey"/>); asked at each wait, so a saved setting counts.</summary>
    public Func<TimeSpan> Idle { get; init; } = () => NodeSettings.DefaultPrWatchIdle;

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
    public Task<PullRequestsRow?> RefreshAsync(CancellationToken cancellationToken) => RunAsync("refresh asked").WaitAsync(cancellationToken);

    /// <summary>Wakes the wait in progress to count again, as the idle interval was saved changed.</summary>
    public void PaceChanged() => Wake();

    // A read, or the one already running, which a later ask joins and keeps its reason.
    private Task<PullRequestsRow?> RunAsync(string why)
    {
        lock (_gate)
        {
            return _running ??= Task.Run(() => ReadAsync(why));
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
        var due = _lastRead is { } last ? last + (viewed ? Interval : Idle()) : now;
        if (_limitedUntil is { } until && until > due)
        {
            due = until;
        }
        return due - now;
    }

    /// <summary>
    /// The GitHub <c>owner/name</c> of every repo pilot works in, sorted, each once: the clones with a GitHub origin, the repo of
    /// every flight (its legs fly in it) and every repo a PR review was run on, a clone here or not. Nothing org-wide.
    /// </summary>
    public static string[] TrackedRepos(RepoInfo[] catalog, FlightRow[] flights, PrReviewRow[] reviews) =>
    [
        .. catalog.Select(r => r.GitHub)
            .Concat(flights.Select(f => f.RepoPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => RepoCatalog.GitHubOf(Path.Combine(p, ".git"))))
            .Concat(reviews.Select(r => r.Repo))
            .OfType<string>()
            .Where(r => OwnerOf(r) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    private async Task<PullRequestsRow?> ReadAsync(string why)
    {
        try
        {
            var flights = state.Flights();
            var catalog = repos.Repos();
            var tracked = TrackedRepos(catalog, flights, state.PrReviews());
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
            var cost = 0;
            int? remaining = null;
            DateTimeOffset? limitedUntil = null;
            var searches = PullRequestSearch.Batches(tracked);
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
            // Not read this time (its batch failed, or the limit stopped it): what was read of it before stands, under the error or note.
            prs.AddRange(heldPrs.Where(p => !read.Contains(p.Repo)));

            var row = new PullRequestsRow
            {
                Orgs = orgs,
                PullRequests = [.. prs.Select(p => p with { Flight = FlightOf(p, catalog, flights) })],
                Errors = [.. errors.Distinct()],
                Cost = cost,
                RateRemaining = remaining,
                Note = limitedUntil is { } reset ? $"Rate-limited by GitHub until {reset.UtcDateTime:HH:mm} UTC: showing what was last read, reading again then" : null,
                ReadAt = now,
                Viewer = await ViewerAsync(),
            };
            await Error.WriteLineAsync($"pull request watch: read {row.PullRequests.Length} open PRs of {tracked.Length} repos in {searches.Length} searches, "
                + $"{cost} GraphQL points{(remaining is { } left ? $", {left} left" : "")} ({why})");
            state.PutPullRequests(row);
            return row;
        }
        finally
        {
            lock (_gate)
            {
                _lastRead = time.GetUtcNow();
                _running = null;
            }
        }
    }

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

    private static string? OwnerOf(string? repo) => repo?.IndexOf('/') is > 0 and var slash ? repo[..slash] : null;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
