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
    /// a newer review of the PR closes it.
    /// </summary>
    public const string PushedUnreviewedKind = "pr-fix-pushed-unreviewed";

    /// <summary>The card's choice that starts the fixer.</summary>
    public const string FixLabel = "Fix it";

    /// <summary>The ring of a changes verdict after the last round of fixes: no further fixer starts.</summary>
    public const string FixCappedKind = "pr-fix-capped";

    /// <summary>The For-you card of a fixer whose session ended with the PR's head where it was, with Fix it to try again.</summary>
    public const string FixEndedKind = "pr-fix-ended";

    /// <summary>The For-you card said when a fixer's worktree could not be removed; the node tries again.</summary>
    public const string FixLeftKind = "pr-fix-left";

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
            if (!await EndFixerAsync(review, cancellationToken))
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
                    [new CardChoice { Label = $"Start the review of {pr} on the fixer's push", Verb = Verbs.ReviewPr, Target = subject }], link: review.Url);
            }
            return;
        }
        var sessions = state.Sessions().Where(s => string.Equals(s.Name, review.Fixer, StringComparison.Ordinal)).ToList();
        if (sessions.Count > 0 && sessions.All(s => s.State == SessionState.Ended) && await EndFixerAsync(review, cancellationToken))
        {
            state.RaiseCard(CardBin.ForYou, FixEndedKind, subject, $"{pr}: {review.Fixer} ended without a push",
                $"The PR's head is still {review.Head[..Math.Min(8, review.Head.Length)]}. Fix it starts another fixer on the same findings.",
                [new CardChoice { Label = FixLabel, Verb = Verbs.FixPr, Target = subject }], link: review.Url);
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
    private async Task FollowUpAsync(PrRef pr, PrReviewRow review, PullRequest read, CancellationToken cancellationToken)
    {
        var worktree = review.FixWorktree!;
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        if (review.FixerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
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
                    $"{pr} merged while its fixer worked: its commits could not be counted ({LegCommander.OneLine(count.Why)})",
                    $"{count.Why}\nThe fixer's worktree {worktree} is removed; anything it committed stays on its local branch in {review.ClonePath}.", link: review.Url);
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
                $"{pr} merged while its fixer worked: its commits could not be looked at ({LegCommander.OneLine(ex.Message)})",
                $"{ex.Message}\nAnything the fixer committed stays on its local branch in {review.ClonePath}.", link: review.Url);
            return;
        }
        var branch = $"{(read.HeadRef.Length > 0 ? read.HeadRef : "fix")}-followup-{review.Number}";
        void Failed(string why) => state.RaiseCard(CardBin.ForYou, FixFollowUpFailedKind, subject,
            $"{pr} merged while its fixer worked: commit {Short(tip)} has no follow-up PR ({LegCommander.OneLine(why)})",
            $"{why}\nThe commit {tip} stays on the local branch {localBranch} in {review.ClonePath}; push it if {branch} is not on origin, and open a PR against {baseBranch} by hand.", link: review.Url);
        try
        {
            var push = await git.RunAsync(["push", "origin", $"HEAD:refs/heads/{branch}"], worktree, cancellationToken);
            if (!push.Ok)
            {
                Failed($"git could not push {branch}: {push.Why}");
                return;
            }
            var answer = review.DesignAnswer is { Length: > 0 } given ? $"\n\nThe design answer it carried out: {given}" : "";
            var findings = review.Findings.Length > 0 ? "\n\nFindings of the review:\n" + string.Join('\n', review.Findings.Select(f => "- " + f)) : "";
            var body = $"Follow-up of {pr} ({review.Url}), which merged while its fixer was still working. These are the fixer's commits past the merged head {Short(merged)}.\n\nFrom the review of {pr}.{findings}{answer}";
            var created = await github.CreateAsync(review.ClonePath, branch, baseBranch, $"Follow-up of {pr}: {read.Title}", body, cancellationToken);
            var follow = new PrRef(pr.Owner, pr.Name, created.Number);
            state.RaiseCard(CardBin.Handled, FixFollowUpKind, subject, $"{pr} merged while its fixer worked: its commit {Short(tip)} is now {follow} on {branch}", $"against {baseBranch}", link: created.Url);
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

    // Fixers whose merged PR was already followed up (or failed to be), keyed by PR and worktree, so a worktree that will not go is retried alone.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _followedUp = new(StringComparer.Ordinal);

    /// <summary>Stops a PR's fixer and removes its worktree, then clears the fix fields; false when the worktree is held, said on a card and tried again.</summary>
    private async Task<bool> EndFixerAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        if (review.FixerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        if (review.FixWorktree is { } worktree)
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
                    state.RaiseCard(CardBin.ForYou, FixLeftKind, subject, $"{review.Repo}#{review.Number}'s fixer worktree is still there", $"{held}; the node tries again");
                }
                return false;
            }
        }
        state.CloseCardsAbout(Verbs.PrTarget(review.Repo, review.Number), "the fixer ended", FixLeftKind);
        if (Of(PrRef.Parse($"{review.Repo}#{review.Number}")!) is { } latest)
        {
            state.PutPrReview(latest with { Fixer = null, FixerJob = null, FixWorktree = null });
        }
        return true;
    }

    /// <summary>The Handled notice a settled design question leaves in place of its ringing card.</summary>
    public const string DesignSettledKind = "pr-review-design-settled";

    /// <summary>
    /// Downgrades the open design card of a PR to a Handled notice naming what settled it (the PR merged or closed, the question
    /// answered, a flight that follows it landed); false when no design card is open. A settled question rings no more, and the
    /// watchtower, which reads the open cards, never asks about it.
    /// </summary>
    private bool SettleDesign(string repo, int number, string cause, string? link, string? underway = null)
    {
        var subject = Verbs.PrTarget(repo, number);
        if (!state.Cards(openOnly: true).Any(c => c.Kind == DesignKind && c.Subject == subject))
        {
            return false;
        }
        state.CloseCardsAbout(subject, $"settled: {cause}", DesignKind);
        state.RaiseCard(CardBin.Handled, DesignSettledKind, subject, $"{repo}#{number}: the design question is settled, {cause}",
            underway is null ? "The card stopped ringing; nothing is left to answer." : $"The card stopped ringing: the question is answered and a fix is under way ({underway}).", link: link);
        return true;
    }

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
            SettleDesign(pr.Repo, pr.Number, "the PR's conversation answers it", review.Url, by);
            return;
        }
        try
        {
            await ActOnAnswerAsync(pr, review, comment, "the PR's comment", null, false, null, cancellationToken);
        }
        catch (FlightException ex) when (flight is null)
        {
            RaiseOnce(pr, review, CardBin.ForYou, FixFailedKind, target, $"{pr}: the fixer could not start: {LegCommander.OneLine(ex.Message)}", ex.Message,
                [new CardChoice { Label = $"Start the fixer on {pr} again", Verb = Verbs.FixPr, Target = target }]);
            return;
        }
        // Say only what is true: a fixer running, one queued for a slot, or the flight's leg sent round.
        var now = Of(pr) ?? review;
        var how = flight is not null ? "the flight's last leg goes round with the answer"
            : now.Fixer is { } started ? $"{started} is making the changes the answer calls for"
            : _queued.ContainsKey(target) ? "the fixer waits for a free Opus slot and starts with the answer"
            : null;
        if (how is null) return;
        SettleDesign(pr.Repo, pr.Number, "the PR's conversation answers it", review.Url, how);
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
