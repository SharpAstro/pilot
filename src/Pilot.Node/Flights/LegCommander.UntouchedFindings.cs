using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The gate on a review go-around that changes none of the files its round's blocking findings name (pilot #1024): pushing it
/// would start a review that spends one of the review loop's rounds on a branch the reviewer will read the same way (PIL1020).
/// </summary>
public sealed partial class LegCommander
{
    public const string UntouchedFindingsKind = "untouched-findings";

    // The (flight, leg, go-around, plane head) already refused once: a second finish there with a report is the worker's answer.
    private readonly ConcurrentDictionary<string, byte> _untouchedRefused = new(StringComparer.Ordinal);

    // The go-arounds (flight, leg, count) the node sent for a conflict with the base: no answer to the review, so never gated.
    private readonly ConcurrentDictionary<string, byte> _conflictGoArounds = new(StringComparer.Ordinal);

    private sealed record UntouchedRound(string Head, IReadOnlyList<string> Named, IReadOnlyList<string> Blocking);

    /// <summary>
    /// For a leg going round on its review's findings: the files those findings name when the leg's own commits since the round's head
    /// changed none of them. Null (no gate) when it is not a review go-around, the round has no head in the plane, its findings name no
    /// file, or any of them was touched.
    /// </summary>
    private async Task<UntouchedRound?> UntouchedFindingsAsync(FlightRow flight, LegRow leg, CancellationToken cancellationToken)
    {
        if (leg.ReviewGoArounds <= 0
            || _conflictGoArounds.ContainsKey(RoundKey(flight, leg))
            || flight.Review is not { State: ReviewState.Changes }
            || flight.ReviewRounds?.LastOrDefault() is not { Verdict: ReviewState.Changes, Head: { Length: > 0 } roundHead } round
            || !Directory.Exists(flight.Worktree))
        {
            return null;
        }
        var findings = ReviewFindings.Read(round.Findings);
        if (findings.BlockingFiles.Count == 0)
        {
            return null;
        }
        if (!(await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{roundHead}^{{commit}}"], flight.Worktree, cancellationToken)).Ok)
        {
            return null;
        }
        var head = await git.RunAsync(["rev-parse", "--verify", "--quiet", "HEAD"], flight.Worktree, cancellationToken);
        var changed = await git.RunAsync(["log", "--first-parent", "--no-merges", "--name-only", "--format=", $"{roundHead}..HEAD"], flight.Worktree, cancellationToken);
        if (!head.Ok || !changed.Ok || findings.Touches(changed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            return null;
        }
        return new UntouchedRound(head.Output.Trim(), findings.BlockingFiles, findings.Blocking);
    }

    /// <summary>
    /// Null when the finish may go on; else why it is refused. A refusal is remembered at the plane's head, so the same finish again
    /// with a report (the worker saying why no named file needs a change) goes through and a Handled card quotes that report.
    /// </summary>
    private async Task<string?> UntouchedRefusalAsync(FlightRow flight, LegRow leg, string report, CancellationToken cancellationToken)
    {
        if (await UntouchedFindingsAsync(flight, leg, cancellationToken) is not { } untouched)
        {
            return null;
        }
        var key = $"{RoundKey(flight, leg)}/{untouched.Head}";
        if (report.Length > 0 && _untouchedRefused.ContainsKey(key))
        {
            state.RaiseCard(CardBin.Handled, UntouchedFindingsKind, Verbs.LegTarget(flight.Code, leg.Name), $"{flight.Code}/{leg.Name} went round without touching the files its findings name",
                $"None of {string.Join(", ", untouched.Named)} changed since the review's last round, and the worker says no change to them is needed: {report}");
            return null;
        }
        _untouchedRefused[key] = 0;
        var quoted = string.Join("\n", untouched.Blocking.Select(b => $"- {b}"));
        return $"{flight.Code}/{leg.Name} is going round on its review's findings, and its commits since that round changed none of the files they name ({string.Join(", ", untouched.Named)}). "
            + $"Pushing it would start a review that costs one of the review loop's rounds for nothing. The Blocking findings:\n{quoted}\n"
            + "Change what they ask for and finish again; or, if no named file needs a change, finish again at this same head with a report saying why.";
    }
}
