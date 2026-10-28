using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

namespace Pilot.Node.Flights;

/// <summary>
/// The <see cref="SummaryPrompt.Resolved"/> section of a summary's answer, taken out of it: what is left is the prose the person
/// reads, and the ids are what the call proposes, nothing more. Read strictly: a section that holds anything but card ids, or the
/// word <see cref="SummaryPrompt.NoneResolved"/>, or that appears twice, proposes nothing.
/// </summary>
public static partial class StaleCardProposal
{
    /// <summary>The most ids one answer may propose; a longer list is not one the node believes.</summary>
    public const int MaxProposed = 40;

    [GeneratedRegex(@"^(?:[-*]\s+)?(c\d{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex Id();

    /// <summary><paramref name="answer"/> without its resolved section(s), and the card ids it proposed, in order and once each.</summary>
    public static (string Prose, IReadOnlyList<string> Ids) Split(string answer)
    {
        var prose = new StringBuilder();
        var sections = new List<List<string>>();
        List<string>? section = null;
        foreach (var line in answer.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.Equals(line.Trim(), SummaryPrompt.Resolved, StringComparison.OrdinalIgnoreCase))
            {
                sections.Add(section = []);
                continue;
            }
            if (section is not null && line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = null;
            }
            if (section is not null)
            {
                section.Add(line.Trim());
            }
            else
            {
                prose.Append(line).Append('\n');
            }
        }

        return (prose.ToString().Trim(), sections.Count == 1 ? Read(sections[0]) : []);
    }

    private static List<string> Read(List<string> lines)
    {
        var ids = new List<string>();
        var said = lines.Where(l => l.Length > 0).ToList();
        if (said.Count == 1 && string.Equals(said[0].TrimEnd('.'), SummaryPrompt.NoneResolved, StringComparison.OrdinalIgnoreCase))
        {
            return ids;
        }
        foreach (var line in said)
        {
            if (Id().Match(line) is not { Success: true } match)
            {
                return [];
            }
            if (!ids.Contains(match.Groups[1].Value, StringComparer.Ordinal))
            {
                ids.Add(match.Groups[1].Value);
            }
        }
        return ids.Count > MaxProposed ? [] : ids;
    }
}

/// <summary>What the node made of one proposed card: dismissed, or left, and why in a few words.</summary>
public sealed record StaleVerdict(string Card, bool Dismissed, string Note);

/// <summary>
/// Closes the open cards a summary proposes as resolved, only where the node itself reads the fact that says so: a PR card whose PR
/// merged or closed, a failed-check card whose PR is green now, a conflict card whose branch merges cleanly, a stalled-review card
/// whose review has given its verdict, a flight's card for a flight that has landed. The call only proposes. A card of a kind with
/// no check, one that needs the person's decision (Ready anyway, an ask, a usage cap), one whose cause cannot be read (GitHub does not
/// say), and one the check contradicts are all left open. Each one closed is a Handled line saying which flight, which PR and what fact.
/// </summary>
public sealed class StaleCards(NodeState state, IGitHub github)
{
    /// <summary>The kind of the Handled line a dismissal leaves.</summary>
    public const string HandledKind = "card-resolved";

    /// <summary>Who closes the card: the summary's check.</summary>
    public const string ClosedBy = "node";

    // Cards about a PR: closed once the PR has merged or closed, whatever else they said.
    private static readonly HashSet<string> PrCards = [LegCommander.PrReady, LegCommander.MergeFailed, PrChecks.QueueDropped, PrChecks.MergeDropped, ConflictKind, ChecksFailedKind];

    private const string ConflictKind = "pr-conflicts";
    private const string ChecksFailedKind = "pr-checks-failed";

    // Cards about a flight's progress, which a landed flight has no use for. Not here: what the person decides (an overlap, Ready
    // anyway, an ask, a cap, a digest, a usage window, an update), and what a landing leaves for the person (plane-left, landed).
    private static readonly HashSet<string> FlightCards =
    [
        LegCommander.PrReady, LegCommander.MergeFailed, PrChecks.QueueDropped, PrChecks.MergeDropped, ConflictKind, ChecksFailedKind,
        LegWatch.ReviewStalled, LegWatch.ValidationStalled, LegWatch.Stopped, LegWatch.Idle, LegWatch.OverBudget, "leg-failed", LegCommander.NextStepKind,
    ];

