using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// One choice a reviewer offers for a design question: a short choice, what follows from it, whether it is the recommended one, and
/// what it misses. <paramref name="Misses"/> is the reviewer's <c>misses:</c> text (the criteria the option fails, or <c>none</c>), and
/// null when the reviewer did not say, as in a finding written before the criteria were asked for.
/// </summary>
public sealed record DesignOption(string Text, string Consequence, bool Recommended, string? Misses = null)
{
    /// <summary>The words posted as the answer when this option is chosen: the choice, then its consequence.</summary>
    public string Answer => Consequence.Length > 0 ? $"{Text} ({Consequence})" : Text;

    /// <summary>Whether the reviewer said this option misses none of the criteria. An option that says nothing of what it misses reaches none.</summary>
    public bool Reaches => Misses is { } said && DesignQuestion.IsNone(said);
}

/// <summary>What the node took for a design question the criteria settle: the answer to act on and the reason to record.</summary>
public sealed record DesignDecided(string Answer, string Reason);

/// <summary>
/// A design finding read apart: the question and the reviewer's options. A reviewer writes it on one <c>--finding</c> line as
/// <c>design: &lt;question&gt; Option: &lt;choice&gt; =&gt; &lt;consequence&gt; misses: &lt;criteria it fails, or none&gt; Option*: ...</c>,
/// <c>Option*</c> marking the recommended one and <c>misses:</c> naming what the option fails of the criteria (the PR's issue ACs, the PR's
/// description and the repo docs the PR touches). With no option that parses, the whole text is the question and Options is empty.
/// </summary>
public sealed record DesignQuestion(PrFinding Finding, string Question, IReadOnlyList<DesignOption> Options)
{
    private static readonly Regex Marker = new(@"\bOption(?<star>\*)?\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MissesMarker = new(@"\bmisses\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What a reviewer writes after <c>misses:</c> for an option that fails no criterion.</summary>
    internal static bool IsNone(string misses) =>
        misses.Trim().Trim('.', ';', '(', ')', ' ').ToLowerInvariant() is "none" or "nothing";

    /// <summary>Reads a finding whose body starts with <c>design:</c>; null when it does not.</summary>
    public static DesignQuestion? Parse(PrFinding finding)
    {
        var body = finding.Body.Trim();
        if (!body.StartsWith(PrReviews.DesignPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var text = body[PrReviews.DesignPrefix.Length..].Trim();
        var marks = Marker.Matches(text);
        var options = new List<DesignOption>();
        for (var i = 0; i < marks.Count; i++)
        {
            var from = marks[i].Index + marks[i].Length;
            var to = i + 1 < marks.Count ? marks[i + 1].Index : text.Length;
            var part = text[from..to].Trim();
            // The misses part is the tail of the option, so it is cut off first: a choice with no arrow still ends where it starts.
            string? misses = null;
            if (MissesMarker.Match(part) is { Success: true } said)
            {
                misses = part[(said.Index + said.Length)..].Trim();
                part = part[..said.Index].Trim().TrimEnd(';', ',').TrimEnd();
                if (misses.Length == 0)
                {
                    misses = null;
                }
            }
            var arrow = part.IndexOf("=>", StringComparison.Ordinal);
            var choice = (arrow < 0 ? part : part[..arrow]).Trim();
            var consequence = arrow < 0 ? "" : part[(arrow + 2)..].Trim();
            if (choice.Length > 0)
            {
                options.Add(new DesignOption(choice, consequence, marks[i].Groups["star"].Success, misses));
            }
        }
        if (options.Count == 0)
        {
            return new DesignQuestion(finding, text, []);
        }
        var question = text[..marks[0].Index].Trim();
        return new DesignQuestion(finding, question.Length > 0 ? question : text, options);
    }

    /// <summary>The options with the recommended ones first, otherwise in the reviewer's order.</summary>
    public IEnumerable<DesignOption> RecommendedFirst => Options.Where(o => o.Recommended).Concat(Options.Where(o => !o.Recommended));

    /// <summary>The options the reviewer said miss none of the criteria.</summary>
    public IReadOnlyList<DesignOption> Reaching => [.. Options.Where(o => o.Reaches)];

    /// <summary>Whether the reviewer said what each option misses; a finding written the old way has no such word and is never decided by the node.</summary>
    public bool SaysMisses => Options.Count > 0 && Options.All(o => o.Misses is not null);

    /// <summary>The one option that reaches the criteria; null when none does, when two or more do, or when the reviewer did not say what each misses.</summary>
    public DesignOption? OnlyReaching => SaysMisses && Reaching is [var one] ? one : null;

    /// <summary>
    /// Why the question rings, or null when the reviewer did not say what the options miss (it rings as it always did): no option reaches
    /// the criteria, or two or more do and differ in what the owner gets.
    /// </summary>
    public string? WhyRings => !SaysMisses ? null
        : Reaching.Count == 0 ? "no option reaches the criteria"
        : Reaching.Count > 1 ? $"{Reaching.Count} options reach the criteria and differ in what you get"
        : null;

    /// <summary>The criteria every design question is judged by, as the reviewer is told them.</summary>
    internal const string Criteria = "the PR's issue ACs, its description and the repo docs it touches";

    /// <summary>
    /// What the node takes for these questions: set only when every one has exactly one option that reaches the criteria. The answer is
    /// that option's words (with several questions, each question's followed by its option); the reason says it is the only option that
    /// reaches the criteria and what each other option misses.
    /// </summary>
    public static DesignDecided? Decide(IReadOnlyList<DesignQuestion> questions)
    {
        if (questions.Count == 0 || questions.Any(q => q.OnlyReaching is null))
        {
            return null;
        }
        var answers = new List<string>();
        var reasons = new List<string>();
        foreach (var question in questions)
        {
            var one = question.OnlyReaching!;
            var others = question.Options.Where(o => !ReferenceEquals(o, one)).ToList();
            var why = others.Count == 0
                ? $"{PrReviews.Clean(one.Text, 120)} is the only option that reaches the criteria ({Criteria}), and no other was offered."
                : $"{PrReviews.Clean(one.Text, 120)} is the only option that reaches the criteria ({Criteria}); "
                    + $"the others miss: {string.Join("; ", others.Select(o => $"{PrReviews.Clean(o.Text, 120)}: {PrReviews.Clean(o.Misses ?? "", 200)}"))}.";
            answers.Add(questions.Count == 1 ? one.Answer : $"{PrReviews.Clean(question.Question, 200)} => {one.Answer}");
            reasons.Add(questions.Count == 1 ? why : $"{question.Finding.Path}:{question.Finding.Line} {why}");
        }
        return new DesignDecided(string.Join('\n', answers), string.Join(' ', reasons));
    }

    /// <summary>The options' words as a decision lists them: the choice of each option of each question, each once.</summary>
    internal static string[] OptionLabels(IEnumerable<DesignQuestion> questions) =>
        [.. questions.SelectMany(q => q.Options).Select(o => PrReviews.Clean(o.Text, 60)).Distinct()];
}
