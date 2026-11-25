using System.Text;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>The node command a refused pilot command would have sent: what <c>PilotCommand</c>'s own Send passes for it.</summary>
public sealed record PressedCommand(string Verb, string Target, string[] Args, string? Why);

/// <summary>Why a refused command's card cannot run it when pressed.</summary>
public enum PressBlock
{
    /// <summary>The words do not make a command of the table: an unclosed quote, an option it does not know, missing words, a target only the plane gives.</summary>
    Unparsable,

    /// <summary>Not a command the node takes through a button: status, cards, a verb no row has, or no pilot call at all.</summary>
    NotANodeVerb,

    /// <summary>A second pilot call anywhere in the line: a press runs one command, and the first's effect is what a later check reads.</summary>
    MoreThanOneCommand,
}

/// <summary>
/// A refused pilot command read back into the node command it would have sent, or the reason it cannot be pressed (pilot #1353).
/// <see cref="Command"/> is set whenever the first pilot call matched a row and parsed, even when a second call blocks the press, so a
/// later leg can check the first one's effect.
/// </summary>
public sealed record RefusedReading(PressedCommand? Command, PressBlock? Blocked, string Detail)
{
    /// <summary>Whether a button may send <see cref="Command"/>.</summary>
    public bool CanPress => Command is not null && Blocked is null;
}

/// <summary>
/// The node's own table of CLI commands a card can press again. <c>Pilot.Node</c> takes no reference to <c>Pilot.Commands</c>, so each row
/// repeats what that command's Send passes as verb, target, args and why, and a guard test (<c>RefusedCommandTests</c>) runs each row's
/// <see cref="Samples"/> through the real CLI to keep the table in step.
/// </summary>
public static partial class RefusedCommand
{
    private enum Target
    {
        /// <summary>The first word is a flight code: <c>flight/&lt;code&gt;</c>.</summary>
        Flight,

        /// <summary>The leg is <c>--leg &lt;code&gt;/&lt;leg&gt;</c> (finish also takes it as its first word): <c>flight/&lt;code&gt;/&lt;leg&gt;</c>. Inside a plane the CLI finds it from the folder, which the node cannot.</summary>
        Leg,

        /// <summary>The first word is a session row id: <c>session/&lt;id&gt;</c>.</summary>
        Session,
    }

    private enum WhyGoes
    {
        /// <summary>The command has no --why.</summary>
        Nowhere,

        /// <summary>The command's --why option: <see cref="CommandRequest.Why"/>.</summary>
        Why,

        /// <summary>The command's positional words are its why (an MCP call names them <c>why</c>): they are the args.</summary>
        Args,
    }

    private sealed record Row(string Path, string Verb, Target Target, int MinWords, int MaxWords, WhyGoes WhyTo, string[] Flags, string[] Values, Func<string[], Dictionary<string, string>, string[]> ArgsOf, string[] Sample);

    private static readonly string[] None = [];

    private static string[] Words(string[] words, Dictionary<string, string> options) =>
        [.. words, .. options.TryGetValue("--why", out var why) ? [why] : Array.Empty<string>()];

