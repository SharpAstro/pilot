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
public sealed class CardKindTests
{
    private static readonly Lazy<string> Src = new(FindSrc);

    private static readonly Lazy<List<(string File, string Source)>> Sources = new(() =>
        [.. Directory.EnumerateFiles(Path.Combine(Src.Value, "Pilot.Node"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (File: Path.GetRelativePath(Path.Combine(Src.Value, "Pilot.Node"), f).Replace('\\', '/'), Source: System.IO.File.ReadAllText(f)))]);

    private static readonly Lazy<List<CardCall>> Calls = new(() => [.. Sources.Value.SelectMany(s => CardSource.Calls(s.File, s.Source))]);

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
    private static (string Value, string Token)? Resolve(string file, string expr)
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
        var tests = Directory.EnumerateFiles(Path.Combine(Src.Value, "Pilot.Node.Tests"), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => File.ReadAllText(f), StringComparer.Ordinal);
        var tokens = Calls.Value
            .Select(c => (Call: c, Kind: Resolve(c.File, c.KindExpr)))
            .Where(c => c.Kind is not null)
            .GroupBy(c => c.Kind!.Value.Value, c => c.Kind!.Value.Token)
            .ToDictionary(g => g.Key, g => g.ToHashSet());
        var problems = new List<string>();
        foreach (var entry in CardKindCatalog.All)
        {
            var dot = entry.Pin.IndexOf('.');
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