    // Kinds that are the person's to decide: said as such when proposed, so a refusal is not mistaken for a check that failed.
    private static readonly HashSet<string> Decisions =
    [
        LegCommander.ReviewChanges, CardKinds.AssistantAsk, LegCommander.OverlapKind, StartGate.WeekKind, UsageReader.HighKind, NodeLan.AskedKind,
        NodeUpdater.Kind, Digest.Kind, LegCommander.NoGoKind,
    ];

    /// <summary>
    /// Checks each of <paramref name="proposed"/> and closes those whose fact holds. <paramref name="about"/> is the subject the
    /// summary was about (a flight), when it was one: a card of anything else is none of its business. One verdict per id.
    /// </summary>
    public async Task<IReadOnlyList<StaleVerdict>> DismissAsync(IEnumerable<string> proposed, string? about, CancellationToken cancellationToken)
    {
        var verdicts = new List<StaleVerdict>();
        foreach (var id in proposed)
        {
            var card = state.Cards().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
            (Found? Found, string Note) read = card switch
            {
                null => (null, "there is no such card"),
                { State: not CardState.Open } => (null, "it is not open"),
                _ when about is not null && card.Subject != about && !card.Subject.StartsWith(about + "/", StringComparison.Ordinal) => (null, "it is not about what was summarised"),
                _ when Decisions.Contains(card.Kind) => (null, "it needs the person's decision"),
                _ => await CheckAsync(card, cancellationToken),
            };
            var (found, note) = read;
            if (card is null || found is null)
            {
                verdicts.Add(new StaleVerdict(id, false, note));
                continue;
            }

            if (!state.CloseCard(card.Id, $"dismissed: {found.Fact}", ClosedBy))
            {
                verdicts.Add(new StaleVerdict(id, false, "it was closed meanwhile"));
                continue;
            }
            state.RaiseCard(CardBin.Handled, HandledKind, card.Subject, $"{found.Flight}'s {found.What}: {found.Fact}",
                $"Card {card.Id} ({card.Title}) closed: the summary proposed it as resolved, and the node read the fact itself.", link: card.Link ?? found.Link);
            verdicts.Add(new StaleVerdict(id, true, found.Fact));
        }
        return verdicts;
    }

    /// <summary>What the check read: which flight, what the card was ("#344 conflict"), the fact that settles it, and a page for it.</summary>
    private sealed record Found(string Flight, string What, string Fact, string? Link = null);

    private async Task<(Found? Found, string Note)> CheckAsync(CardRow card, CancellationToken cancellationToken)
    {
        if (card.Subject.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal))
        {
            return card.Kind == LegWatch.ReviewStalled ? CheckPrReview(card) : (null, "no check reads this kind of card");
        }
        if (!card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            return (null, "no check reads this kind of card");
        }

