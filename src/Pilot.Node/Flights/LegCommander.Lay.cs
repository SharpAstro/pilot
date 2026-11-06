using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What <see cref="LegCommander.ProbeRebaseAsync"/> found: whether the branch rebases onto its base, and why not.</summary>
/// <param name="Clean">True when every commit of the branch replays onto the base.</param>
/// <param name="Files">The files left unmerged where the rebase stopped; empty when clean or when the probe could not run.</param>
/// <param name="Error">Why the probe could not run (a worktree that would not add, a base that is not there); null when it ran.</param>
public sealed record RebaseProbe(bool Clean, IReadOnlyList<string> Files, string? Error = null);

public sealed partial class LegCommander
{
    /// <summary>The kind of card that says a PR could not be laid on its base.</summary>
    public const string LayFailedKind = "lay-failed";

    /// <summary>The kind of card that says a PR was laid on its base as one commit.</summary>
    public const string LaidKind = "laid-on-base";

    /// <summary>
    /// Lays the plane's branch on <c>origin/&lt;base&gt;</c> as one commit with the tree of the clean merge of the two, keeps the old head
    /// as <c>refs/pilot/laid/&lt;code&gt;</c>, moves the plane's branch to the commit and pushes it. <b>This is the one push of the node
    /// that replaces a pushed branch</b> (<c>--force-with-lease=&lt;branch&gt;:&lt;old head&gt;</c>, naming the head read, so a branch
    /// pushed from elsewhere meanwhile is never overwritten); <see cref="PushAsync"/> keeps its plain-push rule. The plane must be clean.
    /// Asks git only, so it runs with the lock let go. Returns the new head and the old one.
    /// </summary>
    private async Task<(string Laid, string Old)> LayOnBaseAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var plane = flight.Worktree;
        var baseRef = $"origin/{BaseBranch(flight)}";
        if ((await git.RunAsync(["status", "--porcelain"], plane, cancellationToken)).Output.Trim().Length > 0)
        {
            throw new FlightException("its plane holds changes not committed");
        }
        var old = (await git.RunAsync(["rev-parse", "HEAD"], plane, cancellationToken)).Output.Trim();
        var merged = await git.RunAsync(["merge-tree", "--write-tree", baseRef, "HEAD"], plane, cancellationToken);
        if (!merged.Ok)
        {
            throw new FlightException($"its merge with {baseRef} is not clean ({merged.Why})");
        }
        var tree = merged.Output.Trim().Split('\n')[0].Trim();
        var message = $"{PrTitle(flight, "")}\n\nLaid on {baseRef} as one commit ({flight.Code}): the branch merges cleanly but does not rebase.";
        var made = await git.RunAsync(["commit-tree", tree, "-p", baseRef, "-m", message], plane, cancellationToken);
        if (!made.Ok || made.Output.Trim() is not { Length: > 0 } laid)
        {
            throw new FlightException($"the commit could not be made ({made.Why})");
        }
        var got = (await git.RunAsync(["rev-parse", $"{laid}^{{tree}}"], plane, cancellationToken)).Output.Trim();
        if (!string.Equals(got, tree, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"the laid commit's tree {got} is not the merge's {tree}");
        }
        var kept = await git.RunAsync(["update-ref", $"refs/pilot/laid/{flight.Code}", old], plane, cancellationToken);
        if (!kept.Ok)
        {
            throw new FlightException($"the old head could not be kept ({kept.Why})");
        }
        var moved = await git.RunAsync(["reset", "--hard", "--quiet", laid], plane, cancellationToken);
        if (!moved.Ok)
        {
            throw new FlightException($"the plane's branch could not be moved ({moved.Why})");
        }
        await LeasePushAsync(plane, flight.Branch, old, cancellationToken);
        return (laid, old);
    }

    /// <summary>
    /// Whether the flight's files at <paramref name="reviewed"/> are those of <paramref name="laid"/> exactly: the same set of files
    /// changed against <paramref name="baseRef"/>, with the same content. What a review read is then what the laid commit holds.
    /// </summary>
    private async Task<bool> SameFlightFilesAsync(string plane, string baseRef, string reviewed, string laid, CancellationToken cancellationToken)
    {
        async Task<List<string>?> FilesAsync(string head)
        {
            var bas = await git.RunAsync(["merge-base", baseRef, head], plane, cancellationToken);
            if (!bas.Ok)
            {
                return null;
            }
            var names = await git.RunAsync(["diff", "--name-only", bas.Output.Trim(), head], plane, cancellationToken);
            return names.Ok ? [.. names.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)] : null;
        }
        if (!(await git.RunAsync(["cat-file", "-e", $"{reviewed}^{{commit}}"], plane, cancellationToken)).Ok)
        {
            return false;
        }
        if (await FilesAsync(reviewed) is not { Count: > 0 } theirs || await FilesAsync(laid) is not { } ours || !theirs.SequenceEqual(ours))
        {
            return false;
        }
        return (await git.RunAsync(["diff", "--quiet", reviewed, laid, "--", .. theirs], plane, cancellationToken)).Ok;
    }

    private int _probes;

    /// <summary>How many rebase probes have run (for tests that show a queue is never probed).</summary>
    public int RebaseProbes => Volatile.Read(ref _probes);

    // When the node laid a head, by "code head": a removal of that head older than this is the old head's drop, not GitHub's verdict on the laid commit.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _laidAt = new(StringComparer.Ordinal);

    // Heads GitHub's queue dropped for a conflict, by "code head": none of them is queued again until the head moves. Kept in memory
    // only (with _dropRung): after a node restart a dropped head is met again and rung once more, which is intended.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _droppedHeads = new(StringComparer.Ordinal);

    // The heads a drop was rung for: one ring per flight and head.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _dropRung = new(StringComparer.Ordinal);

    /// <summary>The kind of card that says a PR the queue dropped for a conflict has no cause the node can act on.</summary>
    public const string DropUnexplainedKind = "drop-unexplained";

    /// <summary>Whether GitHub's merge queue dropped <paramref name="head"/> of the flight for a conflict: it is not queued again until the head moves.</summary>
    public bool DroppedForConflict(string code, string head) => _droppedHeads.ContainsKey($"{code} {head}");

    /// <summary>
    /// A PR the merge queue dropped for a conflict, as the node first meets it: the head is marked so no path (the autopilot's merge, a
    /// Merge card, the entry kept by a refused merge) queues it again unchanged. When the base brings nothing new (it is merged in
    /// already, so a base merge would leave the head as it was) the cause is looked for: a PR whose merge is clean but whose rebase
    /// is not is laid on its base and queued once; otherwise one Ring card says why and the head is not queued. Returns false when the
    /// base has something to merge in, which the caller's update then does (it moves the head).
    /// </summary>
    public async Task<bool> HandleConflictDropAsync(string code, string head, DateTimeOffset removedAt, CancellationToken cancellationToken)
    {
        if (_laidAt.TryGetValue($"{code} {head}", out var laidAt) && removedAt <= laidAt)
        {
            // The removal is the old head's: the node laid this one itself and it is not a GitHub drop.
            return true;
        }
        _droppedHeads[$"{code} {head}"] = true;
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying } first || !Directory.Exists(first.Worktree))
        {
            return false;
        }
        var baseRef = $"origin/{BaseBranch(first)}";
        await git.RunAsync(["fetch", "--quiet", "origin"], first.Worktree, cancellationToken);
        var plane = (await git.RunAsync(["rev-parse", "HEAD"], first.Worktree, cancellationToken)).Output.Trim();
        if (!string.Equals(plane, head, StringComparison.OrdinalIgnoreCase)
            || !(await git.RunAsync(["merge-base", "--is-ancestor", baseRef, "HEAD"], first.Worktree, cancellationToken)).Ok)
        {
            return false;
        }
        FlightRow flight;
        string laid;
        // The lay lets the lock go for its git and GitHub asks, so it is made holding it; the merge after it takes the lock itself.
        var call = await EnterAsync(code, "being laid on its base", cancellationToken);
        try
        {
            // Read again: the flight may have moved while another command held the lock.
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying } now || Moved(first) is not null)
            {
                return true;
            }
            flight = now;
            laid = await ShapeForMergeAsync(flight, head, Models.MergeMethod, cancellationToken);
        }
        catch (FlightException)
        {
            // The lay rang for itself (a Ring card with why); the same head is not asked again.
            return true;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
        if (!string.Equals(laid, head, StringComparison.OrdinalIgnoreCase))
        {
            // The laid head is new, so the queue is joined once there.
            // Asked of GitHub directly, at the head just pushed: its PR may still show the old one for a moment, which a merge command would refuse.
            if (flight.PullRequest is { } queued)
            {
                var outcome = await LandPrAsync(github, flight.RepoPath, queued, laid, Models.MergeMethod, cancellationToken);
                if (outcome.Kind is LandKind.RetryLater)
                {
                    // GitHub did not answer: the node waits on the laid head and asks again at its next look, with no card.
                    Record(code, f => f with { MergeWhenGreen = [laid, Models.MergeMethod] });
                }
                else if (outcome.Kind is LandKind.Refused)
                {
                    // Refused (its checks have not run on the new head): a Merge card at the laid head stays, so the merge goes through
                    // once they pass. The laid head is the node's own, never marked as a GitHub drop.
                    RaiseReadyAtLaid(flight, laid, outcome.Said ?? "");
                    // Where the repo's queue needs the checks first (auto-merge off, enqueue refused until they report), the node waits for
                    // them on the laid head and joins the queue then, as for any armed merge.
                    Record(code, f => f with { MergeWhenGreen = [laid, Models.MergeMethod] });
                    if (_dropRung.TryAdd($"{code} {laid}", true))
                    {
                        state.RaiseCard(CardBin.Handled, DropUnexplainedKind, Verbs.FlightTarget(code), $"{code}'s laid PR could not be queued yet", outcome.Said);
                    }
                }
            }
            return true;
        }
        if (_dropRung.TryAdd($"{code} {head}", true))
        {
            string reason;
            try
            {
                var summary = flight.PullRequest is { } number ? await github.ChecksSummaryAsync(flight.RepoPath, number, cancellationToken) : null;
                reason = summary is { Status: ChoiceStatus.Failing } ? "its checks fail"
                    : $"its merge with {flight.Base} is clean and it holds {flight.Base} already, so the node found no merge or rebase conflict to resolve";
            }
            catch (FlightException ex)
            {
                reason = $"no cause found ({ex.Message})";
            }
            state.RaiseCard(CardBin.Ring, DropUnexplainedKind, Verbs.FlightTarget(code), $"{code}'s PR was dropped from the merge queue for a conflict",
                $"GitHub's queue dropped {CommitRefs.Short(head)} for a conflict: {reason}. It is not queued again at this head; push a changed head, or look at the PR page.");
        }
        return true;
    }

    /// <summary>A PR-ready card at the laid head, for when the queue would not take it yet: its Merge is offered there.</summary>
    private void RaiseReadyAtLaid(FlightRow flight, string laid, string why) =>
        state.RaiseCard(MergeCardBin(), PrReady, Verbs.FlightTarget(flight.Code),
            $"{flight.Code}'s PR #{flight.PullRequest} is ready to merge",
            $"{flight.Title ?? flight.Item.Noun}: laid on {flight.Base} as {CommitRefs.Short(laid)}, its review carried over. The queue would not take it yet ({why}); Merge queues it once its checks pass.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [laid] }], link: flight.PullRequestUrl,
            summary: $"{flight.Code} was laid on its base and is reviewed; its PR is ready. Merge lands it.");

    /// <summary>
    /// Whether the branch of the plane at <paramref name="plane"/> rebases onto <c>origin/&lt;baseBranch&gt;</c>, as a merge queue that
    /// rebases would do. A PR can merge cleanly (merge-tree is clean) and still not rebase: a keep-ours merge in its history, with later
    /// commits touching the same lines, stops a replay one commit at a time. The test runs in a detached throwaway worktree in a
    /// temp folder at the plane's HEAD, which is aborted and removed whatever happens; the plane and its branch are never touched.
    /// </summary>
    public async Task<RebaseProbe> ProbeRebaseAsync(string plane, string baseBranch, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _probes);
        var folder = Path.Combine(Path.GetTempPath(), "pilot-rebase-probe-" + Guid.NewGuid().ToString("N")[..12]);
        var added = false;
        try
        {
            var add = await git.RunAsync(["worktree", "add", "--detach", folder, "HEAD"], plane, cancellationToken);
            if (!add.Ok)
            {
                return new RebaseProbe(false, [], $"the rebase test worktree could not be added ({add.Why})");
            }
            added = true;
            var rebase = await git.RunAsync(["rebase", $"origin/{baseBranch}"], folder, cancellationToken);
            if (rebase.Ok)
            {
                return new RebaseProbe(true, []);
            }
            var unmerged = await git.RunAsync(["diff", "--name-only", "--diff-filter=U"], folder, cancellationToken);
            var files = unmerged.Ok
                ? unmerged.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            await git.RunAsync(["rebase", "--abort"], folder, cancellationToken);
            return files.Length == 0
                ? new RebaseProbe(false, [], $"the rebase onto origin/{baseBranch} stopped ({rebase.Why})")
                : new RebaseProbe(false, files);
        }
        finally
        {
            if (added)
            {
                await git.RunAsync(["rebase", "--abort"], folder, CancellationToken.None);
                await git.RunAsync(["worktree", "remove", "--force", folder], plane, CancellationToken.None);
            }
            await git.RunAsync(["worktree", "prune"], plane, CancellationToken.None);
            if (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held: git has forgotten it already, and the temp folder is swept by the OS.
                }
            }
        }
    }
}
