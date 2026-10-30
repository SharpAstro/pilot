using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Brings each tracked repo up to date with its remote: <c>git fetch -p --all</c>, then a fast-forward-only merge of
/// <c>origin/&lt;default&gt;</c> into the main checkout. Never a merge commit, and the main checkout is left alone when
/// it is dirty, on another branch, diverged from origin or has no origin default to take; each of those is an outcome
/// that says so, not an error. A repo's failure is its own and never stops the others. Every git call has a timeout.
/// </summary>
public sealed class RepoSync(IGit git, Planes planes, RepoWatch watch)
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>One lock per clone, by its full path, so no two syncs (a Sync repos, a leg's start) fetch or merge the same clone at once.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clones = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>How long one git call may take before it is cut off.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Syncs every tracked repo, one at a time, then has <see cref="RepoWatch"/> read them afresh; one outcome per repo.</summary>
    public async Task<RepoSyncOutcome[]> SyncAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var tracked = (await watch.StatusAsync(refresh: false, cancellationToken)).Where(r => r.Tracked).ToArray();
            var outcomes = new List<RepoSyncOutcome>();
            foreach (var repo in tracked)
            {
                outcomes.Add(await SyncOneAsync(repo.Name, repo.Path, cancellationToken));
            }
            await watch.RefreshAsync(cancellationToken);
            return [.. outcomes];
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The last <see cref="RefreshWatchInBackground"/>, so a test can wait for it; completed when none has run.</summary>
    internal Task WatchRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Has <see cref="RepoWatch"/> read the repos afresh without the caller waiting on it, as a leg's start does after it
    /// pulled a sibling, so the dashboard stops showing that clone behind. A failed read is logged, never thrown.
    /// </summary>
    internal void RefreshWatchInBackground()
        => WatchRefresh = Task.Run(async () =>
        {
            try
            {
                await watch.RefreshAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                await Error.WriteLineAsync($"repo sync: the repo watch could not be refreshed after a pull: {ex.Message}");
            }
        });

    /// <summary>
    /// Fetches one clone and fast-forwards its main checkout's default branch, under the rules above. Also what a leg's start
    /// runs on a sibling clone found behind its origin, so the person is told only about one the node could not bring up.
    /// Takes the clone's lock, so a second sync of it waits for this one and then finds it current.
    /// </summary>
    internal Task<RepoSyncOutcome> SyncOneAsync(string name, string path, CancellationToken cancellationToken)
        => SyncOneAsync(name, path, fetch: true, cancellationToken);

    /// <summary>
    /// The same sync of one clone with the fetch left out when <paramref name="fetch"/> is false: it reads the default ref, the
    /// checkout, its dirtiness and ahead/behind from what is already fetched and fast-forwards under the same
    /// Skipped/Current/Updated/Failed rules. For a caller that has just fetched, so a leg's start does not fetch twice.
    /// </summary>
    internal async Task<RepoSyncOutcome> SyncOneAsync(string name, string path, bool fetch, CancellationToken cancellationToken)
    {
        var clone = _clones.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), _ => new SemaphoreSlim(1, 1));
        await clone.WaitAsync(cancellationToken);
        try
        {
            return await SyncLockedAsync(name, path, fetch, cancellationToken);
        }
        finally
        {
            clone.Release();
        }
    }

    private async Task<RepoSyncOutcome> SyncLockedAsync(string name, string path, bool fetch, CancellationToken cancellationToken)
    {
        RepoSyncOutcome Outcome(string status, string detail) => new() { Name = name, Status = status, Detail = detail };
        try
        {
            if (fetch)
            {
                var fetched = await RunAsync(["fetch", "-p", "--all"], path, cancellationToken);
                if (!fetched.Ok)
                {
                    return Outcome(RepoSyncStatus.Failed, $"git fetch failed: {fetched.Why}");
                }
            }

            // Read with the fetch's news: the remote's default branch may have moved or been renamed.
            using var budget = Budget(cancellationToken);
            var defaultRef = await planes.DefaultRefAsync(path, budget.Token);
            if (defaultRef is null)
            {
                return Outcome(RepoSyncStatus.Skipped, "no origin default branch to take (no upstream)");
            }
            var branch = defaultRef.StartsWith("origin/", StringComparison.Ordinal) ? defaultRef["origin/".Length..] : defaultRef;

            var current = (await planes.WorktreesAsync(path, budget.Token))?.FirstOrDefault()?.Branch;
            if (current != branch)
            {
                return Outcome(RepoSyncStatus.Skipped, current is null ? $"the main checkout is detached, not on {branch}" : $"the main checkout is on {current}, not {branch}");
            }
            switch (await planes.IsDirtyAsync(path, budget.Token, withoutPlanes: true))
            {
                case true:
                    return Outcome(RepoSyncStatus.Skipped, "the main checkout has uncommitted changes");
                case null:
                    return Outcome(RepoSyncStatus.Skipped, "git could not read the main checkout's status");
            }
            if (await planes.AheadBehindAsync(path, defaultRef, budget.Token) is not { } sync)
            {
                return Outcome(RepoSyncStatus.Skipped, $"git could not compare {branch} with {defaultRef}");
            }
            if (sync.Behind == 0)
            {
                return Outcome(RepoSyncStatus.Current, sync.Ahead > 0 ? $"{branch} is {sync.Ahead} ahead of {defaultRef}, nothing to take" : $"{branch} is up to date");
            }
            if (sync.Ahead > 0)
            {
                return Outcome(RepoSyncStatus.Skipped, $"{branch} has diverged from {defaultRef} ({sync.Ahead} ahead, {sync.Behind} behind)");
            }

            // The one checkout a person builds from: its LFS files get their content, not the planes' pointers.
            var merge = await RunAsync(["merge", "--ff-only", defaultRef], path, cancellationToken, smudge: true);
            return merge.Ok
                ? Outcome(RepoSyncStatus.Updated, $"{branch} took {sync.Behind} commit{(sync.Behind == 1 ? "" : "s")} from {defaultRef}")
                : Outcome(RepoSyncStatus.Failed, $"git merge --ff-only {defaultRef} failed: {merge.Why}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Outcome(RepoSyncStatus.Failed, $"git ran past {Timeout.TotalSeconds:0.##} seconds and was stopped");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Outcome(RepoSyncStatus.Failed, ex.Message);
        }
    }

    private async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken, bool smudge = false)
    {
        using var budget = Budget(cancellationToken);
        return smudge ? await git.RunSmudgingAsync(args, cwd, budget.Token) : await git.RunAsync(args, cwd, budget.Token);
    }

    private CancellationTokenSource Budget(CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        return budget;
    }
}

