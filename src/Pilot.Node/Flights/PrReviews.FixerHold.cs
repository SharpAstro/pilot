using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The For-you card of a fixer's work the node could not push to the PR (a refused push, or files never committed); the PR stays held.</summary>
    public const string FixerWorkHeldKind = "pr-fixer-work-held";

    /// <summary>
    /// How long a fixer whose own job has ended (or whose outcome was told) is still read as waiting when it has not taken a turn since: the node
    /// tells it the job's end and it answers, so an end read in that window is not its own (pilot #1309 go-around). As LegWatch's idle grace.
    /// </summary>
    public TimeSpan FixerToldGrace { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A fixer session that reads Ended but owns a queued or running job (by id, session id, earlier id or name): waiting, not finished. So is one
    /// whose own job ended or was told (<see cref="OwnJobWait.Spoken"/>) after its last turn, within <see cref="FixerToldGrace"/> of that: the node
    /// has not had its answer to the job's end yet, and reading it as ended would spend its one resume on the gap.
    /// </summary>
    private bool FixerWaitsOnJob(SessionRow s)
    {
        // Idle reads as waiting too: a fixer that ended its turn on 'pilot run' stays running and idle until the job's end wakes it, so an idle one with an
        // open own job is left alone, and only one with none is finished (pilot #1327).
        if (s.State is not (SessionState.Ended or SessionState.Idle))
        {
            return false;
        }
        var wait = OwnJobWait.Of(state, s);
        return wait.Open
            || wait.Spoken is { } spoken && (s.LastResponseAt ?? s.StartedAt) < spoken && time.GetUtcNow() - spoken < FixerToldGrace;
    }

    /// <summary>
    /// A fixer session that has finished: Ended, or Idle (its turn over, still running) and not waiting on a job of its own. An idle Claude session never
    /// ends by itself, so waiting for it to end holds its round open until the PR merges (pilot #1327); the sweep ends it instead. An idle one that has
    /// not answered once yet has not begun (a session reads Idle between its start and its first turn), and one whose last turn the API stopped (a usage
    /// limit, an error: <see cref="SessionRow.ApiError"/>) is paused with its work half done and resumes when the limit lifts: neither has finished.
    /// </summary>
    private bool FixerFinished(SessionRow s) => s.State switch
    {
        SessionState.Ended => !FixerWaitsOnJob(s),
        SessionState.Idle => !IdleNotDone(s) && !FixerWaitsOnJob(s),
        _ => false,
    };

    /// <summary>An idle fixer that is not done: it has taken no first turn, or its last turn died on an API error or a usage-limit pause (pilot #1327).</summary>
    private static bool IdleNotDone(SessionRow s) => s.State == SessionState.Idle && (s.LastResponseAt is null || s.ApiError is { Length: > 0 });

    /// <summary>
    /// The fixer's own session of this PR that is still running, or null: the row's job (or its name, for a row stored before FixerJob), and any other
    /// fixer session (as <see cref="AdmissionGate.IsFixer"/> reads it) for the PR's number in its repo. Read from the session store, not only from the row,
    /// since a row can read no fixer while one works (tianwen#2470, pilot #1291).
    /// </summary>
    private SessionRow? LiveFixerOf(PrReviewRow review) => LiveFixersOf(review).FirstOrDefault();

    /// <summary>Every live fixer session of the PR (see <see cref="LiveFixerOf"/>): a second one beside the row's own is read, and stopped, with it.</summary>
    private List<SessionRow> LiveFixersOf(PrReviewRow review)
    {
        var clone = review.ClonePath.TrimEnd('\\', '/');
        var folder = Path.GetFileName(clone);
        var name = review.Repo[(review.Repo.IndexOf('/') + 1)..];
        // A fixer whose turn ended on a pilot run job of its own reads Ended but is waiting: the job's end wakes it (pilot #1309, tianwen #2474/#2476).
        return state.Sessions().Where(s => !s.Archived && !s.Gone && (s.State is not (SessionState.Ended or SessionState.Failed) || FixerWaitsOnJob(s))
            && (review.FixerJob is { } job && string.Equals(s.Id, job, StringComparison.Ordinal)
                || AdmissionGate.IsFixer(s) && s.Pr == review.Number
                   && (string.Equals(s.Repo, folder, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Repo, name, StringComparison.OrdinalIgnoreCase)
                       || s.Cwd.StartsWith(clone + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                       || s.Cwd.StartsWith(clone + '/', StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    /// <summary>
    /// The fix worktree of a PR the row does not name a fixer for: the row's FixWorktree, else the derived fix-slug-number path, else a live fixer
    /// session's Cwd when it is a worktree under the clone; null when none of them is a folder on disk.
    /// </summary>
    private string? FixWorktreeOf(PrReviewRow review, SessionRow? live)
    {
        if (review.FixWorktree is { } named && Directory.Exists(named))
        {
            return named;
        }
        var derived = Path.Combine(review.ClonePath, ".claude", "worktrees", $"fix-{Slug(review.Repo[(review.Repo.IndexOf('/') + 1)..])}-{review.Number}");
        if (Directory.Exists(derived))
        {
            return derived;
        }
        var under = Path.Combine(review.ClonePath, ".claude", "worktrees") + Path.DirectorySeparatorChar;
        return live is { Cwd.Length: > 0 } && live.Cwd.Replace('/', Path.DirectorySeparatorChar).StartsWith(under, StringComparison.OrdinalIgnoreCase) && Directory.Exists(live.Cwd) ? live.Cwd : null;
    }

    /// <summary>
    /// A PR that merged with no Fixer on its row, while a fixer of it is live or a fix worktree is on disk (tianwen#2470): the commits past the merged
    /// head carry on as a follow-up PR (<see cref="FollowUpAsync"/>), once per PR and worktree, and what is left is ended. GitHub is asked only for
    /// rows that have such a worktree or session.
    /// </summary>
    private async Task FollowUpMergedWithoutFixerAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        var live = LiveFixerOf(review);
        var worktree = FixWorktreeOf(review, live);
        if (worktree is null && live is null)
        {
            return;
        }
        PullRequest read;
        try
        {
            read = await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken);
        }
        catch (FlightException)
        {
            return;
        }
        if (!string.Equals(read.State, "MERGED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var pr = new PrRef(review.Repo[..review.Repo.IndexOf('/')], review.Repo[(review.Repo.IndexOf('/') + 1)..], review.Number);
        if (worktree is not null && _followedUp.TryAdd($"{review.Repo}#{review.Number}:{worktree}", 0))
        {
            await FollowUpAsync(pr, review, read, cancellationToken, worktree);
        }
        else
        {
            // Every live fixer, not only the first: the second one of the PR is stopped while it works too.
            foreach (var other in LiveFixersOf(review))
            {
                await StopQuietly(other.Id, cancellationToken);
            }
        }
        await EndFixerAsync(review with { FixWorktree = worktree, FixWorktreeBorrowed = null }, cancellationToken);
    }

    /// <summary>
    /// Why the node must not queue, merge or arm this PR now, or null (pilot #1291): its fixer is live, or its fix worktree holds work the PR's head
    /// does not have. Judged from the facts (the session store and the worktree on disk), not only from the row's Fixer. Commits past the PR's head
    /// with no live fixer are pushed to the PR's branch from that worktree (a plain push, never forced), and the moved head gets a fresh review; a
    /// refused push, or files never committed, is one For-you card, and the PR stays held.
    /// </summary>
    internal async Task<string?> FixerHoldAsync(PrReviewRow review, PullRequest read, CancellationToken cancellationToken)
    {
        var pr = new PrRef(review.Repo[..review.Repo.IndexOf('/')], review.Repo[(review.Repo.IndexOf('/') + 1)..], review.Number);
        if (LiveFixerOf(review) is { } live)
        {
            return Held($"its fixer {live.Name} is still working on it");
        }
        if (review.Fixer is not null || review.FixerJob is not null)
        {
            return Held($"{review.Fixer ?? review.FixerJob} is its fixer, and its round is not settled yet");
        }
        var worktree = review.FixWorktree ?? Path.Combine(review.ClonePath, ".claude", "worktrees", $"fix-{Slug(pr.Name)}-{pr.Number}");
        if (review.FixWorktreeBorrowed == true || await WorkPastAsync(worktree, read.Head, cancellationToken) is not { } work)
        {
            return null;
        }
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        if (work.Commits > 0 && read.HeadRef.Length > 0 && !read.Fork)
        {
            var cause = await PushWorktreeAndReviewAsync(pr, review, read, worktree, work, cancellationToken);
            return cause is null
                ? Held($"its fixer's work in {worktree} was pushed ({work.Line}); the moved head gets a fresh review")
                : Ring(cause);
        }
        return Ring(work.Commits > 0 ? "its branch is on a fork, so the node cannot push it" : "it is not committed");

        string Held(string why)
        {
            try
            {
                NodeApp.NodeLog(options).Write($"pr-review: {review.Repo}#{review.Number} is not queued or merged: {why}");
            }
            catch (IOException)
            {
            }
            return why;
        }

        string Ring(string cause)
        {
            var why = $"the fix worktree {worktree} holds work the PR does not have: {work.Line}";
            RaiseOnce(pr, review, CardBin.ForYou, FixerWorkHeldKind, subject, $"{pr} is held: its fixer left work not pushed",
                $"{why}. {cause}.\nThe PR is not queued or merged until that work is pushed or removed.", [new CardChoice { Label = "Review the PR again", Verb = Verbs.ReviewPr, Target = subject }], summary: $"Push or remove the work in {worktree}: {LegCommander.OneLine(cause)}.");
            return Held(why);
        }
    }

    /// <summary>
    /// Pushes the HEAD of a fix worktree to the PR's own branch, plainly (never forced), then starts the review of the moved head; when that review
    /// cannot start, or GitHub still reads the old head, a For-you card offers it. Returns null once the push landed, else why git could not push.
    /// One block for the hold path (<see cref="FixerHoldAsync"/>) and the person's push-fix choice (<see cref="PushFixAsync"/>).
    /// </summary>
    private async Task<string?> PushWorktreeAndReviewAsync(PrRef pr, PrReviewRow review, PullRequest read, string worktree, WorkPast work, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        try
        {
            var push = await git.RunAsync(["push", "origin", $"HEAD:refs/heads/{read.HeadRef}"], worktree, cancellationToken);
            if (!push.Ok)
            {
                return $"git could not push it: {push.Why}";
            }
            var now = await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken);
            if (now.Head.Length > 0 && !string.Equals(now.Head, review.Head, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await StartAsync(pr, cancellationToken);
                }
                catch (FlightException ex)
                {
                    state.RaiseCard(CardBin.ForYou, PushedUnreviewedKind, subject, $"{pr}: the fixer's push has no review yet", ex.Message,
                        [new CardChoice { Label = "Review the fixer's push", Verb = Verbs.ReviewPr, Target = subject }], link: review.Url);
                }
            }
            else
            {
                // GitHub still read the old head right after the push: no review can start from that read, and the next sweep would note
                // the newer head and leave the row, so nothing would review the push. The person's card starts it.
                RaiseOnce(pr, review, CardBin.ForYou, PushedUnreviewedKind, subject, $"{pr}: the fixer's push has no review yet",
                    $"The node pushed the fixer's work from {worktree} ({work.Line}), but GitHub still read the PR's head as {CommitRefs.Short(now.Head.Length > 0 ? now.Head : read.Head)}, so no review started on the push.",
                    [new CardChoice { Label = "Review the fixer's push", Verb = Verbs.ReviewPr, Target = subject }],
                    summary: $"Review {pr} again: the node pushed its fixer's work, and GitHub had not shown the new head yet.");
            }
            return null;
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
        {
            return $"git could not push it: {ex.Message}";
        }
    }

    /// <summary>The label of the ended card's choice that pushes the kept worktree's commits.</summary>
    public const string PushFixLabel = "Push its commits";

    /// <summary>
    /// push-fix: a plain push of the commits a fixer left in its kept fix worktree to the PR's branch, then a review of the moved head. Refused while a
    /// fixer is live, for a PR that is not open or is from a fork, with no commits past the PR's head, or when the PR's head is no ancestor of the
    /// worktree's HEAD (the head moved: the work stays where it is). The worktree is never removed here.
    /// </summary>
    private async Task<string> PushFixAsync(PrRef pr, CancellationToken cancellationToken)
    {
        var review = Of(pr) ?? throw new FlightException($"{pr} has no review on this node");
        if (LiveFixerOf(review) is { } live)
        {
            throw new FlightException($"{pr}'s fixer {live.Name} is still working on it; its commits are pushed once it ends");
        }
        var read = await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken);
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr} is {read.State.ToLowerInvariant()}, not open: nothing is pushed to it");
        }
        if (read.Fork || read.HeadRef.Length == 0)
        {
            throw new FlightException($"{pr}'s branch is on a fork, so the node cannot push to it");
        }
        var worktree = FixWorktreeOf(review, null) ?? throw new FlightException($"{pr} has no kept fix worktree on disk to push from");
        var work = await WorkPastAsync(worktree, read.Head, cancellationToken);
        if (work is not { Commits: > 0 })
        {
            throw new FlightException($"{worktree} has no commits past {pr}'s head {CommitRefs.Short(read.Head)} to push");
        }
        var ancestor = await git.RunAsync(["merge-base", "--is-ancestor", read.Head, "HEAD"], worktree, cancellationToken);
        if (!ancestor.Ok)
        {
            throw new FlightException($"{pr}'s head {CommitRefs.Short(read.Head)} is not an ancestor of the HEAD in {worktree}: the head moved, so a plain push would be refused. The work stays in {worktree}; Fix it continues there");
        }
        var cause = await PushWorktreeAndReviewAsync(pr, review, read, worktree, work, cancellationToken);
        return cause is null ? $"pushed {work.Line} from {worktree}; the moved head gets a review" : throw new FlightException(cause);
    }
}
