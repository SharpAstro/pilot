using System.Text.RegularExpressions;

namespace Pilot.Node.Tests;

/// <summary>
/// The plain-words rules every card keeps (#797), as one checker over the three things a person reads: the title, the first line of the
/// body and the buttons. Each check returns the names of the rules broken (<see cref="Rules"/>), none when the text is plain. Reusable on a
/// literal read from source (<c>CardLintTests</c>, with an interpolated part written as <c>§</c>) or on a card's text once raised.
/// </summary>
internal static class CardLint
{
    /// <summary>The rule names a check returns and a baseline entry ends in.</summary>
    public static class Rules
    {
        public const string TitleLength = "title over 70 characters";
        public const string TitleCut = "title cut or ending open";
        public const string BannedWord = "banned word";
        public const string ButtonWords = "button outside 2 to 5 words";
        public const string ButtonEllipsis = "button ends in an ellipsis";
        public const string ChoiceProse = "choice prose repeats the buttons";
        public const string NoAction = "Ring or ForYou kind with no action";
    }

    /// <summary>The longest title a person reads at a glance.</summary>
    public const int MaxTitle = 70;

    /// <summary>How many characters an interpolated part stands for when a title is measured from source: a flight code, a number.</summary>
    public const int HoleWidth = 6;

    /// <summary>The words that say how the node works inside; the person reads none of them (#797 rule 3).</summary>
    private static readonly (Regex Pattern, string Word)[] Banned =
    [
        (new Regex(@"\b(?:un)?ticked\b", RegexOptions.IgnoreCase), "ticked"),
        (new Regex(@"\bstale bound\b", RegexOptions.IgnoreCase), "stale bound"),
        (new Regex(@"\bredo\s+(?:\d+|§)", RegexOptions.IgnoreCase), "redo N"),
        (new Regex(@"\bgo-around\s+(?:\d+|§)", RegexOptions.IgnoreCase), "go-around N"),
        (new Regex(@"\bon (?:Opus|Sonnet|Haiku)\b(?: at \S+)?", RegexOptions.IgnoreCase), "on Opus/Sonnet at <effort>"),
        (new Regex(@"\bat (?:low|medium|high|xhigh|max)\b", RegexOptions.IgnoreCase), "at <effort>"),
        (new Regex(@"\bhalf a stale bound\b", RegexOptions.IgnoreCase), "half a stale bound"),
        (new Regex(@"\bholder unknown\b", RegexOptions.IgnoreCase), "holder unknown"),
    ];

    /// <summary>The banned words in <paramref name="text"/>, each as <c>banned word 'x'</c>.</summary>
    public static IEnumerable<string> Words(string text) =>
        Banned.Where(b => b.Pattern.IsMatch(text)).Select(b => $"{Rules.BannedWord} '{b.Word}'");

    /// <summary>What breaks the title rules (<paramref name="title"/> with an interpolated part as <c>§</c>, which counts <see cref="HoleWidth"/>).</summary>
    public static IEnumerable<string> Title(string title)
    {
        var text = title.Trim();
        if (text.Replace("§", new string('x', HoleWidth)).Length > MaxTitle)
        {
            yield return Rules.TitleLength;
        }
        if (text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith('…') || text.Length > 0 && text[^1] is '-' or ',' or ':' or ';' or '(' or '/' or '—')
        {
            yield return Rules.TitleCut;
        }
        foreach (var word in Words(text))
        {
            yield return word;
        }
    }

    /// <summary>What breaks the rules of the first line of a card's body.</summary>
    public static IEnumerable<string> FirstLine(string line) => Words(line);

    /// <summary>What breaks the button rules: two to five words, no ellipsis, no banned word. An interpolated part (<c>§</c>) counts as one word.</summary>
    public static IEnumerable<string> Button(string label)
    {
        var text = label.Trim();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words is < 2 or > 5)
        {
            yield return Rules.ButtonWords;
        }
        if (text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith('…'))
        {
            yield return Rules.ButtonEllipsis;
        }
        foreach (var word in Words(text))
        {
            yield return word;
        }
    }

    /// <summary>
    /// Whether <paramref name="prose"/> says again what a button already says: it holds a button's own words (the literal part of its
    /// label of at least two words and ten characters, between interpolated parts) (#797 rule 5). A letter A, B, C labelling an option
    /// in the body is not a repeat: its button says only <c>Pick option A</c>.
    /// </summary>
    public static IEnumerable<string> ChoiceProse(string prose, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var repeated = label.Split('§').Select(f => f.Trim(' ', '.', ',', ';', ':'))
                .Any(f => f.Length >= 10 && f.Contains(' ') && prose.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (repeated)
            {
                yield return $"{Rules.ChoiceProse}: '{label}'";
            }
        }
    }
}