/// <summary>The repo verbs: <c>sync-repos</c> and <c>create-issue</c>. Both change something outside the node, so both are the controller's.</summary>
public sealed class RepoCommander(NodeState state, RepoSync sync, Planes planes, IGitHub github)
{
    public static bool Handles(string verb) => verb is Verbs.SyncRepos or Verbs.CreateIssue;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        try
        {
            return command.Verb == Verbs.SyncRepos
                ? state.Complete(command, null, made: string.Join('\n', (await sync.SyncAsync(cancellationToken)).Select(o => o.Line())))
                : await CreateIssueAsync(command, cancellationToken);
        }
        catch (FlightException ex)
        {
            return state.Complete(command, ex.Message);
        }
    }

    /// <summary><c>create-issue repo/&lt;path&gt; &lt;title&gt; [body]</c>; the number is what the result made.</summary>
    private async Task<CommandResult> CreateIssueAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (!command.Target.StartsWith(Verbs.RepoTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.RepoTargetPrefix.Length)
        {
            return state.Complete(command, $"create-issue takes a repo target, {Verbs.RepoTargetPrefix}<path to its checkout>, not {command.Target}");
        }
        var args = command.Args ?? [];
        var title = args.Length > 0 ? args[0].Trim() : "";
        if (title.Length == 0)
        {
            return state.Complete(command, "create-issue needs a title: create-issue repo/<path> <title> [body]");
        }
        var body = args.Length > 1 ? string.Join(' ', args.Skip(1)).Trim() : "";
        var path = command.Target[Verbs.RepoTargetPrefix.Length..];
        if (await planes.MainCheckoutAsync(path, cancellationToken) is not { } repoPath)
        {
            return state.Complete(command, $"{path} is no git checkout");
        }
        var issue = await github.CreateIssueAsync(repoPath, title, body.Length > 0 ? body : null, cancellationToken);
        return state.Complete(command, null, made: issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
