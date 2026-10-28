using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps the node's <see cref="PullRequestsRow"/>: every open PR of every org the clones' <c>origin</c>s belong to
/// (<see cref="RepoCatalog"/>), one paged GitHub search per org (<see cref="IGitHub.OpenPullRequestsOfAsync"/>), read every
/// <see cref="Interval"/> and on a <see cref="Verbs.RefreshPullRequests"/>, and put on the event stream for every client. An org
/// whose search fails keeps the PRs last read of it and says so in <see cref="PullRequestsRow.Errors"/>, never failing the rest; one
/// refresh runs at a time, and one asked for while another runs gets that one's answer.
/// </summary>
public sealed class PullRequestWatch(NodeState state, RepoCatalog repos, IGitHub github, TimeProvider time) : BackgroundService
{
    private readonly object _gate = new();
    private Task<PullRequestsRow?>? _running;
    private string? _viewer;

    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Reads every org afresh and puts the row on the stream; joins the refresh already running, if one is. Null when the clones have no org to read.</summary>
    public Task<PullRequestsRow?> RefreshAsync(CancellationToken cancellationToken)
    {
        Task<PullRequestsRow?> run;
        lock (_gate)
        {
            run = _running ??= Task.Run(ReadAsync);
        }
        return run.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
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
                    await Error.WriteLineAsync($"pull request watch: one read failed, reading again: {ex.Message}");
                }
                await Task.Delay(Interval, time, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task<PullRequestsRow?> ReadAsync()
    {
        try
        {
            var catalog = repos.Repos();
            var orgs = catalog.Select(r => OwnerOf(r.GitHub)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var held = state.PullRequests();
            // A node whose clones have no GitHub origin has nothing to say, and says nothing until it has.
            if (orgs.Length == 0 && held is null)
            {
                return null;
            }
            var reads = await Task.WhenAll(orgs.Select(ReadOrgAsync));

            var flights = state.Flights();
            var prs = new List<PullRequestRow>();
            var errors = new List<PullRequestsError>();
            foreach (var (org, read, error) in reads)
            {
                if (read is not null)
                {
                    prs.AddRange(read);
                    continue;
                }
                errors.Add(new PullRequestsError { Org = org, Message = error! });
                // Not read this time: what was read of it before stands, under the error, rather than the org's PRs vanishing for a minute.
                prs.AddRange((held?.PullRequests ?? []).Where(p => string.Equals(OwnerOf(p.Repo), org, StringComparison.OrdinalIgnoreCase)));
            }
            var row = new PullRequestsRow
            {
                Orgs = orgs,
                PullRequests = [.. prs.Select(p => p with { Flight = FlightOf(p, catalog, flights) })],
                Errors = [.. errors],
                ReadAt = time.GetUtcNow(),
                Viewer = await ViewerAsync(),
            };
            state.PutPullRequests(row);
            return row;
        }
        finally
        {
            lock (_gate)
            {
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

    private async Task<(string Org, PullRequestRow[]? Read, string? Error)> ReadOrgAsync(string org)
    {
        try
        {
            return (org, await github.OpenPullRequestsOfAsync(org, CancellationToken.None), null);
        }
        catch (Exception ex) when (ex is FlightException or HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            return (org, null, ex.Message);
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
