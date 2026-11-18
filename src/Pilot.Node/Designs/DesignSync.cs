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

    /// <summary>The label lock and the read-back of the issue's text (PIL1171), settled after each design's versions are sent and at every sweep; null where none is wired.</summary>
    public DesignLock? Lock { get; init; }

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
            // Not a failure (#1183): the design is kept here only, which the page shows as a state. A line an older release stored for it is cleared.
            if (design.SyncError is not null)
            {
                store.Update(design.Id, r => r with { SyncError = null, SyncErrorAt = null });
            }
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
            // One design's failure (a store file that cannot be written, say) must not skip the designs after it.
            try
            {
                await SyncAsync(design.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Error.WriteLineAsync($"design: {design.Id} could not be sent: {ex.Message}").ConfigureAwait(false);
            }
        }
        if (Lock is { } locks)
        {
            await locks.SweepAsync(cancellationToken).ConfigureAwait(false);
        }
        if (time.GetUtcNow() - _mirrored >= MirrorEvery)
        {
            _mirrored = time.GetUtcNow();
            try
            {
                await MirrorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Error.WriteLineAsync($"design: the designs on GitHub could not be read: {ex.Message}").ConfigureAwait(false);
            }
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
            var now = await github.IssueNowAsync(info.Path, number, token).ConfigureAwait(false)
                ?? throw new DesignException($"{info.GitHub} has no issue #{number.ToString(CultureInfo.InvariantCulture)}");
            // One issue keeps one design: an issue whose body holds another design's block (or that another design here is linked to) is refused, never overwritten.
            if (DesignIssueDocument.Meta(now.Body) is { } meta && !(string.Equals(meta.Design, design.Id, StringComparison.Ordinal) && string.Equals(meta.Node, node, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DesignException($"{info.GitHub}#{number.ToString(CultureInfo.InvariantCulture)} already keeps {meta.Design} of {meta.Node}: one issue keeps one design");
            }
            if (store.List().FirstOrDefault(d => d.Id != design.Id && d.MadeOn is null && string.Equals(d.IssueRepo, info.GitHub, StringComparison.OrdinalIgnoreCase) && d.IssueNumber == number) is { } other)
            {
                throw new DesignException($"{info.GitHub}#{number.ToString(CultureInfo.InvariantCulture)} already keeps {other.Id}: one issue keeps one design");
            }
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

    /// <summary>The ideas of <paramref name="design"/> as GitHub shows them, <c>owner/repo#n</c> each; an idea of a repo this node does not list is left out.</summary>
    private string[] IdeaRefs(DesignRow design) =>
        [.. design.Ideas().Select(key => IdeaIds.Parse(key).Ref is { Repo: { } name } r && Resolve(name) is { GitHub: { } gh } ? $"{gh}#{r.Number.ToString(CultureInfo.InvariantCulture)}" : null).OfType<string>()];

    /// <summary>The idea key (<c>pilot I381</c>) of an <c>owner/repo#n</c> reference, by the repo this node lists under that GitHub name; null when it reads as none.</summary>
    private string? KeyOfRef(string reference)
    {
        var hash = reference.LastIndexOf('#');
        if (hash <= 0 || !int.TryParse(reference.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            return null;
        }
        var gh = reference[..hash];
        var name = repos.Repos().FirstOrDefault(r => string.Equals(r.GitHub, gh, StringComparison.OrdinalIgnoreCase))?.Name ?? gh[(gh.LastIndexOf('/') + 1)..];
        return NodeState.IdeaKey(name, IdeaIds.Id(number));
    }

    /// <summary>
    /// A link of design <paramref name="id"/> to <paramref name="idea"/> was made (<paramref name="linked"/>) or taken away (#1185): the ideas the design's issue lists are written again, and the idea's issue is told once.
    /// Best effort: the link is on the design's record whatever GitHub does, so a failure is in the node's log and never undoes it.
    /// </summary>
    public async Task IdeaLinkChangedAsync(string id, string idea, bool linked, CancellationToken cancellationToken)
    {
        var design = store.Find(id);
        if (design is null || design.MadeOn is not null)
        {
            return;
        }
        string? issueRef = null;
        try
        {
            using var deadline = Deadlined(cancellationToken);
            var token = deadline.Token;
            if (design.IssueNumber is { } issue && Resolve(design.IssueRepo ?? design.Repo) is { } info)
            {
                issueRef = $"{info.GitHub}#{issue.ToString(CultureInfo.InvariantCulture)}";
                if (await github.IssueNowAsync(info.Path, issue, token).ConfigureAwait(false) is { } now && now.Body is { Length: > 0 } body)
                {
                    var next = DesignIssueDocument.WithIdeas(body, IdeaRefs(design));
                    if (next != body)
                    {
                        await github.EditIssueAsync(info.Path, issue, now.Title, next, now.UpdatedAt, token).ConfigureAwait(false);
                    }
                }
            }
            if (IdeaIds.Parse(idea).Ref is { Repo: { } name } r && Resolve(name) is { } ideaRepo)
            {
                var where = issueRef is null ? design.Id : $"{design.Id} ({issueRef})";
                await github.CommentIssueAsync(ideaRepo.Path, r.Number, linked ? $"Design {where} is linked to this idea." : $"Design {where} is no longer linked to this idea.", token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is FlightException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await Error.WriteLineAsync($"design: the link of {design.Id} to {idea} was kept, but GitHub was not told: {(ex is OperationCanceledException ? "it took longer than " + Deadline : ex.Message)}").ConfigureAwait(false);
        }
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
        // The labels of a published design come off once its version is on the issue, so another node never sees an unlocked design with the old text.
        if (Lock is { } locks)
        {
            await locks.SettleAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One version's whole write. False when it failed (the record then says so).</summary>
    private async Task<bool> SendAsync(DesignRow design, int version, CancellationToken cancellationToken)
    {
        var repoName = design.IssueRepo ?? design.Repo;
        if (repoName is not { Length: > 0 })
        {
            return false;
        }
        var info = Resolve(repoName);
        if (info is null)
        {
            Failed(design.Id, DesignSyncText.Failure($"{repoName} is no repo on GitHub that {node} lists", version, node));
            return false;
        }
        // The page shows 'Writing v<k> to <repo>#<n>...' while the try runs: the last failure's line is taken off. A held version keeps its 'Held:' line until the hold is seen gone,
        // so a round under a hold writes the record at most once and the page never flickers through 'Writing' on the way back to 'Held'.
        if (design.SyncError is not null)
        {
            store.Update(design.Id, r => r with { SyncError = null, SyncErrorAt = null });
        }
        try
        {
            using var deadline = Deadlined(cancellationToken);
            var token = deadline.Token;
            if (design.IssueNumber is { } held && (await github.IssueLabelsOrThrowAsync(info.Path, held, token).ConfigureAwait(false)).Contains(PilotMarks.Hold, StringComparer.OrdinalIgnoreCase))
            {
                // 'pilot: hold' keeps its meaning: no node acts on the issue. Not an error; the page says it is held, and the timer sends the version once the hold is off.
                if (design.SyncHeld != true)
                {
                    store.Update(design.Id, r => r with { SyncHeld = true });
                }
                return false;
            }
            if (design.SyncHeld == true)
            {
                store.Update(design.Id, r => r with { SyncHeld = null });
            }
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
            var block = DesignIssueDocument.Block(row.Id, document, version, why, node, repoFull, issue, [.. files.Keys.Where(k => k.StartsWith("renders/", StringComparison.Ordinal))], IdeaRefs(row));
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
                SyncHeld = null,
                History = [.. r.History.Select(v => v.Number == version ? v with { SyncedAt = at } : v)],
            });
            return true;
        }
        catch (Exception ex) when (ex is FlightException or DesignException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await Error.WriteLineAsync($"design: {design.Id} v{version.ToString(CultureInfo.InvariantCulture)} was not written to GitHub: {(ex is OperationCanceledException ? "it took longer than " + Deadline : ex.Message)}").ConfigureAwait(false);
            // The page says the real reason: 'GitHub did not answer' only for a try that ran out of time, else what failed (an issue changed while its body was written, a missing issue, a refusal).
            // A gh failure can run to many lines; the page takes its first, and the whole message is in the node's log just above.
            Failed(design.Id, DesignSyncText.Failure(ex is OperationCanceledException ? DesignSyncText.NoAnswer : FirstLine(ex.Message), version, node));
            return false;
        }
    }

    private void Failed(string id, string line) =>
        store.Update(id, r => r with { SyncError = line, SyncErrorAt = time.GetUtcNow() });

    // The first line of a failure's message with words on it, without its closing full stop (the page's sentence adds its own).
    internal static string FirstLine(string message) =>
        (message.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? message.Trim()).TrimEnd('.');

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
                catch (Exception ex) when (ex is FlightException or DesignException or IOException or UnauthorizedAccessException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
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
        var ideas = meta.Ideas is { } listed ? listed.Select(KeyOfRef).OfType<string>().ToArray() : [];
        if (have?.SyncedVersion is { } kept && kept >= meta.Version)
        {
            // The ideas of a design change without a new version: the links are read again.
            if (!have.ForIdeas.SequenceEqual(ideas, StringComparer.OrdinalIgnoreCase))
            {
                store.Update(id, r => r with { ForIdeas = ideas });
            }
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
            ForIdeas = ideas,
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
                try
                {
                    await sync.SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unexpected failure ends this round, not the node (the host stops on an exception out of a hosted service).
                    await sync.Error.WriteLineAsync($"design: the sync round failed: {ex.Message}").ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
