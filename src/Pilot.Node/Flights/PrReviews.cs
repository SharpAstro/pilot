using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Reviews of any PR, not only a flight's (#113): <c>review-pr</c> finds a clone of the PR's repo under the repo roots, or
/// makes one in the node's clone cache, pins a worktree of its own at the PR's head, and starts a fresh reviewer there as
/// a job of the node; <c>pr-verdict</c> is the reviewer's verdict, which a ring card carries to the controller. The
/// worktree goes once that card closes, by whatever way it closed, so a review leaves nothing behind in anyone's clone.
/// </summary>
public sealed class PrReviews(
    NodeState state, IWorkerLauncher workers, IGitHub github, IGit git, Planes planes, RepoCatalog repos, NodeOptions options, TimeProvider time) : BackgroundService
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The card a verdict raises: the review is ready for the controller.</summary>
    public const string ReadyKind = "pr-review-ready";

    /// <summary>The Handled line a start leaves.</summary>
    public const string StartedKind = "pr-review-started";

    /// <summary>The card said when a review's worktree could not be removed after its card closed.</summary>
    public const string LeftKind = "pr-review-left";

    /// <summary>The verdicts a PR reviewer gives, as GitHub's review events will take them: approve, request changes, comment.</summary>
    public static readonly string[] Verdicts = ["approve", "changes", "comment"];

    /// <summary>How often it looks for reviews whose card has closed.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a reviewer that gave its verdict has to end its turn before it is stopped.</summary>
    public TimeSpan SelfFinishGrace { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The model and effort the reviewer starts on; a test passes none.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>The CLI the reviewer is told to run, found once.</summary>
    public string Cli { get; init; } = Briefs.PilotCli();

    /// <summary>The node's socket when it is not the default one, so the reviewer's CLI reaches this node; null otherwise.</summary>
    public string? Socket => string.Equals(Path.GetFullPath(options.SocketPath), Path.GetFullPath(NodeSocket.DefaultPath), StringComparison.OrdinalIgnoreCase)
        ? null
        : options.SocketPath;

    /// <summary>
    /// Where the node clones a repo with no clone under the repo roots: <c>&lt;data&gt;/clones/&lt;owner&gt;/&lt;repo&gt;</c>. Claude
    /// Code starts no background session in a folder never trusted, so this folder is trusted once, which covers every
    /// clone in it.
    /// </summary>
    public string CloneCache => Path.Combine(options.DataFolder, "clones");

    public static bool Handles(string verb) => verb is Verbs.ReviewPr or Verbs.PrVerdict;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        try
        {
            var pr = PrRef.Parse(command.Target) is { } parsed && command.Target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal)
                ? parsed
                : throw new FlightException($"this verb takes a PR target, {Verbs.PrTargetPrefix}<owner>/<repo>#<n>, not {command.Target}");
            switch (command.Verb)
            {
                case Verbs.ReviewPr:
                    await StartAsync(pr, cancellationToken);
                    break;
                case Verbs.PrVerdict:
                    await VerdictAsync(pr, command, cancellationToken);
                    break;
            }
        }
        catch (FlightException ex)
        {
            error = ex.Message;
        }
        finally
        {
            _one.Release();
        }
        return state.Complete(command, error);
    }

    /// <summary>The review of <paramref name="pr"/>, the latest one, or null.</summary>
    public PrReviewRow? Of(PrRef pr) =>
        state.PrReviews().FirstOrDefault(r => string.Equals(r.Repo, pr.Repo, StringComparison.OrdinalIgnoreCase) && r.Number == pr.Number);

    private async Task StartAsync(PrRef pr, CancellationToken cancellationToken)
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

        // One review of a PR at a time: an earlier one, still on or waiting on its card, ends first.
        if (Of(pr) is { State: not PrReviewState.Closed } earlier)
        {
            await EndAsync(earlier, "a fresh review started", cancellationToken);
        }

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
            throw new FlightException($"{pr}'s head {read.Head[..Math.Min(8, read.Head.Length)]} was not fetched: it moved while it was; review it again");
        }

        var name = Slug(pr.Name);
        var plane = $"review-{name}-{pr.Number}";
        var worktree = Path.Combine(clone, ".claude", "worktrees", plane);
        await planes.PinAsync(clone, worktree, read.Head, cancellationToken);

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
            StartedAt = time.GetUtcNow(),
        };
        var commits = await git.RunAsync(["log", "--oneline", $"origin/{read.Base}..HEAD"], worktree, cancellationToken);
        var stat = await git.RunAsync(["diff", "--stat", $"origin/{read.Base}...HEAD"], worktree, cancellationToken);
        var brief = Briefs.ReviewPr(row, new Briefs.PlaneState(commits.Ok ? commits.Output : "", stat.Ok ? stat.Output : ""),
            await github.LoginAsync(cancellationToken), Briefs.Run(Cli, Socket));
        string job;
        try
        {
            job = await workers.StartAsync(new WorkerStart(reviewer, clone, plane, brief, Models.Model(RoleModels.Reviewer), Models.Effort(RoleModels.Reviewer)), cancellationToken);
        }
        catch (FlightException ex)
        {
            // A reviewer that did not start leaves no worktree behind for nobody to read.
            await planes.RemoveAsync(clone, worktree, cancellationToken, force: true);
            throw new FlightException(ex.Message.Contains("trust", StringComparison.OrdinalIgnoreCase) && IsCached(clone)
                ? $"{ex.Message}. Claude Code starts no background session in a folder never trusted: run claude in {CloneCache} once and accept its prompt, which covers every clone the node makes there, then review it again"
                : ex.Message);
        }
        state.PutPrReview(row with { ReviewerJob = job });
        state.RaiseCard(CardBin.Handled, StartedKind, Verbs.PrTarget(pr.Repo, pr.Number),
            $"Started {reviewer} on {pr} at {read.Head[..Math.Min(8, read.Head.Length)]}", $"in {worktree}");
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
            try
            {
                foreach (var file in Directory.EnumerateFiles(cached, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(cached, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FlightException($"{cached} holds a clone cut short that could not be removed: {ex.Message}");
            }
        }
        await github.CloneAsync(pr.Repo, cached, cancellationToken);
        return cached;
    }

    private bool IsCached(string clone) =>
        Path.GetFullPath(clone).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(CloneCache)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async Task VerdictAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
    {
        var args = command.Args ?? [];
        var verdict = args.Length > 0 ? args[0] : "";
        var report = string.Join(' ', args.Skip(1)).Trim();
        if (!Verdicts.Contains(verdict))
        {
            throw new FlightException($"a PR review's verdict is approve, changes or comment, then the report; not '{verdict}'");
        }
        if (Of(pr) is not { } review)
        {
            throw new FlightException($"{pr} has no review; review-pr starts one");
        }
        if (review.State != PrReviewState.Reviewing)
        {
            throw new FlightException($"{pr}'s review is {review.State.ToString().ToLowerInvariant()} already");
        }
        if (!string.Equals(command.Who, review.Reviewer, StringComparison.Ordinal))
        {
            throw new FlightException($"only {review.Reviewer} gives {pr}'s verdict");
        }
        if (report.Length == 0)
        {
            throw new FlightException("a verdict needs its report: a summary, then each finding");
        }
        // GitHub refuses an approval or a request for changes on one's own PR; said now, not when it is posted.
        if (verdict != "comment" && review.Author is { } author && await github.LoginAsync(cancellationToken) is { } login
            && string.Equals(login, author, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr} is {login}'s own PR, and its review goes out as {login}: GitHub takes only a comment there; give comment");
        }

        var now = time.GetUtcNow();
        var subject = Verbs.PrTarget(pr.Repo, pr.Number);
        state.CloseCardsAbout(subject, "the verdict is in", LegWatch.ReviewStalled);
        var card = state.RaiseCard(CardBin.Ring, ReadyKind, subject,
            $"{pr}: review ready, {verdict}{(review.Title is { Length: > 0 } title ? $" ({title})" : "")}",
            $"{(review.Author is { } by ? $"By {by}, r" : "R")}eviewed at {review.Head[..Math.Min(8, review.Head.Length)]}.\n{report}",
            link: review.Url);
        state.PutPrReview(review with { State = PrReviewState.Reviewed, Verdict = verdict, Report = report, Card = card.Id, FinishedAt = now });

        // A reviewer that gave its verdict is done with, after a grace to end its turn, as a flight's is.
        if (review.ReviewerJob is { } job)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                await StopQuietly(job, CancellationToken.None);
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Every review whose verdict card has closed (dismissed, or answered) ends: its reviewer stopped if it still runs, its
    /// worktree removed. What the sweep does every <see cref="Interval"/>, and the node at once after a card is closed.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var closed = state.Cards().Where(c => c.State == CardState.Closed).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var review in state.PrReviews().Where(r => r is { State: PrReviewState.Reviewed, Card: { } card } && closed.Contains(card)))
            {
                await EndAsync(review, "its card closed", cancellationToken);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Ends a review: its reviewer stopped, its worktree removed, its open cards closed. A worktree held open is said on a card and tried again.</summary>
    private async Task EndAsync(PrReviewRow review, string why, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        if (review.ReviewerJob is { } job)
        {
            await StopQuietly(job, cancellationToken);
        }
        string? held;
        try
        {
            held = await planes.RemoveAsync(review.ClonePath, review.Worktree, cancellationToken, force: true);
        }
        catch (FlightException ex)
        {
            held = ex.Message;
        }
        if (held is not null)
        {
            state.RaiseCard(CardBin.ForYou, LeftKind, subject, $"{review.Repo}#{review.Number}'s review worktree is still there", $"{held}; the node tries again");
            return;
        }
        state.PutPrReview(review with { State = PrReviewState.Closed, ClosedAt = time.GetUtcNow() });
        state.CloseCardsAbout(subject, why);
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await SweepAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that failed once is tried again: a worktree left for good is what this is here to prevent.
                await Console.Error.WriteLineAsync($"pr reviews: one sweep failed, sweeping again: {ex.Message}");
            }
        }
    }

    private async Task StopQuietly(string job, CancellationToken cancellationToken)
    {
        try
        {
            await workers.StopAsync(job, cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already, or never there: either way it is not reviewing.
        }
    }

    /// <summary>A repo's name as a worktree and a session name take it: lower case, anything but letters and digits a dash.</summary>
    private static string Slug(string name) =>
        new([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
}
