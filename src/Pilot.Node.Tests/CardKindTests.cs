using System.Reflection;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// No card goes up without saying what is wrong and why (PIL474), and the next one added cannot forget to. <see cref="CardKindCatalog"/>
/// lists every kind the node raises with its bin and the test that pins its title and choices; this reads the <c>RaiseCard</c>
/// calls under Pilot.Node and fails on a kind the catalog does not know, a catalogued kind nothing raises, a title or a button
/// whose words are generic, and a kind the summary table (<see cref="CardSummaries"/>) leaves to its generic fallback.
/// </summary>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed class CardKindTests
{
    internal static readonly Lazy<string> Src = new(FindSrc);

    internal static readonly Lazy<List<(string File, string Source)>> Sources = new(() =>
        [.. Directory.EnumerateFiles(Path.Combine(Src.Value, "Pilot.Node"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (File: Path.GetRelativePath(Path.Combine(Src.Value, "Pilot.Node"), f).Replace('\\', '/'), Source: System.IO.File.ReadAllText(f)))]);

    internal static readonly Lazy<List<CardCall>> Calls = new(() => [.. Sources.Value.SelectMany(s => CardSource.Calls(s.File, s.Source))]);

    private static string FindSrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pilot.slnx")))
            {
                return dir.FullName;
            }
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }
        throw new InvalidOperationException("no Pilot.slnx above the test's directory");
    }

    private static string Compact(string expr) => Regex.Replace(expr, @"\s+", " ");

    private static readonly Assembly[] Assemblies = [typeof(NodeState).Assembly, typeof(CardRow).Assembly];

    /// <summary>The value of the constant a kind expression names, and the <c>Type.Name</c> a test would write for it; null for a variable.</summary>
    internal static (string Value, string Token)? Resolve(string file, string expr)
    {
        expr = Compact(expr);
        if (Regex.Match(expr, "^\"([^\"]*)\"$") is { Success: true } literal)
        {
            return (literal.Groups[1].Value, $"\"{literal.Groups[1].Value}\"");
        }
        var named = Regex.Match(expr, @"^(?:(\w+)\.)?(\w+)$");
        if (!named.Success)
        {
            return null;
        }
        var typeNames = named.Groups[1].Success
            ? [named.Groups[1].Value]
            : Regex.Matches(CardSource.Mask(Sources.Value.First(s => s.File == file).Source), @"\b(?:class|record|struct)\s+(\w+)").Select(m => m.Groups[1].Value).ToArray();
        foreach (var type in Assemblies.SelectMany(a => a.GetTypes()).Where(t => typeNames.Contains(t.Name)))
        {
            var field = type.GetField(named.Groups[2].Value, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field is { IsLiteral: true } && field.GetRawConstantValue() is string value)
            {
                return (value, $"{type.Name}.{field.Name}");
            }
        }
        return null;
    }

    [Fact]
    public void TheCatalogNamesEachKindOnce()
    {
        CardKindCatalog.All.GroupBy(e => e.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
        CardKindCatalog.All.ShouldAllBe(e => e.Files.Length > 0 && e.Pin.Contains('.'));
    }

    [Fact]
    public void EveryCardRaisedUnderPilotNodeIsInTheCatalogWithItsBinAndItsFile()
    {
        // The scan finds what is there: far fewer calls than the node has is a scanner that lost its way, not a node with fewer cards.
        Calls.Value.Count.ShouldBeGreaterThan(100);
        var problems = new List<string>();
        var seen = new HashSet<(string Kind, string File)>();
        foreach (var call in Calls.Value)
        {
            var at = $"{call.File}:{call.Line}";
            if (Resolve(call.File, call.KindExpr) is not { } kind)
            {
                if (!CardKindCatalog.VariableKinds.Contains($"{call.File}|{Compact(call.KindExpr)}"))
                {
                    problems.Add($"{at}: the kind '{Compact(call.KindExpr)}' of a {call.Form} is neither a constant nor a variable site the catalog names (CardKindCatalog.VariableKinds)");
                }
                continue;
            }
            seen.Add((kind.Value, call.File));
            var entry = CardKindCatalog.All.FirstOrDefault(e => e.Kind == kind.Value);
            if (entry is null)
            {
                problems.Add($"{at}: a card of kind '{kind.Value}' is raised and the catalog does not know it; add it to CardKindCatalog with its bin and the test that pins its title and choices");
                continue;
            }
            if (!entry.Files.Contains(call.File))
            {
                problems.Add($"{at}: '{kind.Value}' is raised here, and the catalog lists only {string.Join(", ", entry.Files)}");
            }
            var bins = entry.Bins.Split('|', StringSplitOptions.RemoveEmptyEntries);
            foreach (var bin in call.Bins.Where(b => !bins.Contains(b)))
            {
                problems.Add($"{at}: '{kind.Value}' is raised into {bin}, and the catalog lists {entry.Bins}");
            }
        }
        foreach (var entry in CardKindCatalog.All)
        {
            foreach (var file in entry.Files.Where(f => !seen.Contains((entry.Kind, f))))
            {
                problems.Add($"catalog: '{entry.Kind}' is no longer raised in {file}");
            }
        }
        problems.ShouldBeEmpty(string.Join('\n', problems));
    }

    [Fact]
    public void EveryTitleAndEveryButtonUnderPilotNodeNamesItsReasonAndSaysWhatItDoes()
    {
        var problems = new List<string>();
        foreach (var call in Calls.Value)
        {
            problems.AddRange(CardGuard.Findings(call));
            if (call.Title.Count == 0 && !CardKindCatalog.TitlesFrom.ContainsKey($"{call.File}|{Compact(call.TitleExpr)}"))
            {
                problems.Add($"{call.File}:{call.Line}: the title '{Compact(call.TitleExpr)}' is no literal and the catalog does not say where its words come from (CardKindCatalog.TitlesFrom)");
            }
        }
        foreach (var (file, source) in Sources.Value)
        {
            problems.AddRange(CardSource.Labels(file, source).SelectMany(CardGuard.Findings));
        }
        problems.ShouldBeEmpty(string.Join('\n', problems));
    }

    [Fact]
    public void EveryCatalogKindHasAPinningTestThatExistsAndNamesTheKind()
    {
        // Build output is no test source: obj holds one generated file of the same name per configuration built (Debug and Release).
        var tests = Directory.EnumerateFiles(Path.Combine(Src.Value, "Pilot.Node.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => File.ReadAllText(f), StringComparer.Ordinal);
        var tokens = Calls.Value
            .Select(c => (Call: c, Kind: Resolve(c.File, c.KindExpr)))
            .Where(c => c.Kind is not null)
            .GroupBy(c => c.Kind!.Value.Value, c => c.Kind!.Value.Token)
            .ToDictionary(g => g.Key, g => g.ToHashSet());
        var problems = new List<string>();
        foreach (var entry in CardKindCatalog.All)
        {
            // The last dot: a partial's file (WatchtowerChatTests.Refused) has one in its name.
            var dot = entry.Pin.LastIndexOf('.');
            var (cls, method) = (entry.Pin[..dot], entry.Pin[(dot + 1)..]);
            if (!tests.TryGetValue(cls, out var text) || Regex.Match(text, $@"public (?:async Task|void) {Regex.Escape(method)}\(") is not { Success: true } at)
            {
                problems.Add($"{entry.Kind}: the pin {entry.Pin} is not a test in Pilot.Node.Tests");
                continue;
            }
            if (entry.Pin == CardKindCatalog.SourcePinned)
            {
                CardKindCatalog.SourcePins.Select(p => p.Kind).ShouldContain(entry.Kind);
                continue;
            }
            var next = Regex.Match(text[(at.Index + at.Length)..], @"\n    (?:\[[^\n]*\]\n    )*public ");
            var body = next.Success ? text.Substring(at.Index, at.Length + next.Index) : text[at.Index..];
            var wanted = new[] { $"\"{entry.Kind}\"" }.Concat(tokens.GetValueOrDefault(entry.Kind) ?? []).ToArray();
            if (!wanted.Any(body.Contains))
            {
                problems.Add($"{entry.Kind}: the pin {entry.Pin} never names it (as {string.Join(" or ", wanted)})");
            }
        }
        problems.ShouldBeEmpty(string.Join('\n', problems));
    }

    /// <summary>
    /// A ring is for what only the person can settle (#799 rule 5). So each Ring kind names the fact it was checked against before it rang,
    /// and offers at least one choice that does something: a verb of <see cref="Verbs"/> that its raising files really offer (so a card
    /// whose choices were all dropped, or never written, fails here). Dismissing alone is no choice: every card has it. A kind with no
    /// verb to press is a For-you card or a Handled line, not a Ring.
    /// This is the static half: it reads the source, so it guards against drift and proves nothing about a card at runtime. The runtime
    /// half is the one kind that can raise a Ring with no choice, the node's refused merge on a PR that is closed: it is raised
    /// For you then, pinned by <c>FalseRingTests.TheNodesMergeRefusedOnAClosedPrIsAForYouCardWithNothingToPress_c17100</c>.
    /// </summary>
    [Fact]
    public void EveryRingKindNamesTheFactItIsCheckedAgainstAndAChoiceThePersonCanUsefullyPress()
    {
        var known = typeof(Verbs).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var rings = CardKindCatalog.All.Where(e => e.Bins.Split('|', StringSplitOptions.RemoveEmptyEntries).Contains("Ring")).ToList();
        rings.Count.ShouldBeGreaterThan(30);
        var problems = new List<string>();
        foreach (var entry in rings)
        {
            if (string.IsNullOrWhiteSpace(entry.Fact) || entry.Fact.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5)
            {
                problems.Add($"{entry.Kind}: rings and names no fact it is checked against (CardKindEntry.Fact: what the node read that says the person must act)");
            }
            var choices = entry.Choices ?? [];
            if (choices.Where(c => c != nameof(Verbs.Dismiss)).Count() == 0)
            {
                problems.Add($"{entry.Kind}: rings with no choice to press but Dismiss; give its card a verb the person can use, or raise it into ForYou or Handled");
            }
            foreach (var choice in choices)
            {
                if (!known.Contains(choice))
                {
                    problems.Add($"{entry.Kind}: its choice '{choice}' is no Verbs constant, so the node would refuse it");
                }
                else if (!entry.Files.Concat(entry.ChoiceFiles ?? []).Any(f => Sources.Value.First(s => s.File == f).Source.Contains($"Verbs.{choice}", StringComparison.Ordinal)))
                {
                    problems.Add($"{entry.Kind}: its choice '{choice}' is not offered by {string.Join(", ", entry.Files)}");
                }
            }
        }
        problems.ShouldBeEmpty(string.Join('\n', problems));
        // Only a Ring kind carries them: a fact or choices on another bin would be read as checked when nothing is.
        CardKindCatalog.All.Where(e => !rings.Contains(e) && (e.Fact is not null || e.Choices is not null)).Select(e => e.Kind).ShouldBeEmpty();
    }

    /// <summary>
    /// What only the node's rules decide is a NodePress, so the watchtower never presses it through a CLI command Claude Code's classifier can
    /// refuse (#1311: <c>review pr</c> on pr-fix-pushed-unreviewed). Each marked kind is one NodePress owns, and offers the verb NodePress presses on it;
    /// each NodePress kind is marked; and a kind that offers one of NodePress's verbs is marked or named as the person's, with its reason.
    /// What each scan sees, and what it does not. The unmarked-kind scan reads the <c>Verbs.Name</c> constants in a raising call's own
    /// arguments, and, when a call passes its choices as a plain identifier, those of the enclosing member (CardSource.Calls, ListBuiltVerbs),
    /// so a kind that builds its choices in a list before the call is seen. It still cannot see a list built in another member, a field or a
    /// helper, and a member raising several kinds gives each the verbs of all of them. A marked kind is read in two ways: its calls and, failing
    /// that, a word match of <c>Verbs.Name</c> in its raising files; a PersonsOwn entry only by that word match in its raising files and
    /// ChoiceFiles. Both matches are per file and not per kind: an entry stays valid after its own kind stops offering the verb while
    /// another kind in the same files still does.
    /// </summary>
    [Fact]
    public void EveryKindTheNodeAloneDecidesIsANodePressAndNoKindOffersItsVerbsUnmarked()
    {
        var problems = new List<string>();
        var pressVerbs = NodePress.Kinds.Select(k => k.Verb).ToHashSet(StringComparer.Ordinal);
        // Compared by constant NAME, never value to name: Verbs.OverlapPickFold and Verbs.Fold are both "fold".
        pressVerbs.SetEquals(PressNames.Select(ValueOf)).ShouldBeTrue("NodePress.Kinds presses a verb this guard does not name in PressNames, or the reverse; update PressNames");
        string NameOf(string verb) => PressNames.First(n => ValueOf(n) == verb);
        var offered = Calls.Value
            .Select(c => (Kind: Resolve(c.File, c.KindExpr)?.Value, Verbs: c.ChoiceVerbs ?? []))
            .Where(c => c.Kind is not null)
            .GroupBy(c => c.Kind!)
            .ToDictionary(g => g.Key, g => g.SelectMany(c => c.Verbs).ToHashSet(StringComparer.Ordinal));
        var asNames = PressNames.ToDictionary(n => n, n => ValueOf(n));
        bool FileOffers(string file, string name) => Regex.IsMatch(Sources.Value.First(s => s.File == file).Source, $@"\bVerbs\.{name}\b");

        foreach (var entry in CardKindCatalog.All.Where(e => e.NodePressed))
        {
            if (!NodePress.Owns(entry.Kind))
            {
                problems.Add($"{entry.Kind}: marked NodePressed and NodePress.Owns is false for it; add it to NodePress.Kinds with the verb it presses");
            }
            else if (NodePress.VerbOf(entry.Kind) is { } verb && !(offered.GetValueOrDefault(entry.Kind) ?? []).Contains(NameOf(verb))
                && !entry.Files.Any(f => FileOffers(f, NameOf(verb))))
            {
                // A card whose choices are built in a list before the call is read from its raising files, as the Ring guard reads them.
                problems.Add($"{entry.Kind}: NodePress presses {NameOf(verb)} on it and its raising calls do not offer that verb");
            }
        }
        foreach (var press in NodePress.Kinds)
        {
            var entry = CardKindCatalog.All.FirstOrDefault(e => e.Kind == press.Kind);
            if (entry is not { NodePressed: true })
            {
                problems.Add($"{press.Kind}: NodePress presses {NameOf(press.Verb)} on it and the catalog does not mark it NodePressed");
            }
        }
        foreach (var entry in CardKindCatalog.All.Where(e => !e.NodePressed))
        {
            // The verbs of the kind's own calls (arguments, or the enclosing member for a list built before the call): a word match in a whole file flags every kind sharing it (ten kinds did).
            var verbs = (offered.GetValueOrDefault(entry.Kind) ?? []).Where(asNames.ContainsKey).Order().ToList();
            var named = CardKindCatalog.PersonsOwn.TryGetValue(entry.Kind, out var reason);
            if (verbs.Count > 0 && !named)
            {
                problems.Add($"{entry.Kind}: offers {string.Join(", ", verbs)}, which NodePress presses on other kinds, and is neither NodePressed nor in CardKindCatalog.PersonsOwn with a reason");
            }
            // A named kind may build its choices before the call, so it is read from its raising files and ChoiceFiles.
            var inFiles = asNames.Keys.Any(n => entry.Files.Concat(entry.ChoiceFiles ?? []).Any(f => FileOffers(f, n)));
            if (named && (!inFiles || string.IsNullOrWhiteSpace(reason)))
            {
                problems.Add($"{entry.Kind}: named in CardKindCatalog.PersonsOwn but {(!inFiles ? "its raising files offer none of NodePress's verbs" : "gives no reason")}");
            }
        }
        foreach (var kind in CardKindCatalog.PersonsOwn.Keys.Where(k => CardKindCatalog.All.All(e => e.Kind != k || e.NodePressed)))
        {
            problems.Add($"{kind}: named in CardKindCatalog.PersonsOwn and is no unmarked catalog kind");
        }
        problems.ShouldBeEmpty(string.Join('\n', problems));
    }

    /// <summary>The constants of <see cref="Verbs"/> that NodePress runs, by name.</summary>
    private static readonly string[] PressNames = [nameof(Verbs.EndStray), nameof(Verbs.Fold), nameof(Verbs.FixPr), nameof(Verbs.ReviewPr)];

    private static string ValueOf(string name) => (string)typeof(Verbs).GetField(name)!.GetRawConstantValue()!;

    /// <summary>
    /// Pins why the guard compares constant names: Verbs.Fold and Verbs.OverlapPickFold share a value, so a value compare would read one
    /// as the other. It does not run the guard; the guard's own read of the overlap kind holds the Fold case.
    /// </summary>
    [Fact]
    public void TheGuardTellsFoldFromOverlapPickFoldAlthoughTheyShareAValue()
    {
        ValueOf(nameof(Verbs.Fold)).ShouldBe(ValueOf(nameof(Verbs.OverlapPickFold)));
        new[] { nameof(Verbs.Fold) }.Where(PressNames.Contains).ShouldBe([nameof(Verbs.Fold)]);
        new[] { nameof(Verbs.OverlapPickFold) }.Where(PressNames.Contains).ShouldBeEmpty();
    }

    [Fact]
    public void SourcePinned()
    {
        var problems = new List<string>();
        foreach (var (kind, titleHas, labelHas) in CardKindCatalog.SourcePins)
        {
            var calls = Calls.Value.Where(c => Resolve(c.File, c.KindExpr)?.Value == kind).ToList();
            if (!calls.Any(c => c.Title.Any(l => l.Template.Contains(titleHas, StringComparison.OrdinalIgnoreCase))))
            {
                problems.Add($"{kind}: no title of it keeps '{titleHas}'");
            }
            if (labelHas is not null)
            {
                var files = calls.Select(c => c.File).ToHashSet();
                var labels = Sources.Value.Where(s => files.Contains(s.File)).SelectMany(s => CardSource.Labels(s.File, s.Source)).SelectMany(l => l.Literals);
                if (!labels.Any(l => l.Template.Contains(labelHas, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{kind}: no button of it keeps '{labelHas}'");
                }
            }
        }
        problems.ShouldBeEmpty(string.Join('\n', problems));
    }

    [Fact]
    public void TheSummaryTableKnowsEveryKindInTheCatalogSoTheGenericFallbackIsReachedByNone()
    {
        const string probe = "PROBE-TITLE";
        var generic = CardKindCatalog.All
            .Where(e => CardSummaries.For(e.Kind, "flight/X1/a", probe, []).Contains(probe, StringComparison.Ordinal))
            .Select(e => e.Kind)
            .ToList();

        generic.ShouldBeEmpty($"no line in CardSummaries.For for: {string.Join(", ", generic)}");
    }

    [Fact]
    public void ASummaryTableLineSaysNothingGenericEither()
    {
        foreach (var entry in CardKindCatalog.All)
        {
            var line = CardSummaries.For(entry.Kind, "flight/X1/a", "title", []);
            CardSummaries.Usable(line).ShouldBeTrue($"{entry.Kind}: {line}");
            CardGuard.Words(line).ShouldBeEmpty($"{entry.Kind}: {line}");
        }
    }

    [Fact]
    public void TheGuardReportsAGenericTitleAndAGenericButtonInASampleCall()
    {
        const string sample = """
            state.RaiseCard(CardBin.Ring, "x-kind", "flight/X1", "x failed", null,
                [new CardChoice { Label = "Redo leg", Verb = "go-around", Target = "flight/X1/a" }]);
            """;

        var findings = CardSource.Calls("Sample.cs", sample).SelectMany(CardGuard.Findings)
            .Concat(CardSource.Labels("Sample.cs", sample).SelectMany(CardGuard.Findings)).ToList();

        findings.ShouldContain(f => f.Contains("failed", StringComparison.Ordinal) && f.Contains("title", StringComparison.Ordinal));
        findings.ShouldContain(f => f.Contains("Redo leg", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{code}'s worker stalled", null);""", "stalled")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", "The worker gave up", null);""", "gave up")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{pr} asks for changes", null);""", "asks for changes")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", ok ? "Merged by itself" : $"{pr} stopped by itself", null);""", "by itself")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{pr} did not merge", null, [new CardChoice { Label = "Mark done anyway", Verb = "v", Target = "t" }]);""", "Mark done anyway")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{pr} did not merge", null, [new CardChoice { Label = "Ready anyway", Verb = "v", Target = "t" }]);""", "Ready anyway")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{pr} did not merge", null, [new CardChoice { Label = "Retry", Verb = "v", Target = "t" }]);""", "Retry")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{pr} did not merge", null, [new CardChoice { Label = "Resume", Verb = "v", Target = "t" }]);""", "Resume")]
    public void EachGenericWordTheDoneWhenNamesIsReported(string sample, string word)
    {
        var findings = CardSource.Calls("Sample.cs", sample).SelectMany(CardGuard.Findings)
            .Concat(CardSource.Labels("Sample.cs", sample).SelectMany(CardGuard.Findings)).ToList();

        findings.ShouldContain(f => f.Contains(word, StringComparison.Ordinal), string.Join('\n', findings));
    }

    [Theory]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{code}'s worker stalled: {why}", null);""")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{code}'s worker failed after {n} nudges", null);""")]
    [InlineData("""state.RaiseCard(CardBin.Ring, "k", "s", $"{pr} was merged by itself because its checks passed", null, [new CardChoice { Label = $"Retry the push of {pr}", Verb = "v", Target = "t" }]);""")]
    public void ATitleOrButtonThatSaysWhyOrWhatItDoesIsLeftAlone(string sample)
    {
        CardSource.Calls("Sample.cs", sample).SelectMany(CardGuard.Findings)
            .Concat(CardSource.Labels("Sample.cs", sample).SelectMany(CardGuard.Findings)).ShouldBeEmpty();
    }

    [Fact]
    public void ACallPassingAListBuiltEarlierInTheMemberGetsThatListsVerbs()
    {
        const string sample = """
            class C
            {
                void Raise()
                {
                    var choices = new List<CardChoice> { new() { Label = "Review it", Verb = Verbs.ReviewPr, Target = "t" } };
                    state.RaiseCard(CardBin.Ring, "x-kind", "s", "x needs a look", null, choices);
                }
            }
            """;

        CardSource.Calls("Sample.cs", sample).Single().ChoiceVerbs.ShouldBe([nameof(Verbs.ReviewPr)]);
    }

    [Fact]
    public void ACallWithItsVerbsInItsArgumentsOrAListBuiltInAnotherMemberKeepsTheAnswerTheSummarySays()
    {
        const string own = """
            class C
            {
                void Raise()
                {
                    state.RaiseCard(CardBin.Ring, "x-kind", "s", "x needs a look", null, [new CardChoice { Label = "Fold it", Verb = Verbs.Fold, Target = "t" }]);
                }
            }
            """;
        const string other = """
            class C
            {
                void Build()
                {
                    var choices = new List<CardChoice> { new() { Label = "Fix it", Verb = Verbs.FixPr, Target = "t" } };
                }

                void Raise(List<CardChoice> choices)
                {
                    state.RaiseCard(CardBin.Ring, "x-kind", "s", "x needs a look", null, choices);
                }
            }
            """;

        CardSource.Calls("Sample.cs", own).Single().ChoiceVerbs.ShouldBe([nameof(Verbs.Fold)]);
        CardSource.Calls("Sample.cs", other).Single().ChoiceVerbs.ShouldBeEmpty();
    }

    [Fact]
    public void ARecordCardWhoseLastArgumentIsPlainTextGetsNoVerbsFromItsSiblingsInTheMember()
    {
        const string source = """
            class C
            {
                void Run(string said)
                {
                    state.RaiseCard(CardBin.Handled, "x-word", "s", "x answered", said);
                    state.RaiseCard(CardBin.Ring, "x-other", "s", "needs a look", null, [new CardChoice { Label = "Fix it", Verb = Verbs.FixPr, Target = "t" }]);
                }
            }
            """;

        CardSource.Calls("Sample.cs", source).Single(c => c.KindExpr.Contains("x-word")).ChoiceVerbs.ShouldBeEmpty();
    }

    [Fact]
    public void AListBuiltReadSeesAChoicesListFollowedByANamedArgumentButNotANullOrBoolean()
    {
        const string source = """
            class C
            {
                void Run()
                {
                    var choices = new List<CardChoice> { new CardChoice { Label = "Fix it", Verb = Verbs.FixPr, Target = "t" } };
                    state.RaiseCard(CardBin.Ring, "x-list", "s", "needs a look", choices, link: "l");
                    state.RaiseCard(CardBin.Ring, "x-null", "s", "needs a look", null);
                    state.RaiseCard(CardBin.Ring, "x-bool", "s", "needs a look", true);
                }
            }
            """;

        var calls = CardSource.Calls("Sample.cs", source);
        calls.Single(c => c.KindExpr.Contains("x-list")).ChoiceVerbs.ShouldContain("FixPr");
        calls.Single(c => c.KindExpr.Contains("x-null")).ChoiceVerbs.ShouldBeEmpty();
        calls.Single(c => c.KindExpr.Contains("x-bool")).ChoiceVerbs.ShouldBeEmpty();
    }

    [Fact]
    public void TheScannerSkipsCommentsAndStringsThatLookLikeCalls()
    {
        const string source = """
            // state.RaiseCard(CardBin.Ring, "no", "s", "ignored failed", null);
            var text = "state.RaiseCard(CardBin.Ring, \"no\", \"s\", \"ignored failed\", null)";
            state.RaiseCard(CardBin.Handled, Kind, subject, $"Compacted {row.Name}: {(a ? "x, y" : "z")}, done");
            """;

        var calls = CardSource.Calls("Sample.cs", source);

        calls.Count.ShouldBe(1);
        calls[0].KindExpr.ShouldBe("Kind");
        calls[0].Bins.ShouldBe(["Handled"]);
        calls[0].Title.Single().Template.ShouldBe("Compacted §: §, done");
    }
}
