using System.Globalization;
using System.Text;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Designs;

/// <summary>
/// Who wins when a design's issue and a node both changed (PIL1171) is settled by a label lock, not a merge. A node that edits a design (its designer drafting, or the owner
/// typing) puts <see cref="DesignLockText.EditingLabel"/> and its own <see cref="PilotMarks.NodeLabel"/> on the design's issue; another node that sees both shows the design read
/// only, 'being edited on &lt;node&gt;', and writes nothing to the issue. The labels come off when the version is published or the draft discarded, when the designer's session
/// ends, and when the locking node has not been heard from for <see cref="StaleAfter"/>. <c>pilot: hold</c> keeps its meaning: while the issue carries it, no node acts here.
/// </summary>
/// <remarks>
/// A failure to label is on the design's record (<see cref="DesignRow.LockError"/>) and tried again by <see cref="SweepAsync"/>; it never blocks a local edit.
/// The other direction, <see cref="ReadBackAsync"/>, reads the issue's own text: an edit made on GitHub is a typed change while nobody holds the lock, and a draft kept beside the design
/// (<see cref="DesignRow.GitHubEdits"/>) for the editor while this node does.
/// </remarks>
public sealed class DesignLock(DesignStore store, RepoCatalog repos, IGitHub github, TimeProvider time, string node, Func<string, TimeSpan?> silence)
{
    /// <summary>
    /// How long a node may go unheard before the lock it left on an issue is taken off by any node. A peer is read every few seconds to minutes, so a quarter of an hour of
    /// silence is a node that is down or cut off, not one that is busy, and a design must not wait on it for good; a sleep or a reboot is shorter. The owner can also take the label off by hand.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often the sweep reads the issue of a design nothing suggests is changing (no lock wanted, held or seen, no labelling to retry). Such a design is read for an edit made on
    /// GitHub or a lock another node took; ten minutes, as often as the mirror of other nodes' designs, keeps a node with many designs from reading every issue every round.
    /// A design open for editing here or elsewhere is read every round.
    /// </summary>
    public static readonly TimeSpan QuietEvery = TimeSpan.FromMinutes(10);

    /// <summary>The longest one try (a read, a labelling) may take.</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Where what a failure was is told: the node's log; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>Told what the owner typed (through a read-back or an Apply) so the designer hears of it at its next turn: the design, the part, the cell, the text. Null where no designer is wired.</summary>
    public Action<string, string, string, string>? Typed { get; set; }

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Lock _kicks = new();
    private readonly List<Task> _running = [];
    private readonly Dictionary<string, DateTimeOffset> _read = new(StringComparer.Ordinal);

    /// <summary>This node's code, as its node label names it.</summary>
    public string Node => node;

    /// <summary>
    /// Records that this node wants the lock on <paramref name="id"/>'s issue (its designer drafts, or the owner typed) and starts putting the labels on in the background.
    /// Refused only when another node is known to hold the lock; never because GitHub did not answer.
    /// </summary>
    /// <exception cref="DesignException">The design is locked on another node.</exception>
    public void Want(string id)
    {
        var row = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        if (row.LockedBy is { Length: > 0 } by && row.LockHeld != true)
        {
            throw new DesignException(DesignLockText.BeingEdited(by));
        }
        if (row.LockWanted == true && (row.LockHeld == true || row.LockError is null))
        {
            // Wanted already: held, or the labelling is under way. A further typed edit (every autosave makes one) needs no settle of its own; a failed one is tried again here or by the sweep.
            return;
        }
        if (row.LockWanted != true)
        {
            store.Update(row.Id, r => r with { LockWanted = true });
        }
        Kick(row.Id);
    }

    /// <summary>Records that this node no longer needs the lock (published, discarded, the designer's session ended) and takes the labels off in the background unless <paramref name="kick"/> is false.</summary>
    public void Release(string id, bool kick = true)
    {
        if (store.Find(id) is not { } row)
        {
            return;
        }
        if (row.LockWanted == true)
        {
            store.Update(row.Id, r => r with { LockWanted = false });
        }
        if (kick)
        {
            Kick(row.Id);
        }
    }

