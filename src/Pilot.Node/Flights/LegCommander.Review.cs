using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The review of a flight: the reviewer, its verdicts, the rounds of changes and the review loop.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The leg name reserved for the flight's reviewer: <c>&lt;machine&gt;-&lt;flight&gt;-review</c>.</summary>
    public const string ReviewLeg = "review";

    /// <summary>The name a flight's planner flies under, which no leg may take.</summary>
    public const string PlanLeg = "plan";

    /// <summary>The card a planner's <c>planned</c> raises, offering the first leg.</summary>
    public const string PlannedKind = "planned";

    public const string ReviewChanges = "review-changes";

    public const string PrReady = "pr-ready";

    /// <summary>
    /// What a PR-ready card turns into when its armed merge fails on a check or a moved head: the same card, saying what failed,
    /// offering a go-around, a re-run of the checks and the merge again. Not sent back as changes by itself, as a PR-ready one
    /// whose checks fail is: the person chooses.
    /// </summary>
    public const string MergeFailed = "merge-failed";

    /// <summary>The card a leg's start raises when a sibling clone its build compiles against is behind its origin.</summary>
    public const string SiblingBehindKind = "sibling-behind";

    /// <summary>The Handled line a leg's start leaves for a sibling clone it pulled up to its origin.</summary>
    public const string SiblingPulledKind = "sibling-pulled";

    private string ReviewerName(FlightRow flight) => $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{ReviewLeg}";

    /// <summary>A fresh reviewer on the flight's branch, the last one stopped; any earlier verdict's cards close.</summary>
    /// <param name="quiet">The caller is no command to tell: a reviewer that cannot start turns the review to Changes, with a card, as OpenPr always did.</param>
    private async Task StartReviewAsync(FlightRow flight, CancellationToken cancellationToken, bool quiet = false, string? who = null)
    {
        string? reviewedHead = null;
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        // A review loop held at its round limit starts no reviewer for the node or the watchtower: that round is the watchtower's word or the person's.
        var loopHeld = ReviewLoopHeld(flight);
        if (loopHeld && !IsPerson(flight, who))
        {
            if (quiet)
            {
                return;
            }
            throw new FlightException(RoundRefusal(flight));
        }
        RefuseIfStarting(ReviewKey(flight.Code), $"{flight.Code}'s reviewer", coalesce: true);
        var legs = state.Legs(flight.Code);
        if (legs.Where(l => l.State != LegState.Done).Select(l => l.Name).ToList() is { Count: > 0 } open)
        {
            throw new FlightException($"{flight.Code} has legs not done: {string.Join(", ", open)}; its review starts by itself once they land");
        }
        // The person's word at a held loop allows the round, recorded once the refusals above have passed.
        if (loopHeld)
        {
            AllowedByPerson(flight);
            flight = FlightOf(Verbs.FlightTarget(flight.Code));
        }
        if (await KeepPassAsync(flight, cancellationToken))
        {
            return;
        }
        // A spent 5-hour window holds the reviewer. The review is let go meanwhile, as a fresh one replaces it either way and an
        // old pass must not let the merge by a head nobody read: with its PR open and no review kept, the sweep starts it afresh.
        var held = Gate.Hold($"{Verbs.StartReview} {Verbs.FlightTarget(flight.Code)}", Models.StartLimitFor(flight.Repo), Models.WeekLimit, NextStepKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} waits to start its reviewer", Models.WeekPaceFor(flight.Repo));
        if (flight.Review?.ReviewerJob is { } last)
        {
            // One still reviewing is replaced without its verdict.
            if (flight.Review.State == ReviewState.Reviewing)
            {
                GaveUp(flight.Code, state.Sessions().FirstOrDefault(s => s.Id == last), "reviewer given up without a verdict");
            }
            DropQueuedJobsOf(last, "its reviewer was replaced");
            await StopQuietly(last, cancellationToken);
        }
        // The Opus cap and the repo mode hold it the same way (queued on the flight, its review let go meanwhile); the reviewer being
        // replaced does not count against its own successor.
        var reviewModel = Models.ReviewerModel(ReviewDepth.Standard);
        var waiting = !held && AdmissionWaits(AdmissionRole.Reviewer, reviewModel, flight, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ReviewerName(flight) });
        if (held || waiting)
        {
            if (flight.Review is not null)
            {
                state.PutFlight(flight with { Review = null });
                state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), held ? "its review waits for the 5-hour window" : "its review waits for a slot", ReviewChanges, PrReady, MergeFailed, PrChecks.AutopilotMergeRefused, LegWatch.ReviewStalled);
            }
            return;
        }

        var reviewer = ReviewerName(flight);
        var subject = Verbs.FlightTarget(flight.Code);
        var before = flight.Review?.ReviewerJob;
        ReviewPlan? plan = null;
        var baseMergeOnly = false;
        string? model = null;
        Launching(ReviewKey(flight.Code), $"{reviewer}, to review {flight.Code}", subject,
            new CardChoice { Label = $"Start {flight.Code}'s reviewer again", Verb = Verbs.StartReview, Target = subject },
            async entering =>
            {
                await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, CancellationToken.None);
                var commits = await git.RunAsync(["log", "--oneline", $"{flight.Base}..HEAD"], flight.Worktree, CancellationToken.None);
                var stat = await git.RunAsync(["diff", "--stat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                var numstat = await git.RunAsync(["diff", "--numstat", $"{flight.Base}...HEAD"], flight.Worktree, CancellationToken.None);
                plan = ReviewPlan.Choose(state.Usage(), Models.IsPriority(flight.Repo), numstat.Ok ? ReviewPlan.ParseNumstat(numstat.Output) : null, time.GetUtcNow());
                var again = await ReReviewOfAsync(flight, CancellationToken.None);
                // A re-review reads only the range since the last reviewed head, so the whole PR's size must not buy it a deep fan-out.
                if (again is not null && plan.Depth == ReviewDepth.Deep)
                {
                    plan = new ReviewPlan(ReviewDepth.Standard, "a re-review reads only what changed since the last round");
                }
                var brief = Briefs.Review(flight, legs, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""), Briefs.Run(Cli, Socket), plan, options.Machine, again, await PilotMdOfAsync(flight));
                // The head the reviewer reads, from the plane it reads: the pilot/review status goes on it, whatever the branch moves to.
                // GitHub can still report the head before a push for a moment, so it is only the fallback.
                var planeHead = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, CancellationToken.None);
                reviewedHead = planeHead.Ok && planeHead.Output.Trim() is { Length: > 0 } sha ? sha : await _marks.HeadAsync(flight, CancellationToken.None);
                // Only a base merge since the last review: no commit of the leg's own between the two heads (first parent only, or the base's commits the merge brought in count as own).
                if (flight.Review?.ReviewedHead is { Length: > 0 } was && reviewedHead is { Length: > 0 } now && was != now
                    && (await git.RunAsync(["merge-base", "--is-ancestor", was, now], flight.Worktree, CancellationToken.None)).Ok)
                {
                    var own = await git.RunAsync(["rev-list", "--first-parent", "--no-merges", "--count", $"{was}..{now}"], flight.Worktree, CancellationToken.None);
                    baseMergeOnly = own.Ok && own.Output.Trim() == "0";
                }
                // A re-review reads only what changed, on the leg's model; Opus is for the first review of a large diff (if the setting lets it).
                model = Models.ReviewerModelFor(plan.Depth, (flight.ReviewRounds?.Length ?? 0) + 1, legs.OrderBy(l => l.Order).LastOrDefault()?.Model);
                entering();
                return await workers.StartAsync(new WorkerStart(reviewer, flight.RepoPath, PlaneOf(flight), brief, model, Models.ReviewerEffort(plan.Depth), Reason: SessionStartReasons.FlightReview), CancellationToken.None);
            },
            job =>
            {
                // The flight as it is now: landed, or its review replaced meanwhile (a fresh start, a verdict), is no flight this
                // reviewer was started for.
                if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { State: not FlightState.Landed } now
                    || now.Review?.ReviewerJob != before || state.Legs(flight.Code).Any(l => l.State != LegState.Done) || plan is null)
                {
                    return $"{flight.Code} changed while {reviewer} was being started";
                }
                // A fresh review of a head past the reviewed one is a push that ends a hold (a pass kept over a base that only came in
                // starts no reviewer and keeps it): the hold's leg, or flight pr, is who pushed.
                var endsHold = EndHoldPastReview(now, reviewedHead, now.Holding?.By is { } leg && leg != "node" ? $"the {leg} leg" : "a push");
                if (endsHold)
                {
                    now = FlightOf(Verbs.FlightTarget(flight.Code));
                }
                state.PutFlight(now with
                {
                    Review = new ReviewRow
                    {
                        State = ReviewState.Reviewing, Reviewer = reviewer, ReviewerJob = job, StartedAt = time.GetUtcNow(), Depth = plan.Depth, DepthWhy = plan.Why, Model = model,
                        GoAround = legs.Sum(l => l.GoArounds), ReviewedHead = reviewedHead,
                    },
                });
                state.CloseCardsAbout(subject, "a fresh review started", ReviewChanges, ReviewLoopKind, PrReady, MergeFailed, PrChecks.AutopilotMergeRefused, LegWatch.ReviewStalled);
                if (baseMergeOnly)
                {
                    Ledger?.Mark(new WasteMark(WasteKinds.BaseMergeReview, flight.Code, null, null, null, 0, "head changed only by a base merge", time.GetUtcNow()));
                }
                return null;
            },
            after: async () =>
            {
                await MarkReadyForReviewAsync(flight);
                await _marks.ApplyAsync(flight.Code, CancellationToken.None);
            },
            quietFailure: quiet ? why => { ReviewNotStarted(flight.Code, $"the reviewer did not start: {why}"); return Task.CompletedTask; } : null,
            model: reviewModel);
    }

    /// <summary>The last round and what changed since its head, when that head is still in the plane; null reads the whole diff.</summary>
    private async Task<Briefs.ReReview?> ReReviewOfAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.ReviewRounds?.LastOrDefault() is not { Head: { Length: > 0 } head } last
            || !(await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{head}^{{commit}}"], flight.Worktree, cancellationToken)).Ok)
        {
            return null;
        }
        var commits = await git.RunAsync(["log", "--oneline", $"{head}..HEAD"], flight.Worktree, cancellationToken);
        var stat = await git.RunAsync(["diff", "--stat", $"{head}..HEAD"], flight.Worktree, cancellationToken);
        return commits.Ok && stat.Ok ? new Briefs.ReReview(last, new Briefs.PlaneState(commits.Output, stat.Output)) : null;
    }

    /// <summary>
    /// A flight whose review passed at an earlier head and whose new head has the same own diff against the base (only the base
    /// merged in between) keeps the pass: no reviewer starts, the marks move to the new head and a Handled line says so.
    /// </summary>
    private async Task<bool> KeepPassAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.Review is not { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } old } passed || flight.PullRequest is null)
        {
            return false;
        }
        await planes.EnsureAsync(flight.RepoPath, flight.Worktree, flight.Branch, cancellationToken);
        var headNow = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
        if (!headNow.Ok || headNow.Output.Trim() is not { Length: > 0 } head || string.Equals(head, old, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var before = await OwnDiffIdAsync(flight, old, cancellationToken);
        if (before is null || before != await OwnDiffIdAsync(flight, head, cancellationToken))
        {
            return false;
        }
        if (state.Flights().FirstOrDefault(f => f.Code == flight.Code) is not { Review: { State: ReviewState.Passed } } now)
        {
            return false;
        }
        // The kept round moves to the new head too, so the next re-review ranges past the base that came in, not over it.
        var rounds = now.ReviewRounds is [.. var earlier, { Verdict: ReviewState.Passed } keptRound] ? [.. earlier, keptRound with { Head = head }] : now.ReviewRounds;
        state.PutFlight(now with { Review = now.Review with { ReviewedHead = head, StatusState = null, MarksWhy = null }, ReviewRounds = rounds });
        var short8 = (string s) => CommitRefs.Short(s);
        state.RaiseCard(CardBin.Handled, "review-kept", Verbs.FlightTarget(flight.Code),
            $"{flight.Code}: review kept at {short8(head)}; it passed at {short8(old)} and the PR's own diff is the same (only the base came in), so no reviewer started");
        // No fresh review follows, so the hold (if the leg made one its own) is the watchtower's again.
        HoldForWatchtower(flight.Code, null);
        await _marks.ApplyAsync(flight.Code, cancellationToken);
        return true;
    }

    /// <summary>The patch-id of the PR's own diff at <paramref name="head"/>: the base...head diff without its index lines and hunk positions; null when git cannot say or the diff is empty.</summary>
    private async Task<string?> OwnDiffIdAsync(FlightRow flight, string head, CancellationToken cancellationToken)
    {
        var diff = await git.RunAsync(["diff", "--no-color", "--no-ext-diff", $"{flight.Base}...{head}"], flight.Worktree, cancellationToken);
        if (!diff.Ok || diff.Output.Trim().Length == 0)
        {
            return null;
        }
        var kept = diff.Output.Split('\n').Where(l => !l.StartsWith("index ", StringComparison.Ordinal)).Select(l => l.StartsWith("@@", StringComparison.Ordinal) ? "@@" : l.TrimEnd('\r'));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', kept))));
    }

    /// <summary>A reviewer that could not start costs the flight its review, not its merge: the card says so and the controller decides.</summary>
    private void ReviewNotStarted(string code, string why)
    {
        var row = FlightOf(Verbs.FlightTarget(code));
        state.PutFlight(row with { Review = new ReviewRow { State = ReviewState.Changes, Reviewer = ReviewerName(row), Report = why, StartedAt = time.GetUtcNow(), FinishedAt = time.GetUtcNow() } });
        RaiseReviewChanges(FlightOf(Verbs.FlightTarget(code)), why, SendBackReason.ReviewNotStarted);
    }

    private async Task ReviewAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        var args = command.Args ?? [];
        var verdict = args.Length > 0 ? args[0] : "";
        var report = string.Join(' ', args.Skip(1)).Trim();
        if (verdict is not ("pass" or "changes"))
        {
            throw new FlightException("review takes pass or changes, then a report");
        }
        if (flight.Review is not { } review)
        {
            throw new FlightException($"{flight.Code} has no review; flight pr starts one");
        }
        var fromReviewer = string.Equals(command.Who, review.Reviewer, StringComparison.Ordinal);
        if (fromReviewer && review.State != ReviewState.Reviewing)
        {
            throw new FlightException($"{flight.Code}'s review is {review.State.ToString().ToLowerInvariant()} already");
        }
        // Anyone else passes only a review that asked for changes: the controller's "ready anyway".
        if (!fromReviewer && !(verdict == "pass" && review.State == ReviewState.Changes))
        {
            throw new FlightException($"only {review.Reviewer} gives {flight.Code}'s verdict; the controller may only pass one that asked for changes");
        }
        // The watchtower's "ready anyway" is as much a way round the person as its go-around: past the cap the card is theirs.
        if (!fromReviewer && IsWatchtower(command.Who) && command.PersonsWord is null && state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault() is { ReviewGoArounds: >= MaxReviewGoArounds } capped)
        {
            throw new FlightException(
                $"{flight.Code}/{capped.Name} has gone around {capped.ReviewGoArounds} times on its review's findings: passing it over them is the person's call. Leave the card open for them");
        }
        if (report.Length == 0)
        {
            throw new FlightException("a verdict needs its report: what was checked, or the changes asked for");
        }
        var findings = ReviewFindings.Read(report);
        if (verdict == "changes" && !findings.HasBlocking)
        {
            throw new FlightException("changes needs at least one finding under `## Blocking`; with only not blocking findings (nits, suggestions) give `review pass` with them under `## Not blocking`: a pass with notes");
        }
        var notes = verdict == "pass" && fromReviewer && findings.NotBlocking.Count > 0 ? findings.NotBlocking : null;

        // On GitHub before the verdict is kept: a gh that fails leaves the review as it was, for the reviewer to say again,
        // never a pass whose PR is still a draft, or changes whose PR can still be merged with the fixes pending.
        // GitHub is asked with the lock let go (PIL338): the flight is this verdict's meanwhile, and the verdict is kept only if the
        // flight is found as it was.
        using var call = Calling(flight.Code, "being given its verdict");
        if (flight.PullRequest is { } number)
        {
            await OffLockAsync(flight, () => github.SetDraftAsync(flight.RepoPath, number, draft: verdict == "changes", cancellationToken));
        }

        var now = time.GetUtcNow();
        var kept = fromReviewer ? report : $"{report} (passed by {command.Who} over: {review.Report})";
        Record(flight.Code, f => f with
        {
            Review = f.Review is { } held ? held with { State = verdict == "pass" ? ReviewState.Passed : ReviewState.Changes, Report = kept, FinishedAt = now } : null,
            // Notes put the flight in a holding pattern; a pass or changes without notes ends any hold.
            Holding = notes is not null
                ? new FlightHolding { Reason = HoldingReason, Since = now, By = "node" }
                : null,
            ReviewRounds = f.Review is { } judged
                ? [.. f.ReviewRounds ?? [], new ReviewRound
                {
                    Round = (f.ReviewRounds?.Length ?? 0) + 1, Head = judged.ReviewedHead, Verdict = verdict == "pass" ? ReviewState.Passed : ReviewState.Changes,
                    Findings = kept, Depth = judged.Depth, Model = judged.Model ?? Models.ReviewerModel(judged.Depth ?? ReviewDepth.Standard),
                }]
                : f.ReviewRounds,
        });
        // On the PR now the verdict is kept: a mark GitHub refuses is a card, never a verdict lost.
        await OffLockAsync(null, () => _marks.ApplyAsync(flight.Code, cancellationToken));
        flight = FlightOf(command.Target);
        // A flagged flight's human checks are filed as issues for the controller, with the lock let go as the marks are: a refusal
        // is a Handled card, never a verdict lost or delayed. Only the reviewer's own report names them.
        if (fromReviewer && flight.HumanVerification && findings.HumanVerification.Count > 0)
        {
            var human = findings.HumanVerification;
            var seen = flight;
            await OffLockAsync(null, () => FileHumanChecksAsync(seen, human, cancellationToken));
            flight = FlightOf(command.Target);
        }
        if (verdict == "pass")
        {
            state.CloseCardsAboutBy(fromReviewer ? "node" : command.Who, Verbs.FlightTarget(flight.Code),
                !fromReviewer && command.Why?.Trim() is { Length: > 0 } passWhy ? $"passed anyway by {command.Who}: {passWhy}" : "passed",
                ReviewChanges, ReviewLoopKind, LegWatch.ReviewStalled);
            var reviewed = review.Depth is { } depth ? ReviewPlan.Phrase(depth, review.DepthWhy) : "Reviewed";
            // The verdict is kept already: a card that could not be raised (gh failing, the node stopping) is the sweep's to restore,
            // never a reason to throw the pass away (PIL682, 2026-11-03: a restart mid-verdict left a passed flight with no merge card).
            try
            {
                await RaisePrReadyAsync(flight, kept, notes is null ? reviewed : $"{reviewed}, passed with {notes.Count} {(notes.Count == 1 ? "note" : "notes")}", cancellationToken);
            }
            catch (Exception ex) when (ex is FlightException or OperationCanceledException)
            {
                await Error.WriteLineAsync($"review: {flight.Code}'s PR-ready card was not raised, the next look restores it: {ex.Message}");
            }
            if (notes is not null)
            {
                // The verdict is kept and given to GitHub: the flight is let go (Dispose is safe twice), as plan-leg refuses a flight with a call out.
                call.Dispose();
                var fixer = await PlanReviewNotesLegAsync(flight, notes, cancellationToken);
                RaiseReviewNotes(FlightOf(command.Target), notes, fixer);
            }
        }
        else if (ReviewLoopHeld(flight))
        {
            RaiseReviewLoop(flight);
        }
        else
        {
            RaiseReviewChanges(flight, report);
        }

        // A reviewer that gave its verdict is done with, after a grace to end its turn, as a worker that finished its own leg.
        if (fromReviewer && review.ReviewerJob is { } job)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                DropQueuedJobsOf(job, "its reviewer was done");
                await StopQuietly(job, CancellationToken.None);
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// A PR review's approve of a flight's PR, taken as the flight's own verdict (PIL483): at the head the flight's PR has now, the
    /// flight's review passes, even one that asked for changes, its PR is marked ready and its PR-ready card raised. A head the
    /// PR has moved past, a landed flight or a flight with no review is left as it is. Null when taken; else why not.
    /// </summary>
    public async Task<string?> TakePrVerdictAsync(string code, string head, string verdict, string report, CancellationToken cancellationToken)
    {
        if (verdict != "approve")
        {
            return $"only an approve passes {code}'s review";
        }
        var call = await EnterAsync(code, "being given a PR review's verdict", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Review: { } review } flight)
            {
                return $"{code} is landed or has no review";
            }
            if (review.State == ReviewState.Passed)
            {
                return null;
            }
            var current = await OffLockAsync(flight, () => github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken));
            if (current is not { State: "OPEN" } || !string.Equals(current.Head, head, StringComparison.OrdinalIgnoreCase))
            {
                return $"{code}'s PR is not open at {CommitRefs.Short(head)}";
            }
            flight = FlightOf(Verbs.FlightTarget(code));
            if (flight.PullRequest is { } number)
            {
                await OffLockAsync(flight, () => github.SetDraftAsync(flight.RepoPath, number, draft: false, cancellationToken));
            }
            var kept = $"{report} (approved by a PR review at {CommitRefs.Short(head)})";
            Record(code, f => f with { Review = f.Review is { } held ? held with { State = ReviewState.Passed, Report = kept, FinishedAt = time.GetUtcNow(), ReviewedHead = head } : null });
            await OffLockAsync(null, () => _marks.ApplyAsync(code, cancellationToken));
            flight = FlightOf(Verbs.FlightTarget(code));
            state.CloseCardsAbout(Verbs.FlightTarget(code), "passed by a PR review", ReviewChanges, LegWatch.ReviewStalled);
            await RaisePrReadyAsync(flight, kept, review.Depth is { } depth ? ReviewPlan.Phrase(depth, review.DepthWhy) : "Reviewed", cancellationToken);
            return null;
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    /// <summary>
    /// A passed review taken as asking for <paramref name="changes"/>: the PR goes back to draft, which GitHub will not merge, and
    /// the flight's review turns to Changes, so the leg's finish pushes and reviews afresh. A new go-around's changes are its own
    /// COMMENT review, so the one that passed is not the last word on the PR. Called under the commander's lock with the flight
    /// taken (<see cref="Calling"/>), and lets the lock go for GitHub; a flight found changed is refused, and nothing is recorded.
    /// </summary>
    private async Task TurnToChangesAsync(FlightRow flight, string changes, CancellationToken cancellationToken)
    {
        var number = flight.PullRequest!.Value;
        await OffLockAsync(flight, () => github.SetDraftAsync(flight.RepoPath, number, draft: true, cancellationToken));
        Record(flight.Code, f => f with { Review = f.Review is { } held ? held with { State = ReviewState.Changes, Report = changes, FinishedAt = time.GetUtcNow(), ReviewUrl = null, MarksWhy = null } : null });
        await OffLockAsync(null, () => _marks.ApplyAsync(flight.Code, cancellationToken));
    }

    private async Task<string?> SendBackAsync(string code, Func<int, string> changesFor, string closing, CancellationToken cancellationToken, SendBackReason reason = SendBackReason.Findings)
    {
        // The flight is this send-back's while GitHub is asked, as a rebase's: one under way on it is waited for, without the lock.
        var call = await EnterAsync(code, "being sent back for changes", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, Review: { State: ReviewState.Passed }, PullRequest: { } number } flight)
            {
                return $"{code} is not a flying flight with a passed review";
            }
            var changes = changesFor(number);
            await TurnToChangesAsync(flight, changes, cancellationToken);
            state.CloseCardsAbout(Verbs.FlightTarget(code), closing, PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
            RaiseReviewChanges(FlightOf(Verbs.FlightTarget(code)), changes, reason);
            return null;
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    /// <summary>Every leg done, reviewed, the PR open: the merge is the controller's, a gate before an irreversible step.</summary>
    private async Task RaisePrReadyAsync(FlightRow flight, string review, string reviewed, CancellationToken cancellationToken)
    {
        var holding = flight.Holding is not null;
        var holdReason = flight.Holding?.Reason ?? HoldingReason;
        var pr = await OffLockAsync(null, () => github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken))
            ?? throw new FlightException($"{flight.Branch} has no PR");
        var legs = state.Legs(flight.Code);
        state.RaiseCard(MergeCardBin(), PrReady, Verbs.FlightTarget(flight.Code),
            holding ? $"{flight.Code}'s PR #{pr.Number} is holding: {holdReason}" : $"{flight.Code}'s PR #{pr.Number} is ready to merge",
            $"{flight.Title ?? flight.Item.Noun}: {legs.Length} {(legs.Length == 1 ? "leg" : "legs")} done, head {CommitRefs.Short(pr.Head)}. {reviewed}: {review}."
                + (holding ? " The merge is held until the watchtower clears it or the fixes are pushed (a new review then gates it)." : ""),
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [pr.Head] }], link: pr.Url,
            summary: holding
                ? $"{flight.Code} passed its review with notes and circles in a holding pattern: {holdReason}. Merge is refused until it is cleared or the fixes are pushed."
                : $"{flight.Code} is reviewed and its PR #{pr.Number} is ready. Merge lands it; dismissing leaves the PR open.");
    }

    /// <summary>The Handled card kind that says a passed flight's PR-ready card was restored by the sweep.</summary>
    public const string PrReadyRestoredKind = "pr-ready-restored";

    /// <summary>
    /// A flight whose review passed at <paramref name="head"/>, every leg done and its PR open, but with no merge card (the node
    /// went away between the stored verdict and the card, PIL682): its PR-ready card is raised as the verdict would have, holding
    /// flights getting the holding card and their notes card. A flight whose PR-ready card for that head was closed (dismissed,
    /// answered, merged into a queue) is left alone. True when a card was raised.
    /// </summary>
    internal async Task<bool> RestorePrReadyAsync(string code, string head, CancellationToken cancellationToken)
    {
        var call = await EnterAsync(code, "having its PR-ready card restored", cancellationToken);
        try
        {
            var subject = Verbs.FlightTarget(code);
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, PullRequest: not null, Review: { State: ReviewState.Passed, ReviewedHead: { } reviewed } review, MergeWhenGreen: null } flight
                || !string.Equals(reviewed, head, StringComparison.OrdinalIgnoreCase)
                || state.Legs(code) is not { Length: > 0 } flown || flown.Any(l => l.State != LegState.Done))
            {
                return false;
            }
            // Any merge card of this head, open or closed: a person's dismissal, an answered or queued merge, is not undone.
            if (state.Cards(openOnly: false).Any(c => c.Subject == subject && c.Kind is PrReady or MergeFailed
                && (c.ClosedAt is null ||c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Args.FirstOrDefault() is { } h && string.Equals(h, head, StringComparison.OrdinalIgnoreCase)))))
            {
                return false;
            }
            var report = flight.ReviewRounds is { Length: > 0 } rounds && !string.IsNullOrWhiteSpace(rounds[^1].Findings) ? rounds[^1].Findings : review.Report ?? "passed";
            var notes = flight.Holding is not null ? ReviewFindings.Read(report).NotBlocking : [];
            var phrase = review.Depth is { } depth ? ReviewPlan.Phrase(depth, review.DepthWhy) : "Reviewed";
            await RaisePrReadyAsync(flight, report, notes.Count > 0 ? $"{phrase}, passed with {notes.Count} {(notes.Count == 1 ? "note" : "notes")}" : phrase, cancellationToken);
            if (flight.Holding is not null && notes.Count > 0 && !state.Cards(openOnly: true).Any(c => c.Subject == subject && c.Kind == ReviewNotesKind))
            {
                RaiseReviewNotes(FlightOf(subject), notes);
            }
            state.RaiseCard(CardBin.Handled, PrReadyRestoredKind, subject, $"{code}'s PR-ready card was restored",
                "its review passed but no merge card was open (the node went away between the verdict and the card)");
            return true;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    /// <summary>Whether a command comes from this node's watchtower, which the person's own words are not.</summary>
    private bool IsWatchtower(string? who) =>
        string.Equals(who, $"{options.Machine.ToLowerInvariant()}-watchtower", StringComparison.Ordinal);

    /// <summary>How many times the watchtower may send a done leg round on its review's findings before the card is the person's.</summary>
    public const int MaxReviewGoArounds = LegRow.MaxReviewGoArounds;

    /// <summary>
    /// A pass with notes: the not blocking findings go to the watchtower as a card, to make the small ones in the plane and
    /// commit them. No leg goes round, and the merge card stays as it is.
    /// </summary>
    private void RaiseReviewNotes(FlightRow flight, IReadOnlyList<string> notes, string? fixerLeg = null)
    {
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "its review passed again", ReviewNotesKind);
        var action = fixerLeg is not null
            ? $"The node planned {flight.Code}/{fixerLeg} to make them (autopilot); it starts as any planned leg and the review runs again on its result. Nothing to do but watch it."
            : $"Delegate: plan a review-notes leg carrying the notes (pilot leg plan, then pilot leg start), or pilot flight clear {flight.Code} <why> when they are not worth a round, within a few minutes, and say which here. "
                + $"A fix of your own only for a one-line change in the plane ({flight.Worktree}), then pilot flight pr {flight.Code}.";
        state.RaiseCard(CardBin.ForYou, ReviewNotesKind, Verbs.FlightTarget(flight.Code), $"{flight.Code}'s review passed with notes",
            $"PR #{flight.PullRequest}, head {(flight.Review?.ReviewedHead is { } head ? CommitRefs.Short(head) : "unknown")}. Notes: {string.Join("; ", notes)} {action}",
            link: flight.PullRequestUrl,
            summary: CardSummaries.ReviewNotes(flight.Code, notes, fixerLeg));
    }

    /// <summary>The name a review-notes leg is planned under: <c>review-notes</c>, then <c>review-notes-2</c> and on for a later round.</summary>
    public const string ReviewNotesLeg = "review-notes";

    /// <summary>
    /// On autopilot, a pass with notes plans a review-notes leg by the same path as leg plan: one checklist item per note and a
    /// commit step, the check of the flight's last leg so the rerun covers the same build. The sweep starts it as any planned leg.
    /// Planned while the review is Passed and a PR is open: plan-leg has no guard against either (more work on a flight whose merge
    /// is armed takes the merge back), so nothing is bypassed. No second leg is planned while one is planned or flying.
    /// Returns the leg's name, or null when none was planned (autopilot off, or one is already on its way).
    /// </summary>
    private async Task<string?> PlanReviewNotesLegAsync(FlightRow flight, IReadOnlyList<string> notes, CancellationToken cancellationToken)
    {
        // Advances is on for every real node; the owner's autopilot setting is what this waits on.
        if (!Models.Advances || !Models.Autopilot)
        {
            return null;
        }
        var legs = state.Legs(flight.Code);
        if (legs.FirstOrDefault(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) && l.State is LegState.Planned or LegState.Flying) is { } onWay)
        {
            return onWay.Name;
        }
        var taken = legs.Count(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal));
        var name = taken == 0 ? ReviewNotesLeg : $"{ReviewNotesLeg}-{taken + 1}";
        var clean = notes.Select(n => n.Trim().TrimStart('-', '*', ' ').ReplaceLineEndings(" ")).Where(n => n.Length > 0).ToList();
        var args = new List<string>
        {
            $"name={name}",
            "goal=make the review's not-blocking notes",
            $"done-when=Each of the review's not-blocking notes is made or answered in a commit: {string.Join("; ", clean)}. The check passes.",
        };
        if (legs.OrderBy(l => l.Order).LastOrDefault(l => l.Check is not null)?.Check is { } check)
        {
            args.Add($"check={check}");
        }
        args.AddRange(clean.Select(n => $"item={n}"));
        args.Add("item=Commit the changes with a message that names the notes");
        try
        {
            await PlanAsync(new CommandRequest { Verb = Verbs.PlanLeg, Target = Verbs.FlightTarget(flight.Code), Who = "node", Args = [.. args] }, cancellationToken);
            return name;
        }
        catch (FlightException ex)
        {
            await Error.WriteLineAsync($"review: {flight.Code}'s review-notes leg was not planned, the watchtower is told instead: {ex.Message}");
            return null;
        }
    }

    /// <summary>The Handled card kind that says a flight was cleared to land.</summary>
    public const string HoldingClearedKind = "holding-cleared";

    /// <summary>Why a flight whose review passed with notes holds its merge.</summary>
    public const string HoldingReason = "the watchtower is making the review's small fixes";

    /// <summary>
    /// The watchtower's word, or the person's, that a flight in a holding pattern is cleared to land: the hold ends, the notes card
    /// closes and a Handled card says so. Autopilot then merges it at its next look as for any passed review.
    /// </summary>
    private void ClearHolding(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (!IsWatchtower(command.Who) && !IsPerson(flight, command.Who))
        {
            throw new FlightException("only the watchtower or the person may clear a flight in a holding pattern");
        }
        if (flight.Holding is null)
        {
            throw new FlightException($"{flight.Code} is not in a holding pattern: nothing to clear");
        }
        ReleaseHolding(flight, command.Who, string.Join(' ', command.Args ?? []).Trim());
    }

    /// <summary>The hold's reason while a review-notes leg flies on the flight.</summary>
    internal static string LegHoldingReason(string leg) => $"the {leg} leg is making the review's fixes";

    /// <summary>
    /// A leg started on a flight in a holding pattern is what the hold waits on: its reason and its <c>By</c> name the leg, so the
    /// hold says what it really waits on, and its watch covers the wait (no timeout card rings while it flies).
    /// </summary>
    private void HoldForLeg(string code, string leg, string? reason = null)
    {
        if (FlightOf(Verbs.FlightTarget(code)).Holding is { } hold)
        {
            Record(code, f => f with { Holding = hold with { Reason = reason ?? LegHoldingReason(leg), By = leg } });
        }
    }

    /// <summary>A hold whose leg ended with nothing pushed goes back to the watchtower's own reason.</summary>
    private void HoldForWatchtower(string code, string? leg)
    {
        if (FlightOf(Verbs.FlightTarget(code)).Holding is { } hold && hold.By is { } by && by != "node" && (leg is null || string.Equals(by, leg, StringComparison.Ordinal)))
        {
            Record(code, f => f with { Holding = hold with { Reason = HoldingReason, By = "node" } });
        }
    }

    /// <summary>
    /// A push of a head past the one the review read ends a flight's hold and closes its review-notes card saying who pushed (the leg,
    /// flight pr, the PR's open): the fresh review and checks gate the merge as for any push. <paramref name="before"/> is the flight as it
    /// was before the review moved. True when the hold ended.
    /// </summary>
    private bool EndHoldPastReview(FlightRow before, string? pushedHead, string by)
    {
        if (before is not { Holding: not null, Review.ReviewedHead: { Length: > 0 } reviewedHead } || string.IsNullOrEmpty(pushedHead)
            || string.Equals(reviewedHead, pushedHead, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        Record(before.Code, f => f with { Holding = null });
        state.CloseCardsAbout(Verbs.FlightTarget(before.Code), $"the fixes were pushed by {by}", ReviewNotesKind);
        return true;
    }

    /// <summary>
    /// Whether the flight's review reads a head the plane has left: it asked for changes, or it passed (with notes or plainly) at a head
    /// that is not the plane's now. False with no PR open.
    /// </summary>
    private async Task<bool> ReviewBehindPlaneAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.PullRequest is null)
        {
            return false;
        }
        if (flight.Review is { State: ReviewState.Changes })
        {
            return true;
        }
        if (flight.Review is not { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } read })
        {
            return false;
        }
        var head = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
        return head.Ok && head.Output.Trim() is { Length: > 0 } sha && !string.Equals(sha, read, StringComparison.OrdinalIgnoreCase);
    }

    private void ReleaseHolding(FlightRow flight, string? who, string why)
    {
        Record(flight.Code, f => f with { Holding = null });
        state.CloseCardsAboutBy(who ?? "node", Verbs.FlightTarget(flight.Code), $"{who} cleared it to land{(why.Length > 0 ? $": {why}" : "")}", ReviewNotesKind, HoldingTimeoutKind);
        state.RaiseCard(CardBin.Handled, HoldingClearedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} is cleared to land",
            why.Length > 0 ? $"{who}: {why}" : $"cleared by {who}");
    }

    /// <summary>The Ring card kind of a holding flight the watchtower has not cleared in time, or whose watchtower is not live.</summary>
    public const string HoldingTimeoutKind = "holding-timeout";

    /// <summary>The third merge argument of the holding-timeout card's Merge as it is: the person's word that the hold ends with the merge.</summary>
    public const string MergeAsIs = "as-is";

    /// <summary>The card kind of a pass with notes, for the watchtower to make the small ones.</summary>
    public const string ReviewNotesKind = "review-notes";

    /// <summary>The card kind of a review loop held at its round limit, for the watchtower to settle.</summary>
    public const string ReviewLoopKind = "review-loop";

    /// <summary>The round at which a review that still asks for changes is held for the watchtower: the third.</summary>
    public const int ReviewLoopRound = 3;

    private static int RoundsAllowed(FlightRow flight) => Math.Max(ReviewLoopRound, flight.ReviewRoundsAllowed ?? 0);

    /// <summary>The rounds whose verdict asked for changes: a pass spends none of the rounds a loop may take.</summary>
    private static int ChangesRounds(FlightRow flight) => flight.ReviewRounds?.Count(r => r.Verdict == ReviewState.Changes) ?? 0;

    /// <summary>
    /// Whether a flight's review has used the rounds with changes it may have without the watchtower's word, and the reviewer's own
    /// latest verdict still asks for changes. A review the node turned back to Changes after a pass (a red check, a merge taken back)
    /// ends on a passed round and is not held.
    /// </summary>
    private static bool ReviewLoopHeld(FlightRow flight) =>
        flight.Review is { State: ReviewState.Changes } && flight.ReviewRounds is [.., { Verdict: ReviewState.Changes }]
        && ChangesRounds(flight) >= RoundsAllowed(flight);

    /// <summary>
    /// Whether a sender may start a held round or allow one: the person, as the node, the lookout of legs, the watchtower and the
    /// flight's own reviewer and workers may not.
    /// </summary>
    private bool IsPerson(FlightRow flight, string? who) =>
        !string.IsNullOrWhiteSpace(who) && who != "node" && who != LegWatch.Who && !IsWatchtower(who)
        && !string.Equals(who, ReviewerName(flight), StringComparison.Ordinal)
        && !state.Legs(flight.Code).Any(l => string.Equals(who, WorkerName(flight, l), StringComparison.Ordinal));

    /// <summary>The person's own go-around or start-review at a held loop is their word for one more round: recorded, and the loop card closed.</summary>
    private void AllowedByPerson(FlightRow flight)
    {
        var rounds = ChangesRounds(flight);
        Record(flight.Code, f => f with { ReviewRoundsAllowed = rounds + 1 });
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "the person allowed one more round", ReviewLoopKind);
    }

    private static string RoundRefusal(FlightRow flight) =>
        $"{flight.Code}'s review has had {ChangesRounds(flight)} rounds with changes: another round is the watchtower's call (`allow-round`), or the person's";

    /// <summary>The watchtower's word, or the person's, that a held review loop may have exactly one more round.</summary>
    private void AllowRound(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (!IsWatchtower(command.Who) && !IsPerson(flight, command.Who))
        {
            throw new FlightException("only the watchtower or the person may allow another review round");
        }
        if (!ReviewLoopHeld(flight))
        {
            throw new FlightException($"{flight.Code}'s review is not held at its round limit: nothing to allow");
        }
        var rounds = ChangesRounds(flight);
        Record(flight.Code, f => f with { ReviewRoundsAllowed = rounds + 1 });
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{command.Who} allowed one more round", ReviewLoopKind);
        RaiseReviewChanges(FlightOf(command.Target), flight.Review!.Report ?? "");
    }

    /// <summary>
    /// A review that still asks for changes at its third round: the node sends no leg round and starts no reviewer. The watchtower
    /// gets each round's findings, and passes with notes, settles the disagreement in the plane, rings the person, or allows one more round.
    /// </summary>
    private void RaiseReviewLoop(FlightRow flight)
    {
        var rounds = flight.ReviewRounds ?? [];
        var detail = new System.Text.StringBuilder();
        foreach (var round in rounds)
        {
            detail.AppendLine($"Round {round.Round} ({round.Verdict.ToString().ToLowerInvariant()}{(round.Head is { Length: > 0 } head ? $", head {CommitRefs.Short(head)}" : "")}):");
            detail.AppendLine(round.Findings);
        }
        var target = Verbs.FlightTarget(flight.Code);
        state.CloseCardsAbout(target, "its review loop was held again", ReviewLoopKind);
        state.RaiseCard(CardBin.ForYou, ReviewLoopKind, target, $"{flight.Code}'s review is in a loop: {rounds.Length} rounds with changes",
            detail.ToString().TrimEnd(),
            [
                new CardChoice { Label = "Pass with notes", Verb = Verbs.Review, Target = target, Args = ["pass", "passed with the findings noted: repeated or not blocking"] },
                new CardChoice { Label = "Allow one more round", Verb = Verbs.AllowRound, Target = target, Args = ["the watchtower allowed it"] },
            ], link: flight.PullRequestUrl,
            summary: $"The review of {flight.Code} (PR {flight.PullRequestUrl}) has asked for changes {rounds.Length} times. The node sends no leg round and starts no reviewer: the watchtower passes it with the findings noted, settles the disagreement in the plane ({flight.Worktree}), rings the person for what it cannot settle, or allows one more round.");
    }

    /// <summary>
    /// Why a done leg is sent back: what its card is worded for (the review's findings, failed checks, or a reviewer that did not start).
    /// A conflict is not sent back: it goes to the watchtower (<see cref="ConflictsAsync"/>).
    /// </summary>
    private enum SendBackReason
    {
        Findings,
        ChecksFailed,
        ReviewNotStarted,
    }

    private void RaiseReviewChanges(FlightRow flight, string changes, SendBackReason reason = SendBackReason.Findings)
    {
        var last = state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault();
        var leg = last is null ? flight.Code : $"{flight.Code}/{last.Name}";
        // Each card says what happened, not a review's findings it did not have (the review of #453): a check that failed and a
        // reviewer that never started raise this card too.
        var firstLine = changes.Split('\n', 2)[0].Trim();
        var because = firstLine.Length > 0 ? $": {Clip(firstLine)}" : ": nothing was written down, see the PR's review";
        var (title, words, said, anyway) = reason switch
        {
            SendBackReason.ChecksFailed => ($"{flight.Code}'s PR fails its checks{because}", "Fix the failing checks", $"The PR of {leg} fails its checks.", $"Land {flight.Code} with its checks failing"),
            SendBackReason.ReviewNotStarted => ($"{flight.Code}'s review did not start{because}", $"Start {flight.Code}'s review again", $"The reviewer of {leg} did not start, so nothing was reviewed.", $"Land {flight.Code} without a review"),
            _ => ($"{flight.Code}'s review asks for changes{because}", "Fix the review's findings", $"The review of {leg} found things to fix.", $"Land {flight.Code} with the review's findings unfixed"),
        };
        // Said on the card's detail once the watchtower may no longer answer it, so the person sees why it waits on them. Not in
        // the go-around's args, which become the next worker's brief: that worker is told what the review asked, not who decides.
        var detail = changes;
        // Not for a review that did not start: nothing is sent round from that card, so there is no round to decide on.
        if (last is { ReviewGoArounds: >= MaxReviewGoArounds } && reason != SendBackReason.ReviewNotStarted)
        {
            // The findings first: a card shows its first lines, and this note there hid the problem it was about (PIL294's #357, 2026-10-27).
            detail = $"{changes}\n{last.Name} has been sent back {last.ReviewGoArounds} times: this is yours to decide ({words} once more, mark it ready, or change what is asked).";
        }
        var choices = new List<CardChoice>();
        if (reason == SendBackReason.ReviewNotStarted)
        {
            // Nothing was found, so nothing is sent round: the review is started again.
            choices.Add(new CardChoice { Label = words, Verb = Verbs.StartReview, Target = Verbs.FlightTarget(flight.Code) });
        }
        else if (last is not null)
        {
            // Not "Redo leg": the worker fixes what was found on the leg's work as it stands; nothing is thrown away (the owner, 2026-10-29).
            choices.Add(new CardChoice { Label = Models.SendBackLabel(last, words), Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, last.Name), Args = [reason == SendBackReason.Findings ? $"the review asked for changes: {changes}" : changes] });
        }
        choices.Add(new CardChoice { Label = anyway, Verb = Verbs.Review, Target = Verbs.FlightTarget(flight.Code), Args = ["pass", "ready anyway"] });
        state.RaiseCard(CardBin.Ring, ReviewChanges, Verbs.FlightTarget(flight.Code), title, detail, choices, link: flight.PullRequestUrl,
            summary: reason == SendBackReason.ReviewNotStarted || last is null
                ? $"{said} {(reason == SendBackReason.ReviewNotStarted ? $"{words} starts it again; " : "")}{anyway} takes it as it is."
                : $"{said} {words} has a worker do it on the leg's work as it stands, nothing thrown away; {anyway} takes it as it is.");
    }
}
