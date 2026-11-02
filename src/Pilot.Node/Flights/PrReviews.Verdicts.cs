using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
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
        state.PutPrReview(reviewed);
        var (verdictStatus, verdictDescription) = StatusOf(reviewed);
        await SetStatusAsync(reviewed, verdictStatus, verdictDescription, review.Url, cancellationToken);
        await SetLabelAsync(reviewed, cancellationToken);
        // Only changes turns it into a draft: the start made it ready, and an approve or comment leaves it as it is.
        if (verdict == "changes")
        {
            await SetDraftAsync(reviewed, draft: true, cancellationToken);
        }
        await GiveFlightVerdictAsync(reviewed, pr, cancellationToken);
        // The node posts first and asks after. The Post card used to be raised before the node's own post, which takes some 30 s,
        // and was pressed meanwhile: the press was refused as a second post and reopened the card (DIR.Lib#168, SE1, 2026-10-28).
        var (posted, notPosted) = await PostIfOpenAsync(pr, subject, cancellationToken);
        if (!posted)
        {
            // For-you, not a ring, and only for a review the node did not post: a PR no longer open, a post GitHub refused, or one
            // that went out and never said back, which may be on the PR and PostAsync refuses again, so it gets no Post.
            var sentUnanswered = Of(pr) is { PostedId: null, PostingAt: not null };
            var card = state.RaiseCard(CardBin.ForYou, ReadyKind, subject,
                $"{pr}: review ready, {verdict}{(review.Title is { Length: > 0 } title ? $" ({title})" : "")}",
                (notPosted is { } why ? $"Not posted by the node: {why}\n" : "") + detail.ToString(),
                sentUnanswered ? [] : [new CardChoice { Label = PostLabel, Verb = Verbs.PostReview, Target = subject }],
                link: review.Url);
            state.PutPrReview((Of(pr) ?? reviewed) with { Card = card.Id });
        }

        // A reviewer that gave its verdict is done with, after a grace to end its turn, as a flight's is. A review the node posted
        // itself is over too, its card closed by the post, but its worktree and reviewer go only after that grace, not before the
        // verdict's call has been answered and the reviewer has ended its turn: the sweep ends it then, which stops the reviewer.
        if (posted)
        {
            _graced.TryAdd(subject, 0);
        }
        if (review.ReviewerJob is not null || posted)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                    if (posted)
                    {
                        // Outside any lock: the sweep takes _one itself.
                        _graced.TryRemove(subject, out _);
                        await SweepAsync(CancellationToken.None);
                    }
                    else if (review.ReviewerJob is { } reviewer)
                    {
                        await StopQuietly(reviewer, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    // The periodic sweep ends a posted review all the same.
                    await Error.WriteLineAsync($"pr reviews: ending {subject} after its grace failed: {ex.Message}");
                }
            }, CancellationToken.None);
        }
        return posted;
    }

    /// <summary>
    /// A verdict on a PR still open is posted by the node at once, as a flight's review is, and a Handled card says what went
    /// out: the person is asked for nothing. It rang with a Post button whatever the verdict, and #241's sat ringing all night
    /// (SE1, 2026-10-26). A PR merged or closed meanwhile, or a post GitHub refused, gets the For-you card and its Post; a post
    /// that went out and never said back gets the card without it, as the post is never made twice. Posted is true when the review
    /// went out; NotPosted is why a post the node tried did not, so a press of the card's Post is not a guess.
    /// An approve posts itself too, by design, as a changes or a comment does. The Handled posted card closes with the review in
    /// <see cref="EndAsync"/> (<c>CloseCardsAbout</c>), on purpose: it is a record, still shown under <c>--all</c> and in the digest.
    /// </summary>
    private async Task<(bool Posted, string? NotPosted)> PostIfOpenAsync(PrRef pr, string subject, CancellationToken cancellationToken)
    {
        if (!PostsItself)
        {
            return (false, null);
        }
        try
        {
            var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
            if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }
            await PostAsync(pr, new CommandRequest { Verb = Verbs.PostReview, Target = subject, Who = "node" }, cancellationToken);
            if (Of(pr) is { PostedUrl: { } url, Verdict: { } verdict })
            {
                state.RaiseCard(CardBin.Handled, PostedKind, subject, $"{pr}: pilot's review posted, {verdict}", url, link: url);
            }
            return (true, null);
        }
        catch (FlightException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>An approve of a flight's PR at the flight's current head is the flight's verdict (PIL483); what the flight refuses is said in the log, the review stands.</summary>
    private async Task GiveFlightVerdictAsync(PrReviewRow review, PrRef pr, CancellationToken cancellationToken)
    {
        if (review.Verdict != "approve" || TakeFlightVerdict is not { } take)
        {
            return;
        }
        var headRef = "";
        var fork = false;
        try
        {
            var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
            (headRef, fork) = (read.HeadRef, read.Fork);
        }
        catch (FlightException)
        {
        }
        if (FlightOwner(pr, headRef, fork) is not { } owner)
        {
            return;
        }
        var why = await take(owner.Code, review.Head, "approve", review.Report ?? "", cancellationToken);
        if (why is not null)
        {
            await Error.WriteLineAsync($"pr reviews: {pr}'s approve was not taken by {owner.Code}: {why}");
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
}
