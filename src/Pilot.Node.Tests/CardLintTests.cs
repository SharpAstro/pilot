using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Every card kind keeps the plain-words rules of #797 (<see cref="CardLint"/>): a title of at most 70 characters that is never cut, buttons of
/// two to five words that never end in an ellipsis, none of the node's own words (ticked, stale bound, redo N, on Opus at high, holder
/// unknown...), a first line that does not say again what the buttons say, and a Ring or For-you kind that offers something to do.
/// It reads the same source as <see cref="CardKindTests"/> (every <c>RaiseCard</c> call and every button label under Pilot.Node) and the
/// table of first lines (<see cref="CardSummaries"/>) of every kind in <see cref="CardKindCatalog"/>.
/// The kinds not yet rewritten sit on <see cref="Baseline"/>, each with the rule it breaks: the next legs shrink that list and nothing adds to
/// it, because a finding not on it fails, and so does an entry that no longer holds (remove it).
/// </summary>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed class CardLintTests
{
    /// <summary>
    /// What is known to break a rule and is not yet rewritten: <c>kind: rule</c> for a title, a first line or a missing action,
    /// <c>button file: "label": rule</c> for a button, <c>summary file: "text": rule</c> for a first line a raiser writes.
    /// </summary>
    private static readonly string[] Baseline =
    [
        "autopilot-ran: title over 70 characters",
        "bench-result: Ring or ForYou kind with no action",
        "bench-result: title over 70 characters",
        "button Autopilot.cs: \"Allow\": button outside 2 to 5 words",
        "button Autopilot.cs: \"Deny\": button outside 2 to 5 words",
        "button Flights/LegCommander.Advance.cs: \"Start §'s reviewer at its new head\": button outside 2 to 5 words",
        "button Flights/LegCommander.Advance.cs: \"§§ now, asking the node again\": button outside 2 to 5 words",
        "button Flights/LegCommander.Finish.cs: \"Merge origin/§ into the plane and push\": button outside 2 to 5 words",
        "button Flights/LegCommander.Finish.cs: \"Push the rebased branch with lease\": button outside 2 to 5 words",
        "button Flights/LegCommander.Lay.cs: \"Merge\": button outside 2 to 5 words",
        "button Flights/LegCommander.LegEnd.cs: \"Plan § in parallel with §\": button outside 2 to 5 words",
        "button Flights/LegCommander.Merge.cs: \"Merge §'s PR again as it is\": button outside 2 to 5 words",
        "button Flights/LegCommander.Merge.cs: \"Merge\": button outside 2 to 5 words",
        "button Flights/LegCommander.Merge.cs: \"Re-run the checks of §'s PR\": button outside 2 to 5 words",
        "button Flights/LegCommander.Overlap.cs: \"Plan § in parallel with §\": button outside 2 to 5 words",
        "button Flights/LegCommander.Review.cs: \"Land § with its checks failing\": button outside 2 to 5 words",
        "button Flights/LegCommander.Review.cs: \"Land § with the review's findings unfixed\": button outside 2 to 5 words",
        "button Flights/LegCommander.Review.cs: \"Merge\": button outside 2 to 5 words",
        "button Flights/LegCommander.Review.cs: \"The PR of § fails its checks.\": button outside 2 to 5 words",
        "button Flights/LegCommander.Review.cs: \"The review of § found things to fix.\": button outside 2 to 5 words",
        "button Flights/LegCommander.Review.cs: \"The reviewer of § did not start, so nothing was reviewed.\": button outside 2 to 5 words",
        "button Flights/LegCommander.Start.cs: \"Start § again, its validator first\": button outside 2 to 5 words",
        "button Flights/LegCommander.Start.cs: \"Start § as the tree is\": button outside 2 to 5 words",
        "button Flights/LegWatch.cs: \"Let §'s worker spend up to §\": button outside 2 to 5 words",
        "button Flights/LegWatch.cs: \"Nudge § to finish § or say why not\": button outside 2 to 5 words",
        "button Flights/LegWatch.cs: \"Start a fresh reviewer for §\": button outside 2 to 5 words",
        "button Flights/LegWatch.cs: \"Tell § to carry on with §\": button outside 2 to 5 words",
        "button Flights/LegWatch.cs: \"its ticker stopped without finishing it\": button outside 2 to 5 words",
        "button Flights/LegWatch.cs: \"spent § of a § budget\": button outside 2 to 5 words",
        "button Flights/PrChecks.cs: \"Merge §'s PR #§ once more\": button outside 2 to 5 words",
        "button Flights/PrChecks.cs: \"Open a PR with the §\": button outside 2 to 5 words",
        "button Flights/PrChecks.cs: \"Queue §'s PR #§ for merging again\": button outside 2 to 5 words",
        "button Flights/PrChecks.cs: \"Wait\": button outside 2 to 5 words",
        "button Flights/PrReviews.Fixer.cs: \"Start the fixer on § again\": button outside 2 to 5 words",
        "button Flights/PrReviews.FixerRounds.cs: \"Start the fixer on § again\": button outside 2 to 5 words",
        "button Flights/PrReviews.FixerRounds.cs: \"Start the review of § on the fixer's push\": button outside 2 to 5 words",
        "button Flights/PrReviews.Queue.cs: \"Merge\": button outside 2 to 5 words",
        "button Flights/PrReviews.Sweep.cs: \"Start the review of § afresh\": button outside 2 to 5 words",
        "button Flights/StopGate.cs: \"Tell the session to commit its files in §'s plane\": button outside 2 to 5 words",
        "button Flights/Watchtower.cs: \"Start the watchtower now instead of waiting for the next try\": button outside 2 to 5 words",
        "button NodeLan.cs: \"Allow § to run commands here until revoked\": button outside 2 to 5 words",
        "button Updates/NodeUpdater.cs: \"Install pilot § and restart the node\": button outside 2 to 5 words",
        "design-mirror-failed: Ring or ForYou kind with no action",
        "digest: Ring or ForYou kind with no action",
        "disk-low: Ring or ForYou kind with no action",
        "disk-swept: title over 70 characters",
        "flown-elsewhere: Ring or ForYou kind with no action",
        "holding-timeout: choice prose repeats the buttons",
        "human-verification: Ring or ForYou kind with no action",
        "idea-conflict: Ring or ForYou kind with no action",
        "issue-left-open: Ring or ForYou kind with no action",
        "job-idle: Ring or ForYou kind with no action",
        "job-untold: title over 70 characters",
        "lay-failed: Ring or ForYou kind with no action",
        "leg-no-go: choice prose repeats the buttons",
        "leg-ticker-started: banned word 'ticked'",
        "leg-ticker-started: title over 70 characters",
        "leg-validating: title over 70 characters",
        "merged-under-leg: Ring or ForYou kind with no action",
        "move-failed: choice prose repeats the buttons",
        "node-label-refused: Ring or ForYou kind with no action",
        "node-label-refused: title over 70 characters",
        "not-landed: title over 70 characters",
        "plane-left: Ring or ForYou kind with no action",
        "planner-ended: title over 70 characters",
        "pr-fix-follow-up-failed: Ring or ForYou kind with no action",
        "pr-fix-follow-up-failed: title over 70 characters",
        "pr-fix-follow-up: title over 70 characters",
        "pr-fix-left: Ring or ForYou kind with no action",
        "pr-fix-started: title over 70 characters",
        "pr-marks-refused: Ring or ForYou kind with no action",
        "pr-recheck-conflicts: Ring or ForYou kind with no action",
        "pr-recheck-failed: Ring or ForYou kind with no action",
        "pr-review-left: title over 70 characters",
        "pr-review-lost: title over 70 characters",
        "pr-review-merge: title over 70 characters",
        "pr-review-waiting: Ring or ForYou kind with no action",
        "push-blocked: choice prose repeats the buttons",
        "push-blocked: title over 70 characters",
        "push-diverged: Ring or ForYou kind with no action",
        "queue-dropped: title over 70 characters",
        "removal-held: Ring or ForYou kind with no action",
        "review-changes: choice prose repeats the buttons",
        "review-kept: title over 70 characters",
        "review-notes: Ring or ForYou kind with no action",
        "review-notes: choice prose repeats the buttons",
        "session-compacted: title over 70 characters",
        "sibling-behind: Ring or ForYou kind with no action",
        "spend-alert: Ring or ForYou kind with no action",
        "spend-alert: title over 70 characters",
        "stale-acted: banned word 'half a stale bound'",
        "stale-acted: banned word 'stale bound'",
        "stale-acted: title over 70 characters",
        "store: Ring or ForYou kind with no action",
        "summary Flights/LegCommander.Finish.cs: \"§/§ is done and its check passes, but its branch w\": choice prose repeats the buttons",
        "summary Flights/LegCommander.GoAround.cs: \"§/§ has gone around § times, the cap. The watchtow\": choice prose repeats the buttons",
        "summary Flights/LegCommander.Review.cs: \"The review of § (PR §) has asked for changes § tim\": choice prose repeats the buttons",
        "summary Flights/LegWatch.cs: \"The reviewer of § showed no first turn after two s\": choice prose repeats the buttons",
        "summary Flights/PrChecks.cs: \"§ has circled in a holding pattern and §. Merge as\": choice prose repeats the buttons",
        "usage-week: Ring or ForYou kind with no action",
        "validation-stalled: choice prose repeats the buttons",
        "watchtower-churning: Ring or ForYou kind with no action",
        "watchtower-down: title over 70 characters",
        "watchtower-parked: title over 70 characters",
        "worker-compacted: title over 70 characters",
        "worker-idle: choice prose repeats the buttons",
        "worker-idle: title over 70 characters",
        "worker-nudged: title over 70 characters",
        "worker-replaced: title over 70 characters",
        "worker-told-job: title over 70 characters",
        "worker-waiting-job: title over 70 characters",
        "worktree-dirty: Ring or ForYou kind with no action",
    ];

    private static IEnumerable<string> Labels(IEnumerable<string> files) =>
        CardKindTests.Sources.Value.Where(s => files.Contains(s.File))
            .SelectMany(s => CardSource.Labels(s.File, s.Source)).SelectMany(l => l.Literals).Where(CardSource.Ends).Select(l => l.Template);

    /// <summary>Every finding over the source and the catalog, each as one line a baseline entry spells the same.</summary>
    private static SortedSet<string> Findings()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var call in CardKindTests.Calls.Value)
        {
            var kind = CardKindTests.Resolve(call.File, call.KindExpr)?.Value ?? $"{call.File} {System.Text.RegularExpressions.Regex.Replace(call.KindExpr, @"\s+", " ")}";
            foreach (var literal in call.Title.Where(CardSource.Ends))
            {
                foreach (var rule in CardLint.Title(literal.Template))
                {
                    found.Add($"{kind}: {rule}");
                }
            }
        }
        foreach (var (file, source) in CardKindTests.Sources.Value)
        {
            foreach (var label in CardSource.Labels(file, source))
            {
                foreach (var literal in label.Literals.Where(CardSource.Ends))
                {
                    foreach (var rule in CardLint.Button(literal.Template))
                    {
                        found.Add($"button {file}: \"{literal.Template}\": {rule}");
                    }
                }
            }
            var buttons = Labels([file]).ToList();
            foreach (var summary in CardSource.Summaries(file, source))
            {
                foreach (var literal in summary.Literals.Where(CardSource.Ends))
                {
                    var shown = literal.Template.Length > 50 ? literal.Template[..50] : literal.Template;
                    foreach (var rule in CardLint.FirstLine(literal.Template).Concat(CardLint.ChoiceProse(literal.Template, buttons).Select(r => r[..r.IndexOf(':')])).Distinct())
                    {
                        found.Add($"summary {file}: \"{shown}\": {rule}");
                    }
                }
            }
        }
        foreach (var entry in CardKindCatalog.All)
        {
            var bins = entry.Bins.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (CardSummaries.Describes(entry.Kind, []))
            {
                // Help, not a card's first line (CardSummaries.Resolve): it is shown on no card.
            }
            else
            {
                var line = CardSummaries.For(entry.Kind, "flight/X1/a", "title", []);
                foreach (var rule in CardLint.FirstLine(line))
                {
                    found.Add($"{entry.Kind}: {rule}");
                }
                foreach (var rule in CardLint.ChoiceProse(line, Labels(entry.Files.Concat(entry.ChoiceFiles ?? []))).Select(r => r[..r.IndexOf(':')]).Distinct())
                {
                    found.Add($"{entry.Kind}: {rule}");
                }
            }
            if (bins.Any(b => b is "Ring" or "ForYou")
                && (entry.Choices ?? []).Length == 0
                && !CardKindTests.Calls.Value.Any(c => c.HasChoices && CardKindTests.Resolve(c.File, c.KindExpr)?.Value == entry.Kind))
            {
                found.Add($"{entry.Kind}: {CardLint.Rules.NoAction}");
            }
        }
        return found;
    }

    [Fact]
    public void EveryKindKeepsThePlainWordsRulesOrIsOnTheBaselineThatOnlyShrinks()
    {
        var found = Findings();
        var baseline = new SortedSet<string>(Baseline, StringComparer.Ordinal);

        var added = found.Except(baseline).ToList();
        var gone = baseline.Except(found).ToList();

        if (added.Count > 0 || gone.Count > 0)
        {
            // Where a developer reads the whole list when it is long: outside the repo, never in it.
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "pilot-card-lint-findings.txt"), found);
        }
        added.ShouldBeEmpty($"these break a plain-words rule (CardLint) and are not on the baseline; rewrite them:\n{string.Join('\n', added)}");
        gone.ShouldBeEmpty($"these no longer break a rule: take them off the baseline:\n{string.Join('\n', gone)}");
    }

    [Theory]
    [InlineData("PIL716/failure-investigation's worker has been idle 3 min with 0 of 6 steps ticked, though it answers its nudges", "ticked")]
    [InlineData("PDF446/encoder stopped: Retired: PDF446 split, encoder went upstream to DIR188; worker stop", "title over 70")]
    [InlineData("Redo 2, on Opus at high, of §", "on Opus")]
    [InlineData("§ stopped: stale bound passed", "stale bound")]
    [InlineData("§ held by holder unknown", "holder unknown")]
    [InlineData("§ is waiting on the", "title over 70", false)]
    public void ATitleThatBreaksARuleIsReported(string title, string expected, bool report = true)
    {
        var findings = CardLint.Title(title).ToList();

        if (report)
        {
            findings.ShouldContain(f => f.Contains(expected, StringComparison.OrdinalIgnoreCase), string.Join('\n', findings));
        }
        else
        {
            findings.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("The merge of PIL1 stopped because the…")]
    [InlineData("Worker stopped, and so on...")]
    [InlineData("Worker stopped:")]
    public void ATitleCutShortIsReported(string title) => CardLint.Title(title).ShouldContain(CardLint.Rules.TitleCut);

    [Theory]
    [InlineData("Retry", CardLint.Rules.ButtonWords)]
    [InlineData("Send encoder round again to Retired: PDF446 split", CardLint.Rules.ButtonWords)]
    [InlineData("measure the share of clear frames of the archive nebula...", CardLint.Rules.ButtonEllipsis)]
    [InlineData("Send it round (redo 2)", "redo N")]
    [InlineData("Resume on Sonnet at high", "on Opus/Sonnet")]
    public void AButtonThatBreaksARuleIsReported(string label, string expected) =>
        CardLint.Button(label).ShouldContain(f => f.Contains(expected, StringComparison.Ordinal));

    [Theory]
    [InlineData("Restart the worker")]
    [InlineData("Pause all flights")]
    [InlineData("Pick option A")]
    [InlineData("Answer in your own words")]
    public void AVerbOfTwoToFiveWordsIsLeftAlone(string label) => CardLint.Button(label).ShouldBeEmpty();

    [Fact]
    public void ProseThatRepeatsAButtonIsReportedAndOptionsLetteredInTheBodyAreNot()
    {
        CardLint.ChoiceProse("Nudge it to finish carries it on; Start a fresh worker lets it go.", ["Nudge it to finish", "Start a fresh worker"])
            .Count().ShouldBe(2);
        CardLint.ChoiceProse("A: keep a library. B: make it a service.", ["Pick option A", "Pick option B"]).ShouldBeEmpty();
        CardLint.ChoiceProse("Merge lands it", ["Merge"]).ShouldBeEmpty();
    }

    [Fact]
    public void ADesignQuestionCardAndALegsStopCardKeepTheRulesAtRuntime()
    {
        var choices = new[] { "Pick option A", "Pick option B", PrReviews.AnswerLabel };

        choices.SelectMany(CardLint.Button).ShouldBeEmpty();
    }
}
