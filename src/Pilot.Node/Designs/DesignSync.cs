using System.Globalization;
using System.Text;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Designs;

/// <summary>
/// A published design version is kept on GitHub (PIL1171): its files go to the branch <c>design/&lt;issue&gt;</c> as one commit whose message is the why, the document goes
/// between the markers of the issue's body, and the issue gets the comment <c>v&lt;k&gt;: &lt;why&gt;</c>. A design with no issue gets one made in its repo at its first
/// publish, labelled <see cref="DesignSyncText.Label"/>; one can be linked to an issue that is there (<see cref="LinkAsync"/>). The versions go oldest first, each try
/// under a deadline; a failure keeps the version and says so on the record and never blocks a publish, and the node's timer sends it again.
/// The other way round, <see cref="MirrorAsync"/> lists the labelled issues of the repos the node knows and keeps each one made on another node as a read-only design.
/// Nothing here holds the store's lock across a GitHub call.
/// </summary>
public sealed class DesignSync(DesignStore store, RepoCatalog repos, IGitHub github, TimeProvider time, string node)
{
    /// <summary>The longest one try (a version's whole write, or a repo's listing) may take.</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How often the issues are read for designs of other nodes.</summary>
    public TimeSpan MirrorEvery { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Where what a failure was is told: the node's log; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _kicks = new();
    private readonly List<Task> _running = [];
    private DateTimeOffset _mirrored = DateTimeOffset.MinValue;

    /// <summary>Starts sending <paramref name="id"/>'s unsent versions in the background, so a publish does not wait for GitHub.</summary>
    public void Kick(string id)
    {
        // A design with no repo has nowhere to be written: that is said on its record before the publish returns, not by a task that races the reader of the record.
        if (store.Find(id) is { MadeOn: null, Current: not null } design && (design.IssueRepo ?? design.Repo) is not { Length: > 0 })
        {
            Failed(design.Id, DesignSyncText.NoRepo(design.Id, (design.SyncedVersion ?? 0) + 1, node));
            return;
        }
        var task = Task.Run(() => SyncAsync(id, CancellationToken.None));
        lock (_kicks)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(task);
        }
    }

    /// <summary>Completes once every <see cref="Kick"/> so far has ended.</summary>
    public Task WhenIdleAsync()
    {
        lock (_kicks)
        {
            return Task.WhenAll(_running);
        }
    }

