using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The <see cref="PilotMarks.Recheck"/> label (#286): a person puts it on any open PR of a repo the node tracks to have pilot
/// look at it again, whoever worked on it. Every <see cref="Interval"/> the node lists each tracked GitHub repo's open PRs,
/// and a labelled one is this node's when it holds the flight on the PR's branch, else its own review of the PR, else no peer
/// node of the LAN does (a peer that does not answer holds nothing: whichever node sees the label first takes it).
/// One recheck of a PR runs at a time. A label seen again after the recheck consumed it runs it once more, and one put back
/// while it runs runs it once more after it, never two at once. A recheck that leaves the label on (it could not run, or a peer
/// has the PR) is not run again until the label has been seen gone, so a PR that cannot be rechecked is not tried every look.
/// </summary>
public sealed class PrRecheck(NodeState state, IGitHub github, PrReviews reviews, RepoCatalog repos, TimeProvider time, LegCommander? legs = null) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The Handled line a recheck leaves, saying what it found.</summary>
    public const string HandledKind = "pr-recheck";

    /// <summary>The For-you card said when a recheck could not run; its label is kept.</summary>
    public const string FailedKind = "pr-recheck-failed";

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The nodes this one reaches; null, or none held, leaves the PR to this node.</summary>
    public IFleet? Fleet { get; set; }

    private enum Outcome
    {
        /// <summary>The label is gone or replaced: the recheck is over.</summary>
        Consumed,

        /// <summary>The label stays on the PR, and is not acted on again until it has been seen gone.</summary>
        Kept,
    }

    private sealed class Entry(string repo, string path, int number)
    {
        public string Repo { get; } = repo;

        public string Path { get; } = path;

        public int Number { get; } = number;

        public bool Running;

        public bool Again;

        public bool Stale;

        // Seen gone during a run: a label seen again after that was put back.
        public bool GoneSeen;

        public Task? Task;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A PR as the Handled line says it: <c>pilot#253</c>.</summary>
    private static string Short(string repo, int number) => $"{repo[(repo.IndexOf('/') + 1)..]}#{number}";

    /// <summary>One look at the open PRs of every tracked repo; a recheck it starts runs on, see <see cref="WhenIdleAsync"/>.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in repos.Repos().Where(r => r.GitHub is not null))
        {
            // Two clones of one repo are one look.
            if (!seen.Add(repo.GitHub!))
            {
                continue;
            }
            int[] listed;
            try
            {
                listed = await github.OpenPullRequestsLabelledAsync(repo.Path, PilotMarks.Recheck, cancellationToken);
            }
            catch (FlightException ex)
            {
                // Not listed is not unlabelled: what a recheck knows of its label stays as it was.
                await Error.WriteLineAsync($"pr recheck: {repo.GitHub}'s PRs could not be listed: {ex.Message}");
                continue;
            }
            var labelled = listed.ToHashSet();
            var start = new List<Entry>();
            lock (_gate)
            {
                foreach (var gone in _entries.Values.Where(e => string.Equals(e.Repo, repo.GitHub, StringComparison.OrdinalIgnoreCase) && !labelled.Contains(e.Number)).ToList())
                {
                    Seen(gone, present: false);
                    if (!gone.Running)
                    {
                        _entries.Remove(Key(gone.Repo, gone.Number));
                    }
                }
                foreach (var number in labelled)
                {
                    var key = Key(repo.GitHub!, number);
                    if (!_entries.TryGetValue(key, out var entry))
                    {
                        _entries[key] = entry = new Entry(repo.GitHub!, repo.Path, number);
                    }
                    if (Seen(entry, present: true))
                    {
                        start.Add(entry);
                    }
                }
            }
            foreach (var entry in start)
            {
                var task = Task.Run(() => RunAsync(entry, cancellationToken), CancellationToken.None);
                lock (_gate)
                {
                    entry.Task = task;
                }
            }
        }
    }

    private static string Key(string repo, int number) => $"{repo}#{number}";

    /// <summary>What a look found of one PR's label; true when a recheck is to start now. Held under the gate.</summary>
    private static bool Seen(Entry entry, bool present)
    {
        if (!present)
        {
            entry.Stale = false;
            entry.GoneSeen = entry.Running;
            return false;
        }
        if (entry.Running)
        {
            if (entry.GoneSeen)
            {
                entry.Again = true;
                entry.GoneSeen = false;
            }
            return false;
        }
        if (entry.Stale)
        {
            return false;
        }
        entry.Running = true;
        return true;
    }

    /// <summary>Waits until no recheck runs, the ones a running one asked for after it included.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = [.. _entries.Values.Where(e => e.Running).Select(e => e.Task).OfType<Task>()];
            }
            if (running.Length == 0)
            {
                return;
            }
            await Task.WhenAll(running);
        }
    }

    private async Task RunAsync(Entry entry, CancellationToken cancellationToken)
    {
        while (true)
        {
            var outcome = Outcome.Kept;
            try
            {
                outcome = await RecheckAsync(entry, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    entry.Running = false;
                }
                return;
            }
            catch (Exception ex)
            {
                Fail(entry, ex.Message, null);
            }
            lock (_gate)
            {
                if (entry.Again)
                {
                    entry.Again = false;
                    entry.Stale = false;
                    continue;
                }
                entry.Running = false;
                entry.Stale = outcome == Outcome.Kept;
                return;
            }
        }
    }

    private async Task<Outcome> RecheckAsync(Entry entry, CancellationToken cancellationToken)
    {
        PullRequest pr;
        try
        {
            pr = await github.PullRequestOfAsync(entry.Repo, entry.Number, cancellationToken);
        }
        catch (FlightException ex)
        {
            Fail(entry, ex.Message, null);
            return Outcome.Kept;
        }

        if (OwnFlight(entry) is { } flight)
        {
            return await ForFlightAsync(entry, flight, pr, cancellationToken);
        }
        if (OwnReview(entry) is { } review)
        {
            return await ForReviewedAsync(entry, review, pr, cancellationToken);
        }
        if (await PeerHasAsync(entry, cancellationToken) is { } peer)
        {
            // Its node looks at the same label and takes it; this one leaves it there.
            await Error.WriteLineAsync($"pr recheck: {Short(entry.Repo, entry.Number)} is {peer}'s, left to it");
            return Outcome.Kept;
        }

        if (!string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            await github.RemoveLabelAsync(entry.Path, entry.Number, PilotMarks.Recheck, cancellationToken);
            Handled(entry, $"already {pr.State.ToLowerInvariant()}", pr.Url);
            return Done(entry);
        }
        if (await github.MergeQueueAsync(entry.Path, entry.Number, cancellationToken) is { State: "OPEN", Queued: true })
        {
            await github.SetPilotLabelAsync(entry.Path, entry.Number, PilotMarks.Queued, cancellationToken);
            Handled(entry, "already queued", pr.Url);
            return Done(entry);
        }

        // A PR pilot never touched: reviewed as 'pilot review pr' does.
        var subject = Verbs.PrTarget(entry.Repo, entry.Number);
        var started = await reviews.RunAsync(new CommandRequest { Verb = Verbs.ReviewPr, Target = subject, Who = "node" }, cancellationToken);
        if (!started.Ok)
        {
            Fail(entry, started.Error ?? "the review did not start", pr.Url);
            return Outcome.Kept;
        }
        await github.SetPilotLabelAsync(entry.Path, entry.Number, PilotMarks.Reviewing, cancellationToken);
        Handled(entry, "review started", pr.Url);
        return Done(entry);
    }

    private static string Short8(string sha) => sha[..Math.Min(8, sha.Length)];

    private FlightRow? Now(string code) => state.Flights().FirstOrDefault(f => f.Code == code);

    /// <summary>The label a flight's review stands at; null while it holds none (its reviewer waits for the 5-hour window).</summary>
    private string? LabelOf(string code) => Now(code)?.Review?.State switch
    {
        ReviewState.Passed => PilotMarks.Passed,
        ReviewState.Changes => PilotMarks.Changes,
        ReviewState.Reviewing => PilotMarks.Reviewing,
        _ => null,
    };

    /// <summary>Says what the recheck did and leaves the PR with the one <c>pilot:</c> label for where it stands, in place of the recheck's.</summary>
    private async Task<Outcome> FinishAsync(Entry entry, PullRequest pr, string said, string? label, CancellationToken cancellationToken)
    {
        Handled(entry, said, pr.Url);
        try
        {
            if (label is null)
            {
                await github.RemoveLabelAsync(entry.Path, entry.Number, PilotMarks.Recheck, cancellationToken);
            }
            else
            {
                await github.SetPilotLabelAsync(entry.Path, entry.Number, label, cancellationToken);
            }
        }
        catch (FlightException ex)
        {
            Fail(entry, $"{said}, but its label could not be set ({ex.Message})", pr.Url);
            return Outcome.Kept;
        }
        return Done(entry);
    }

    /// <summary>
    /// A recheck on a PR with a flight of this node's, GitHub read afresh and one thing done, in this order: a conflict or a PR
    /// behind its base is rebased as the queue-conflict path does, red checks on a passed review send it back as changes, a head
    /// the review never read is reviewed, and a passed, green, clean one goes to the merge by the usual rule. The explicit ask
    /// bypasses <see cref="PrChecks"/>' once-a-head sets, which it leaves as they are.
    /// </summary>
    private async Task<Outcome> ForFlightAsync(Entry entry, FlightRow flight, PullRequest listed, CancellationToken cancellationToken)
    {
        if (legs is null)
        {
            Fail(entry, "this node holds no way to move a flight's PR", listed.Url);
            return Outcome.Kept;
        }
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        if (pr is null || pr.Number != entry.Number)
        {
            Fail(entry, $"{flight.Branch} has no PR #{entry.Number} on GitHub", listed.Url);
            return Outcome.Kept;
        }
        if (!string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            await github.RemoveLabelAsync(entry.Path, entry.Number, PilotMarks.Recheck, cancellationToken);
            Handled(entry, $"already {pr.State.ToLowerInvariant()}", pr.Url);
            return Done(entry);
        }
        var merge = await github.MergeStateAsync(entry.Path, entry.Number, cancellationToken);
        var checks = await github.ChecksAsync(entry.Path, entry.Number, cancellationToken);
        var code = flight.Code;
        var head = Short8(pr.Head);
        var subject = Verbs.FlightTarget(code);

        if (merge is { Kind: MergeKinds.Queued })
        {
            return await FinishAsync(entry, pr, "already in the merge queue", PilotMarks.Queued, cancellationToken);
        }

        if (pr.Mergeable == "CONFLICTING" || merge is { Kind: MergeKinds.Conflicts or MergeKinds.Behind })
        {
            if (await legs.BringUpToDateAsync(code, cancellationToken) is not { } why)
            {
                var onto = (Now(code)?.Base ?? flight.Base).Replace("origin/", "", StringComparison.Ordinal);
                var restarted = Now(code)?.Review?.ReviewedHead is { Length: > 0 } read ? $"review restarted at {Short8(read)}" : "its review has not started yet";
                return await FinishAsync(entry, pr, $"brought up to date with {onto}, {restarted}", LabelOf(code), cancellationToken);
            }
            // A merge that conflicts goes to the watchtower as PrChecks hands it, and the Handled line says so too.
            var notSent = await legs.ConflictsAsync(code, why, cancellationToken);
            return await FinishAsync(entry, pr, $"{why}{(notSent is null ? ": the watchtower is told to resolve it in the plane" : $" ({notSent})")}", LabelOf(code), cancellationToken);
        }

        if (checks == ChoiceStatus.Failing && flight.Review is { State: ReviewState.Passed })
        {
            var why = await legs.ChecksFailedAsync(code, pr.Head, cancellationToken);
            if (why is not null)
            {
                state.RaiseCard(CardBin.Ring, "pr-checks-failed", subject, $"{code}'s PR #{entry.Number} fails its checks", why, link: pr.Url);
            }
            return await FinishAsync(entry, pr, why is null ? $"checks failed at {head}, sent back as changes" : $"checks failed at {head}, not sent back ({why}), rung", LabelOf(code), cancellationToken);
        }

        var review = flight.Review;
        if (review is null || review.ReviewedHead is { Length: > 0 } reviewed && pr.Head.Length > 0 && !string.Equals(reviewed, pr.Head, StringComparison.OrdinalIgnoreCase))
        {
            var started = await legs.RunAsync(new CommandRequest { Verb = Verbs.StartReview, Target = subject, Who = "node" }, cancellationToken);
            if (!started.Ok)
            {
                Fail(entry, started.Error ?? "the review did not start", pr.Url);
                return Outcome.Kept;
            }
            var restarted = Now(code)?.Review?.ReviewedHead is { Length: > 0 } read ? $"review restarted at {Short8(read)}" : "its review waits for a window to start";
            return await FinishAsync(entry, pr, $"head {head} was not the one reviewed, {restarted}", LabelOf(code), cancellationToken);
        }

        if (review.State == ReviewState.Passed && checks == ChoiceStatus.Passing && merge is null or { Kind: MergeKinds.Ready })
        {
            if (legs.Models.NoAutoMerge(flight.Repo))
            {
                return await FinishAsync(entry, pr, $"passed and green at {head}, but {flight.Repo} is never merged by the node: left to you", PilotMarks.Passed, cancellationToken);
            }
            var merged = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = subject, Args = [pr.Head], Who = "node" }, cancellationToken);
            if (!merged.Ok)
            {
                Fail(entry, $"passed and green, but the merge was refused: {merged.Error}", pr.Url);
                return Outcome.Kept;
            }
            if (merged.Way == MergeWays.Updated)
            {
                return await FinishAsync(entry, pr, $"passed and green, but behind its base: brought up to date and reviewed afresh", null, cancellationToken);
            }
            return merged.Way is null
                ? await FinishAsync(entry, pr, $"passed and green, merged at {head}", null, cancellationToken)
                : await FinishAsync(entry, pr, $"passed and green, queued for merge at {head}", PilotMarks.Queued, cancellationToken);
        }

        var standing = review.State switch
        {
            ReviewState.Passed => checks == ChoiceStatus.Pending ? "its checks are still running" : "GitHub does not show it ready to merge",
            ReviewState.Changes => "its review asks for changes",
            _ => "its review is still reading it",
        };
        return await FinishAsync(entry, pr, $"nothing to do at {head}: {standing}", LabelOf(code), cancellationToken);
    }

    /// <summary>
    /// A recheck on a PR this node reviewed with <c>review-pr</c>, GitHub read afresh, in this order: a PR no longer open loses
    /// the label; one in the merge queue is left there; a head other than the review's is reviewed afresh; a conflict (or a PR
    /// behind its base) on the controller's own branch (author the gh login, in the base repo) is rebased and reviewed afresh, and
    /// on anyone's else, or one whose rebase conflicts, is a For-you card (a flight's own branch is the flight path's);
    /// a passed, green one goes to the merge queue by <see cref="PrReviews.QueueWhenGreenAsync"/>'s rule.
    /// </summary>
    private async Task<Outcome> ForReviewedAsync(Entry entry, PrReviewRow review, PullRequest pr, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(entry.Repo, entry.Number);
        if (!string.Equals(pr.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            await github.RemoveLabelAsync(entry.Path, entry.Number, PilotMarks.Recheck, cancellationToken);
            Handled(entry, $"already {pr.State.ToLowerInvariant()}", pr.Url);
            return Done(entry);
        }
        if (await github.MergeQueueAsync(entry.Path, entry.Number, cancellationToken) is { State: "OPEN", Queued: true })
        {
            return await FinishAsync(entry, pr, "already queued", PilotMarks.Queued, cancellationToken);
        }

        var head = Short8(pr.Head);
        var stands = review.State == PrReviewState.Reviewing ? PilotMarks.Reviewing : review.Verdict == "changes" ? PilotMarks.Changes : PilotMarks.Passed;
        if (pr.Head.Length > 0 && !string.Equals(review.Head, pr.Head, StringComparison.OrdinalIgnoreCase))
        {
            var started = await reviews.RunAsync(new CommandRequest { Verb = Verbs.ReviewPr, Target = subject, Who = "node" }, cancellationToken);
            if (!started.Ok)
            {
                Fail(entry, started.Error ?? "the review did not start", pr.Url);
                return Outcome.Kept;
            }
            return await FinishAsync(entry, pr, $"head {head} was not the one reviewed ({Short8(review.Head)}), review restarted", PilotMarks.Reviewing, cancellationToken);
        }

        var merge = await github.MergeStateAsync(entry.Path, entry.Number, cancellationToken);
        if (pr.Mergeable == "CONFLICTING" || merge is { Kind: MergeKinds.Conflicts or MergeKinds.Behind })
        {
            var what = merge is { Kind: MergeKinds.Behind } && pr.Mergeable != "CONFLICTING" ? "is behind its base" : "conflicts with its base";
            var login = await github.LoginAsync(cancellationToken);
            var own = login is { Length: > 0 } && string.Equals(pr.Author, login, StringComparison.OrdinalIgnoreCase);
            string? tried = null;
            if (own)
            {
                // The controller's own branch in the base repo: pilot rebases it, as it does a flight's.
                var parts = entry.Repo.Split('/');
                switch (await reviews.RebaseOwnBranchAsync(new PrRef(parts[0], parts[1], entry.Number), pr, cancellationToken))
                {
                    case null:
                        var started = await reviews.RunAsync(new CommandRequest { Verb = Verbs.ReviewPr, Target = subject, Who = "node" }, cancellationToken);
                        if (!started.Ok)
                        {
                            Fail(entry, $"rebased onto {pr.Base}, but the review did not start: {started.Error}", pr.Url);
                            return Outcome.Kept;
                        }
                        state.CloseCardsAbout(subject, "rebased", ConflictKind);
                        return await FinishAsync(entry, pr, $"rebased onto {pr.Base}, review restarted", PilotMarks.Reviewing, cancellationToken);
                    case { Why: var why }:
                        tried = why;
                        break;
                }
            }
            state.CloseCardsAbout(subject, "looked at again", ConflictKind);
            state.RaiseCard(CardBin.ForYou, ConflictKind, subject, $"{Short(entry.Repo, entry.Number)} {what}",
                $"It is not pilot's branch to rebase{(tried is null ? (string.IsNullOrEmpty(pr.Author) ? "" : $": it is {pr.Author}'s") : $" (yours, but {tried})")}. Rebase it and push, and it is looked at again with the label.", link: pr.Url);
            return await FinishAsync(entry, pr, tried is null ? $"{what}, not pilot's branch to rebase: a card asks you" : $"{what}, not rebased ({tried}): a card asks you", PilotMarks.Conflicts, cancellationToken);
        }

        if (review.State != PrReviewState.Reviewing && review.Verdict is "approve" or "comment" && review.QueuedAt is null)
        {
            // The queue watch decides once; a recheck is a new ask, so what it noted before is cleared and it decides again.
            var fresh = review with { QueueNote = null };
            state.PutPrReview(fresh);
            await reviews.QueueWhenGreenAsync(fresh, cancellationToken);
            var now = reviews.Of(new PrRef(entry.Repo.Split('/')[0], entry.Repo.Split('/')[1], entry.Number)) ?? fresh;
            if (now.QueuedAt is not null)
            {
                return await FinishAsync(entry, pr, $"passed and green, queued for merge at {head}", PilotMarks.Queued, cancellationToken);
            }
            if (now.QueueNote is { } note)
            {
                return await FinishAsync(entry, pr, $"nothing queued at {head}: {note}", stands, cancellationToken);
            }
        }
        // Changes asked for and nobody fixing them: the fixer is what the recheck asks for. tianwen #1857's could not start (its
        // branch was checked out elsewhere); once that was cleared, the label was taken off with nothing started.
        string? noFixer = null;
        if (review.State != PrReviewState.Reviewing && review.Verdict == "changes" && review.Fixer is null
            && (noFixer = await reviews.WhyNoFixerAsync(new PrRef(entry.Repo.Split('/')[0], entry.Repo.Split('/')[1], entry.Number), review, cancellationToken)) is null)
        {
            var fixing = await reviews.RunAsync(new CommandRequest { Verb = Verbs.FixPr, Target = subject, Who = "node" }, cancellationToken);
            if (!fixing.Ok)
            {
                Fail(entry, $"its review asks for changes, but the fixer did not start: {fixing.Error}", pr.Url);
                return Outcome.Kept;
            }
            return await FinishAsync(entry, pr, $"its review asks for changes at {head}, fixer started", PilotMarks.Changes, cancellationToken);
        }
        var standing = review.State == PrReviewState.Reviewing ? "its review is still reading it"
            : review.Verdict == "changes" ? (noFixer is null ? "its review asks for changes, and its fixer is on it" : $"its review asks for changes, and no fixer starts: {noFixer}")
            : "its checks are still running";
        return await FinishAsync(entry, pr, $"nothing to do at {head}: {standing}", stands, cancellationToken);
    }

    /// <summary>The For-you card a recheck raises on a reviewed PR that conflicts with its base.</summary>
    public const string ConflictKind = "pr-recheck-conflicts";

    private Outcome Done(Entry entry)
    {
        state.CloseCardsAbout(Verbs.PrTarget(entry.Repo, entry.Number), "rechecked", FailedKind);
        return Outcome.Consumed;
    }

    private void Handled(Entry entry, string said, string? link) =>
        state.RaiseCard(CardBin.Handled, HandledKind, Verbs.PrTarget(entry.Repo, entry.Number), $"recheck of {Short(entry.Repo, entry.Number)}: {said}", link: link);

    /// <summary>The label is kept, and a For-you card says why the recheck could not run; one card per PR, replaced by the next try.</summary>
    private void Fail(Entry entry, string why, string? link)
    {
        var subject = Verbs.PrTarget(entry.Repo, entry.Number);
        state.CloseCardsAbout(subject, "tried again", FailedKind);
        state.RaiseCard(CardBin.ForYou, FailedKind, subject, $"The recheck of {Short(entry.Repo, entry.Number)} could not run",
            $"{why}. The label stays on the PR; take it off and put it back to try again.", link: link);
    }

    private FlightRow? OwnFlight(Entry entry) =>
        state.Flights().FirstOrDefault(f => f.State != FlightState.Landed && IsFlightOf(f, entry.Repo, entry.Number));

    private static bool IsFlightOf(FlightRow flight, string repo, int number) =>
        flight.PullRequest == number && string.Equals(PrMarks.RepoOf(flight.PullRequestUrl), repo, StringComparison.OrdinalIgnoreCase);

    private PrReviewRow? OwnReview(Entry entry) =>
        state.PrReviews().FirstOrDefault(r => r.Number == entry.Number && string.Equals(r.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase));

    /// <summary>The endpoint of a peer that holds a flight on, or a review of, the PR; null when none does. A peer that does not answer holds nothing for this.</summary>
    private async Task<string?> PeerHasAsync(Entry entry, CancellationToken cancellationToken)
    {
        foreach (var peer in Fleet?.Peers() ?? [])
        {
            if (!peer.HasGrant)
            {
                continue;
            }
            try
            {
                var load = await peer.LoadAsync(cancellationToken);
                if (load.Flights.Any(f => f.State != FlightState.Landed && IsFlightOf(f, entry.Repo, entry.Number))
                    || (load.PrReviews ?? []).Any(r => r.Number == entry.Number && string.Equals(r.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase)))
                {
                    return peer.Endpoint;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or InvalidOperationException)
            {
                await Error.WriteLineAsync($"pr recheck: {peer.Endpoint} did not say what it holds: {ex.Message}");
            }
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await LookAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await Error.WriteLineAsync($"pr recheck: one look failed, looking again: {ex.Message}");
                await Task.Delay(Interval, time, stopping);
            }
        }
    }
}
