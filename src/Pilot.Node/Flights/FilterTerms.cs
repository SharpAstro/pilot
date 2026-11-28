using System.Text;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// The name terms of a check's <c>dotnet test</c> filter, and whether each matches a test of the project it targets (pilot #1157).
/// A term that matches no test runs nothing, and the run still exits 0: a leg's check and a review then report tests as passed that
/// never ran. <c>leg plan</c> refuses such a check, naming the term, as it refuses the wrong runner's switch (#1132). Read from the
/// <c>.cs</c> sources only, with a plain reader, and no process: a project that cannot be found or read refuses nothing.
/// </summary>
public static partial class FilterTerms
{
    /// <summary>The key of the plan-leg argument that names the test classes the leg will add.</summary>
    public const string NewTestsKey = "new-tests";

    /// <summary>What a leg planned with <see cref="NewTestsKey"/> says in its done-when, followed by the class names.</summary>
    public const string NewTestsMark = "The leg adds the test classes: ";

    /// <summary>One name term of a filter: <see cref="Text"/> as written, its property, operator (<c>~</c> contains, <c>=</c> exact, <c>*</c> wildcard) and value.</summary>
    public sealed record Term(string Text, string Property, string Op, string Value);

    /// <summary>A test project, its folder and the tests its sources declare.</summary>
    public sealed class Project
    {
        public required string Name { get; init; }

        /// <summary>The full names Namespace.Class.Method of every test, a nested class under both Outer+Inner and Outer.Inner.</summary>
        public List<string> FullNames { get; } = [];

        /// <summary>The method names alone.</summary>
        public HashSet<string> MethodNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The full class names (both nested forms) of every class that holds a test and is not abstract.</summary>
        public List<string> ClassNames { get; } = [];

        /// <summary>The simple names of the classes that hold a test, directly or inherited (abstract bases too).</summary>
        public SortedSet<string> TestClasses { get; } = new(StringComparer.Ordinal);

        internal List<TypeInfo> Types { get; } = [];

        internal List<(string Path, string Text)> Files { get; } = [];
    }

    internal sealed class TypeInfo
    {
        public string Ns = "";
        public string Chain = "";
        public string Simple = "";
        public bool Abstract;
        public string File = "";
        public List<string> Bases = [];
        public List<string> Methods = [];
    }

    // ---- the filter's terms ----

    /// <summary>The name terms of the words after <c>dotnet test</c>: VSTest's name properties and the Platform class and method values. Negations, Category, Trait and the rest are never terms.</summary>
    public static List<Term> TermsOf(IEnumerable<string> words)
    {
        var terms = new List<Term>();
        foreach (var (option, value) in TestFilters.Read(words))
        {
            if (value.Length == 0 || value.IndexOfAny(['$', '`']) >= 0)
            {
                continue;
            }
            if (option == TestFilters.VsTest)
            {
                terms.AddRange(Expression(value));
            }
            else if (option is TestFilters.Class or TestFilters.Method)
            {
                terms.Add(new Term($"{option} {value}", option, "*", value));
            }
        }
        return terms;
    }