    private static readonly Row[] Table =
    [
        new("flight allow-round", Verbs.AllowRound, Target.Flight, 0, int.MaxValue, WhyGoes.Args, None, None, Words, ["flight", "allow-round", "PIL1", "one more round is worth it"]),
        new("flight merge", Verbs.Merge, Target.Flight, 1, 1, WhyGoes.Why, ["--squash", "--rebase", "--merge"], ["--method"], MergeArgs, ["flight", "merge", "PIL1", "abc123", "--squash", "--why", "job j9 is green"]),
        new("flight land", Verbs.Land, Target.Flight, 0, 0, WhyGoes.Why, None, None, (_, _) => [], ["flight", "land", "PIL1", "--why", "merged by hand"]),
        new("flight clear", Verbs.Clear, Target.Flight, 0, int.MaxValue, WhyGoes.Args, None, None, Words, ["flight", "clear", "PIL1", "the notes are minor"]),
        new("flight ground", Verbs.Ground, Target.Flight, 1, int.MaxValue, WhyGoes.Args, None, None, Words, ["flight", "ground", "PIL1", "the owner asked for a hold"]),
        new("flight unground", Verbs.Unground, Target.Flight, 0, int.MaxValue, WhyGoes.Args, None, None, Words, ["flight", "unground", "PIL1", "the hold is released"]),
        new("flight connect", Verbs.Connect, Target.Flight, 1, 1, WhyGoes.Nowhere, None, None, (w, _) => w, ["flight", "connect", "PIL1", "PIL2"]),
        new("flight disconnect", Verbs.Disconnect, Target.Flight, 1, 1, WhyGoes.Why, [Verbs.AnywayFlag], None, (w, o) => [.. w, .. o.ContainsKey(Verbs.AnywayFlag) ? [Verbs.AnywayFlag] : Array.Empty<string>()], ["flight", "disconnect", "PIL1", "PIL2", Verbs.AnywayFlag, "--why", "the owner chose it"]),
        new("flight ci-budget", Verbs.CiBudget, Target.Flight, 1, 1, WhyGoes.Why, None, None, (w, _) => w, ["flight", "ci-budget", "PIL1", Verbs.CiContinue, "--why", "the budget is fine"]),
        new("leg finish", Verbs.FinishLeg, Target.Leg, 1, int.MaxValue, WhyGoes.Nowhere, None, ["--leg"], (w, _) => w, ["leg", "finish", "done", "all done", "--leg", "PIL1/a"]),
        new("leg go-around", Verbs.GoAround, Target.Leg, 1, int.MaxValue, WhyGoes.Why, None, ["--leg"], (w, _) => [string.Join(' ', w)], ["leg", "go-around", "the tests were red", "--leg", "PIL1/a", "--why", "round two"]),
        new("session end", Verbs.EndSession, Target.Session, 1, int.MaxValue, WhyGoes.Args, None, None, (w, _) => [string.Join(' ', w)], ["session", "end", "s1", "not needed any more"]),
    ];

    // The CLI's own: the merge method, from --method or else the flag that names it, else the node's default.
    private static string[] MergeArgs(string[] words, Dictionary<string, string> options)
    {
        var how = options.TryGetValue("--method", out var method) ? method
            : options.ContainsKey("--rebase") ? "rebase" : options.ContainsKey("--merge") ? "merge" : options.ContainsKey("--squash") ? "squash" : null;
        return how is null ? words : [.. words, how];
    }

    /// <summary>Each row's path (<c>flight merge</c>) and a sample of its words after <c>pilot</c>, as <c>PilotCommand.RunAsync</c> takes them.</summary>
    public static IReadOnlyList<(string Path, string[] Argv)> Samples => [.. Table.Select(r => (r.Path, r.Sample))];

    /// <summary>A sample as the line a shell would run it from: words with spaces quoted.</summary>
    public static string SampleLine(string[] argv) => "pilot " + string.Join(' ', argv.Select(w => w.Contains(' ') ? $"\"{w}\"" : w));

    /// <summary>The commands of a pilot call group's verb that have a row, by path.</summary>
    public static IEnumerable<string> Paths => Table.Select(r => r.Path);

    // <code>/<leg>, as pilot leg finish PIL1157/job-fails-empty-terms done names it.
    [GeneratedRegex(@"^[A-Za-z]+\d+/[a-z0-9][a-z0-9-]*$")]
    private static partial Regex LegWord();

    /// <summary>Reads the first pilot call of <paramref name="text"/> (a Bash or PowerShell line, or an MCP call as <c>StreamSession</c> writes it).</summary>
    public static RefusedReading Read(string text)
    {
        if (Watchtower.PilotCall(text) is not { } call)
        {
            return new RefusedReading(null, PressBlock.NotANodeVerb, "there is no pilot command in it");
        }
        var verb = call.Groups["verb"].Value.ToLowerInvariant();
        var restGroup = call.Groups["rest"];
        // Only pilot's own words: what follows a shell operator (2>&1, && …, | head) is another command's.
        var own = Watchtower.OwnWords(restGroup.Value);
        if (Split(own) is not { } tokens)
        {
            return new RefusedReading(null, PressBlock.Unparsable, "a quote is never closed");
        }
        var path = tokens.Count > 0 && !tokens[0].Quoted ? $"{verb} {tokens[0].Text}" : verb;
        var row = Table.FirstOrDefault(r => r.Path == path);
        if (row is null)
        {
            return new RefusedReading(null, PressBlock.NotANodeVerb, $"pilot {path} is not a command a button sends");
        }
        var (command, why) = Build(row, tokens.Skip(1).ToList());
        if (command is null)
        {
            return new RefusedReading(null, PressBlock.Unparsable, why);
        }
        // A second pilot call after what the first one owns: the rest of the line and the lines below it.
        var after = text[(restGroup.Index + own.Length)..];
        return Watchtower.PilotCall(after) is not null
            ? new RefusedReading(command, PressBlock.MoreThanOneCommand, "the line runs more than one pilot command")
            : new RefusedReading(command, null, "");
    }

