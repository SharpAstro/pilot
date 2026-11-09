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
/// There is no baseline and no exception: a finding fails the test, and the kind is rewritten to the rules.
/// </summary>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed class CardLintTests
{
    private static IEnumerable<string> Labels(IEnumerable<string> files) =>
        CardKindTests.Sources.Value.Where(s => files.Contains(s.File))
            .SelectMany(s => CardSource.Labels(s.File, s.Source)).SelectMany(l => l.Literals).Where(CardSource.Ends).Select(l => l.Template);

    /// <summary>Every finding over the source and the catalog, each as one line: <c>kind: rule</c> for a title, a first line or a missing action, <c>button file: "label": rule</c> for a button, <c>summary file: "text": rule</c> for a first line a raiser writes.</summary>
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
    public void EveryKindKeepsThePlainWordsRules()
    {
        var found = Findings();

        if (found.Count > 0)
        {
            // Where a developer reads the whole list when it is long: outside the repo, never in it.
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "pilot-card-lint-findings.txt"), found);
        }
        found.ShouldBeEmpty($"these break a plain-words rule (CardLint); rewrite them:\n{string.Join('\n', found)}");
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
