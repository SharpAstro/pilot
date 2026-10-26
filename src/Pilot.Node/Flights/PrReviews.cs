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

    /// <summary>Whether a verdict on a PR still open is posted by the node at once; off, it waits on its card's Post, as a test of that path has it.</summary>
    public bool PostsItself { get; init; } = true;

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

    /// <summary>The card's choice that posts the review.</summary>
    public const string PostLabel = "Post";

    /// <summary>
    /// Where a review's payload is written, <c>&lt;data&gt;/reviews/&lt;owner&gt;/&lt;repo&gt;-&lt;n&gt;.json</c>: in the node's own
    /// folder, so it outlives the worktree, and a full path, as gh's <c>--input</c> takes it.
    /// </summary>
    public string DraftOf(PrRef pr) => Path.Combine(Path.GetFullPath(options.DataFolder), "reviews", pr.Owner, $"{pr.Name}-{pr.Number}.json");

    public static bool Handles(string verb) => verb is Verbs.ReviewPr or Verbs.PrVerdict or Verbs.PostReview or Verbs.FixPr;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        var postedByNode = false;
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
                    postedByNode = await VerdictAsync(pr, command, cancellationToken);
                    if (postedByNode)
                    {
                        await FixAfterPostAsync(pr, cancellationToken);
                    }
                    break;
                case Verbs.PostReview:
                    await PostAsync(pr, command, cancellationToken);
                    await FixAfterPostAsync(pr, cancellationToken);
                    break;
                case Verbs.FixPr:
                    await FixAsync(pr, command, cancellationToken);
                    break;
                case Verbs.Merge:
                    await MergeAsync(pr, command, cancellationToken);
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
        // A review the node posted itself is over, its card closed by the post: its worktree and reviewer go now, as after a
        // Post press or a dismiss, not at the next sweep. After the lock is released, which the sweep takes.
        if (postedByNode)
        {
            await SweepAsync(cancellationToken);
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

        // The fix of an earlier review goes on past a fresh one: its session and worktree are the next round's to end.
        var fixOn = Of(pr);
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
            Fixer = fixOn?.Fixer,
            FixerJob = fixOn?.FixerJob,
            FixWorktree = fixOn?.FixWorktree,
            FixRound = fixOn?.FixRound,
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
        await SetStatusAsync(row, "pending", "reviewing", read.Url, cancellationToken);
    }

    /// <summary>
    /// Sets the <see cref="PilotMarks.StatusContext"/> status on the head the review read (#222), as a flight's review does on
    /// its PR. GitHub refusing it is a For-you card, never a failed review: the status is a mark, the review stands without it.
    /// </summary>
    private async Task SetStatusAsync(PrReviewRow review, string status, string description, string? link, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        try
        {
            await github.SetCommitStatusAsync(review.ClonePath, review.Head, status, description, link, cancellationToken);
            // A refusal an earlier write left is over once one goes through.
            state.CloseCardsAbout(subject, "set on GitHub", PrMarks.RefusedKind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            state.CloseCardsAbout(subject, "refused again", PrMarks.RefusedKind);
            state.RaiseCard(CardBin.ForYou, PrMarks.RefusedKind, subject, $"GitHub refused the {PilotMarks.StatusContext} status on {review.Repo}#{review.Number}",
                $"{ex.Message}. The review itself stands.", link: review.Url);
        }
    }

    /// <summary>
    /// Sets the <c>pilot:</c> label a verdict calls for on the PR: changes is <see cref="PilotMarks.Changes"/>, approve and comment
    /// <see cref="PilotMarks.Passed"/>. Refused, it is a For-you card as the status is, never a failed review.
    /// </summary>
    private async Task SetLabelAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        // A flight's PR keeps the flight's label (passed, conflicts, queued): PrMarks writes it only when its own changes, so
        // one set here would stand over it unrepaired.
        if (FlightOwns(review))
        {
            return;
        }
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        var label = review.Verdict == "changes" ? PilotMarks.Changes : PilotMarks.Passed;
        try
        {
            await github.SetPilotLabelAsync(review.ClonePath, review.Number, label, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            state.RaiseCard(CardBin.ForYou, PrMarks.RefusedKind, subject, $"GitHub refused the {label} label on {review.Repo}#{review.Number}",
                $"{ex.Message}. The review itself stands.", link: review.Url);
        }
    }

    /// <summary>Whether the reviewed PR is a flight's: its PR, on the same repo.</summary>
    // A landed flight's PR is no longer kept by it: a later review of that PR labels it as any PR's.
    private bool FlightOwns(PrReviewRow review) =>
        state.Flights().Any(f => f.State != FlightState.Landed && f.PullRequest == review.Number && f.PullRequestUrl is { } url
            && url.Contains($"/{review.Repo}/pull/{review.Number}", StringComparison.OrdinalIgnoreCase));

    /// <summary>The status a verdict leaves: approve and comment block nothing, changes fail; the description says which, at the head it read.</summary>
    internal static (string State, string Description) StatusOf(PrReviewRow review)
    {
        var at = review.Head[..Math.Min(8, review.Head.Length)];
        return review.Verdict switch
        {
            "changes" => ("failure", Clip($"changes: {PrMarks.FirstFinding(review.Report)}")),
            "approve" => ("success", $"approved at {at}"),
            _ => ("success", $"commented at {at}"),
        };
    }

    private static string Clip(string text) => text.Length <= 140 ? text : text[..139] + "…";

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

    /// <summary>Gives the verdict; true when the node posted the review itself, so the caller sweeps once it holds no lock.</summary>
    private async Task<bool> VerdictAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
    {
        var args = command.Args ?? [];
        var verdict = args.Length > 0 ? args[0] : "";
        var report = args.Length > 1 ? args[1].Trim() : "";
        var findings = new List<PrFinding>();
        foreach (var text in args.Skip(2))
        {
            findings.Add(PrFinding.Parse(text) ?? throw new FlightException($"a finding is the file and line from the repo's root, then what is wrong (src/A.cs:42 the null check is gone); not '{text}'"));
        }
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
            throw new FlightException("a verdict needs its report: a summary, then any finding on no line");
        }
        // The verdict is kept as given, on one's own PR too: only the GitHub event is forced to COMMENT there (see Event).
        var login = await github.LoginAsync(cancellationToken);

        // GitHub refuses a whole review over one comment on a line outside the diff, so only a finding whose line is in it
        // goes inline; the others go in the body. Read now, while the worktree is there to diff.
        var diff = await git.RunAsync(["-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", $"origin/{review.Base}...HEAD"],
            review.Worktree, cancellationToken);
        if (!diff.Ok)
        {
            throw new FlightException($"git could not diff {pr} against origin/{review.Base} in {review.Worktree}: {diff.Why}");
        }
        var lines = DiffLines(diff.Output);
        var anchored = findings.Select(f => f with { Inline = lines.TryGetValue(f.Path, out var ranges) && ranges.Any(r => f.Line >= r.From && f.Line <= r.To) }).ToArray();

        var now = time.GetUtcNow();
        var reviewed = review with { State = PrReviewState.Reviewed, Verdict = verdict, Report = report, Findings = anchored, Draft = DraftOf(pr), FinishedAt = now };
        await WriteDraftAsync(reviewed, login, cancellationToken);

        var subject = Verbs.PrTarget(pr.Repo, pr.Number);
        var inline = anchored.Count(f => f.Inline);
        var detail = new System.Text.StringBuilder();
        detail.Append($"{(review.Author is { } by ? $"By {by}, r" : "R")}eviewed at {review.Head[..Math.Min(8, review.Head.Length)]}.\n");
        var posts = Event(verdict, review.Author, login);
        detail.Append($"Verdict {verdict}; posts as {posts}{(login is { Length: > 0 } ? $" under {login}" : "")}, {Plural(inline, "inline comment")}");
        detail.Append(anchored.Length > inline ? $" and {anchored.Length - inline} in its body, their lines not in the diff.\n" : ".\n");
        detail.Append($"The draft: {reviewed.Draft}\n");
        detail.Append(report);
        foreach (var finding in anchored)
        {
            detail.Append($"\n{finding}");
        }
        state.CloseCardsAbout(subject, "the verdict is in", LegWatch.ReviewStalled);
        // For-you, not a ring: on an open PR the node posts it itself (below), and the card is only for one it could not.
        var card = state.RaiseCard(CardBin.ForYou, ReadyKind, subject,
            $"{pr}: review ready, {verdict}{(review.Title is { Length: > 0 } title ? $" ({title})" : "")}",
            detail.ToString(),
            [new CardChoice { Label = PostLabel, Verb = Verbs.PostReview, Target = subject }],
            link: review.Url);
        state.PutPrReview(reviewed with { Card = card.Id });
        var (verdictStatus, verdictDescription) = StatusOf(reviewed);
        await SetStatusAsync(reviewed, verdictStatus, verdictDescription, review.Url, cancellationToken);
        await SetLabelAsync(reviewed, cancellationToken);
        var posted = await PostIfOpenAsync(pr, subject, cancellationToken);

        // A reviewer that gave its verdict is done with, after a grace to end its turn, as a flight's is.
        if (review.ReviewerJob is { } job)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                await StopQuietly(job, CancellationToken.None);
            }, CancellationToken.None);
        }
        return posted;
    }

    /// <summary>
    /// A verdict on a PR still open is posted by the node at once, as a flight's review is, and a Handled card says what went
    /// out: the person is asked for nothing. It rang with a Post button whatever the verdict, and #241's sat ringing all night
    /// (SE1, 2026-10-26). A PR merged or closed meanwhile, or a post GitHub refused, keeps the For-you card and its Post; a post
    /// that went out and never said back keeps the card without it, as the post is never made twice. True when the review went out.
    /// </summary>
    private async Task<bool> PostIfOpenAsync(PrRef pr, string subject, CancellationToken cancellationToken)
    {
        if (!PostsItself)
        {
            return false;
        }
        try
        {
            var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
            if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            await PostAsync(pr, new CommandRequest { Verb = Verbs.PostReview, Target = subject, Who = "node" }, cancellationToken);
            if (Of(pr) is { PostedUrl: { } url, Verdict: { } verdict })
            {
                state.RaiseCard(CardBin.Handled, PostedKind, subject, $"{pr}: pilot's review posted, {verdict}", url, link: url);
            }
            return true;
        }
        catch (FlightException ex)
        {
            // The For-you card with Post stays, and says why the node did not post it, so a press is not a guess.
            if (Of(pr)?.Card is { } card && state.Cards(openOnly: true).FirstOrDefault(c => c.Id == card) is { } open)
            {
                state.CloseCard(open.Id, "replaced: the node could not post it", "node");
                // A post that went out and never said back may be on the PR, and PostAsync refuses it again: so no Post, only a look
                // on the PR and a dismiss.
                var sentUnanswered = Of(pr) is { PostedId: null, PostingAt: not null };
                var again = state.RaiseCard(CardBin.ForYou, ReadyKind, subject, open.Title,
                    $"Not posted by the node: {ex.Message}\n{open.Detail}", sentUnanswered ? [] : open.Choices, link: open.Link);
                if (Of(pr) is { } review)
                {
                    state.PutPrReview(review with { Card = again.Id });
                }
            }
        }
        return false;
    }

    /// <summary>The Handled card of a review the node posted itself.</summary>
    public const string PostedKind = "pr-review-posted";

    /// <summary>
    /// Posts the review to GitHub, as one review under gh's login: the draft written again with the event that login takes,
    /// then the one <c>gh api</c> call. Once only: the post is marked before the call, and a review posted, or a post that
    /// went and never said back, is not posted again.
    /// </summary>
    private async Task PostAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
    {
        if (Of(pr) is not { } review)
        {
            throw new FlightException($"{pr} has no review; review-pr starts one");
        }
        if (review.PostedId is { } posted)
        {
            throw new FlightException($"{pr}'s review is posted already, as review {posted} ({review.PostedUrl}); GitHub has it once");
        }
        if (review.PostingAt is { } at)
        {
            throw new FlightException($"{pr}'s review went to GitHub at {at:u} and never said back, so it may be there: look on {review.Url}; it is not posted again");
        }
        // A review whose card closed may still be posted: answering closes the card before its choice runs, and a sweep
        // between the two ends the review. The draft is in the node's folder, not the worktree, so it posts all the same.
        if (review is not { Verdict: { }, Report: { } })
        {
            throw new FlightException($"{pr}'s review is {review.State.ToString().ToLowerInvariant()}, with no verdict to post");
        }
        if (string.Equals(command.Who, review.Reviewer, StringComparison.Ordinal))
        {
            throw new FlightException($"the controller posts {pr}'s review, not its reviewer");
        }
        var login = await github.LoginAsync(cancellationToken)
            ?? throw new FlightException("gh says it is signed in as no one, so the review would go out under no name: gh auth login, then post it again");

        // Written again just before it goes: the event follows the login that posts it, which may not be the one at the verdict.
        var draft = DraftOf(pr);
        await WriteDraftAsync(review with { Draft = draft }, login, cancellationToken);
        var posting = review with { Draft = draft, PostingAt = time.GetUtcNow() };
        state.PutPrReview(posting);
        PostedReview made;
        try
        {
            made = await github.PostReviewAsync(pr.Repo, pr.Number, draft, cancellationToken);
        }
        catch (FlightException)
        {
            // GitHub refused it, so nothing is on the PR: it may be posted again once whatever it said is put right.
            state.PutPrReview(posting with { PostingAt = null });
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new FlightException($"{pr}'s review went to GitHub and did not come back ({ex.Message}), so it may be there: look on {review.Url}; it is not posted again");
        }
        state.PutPrReview(posting with { PostedId = made.Id, PostedState = made.State, PostedUrl = made.Url });
        // The status links the review now it is there, as a flight's does.
        var (postedStatus, postedDescription) = StatusOf(review);
        await SetStatusAsync(review, postedStatus, postedDescription, made.Url, cancellationToken);
        await SetLabelAsync(review, cancellationToken);
        if (review.Card is { } card)
        {
            var outcome = $"{PostLabel}: review {made.Id}, {made.State}, under {login}";
            state.CloseCard(card, outcome, command.Who);
            state.NoteOutcome(card, outcome, made.Url);
        }
    }

    private async Task WriteDraftAsync(PrReviewRow review, string? login, CancellationToken cancellationToken)
    {
        var path = review.Draft ?? throw new FlightException($"{review.Repo}#{review.Number}'s review has no draft path");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Payload(review, login), cancellationToken);
    }

    /// <summary>
    /// The review as GitHub's create-a-review call takes it: the head it was read at, the event, the report as its body with
    /// every finding on a line outside the diff after it, and an inline comment on each finding whose line is in the diff.
    /// </summary>
    public static byte[] Payload(PrReviewRow review, string? login)
    {
        var body = new System.Text.StringBuilder((review.Report ?? "").Trim());
        var off = review.Findings.Where(f => !f.Inline).ToList();
        if (off.Count > 0)
        {
            body.Append("\n\nOn lines outside the diff:\n");
            foreach (var finding in off)
            {
                body.Append($"\n- `{finding.Path}:{finding.Line}`: {finding.Body}");
            }
        }
        using var buffer = new MemoryStream();
        // Relaxed escaping, so the draft reads as written when the controller opens it; GitHub takes either.
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
               {
                   Indented = true,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            writer.WriteStartObject();
            writer.WriteString("commit_id", review.Head);
            writer.WriteString("event", Event(review.Verdict ?? "comment", review.Author, login));
            writer.WriteString("body", body.ToString());
            writer.WriteStartArray("comments");
            foreach (var finding in review.Findings.Where(f => f.Inline))
            {
                writer.WriteStartObject();
                writer.WriteString("path", finding.Path);
                writer.WriteNumber("line", finding.Line);
                writer.WriteString("side", "RIGHT");
                writer.WriteString("body", finding.Body);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// GitHub's review event for a verdict: <c>APPROVE</c>, <c>REQUEST_CHANGES</c> or <c>COMMENT</c>; always <c>COMMENT</c> on
    /// the PR of the login that posts it, as GitHub refuses the others there.
    /// </summary>
    public static string Event(string verdict, string? author, string? login) =>
        Own(author, login) ? "COMMENT" : verdict switch { "approve" => "APPROVE", "changes" => "REQUEST_CHANGES", _ => "COMMENT" };

    private static bool Own(PrReviewRow review, string? login) => Own(review.Author, login);

    private static bool Own(string? author, string? login) =>
        login is { Length: > 0 } && string.Equals(login, author, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lines of each file's new side a unified diff shows, hunk by hunk, as GitHub takes an inline comment on any of
    /// them: by path, each hunk's first and last line. A hunk is read by its counts, so a line of content that starts like
    /// a header is not taken for one.
    /// </summary>
    public static Dictionary<string, List<(int From, int To)>> DiffLines(string diff)
    {
        var lines = new Dictionary<string, List<(int From, int To)>>(StringComparer.Ordinal);
        string? path = null;
        int oldLeft = 0, newLeft = 0;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (oldLeft > 0 || newLeft > 0)
            {
                switch (line.Length > 0 ? line[0] : ' ')
                {
                    case '\\':
                        break;
                    case '-':
                        oldLeft--;
                        break;
                    case '+':
                        newLeft--;
                        break;
                    default:
                        oldLeft--;
                        newLeft--;
                        break;
                }
                continue;
            }
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                path = null;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                // Git ends a name holding a space with a tab, for patch's sake.
                var name = line[4..].TrimEnd('\t');
                path = name.StartsWith("b/", StringComparison.Ordinal) ? name[2..] : null;
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal) && Hunk(line) is { } hunk)
            {
                (oldLeft, newLeft) = (hunk.OldCount, hunk.NewCount);
                if (path is not null && hunk.NewCount > 0)
                {
                    (lines.TryGetValue(path, out var ranges) ? ranges : lines[path] = []).Add((hunk.NewStart, hunk.NewStart + hunk.NewCount - 1));
                }
            }
        }
        return lines;

        // @@ -a[,b] +c[,d] @@: a count left out is one.
        static (int OldCount, int NewStart, int NewCount)? Hunk(string header)
        {
            var parts = header.Split(' ');
            return parts.Length >= 3 && parts[1].StartsWith('-') && parts[2].StartsWith('+')
                   && Range(parts[1][1..]) is { } old && Range(parts[2][1..]) is { } next
                ? (old.Count, next.Start, next.Count)
                : null;
        }

        static (int Start, int Count)? Range(string text)
        {
            var comma = text.IndexOf(',');
            var start = comma < 0 ? text : text[..comma];
            var count = comma < 0 ? "1" : text[(comma + 1)..];
            return int.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, out var s) && int.TryParse(count, System.Globalization.CultureInfo.InvariantCulture, out var c)
                ? (s, c)
                : null;
        }
    }

    private static string Plural(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";

    /// <summary>
    /// Every review whose verdict card has closed (dismissed, or answered) ends: its reviewer stopped if it still runs, its
    /// worktree removed. What the sweep does every <see cref="Interval"/>, and the node at once after a card is closed.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            foreach (var fixing in state.PrReviews().Where(r => r.Fixer is not null))
            {
                await TendFixerAsync(fixing, cancellationToken);
            }
            var closed = state.Cards().Where(c => c.State == CardState.Closed).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var review in state.PrReviews().Where(r => r is { State: PrReviewState.Reviewed, Card: { } card } && closed.Contains(card)))
            {
                await EndAsync(review, "its card closed", cancellationToken);
            }
            // Also a review whose card closed: the queue watch goes on until the review is decided.
            foreach (var review in state.PrReviews().Where(r => r is { Verdict: "approve" or "comment", QueuedAt: null, QueueNote: null, State: not PrReviewState.Reviewing }))
            {
                await QueueWhenGreenAsync(review, cancellationToken);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The Handled line a queued review leaves.</summary>
    public const string QueuedKind = "pr-review-queued";

    /// <summary>The card of a reviewed PR whose base has no merge queue: its Merge choice is a flight's.</summary>
    public const string MergeKind = "pr-review-merge";

    /// <summary>The card said when GitHub refused to queue a reviewed PR.</summary>
    public const string QueueRefusedKind = "pr-review-queue-refused";

    /// <summary>
    /// A reviewed PR whose verdict is approve or comment goes in its base's merge queue at the reviewed head once its checks
    /// are green (#279). Pending checks and a draft wait for the next sweep; anything else decides the review once and for all.
    /// </summary>
    private async Task QueueWhenGreenAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        PullRequest read;
        try
        {
            read = await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken);
        }
        catch (FlightException)
        {
            return;
        }
        var at = review.Head[..Math.Min(8, review.Head.Length)];
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            state.PutPrReview(review with { QueueNote = $"the PR is {read.State.ToLowerInvariant()}" });
            return;
        }
        if (read.Draft)
        {
            return;
        }
        if (!string.Equals(read.Head, review.Head, StringComparison.OrdinalIgnoreCase))
        {
            state.PutPrReview(review with { QueueNote = $"its head moved to {read.Head[..Math.Min(8, read.Head.Length)]} after the review of {at}" });
            return;
        }
        var checks = await github.ChecksSummaryAsync(review.ClonePath, review.Number, cancellationToken);
        if (checks?.Status == ChoiceStatus.Failing)
        {
            state.PutPrReview(review with { QueueNote = $"its checks fail{(checks.FailedName is { } name ? $" ({name})" : "")}" });
            return;
        }
        if (checks?.Status != ChoiceStatus.Passing)
        {
            return;
        }
        // A repo set to no-auto-merge is never merged by the node by itself: the person's card, as a flight's merge is.
        var folder = Path.GetFileName(review.ClonePath.TrimEnd('\\', '/'));
        var repoName = review.Repo[(review.Repo.IndexOf('/') + 1)..];
        if (Models.NoAutoMerge(folder) || Models.NoAutoMerge(repoName))
        {
            var why = $"{review.Repo} is set to no-auto-merge";
            state.PutPrReview(review with { QueueNote = why });
            state.RaiseCard(CardBin.ForYou, MergeKind, subject, $"{review.Repo}#{review.Number} is reviewed {review.Verdict} and green, but {why}",
                $"Reviewed {review.Verdict} at {at}, checks green. The node does not merge this repo by itself: merge it when you are ready.",
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
            return;
        }
        if (await github.MergeQueueAsync(review.ClonePath, review.Number, cancellationToken) is null)
        {
            state.PutPrReview(review with { QueueNote = $"{read.Base} has no merge queue" });
            state.RaiseCard(CardBin.ForYou, MergeKind, subject, $"{review.Repo}#{review.Number} is reviewed {review.Verdict} and green, but {read.Base} has no merge queue",
                $"Reviewed {review.Verdict} at {at}, checks green. Merge it when you are ready.",
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
            return;
        }
        try
        {
            await github.MergeAsync(review.ClonePath, review.Number, review.Head, Models.MergeMethod, cancellationToken);
        }
        catch (FlightException ex)
        {
            state.PutPrReview(review with { QueueNote = $"GitHub refused to queue it: {ex.Message}" });
            state.RaiseCard(CardBin.ForYou, QueueRefusedKind, subject, $"GitHub refused to queue {review.Repo}#{review.Number}", ex.Message,
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
            return;
        }
        state.PutPrReview(review with { QueuedAt = time.GetUtcNow() });
        state.RaiseCard(CardBin.Handled, QueuedKind, subject, $"Queued {review.Repo}#{review.Number} in {read.Base}'s merge queue at {at}",
            $"Reviewed {review.Verdict}, checks green.", link: review.Url);
    }

    /// <summary>The Merge choice of a reviewed PR's card: the PR joins its base's merge queue, or merges, at the head the card named.</summary>
    private async Task MergeAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
    {
        if (Of(pr) is not { } review)
        {
            throw new FlightException($"{pr} has no review; review-pr starts one");
        }
        var head = (command.Args ?? []).FirstOrDefault()?.Trim() ?? "";
        if (head.Length < 7)
        {
            throw new FlightException("merge needs the head commit you reviewed (7 characters at least)");
        }
        var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr} is {read.State.ToLowerInvariant()}, not open");
        }
        if (!read.Head.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr}'s head moved to {read.Head[..Math.Min(8, read.Head.Length)]} past the {head[..7]} reviewed: review it again");
        }
        var queued = await github.MergeAsync(review.ClonePath, pr.Number, read.Head, Models.MergeMethod, cancellationToken);
        state.PutPrReview(review with { QueuedAt = time.GetUtcNow() });
        state.RaiseCard(CardBin.Handled, QueuedKind, Verbs.PrTarget(pr.Repo, pr.Number),
            queued ? $"Queued {pr} in {read.Base}'s merge queue" : $"Merged {pr}", $"{command.Who} chose Merge at {head[..7]}.", link: review.Url);
    }

    /// <summary>The Handled line a fixer's start leaves.</summary>
    public const string FixStartedKind = "pr-fix-started";

    /// <summary>The For-you card of a changes verdict on a PR that is not the controller's own, with Fix it.</summary>
    public const string FixOfferKind = "pr-fix-offer";

    /// <summary>The ring of a changes verdict with a design question in it: the author decides, no fixer starts.</summary>
    public const string DesignKind = "pr-review-design";

    /// <summary>The For-you card said when the fixer could not start.</summary>
    public const string FixFailedKind = "pr-fix-failed";

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
                state.RaiseCard(CardBin.ForYou, FixFailedKind, subject, $"{pr}: the fixer pushed, but its review could not start", $"{ex.Message}\npilot review {pr} tries again.", link: review.Url);
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

    /// <summary>The prefix that marks a finding as a question of design, not a defect to fix.</summary>
    public const string DesignPrefix = "design:";

    private static bool IsDesign(PrFinding finding) => finding.Body.StartsWith(DesignPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The clone of a repo the node tracks (one RepoCatalog lists under the repo roots, never the clone cache), or null.</summary>
    private string? TrackedClone(PrRef pr) =>
        repos.Repos().FirstOrDefault(r => string.Equals(r.GitHub, pr.Repo, StringComparison.OrdinalIgnoreCase))?.Path;

    /// <summary>Whether a flight's PR is this one: a flight's own review and fix are the flight's.</summary>
    private bool FlightOwns(PrRef pr) =>
        state.Flights().Any(f => f.PullRequestUrl is { } url && PrRef.Parse(url) is { } owned
            && string.Equals(owned.Repo, pr.Repo, StringComparison.OrdinalIgnoreCase) && owned.Number == pr.Number);

    /// <summary>
    /// A changes verdict on a PR now posted (#290). On the controller's own PR in a repo the node tracks, with no flight owning
    /// it, one fixer starts on the PR's branch; on anyone else's, a For-you card offers Fix it. A finding marked as a design
    /// question rings instead and starts none, and an untracked repo gets nothing. A fixer already on is never joined by a second.
    /// A fixer that could not start is a For-you card, not a failed post.
    /// </summary>
    private async Task FixAfterPostAsync(PrRef pr, CancellationToken cancellationToken)
    {
        if (Of(pr) is not { Verdict: "changes", Fixer: null } review || TrackedClone(pr) is null || FlightOwns(pr))
        {
            return;
        }
        var subject = Verbs.PrTarget(pr.Repo, pr.Number);
        if (review.Findings.Any(IsDesign))
        {
            var design = string.Join('\n', review.Findings.Where(IsDesign).Select(f => f.ToString()));
            state.RaiseCard(CardBin.Ring, DesignKind, subject, $"{pr}: the review asks a question of design", $"{design}\nNo fixer started: this is the author's to decide.", link: review.Url);
            return;
        }
        var rounds = review.FixRound ?? 0;
        if (rounds >= MaxFixRounds)
        {
            state.RaiseCard(CardBin.Ring, FixCappedKind, subject, $"{pr}: still asking for changes after {rounds} rounds of fixes",
                $"{rounds} fixers have pushed and the review still asks for changes, so no fourth starts: this is yours.\n{(review.Report ?? "").Trim()}\n{string.Join('\n', review.Findings.Select(f => f.ToString()))}",
                link: review.Url);
            return;
        }
        // A PR the controller already chose Fix it on goes on by itself, whoever owns it.
        var own = rounds > 0 || Own(review, await github.LoginAsync(cancellationToken));
        if (!own)
        {
            if (!state.Cards(openOnly: true).Any(c => c.Kind == FixOfferKind && c.Subject == subject))
            {
                state.RaiseCard(CardBin.ForYou, FixOfferKind, subject, $"{pr}: the review asked for changes, and it is {review.Author ?? "someone else"}'s PR",
                    $"{(review.Report ?? "").Trim()}\nFix it starts a session on the PR's branch that makes them.",
                    [new CardChoice { Label = FixLabel, Verb = Verbs.FixPr, Target = subject }], link: review.Url);
            }
            return;
        }
        try
        {
            await StartFixerAsync(pr, review, cancellationToken);
        }
        catch (FlightException ex)
        {
            state.RaiseCard(CardBin.ForYou, FixFailedKind, subject, $"{pr}: the fixer could not start", ex.Message, link: review.Url);
        }
    }

    /// <summary>The Fix it choice: the same fixer, started on the controller's word on a PR that is not their own.</summary>
    private async Task FixAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
    {
        if (Of(pr) is not { } review)
        {
            throw new FlightException($"{pr} has no review; review-pr starts one");
        }
        if (review.Verdict != "changes")
        {
            throw new FlightException($"{pr}'s review did not ask for changes, so there is nothing to fix");
        }
        await StartFixerAsync(pr, review, cancellationToken);
    }

    /// <summary>
    /// Starts the one fixer of a PR: its branch fetched, a worktree <c>&lt;clone&gt;/.claude/worktrees/fix-&lt;repo&gt;-&lt;n&gt;</c> on it,
    /// a session briefed with the review. Refused for a repo the node does not track, a PR a flight owns, one that is not
    /// open, and one that has a fixer already.
    /// </summary>
    private async Task StartFixerAsync(PrRef pr, PrReviewRow review, CancellationToken cancellationToken)
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
        await planes.EnsureAsync(clone, worktree, branch, cancellationToken);

        var fixer = $"{options.Machine.ToLowerInvariant()}-fix-{name}-{pr.Number}";
        var fixing = review with { Fixer = fixer, FixWorktree = worktree, FixRound = (review.FixRound ?? 0) + 1 };
        string job;
        try
        {
            job = await workers.StartAsync(new WorkerStart(fixer, clone, plane, Briefs.FixPr(fixing, branch, CiShape.Of(worktree)),
                Models.Model(RoleModels.Worker), Models.Effort(RoleModels.Worker)), cancellationToken);
        }
        catch (FlightException)
        {
            await planes.RemoveAsync(clone, worktree, cancellationToken, force: true);
            throw;
        }
        state.PutPrReview(fixing with { FixerJob = job });
        state.CloseCardsAbout(Verbs.PrTarget(pr.Repo, pr.Number), "a fixer started", FixOfferKind);
        state.RaiseCard(CardBin.Handled, FixStartedKind, Verbs.PrTarget(pr.Repo, pr.Number),
            $"Started {fixer} on {pr}'s branch {branch}, to make the changes the review asked for", $"in {worktree}", link: review.Url);
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
        // A Handled line is born closed, so "said once" is looked for among every card since the verdict, not the open ones.
        var since = review.FinishedAt ?? review.StartedAt;
        var saidHeld = state.Cards().Any(c => c.Subject == subject && c.Kind == LeftKind && c.At >= since);
        if (held is not null)
        {
            // Not the person's: something still has the folder open (a reviewer's process ending, an editor), and the sweep tries
            // again every Interval until it goes. A For-you card raised at every sweep rang the person in a loop (pilot#269's
            // review, SE1 2026-10-26); one Handled line says it, once.
            if (!saidHeld)
            {
                state.RaiseCard(CardBin.Handled, LeftKind, subject, $"{review.Repo}#{review.Number}'s review worktree is still held; the node removes it when it is let go", held);
            }
            return;
        }
        if (saidHeld)
        {
            state.RaiseCard(CardBin.Handled, LeftKind, subject, $"{review.Repo}#{review.Number}'s review worktree is removed, let go at last");
        }
        state.PutPrReview(review with { State = PrReviewState.Closed, ClosedAt = time.GetUtcNow() });
        // The Merge card of a reviewed PR outlives the verdict card; a fresh review of the PR is the one thing that retires it.
        state.CloseCardsAbout(subject, why, [ReadyKind, LegWatch.ReviewStalled, PrMarks.RefusedKind, LeftKind, .. why == "a fresh review started" ? new[] { MergeKind, QueueRefusedKind } : []]);
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
