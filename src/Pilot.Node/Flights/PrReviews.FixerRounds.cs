using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The Handled line a fixer's start leaves.</summary>
    public const string FixStartedKind = "pr-fix-started";

    /// <summary>The For-you card of a changes verdict on a PR that is not the controller's own, with Fix it.</summary>
    public const string FixOfferKind = "pr-fix-offer";

    /// <summary>The ring of a changes verdict with a design question in it: the author decides, no fixer starts.</summary>
    public const string DesignKind = "pr-review-design";

    /// <summary>The For-you card said when the fixer could not start.</summary>
    public const string FixFailedKind = "pr-fix-failed";

    /// <summary>
    /// The For-you card of a fixer that pushed when the review of the pushed head could not start. It is about a head newer than the
    /// review row's, so it is outside <see cref="ReviewCardKinds"/>, which a sweep closes once the head is past the review's own;
    /// a newer review of the PR closes it, and so does the sweep that reads the PR as merged or closed (it is a PR-acting kind).
    /// </summary>
    public const string PushedUnreviewedKind = "pr-fix-pushed-unreviewed";

    /// <summary>The card's choice that starts the fixer.</summary>
    public const string FixLabel = "Fix it";

    /// <summary>The ring of a changes verdict after the last round of fixes: no further fixer starts.</summary>
    public const string FixCappedKind = "pr-fix-capped";

    /// <summary>The For-you card of a fixer whose session ended with the PR's head where it was, with Fix it to try again.</summary>
    public const string FixEndedKind = "pr-fix-ended";

    /// <summary>The words of a <see cref="FixEndedKind"/> card whose fixer left work in its kept worktree; the node does not press Fix it on one (#1269).</summary>
    public const string FixLeftWorkMark = "but the fixer left work in";

    /// <summary>The For-you card said when a fixer's worktree could not be removed; the node tries again.</summary>
    public const string FixLeftKind = "pr-fix-left";

    /// <summary>The Handled notice of an idle fixer of an approved PR that finished with nothing left and was ended (pilot #1327).</summary>
    public const string FixerFinishedKind = "pr-fixer-finished";

    /// <summary>The Handled notice of a merged PR's fixer commits that are already on the base, or on the follow-up branch, so no PR was opened (pilot #1327).</summary>
    public const string FixFollowUpSettledKind = "pr-fix-follow-up-settled";

    /// <summary>How many rounds of fixes a PR gets before the person is rung, the same cap as a flight's review go-arounds (#249).</summary>
    public const int MaxFixRounds = LegCommander.MaxReviewGoArounds;

    /// <summary>
    /// Looks at a PR a fixer is on (#290): merged or closed, the fixer ends; a head past the reviewed one means it pushed, so it
    /// ends and one fresh review starts at the new head, carrying the round; a session that ended with the head unmoved is a card
    /// with Fix it to try again.
    /// </summary>
    private async Task TendFixerAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        var pr = PrRef.Parse($"{review.Repo}#{review.Number}")!;
        PullRequest read;
        try
        {
            read = await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken);
        }
        catch (FlightException)
        {
            return;
        }
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            // Once per fixer and worktree: a worktree that cannot be removed is tried again, the follow-up is not.
            if (string.Equals(read.State, "MERGED", StringComparison.OrdinalIgnoreCase) && review.FixWorktree is not null
                && _followedUp.TryAdd($"{review.Repo}#{review.Number}:{review.FixWorktree}", 0))
            {
                await FollowUpAsync(pr, review, read, cancellationToken);
            }
            await EndFixerAsync(review, cancellationToken);
            return;
        }
        if (read.Head.Length > 0 && !string.Equals(read.Head, review.Head, StringComparison.OrdinalIgnoreCase))
        {
            // A fixer started on a passing review has had its go at the points its brief listed (ReviewFollowUp.ToMake, the same list): they are
            // acted on, so a point it answered deferred does not start another fixer. A changes-verdict fixer's brief lists none, so none is recorded.
            // The fixer's session working, waiting on a person or on a job of its own: it may push more, so it is left running and no review starts yet; a
            // later sweep looks again once it has finished. An idle one has finished (its turn is over and an idle session never ends by itself), so it is ended
            // below and the review of its push starts (pilot #1327).
            var lives = LiveFixersOf(review);
            if (lives.Any(l => !FixerFinished(l)))
            {
                return;
            }
            var live = lives.FirstOrDefault();
            // An idle fixer that left work past the PR's head is nudged once; a second look ends it with its worktree kept, and the hold pushes or rings for it.
            if (live is { State: SessionState.Idle } && review.FixerJob is { } idleJob && string.Equals(live.Id, idleJob, StringComparison.Ordinal)
                && review.FixWorktree is { } idleTree && review.FixWorktreeBorrowed != true
                && !string.Equals(review.FixerResumedJob, idleJob, StringComparison.Ordinal) && ResumeFixer is { } nudge
                && await WorkPastAsync(idleTree, read.Head, cancellationToken) is { } idleWork)
            {
                if (Of(pr) is { } latest)
                {
                    state.PutPrReview(latest with { FixerResumedJob = idleJob });
                }
                string? refused;
                try
                {
                    refused = await nudge(idleJob, ResumeText(review, idleWork.Line), cancellationToken);
                }
                catch (FlightException ex)
                {
                    refused = ex.Message;
                }
                if (refused is null)
                {
                    return;
                }
                Error.WriteLine($"pr reviews: {pr}'s idle fixer could not be nudged: {refused}");
            }
            var tried = string.Equals(review.Verdict, "changes", StringComparison.OrdinalIgnoreCase) ? []
                : ReviewFollowUp.ToMake(review).Select(i => i.Key).ToArray();
            if (tried.Length > 0)
            {
                state.PutPrReview(review with { FollowUpPoints = [.. review.FollowUpPoints ?? [], .. tried] });
            }
            if (!await EndFixerAsync(review, cancellationToken, read.Head))
            {
                return;
            }
            try
            {
                await StartAsync(pr, cancellationToken);
            }
            catch (FlightException ex)
            {
                state.RaiseCard(CardBin.ForYou, PushedUnreviewedKind, subject, $"{pr}: the fixer pushed, but its review could not start: {LegCommander.OneLine(ex.Message)}", $"{ex.Message}\nThe push stands; the review has not started on it.",
                    [new CardChoice { Label = "Review the fixer's push", Verb = Verbs.ReviewPr, Target = subject }], link: review.Url);
            }
            return;
        }
        // The fixer's own session, by the job it was started as: every fixer of a PR has one name, so by name an earlier fixer's ended session made a
        // fixer started a moment ago (its row not listed yet) read as ended, and another was started on it (tianwen#2435 had three; pilot #1247). A row
        // stored before FixerJob was kept falls back to the name.
        var sessions = state.Sessions().Where(s => review.FixerJob is { } job ? string.Equals(s.Id, job, StringComparison.Ordinal) : string.Equals(s.Name, review.Fixer, StringComparison.Ordinal)).ToList();
        if (sessions.Count > 0 && sessions.All(s => s.State is SessionState.Ended or SessionState.Idle))
        {
            // Ended with a pilot run job of its own queued or running is waiting, not finished: the job's end wakes it (pilot #1309: tianwen #2474 and
            // #2476 ended their turn on 'pilot run', were read as finished and stopped, #2474 was queued and its push refused).
            if (sessions.Any(FixerWaitsOnJob))
            {
                return;
            }
            // An idle one that has taken no turn yet, or whose last turn the API stopped, has not finished: left alone, looked at again next sweep.
            if (sessions.Any(IdleNotDone))
            {
                return;
            }
            // A second live fixer of the PR (one the row does not name) that is still working keeps the round open: EndFixerAsync stops every live fixer,
            // which mid-turn would cut it off. Looked at again next sweep, as the moved-head path does.
            if (LiveFixersOf(review).Any(l => !FixerFinished(l)))
            {
                return;
            }
            // What it left: a fixer that ended with its fix uncommitted, or committed and not pushed (a detached HEAD, a push that failed), had its worktree
            // force-removed with the work in it (pilot #1247: tianwen#2435's fix sat in a commit nothing pointed to). Its worktree is kept then, and the card
            // says where the work is and what it is.
            var left = await LeftWorkAsync(review, cancellationToken);
            // An idle fixer of an approved review with nothing left has done its part (its push was reviewed and passed): ended quietly, no card, since it did
            // not end 'without a push'. Idle with the head unmoved on a changes verdict, or with work left, takes the path of an ended one (pilot #1327).
            if (left is null && string.Equals(review.Verdict, "approve", StringComparison.OrdinalIgnoreCase) && sessions.All(s => s.State == SessionState.Idle))
            {
                if (await EndFixerAsync(review, cancellationToken))
                {
                    // A line to read, not a card to answer: the fixer's end is on record, and why no review followed.
                    state.RaiseCard(CardBin.Handled, FixerFinishedKind, subject, $"{pr}: {review.Fixer} finished and was ended",
                        $"Its push was reviewed and approved (head {CommitRefs.Short(review.Head)}), and it left nothing uncommitted or unpushed, so it was ended with its worktree removed.");
                }
                return;
            }
            // Ended on its own with the head unmoved: resumed once per fixer job before any card (pilot #1309). Only a second end, or a resume
            // that could not be delivered, falls through to the card.
            // A fixer a person or the watchtower stopped reads Ended too: waking it would undo the stop, so it goes straight to the card.
            var stoppedOnPurpose = sessions.Any(s => Ledger?.Find([s.SessionId, s.Id, .. s.EarlierSessionIds ?? []]) is { EndReason: not null and not SessionEndReasons.Finished });
            if (!stoppedOnPurpose && review.FixerJob is { } ended && !string.Equals(review.FixerResumedJob, ended, StringComparison.Ordinal) && ResumeFixer is { } resume)
            {
                if (Of(PrRef.Parse($"{review.Repo}#{review.Number}")!) is { } latest)
                {
                    state.PutPrReview(latest with { FixerResumedJob = ended });
                }
                string? refused;
                try
                {
                    refused = await resume(ended, ResumeText(review, left), cancellationToken);
                }
                catch (FlightException ex)
                {
                    refused = ex.Message;
                }
                if (refused is null)
                {
                    return;
                }
                Error.WriteLine($"pr reviews: {pr}'s ended fixer could not be resumed: {refused}");
            }
            if (!await EndFixerAsync(left is null ? review : review with { FixWorktreeBorrowed = true }, cancellationToken))
            {
                return;
            }
            if (left is null && review.DesignAnswer is { Length: > 0 })
            {
                RaiseDesignFixMissingCard(review, subject, $"{pr}");
                return;
            }
            state.RaiseCard(CardBin.ForYou, FixEndedKind, subject, $"{pr}: {review.Fixer} ended without a push",
                left is null
                    ? $"The PR's head is still {CommitRefs.Short(review.Head)}. Fix it starts another fixer on the same findings."
                    : $"The PR's head is still {CommitRefs.Short(review.Head)}, {FixLeftWorkMark} {review.FixWorktree}, kept there: {left}. Push it from there, or Fix it starts another fixer on the same findings in a fresh worktree.",
                [new CardChoice { Label = FixLabel, Verb = Verbs.FixPr, Target = subject }], link: review.Url);
        }
    }

    /// <summary>
    /// The card for a PR whose design question was answered and whose fixer ended without a push (tianwen #2495): the fix the answer called for is
    /// missing, offered as Fix it. An answer that asks for no code (close, supersede) is the owner's to carry out on GitHub, since no fixer closes a
    /// PR: a card with no choice. Never a Merge choice. One place, so the fixer-ended path and the queue's design branch say the same words.
    /// </summary>
    private void RaiseDesignFixMissingCard(PrReviewRow review, string subject, string pr)
    {
        var answer = (review.DesignAnswer ?? "").Trim();
        var at = CommitRefs.Short(review.Head);
        var noCode = AsksForNoCode(answer);
        state.RaiseCard(CardBin.ForYou, FixEndedKind, subject,
            noCode ? $"{pr}: its design answer is to close it, which is yours to do" : $"{pr}: the fix its design answer called for is missing",
            noCode
                ? $"The answer to its design question was \"{answer}\": the PR is to be closed as answered. No fixer closes a PR, so close it on GitHub yourself. Nothing was pushed (head {at})."
                : $"The answer to its design question was \"{answer}\", but its fixer ended without pushing the fix (head {at}). Fix it starts another fixer with the answer.",
            noCode ? [] : [new CardChoice { Label = FixLabel, Verb = Verbs.FixPr, Target = subject }], link: review.Url);
    }

    /// <summary>
    /// True when a design answer asks for no change to the code: it names the PR itself ("close it", "close the PR", "this PR is superseded",
    /// "superseded by #N", "drop the PR") or says to leave things as they are ("leave it as is", "keep main as it is"). "Close the handle in Dispose"
    /// or "drop the cache layer" are code and do not match. A bare "it" or "this" counts only where it ends the clause ("close it.", "drop it"), or
    /// where the next words are one of two named tails: "as superseded", "as a duplicate", "as obsolete" or "as answered" (the issue's "close it as
    /// superseded by #N"), and "or the PR" / "or this pull request" (the brief's "close it or the PR"). Any other words after it keep it code, so
    /// "close this connection", "close it in the finally block", "close it as soon as the response is read", "close it and reopen on error",
    /// "drop it and use a plain list" and "drop this cache layer" stay code; "is closed" counts only for the PR ("this PR is closed"), so "before it
    /// is closed" stays code. The fixer brief's no-change list says the same.
    /// </summary>
    internal static bool AsksForNoCode(string answer) => NoCodeAnswer().IsMatch(answer);

    private const string ThePr = @"(?:the|this)\s+(?:pr|pull\s+request)";

    private const string BareIt = @"(?:it|this)(?=\s*(?:[.,;:!]|$)|\s+as\s+(?:superseded|a\s+duplicate|obsolete|answered)\b|\s+or\s+" + ThePr + @"\b)";

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:close[ds]?\s+(?:" + ThePr + "|" + BareIt + ")"
        + @"|" + ThePr + @"\s+(?:is|can\s+be|should\s+be|to\s+be)\s+(?:closed|superseded|dropped|abandoned|withdrawn)"
        + @"|it\s+is\s+superseded"
        + @"|supersede[ds]?\s+by\s+(?:#|pr)"
        + @"|(?:drop|abandon|withdraw)\s+(?:" + ThePr + "|" + BareIt + ")"
        + @"|(?:leave|keep)\s+(?:it|main|this|things|everything)?\s*(?:as\s+(?:it\s+is|is)|unchanged))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NoCodeAnswer();

    /// <summary>What the node says to a fixer that ended its turn with the PR's head unmoved: what is left, then what to do about it.</summary>
    private static string ResumeText(PrReviewRow review, string? left) =>
        (left is null ? $"Your turn ended with nothing pushed: the PR's head is still {CommitRefs.Short(review.Head)}. " : $"Your turn ended with work not on the PR: {left}. ")
        + "Commit and push it to the PR's branch, and comment on the PR; or, if something stops you (a refused push, a failing build, a point that asks for no change), say what in a PR comment and end your turn.";

    /// <summary>
    /// What an ended fixer left in its worktree that the PR does not have: its uncommitted files and its commits past the PR's head, in a line; null when
    /// nothing (or no worktree of its own, or git cannot read it).
    /// </summary>
    private async Task<string?> LeftWorkAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        if (review.FixWorktree is not { } worktree || review.FixWorktreeBorrowed == true)
        {
            return null;
        }
        return (await WorkPastAsync(worktree, review.Head, cancellationToken))?.Line;
    }

    /// <summary>What a worktree holds that the PR does not have: its commits past <c>head</c> and its uncommitted files, as a line and as counts.</summary>
    private sealed record WorkPast(string Line, int Commits, int Files);

    /// <summary>
    /// The work in <paramref name="worktree"/> past <paramref name="head"/> (the PR's head): commits not on it and uncommitted files; null when
    /// nothing (or no folder, or git cannot read it).
    /// </summary>
    private async Task<WorkPast?> WorkPastAsync(string worktree, string head, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree))
        {
            return null;
        }
        try
        {
            var status = await git.RunAsync(["status", "--porcelain"], worktree, cancellationToken);
            var ahead = await git.RunAsync(["log", "--format=%h %s", $"{head}..HEAD"], worktree, cancellationToken);
            if (!ahead.Ok)
            {
                // The PR's head is not in this clone (someone else pushed it): what HEAD has that no remote branch has is what is not pushed.
                ahead = await git.RunAsync(["log", "--format=%h %s", "HEAD", "--not", "--remotes"], worktree, cancellationToken);
            }
            var files = status.Ok ? status.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            var commits = ahead.Ok ? ahead.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            var parts = new List<string>();
            if (commits.Length > 0)
            {
                parts.Add($"{commits.Length} commit{(commits.Length == 1 ? "" : "s")} not pushed ({string.Join("; ", commits.Take(3))}{(commits.Length > 3 ? "; ..." : "")})");
            }
            if (files.Length > 0)
            {
                parts.Add($"{files.Length} file{(files.Length == 1 ? "" : "s")} not committed ({string.Join(", ", files.Take(5).Select(f => f.Length > 3 ? f[3..] : f))}{(files.Length > 5 ? ", ..." : "")})");
            }
            return parts.Count > 0 ? new WorkPast(string.Join(", and ", parts), commits.Length, files.Length) : null;
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>The Handled line a follow-up PR of a merged PR's fixer leaves.</summary>
    public const string FixFollowUpKind = "pr-fix-follow-up";

    /// <summary>The For-you card of a fixer's commits that could not be pushed or opened as a follow-up PR; the local branch holds them.</summary>
    public const string FixFollowUpFailedKind = "pr-fix-follow-up-failed";

    /// <summary>
    /// The PR merged while its fixer worked (#617): commits in the fixer's worktree past the merged head are pushed to a new branch and
    /// opened as a follow-up PR against the same base, which is reviewed as any PR. Nothing past the head, nothing to do. A push or a
    /// create that fails is a For-you card naming the commit and the local branch that keeps it. Never a force push.
    /// </summary>
    private async Task FollowUpAsync(PrRef pr, PrReviewRow review, PullRequest read, CancellationToken cancellationToken, string? followWorktree = null)
    {
        var worktree = followWorktree ?? review.FixWorktree!;
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        if (review.FixerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        // Every live fixer of the PR the row does not name (pilot #1291, tianwen#2470), a second one beside the row's own included, is stopped the
        // same way before its commits are read, working or not: the first of them being the row's own job must not hide the rest.
        foreach (var other in LiveFixersOf(review).Where(l => !string.Equals(l.Id, review.FixerJob, StringComparison.Ordinal)))
        {
            await StopQuietly(other.Id, cancellationToken);
        }
        var merged = read.Head.Length > 0 ? read.Head : review.Head;
        var baseBranch = read.Base.Length > 0 ? read.Base : "main";
        // A worktree folder gone (or half removed, with no .git of its own, where git would resolve to the clone around it) holds nothing to follow up.
        if (!Directory.Exists(worktree) || !(File.Exists(Path.Combine(worktree, ".git")) || Directory.Exists(Path.Combine(worktree, ".git"))))
        {
            return;
        }
        string tip = "HEAD";
        string localBranch = read.HeadRef;
        try
        {
            var count = await git.RunAsync(["rev-list", "--count", $"{merged}..HEAD"], worktree, cancellationToken);
            if (!count.Ok)
            {
                state.RaiseCard(CardBin.ForYou, FixFollowUpFailedKind, subject,
                    $"{pr} merged while its fixer worked: its commits were not counted",
                    $"{count.Why}\nThe fixer's worktree {worktree} is removed; anything it committed stays on its local branch in {review.ClonePath}.", link: review.Url,
                    summary: $"Look in {review.ClonePath} for commits the fixer made after {pr} merged: git could not count them ({LegCommander.OneLine(count.Why)}).");
                return;
            }
            if (!int.TryParse(count.Output.Trim(), out var ahead) || ahead == 0)
            {
                return;
            }
            var head = await git.RunAsync(["rev-parse", "HEAD"], worktree, cancellationToken);
            tip = head.Ok ? head.Output.Trim() : "HEAD";
            var name = await git.RunAsync(["symbolic-ref", "--short", "HEAD"], worktree, cancellationToken);
            localBranch = name.Ok ? name.Output.Trim() : read.HeadRef;
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            state.RaiseCard(CardBin.ForYou, FixFollowUpFailedKind, subject,
                $"{pr} merged while its fixer worked: its commits were not read",
                $"{ex.Message}\nAnything the fixer committed stays on its local branch in {review.ClonePath}.", link: review.Url,
                summary: $"Look in {review.ClonePath} for commits the fixer made after {pr} merged: they could not be read ({LegCommander.OneLine(ex.Message)}).");
            return;
        }
        // Judged by content, not by commit id: a commit whose changes reached the base under another SHA (a squash, a cherry-pick) needs no PR.
        try
        {
            if (await ChangesOnBaseAsync(worktree, $"origin/{baseBranch}", merged, baseBranch, cancellationToken))
            {
                NoteAlreadyThere(pr, review, $"on {baseBranch}");
                return;
            }
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Not known: the commits are followed up as ever.
        }
        var branch = $"{(read.HeadRef.Length > 0 ? read.HeadRef : "fix")}-followup-{review.Number}";
        void Failed(string why) => state.RaiseCard(CardBin.ForYou, FixFollowUpFailedKind, subject,
            $"{pr} merged while its fixer worked: its commit has no PR",
            $"{why}\nThe commit {tip} stays on the local branch {localBranch} in {review.ClonePath}; push it if {branch} is not on origin, and open a PR against {baseBranch} by hand.", link: review.Url,
            summary: $"Push commit {CommitRefs.Short(tip)} and open a PR against {baseBranch} by hand: {LegCommander.OneLine(why)}.");
        try
        {
            var push = await git.RunAsync(["push", "origin", $"HEAD:refs/heads/{branch}"], worktree, cancellationToken);
            if (!push.Ok)
            {
                // The branch may exist on origin already (an earlier follow-up): read it. Holding the changes means the work is there; never a force push.
                var fetched = await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], worktree, cancellationToken);
                if (!fetched.Ok)
                {
                    Failed($"git could not push {branch}: {push.Why}");
                    return;
                }
                if (await ChangesOnBaseAsync(worktree, $"origin/{branch}", merged, null, cancellationToken))
                {
                    NoteAlreadyThere(pr, review, $"on origin's {branch}");
                    return;
                }
                var open = await github.PullRequestAsync(review.ClonePath, branch, cancellationToken);
                Failed($"The branch {branch} already exists on origin with other work{(open is { State: "OPEN" } ? $" (its open PR is #{open.Number})" : "")}, so the commit could not be pushed to it ({LegCommander.OneLine(push.Why)})");
                return;
            }
            var answer = review.DesignAnswer is { Length: > 0 } given ? $"\n\nThe design answer it carried out: {given}" : "";
            var findings = review.Findings.Length > 0 ? "\n\nFindings of the review:\n" + string.Join('\n', review.Findings.Select(f => "- " + f)) : "";
            var body = $"Follow-up of {pr} ({review.Url}), which merged while its fixer was still working. These are the fixer's commits past the merged head {CommitRefs.Short(merged)}.\n\nFrom the review of {pr}.{findings}{answer}";
            var created = await github.CreateAsync(review.ClonePath, branch, baseBranch, $"Follow-up of {pr}: {read.Title}", body, cancellationToken);
            var follow = new PrRef(pr.Owner, pr.Name, created.Number);
            state.RaiseCard(CardBin.Handled, FixFollowUpKind, subject, $"{pr} merged mid-fix: its commit is now {follow}", $"Commit {CommitRefs.Short(tip)} is on {branch}, against {baseBranch}; the follow-up is reviewed as any PR.", link: created.Url);
            try
            {
                await StartAsync(follow, cancellationToken);
            }
            catch (FlightException ex)
            {
                state.RaiseCard(CardBin.ForYou, PushedUnreviewedKind, Verbs.PrTarget(follow.Repo, follow.Number), $"{follow}: the follow-up's review could not start: {LegCommander.OneLine(ex.Message)}", ex.Message,
                    [new CardChoice { Label = $"Start the review of {follow}", Verb = Verbs.ReviewPr, Target = Verbs.PrTarget(follow.Repo, follow.Number) }], link: created.Url);
            }
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Failed(ex.Message);
        }
    }

    /// <summary>
    /// True when every change of the commits past <paramref name="merged"/> is already on <paramref name="target"/> (a base or a branch on origin),
    /// whatever their SHAs: <c>git cherry</c> says no commit is missing, or, where a squash hides that, the files they touch do not differ.
    /// <paramref name="fetchBase"/> is fetched first when given.
    /// </summary>
    private async Task<bool> ChangesOnBaseAsync(string worktree, string target, string merged, string? fetchBase, CancellationToken cancellationToken)
    {
        if (fetchBase is not null && !(await git.RunAsync(["fetch", "--quiet", "origin", fetchBase], worktree, cancellationToken)).Ok)
        {
            return false;
        }
        var cherry = await git.RunAsync(["cherry", target, "HEAD", merged], worktree, cancellationToken);
        if (cherry.Ok && cherry.Output.Trim().Length > 0 && !cherry.Output.Split('\n').Any(l => l.StartsWith('+')))
        {
            return true;
        }
        // NUL-separated, and compared as names rather than handed back to git as pathspecs: git quotes a name with a space edge, a non-ASCII letter or a
        // control character in its line output, and a quoted name as a pathspec matches nothing, which read as 'no difference' (already on base).
        var names = await git.RunAsync(["diff", "--name-only", "-z", merged, "HEAD"], worktree, cancellationToken);
        var differing = await git.RunAsync(["diff", "--name-only", "-z", target, "HEAD"], worktree, cancellationToken);
        if (!names.Ok || !differing.Ok)
        {
            return false;
        }
        var files = names.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var apart = differing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return files.Length > 0 && !files.Any(apart.Contains);
    }

    /// <summary>A line to read (Handled) saying the fixer's commits need no follow-up PR because their changes are already there.</summary>
    private void NoteAlreadyThere(PrRef pr, PrReviewRow review, string where) =>
        state.RaiseCard(CardBin.Handled, FixFollowUpSettledKind, Verbs.PrTarget(review.Repo, review.Number), $"{pr} merged while its fixer worked: its commits are already {where}",
            $"The changes of the commits its fixer made after the merge are already {where} (under other commit ids), so no follow-up PR was opened and nothing was pushed.");

    // Fixers whose merged PR was already followed up (or failed to be), keyed by PR and worktree, so a worktree that will not go is retried alone.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _followedUp = new(StringComparer.Ordinal);

    /// <summary>Stops a PR's fixer and removes its worktree, then clears the fix fields; false when the worktree is held, said on a card and tried again.</summary>
    private async Task<bool> EndFixerAsync(PrReviewRow review, CancellationToken cancellationToken, string? prHead = null)
    {
        if (review.FixerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        // A second live fixer session of the PR (one the row does not name) is stopped with it, as the follow-up does: left running it would push
        // to a PR whose round has been closed.
        foreach (var other in LiveFixersOf(review).Where(l => !string.Equals(l.Id, review.FixerJob, StringComparison.Ordinal)))
        {
            await StopQuietly(other.Id, cancellationToken);
        }
        // With the PR's current head given (an open PR), a worktree holding commits past it or uncommitted files is kept, never force-removed: the
        // hold (FixerHoldAsync) pushes the commits or rings for it (pilot #1291).
        var keep = prHead is not null && review.FixWorktree is { } kept && review.FixWorktreeBorrowed != true
            && await WorkPastAsync(kept, prHead, cancellationToken) is not null;
        // A borrowed worktree is someone else's: only the row's fix fields are cleared.
        if (!keep && review.FixWorktree is { } worktree && review.FixWorktreeBorrowed != true)
        {
            string? held;
            try
            {
                held = await planes.RemoveAsync(review.ClonePath, worktree, cancellationToken, force: true);
            }
            catch (FlightException ex)
            {
                held = ex.Message;
            }
            if (held is not null)
            {
                var subject = Verbs.PrTarget(review.Repo, review.Number);
                if (!state.Cards(openOnly: true).Any(c => c.Kind == FixLeftKind && c.Subject == subject))
                {
                    // Nothing for the person to press: the node tries again, so it is a line to read, not a card to answer.
                    state.RaiseCard(CardBin.Handled, FixLeftKind, subject, $"{review.Repo}#{review.Number}'s fixer worktree is still there", $"{held}; the node tries again");
                }
                return false;
            }
        }
        state.CloseCardsAbout(Verbs.PrTarget(review.Repo, review.Number), "the fixer ended", FixLeftKind);
        if (Of(PrRef.Parse($"{review.Repo}#{review.Number}")!) is { } latest)
        {
            state.PutPrReview(latest with { Fixer = null, FixerJob = null, FixerResumedJob = null, FixWorktree = null, FixWorktreeBorrowed = null, FixDetached = null });
        }
        return true;
    }

    /// <summary>The Handled notice a settled design question leaves in place of its ringing card.</summary>
    public const string DesignSettledKind = "pr-review-design-settled";

    /// <summary>
    /// What a design question was answered with: the choice taken, why, and who took it (null when it is not known). The settled notice
    /// names the choice and the reason, whichever way the answer came: the card, the watchtower's review fix, the PR's conversation or the node.
    /// </summary>
    private sealed record DesignChoice(string Choice, string Reason, string? By = null);

    /// <summary>The words of the settled notice's cause for an answer: <c>answered by &lt;who&gt; with &lt;choice&gt;: &lt;what followed&gt;</c>, as far as it is known.</summary>
    private static string AnsweredCause(DesignChoice? chose, string followed) =>
        chose is null ? $"answered: {followed}"
            : $"answered{(chose.By is { Length: > 0 } by ? $" by {by}" : "")} with {Clean(chose.Choice, 80)}: {followed}";

    /// <summary>
    /// Downgrades the open design card of a PR to a Handled notice naming what settled it (the PR merged or closed, the question
    /// answered, a later review that decides it or no longer asks it, a flight that follows it landed); false when no design card is
    /// open. A settled question rings no more, and the watchtower, which reads the open cards, never asks about it. When the choice
    /// is known (<paramref name="chose"/>) the notice names it and why; <paramref name="closing"/> says what is left in place of the
    /// default line, for a question that was settled without a fix under way.
    /// </summary>
    private bool SettleDesign(string repo, int number, string cause, string? link, string? underway = null, DesignChoice? chose = null, string? closing = null)
    {
        var subject = Verbs.PrTarget(repo, number);
        if (!state.Cards(openOnly: true).Any(c => c.Kind == DesignKind && c.Subject == subject))
        {
            return false;
        }
        state.CloseCardsAbout(subject, $"settled: {cause}", DesignKind);
        var ending = closing ?? (underway is null
            ? "The card stopped ringing; nothing is left to answer."
            : $"The card stopped ringing: the question is answered and a fix is under way ({underway}).");
        var named = chose is null ? "" : $"Chosen: {Clean(chose.Choice, 300)}\nWhy: {Clean(chose.Reason, 600)}\n";
        state.RaiseCard(CardBin.Handled, DesignSettledKind, subject, $"{repo}#{number}: the design question is settled, {cause}", named + ending, link: link);
        return true;
    }

    /// <summary>
    /// The choice the PR's stored answer stands for, read off the decision recorded for it (who chose and why); the answer alone when none
    /// is recorded yet, as for one given while the fixer waited for a free slot. Null when no answer is stored.
    /// </summary>
    private DesignChoice? StoredChoice(PrReviewRow review)
    {
        if (review.DesignAnswer is not { Length: > 0 } stored)
        {
            return null;
        }
        var target = Verbs.PrTarget(review.Repo, review.Number);
        var said = stored.Trim();
        var recorded = state.Decisions(kind: DecisionKinds.Design).LastOrDefault(d => d.Subject == target && d.Choice == said);
        return recorded is null
            ? new DesignChoice(stored, "it was answered before the fixer could start", null)
            : new DesignChoice(stored, recorded.Reason.Length > 0 ? recorded.Reason : "answered", recorded.By.Length > 0 ? recorded.By : null);
    }

    /// <summary>The name an answer given by the PR's conversation carries, as the person's or the node's own do.</summary>
    private const string CommentWho = "the PR's comment";

    /// <summary>Why an answer was taken, in words: the node's decision says its own reason; the others say who answered and how.</summary>
    private static string AnswerReason(DesignDecided? decision, string? who, bool autopilot) =>
        decision is not null ? decision.Reason
        : who == CommentWho ? "the PR's author, or the controller, answered it in a comment on the PR"
        : autopilot ? $"the watchtower answered it with review fix ({who})"
        : $"{who} answered it, on the card or with review fix";

    /// <summary>
    /// Looks at each PR with a design card open and downgrades it when the PR is merged or closed, when a flight that names it as its
    /// follow-up has landed, or when the PR's conversation holds the answer. An open PR nobody has answered keeps ringing.
    /// </summary>
    private async Task SettleDesignCardsAsync(CancellationToken cancellationToken)
    {
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == DesignKind).ToList())
        {
            if (PrRef.Parse(card.Subject) is not { } pr || Of(pr) is not { } review)
            {
                continue;
            }
            try
            {
                var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
                if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
                {
                    SettleDesign(pr.Repo, pr.Number, $"{pr} is {read.State.ToLowerInvariant()}", review.Url);
                    continue;
                }
                if (LandedFollowUp(pr) is { } landed)
                {
                    SettleDesign(pr.Repo, pr.Number, $"{landed.Code} landed as its follow-up", review.Url);
                    continue;
                }
                if (await AnswerCommentAsync(pr, review, cancellationToken) is { Length: > 0 } comment)
                {
                    await ActOnCommentAnswerAsync(pr, review, comment, cancellationToken);
                }
            }
            catch (FlightException)
            {
                // GitHub not answering settles nothing: the next sweep looks again.
            }
        }
    }

    /// <summary>
    /// A comment on the PR answered the design question: it goes the way the card's Answer goes. A fixer already on, one held for a slot
    /// or an answer already stored starts nothing more; a start that fails leaves the failed-start card, not a settled notice.
    /// </summary>
    private async Task ActOnCommentAnswerAsync(PrRef pr, PrReviewRow review, string comment, CancellationToken cancellationToken)
    {
        var target = Verbs.PrTarget(pr.Repo, pr.Number);
        var flight = FlightOf(pr);
        // A stored answer alone proves nothing: it is stored before the start, so a start that failed leaves it set. Only a fixer on or queued counts.
        if (flight is null && (review.Fixer is not null || _queued.ContainsKey(target)))
        {
            var by = review.Fixer is { } fixer ? $"{fixer} is making the changes the answer calls for" : "the fixer waits for a free Opus slot and starts with the answer";
            // The answer already acted on is the stored one (a card's, say); the comment is the choice only when none is stored.
            var acted = StoredChoice(review) ?? new DesignChoice(comment, AnswerReason(null, CommentWho, false), CommentWho);
            SettleDesign(pr.Repo, pr.Number, "the PR's conversation answers it", review.Url, by, acted);
            return;
        }
        try
        {
            await ActOnAnswerAsync(pr, review, comment, CommentWho, null, false, null, cancellationToken);
        }
        catch (FlightException ex) when (flight is null)
        {
            RaiseOnce(pr, review, CardBin.ForYou, FixFailedKind, target, $"{pr}: the fixer could not start: {LegCommander.OneLine(ex.Message)}", ex.Message,
                [new CardChoice { Label = "Start the fixer again", Verb = Verbs.FixPr, Target = target }]);
            return;
        }
        // Say only what is true: a fixer running, one queued for a slot, or the flight's leg sent round.
        var now = Of(pr) ?? review;
        var how = flight is not null ? "the flight's last leg goes round with the answer"
            : now.Fixer is { } started ? $"{started} is making the changes the answer calls for"
            : _queued.ContainsKey(target) ? "the fixer waits for a free Opus slot and starts with the answer"
            : null;
        if (how is null) return;
        SettleDesign(pr.Repo, pr.Number, "the PR's conversation answers it", review.Url, how, new DesignChoice(comment, AnswerReason(null, CommentWho, false), CommentWho));
    }

    /// <summary>A landed flight in the PR's repo whose title names the PR (<c>#n</c>) and that is not the PR's own flight, or null.</summary>
    private FlightRow? LandedFollowUp(PrRef pr)
    {
        var own = FlightOf(pr);
        var named = new System.Text.RegularExpressions.Regex($@"#{pr.Number}\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        // Only a flight that landed after the review was posted can have followed it up; one that landed before answered nothing.
        var asked = Of(pr) is { } review ? review.PostingAt ?? review.FinishedAt ?? review.StartedAt : (DateTimeOffset?)null;
        return state.Flights().FirstOrDefault(f => f.State == FlightState.Landed && f.Code != own?.Code
            && f.LandedAt is { } landedAt && (asked is null || landedAt > asked)
            && (string.Equals(f.Repo, pr.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(f.Repo, pr.Repo, StringComparison.OrdinalIgnoreCase))
            && named.IsMatch(f.Title ?? ""));
    }

    /// <summary>The prefix that marks a finding as a question of design, not a defect to fix.</summary>
    public const string DesignPrefix = "design:";

    private static bool IsDesign(PrFinding finding) => finding.Body.StartsWith(DesignPrefix, StringComparison.OrdinalIgnoreCase);
}