    /// <summary>Starts <see cref="SettleAsync"/> of <paramref name="id"/> in the background.</summary>
    public void Kick(string id)
    {
        var task = Task.Run(() => SettleAsync(id, CancellationToken.None));
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

    /// <summary>
    /// The timer's round: every design with an issue that something suggests is changing (<see cref="Busy"/>) is settled and its issue's text read back; the rest once every
    /// <see cref="QuietEvery"/>.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var design in store.List().Where(d => d.IssueNumber is not null))
        {
            var now = time.GetUtcNow();
            if (!Busy(design) && _read.TryGetValue(design.Id, out var last) && now - last < QuietEvery)
            {
                continue;
            }
            _read[design.Id] = now;
            try
            {
                await SettleAsync(design.Id, cancellationToken).ConfigureAwait(false);
                await ReadBackAsync(design.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One design's failure (design.json that cannot be written) must not skip the designs after it.
                await Error.WriteLineAsync($"design: {design.Id} could not be settled: {ex.Message}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether a design is open for editing here or elsewhere, or its labels are still to be put right: its issue is read every round.</summary>
    internal static bool Busy(DesignRow design) =>
        design.LockWanted == true || design.LockHeld == true || design.LockedBy is { Length: > 0 } || design.LockError is not null || design.GitHubEdits.Length > 0;

    private RepoInfo? Resolve(string? given) =>
        given is { Length: > 0 }
            ? repos.Repos().FirstOrDefault(r => r.GitHub is not null && (string.Equals(r.GitHub, given, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, given, StringComparison.OrdinalIgnoreCase)))
            : null;

    private CancellationTokenSource Deadlined(CancellationToken cancellationToken)
    {
        var timeout = new CancellationTokenSource(Deadline, time);
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
    }

    /// <summary>
    /// Brings the issue's labels and the record to what this node wants, one design: reads the labels, learns who holds the lock, takes off a stale one, and puts this node's on
    /// or takes them off as <see cref="DesignRow.LockWanted"/> says. A failure is on the record and nothing throws for GitHub.
    /// </summary>
    public async Task SettleAsync(string id, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (store.Find(id) is not { IssueNumber: { } issue } design || Resolve(design.IssueRepo ?? design.Repo) is not { } info)
            {
                return;
            }
            using var deadline = Deadlined(cancellationToken);
            var token = deadline.Token;
            try
            {
                await SettleOneAsync(design, info, issue, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FlightException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var why = ex is OperationCanceledException ? DesignSyncText.NoAnswer : ex.Message;
                await Error.WriteLineAsync($"design: the lock labels of {design.Id} could not be set on {info.GitHub}#{issue.ToString(CultureInfo.InvariantCulture)}: {why}").ConfigureAwait(false);
                if (design.LockWanted == true || design.LockHeld == true)
                {
                    store.Update(design.Id, r => r with { LockError = DesignLockText.LabelFailed(why) });
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task SettleOneAsync(DesignRow design, RepoInfo info, int issue, CancellationToken token)
    {
        var labels = await github.IssueLabelsOrThrowAsync(info.Path, issue, token).ConfigureAwait(false);
        if (labels.Contains(PilotMarks.Hold, StringComparer.OrdinalIgnoreCase))
        {
            // The owner holds it: no node acts.
            return;
        }
        var mine = PilotMarks.NodeLabel(node);
        var editing = labels.Contains(PilotMarks.Editing, StringComparer.OrdinalIgnoreCase);
        var holders = labels.Select(PilotMarks.NodeOf).Where(n => n is { Length: > 0 }).Select(n => n!).ToList();
        var other = holders.FirstOrDefault(n => !string.Equals(n, node, StringComparison.OrdinalIgnoreCase));
        var wanted = design.LockWanted == true;

        if (editing && other is not null)
        {
            if (silence(other) is { } silent && silent > StaleAfter)
            {
                // A lock a dead node left must not hold the design for good.
                await github.RemoveIssueLabelAsync(info.Path, issue, PilotMarks.Editing, token).ConfigureAwait(false);
                foreach (var held in holders.Where(h => !string.Equals(h, node, StringComparison.OrdinalIgnoreCase)))
                {
                    await github.RemoveIssueLabelAsync(info.Path, issue, PilotMarks.NodeLabel(held), token).ConfigureAwait(false);
                }
                editing = false;
                other = null;
            }
            else
            {
                store.Update(design.Id, r => r with { LockedBy = other, LockWanted = false, LockHeld = false, LockError = null });
                return;
            }
        }
        if (editing && other is null && !holders.Any() && !wanted && design.LockHeld != true)
        {
            // The label by hand with no node label: a person's lock, shown as a lock by whoever it is, and no node's to take off.
            store.Update(design.Id, r => r with { LockedBy = "GitHub", LockWanted = false, LockHeld = false, LockError = null });
            return;
        }

        var ours = editing && holders.Contains(node, StringComparer.OrdinalIgnoreCase);
        if (wanted && !ours)
        {
            await github.EnsureLabelAsync(info.Path, PilotMarks.Editing, token).ConfigureAwait(false);
            await github.EnsureLabelAsync(info.Path, mine, token).ConfigureAwait(false);
            // The node label first: a call that fails between the two leaves the node label alone, which is harmless; 'editing' alone would read as a person's lock.
            await github.AddIssueLabelAsync(info.Path, issue, mine, token).ConfigureAwait(false);
            await github.AddIssueLabelAsync(info.Path, issue, PilotMarks.Editing, token).ConfigureAwait(false);
            // Two nodes may have taken it in the same moment: the node whose code sorts first keeps it, the other takes its label off again.
            var after = await github.IssueLabelsOrThrowAsync(info.Path, issue, token).ConfigureAwait(false);
            var rival = after.Select(PilotMarks.NodeOf).Where(n => n is { Length: > 0 } && !string.Equals(n, node, StringComparison.OrdinalIgnoreCase)).Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (rival is not null && string.Compare(rival, node, StringComparison.OrdinalIgnoreCase) < 0)
            {
                await github.RemoveIssueLabelAsync(info.Path, issue, mine, token).ConfigureAwait(false);
                store.Update(design.Id, r => r with { LockedBy = rival, LockWanted = false, LockHeld = false, LockError = null });
                return;
            }
            store.Update(design.Id, r => r with { LockedBy = null, LockHeld = true, LockError = null });
            return;
        }
        if (wanted)
        {
            store.Update(design.Id, r => r with { LockedBy = null, LockHeld = true, LockError = null });
            return;
        }
        if (ours || design.LockHeld == true || holders.Contains(node, StringComparer.OrdinalIgnoreCase))
        {
            // Not wanted any more (or a lock this node left behind before a restart): 'editing' off first, so a call that fails between the two leaves the node label alone (harmless), never 'editing' alone.
            if (editing)
            {
                await github.RemoveIssueLabelAsync(info.Path, issue, PilotMarks.Editing, token).ConfigureAwait(false);
            }
            if (holders.Contains(node, StringComparer.OrdinalIgnoreCase))
            {
                await github.RemoveIssueLabelAsync(info.Path, issue, mine, token).ConfigureAwait(false);
            }
        }
        if (design.LockHeld == true || design.LockedBy is not null || design.LockError is not null)
        {
            store.Update(design.Id, r => r with { LockedBy = null, LockHeld = false, LockError = null });
        }
    }

    /// <summary>
    /// Reads design <paramref name="id"/>'s issue body and compares its design block with the published version's. A difference in the prose parts is a typed change
    /// (a draft whose why is <see cref="DesignLockText.OwnerOnGitHub"/>) while nobody holds the lock, and is kept beside the design for the editor while this node does; a difference
    /// in a frame's link or a block is not applied, and the record says it was ignored and why. A block already handled is not read again.
    /// </summary>
    public async Task ReadBackAsync(string id, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (store.Find(id) is not { MadeOn: null, IssueNumber: { } issue, Current: { } current } design || design.SyncedVersion != current || Resolve(design.IssueRepo ?? design.Repo) is not { } info)
            {
                return;
            }
            if (design.LockedBy is { Length: > 0 } && design.LockHeld != true)
            {
                // Another node is the editor: what is typed on GitHub is its to keep.
                return;
            }
            using var deadline = Deadlined(cancellationToken);
            try
            {
                await ReadBackOneAsync(design, info, issue, current, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FlightException or DesignException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await Error.WriteLineAsync($"design: the text of {info.GitHub}#{issue.ToString(CultureInfo.InvariantCulture)} could not be read back: {(ex is OperationCanceledException ? DesignSyncText.NoAnswer : ex.Message)}").ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task ReadBackOneAsync(DesignRow design, RepoInfo info, int issue, int current, CancellationToken token)
    {
        var now = await github.IssueNowAsync(info.Path, issue, token).ConfigureAwait(false);
        if (now is null || now.Labels?.Contains(PilotMarks.Hold, StringComparer.OrdinalIgnoreCase) == true || DesignIssueReader.BlockOf(now.Body) is not { } block)
        {
            return;
        }
        var sha = DesignIssueReader.Sha256Of(block);
        if (sha == design.GitHubBlockSha256)
        {
            return;
        }
        var files = store.ReadVersion(design.Id, current);
        var published = files.TryGetValue(DesignStore.DocumentFile, out var text) ? DesignDocument.Parse(Encoding.UTF8.GetString(text)) : new DesignDocument(design.Title ?? design.Id, "", []);
        var why = design.History.FirstOrDefault(v => v.Number == current)?.Why ?? design.Why ?? $"v{current.ToString(CultureInfo.InvariantCulture)}";
        var repoFull = info.GitHub!;
        // After a land the block's frames link the merge commit's files, not the branch: expect the base the issue is at.
        var linkBase = DesignIssueDocument.Meta(now.Body) is { Commit.Length: > 0, Folder.Length: > 0 } meta ? DesignLinkBase.OfCommit(meta.Commit, meta.Folder) : null;
        var expected = DesignIssueDocument.Block(design.Id, published, current, why, node, repoFull, issue, [.. files.Keys.Where(k => k.StartsWith("renders/", StringComparison.Ordinal))], at: linkBase);
        if (sha == DesignIssueReader.Sha256Of(expected) || DesignIssueReader.Read(expected, published) is not { } was || DesignIssueReader.Read(block, published) is not { } found)
        {
            store.Update(design.Id, r => r with { GitHubBlockSha256 = sha, GitHubIgnored = [] });
            return;
        }
        var (prose, ignored) = DesignIssueReader.Compare(was, found);
        var at = time.GetUtcNow();
        var locked = design.LockHeld == true || design.LockWanted == true;
        var kept = design.GitHubEdits.ToList();
        List<string> typed = [];
        foreach (var difference in prose)
        {
            var edit = new DesignGitHubEdit { Part = difference.Part, Cell = difference.Cell, Text = difference.Text, At = at };
            if (locked)
            {
                // Kept for the editor, never applied: the newest text of a part replaces its older one.
                kept.RemoveAll(k => k.Key == edit.Key);
                kept.Add(edit);
                continue;
            }
            try
            {
                var applied = store.Edit(design.Id, edit.Part, edit.Cell, edit.Text);
                Typed?.Invoke(design.Id, edit.Part, edit.Cell, applied);
                typed.Add(edit.Where);
            }
            catch (DesignException ex)
            {
                ignored.Add($"The {edit.Where} was changed on GitHub and not applied: {ex.Message}.");
            }
        }
        store.Update(design.Id, r => r with
        {
            GitHubBlockSha256 = sha,
            GitHubIgnored = [.. ignored],
            GitHubEdits = [.. kept],
            DraftWhy = typed.Count > 0 ? OwnerOnGitHubWhy(typed.Select(t => t.Split(' ')[0]).Distinct(StringComparer.Ordinal)) : r.DraftWhy,
        });
        if (typed.Count > 0)
        {
            // A draft the owner made by typing on GitHub: this node now has the design open for editing.
            Want(design.Id);
        }
    }

    private static string OwnerOnGitHubWhy(IEnumerable<string> parts)
    {
        var why = DesignLockText.OwnerOnGitHub(string.Join(", ", parts));
        return why.Length <= DesignWhys.MaxLength ? why : why[..DesignWhys.MaxLength];
    }

    /// <summary>
    /// Applies the edit <paramref name="key"/> (<c>part:cell</c>) that waits beside design <paramref name="id"/> to its draft, as typed, and takes it off the list. The design is then locked
    /// by this node, as a typed edit makes it.
    /// </summary>
    /// <exception cref="DesignException">There is no such waiting edit, or the part cannot take the text.</exception>
    public string Apply(string id, string key)
    {
        var row = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        var edit = row.GitHubEdits.FirstOrDefault(e => e.Key == key) ?? throw new DesignException($"{row.Id} has no edit from GitHub waiting for '{key}'");
        var applied = store.Edit(row.Id, edit.Part, edit.Cell, edit.Text);
        Typed?.Invoke(row.Id, edit.Part, edit.Cell, applied);
        store.Update(row.Id, r => r with { GitHubEdits = [.. r.GitHubEdits.Where(e => e.Key != key)], DraftWhy = r.DraftWhy ?? OwnerOnGitHubWhy([edit.Part]) });
        Want(row.Id);
        return edit.Where;
    }

    /// <summary>Drops the edit <paramref name="key"/> that waits beside design <paramref name="id"/>; the draft is not changed.</summary>
    public string Dismiss(string id, string key)
    {
        var row = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        var edit = row.GitHubEdits.FirstOrDefault(e => e.Key == key) ?? throw new DesignException($"{row.Id} has no edit from GitHub waiting for '{key}'");
        store.Update(row.Id, r => r with { GitHubEdits = [.. r.GitHubEdits.Where(e => e.Key != key)] });
        return edit.Where;
    }
}