    private static (PressedCommand? Command, string Why) Build(Row row, List<(string Text, bool Quoted)> tokens)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        var more = true;
        for (var i = 0; i < tokens.Count; i++)
        {
            var (word, quoted) = tokens[i];
            if (more && !quoted && word == "--")
            {
                more = false;
            }
            else if (more && !quoted && word.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = word.IndexOf('=');
                var name = eq > 0 ? word[..eq] : word;
                if (name == "--why" && row.WhyTo != WhyGoes.Nowhere || row.Values.Contains(name))
                {
                    string? value = eq > 0 ? word[(eq + 1)..] : i + 1 < tokens.Count ? tokens[++i].Text : null;
                    if (value is null)
                    {
                        return (null, $"{name} needs a value");
                    }
                    if (value.Length > 0)
                    {
                        options[name] = value;
                    }
                }
                else if (row.Flags.Contains(name) && eq < 0)
                {
                    options[name] = "";
                }
                else
                {
                    return (null, $"pilot {row.Path} takes no option {name}");
                }
            }
            else
            {
                positionals.Add(word);
            }
        }

        string target;
        switch (row.Target)
        {
            case Target.Flight:
                if (positionals.Count == 0)
                {
                    return (null, "it names no flight");
                }
                target = Verbs.FlightTarget(positionals[0]);
                positionals.RemoveAt(0);
                break;
            case Target.Session:
                if (positionals.Count == 0)
                {
                    return (null, "it names no session");
                }
                target = Verbs.SessionTarget(positionals[0]);
                positionals.RemoveAt(0);
                break;
            default:
                // As the CLI: --leg names it, the flight's prefix before it. A finish also takes it as its first word (as c24577 typed it).
                var named = options.GetValueOrDefault("--leg");
                if (named is null && row.Verb == Verbs.FinishLeg && positionals.Count > 0 && LegWord().IsMatch(positionals[0]))
                {
                    named = positionals[0];
                    positionals.RemoveAt(0);
                }
                if (named is null)
                {
                    return (null, "it names no leg (inside a plane the CLI finds it from the folder; the node cannot)");
                }
                target = Verbs.FlightTargetPrefix + named;
                break;
        }

        // An MCP call's why is the command's positional words for the ones that have no --why option, so it counts toward them.
        var words = row.WhyTo == WhyGoes.Args ? Words([.. positionals], options) : [.. positionals];
        if (words.Length < row.MinWords)
        {
            return (null, row.MinWords == 1 && row.MaxWords == int.MaxValue ? "it is missing its words" : $"it is missing {row.MinWords - words.Length} word(s)");
        }
        if (words.Length > row.MaxWords)
        {
            return (null, $"it has {words.Length - row.MaxWords} word(s) more than pilot {row.Path} takes");
        }
        if (row.Verb == Verbs.CiBudget && words[0] is not (Verbs.CiContinue or Verbs.CiLandGreen or Verbs.CiSplit))
        {
            return (null, $"{words[0]} is not continue, land-green or split");
        }
        if (row.Verb == Verbs.Merge && options.TryGetValue("--method", out var method) && method is not ("squash" or "rebase" or "merge"))
        {
            return (null, $"--method {method} is not squash, rebase or merge");
        }
        var why = row.WhyTo == WhyGoes.Why && options.TryGetValue("--why", out var given) ? given : null;
        return (new PressedCommand(row.Verb, target, row.ArgsOf(words, options), why), "");
    }

    // The words as a shell reads them: quotes keep words together and come off; an unclosed quote is null.
    private static List<(string Text, bool Quoted)>? Split(string text)
    {
        var words = new List<(string, bool)>();
        var word = new StringBuilder();
        var inWord = false;
        var quoted = false;
        var quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inWord = quoted = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add((word.ToString(), quoted));
                    word.Clear();
                    inWord = quoted = false;
                }
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }
        if (quote != '\0')
        {
            return null;
        }
        if (inWord)
        {
            words.Add((word.ToString(), quoted));
        }
        return words;
    }
}
