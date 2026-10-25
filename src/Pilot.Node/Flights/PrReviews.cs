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

    public static bool Handles(string verb) => verb is Verbs.ReviewPr or Verbs.PrVerdict or Verbs.PostReview;

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
                case Verbs.PostReview:
                    await PostAsync(pr, command, cancellationToken);
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

    private async Task VerdictAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
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
        // GitHub refuses an approval or a request for changes on one's own PR; said now, not when it is posted.
        var login = await github.LoginAsync(cancellationToken);
        if (verdict != "comment" && Own(review, login))
        {
            throw new FlightException($"{pr} is {login}'s own PR, and its review goes out as {login}: GitHub takes only a comment there; give comment");
        }

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
        detail.Append($"Posts as {Event(verdict, review.Author, login)}{(login is { Length: > 0 } ? $" under {login}" : "")}, {Plural(inline, "inline comment")}");
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
        await PostIfOpenAsync(pr, subject, cancellationToken);

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
    /// A verdict on a PR still open is posted by the node at once, as a flight's review is, and a Handled card says what went
    /// out: the person is asked for nothing. It rang with a Post button whatever the verdict, and #241's sat ringing all night
    /// (SE1, 2026-10-26). A PR merged or closed meanwhile, or a post GitHub refused, keeps the For-you card and its Post.
    /// </summary>
    private async Task PostIfOpenAsync(PrRef pr, string subject, CancellationToken cancellationToken)
    {
        if (!PostsItself)
        {
            return;
        }
        try
        {
            var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
            if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await PostAsync(pr, new CommandRequest { Verb = Verbs.PostReview, Target = subject, Who = "node" }, cancellationToken);
            if (Of(pr) is { PostedUrl: { } url, Verdict: { } verdict })
            {
                state.RaiseCard(CardBin.Handled, PostedKind, subject, $"{pr}: pilot's review posted, {verdict}", url, link: url);
            }
        }
        catch (FlightException ex)
        {
            // The For-you card with Post stays, and says why the node did not post it, so a press is not a guess.
            if (Of(pr)?.Card is { } card && state.Cards(openOnly: true).FirstOrDefault(c => c.Id == card) is { } open)
            {
                state.CloseCard(open.Id, "replaced: the node could not post it", "node");
                var again = state.RaiseCard(CardBin.ForYou, ReadyKind, subject, open.Title, $"Not posted by the node: {ex.Message}\n{open.Detail}", open.Choices, link: open.Link);
                if (Of(pr) is { } review)
                {
                    state.PutPrReview(review with { Card = again.Id });
                }
            }
        }
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
