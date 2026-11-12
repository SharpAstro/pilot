using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A PR review or review-fix round the node is running outside a flight, as the Flights page shows it (pilot #1007): the PR, the
/// round, the session doing it and the session's own state, so a round "started again" always points somewhere the owner sees.
/// </summary>
internal sealed record PrRoundLine
{
    /// <summary>The review row it comes from.</summary>
    public required PrReviewRow Review { get; init; }

    /// <summary>The PR as a person names it, <c>owner/repo#n</c>.</summary>
    public required string Pr { get; init; }

    /// <summary>The PR's title; empty when the node did not read one.</summary>
    public required string Title { get; init; }

    /// <summary>The round, <c>round 4</c>; <c>review</c> for the first review, before any fixer.</summary>
    public required string Round { get; init; }

    /// <summary>The name of the session doing it: the reviewer while reviewing, the fixer while fixing.</summary>
    public required string Session { get; init; }

    /// <summary>What it is doing and how its session is: <c>fixing · working</c>, <c>reviewing · idle</c>.</summary>
    public required string State { get; init; }

    /// <summary>Whether the round is a fix (the fixer is the session) and not a review.</summary>
    public required bool Fixing { get; init; }

    /// <summary>The node it runs on; null while the page shows one node.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>The line's text on one row, the tooltip's whole.</summary>
    public string Whole => $"{Pr} {Title} · {Round} · {Session} · {State}";

    private static bool Live(SessionRow? s) => SessionPick.IsLive(s);

    private static string Word(SessionState state) => state switch
    {
        SessionState.Working => "working",
        SessionState.Waiting => "waiting",
        SessionState.Idle => "idle",
        SessionState.Ended => "ended",
        _ => "failed",
    };

    /// <summary>One line for each review that is running (reviewing), or whose fixer's session is live; a closed one whose fixer ended has none.</summary>
    public static IReadOnlyList<PrRoundLine> Build(IEnumerable<PrReviewRow> reviews, IEnumerable<SessionRow> sessions)
    {
        var all = sessions as IReadOnlyList<SessionRow> ?? [.. sessions];
        var lines = new List<PrRoundLine>();
        foreach (var review in reviews)
        {
            var fixer = SessionPick.ByName(all, review.Fixer);
            bool fixing;
            if (review.State == PrReviewState.Reviewing && review.ClosedAt is null)
            {
                fixing = false;
            }
            else if (Live(fixer))
            {
                fixing = true;
            }
            else
            {
                continue;
            }
            var session = fixing ? fixer : SessionPick.ByName(all, review.Reviewer);
            var sessionName = fixing ? review.Fixer! : review.Reviewer;
            var state = fixing ? "fixing" : "reviewing";
            lines.Add(new PrRoundLine
            {
                Review = review,
                Pr = $"{review.Repo}#{review.Number}",
                Title = review.Title ?? "",
                Round = review.FixRound is { } round ? $"round {round}" : "review",
                Session = sessionName,
                State = session is null ? state : $"{state} · {Word(session.State)}",
                Fixing = fixing,
            });
        }
        return Order(lines);
    }

    /// <summary>The lines in a steady order: by node, then repo and PR number.</summary>
    public static IReadOnlyList<PrRoundLine> Order(IEnumerable<PrRoundLine> lines) =>
        [.. lines.OrderBy(l => l.Node?.Name ?? "", StringComparer.Ordinal).ThenBy(l => l.Review.Repo, StringComparer.Ordinal).ThenBy(l => l.Review.Number)];
}