    /// <summary>The name terms of a VSTest expression: split on <c>|</c> and <c>&amp;</c>, parentheses and escapes honoured, a negated term left out.</summary>
    public static List<Term> Expression(string expression)
    {
        var terms = new List<Term>();
        var prop = new StringBuilder();
        var value = new StringBuilder();
        string? op = null;
        var inValue = false;

        void Flush()
        {
            var p = prop.ToString().Trim();
            var v = value.ToString().Trim();
            if (op is null && p.Length > 0)
            {
                (p, v, op) = ("FullyQualifiedName", p, "~");
            }
            if (op is "~" or "=" && v.Length > 0 && p.ToLowerInvariant() is "fullyqualifiedname" or "name" or "classname")
            {
                terms.Add(new Term($"{p}{op}{v}", p.ToLowerInvariant(), op, v));
            }
            prop.Clear();
            value.Clear();
            op = null;
            inValue = false;
        }

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '\\' && i + 1 < expression.Length)
            {
                (inValue ? value : prop).Append(expression[++i]);
            }
            else if (c is '|' or '&' or '(' or ')')
            {
                Flush();
            }
            else if (!inValue && c is '!' && i + 1 < expression.Length && expression[i + 1] is '=' or '~')
            {
                op = "!";
                inValue = true;
                i++;
            }
            else if (!inValue && c is '=' or '~')
            {
                op = c.ToString();
                inValue = true;
            }
            else
            {
                (inValue ? value : prop).Append(c);
            }
        }
        Flush();
        return terms;
    }

    // ---- which project a check targets ----

    /// <summary>One <c>dotnet test</c> segment of a check: its text, the name terms, and the test projects (folder paths) it targets.</summary>
    public sealed record Target(string Segment, List<Term> Terms, List<string> Projects);

    /// <summary>The <c>dotnet test</c> segments of <paramref name="check"/> that name a test project or solution found on disk, following <c>cd</c>.</summary>
    public static List<Target> Targets(string check, string plane)
    {
        var found = new List<Target>();
        if (!Directory.Exists(plane))
        {
            return found;
        }
        string? here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plane));
        foreach (var segment in CheckPaths.Segments(check))
        {
            var tokens = CheckPaths.Tokenize(segment);
            if (tokens.Count == 0 || here is null)
            {
                continue;
            }
            if (CheckPaths.IsCd(tokens))
            {
                here = CheckPaths.CdTarget(tokens) is { } target ? CheckPaths.Follow(here, target) : here;
                continue;
            }
            var start = tokens[0].Value == "&" ? 1 : 0;
            if (tokens.Count < start + 2
                || !Path.GetFileNameWithoutExtension(tokens[start].Value).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                || !tokens[start + 1].Value.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var words = tokens.Skip(start + 2).TakeWhile(t => t.Value != "--").Select(t => t.Value).ToList();
            var projects = new List<string>();
            var paths = CheckPaths.PathsOf(tokens);
            foreach (var path in paths.Count == 0 ? [here] : paths)
            {
                projects.AddRange(ProjectsOf(path, here));
            }
            projects = [.. projects.Distinct(StringComparer.OrdinalIgnoreCase)];
            if (projects.Count > 0)
            {
                found.Add(new Target(segment.Trim(), TermsOf(words), projects));
            }
        }
        return found;
    }

    private static IEnumerable<string> ProjectsOf(string path, string here)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path, here);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }
        try
        {
            if (File.Exists(full))
            {
                var ext = Path.GetExtension(full);
                return ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? [Path.GetDirectoryName(full)!] : SolutionTests(full);
            }
            if (Directory.Exists(full))
            {
                var files = Directory.GetFiles(full);
                if (files.Any(f => Path.GetExtension(f).Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
                {
                    return [full];
                }
                return files.Where(f => Path.GetExtension(f) is ".sln" or ".slnx").Take(1).SelectMany(SolutionTests).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    // Every project of a solution that is a test project: it references a test SDK or framework, or is named for tests.
    private static List<string> SolutionTests(string solution)
    {
        var found = new List<string>();
        var text = File.ReadAllText(solution);
        var dir = Path.GetDirectoryName(solution)!;
        var regex = Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? SlnxProject() : SlnProject();
        foreach (Match m in regex.Matches(text))
        {
            var csproj = Path.GetFullPath(m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar), dir);
            if (File.Exists(csproj) && IsTestProject(csproj))
            {
                found.Add(Path.GetDirectoryName(csproj)!);
            }
        }
        return found;
    }

    private static bool IsTestProject(string csproj)
    {
        var name = Path.GetFileNameWithoutExtension(csproj);
        if (name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Test", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var text = File.ReadAllText(csproj);
        return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal) || text.Contains("xunit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("IsTestProject", StringComparison.Ordinal);
    }

    [GeneratedRegex("""Path\s*=\s*"([^"]+\.csproj)""", RegexOptions.IgnoreCase)]
    private static partial Regex SlnxProject();

    [GeneratedRegex(@"""([^""]+\.csproj)""", RegexOptions.IgnoreCase)]
    private static partial Regex SlnProject();

    // ---- the tests of a project ----

    /// <summary>The tests the sources of the project folders declare; null when a folder cannot be read.</summary>
    public static Project? Load(IEnumerable<string> folders, string plane)
    {
        var folderList = folders.ToList();
        var project = new Project
        {
            Name = string.Join(", ", folderList.Select(f => Path.GetRelativePath(plane, f).Replace('\\', '/'))),
        };
        try
        {
            foreach (var folder in folderList)
            {
                foreach (var file in Sources(folder))
                {
                    var parsed = Parsed(file);
                    project.Files.Add((file, parsed.Text));
                    project.Types.AddRange(parsed.Types);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var byName = project.Types.ToLookup(t => t.Simple, StringComparer.Ordinal);
        foreach (var type in project.Types)
        {
            var methods = Inherited(type, byName, []);
            if (methods.Count == 0)
            {
                continue;
            }
            project.TestClasses.Add(type.Simple);
            if (type.Abstract)
            {
                continue;
            }
            var forms = Forms(type);
            project.ClassNames.AddRange(forms);
            foreach (var method in methods)
            {
                project.MethodNames.Add(method);
                project.FullNames.AddRange(forms.Select(f => $"{f}.{method}"));
            }
        }
        return project;
    }

    // A source file's text and the types it declares, read and lexed once while the file stands (its length and write time): every
    // plan check read and lexed every test source of the projects it names again, and FilterTerms.Lex was 45 of the 408 MB the node
    // put on the large object heap in 30 s (DE1, 2026-11-28, pilot #1443). A type is only read once declared, so one is shared.
    // Each plane is a folder of its own, so the files of planes long gone would stay: past MaxParsed files the cache starts again.
    private sealed record ParsedFile(long Length, DateTime WrittenUtc, string Text, TypeInfo[] Types);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ParsedFile> s_parsed = new(StringComparer.Ordinal);

    private const int MaxParsed = 4000;

    private static ParsedFile Parsed(string file)
    {
        var info = new FileInfo(file);
        var (length, written) = (info.Length, info.LastWriteTimeUtc);
        if (s_parsed.TryGetValue(file, out var kept) && kept.Length == length && kept.WrittenUtc == written)
        {
            return kept;
        }
        var text = File.ReadAllText(file);
        var types = new List<TypeInfo>();
        Declare(text, file, types);
        var parsed = new ParsedFile(length, written, text, [.. types]);
        if (s_parsed.Count >= MaxParsed)
        {
            s_parsed.Clear();
        }
        s_parsed[file] = parsed;
        return parsed;
    }

    internal static List<string> Inherited(TypeInfo type, ILookup<string, TypeInfo> byName, HashSet<TypeInfo> seen)
    {
        var methods = new List<string>(type.Methods);
        if (!seen.Add(type))
        {
            return methods;
        }
        foreach (var b in type.Bases)
        {
            foreach (var baseType in byName[b])
            {
                methods.AddRange(Inherited(baseType, byName, seen));
            }
        }
        return [.. methods.Distinct(StringComparer.Ordinal)];
    }

    private static List<string> Forms(TypeInfo type)
    {
        var plus = type.Chain;
        var dot = type.Chain.Replace('+', '.');
        var ns = type.Ns.Length == 0 ? "" : type.Ns + ".";
        return [.. new[] { ns + plus, ns + dot }.Distinct(StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Sources(string folder)
    {
        var queue = new Queue<string>();
        queue.Enqueue(folder);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            foreach (var file in Directory.GetFiles(dir, "*.cs").Order(StringComparer.Ordinal))
            {
                yield return file;
            }
            foreach (var sub in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(sub);
                if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.'))
                {
                    queue.Enqueue(sub);
                }
            }
        }
    }

    // ---- the plain C# reader ----

    private enum Kind { Word, Punct, Literal }

    private readonly record struct Tok(Kind Kind, string Text)
    {
        public bool Is(string text) => Text == text && Kind != Kind.Literal;
    }

    private static List<Tok> Lex(string s)
    {
        var tokens = new List<Tok>();
        var i = 0;
        var lineStart = true;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n')
            {
                lineStart = true;
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '#' && lineStart)
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
            }
            else
            {
                lineStart = false;
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                }
                else if (c is '"' or '$' or '@' && StringStart(s, i) is { } start)
                {
                    i = SkipString(s, i, start);
                    tokens.Add(new Tok(Kind.Literal, "\"\""));
                }
                else if (c == '\'')
                {
                    i++;
                    while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                    {
                        i += s[i] == '\\' ? 2 : 1;
                    }
                    i++;
                    tokens.Add(new Tok(Kind.Literal, "''"));
                }
                else if (char.IsLetterOrDigit(c) || c == '_' || c == '@')
                {
                    var from = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '@'))
                    {
                        i++;
                    }
                    tokens.Add(new Tok(Kind.Word, s[from..i].TrimStart('@')));
                }
                else
                {
                    tokens.Add(new Tok(Kind.Punct, c.ToString()));
                    i++;
                }
            }
        }
        return tokens;
    }

    // The index of the opening quote when a string literal (regular, verbatim, interpolated, raw) starts at i; null otherwise.
    private static (int Quote, bool Verbatim)? StringStart(string s, int i)
    {
        var j = i;
        var verbatim = false;
        while (j < s.Length && s[j] is '$' or '@')
        {
            verbatim |= s[j] == '@';
            j++;
        }
        return j < s.Length && s[j] == '"' ? (j, verbatim) : null;
    }

    private static int SkipString(string s, int from, (int Quote, bool Verbatim) start)
    {
        var i = start.Quote;
        var quotes = 0;
        while (i + quotes < s.Length && s[i + quotes] == '"')
        {
            quotes++;
        }
        // A run of three or more quotes opens a raw string only outside a verbatim one: @""" is a verbatim string
        // whose content starts with an escaped quote, and it ends at the first lone quote after the "" pairs.
        if (quotes >= 3 && !start.Verbatim)
        {
            var close = s.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
            return close < 0 ? s.Length : close + quotes;
        }
        i++;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (start.Verbatim && i + 1 < s.Length && s[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i += s[i] == '\\' && !start.Verbatim ? 2 : 1;
        }
        return s.Length;
    }

    private sealed record Scope(bool IsType, string Name, int Depth, TypeInfo? Info);

    private static readonly HashSet<string> TypeWords = ["class", "struct", "interface", "record"];

    private static void Declare(string source, string file, List<TypeInfo> types)
    {
        var t = Lex(source);
        var scopes = new List<Scope>();
        var depth = 0;
        var fileNs = "";
        Scope? pending = null;
        var attrs = new List<string>();

        string Ns() => string.Join('.', new[] { fileNs }.Concat(scopes.Where(s => !s.IsType).Select(s => s.Name)).Where(n => n.Length > 0));

        for (var i = 0; i < t.Count; i++)
        {
            var tok = t[i];
            if (tok.Is("{"))
            {
                depth++;
                if (pending is not null)
                {
                    scopes.Add(pending with { Depth = depth });
                    pending = null;
                }
            }
            else if (tok.Is("}"))
            {
                if (scopes.Count > 0 && scopes[^1].Depth == depth)
                {
                    scopes.RemoveAt(scopes.Count - 1);
                }
                depth--;
            }
            else if (tok.Is("namespace") && i + 1 < t.Count && t[i + 1].Kind == Kind.Word)
            {
                var j = i + 1;
                var name = new StringBuilder();
                while (j < t.Count && !t[j].Is(";") && !t[j].Is("{"))
                {
                    name.Append(t[j].Text);
                    j++;
                }
                if (j < t.Count && t[j].Is(";"))
                {
                    fileNs = name.ToString();
                }
                else
                {
                    pending = new Scope(false, name.ToString(), 0, null);
                    j--;
                }
                i = j;
            }
            else if (tok.Kind == Kind.Word && TypeWords.Contains(tok.Text) && ReadType(t, i, scopes, Ns(), file) is { } read)
            {
                types.Add(read.Info);
                if (read.Body)
                {
                    pending = new Scope(true, read.Info.Simple, 0, read.Info);
                }
                i = read.Next - 1;
            }
            else if (tok.Is("[") && scopes.Count > 0 && scopes[^1].IsType && scopes[^1].Depth == depth
                && i > 0 && (t[i - 1].Is("{") || t[i - 1].Is("}") || t[i - 1].Is(";") || t[i - 1].Is("]")))
            {
                i = ReadAttributes(t, i, attrs);
                if (i + 1 < t.Count && !t[i + 1].Is("["))
                {
                    if (attrs.Any(IsTestAttribute) && MethodName(t, i + 1) is { } method)
                    {
                        scopes[^1].Info!.Methods.Add(method);
                    }
                    attrs.Clear();
                }
            }
        }
    }

    private static bool IsTestAttribute(string name)
    {
        var n = name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
        return n.EndsWith("Fact", StringComparison.Ordinal) || n.EndsWith("Theory", StringComparison.Ordinal) || n is "Test" or "TestCase" or "TestMethod";
    }

    // Reads the attribute list at t[at] (a '['); adds the attribute names, returns the index of its closing ']'.
    private static int ReadAttributes(List<Tok> t, int at, List<string> names)
    {
        var depth = 0;
        var expectName = true;
        string? last = null;
        for (var i = at; i < t.Count; i++)
        {
            var tok = t[i];
            if (tok.Is("[") || tok.Is("(") || tok.Is("{"))
            {
                depth++;
            }
            else if (tok.Is("]") || tok.Is(")") || tok.Is("}"))
            {
                depth--;
                if (depth == 0)
                {
                    if (last is not null)
                    {
                        names.Add(last);
                    }
                    return i;
                }
            }
            else if (depth == 1 && tok.Is(","))
            {
                if (last is not null)
                {
                    names.Add(last);
                }
                last = null;
                expectName = true;
            }
            else if (depth == 1 && expectName && tok.Kind == Kind.Word)
            {
                last = tok.Text;
            }
            else if (depth == 1 && tok.Is(":"))
            {
                last = null;
            }
        }
        return t.Count - 1;
    }

    // The method a member header starting at t[from] declares: its name before the first '(', or null for a property, field or type.
    private static string? MethodName(List<Tok> t, int from)
    {
        for (var i = from; i < t.Count; i++)
        {
            if (t[i].Is("{") || t[i].Is(";") || t[i].Is("=") || t[i].Is("}"))
            {
                return null;
            }
            if (t[i].Is("("))
            {
                var k = i - 1;
                if (k >= from && t[k].Is(">"))
                {
                    var angle = 0;
                    for (; k >= from; k--)
                    {
                        angle += t[k].Is(">") ? 1 : t[k].Is("<") ? -1 : 0;
                        if (angle == 0)
                        {
                            break;
                        }
                    }
                    k--;
                }
                return k >= from && t[k].Kind == Kind.Word ? t[k].Text : null;
            }
        }
        return null;
    }

    private sealed record ReadResult(TypeInfo Info, bool Body, int Next);

    // A type header at t[at] (class/struct/interface/record): its name, bases and whether a body follows; null when it is no declaration.
    private static ReadResult? ReadType(List<Tok> t, int at, List<Scope> scopes, string ns, string file)
    {
        var j = at + 1;
        if (t[at].Text == "record" && j < t.Count && (t[j].Is("class") || t[j].Is("struct")))
        {
            j++;
        }
        if (j + 1 >= t.Count || t[j].Kind != Kind.Word || !(t[j + 1].Is("<") || t[j + 1].Is(":") || t[j + 1].Is("{") || t[j + 1].Is("(") || t[j + 1].Is(";") || t[j + 1].Is("where")))
        {
            return null;
        }
        var info = new TypeInfo
        {
            Ns = ns,
            Simple = t[j].Text,
            File = file,
            Chain = string.Join('+', scopes.Where(s => s.IsType).Select(s => s.Name).Append(t[j].Text)),
            Abstract = Enumerable.Range(Math.Max(0, at - 5), Math.Min(5, at)).Reverse().Select(k => t[k]).TakeWhile(x => !x.Is(";") && !x.Is("{") && !x.Is("}") && !x.Is("]")).Any(x => x.Is("abstract")),
        };
        j++;
        if (t[j].Is("<"))
        {
            j = SkipPair(t, j, "<", ">");
        }
        if (j < t.Count && t[j].Is("("))
        {
            j = SkipPair(t, j, "(", ")");
        }
        if (j < t.Count && t[j].Is(":"))
        {
            j++;
            string? current = null;
            while (j < t.Count && !t[j].Is("{") && !t[j].Is(";") && !t[j].Is("where"))
            {
                if (t[j].Is("<"))
                {
                    j = SkipPair(t, j, "<", ">");
                    continue;
                }
                if (t[j].Is("("))
                {
                    j = SkipPair(t, j, "(", ")");
                    continue;
                }
                if (t[j].Is(","))
                {
                    if (current is not null)
                    {
                        info.Bases.Add(current);
                    }
                    current = null;
                }
                else if (t[j].Kind == Kind.Word)
                {
                    current = t[j].Text;
                }
                j++;
            }
            if (current is not null)
            {
                info.Bases.Add(current);
            }
        }
        while (j < t.Count && !t[j].Is("{") && !t[j].Is(";"))
        {
            j++;
        }
        return new ReadResult(info, j < t.Count && t[j].Is("{"), j);
    }

    private static int SkipPair(List<Tok> t, int at, string open, string close)
    {
        var depth = 0;
        for (var i = at; i < t.Count; i++)
        {
            depth += t[i].Is(open) ? 1 : t[i].Is(close) ? -1 : 0;
            if (depth == 0)
            {
                return i + 1;
            }
        }
        return t.Count;
    }

    // ---- matching ----

    /// <summary>Whether <paramref name="term"/> matches a test of <paramref name="project"/>, as VSTest matches (<c>~</c> contains, <c>=</c> exact, both ignoring case) or the Platform's wildcard does.</summary>
    public static bool Matches(Term term, Project project)
    {
        IEnumerable<string> pool = term.Property switch
        {
            "classname" or TestFilters.Class => project.ClassNames,
            "name" => project.FullNames.Concat(project.MethodNames),
            _ => project.FullNames,
        };
        return pool.Any(n => Fits(term, n));
    }

    private static bool Fits(Term term, string name)
    {
        var v = term.Value;
        switch (term.Op)
        {
            case "~":
                return name.Contains(v, StringComparison.OrdinalIgnoreCase);
            case "=":
                var paren = v.IndexOf('(');
                return paren > 0 ? name.Equals(v[..paren], StringComparison.OrdinalIgnoreCase) : name.Equals(v, StringComparison.OrdinalIgnoreCase);
            default:
                return Wildcard(v).IsMatch(name);
        }
    }

    private static Regex Wildcard(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether a class the leg declares with new-tests (<paramref name="declared"/>) would be matched by <paramref name="term"/>, given its namespace and methods are not known yet.</summary>
    public static bool DeclaredMatches(Term term, string declared)
    {
        var v = term.Value;
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        return term.Op switch
        {
            "~" => declared.Contains(v, ic) || v.Contains(declared, ic),
            "=" => v.Equals(declared, ic) || v.StartsWith(declared + ".", ic) || v.EndsWith("." + declared, ic) || v.Contains("." + declared + ".", ic),
            _ => Wildcard(v) is var w && (w.IsMatch(declared) || w.IsMatch("N." + declared) || w.IsMatch("N." + declared + ".M")),
        };
    }

    // ---- the refusals ----

    /// <summary>The class names a leg declares with new-tests: words split on spaces, commas and semicolons.</summary>
    public static List<string> ParseDeclared(string? value) =>
        value is null ? [] : [.. value.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>The class names a done-when keeps under <see cref="NewTestsMark"/>.</summary>
    public static List<string> DeclaredIn(string doneWhen)
    {
        foreach (var line in doneWhen.Split('\n'))
        {
            if (line.StartsWith(NewTestsMark, StringComparison.Ordinal))
            {
                return ParseDeclared(line[NewTestsMark.Length..]);
            }
        }
        return [];
    }

    /// <summary>The first name term of <paramref name="command"/>'s <c>dotnet test</c> filter that matches no test of the project it targets, with that project's name. Null when every term matches or nothing can be read (what <see cref="Refusal"/> reads, without the leg's declared classes).</summary>
    public static (Term Term, string Project)? Unmatched(string command, string cwd)
    {
        foreach (var target in Targets(command, cwd))
        {
            if (target.Terms.Count == 0 || Load(target.Projects, cwd) is not { } project || project.FullNames.Count == 0)
            {
                continue;
            }
            foreach (var term in target.Terms)
            {
                if (!Matches(term, project))
                {
                    return (term, project.Name);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The refusal for the first name term of <paramref name="check"/> that matches no test of the project its <c>dotnet test</c> targets
    /// and no class in <paramref name="declared"/>: the term, the project and the nearest test classes. Null when every term matches, a
    /// project cannot be found or read, or it declares no test at all (the reader then knows nothing worth refusing on).
    /// </summary>
    public static string? Refusal(string check, string plane, IReadOnlyCollection<string>? declared = null)
    {
        foreach (var target in Targets(check, plane))
        {
            if (target.Terms.Count == 0 || Load(target.Projects, plane) is not { } project || project.FullNames.Count == 0)
            {
                continue;
            }
            foreach (var term in target.Terms)
            {
                if (Matches(term, project) || (declared ?? []).Any(d => DeclaredMatches(term, d)))
                {
                    continue;
                }
                var nearest = Nearest(term.Value, project.TestClasses);
                return $"the check `{target.Segment}` filters with {term.Text}, which matches no test in {project.Name}: that filter runs nothing and still exits 0, so the tests it names would be reported as passed that never ran. " +
                    (nearest.Count > 0 ? $"The nearest test classes are {string.Join(", ", nearest)}. " : "") +
                    $"A test class this leg adds is declared with {NewTestsKey}=<Class> (--new-tests \"<Class> ...\")";
            }
        }
        return null;
    }

    /// <summary>
    /// The refusal for a done-when that names, as a whole word ending in Test or Tests, a class the targeted test project declares and that holds
    /// no test (a static helper): it names the class and the test classes whose files use it. A class in <paramref name="declared"/> or one the
    /// project does not declare (the leg adds it) is never refused. Null when none.
    /// </summary>
    public static string? DoneWhenRefusal(string doneWhen, string check, string plane, IReadOnlyCollection<string>? declared = null)
    {
        var words = DoneWord().Matches(doneWhen).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
        if (words.Count == 0)
        {
            return null;
        }
        foreach (var target in Targets(check, plane))
        {
            if (Load(target.Projects, plane) is not { } project || project.FullNames.Count == 0)
            {
                continue;
            }
            foreach (var word in words)
            {
                var declaredTypes = project.Types.Where(t => t.Simple == word).ToList();
                if (declaredTypes.Count == 0 || project.TestClasses.Contains(word) || (declared ?? []).Contains(word, StringComparer.Ordinal))
                {
                    continue;
                }
                var use = new Regex($@"\b{Regex.Escape(word)}\b");
                var users = project.Types
                    .Where(t => project.TestClasses.Contains(t.Simple)
                        && project.Files.Any(f => f.Path == t.File && use.IsMatch(f.Text)))
                    .Select(t => t.Simple).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5).ToList();
                return $"the done-when names {word}, a class of {project.Name} that holds no test (a helper), so a filter on it runs nothing" +
                    (users.Count > 0 ? $"; the test classes that use it are {string.Join(", ", users)}: name one of them" : "") +
                    $". A class this leg adds is declared with {NewTestsKey}=<Class>";
            }
        }
        return null;
    }

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*Tests?\b")]
    private static partial Regex DoneWord();

    private static List<string> Nearest(string value, IEnumerable<string> classes)
    {
        var v = value.ToLowerInvariant();
        return [.. classes.Select(c => (Class: c, Score: LongestCommon(v, c.ToLowerInvariant())))
            .Where(x => x.Score >= 3).OrderByDescending(x => x.Score).ThenBy(x => x.Class, StringComparer.Ordinal).Take(3).Select(x => x.Class)];
    }

    private static int LongestCommon(string a, string b)
    {
        var best = 0;
        var row = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = b.Length; j >= 1; j--)
            {
                row[j] = a[i - 1] == b[j - 1] ? row[j - 1] + 1 : 0;
                best = Math.Max(best, row[j]);
            }
        }
        return best;
    }
}
