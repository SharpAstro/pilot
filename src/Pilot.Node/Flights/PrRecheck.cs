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
public sealed class PrRecheck(NodeState state, IGitHub github, PrReviews reviews, RepoCatalog repos, TimeProvider time) : BackgroundService
{
    /// <summary>The Handled line a recheck leaves, saying what it found.</summary>
    public const string HandledKind = "pr-recheck";

    /// <summary>The For-you card said when a recheck could not run; its label is kept.</summary>
    public const string FailedKind = "pr-recheck-failed";

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

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
            PullRequestStatus[] listed;
            try
            {
                listed = await github.OpenPullRequestsAsync(repo.Path, cancellationToken);
            }
            catch (FlightException ex)
            {
                // Not listed is not unlabelled: what a recheck knows of its label stays as it was.
                await Console.Error.WriteLineAsync($"pr recheck: {repo.GitHub}'s PRs could not be listed: {ex.Message}");
                continue;
            }
            var labelled = listed.Where(p => p.Labels.Contains(PilotMarks.Recheck)).Select(p => p.Number).ToHashSet();
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
            await Console.Error.WriteLineAsync($"pr recheck: {Short(entry.Repo, entry.Number)} is {peer}'s, left to it");
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

    /// <summary>A recheck on a PR with a flight of this node's: filled by a later leg of #286, for now it says so and takes the label off.</summary>
    private async Task<Outcome> ForFlightAsync(Entry entry, FlightRow flight, PullRequest pr, CancellationToken cancellationToken)
    {
        await github.RemoveLabelAsync(entry.Path, entry.Number, PilotMarks.Recheck, cancellationToken);
        Handled(entry, $"{flight.Code}'s PR: the recheck of a flight is not wired yet", pr.Url);
        return Done(entry);
    }

    /// <summary>A recheck on a PR this node reviewed: filled by a later leg of #286, for now it says so and takes the label off.</summary>
    private async Task<Outcome> ForReviewedAsync(Entry entry, PrReviewRow review, PullRequest pr, CancellationToken cancellationToken)
    {
        await github.RemoveLabelAsync(entry.Path, entry.Number, PilotMarks.Recheck, cancellationToken);
        Handled(entry, $"reviewed by {review.Reviewer}: the recheck of a reviewed PR is not wired yet", pr.Url);
        return Done(entry);
    }

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
                await Console.Error.WriteLineAsync($"pr recheck: {peer.Endpoint} did not say what it holds: {ex.Message}");
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
                await Console.Error.WriteLineAsync($"pr recheck: one look failed, looking again: {ex.Message}");
                await Task.Delay(Interval, time, stopping);
            }
        }
    }
}
