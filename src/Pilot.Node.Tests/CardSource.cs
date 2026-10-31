using System.Text;
using System.Text.RegularExpressions;

namespace Pilot.Node.Tests;

/// <summary>A string literal found in the source: its text with every interpolation hole as <c>§</c>, and what follows it in the expression.</summary>
internal sealed record SourceLiteral(string Template, string After);

/// <summary>One call that puts a card up: where, which kind and bins its text names, and the literal parts of its title.</summary>
internal sealed record CardCall(string File, int Line, string Form, string KindExpr, string[] Bins, string TitleExpr, IReadOnlyList<SourceLiteral> Title);

/// <summary>A button's label as the source writes it, with the literal parts of the expression.</summary>
internal sealed record LabelUse(string File, int Line, string Expr, IReadOnlyList<SourceLiteral> Literals);

/// <summary>
/// Reads C# source as text, well enough to find the <c>RaiseCard</c> calls and the <c>CardChoice</c> labels of Pilot.Node: strings of
/// every kind (plain, verbatim, interpolated with nested strings, raw) and comments are skipped as the compiler does, so a comma
/// inside a title never splits an argument and a call named only in a comment is never found. Not a parser: it only needs the
/// arguments of a call and the string literals at the top of one expression.
/// </summary>
internal static class CardSource
{
    /// <summary>The call forms that raise a card: its name, the argument holding the kind, the one holding the title, and its bin if fixed.</summary>
    private static readonly (Regex Open, string Form, int Kind, int Title, string? Bin, string? OnlyIn)[] Forms =
    [
        (new Regex(@"\bRaiseCard\("), "RaiseCard", 1, 3, null, null),
        (new Regex(@"(?<![\w.])Ring\("), "Ring", 1, 2, "Ring", "LegWatch.cs"),
        (new Regex(@"\bGate\.Hold\("), "Hold", 3, 5, "Handled", null),
    ];

