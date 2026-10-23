namespace Pilot.Node.Flights;

/// <summary>
/// A job's command line from the words <c>pilot run</c> was given, for the shell the node runs it in (cmd.exe on
/// Windows, /bin/sh elsewhere). One word is a command line already, taken as it is. Several are what the caller's shell
/// split, its quotes gone: each word that holds whitespace or a character the shell would act on is quoted again, so
/// <c>--filter "A|B"</c> stays one argument instead of becoming a pipe (TIA1778's check, 2026-10-23: cmd ran
/// <c>FullyQualifiedName~DatasetSubCommand</c> as a program). A word that is an operator alone (<c>&amp;&amp;</c>,
/// <c>|</c>, <c>2&gt;&amp;1</c>) is left as one, as a caller who wrote it as its own word meant it.
/// </summary>
public static class ShellLine
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "&&", "||", "|", "&", ";", ">", ">>", "<", "2>", "2>>", "2>&1", "1>&2", "*>",
    };

    public static string Join(IReadOnlyList<string> words, bool windows) =>
        words.Count == 1 ? words[0].Trim() : string.Join(' ', words.Select(w => Quote(w, windows))).Trim();

    public static string Quote(string word, bool windows)
    {
        if (word.Length > 0 && (Operators.Contains(word) || !word.Any(c => char.IsWhiteSpace(c) || Special(c, windows))))
        {
            return word;
        }
        // cmd: inside double quotes its metacharacters are text; a quote inside is escaped as the program's argv reads it.
        // sh: single quotes make everything text; a single quote inside closes, adds an escaped one, and reopens.
        return windows ? "\"" + word.Replace("\"", "\\\"") + "\"" : "'" + word.Replace("'", "'\\''") + "'";
    }

    private static bool Special(char c, bool windows) => windows
        ? c is '&' or '|' or '<' or '>' or '^' or '(' or ')' or '%' or '!' or '"'
        : c is '&' or '|' or '<' or '>' or ';' or '(' or ')' or '$' or '`' or '\\' or '"' or '\'' or '*' or '?' or '[' or ']' or '#' or '~' or '{' or '}';
}
