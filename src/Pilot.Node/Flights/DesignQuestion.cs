using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>One choice a reviewer offers for a design question: a short choice, what follows from it, and whether it is the recommended one.</summary>
public sealed record DesignOption(string Text, string Consequence, bool Recommended)
{
    /// <summary>The words posted as the answer when this option is chosen: the choice, then its consequence.</summary>
    public string Answer => Consequence.Length > 0 ? $"{Text} ({Consequence})" : Text;
}

/// <summary>
/// A design finding read apart: the question and the reviewer's options. A reviewer writes it on one <c>--finding</c> line as
/// <c>design: &lt;question&gt; Option: &lt;choice&gt; =&gt; &lt;consequence&gt; Option*: &lt;choice&gt; =&gt; &lt;consequence&gt;</c>, <c>Option*</c> marking
/// the recommended one. With no option that parses, the whole text is the question and Options is empty.
/// </summary>
public sealed record DesignQuestion(PrFinding Finding, string Question, IReadOnlyList<DesignOption> Options)
{
    private static readonly Regex Marker = new(@"\bOption(?<star>\*)?\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            var arrow = part.IndexOf("=>", StringComparison.Ordinal);
            var choice = (arrow < 0 ? part : part[..arrow]).Trim();
            var consequence = arrow < 0 ? "" : part[(arrow + 2)..].Trim();
            if (choice.Length > 0)
            {
                options.Add(new DesignOption(choice, consequence, marks[i].Groups["star"].Success));
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
}
