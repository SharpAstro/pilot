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
        return state.Sessions().FirstOrDefault(s => !s.Archived && s.State is not (SessionState.Ended or SessionState.Failed)
            && (review.FixerJob is { } job && string.Equals(s.Id, job, StringComparison.Ordinal)
                || AdmissionGate.IsFixer(s) && s.Pr == review.Number
                   && (string.Equals(s.Repo, folder, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Repo, name, StringComparison.OrdinalIgnoreCase)
                       || s.Cwd.StartsWith(clone + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                       || s.Cwd.StartsWith(clone + '/', StringComparison.OrdinalIgnoreCase))));
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
        if (review.Fixer is { } on || review.FixerJob is not null)
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
                            state.RaiseCard(CardBin.ForYou, PushedUnreviewedKind, subject, $"{pr}: the fixer's work was pushed, but its review could not start: {LegCommander.OneLine(ex.Message)}", ex.Message,
                                [new CardChoice { Label = "Review the fixer's push", Verb = Verbs.ReviewPr, Target = subject }], link: review.Url);
                        }
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
            RaiseOnce(pr, review, CardBin.ForYou, FixerWorkHeldKind, subject, $"{pr} is held: its fixer left work in {Path.GetFileName(worktree)}",
                $"{why}. {cause}.\nThe PR is not queued or merged until that work is pushed or removed.", summary: $"Push or remove the work in {worktree}: {LegCommander.OneLine(cause)}.");
            return Held(why);
        }
    }
}