    /// <summary>The calls of <paramref name="source"/> that raise a card; a definition of the method itself is not one.</summary>
    public static List<CardCall> Calls(string file, string source)
    {
        var text = Mask(source);
        var code = Mask(source, blankStrings: true);
        var calls = new List<CardCall>();
        foreach (var (open, form, kindAt, titleAt, fixedBin, onlyIn) in Forms)
        {
            if (onlyIn is not null && !file.EndsWith(onlyIn, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match m in open.Matches(code))
            {
                var args = Args(text, m.Index + m.Length - 1);
                if (args.Count <= titleAt && args.Count <= kindAt)
                {
                    continue;
                }
                var first = Slice(text, args[0]);
                if (first.StartsWith("CardBin ", StringComparison.Ordinal) || first.StartsWith("LegRow ", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] bins = fixedBin is not null
                    ? [fixedBin]
                    : [.. Regex.Matches(first, @"CardBin\.(\w+)").Select(b => b.Groups[1].Value).Distinct()];
                var kind = args.Count > kindAt ? Slice(text, args[kindAt]) : "";
                (int Start, int End) title = args.Count > titleAt ? args[titleAt] : (0, 0);
                var titleExpr = Slice(text, title);
                var literals = Literals(text, title.Start, title.End);
                if (literals.Count == 0)
                {
                    literals = Literals(text, title.Start, title.End, anyDepth: true);
                }
                if (literals.Count == 0 && Regex.IsMatch(titleExpr, @"^\w+$"))
                {
                    literals = Declared(text, m.Index, titleExpr);
                }
                calls.Add(new CardCall(file, LineOf(text, m.Index), form, kind, bins, titleExpr, literals));
            }
        }
        return calls;
    }

    /// <summary>Every <c>Label = …</c> in <paramref name="source"/>: a choice's button text.</summary>
    public static List<LabelUse> Labels(string file, string source)
    {
        var text = Mask(source);
        var labels = new List<LabelUse>();
        foreach (Match m in Regex.Matches(Mask(source, blankStrings: true), @"\bLabel\s*=\s*(?!=)"))
        {
            var start = m.Index + m.Length;
            var end = ExprEnd(text, start);
            var expr = text[start..end].Trim();
            var literals = Literals(text, start, end);
            if (literals.Count == 0 && Regex.IsMatch(expr, @"^\w+$"))
            {
                literals = Declared(text, m.Index, expr);
            }
            labels.Add(new LabelUse(file, LineOf(text, m.Index), expr, literals));
        }
        return labels;
    }

    /// <summary>The literals of the statement that gives <paramref name="name"/> its value before <paramref name="before"/> (or anywhere in the file, for a constant).</summary>
    private static List<SourceLiteral> Declared(string text, int before, string name)
    {
        var declaration = new Regex($@"(?:\b(?:var|string)\s+{name}|\({{1}}[^()=;]*\b{name}\b[^()=;]*\))\s*=\s*(?!=)");
        var match = declaration.Matches(text[..before]).LastOrDefault() ?? declaration.Matches(text).FirstOrDefault();
        if (match is null)
        {
            return [];
        }
        var start = match.Index + match.Length;
        return Literals(text, start, ExprEnd(text, start, ';'), anyDepth: true);
    }

    /// <summary>The string literals standing at the top of the expression between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static List<SourceLiteral> Literals(string text, int start, int end, bool anyDepth = false)
    {
        var found = new List<SourceLiteral>();
        var depth = 0;
        for (var i = start; i < end;)
        {
            var c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (IsStringStart(text, i) && (depth == 0 || anyDepth))
            {
                var stringEnd = SkipString(text, i);
                var template = Template(text, i, stringEnd);
                found.Add(new SourceLiteral(template, text[stringEnd..Math.Min(end, text.Length)].Trim()));
                i = stringEnd;
                continue;
            }
            else if (IsStringStart(text, i))
            {
                i = SkipString(text, i);
                continue;
            }
            i++;
        }
        return found;
    }

    /// <summary>Whether a literal's text is the whole of its title: nothing follows it, or only the other branch of a conditional.</summary>
    public static bool Ends(SourceLiteral literal) => literal.After.Length == 0 || literal.After[0] is ':' or '?' or ')' or ',' or ';';

    /// <summary>The source with comments blanked (same length, same lines), so a name in a comment is not found.</summary>
    public static string Mask(string source, bool blankStrings = false)
    {
        var sb = new StringBuilder(source);
        for (var i = 0; i < source.Length;)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    sb[i++] = ' ';
                }
            }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = close < 0 ? source.Length : close + 2;
                for (; i < stop; i++)
                {
                    if (source[i] != '\n')
                    {
                        sb[i] = ' ';
                    }
                }
            }
            else if (IsStringStart(source, i))
            {
                var stop = SkipString(source, i);
                for (; blankStrings && i < stop; i++)
                {
                    if (source[i] != '\n')
                    {
                        sb[i] = '#';
                    }
                }
                i = stop;
            }
            else if (source[i] == '\'')
            {
                i = SkipChar(source, i);
            }
            else
            {
                i++;
            }
        }
        return sb.ToString();
    }

    private static string Slice(string text, (int Start, int End) range) => text[range.Start..range.End].Trim();

    private static int LineOf(string text, int index) => 1 + text.AsSpan(0, index).Count('\n');

    /// <summary>The argument ranges of the call whose opening parenthesis is at <paramref name="open"/>.</summary>
    private static List<(int Start, int End)> Args(string text, int open)
    {
        var args = new List<(int, int)>();
        var depth = 0;
        var start = open + 1;
        for (var i = open; i < text.Length;)
        {
            var c = text[i];
            if (IsStringStart(text, i))
            {
                i = SkipString(text, i);
                continue;
            }
            if (c == '\'')
            {
                i = SkipChar(text, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    if (i > start || args.Count > 0)
                    {
                        args.Add((start, i));
                    }
                    return args;
                }
            }
            else if (c == ',' && depth == 1)
            {
                args.Add((start, i));
                start = i + 1;
            }
            i++;
        }
        return args;
    }

    /// <summary>Where the expression starting at <paramref name="start"/> ends: the next top-level comma, closing bracket or <paramref name="stop"/>.</summary>
    private static int ExprEnd(string text, int start, char stop = ',')
    {
        var depth = 0;
        for (var i = start; i < text.Length;)
        {
            var c = text[i];
            if (IsStringStart(text, i))
            {
                i = SkipString(text, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    return i;
                }
                depth--;
            }
            else if (depth == 0 && (c == stop || c == ';'))
            {
                return i;
            }
            i++;
        }
        return text.Length;
    }

    private static bool IsStringStart(string s, int i)
    {
        if (s[i] == '"')
        {
            return true;
        }
        if (s[i] is not ('$' or '@'))
        {
            return false;
        }
        var j = i;
        while (j < s.Length && s[j] is '$' or '@')
        {
            j++;
        }
        return j < s.Length && s[j] == '"' && (i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_'));
    }

    private static int SkipChar(string s, int i)
    {
        var j = i + 1;
        if (j < s.Length && s[j] == '\\')
        {
            j++;
        }
        j++;
        return j < s.Length && s[j] == '\'' ? j + 1 : i + 1;
    }

    /// <summary>The index just past the string literal starting at <paramref name="i"/>.</summary>
    private static int SkipString(string s, int i)
    {
        var dollars = 0;
        var verbatim = false;
        while (s[i] is '$' or '@')
        {
            if (s[i] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }
            i++;
        }
        var quotes = 0;
        while (i < s.Length && s[i] == '"')
        {
            quotes++;
            i++;
        }
        if (quotes >= 3)
        {
            var close = new string('"', quotes);
            var at = s.IndexOf(close, i, StringComparison.Ordinal);
            return at < 0 ? s.Length : at + quotes;
        }
        if (quotes == 2)
        {
            return i;
        }
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && !verbatim)
            {
                i += 2;
            }
            else if (c == '"')
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            else if (c == '{' && dollars > 0)
            {
                if (i + 1 < s.Length && s[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }
                i = SkipHole(s, i + 1);
            }
            else
            {
                i++;
            }
        }
        return s.Length;
    }

    /// <summary>The index just past the interpolation hole whose code starts at <paramref name="i"/>.</summary>
    private static int SkipHole(string s, int i)
    {
        var depth = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (IsStringStart(s, i))
            {
                i = SkipString(s, i);
                continue;
            }
            if (c == '\'')
            {
                i = SkipChar(s, i);
                continue;
            }
            if (c is '{' or '(' or '[')
            {
                depth++;
            }
            else if (c is '}' or ')' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>The literal's text between its quotes, each interpolation hole written as <c>§</c>.</summary>
    private static string Template(string s, int start, int end)
    {
        var i = start;
        var dollars = 0;
        while (s[i] is '$' or '@')
        {
            if (s[i] == '$')
            {
                dollars++;
            }
            i++;
        }
        var quotes = 0;
        while (i < end && s[i] == '"')
        {
            quotes++;
            i++;
        }
        var body = new StringBuilder();
        var stop = end - quotes;
        while (i < stop)
        {
            if (s[i] == '{' && dollars > 0)
            {
                if (i + 1 < stop && s[i + 1] == '{')
                {
                    body.Append('{');
                    i += 2;
                    continue;
                }
                i = SkipHole(s, i + 1);
                body.Append('§');
                continue;
            }
            if (s[i] == '\\' && i + 1 < stop)
            {
                body.Append(s[i + 1] == 'n' ? '\n' : s[i + 1]);
                i += 2;
                continue;
            }
            body.Append(s[i++]);
        }
        return body.ToString();
    }
}
