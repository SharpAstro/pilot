using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The review of <paramref name="pr"/>, the latest one, or null.</summary>
    public PrReviewRow? Of(PrRef pr) =>
        state.PrReviews().FirstOrDefault(r => string.Equals(r.Repo, pr.Repo, StringComparison.OrdinalIgnoreCase) && r.Number == pr.Number);

    /// <summary>Starts a fresh review of <paramref name="pr"/>; says what it started, as <c>started &lt;reviewer&gt; at &lt;head8&gt;</c>.</summary>
    private async Task<string> StartAsync(PrRef pr, CancellationToken cancellationToken)
    {
        var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr} is {read.State.ToLowerInvariant()}, not open");
        }
        if (read.Head.Length == 0 || read.Base.Length == 0)
        {
            throw new FlightException($"gh did not say {pr}'s head and base");
        }
        if (FlightOwner(pr, read.HeadRef, read.Fork) is { } owner)
        {
            throw new FlightException($"{pr} is {owner.Code}'s PR, which has one review, the flight's: pilot flight review {owner.Code}");
        }

        // Admission: a review waits for a free Opus slot, and the sweep starts it; an earlier review's own reviewer does not count against it.
        if (WaitsForSlot(pr, fix: false, Models.Model(RoleModels.Reviewer), Of(pr)?.Reviewer))
        {
            return $"queued: {pr} waits for a free Opus slot";
        }

        // One review of a PR at a time: an earlier one, still on or waiting on its card, ends first.
        if (Of(pr) is { State: not PrReviewState.Closed } earlier)
        {
            await EndAsync(earlier, "a fresh review started", cancellationToken);
        }

        // The fix of an earlier review goes on past a fresh one: its session and worktree are the next round's to end.
        var fixOn = Of(pr);
        var clone = await CloneOfAsync(pr, cancellationToken);
        var fetch = await git.RunAsync(["fetch", "--quiet", "origin", $"pull/{pr.Number}/head"], clone, cancellationToken);
        if (!fetch.Ok)
        {
            throw new FlightException($"git could not fetch {pr}'s head into {clone}: {fetch.Why}");
        }
        // The base too, so the diff is against what the PR goes into now; a base that will not fetch leaves the last one.
        await git.RunAsync(["fetch", "--quiet", "origin", read.Base], clone, cancellationToken);
        if (!(await git.RunAsync(["cat-file", "-e", $"{read.Head}^{{commit}}"], clone, cancellationToken)).Ok)
        {
            throw new FlightException($"{pr}'s head {CommitRefs.Short(read.Head)} was not fetched: it moved while it was; review it again");
        }

        var name = Slug(pr.Name);
        var plane = $"review-{name}-{pr.Number}";
        var worktree = Path.Combine(clone, ".claude", "worktrees", plane);
        // The folder actually used: a sibling when the usual one is held and queued for removal.
        worktree = await planes.PinAsync(clone, worktree, read.Head, cancellationToken);
        plane = Path.GetFileName(worktree);

        var reviewer = $"{options.Machine.ToLowerInvariant()}-review-{name}-{pr.Number}";
        var row = new PrReviewRow
        {
            Repo = pr.Repo,
            Number = pr.Number,
            Title = read.Title.Length > 0 ? read.Title : null,
            Author = read.Author.Length > 0 ? read.Author : null,
            Url = read.Url,
            Base = read.Base,
            Head = read.Head,
            ClonePath = clone,
            Worktree = worktree,
            Reviewer = reviewer,
            State = PrReviewState.Reviewing,
            Fixer = fixOn?.Fixer,
            FixerJob = fixOn?.FixerJob,
            FixWorktree = fixOn?.FixWorktree,
            FixRound = fixOn?.FixRound,
            FixWorktreeBorrowed = fixOn?.FixWorktreeBorrowed,
            FixDetached = fixOn?.FixDetached,
            StartedAt = time.GetUtcNow(),
        };
        var commits = await git.RunAsync(["log", "--oneline", $"origin/{read.Base}..HEAD"], worktree, cancellationToken);
        var stat = await git.RunAsync(["diff", "--stat", $"origin/{read.Base}...HEAD"], worktree, cancellationToken);
        var brief = Briefs.ReviewPr(row, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""),
            await github.LoginAsync(cancellationToken), Briefs.Run(Cli, Socket), options.Machine);
        string job;
        var counting = new object();
        Slots.Begin("review-pr:" + reviewer, counting, reviewer, null, AdmissionRole.Reviewer, Models.Model(RoleModels.Reviewer));
        try
        {
            job = await workers.StartAsync(new WorkerStart(reviewer, clone, plane, brief, Models.Model(RoleModels.Reviewer), Models.Effort(RoleModels.Reviewer), Reason: SessionStartReasons.PrReview, Pr: pr.Number), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cut off before its row was written: nothing says a review is on, so no worktree stays and no status was set.
            await planes.RemoveAsync(clone, worktree, CancellationToken.None, force: true);
            throw;
        }
        catch (FlightException ex)
        {
            // A reviewer that did not start leaves no worktree behind for nobody to read.
            await planes.RemoveAsync(clone, worktree, cancellationToken, force: true);
            throw new FlightException(ex.Message.Contains("trust", StringComparison.OrdinalIgnoreCase) && IsCached(clone)
                ? $"{ex.Message}. Claude Code starts no background session in a folder never trusted: run claude in {CloneCache} once and accept its prompt, which covers every clone the node makes there, then review it again"
                : ex.Message);
        }
        finally
        {
            // From here the row (put below) counts it.
            Slots.End("review-pr:" + reviewer, counting);
        }
        state.PutPrReview(row with { ReviewerJob = job });
        // The earlier review's cards are its own: a newer review of the PR overtakes them, whatever state that review ended in. Its design
        // card is the exception: that question is settled or replaced by what the newer review says of it once it is posted (FixAfterPostAsync),
        // so it keeps ringing until then, not closed here as if nothing were known.
        state.CloseCardsAbout(Verbs.PrTarget(pr.Repo, pr.Number), $"a newer review started at {CommitRefs.Short(read.Head)}", [.. ReviewCardKinds.Where(k => k != DesignKind), PushedUnreviewedKind]);
        state.RaiseCard(CardBin.Handled, StartedKind, Verbs.PrTarget(pr.Repo, pr.Number),
            $"Started {reviewer} on {pr} at {CommitRefs.Short(read.Head)}", $"in {worktree}");
        await SetStatusAsync(row, "pending", "reviewing", read.Url, cancellationToken);
        if (read.Draft)
        {
            await SetDraftAsync(row, draft: false, cancellationToken);
        }
        return $"started {reviewer} at {CommitRefs.Short(read.Head)}";
    }

    /// <summary>
    /// The clone <paramref name="pr"/>'s worktree is made in: the repo's clone under the repo roots, by its origin; else the
    /// node's own in <see cref="CloneCache"/>, made the first time and fetched into after.
    /// </summary>
    private async Task<string> CloneOfAsync(PrRef pr, CancellationToken cancellationToken)
    {
        if (repos.Repos().FirstOrDefault(r => string.Equals(r.GitHub, pr.Repo, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            return found.Path;
        }
        var cached = Path.Combine(CloneCache, pr.Owner, pr.Name);
        if (Directory.Exists(Path.Combine(cached, ".git")))
        {
            return cached;
        }
        if (Directory.Exists(cached))
        {
            // A clone cut short (the node stopped mid-clone) is no clone: it is made again.
            if (RemovalQueue.TryDelete(cached, out var why))
            {
                // Cleared and reused: its queue entry must go, or the next start deletes the fresh clone.
                planes.Queue?.TryOne(cached);
            }
            else
            {
                if (planes.Queue is not { } queue)
                {
                    throw new FlightException($"{cached} holds a clone cut short that could not be removed: {why}");
                }
                // Held: left for the next start, and the clone is made beside it.
                queue.Add(cached, RemovalQueue.CacheKind, pr.Repo, why);
                for (var n = 2; ; n++)
                {
                    cached = Path.Combine(CloneCache, pr.Owner, $"{pr.Name}-{n}");
                    if (Directory.Exists(Path.Combine(cached, ".git")) || RemovalQueue.TryDelete(cached, out _))
                    {
                        break;
                    }
                }
                if (Directory.Exists(Path.Combine(cached, ".git")))
                {
                    return cached;
                }
            }
        }
        await github.CloneAsync(pr.Repo, cached, cancellationToken);
        return cached;
    }

    private bool IsCached(string clone) =>
        Path.GetFullPath(clone).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(CloneCache)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>A repo's name as a worktree and a session name take it: lower case, anything but letters and digits a dash.</summary>
    private static string Slug(string name) =>
        new([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
}