    /// <summary>The timer's round: every design with a version not sent is sent, then the issues are read for other nodes' designs when <see cref="MirrorEvery"/> has passed.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var design in store.List().Where(d => d.MadeOn is null && Unsent(d)))
        {
            await SyncAsync(design.Id, cancellationToken).ConfigureAwait(false);
        }
        if (time.GetUtcNow() - _mirrored >= MirrorEvery)
        {
            _mirrored = time.GetUtcNow();
            await MirrorAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Unsent(DesignRow design) => design.Current is { } current && current > (design.SyncedVersion ?? 0);

    private RepoInfo? Resolve(string? given) =>
        given is { Length: > 0 }
            ? repos.Repos().FirstOrDefault(r => r.GitHub is not null && (string.Equals(r.GitHub, given, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, given, StringComparison.OrdinalIgnoreCase)))
            : null;

    /// <summary>
    /// Links design <paramref name="id"/> to issue <paramref name="number"/> of <paramref name="repo"/> (a repo name the node lists, or <c>owner/name</c>): the issue must be there, and gets
    /// the <see cref="DesignSyncText.Label"/> label. Returns what to tell the person. The versions already published are sent at once.
    /// </summary>
    public async Task<string> LinkAsync(string id, string repo, int number, CancellationToken cancellationToken)
    {
        var design = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        if (design.MadeOn is not null)
        {
            throw new DesignException($"{design.Id} is read from {design.IssueRepo}#{design.IssueNumber}: it is linked on the node that made it");
        }
        var info = Resolve(repo) ?? throw new DesignException($"{repo} is no repo on GitHub that this node lists");
        try
        {
            using var deadline = Deadlined(cancellationToken);
            var token = deadline.Token;
            _ = await github.IssueNowAsync(info.Path, number, token).ConfigureAwait(false)
                ?? throw new DesignException($"{info.GitHub} has no issue #{number.ToString(CultureInfo.InvariantCulture)}");
            await github.EnsureLabelAsync(info.Path, DesignSyncText.Label, token).ConfigureAwait(false);
            await github.AddIssueLabelAsync(info.Path, number, DesignSyncText.Label, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FlightException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DesignException($"{info.GitHub}#{number.ToString(CultureInfo.InvariantCulture)} could not be linked: {(ex is FlightException ? ex.Message : DesignSyncText.NoAnswer)}");
        }
        var linked = store.Update(design.Id, r => r with { Repo = r.Repo ?? info.Name, IssueRepo = info.GitHub, IssueNumber = number, SyncError = null, SyncErrorAt = null });
        if (Unsent(linked))
        {
            Kick(linked.Id);
        }
        return $"{linked.Id} is kept in {info.GitHub}#{number.ToString(CultureInfo.InvariantCulture)}";
    }

    private Try Deadlined(CancellationToken cancellationToken) => new(cancellationToken, Deadline, time);

    /// <summary>One try's token: cancelled with the caller's, or when the deadline (on the node's clock) passes.</summary>
    private sealed class Try : IDisposable
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _linked;

        public Try(CancellationToken cancellationToken, TimeSpan deadline, TimeProvider time)
        {
            _timeout = new CancellationTokenSource(deadline, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeout.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _linked.Dispose();
            _timeout.Dispose();
        }
    }

    /// <summary>Sends every version of design <paramref name="id"/> not on its issue yet, oldest first, stopping at the first that fails; its record says how it stands.</summary>
    public async Task SyncAsync(string id, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (store.Find(id) is { MadeOn: null, Current: { } current } design && current > (design.SyncedVersion ?? 0))
            {
                var version = (design.SyncedVersion ?? 0) + 1;
                if (!await SendAsync(design, version, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>One version's whole write. False when it failed (the record then says so).</summary>
    private async Task<bool> SendAsync(DesignRow design, int version, CancellationToken cancellationToken)
    {
        var repoName = design.IssueRepo ?? design.Repo;
        if (repoName is not { Length: > 0 })
        {
            Failed(design.Id, DesignSyncText.NoRepo(design.Id, version, node));
            return false;
        }
        var info = Resolve(repoName);
        if (info is null)
        {
            Failed(design.Id, DesignSyncText.Failure($"{repoName} is no repo on GitHub that {node} lists", version, node));
            return false;
        }
        // The page shows 'Writing v<k> to <repo>#<n>...' while the try runs: the last failure's line is taken off.
        store.Update(design.Id, r => r with { SyncError = null, SyncErrorAt = null });
        try
        {
            using var deadline = Deadlined(cancellationToken);
            var token = deadline.Token;
            var files = store.ReadVersion(design.Id, version);
            var row = design;
            var why = row.History.FirstOrDefault(v => v.Number == version)?.Why ?? (version == row.Current ? row.Why : null) ?? $"v{version.ToString(CultureInfo.InvariantCulture)}";
            var document = files.TryGetValue(DesignStore.DocumentFile, out var text)
                ? DesignDocument.Parse(Encoding.UTF8.GetString(text))
                : new DesignDocument(row.Title ?? row.Id, "", []);
            if (row.IssueNumber is not { } issue)
            {
                // Made once: the number is on the record before anything else is written, so a retry never makes a second issue.
                var made = await github.CreateLabelledIssueAsync(info.Path, $"Design: {row.Title ?? row.Id}", "", DesignSyncText.Label, token).ConfigureAwait(false);
                issue = made.Number;
                row = store.Update(row.Id, r => r with { IssueRepo = info.GitHub, IssueNumber = issue });
            }
            var repoFull = info.GitHub!;
            // The files, as one commit whose message is the why.
            await github.CommitFilesAsync(info.Path, DesignSyncText.Branch(issue), why, files, token).ConfigureAwait(false);
            var block = DesignIssueDocument.Block(row.Id, document, version, why, node, repoFull, issue, [.. files.Keys.Where(k => k.StartsWith("renders/", StringComparison.Ordinal))]);
            var now = await github.IssueNowAsync(info.Path, issue, token).ConfigureAwait(false)
                ?? throw new FlightException($"{repoFull} has no issue #{issue.ToString(CultureInfo.InvariantCulture)}");
            var body = DesignIssueDocument.Splice(now.Body, block);
            if (body != now.Body && !await github.EditIssueAsync(info.Path, issue, now.Title, body, now.UpdatedAt, token).ConfigureAwait(false))
            {
                throw new FlightException($"{repoFull}#{issue.ToString(CultureInfo.InvariantCulture)} changed while its body was written");
            }
            await github.CommentIssueAsync(info.Path, issue, $"v{version.ToString(CultureInfo.InvariantCulture)}: {why}", token).ConfigureAwait(false);
            var at = time.GetUtcNow();
            store.Update(row.Id, r => r with
            {
                SyncedVersion = version,
                SyncError = null,
                SyncErrorAt = null,
                History = [.. r.History.Select(v => v.Number == version ? v with { SyncedAt = at } : v)],
            });
            return true;
        }
        catch (Exception ex) when (ex is FlightException or DesignException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await Error.WriteLineAsync($"design: {design.Id} v{version.ToString(CultureInfo.InvariantCulture)} was not written to GitHub: {(ex is OperationCanceledException ? "it took longer than " + Deadline : ex.Message)}").ConfigureAwait(false);
            Failed(design.Id, DesignSyncText.Failure(DesignSyncText.NoAnswer, version, node));
            return false;
        }
    }

    private void Failed(string id, string line) =>
        store.Update(id, r => r with { SyncError = line, SyncErrorAt = time.GetUtcNow() });

    /// <summary>
    /// Reads the open issues labelled <see cref="DesignSyncText.Label"/> of the repos the node knows, and keeps each one made on another node (and not linked here) as a read-only design:
    /// its document, prototype and renders read from its branch, under <c>designs/GH-&lt;owner&gt;-&lt;repo&gt;-&lt;n&gt;/</c>. A version already kept is not read again.
    /// A repo or an issue that cannot be read is left for the next round.
    /// </summary>
    public async Task MirrorAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var info in repos.Tracked().Where(r => r.GitHub is not null))
            {
                try
                {
                    using var deadline = Deadlined(cancellationToken);
            var token = deadline.Token;
                    var issues = await github.LabelledIssuesAsync(info.Path, DesignSyncText.Label, token).ConfigureAwait(false);
                    foreach (var issue in issues)
                    {
                        await MirrorOneAsync(info, issue, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is FlightException or DesignException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    await Error.WriteLineAsync($"design: the designs of {info.GitHub} could not be read: {(ex is OperationCanceledException ? "it took longer than " + Deadline : ex.Message)}").ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task MirrorOneAsync(RepoInfo info, MilestoneIssue issue, CancellationToken token)
    {
        if (DesignIssueDocument.Meta(issue.Body) is not { } meta || string.Equals(meta.Node, node, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var gitHub = info.GitHub!;
        if (store.List().Any(d => d.MadeOn is null && string.Equals(d.IssueRepo, gitHub, StringComparison.OrdinalIgnoreCase) && d.IssueNumber == issue.Number))
        {
            return;
        }
        var id = DesignStore.MirrorId(gitHub, issue.Number);
        var have = store.Find(id);
        if (have?.SyncedVersion is { } kept && kept >= meta.Version)
        {
            return;
        }
        var files = await github.BranchFilesAsync(info.Path, DesignSyncText.Branch(issue.Number), token).ConfigureAwait(false);
        if (files is null || !files.ContainsKey(DesignStore.PrototypeFile))
        {
            return;
        }
        var row = have ?? new DesignRow { Id = id, At = issue.CreatedAt };
        store.Mirror(row with
        {
            Title = meta.Title is { Length: > 0 } title ? title : issue.Title,
            Repo = info.Name,
            IssueRepo = gitHub,
            IssueNumber = issue.Number,
            MadeOn = meta.Node,
        }, meta.Version, meta.Why, files);
    }
}

/// <summary>Sends the versions not on their issues again, and reads the issues for other nodes' designs, on the node's timer (PIL1171).</summary>
public sealed class DesignSyncSweep(DesignSync sync) : Microsoft.Extensions.Hosting.BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            do
            {
                await sync.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
