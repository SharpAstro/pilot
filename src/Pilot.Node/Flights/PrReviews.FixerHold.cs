using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The For-you card of a fixer's work the node could not push to the PR (a refused push, or files never committed); the PR stays held.</summary>
    public const string FixerWorkHeldKind = "pr-fixer-work-held";

    /// <summary>
    /// The fixer's own session of this PR that is still running, or null: the row's job (or its name, for a row stored before FixerJob), and any other
    /// fixer session (as <see cref="AdmissionGate.IsFixer"/> reads it) for the PR's number in its repo. Read from the session store, not only from the row,
    /// since a row can read no fixer while one works (tianwen#2470, pilot #1291).
    /// </summary>
    private SessionRow? LiveFixerOf(PrReviewRow review)
    {
        var clone = review.ClonePath.TrimEnd('\\', '/');
        var folder = Path.GetFileName(clone);
        var name = review.Repo[(review.Repo.IndexOf('/') + 1)..];
        return state.Sessions().FirstOrDefault(s => !s.Archived && !s.Gone && s.State is not (SessionState.Ended or SessionState.Failed)
            && (review.FixerJob is { } job && string.Equals(s.Id, job, StringComparison.Ordinal)
                || AdmissionGate.IsFixer(s) && s.Pr == review.Number
                   && (string.Equals(s.Repo, folder, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Repo, name, StringComparison.OrdinalIgnoreCase)
                       || s.Cwd.StartsWith(clone + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                       || s.Cwd.StartsWith(clone + '/', StringComparison.OrdinalIgnoreCase))));
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
        else if (live is not null)
        {
            await StopQuietly(live.Id, cancellationToken);
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
            try
            {
                var push = await git.RunAsync(["push", "origin", $"HEAD:refs/heads/{read.HeadRef}"], worktree, cancellationToken);
                if (push.Ok)
                {
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
                    return Held($"its fixer's work in {worktree} was pushed ({work.Line}); the moved head gets a fresh review");
                }
                return Ring($"git could not push it: {push.Why}");
            }
            catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
            {
                return Ring($"git could not push it: {ex.Message}");
            }
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
}
