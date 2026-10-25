using System.Text.RegularExpressions;

namespace Pilot.Commands;

/// <summary>What the Markdown builders (<see cref="StatusMarkdown"/>, <see cref="CardsMarkdown"/>) share.</summary>
internal static partial class Markdown
{
    /// <summary>
    /// Text taken as it is: the characters Markdown would act on, a table's pipe among them, escaped. A <c>#</c> is left,
    /// as this text never starts a line, where alone it would make a heading.
    /// </summary>
    internal static string Text(string value) => MarkdownPunctuation().Replace(value, @"\$0");

    [GeneratedRegex(@"[\\`*_\[\]<>|]")]
    private static partial Regex MarkdownPunctuation();
}
