using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>
    /// A changes verdict on a PR now posted (#290). On the controller's own PR in a repo the node tracks, with no flight owning
    /// it, one fixer starts on the PR's branch; on anyone else's, a For-you card offers Fix it. A finding marked as a design
    /// question rings instead and starts none, and an untracked repo gets nothing. A fixer already on is never joined by a second.
    /// A fixer that could not start is a For-you card, not a failed post.
    /// </summary>
    private async Task FixAfterPostAsync(PrRef pr, CancellationToken cancellationToken)
    {
        if (Of(pr) is not { Fixer: null } review)
        {
            return;
        }
        // A flight's PR has its own legs to make changes, but a question of design is the person's: it rings here, and the answer
        // sends the flight's last leg round (review fix).
        var flight = FlightOf(pr);
        var subject = Verbs.PrTarget(pr.Repo, pr.Number);
        var asksDesign = review.Findings.Any(IsDesign);
        if (flight is not null && !asksDesign || review.Verdict != "changes" && !asksDesign)
        {
            return;
        }
        if (flight is null && TrackedClone(pr) is null)
        {
            // Nobody can fix it here, and the post's Handled line says only that it went out: the owner is rung with the findings.
            RaiseNoFixer(pr, review, subject, $"{pr.Repo} is not a repo the node tracks, so it makes no changes there");
            return;
        }
        if (asksDesign)
        {
            var questions = review.Findings.Select(DesignQuestion.Parse).OfType<DesignQuestion>().ToList();
            var design = DesignDetail(questions);
            var asked = questions.Count == 1 ? $": {Clean(questions[0].Question, 80)}" : "";
            var choices = DesignChoices(subject, questions);
            if (flight is not null)
            {
                RaiseOnce(pr, review, CardBin.Ring, DesignKind, subject, $"{pr} ({flight.Code}'s PR): the review asks a question of design{asked}",
                    $"{design}\nNo fixer started: {flight.Code}'s own legs make changes. review fix {pr} <answer> sends its last leg round with the answer, as the card's Answer does.",
                    choices);
                return;
            }
            RaiseOnce(pr, review, CardBin.Ring, DesignKind, subject, $"{pr}: the review asks a question of design{asked}", $"{design}\nNo fixer started: this is the author's to decide. An option or Answer in your own words posts the answer on the PR and starts the fixer.",
                choices);
            return;
        }
        var rounds = review.FixRound ?? 0;
        if (rounds >= MaxFixRounds)
        {
            RaiseOnce(pr, review, CardBin.Ring, FixCappedKind, subject, $"{pr}: still asking for changes after {rounds} rounds of fixes",
                $"{rounds} fixers have pushed and the review still asks for changes, so no fourth starts: this is yours.\n{(review.Report ?? "").Trim()}\n{string.Join('\n', review.Findings.Select(f => f.ToString()))}");
            return;
        }
        // A PR the controller already chose Fix it on goes on by itself, whoever owns it.
        var own = rounds > 0 || Own(review, await github.LoginAsync(cancellationToken));
        if (!own)
        {
            RaiseOnce(pr, review, CardBin.ForYou, FixOfferKind, subject, $"{pr}: the review asked for changes, and it is {review.Author ?? "someone else"}'s PR",
                $"{(review.Report ?? "").Trim()}\nFix it starts a session on the PR's branch that makes them.",
                [new CardChoice { Label = FixLabel, Verb = Verbs.FixPr, Target = subject }]);
            return;
        }
        try
        {
            await StartFixerAsync(pr, review, cancellationToken);
        }
        catch (FlightException ex)
        {
            // Retry is the same Fix it: once what stopped it is cleared (a branch checked out elsewhere), it starts the fixer.
            RaiseOnce(pr, review, CardBin.ForYou, FixFailedKind, subject, $"{pr}: the fixer could not start: {LegCommander.OneLine(ex.Message)}", ex.Message,
                [new CardChoice { Label = $"Start the fixer on {pr} again", Verb = Verbs.FixPr, Target = subject }]);
        }
    }

    /// <summary>The Ring card of a review that asks for changes while no fixer works on the PR: the report, the findings and why none runs (pilot #527).</summary>
    public const string NoFixerKind = "pr-review-no-fixer";

    /// <summary>
    /// Rings the owner with the review's report and findings and the reason no fixer runs. Once per review (<see cref="RaiseOnce"/>), so a
    /// dismissal sticks. It is never closed as handled: it closes only when its cause is gone, saying which (the head moved, a newer
    /// review, a fixer started), as <see cref="ReviewCardKinds"/> and <c>StartFixerAsync</c> do.
    /// </summary>
    public void RingNoFixer(PrRef pr, PrReviewRow review, string why)
    {
        // A review that raised its own card for this reason (design, cap, offer, failed start) is not rung a second time.
        if ((Of(pr) ?? review).Raised.Any(k => k is DesignKind or FixCappedKind or FixOfferKind or FixFailedKind))
        {
            return;
        }
        RaiseNoFixer(pr, review, Verbs.PrTarget(pr.Repo, pr.Number), why);
    }

    private void RaiseNoFixer(PrRef pr, PrReviewRow review, string subject, string why) =>
        RaiseOnce(pr, review, CardBin.Ring, NoFixerKind, subject, review.Verdict == "changes" ? $"{pr}: the review asks for changes, and no fixer works on it" : $"{pr}: the review asks a question, and no fixer works on it",
            $"No fixer started: {why}.\n{(review.Report ?? "").Trim()}\n{string.Join('\n', review.Findings.Select(f => f.ToString()))}".TrimEnd());

    /// <summary>
    /// Why a fixer must not start on its own for this review, or null when one may: the guards <see cref="FixAfterPostAsync"/> keeps
    /// (an untracked repo, a flight's PR, a design question, the cap on rounds, a PR that is not the controller's own and has not
    /// been chosen Fix it on). A recheck asks this before it starts a fixer, so it starts none the post deliberately did not.
    /// </summary>
    public async Task<string?> WhyNoFixerAsync(PrRef pr, PrReviewRow review, CancellationToken cancellationToken)
    {
        if (TrackedClone(pr) is null)
        {
            return $"{pr.Repo} is not a repo the node tracks";
        }
        if (FlightOwns(pr))
        {
            return "a flight owns it, so its own legs fix it";
        }
        if (review.DesignAnswer is null && review.Findings.Any(IsDesign))
        {
            return "its review asks a question of design, which is the author's to decide";
        }
        var rounds = review.FixRound ?? 0;
        if (rounds >= MaxFixRounds)
        {
            return $"{rounds} rounds of fixes have pushed and it still asks for changes, so no more start";
        }
        if (rounds == 0 && !Own(review, await github.LoginAsync(cancellationToken)))
        {
            return $"it is {review.Author ?? "someone else"}'s PR, and Fix it has not been chosen";
        }
        return null;
    }

    /// <summary>
    /// The Fix it choice: the same fixer, started on the controller's word on a PR that is not their own. A PR this node holds no
    /// review of (the Pull requests page's Review word is the pilot/review status, which another node may have set, or the review
    /// was lost) gets pilot's review first; its findings are fixed when it asks for changes, by the way a posted verdict goes.
    /// </summary>
    private async Task<string> FixAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
    {
        if (Of(pr) is not { } review)
        {
            var started = await StartAsync(pr, cancellationToken);
            return $"no review of {pr} here, so a review was started first ({started}); its findings are fixed when it asks for changes";
        }
        var asksDesign = review.Findings.Any(IsDesign);
        if (review.Verdict != "changes" && !asksDesign)
        {
            throw new FlightException($"{pr}'s review did not ask for changes, so there is nothing to fix");
        }
        var words = string.Join(' ', command.Args ?? []).Trim();
        string? given = null;
        if (asksDesign)
        {
            given = words.Length > 0 ? words : review.DesignAnswer is { Length: > 0 } stored ? stored : await AnswerCommentAsync(pr, review, cancellationToken);
            if (given is not { Length: > 0 })
            {
                throw new FlightException($"{pr}'s review asks a question of design and it has no answer yet: answer it on the PR, or give the answer with review fix {pr} <answer>");
            }
        }
        if (asksDesign)
        {
            return await ActOnAnswerAsync(pr, review, given!, command.Who, command.PersonsWord, command.Autopilot, words, cancellationToken, command);
        }
        // The watchtower's fixer on autopilot keeps the guards the node's own fixer keeps (the review of #470): no fourth round, no
        // design question, and no push to another author's PR until the person has chosen Fix it. Those stay the person's.
        if (command.Autopilot && await WhyNoFixerAsync(pr, review, cancellationToken) is { } why)
        {
            throw new FlightException($"not started on the autopilot: {why}; that is the person's to choose");
        }
        return await StartFixerAsync(pr, review, cancellationToken);
    }

    /// <summary>
    /// Acts on the answer to a review's design question, whether the card's Answer gave it or the PR's conversation did: the answer
    /// is stored as DesignAnswer, then a flight's last leg goes round with it, or the fixer starts on a PR no flight owns. The words
    /// the person gave (<paramref name="postWords"/>) are posted on the PR once the act was not refused; an answer that is already a
    /// comment there posts nothing.
    /// </summary>
    private async Task<string> ActOnAnswerAsync(PrRef pr, PrReviewRow review, string given, string? who, string? personsWord, bool autopilot, string? postWords, CancellationToken cancellationToken, CommandRequest? command = null)
    {
        review = review with { DesignAnswer = given };
        // Stored as soon as it is known: a fixer held for an Opus slot starts later from the stored row, and must find it.
        state.PutPrReview(review);
        var answer = $"Answer to the review's question of design (given by {who}):\n\n{postWords}";
        if (FlightOf(pr) is { } flight)
        {
            if (SendFlightRound is not { } send)
            {
                throw new FlightException($"{flight.Code} owns {pr}: its own legs fix it");
            }
            if (command is null && !string.IsNullOrEmpty(postWords))
            {
                await github.CommentIssueAsync(TrackedClone(pr) ?? review.ClonePath, pr.Number, answer, cancellationToken);
            }
            var questions = string.Join('\n', review.Findings.Where(IsDesign).Select(f => f.ToString()));
            var round = $"The review of {pr} asked a question of design:\n{questions}\nThe answer (by {who}):\n{given}\nMake the changes the answer calls for.";
            if (await send(flight.Code, round, who, personsWord, cancellationToken) is { } refused)
            {
                throw new FlightException(refused);
            }
            state.PutPrReview(review);
            if (command is not null)
            {
                await RecordDesignAsync(pr, flight, review, command, given, postWords ?? "", cancellationToken);
            }
            SettleDesign(pr.Repo, pr.Number, $"answered by {who}: {flight.Code}'s last leg went round with it", review.Url, $"{flight.Code}'s last leg was sent round with the answer");
            return $"sent {flight.Code}'s last leg round with the answer to {pr}'s question of design";
        }
        if (autopilot && await WhyNoFixerAsync(pr, review, cancellationToken) is { } why)
        {
            throw new FlightException($"not started on the autopilot: {why}; that is the person's to choose");
        }
        // A PR already in the merge queue is taken out first: the fixer's push would otherwise land the queued head without the fix.
        var dequeued = false;
        if (review.Fixer is null && await github.MergeQueueAsync(TrackedClone(pr) ?? review.ClonePath, pr.Number, cancellationToken) is { State: "OPEN", Queued: true })
        {
            await github.DequeueAsync(TrackedClone(pr) ?? review.ClonePath, pr.Number, cancellationToken);
            dequeued = true;
            review = review with { QueuedAt = null };
            state.PutPrReview(review);
        }
        string fixerStart;
        try
        {
            fixerStart = await StartFixerAsync(pr, review, cancellationToken);
        }
        catch (FlightException ex) when (command is not null)
        {
            // The answer stays stored (DesignAnswer), and the card's choice starts the fixer with it, without asking again.
            var subject = Verbs.PrTarget(pr.Repo, pr.Number);
            RaiseOnce(pr, review, CardBin.ForYou, FixFailedKind, subject, $"{pr}: the fixer could not start: {LegCommander.OneLine(ex.Message)}", ex.Message,
                [new CardChoice { Label = $"Start the fixer on {pr} again", Verb = Verbs.FixPr, Target = subject }]);
            throw;
        }
        if (dequeued)
        {
            state.RaiseCard(CardBin.Handled, FixStartedKind, Verbs.PrTarget(pr.Repo, pr.Number),
                $"{pr} was taken out of the merge queue so the fix can be pushed", "The answer to its design question started a fixer; its push is reviewed afresh and queued when green.", link: review.Url);
        }
        if (command is null && !string.IsNullOrEmpty(postWords))
        {
            // Posted once the start was not refused, so a retry does not post them twice.
            await github.CommentIssueAsync(TrackedClone(pr) ?? review.ClonePath, pr.Number, answer, cancellationToken);
        }
        if (command is not null)
        {
            // Recorded once the start was not refused, so a retry does not record the answer twice.
            await RecordDesignAsync(pr, null, review, command, given, postWords ?? "", cancellationToken);
        }
        return fixerStart;
    }

    // A question of design answered, once: the findings asked, the options the design card offered (its choices that are not the free
    // Answer), the option picked or the free text, and the words as the reason. On a flight's PR the flight is the subject.
    private async Task RecordDesignAsync(PrRef pr, FlightRow? flight, PrReviewRow review, CommandRequest command, string? given, string words, CancellationToken cancellationToken)
    {
        var target = Verbs.PrTarget(pr.Repo, pr.Number);
        var options = state.Cards().Where(c => c.Kind == DesignKind && c.Subject == target)
            .SelectMany(c => c.Choices).Where(c => c.AsksFor is null).Select(c => c.Label).Distinct().ToArray();
        var answer = (given ?? "").Trim();
        var byKind = command.Autopilot ? DecisionByKinds.Autopilot
            : command.PersonsWord is not null || Standing.Of(command, state, "").Kind == StandingKind.Person ? DecisionByKinds.Person
            : DecisionByKinds.Node;
        var decision = state.RecordDecision(flight is null ? target : Verbs.FlightTarget(flight.Code), DecisionKinds.Design,
            string.Join('\n', review.Findings.Where(IsDesign).Select(f => f.ToString())), options, answer.Length > 0 ? answer : "answered",
            command.Who, byKind, words.Length > 0 ? words : "the author's answer on the PR");
        // Off the state lock; a comment that cannot be posted is a card, never a failed answer.
        await new DesignDecisionMirror(state, github).PostAsync(decision, pr, flight, TrackedClone(pr) ?? review.ClonePath, cancellationToken);
    }

    /// <summary>
    /// The answer a PR's conversation gives a review's design question: the newest comment by the PR's author or gh's login made
    /// after the review was posted (or finished), or null.
    /// </summary>
    private async Task<string?> AnswerCommentAsync(PrRef pr, PrReviewRow review, CancellationToken cancellationToken)
    {
        var since = review.PostingAt ?? review.FinishedAt ?? review.StartedAt;
        var login = await github.LoginAsync(cancellationToken);
        var comments = await github.PrCommentsAsync(TrackedClone(pr) ?? review.ClonePath, pr.Number, cancellationToken);
        return comments
            .Where(c => c.CreatedAt > since && c.Body.Trim().Length > 0
                && (string.Equals(c.Author, review.Author, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Author, login, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.Body.Trim())
            .FirstOrDefault();
    }

    /// <summary>
    /// Starts the one fixer of a PR: its branch fetched, a worktree <c>&lt;clone&gt;/.claude/worktrees/fix-&lt;repo&gt;-&lt;n&gt;</c> on it,
    /// a session briefed with the review. Refused for a repo the node does not track, a PR a flight owns, one that is not
    /// open, and one that has a fixer already.
    /// </summary>
    private async Task<string> StartFixerAsync(PrRef pr, PrReviewRow review, CancellationToken cancellationToken)
    {
        if (review.Fixer is { } on)
        {
            throw new FlightException($"{pr} has {on} fixing it already");
        }
        var clone = TrackedClone(pr) ?? throw new FlightException($"{pr.Repo} is not a repo the node tracks, so it makes no changes there");
        if (FlightOwns(pr))
        {
            throw new FlightException($"a flight owns {pr}: its own legs fix it");
        }
        if (WaitsForSlot(pr, fix: true, Models.Model(RoleModels.Worker)))
        {
            return $"queued: the fixer of {pr} waits for a free Opus slot";
        }
        var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr} is {read.State.ToLowerInvariant()}, not open");
        }
        var branch = read.HeadRef.Length > 0 ? read.HeadRef : throw new FlightException($"gh did not say {pr}'s branch");

        var fetch = await git.RunAsync(["fetch", "--quiet", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], clone, cancellationToken);
        if (!fetch.Ok)
        {
            throw new FlightException($"git could not fetch {pr}'s branch {branch} into {clone} (a PR from a fork has none there): {fetch.Why}");
        }
        if (!(await git.RunAsync(["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], clone, cancellationToken)).Ok)
        {
            var made = await git.RunAsync(["branch", "--track", branch, $"origin/{branch}"], clone, cancellationToken);
            if (!made.Ok)
            {
                throw new FlightException($"git could not make the branch {branch} in {clone}: {made.Why}");
            }
        }

        var name = Slug(pr.Name);
        var plane = $"fix-{name}-{pr.Number}";
        var worktree = Path.Combine(clone, ".claude", "worktrees", plane);
        var borrowed = false;
        var detached = false;
        if (await HolderOfBranchAsync(clone, worktree, branch, cancellationToken) is { } holder)
        {
            var notBorrowed = await WhyNotBorrowAsync(clone, holder, branch, cancellationToken);
            if (notBorrowed is null)
            {
                worktree = holder;
                plane = Path.GetFileName(holder);
                borrowed = true;
            }
            else
            {
                try
                {
                    await planes.DetachAsync(clone, worktree, $"refs/remotes/origin/{branch}", cancellationToken);
                }
                catch (FlightException ex)
                {
                    throw new FlightException($"{branch} is checked out in {holder}, which was not used because {notBorrowed}, and a detached worktree could not be made: {ex.Message}");
                }
                detached = true;
            }
        }
        else
        {
            await planes.EnsureAsync(clone, worktree, branch, cancellationToken);
        }

        var fixer = $"{options.Machine.ToLowerInvariant()}-fix-{name}-{pr.Number}";
        var fixing = review with
        {
            Fixer = fixer, FixWorktree = worktree, FixRound = (review.FixRound ?? 0) + 1,
            FixWorktreeBorrowed = borrowed ? true : null, FixDetached = detached ? true : null,
        };
        string job;
        var counting = new object();
        Slots.Begin("fix-pr:" + fixer, counting, fixer, null, AdmissionRole.Fixer, Models.Model(RoleModels.Worker));
        try
        {
            job = await workers.StartAsync(new WorkerStart(fixer, clone, plane, Briefs.FixPr(fixing, branch, CiShape.Of(worktree)),
                Models.Model(RoleModels.Worker), Models.Effort(RoleModels.Worker), Reason: SessionStartReasons.PrFix, Pr: pr.Number), cancellationToken);
        }
        catch (FlightException)
        {
            // A borrowed worktree is someone else's and stays.
            if (!borrowed)
            {
                await planes.RemoveAsync(clone, worktree, cancellationToken, force: true);
            }
            throw;
        }
        finally
        {
            Slots.End("fix-pr:" + fixer, counting);
        }
        state.PutPrReview(fixing with { FixerJob = job });
        SettleDesign(pr.Repo, pr.Number, "answered: the fixer started on it", review.Url, $"{fixer} is making the changes the answer calls for");
        state.CloseCardsAbout(Verbs.PrTarget(pr.Repo, pr.Number), "a fixer started", FixOfferKind, NoFixerKind, DesignKind, FixCappedKind, FixFailedKind);
        state.RaiseCard(CardBin.Handled, FixStartedKind, Verbs.PrTarget(pr.Repo, pr.Number),
            $"Started {fixer} on {pr}'s branch {branch}, to make the changes the review asked for", $"in {worktree}", link: review.Url);
        return $"started {fixer} at {Short(read.Head)}";
    }

    // Another worktree of the clone (not the fix plane's own folder) that has the branch checked out, or null.
    private async Task<string?> HolderOfBranchAsync(string clone, string fixPlane, string branch, CancellationToken cancellationToken)
    {
        if (await planes.WorktreesAsync(clone, cancellationToken) is not { } listed)
        {
            return null;
        }
        return listed.FirstOrDefault(w => !w.Prunable && w.Branch == branch && !SamePath(w.Path, fixPlane)) is { } w2 ? Path.GetFullPath(w2.Path) : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // A holder is borrowed when it is clean, at origin/<branch>, directly under <clone>/.claude/worktrees, and no live session is in it.
    // Null when the holder may be borrowed; else why not.
    private async Task<string?> WhyNotBorrowAsync(string clone, string holder, string branch, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(holder)));
        if (parent is null || !SamePath(parent, Path.Combine(clone, ".claude", "worktrees")))
        {
            return "it lies outside <clone>/.claude/worktrees";
        }
        if (state.Sessions().Any(s => s.State != SessionState.Ended && s.Cwd is { Length: > 0 } cwd && SamePath(cwd, holder)))
        {
            return "a live session works in it";
        }
        if (await planes.IsDirtyAsync(holder, cancellationToken) != false)
        {
            return "it holds uncommitted changes";
        }
        var head = await git.RunAsync(["rev-parse", "HEAD"], holder, cancellationToken);
        var tip = await git.RunAsync(["rev-parse", $"refs/remotes/origin/{branch}"], clone, cancellationToken);
        return head.Ok && tip.Ok && string.Equals(head.Output.Trim(), tip.Output.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null : "it is not at origin's head of the branch";
    }

    /// <summary>
    /// Merges the base of <paramref name="pr"/> into its branch (<c>git merge --no-edit</c>) in a throwaway worktree of the clone pilot
    /// holds and pushes it plainly, a fast-forward of the head read; a recheck of the controller's own PR (#286). Never a rebase and
    /// never a force push, as with a flight's branch (#395). Null when it is pushed; else why not, and whether the merge conflicted
    /// (aborted, nothing pushed).
    /// </summary>
    public async Task<(bool Conflicts, string Why)?> MergeBaseIntoOwnBranchAsync(PrRef pr, PullRequest read, CancellationToken cancellationToken)
    {
        var clone = TrackedClone(pr);
        if (clone is null)
        {
            return (false, $"{pr.Repo} is not a repo the node tracks");
        }
        if (read.HeadRef.Length == 0 || read.Base.Length == 0 || read.Fork)
        {
            return (false, read.Fork ? "its branch is on a fork" : "gh did not say its branch and base");
        }
        if (Of(pr) is { Fixer: not null })
        {
            return (false, "a fixer is working on its branch");
        }
        var branch = read.HeadRef;
        var worktree = Path.Combine(clone, ".claude", "worktrees", $"update-{Slug(pr.Name)}-{pr.Number}");
        try
        {
            var fetch = await git.RunAsync(["fetch", "--quiet", "origin", read.Base, $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], clone, cancellationToken);
            if (!fetch.Ok)
            {
                return (false, $"git could not fetch {branch} and {read.Base}: {fetch.Why}");
            }
            var tip = await git.RunAsync(["rev-parse", $"refs/remotes/origin/{branch}"], clone, cancellationToken);
            if (!tip.Ok || !string.Equals(tip.Output.Trim(), read.Head, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"{branch} is not at the head read from GitHub ({Short(read.Head)}), so it is left alone");
            }
            worktree = await planes.PinAsync(clone, worktree, read.Head, cancellationToken);
            var merge = await git.RunAsync(["merge", "--no-edit", $"origin/{read.Base}"], worktree, cancellationToken);
            if (!merge.Ok)
            {
                await git.RunAsync(["merge", "--abort"], worktree, cancellationToken);
                return (true, $"merging {read.Base} into {branch} conflicts ({merge.Why})");
            }
            // A plain push: the merge sits on the head read, so it is a fast-forward, refused if the branch moved since.
            var push = await git.RunAsync(["push", "origin", $"HEAD:refs/heads/{branch}"], worktree, cancellationToken);
            return push.Ok ? null : (false, $"git could not push {branch}: {push.Why}");
        }
        catch (FlightException ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            await planes.RemoveAsync(clone, worktree, cancellationToken, force: true);
        }
    }
}