        // A flight's code, from its own subject or one of its legs'.
        var code = card.Subject[Verbs.FlightTargetPrefix.Length..].Split('/')[0];
        if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } flight)
        {
            return (null, $"the node holds no flight {code}");
        }
        if (!FlightCards.Contains(card.Kind))
        {
            return (null, "no check reads this kind of card");
        }

        var noun = Noun(card.Kind);
        var number = flight.PullRequest is { } n ? $"#{n} " : "";
        if (flight.State == FlightState.Landed)
        {
            return (new Found(flight.Code, $"{number}{noun}", $"{flight.Code} landed{(flight.LandedAt is { } at ? $" {Stamp(at)}" : "")}", flight.PullRequestUrl), "");
        }
        if (card.Kind == LegWatch.ReviewStalled)
        {
            return CheckFlightReview(card, flight, $"{number}{noun}");
        }
        if (!PrCards.Contains(card.Kind))
        {
            return (null, "its flight has not landed");
        }
        if (flight.PullRequest is not { } pull)
        {
            return (null, "its flight has no PR the node could read");
        }

        PullRequest? pr;
        try
        {
            pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        }
        catch (FlightException ex)
        {
            return (null, $"GitHub could not be read: {ex.Message}");
        }
        if (pr is null || pr.Number != pull)
        {
            return (null, $"GitHub shows no PR #{pull} on its branch");
        }

        var what = $"#{pull} {noun}";
        if (pr.State == "MERGED")
        {
            return (new Found(flight.Code, what, $"#{pull} merged on GitHub{(pr.MergeCommit.Length > 0 ? $" as {Short(pr.MergeCommit)}" : "")}", pr.Url), "");
        }
        if (pr.State == "CLOSED")
        {
            return (new Found(flight.Code, what, $"#{pull} closed on GitHub without a merge", pr.Url), "");
        }
        if (pr.State != "OPEN")
        {
            return (null, $"GitHub shows #{pull} as {pr.State.ToLowerInvariant()}");
        }

        try
        {
            if (card.Kind == ConflictKind)
            {
                return await CheckConflictAsync(flight, pull, pr, what, cancellationToken);
            }
            if (card.Kind == ChecksFailedKind || card is { Kind: LegCommander.MergeFailed } && card.Choices.Any(c => c.Verb == Verbs.RerunChecks))
            {
                return await CheckChecksAsync(card, flight, pull, pr, what, cancellationToken);
            }
        }
        catch (FlightException ex)
        {
            return (null, $"GitHub could not be read: {ex.Message}");
        }
        return (null, $"#{pull} is still open");
    }

    private async Task<(Found? Found, string Note)> CheckConflictAsync(FlightRow flight, int pull, PullRequest pr, string what, CancellationToken cancellationToken)
    {
        if (pr.Mergeable != "MERGEABLE")
        {
            return (null, pr.Mergeable == "CONFLICTING" ? $"#{pull} still conflicts with its base" : "GitHub has not said whether #" + pull + " merges cleanly");
        }
        // Mergeable is GitHub's word on the conflict; being behind its base is the other thing a rebase cures, so it is read too.
        if (await github.MergeStateAsync(flight.RepoPath, pull, cancellationToken) is { Kind: MergeKinds.Conflicts or MergeKinds.Behind } merge)
        {
            return (null, $"GitHub still shows #{pull} as {merge.Kind}");
        }
        return (new Found(flight.Code, what, $"the branch merges cleanly at {Short(pr.Head)}", pr.Url), "");
    }

    private async Task<(Found? Found, string Note)> CheckChecksAsync(CardRow card, FlightRow flight, int pull, PullRequest pr, string what, CancellationToken cancellationToken)
    {
        var checks = await github.ChecksSummaryAsync(flight.RepoPath, pull, cancellationToken);
        if (checks is null)
        {
            return (null, $"GitHub does not say where #{pull}'s checks stand");
        }
        if (checks.Status != ChoiceStatus.Passing)
        {
            return (null, $"#{pull}'s checks are {checks.Status}{(checks.FailedName is { } failed ? $" ({failed})" : "")}");
        }
        // The head the card was about, where its Merge choice kept it: the checks are green at a newer one, or the same re-run.
        var was = card.Choices.FirstOrDefault(c => c.Verb == Verbs.Merge)?.Args.FirstOrDefault();
        var newer = was is { Length: > 0 } && !string.Equals(was, pr.Head, StringComparison.OrdinalIgnoreCase) ? $" at a newer head ({Short(pr.Head)}, was {Short(was)})" : $" at {Short(pr.Head)}";
        return (new Found(flight.Code, what, $"its checks pass{newer}", pr.Url), "");
    }

    private static (Found? Found, string Note) CheckFlightReview(CardRow card, FlightRow flight, string what)
    {
        if (flight.Review is not { } review)
        {
            return (null, "the node holds no review of its flight");
        }
        if (review.State == ReviewState.Reviewing || review.FinishedAt is not { } finished || finished < card.At)
        {
            return (null, "its review has not given a verdict since the card");
        }
        var verdict = review.State == ReviewState.Passed ? "passed" : "asks for changes";
        return (new Found(flight.Code, what, $"its review {verdict} {Stamp(finished)}", flight.PullRequestUrl), "");
    }

    private (Found? Found, string Note) CheckPrReview(CardRow card)
    {
        var review = state.PrReviews().FirstOrDefault(r => Verbs.PrTarget(r.Repo, r.Number) == card.Subject);
        if (review is null)
        {
            return (null, "the node holds no such review");
        }
        if (review.State == PrReviewState.Reviewing || review.FinishedAt is not { } finished || finished < card.At)
        {
            return (null, "its review has not given a verdict since the card");
        }
        return (new Found($"{review.Repo}#{review.Number}", "review stall", $"its review gave its verdict ({review.Verdict ?? "in"}) {Stamp(finished)}", review.Url), "");
    }

    private static string Noun(string kind) => kind switch
    {
        LegCommander.PrReady => "PR card",
        LegCommander.MergeFailed or PrChecks.MergeDropped => "merge card",
        PrChecks.QueueDropped => "queue card",
        ConflictKind => "conflict",
        ChecksFailedKind => "failed checks",
        LegWatch.ReviewStalled => "review stall",
        LegWatch.ValidationStalled => "validation stall",
        _ => $"{kind} card",
    };

    private static string Short(string hash) => hash.Length > SummaryRecords.HashLength ? hash[..SummaryRecords.HashLength] : hash;

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
