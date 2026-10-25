using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a flight's review leaves on its PR on GitHub (#222): the reviewer's report as a COMMENT review, once per
/// go-around; the <see cref="PilotMarks.StatusContext"/> commit status on the head the reviewer read; and the one
/// <c>pilot:</c> label. The node runs on the person's own gh token and that person opens the flight PRs, so GitHub takes no
/// approval or request for changes from it: the verdict goes in the status and the label, the findings in the comment.
/// Each write is kept on the flight's <see cref="ReviewRow"/> once it is made. One GitHub refuses raises a For-you card and
/// the flight goes on; its row says what is still to set, and <see cref="ApplyAsync"/> sets it at the node's next look.
/// </summary>
internal sealed class PrMarks(NodeState state, IGitHub github, NodeOptions options)
{
    /// <summary>The For-you card said when GitHub refused a mark on a flight's PR.</summary>
    public const string RefusedKind = "pr-marks-refused";

    /// <summary>GitHub keeps a status description to 140 characters.</summary>
    private const int DescriptionLength = 140;

    /// <summary>The PR's head as GitHub has it now: what a review is about to read. Null when there is no PR or GitHub would not say.</summary>
    public async Task<string?> HeadAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.PullRequest is null)
        {
            return null;
        }
        try
        {
            return await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken) is { Head: { Length: > 0 } head } ? head : null;
        }
        catch (Exception ex) when (Failed(ex, cancellationToken))
        {
            // Read again by ApplyAsync while the review still reads.
            return null;
        }
    }

    /// <summary>
    /// Makes the PR say what the flight's review says, writing only what is not written yet: the review comment once its
    /// verdict is in, the status on the reviewed head, the label. A write GitHub refuses is kept on the row and carded once.
    /// </summary>
    public async Task ApplyAsync(string code, CancellationToken cancellationToken)
    {
        if (Of(code) is not { PullRequest: { } number, Review: { } started } flight)
        {
            return;
        }
        var subject = Verbs.FlightTarget(code);
        var refused = new List<string>();

        async Task Try(string what, Func<Task> write)
        {
            try
            {
                await write();
            }
            catch (Exception ex) when (Failed(ex, cancellationToken))
            {
                refused.Add($"{what}: {ex.Message}");
            }
        }

        // The head the reviewer reads, if the node could not say it when the review started and the review still reads.
        var head = started.ReviewedHead;
        if (head is null && started.State == ReviewState.Reviewing)
        {
            head = await HeadAsync(flight, cancellationToken);
            if (head is not null)
            {
                Update(code, r => r with { ReviewedHead = head });
            }
        }

        // The comment first, so the status links to it.
        if (started is { State: not ReviewState.Reviewing, ReviewUrl: null, Report: { Length: > 0 } })
        {
            await Try("the review comment", async () =>
            {
                var url = await PostAsync(flight, started, head, cancellationToken);
                Update(code, r => r with { ReviewUrl = url });
            });
        }

        var review = Of(code)?.Review ?? started;
        var (statusState, label) = Intended(review);
        if (head is not null && review.StatusState != statusState)
        {
            await Try($"the {PilotMarks.StatusContext} status", async () =>
            {
                await github.SetCommitStatusAsync(flight.RepoPath, head, statusState, Description(review, head), review.ReviewUrl ?? flight.PullRequestUrl, cancellationToken);
                Update(code, r => r with { StatusState = statusState });
            });
        }
        if (review.Label != label)
        {
            await Try($"the label {label}", async () =>
            {
                await github.SetPilotLabelAsync(flight.RepoPath, number, label, cancellationToken);
                Update(code, r => r with { Label = label });
            });
        }

        var why = refused.Count > 0 ? string.Join("; ", refused) : null;
        var before = Of(code)?.Review?.MarksWhy;
        if (before != why)
        {
            Update(code, r => r with { MarksWhy = why });
        }
        if (why is null)
        {
            state.CloseCardsAbout(subject, "set on GitHub", RefusedKind);
        }
        else if (why != before)
        {
            state.CloseCardsAbout(subject, "refused again", RefusedKind);
            state.RaiseCard(CardBin.ForYou, RefusedKind, subject, $"GitHub refused a mark on {code}'s PR #{number}",
                $"{why}. The review itself stands; the node sets what is missing at its next look at the PR.", link: flight.PullRequestUrl);
        }
    }

    /// <summary>Sets what a refused write left, for every flying flight whose row says it is still to do.</summary>
    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        foreach (var flight in state.Flights().Where(f => f is { State: FlightState.Flying, Review.MarksWhy: not null }))
        {
            await ApplyAsync(flight.Code, cancellationToken);
        }
    }

    /// <summary>The status state and the label a review stands at.</summary>
    internal static (string State, string Label) Intended(ReviewRow review) => review.State switch
    {
        ReviewState.Passed => ("success", PilotMarks.Passed),
        ReviewState.Changes => ("failure", PilotMarks.Changes),
        _ => ("pending", PilotMarks.Reviewing),
    };

    internal static string Description(ReviewRow review, string head)
    {
        var text = review.State switch
        {
            ReviewState.Passed => $"passed at {head[..Math.Min(8, head.Length)]}",
            ReviewState.Changes => $"changes: {FirstFinding(review.Report)}",
            _ => review.GoAround > 0 ? $"reviewing, go-around {review.GoAround}" : "reviewing",
        };
        return text.Length <= DescriptionLength ? text : text[..(DescriptionLength - 1)] + "…";
    }

    /// <summary>The first line of a report that says something, without a list marker.</summary>
    private static string FirstFinding(string? report) =>
        (report ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('-', '*', '#', ' ', '\t')).FirstOrDefault(l => l.Length > 0) ?? "see the review";

    /// <summary>The review as a COMMENT review of the PR, one per go-around; its page. Never posted twice: a post that went and never said back is not retried.</summary>
    private async Task<string> PostAsync(FlightRow flight, ReviewRow review, string? head, CancellationToken cancellationToken)
    {
        var repo = RepoOf(flight.PullRequestUrl)
            ?? throw new FlightException($"{flight.Code}'s PR page {flight.PullRequestUrl ?? "(none)"} does not say which repo it is on");
        var shown = review.State == ReviewState.Passed ? "passed" : "asks for changes";
        var body = $"**pilot's review {(review.GoAround > 0 ? $"(go-around {review.GoAround}) " : "")}{shown}**{(head is { Length: > 0 } ? $" on `{head[..Math.Min(8, head.Length)]}`" : "")}\n\n{(review.Report ?? "").Trim()}";
        var path = Path.Combine(Path.GetFullPath(options.DataFolder), "reviews", "flights", $"{flight.Code}-{review.GoAround}-{review.State.ToString().ToLowerInvariant()}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Payload(head, body), cancellationToken);
        try
        {
            return (await github.PostReviewAsync(repo, flight.PullRequest!.Value, path, cancellationToken)).Url;
        }
        catch (FlightException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // gh said it posted and did not say which: it is on the PR, so it is not posted again.
            Update(flight.Code, r => r with { ReviewUrl = flight.PullRequestUrl });
            throw new FlightException($"the review went to GitHub and did not come back ({ex.Message}); it is not posted again");
        }
    }

    /// <summary>A COMMENT review on <paramref name="head"/> with no inline comments: the report is its body.</summary>
    internal static byte[] Payload(string? head, string body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions
               {
                   Indented = true,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            writer.WriteStartObject();
            if (head is { Length: > 0 })
            {
                writer.WriteString("commit_id", head);
            }
            writer.WriteString("event", "COMMENT");
            writer.WriteString("body", body);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary><c>owner/name</c> from a PR page, <c>https://github.com/owner/name/pull/7</c>; null when it is not one.</summary>
    internal static string? RepoOf(string? pullRequestUrl) =>
        Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var uri) && uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 0).ToArray() is [var owner, var name, "pull", ..]
            ? $"{owner}/{name}"
            : null;

    private FlightRow? Of(string code) => state.Flights().FirstOrDefault(f => f.Code == code);

    private void Update(string code, Func<ReviewRow, ReviewRow> change)
    {
        if (Of(code) is { Review: { } review } flight)
        {
            state.PutFlight(flight with { Review = change(review) });
        }
    }

    // A write that failed is the flight's card, never its failure; only the node stopping is let through.
    private static bool Failed(Exception ex, CancellationToken cancellationToken) =>
        ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}
