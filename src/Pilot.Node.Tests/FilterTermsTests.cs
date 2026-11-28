using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The name terms of a test filter and the tests a project's sources declare (pilot #1157): parsed and matched from files only.</summary>
public sealed class FilterTermsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ft-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() => TestDirs.DeleteTree(_root);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private FilterTerms.Project Load(string source)
    {
        Write("T/T.csproj", "<Project />");
        Write("T/A.cs", source);
        return FilterTerms.Load([Path.Combine(_root, "T")], _root)!;
    }

    private static bool Hit(FilterTerms.Project p, string filter) => FilterTerms.Expression(filter).All(t => FilterTerms.Matches(t, p));

    [Fact]
    public void ASourceIsLexedOnceWhileItStandsAndAgainOnceItChanges()
    {
        // A file's parse is kept by its length and write time (pilot #1443): a plan check read and lexed every test source again.
        var first = Load("""
            namespace N;
            public class KeptTests { [Fact] public void A() { } }
            """);
        first.MethodNames.ShouldBe(["A"]);
        FilterTerms.Load([Path.Combine(_root, "T")], _root)!.MethodNames.ShouldBe(["A"]);

        var file = Path.Combine(_root, "T", "A.cs");
        File.WriteAllText(file, """
            namespace N;
            public class KeptTests { [Fact] public void A() { } [Fact] public void B() { } }
            """);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(2));

        FilterTerms.Load([Path.Combine(_root, "T")], _root)!.MethodNames.ShouldBe(["A", "B"], ignoreOrder: true);
    }

    [Fact]
    public void ExpressionSplitsOnOrAndAndKeepsNameTerms()
    {
        var terms = FilterTerms.Expression("(FullyQualifiedName~A|FullyQualifiedName=B.C)&Name~D").Select(t => t.Text).ToList();

        terms.ShouldBe(["FullyQualifiedName~A", "FullyQualifiedName=B.C", "Name~D"]);
    }

    [Fact]
    public void CategoryTraitAndNegatedTermsAreNeverTerms()
    {
        FilterTerms.Expression("Category=Invariant|Priority=1|FullyQualifiedName!~Slow|Name!=X|TestCategory=Y").ShouldBeEmpty();
    }

    [Fact]
    public void ABareWordIsAFullyQualifiedNameContains()
    {
        var term = FilterTerms.Expression("Foo").ShouldHaveSingleItem();

        (term.Property, term.Op, term.Value).ShouldBe(("fullyqualifiedname", "~", "Foo"));
    }

    [Fact]
    public void AnEscapedParenthesisBelongsToTheValue()
    {
        FilterTerms.Expression(@"FullyQualifiedName~M\(1\)").ShouldHaveSingleItem().Value.ShouldBe("M(1)");
    }

    [Fact]
    public void PlatformClassAndMethodValuesAreTermsAndTheirSiblingsAreNot()
    {
        var terms = FilterTerms.TermsOf(["--filter-class", "A*", "B", "--filter-not-class", "C", "--filter-method=*.M", "--filter-trait", "k", "v"]);

        terms.Select(t => t.Text).ShouldBe(["--filter-class A*", "--filter-class B", "--filter-method *.M"]);
    }

    [Fact]
    public void TestsAreFoundInFileScopedAndBlockNamespacesWithAttributeLists()
    {
        var p = Load("""
            namespace One.Two;
            public class FooTests
            {
                [Fact] public void A() { }
                [Theory, MemberData(nameof(Rows))]
                public async Task B(int x) { await Task.CompletedTask; }
                [Fact(Skip = "later [Fact] public void Fake() {}")] public void C() { }
                public void NotATest() { }
                public static int[] Rows = [1, 2];
            }
            """);

        p.FullNames.ShouldBe(["One.Two.FooTests.A", "One.Two.FooTests.B", "One.Two.FooTests.C"]);

        var block = Load("""
            namespace P { namespace Q { public sealed class BarTests { [Fact] public void Z() { } } } }
            """);
        block.FullNames.ShouldBe(["P.Q.BarTests.Z"]);
    }

    [Fact]
    public void ANestedClassIsListedAsOuterPlusInnerAndOuterDotInner()
    {
        var p = Load("""
            namespace N;
            public class Outer { public class Inner { [Fact] public void M() { } } }
            """);

        p.FullNames.ShouldBe(["N.Outer+Inner.M", "N.Outer.Inner.M"]);
        Hit(p, "FullyQualifiedName~Outer.Inner").ShouldBeTrue();
        Hit(p, "FullyQualifiedName~Outer+Inner").ShouldBeTrue();
    }

    [Fact]
    public void AnInheritedTestIsListedUnderEachDerivedClassAndNotUnderTheAbstractBase()
    {
        var p = Load("""
            namespace N;
            public abstract class BaseTests { [Fact] public void FromBase() { } }
            public sealed class OneTests : BaseTests { [Fact] public void Own() { } }
            public sealed class TwoTests : BaseTests<int> { }
            public abstract class BaseTests<T> : BaseTests { }
            """);

        p.FullNames.ShouldBe(["N.OneTests.FromBase", "N.OneTests.Own", "N.TwoTests.FromBase"], ignoreOrder: true);
        p.TestClasses.ShouldContain("BaseTests");
    }

    [Fact]
    public void StringsCommentsAndDirectivesHideNothingAndInventNothing()
    {
        var p = Load(""""
            namespace N;
            #if DEBUG
            #endif
            public class RealTests
            {
                // [Fact] public void Commented() { }
                /* [Fact] public void Blocked() { } */
                private const string S = "class Fake { [Fact] public void InString() {} }";
                private const string V = @"class Fake2 { [Fact] public void Verbatim() {} } ""x""";
                private const string Raw = """class Fake3 { [Fact] public void RawOne() {} }""";
                [Fact] public void Real() { var c = '{'; }
                [Fact] public void AfterTheBrace() { }
            }
            """");

        p.FullNames.ShouldBe(["N.RealTests.Real", "N.RealTests.AfterTheBrace"]);
    }

    [Fact]
    public void AVerbatimStringOpeningWithEscapedQuotesIsNotARawString()
    {
        var p = Load("""""""
            namespace N;
            public class QuoteTests
            {
                [Theory]
                [InlineData("", @"""""")]
                public void Six(string a, string b) { }
                private const string X = @"""x""";
                private const string Y = $@"""{1}""";
                private const string Z = @$"""z""";
                [Fact] public void AfterSix() { }
                [Fact] public void AfterAll() { }
            }
            """"""");

        p.FullNames.ShouldBe(["N.QuoteTests.Six", "N.QuoteTests.AfterSix", "N.QuoteTests.AfterAll"]);
    }

    [Fact]
    [Trait("Category", InvariantTraitTests.Invariant)]
    public void TheReaderFindsEveryTestTheCompilerBuiltInPilotNodeTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull();
        var p = FilterTerms.Load([Path.Combine(dir.FullName, "src", "Pilot.Node.Tests")], dir.FullName)!;
        var listed = p.FullNames.ToHashSet(StringComparer.Ordinal);

        var compiled = typeof(FilterTermsTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && (t.IsPublic || t.IsNestedPublic))
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                .Where(m => m.GetCustomAttributes(inherit: true).Any(a => a.GetType().Name is var n && (n.EndsWith("FactAttribute", StringComparison.Ordinal) || n.EndsWith("TheoryAttribute", StringComparison.Ordinal))))
                .Select(m => $"{t.FullName}.{m.Name}"))
            .Distinct()
            .ToList();

        compiled.ShouldNotBeEmpty();
        compiled.Where(n => !listed.Contains(n) && !listed.Contains(n.Replace('+', '.'))).ShouldBeEmpty();
    }

    [Fact]
    public void BinAndObjAreSkipped()
    {
        Write("T/T.csproj", "<Project />");
        Write("T/A.cs", "namespace N; public class ATests { [Fact] public void M() { } }");
        Write("T/obj/Gen.cs", "namespace N; public class GenTests { [Fact] public void G() { } }");
        Write("T/bin/Debug/B.cs", "namespace N; public class BinTests { [Fact] public void G() { } }");

        FilterTerms.Load([Path.Combine(_root, "T")], _root)!.FullNames.ShouldBe(["N.ATests.M"]);
    }

    [Fact]
    public void TildeIsACaseInsensitiveContainsAndEqualsIsExactOnTheFullName()
    {
        var p = Load("namespace N; public class FooTests { [Fact] public void Bar() { } }");

        Hit(p, "FullyQualifiedName~footests").ShouldBeTrue();
        Hit(p, "FullyQualifiedName~FooTests.Bar").ShouldBeTrue();
        Hit(p, "FullyQualifiedName=N.FooTests.Bar").ShouldBeTrue();
        Hit(p, "FullyQualifiedName=FooTests.Bar").ShouldBeFalse();
        Hit(p, "FullyQualifiedName~Nope").ShouldBeFalse();
        Hit(p, "ClassName~FooTests").ShouldBeTrue();
        Hit(p, "ClassName=N.FooTests").ShouldBeTrue();
        Hit(p, "Name~Bar").ShouldBeTrue();
    }

    [Fact]
    public void PlatformWildcardsAreHonoured()
    {
        var p = Load("namespace N; public class FooTests { [Fact] public void Bar() { } }");

        FilterTerms.TermsOf(["--filter-class", "*Tests"]).All(t => FilterTerms.Matches(t, p)).ShouldBeTrue();
        FilterTerms.TermsOf(["--filter-class", "N.FooTests"]).All(t => FilterTerms.Matches(t, p)).ShouldBeTrue();
        FilterTerms.TermsOf(["--filter-class", "FooTests"]).All(t => FilterTerms.Matches(t, p)).ShouldBeFalse();
        FilterTerms.TermsOf(["--filter-method", "*.Bar"]).All(t => FilterTerms.Matches(t, p)).ShouldBeTrue();
        FilterTerms.TermsOf(["--filter-method", "*.Baz"]).All(t => FilterTerms.Matches(t, p)).ShouldBeFalse();
    }

    [Fact]
    public void ADeclaredClassMatchesTheTermsThatWouldFindIt()
    {
        var term = FilterTerms.Expression("FullyQualifiedName~NewTests").ShouldHaveSingleItem();

        FilterTerms.DeclaredMatches(term, "NewTests").ShouldBeTrue();
        FilterTerms.DeclaredMatches(term, "OtherTests").ShouldBeFalse();
    }

    [Fact]
    public void ASolutionTargetsEveryTestProjectOfItAndNoOther()
    {
        Write("src/S.slnx", """<Solution><Project Path="App/App.csproj" /><Project Path="App.Tests/App.Tests.csproj" /></Solution>""");
        Write("src/App/App.csproj", "<Project />");
        Write("src/App/Lib.cs", "namespace N; public class LibTests { [Fact] public void NotATestProject() { } }");
        Write("src/App.Tests/App.Tests.csproj", "<Project />");
        Write("src/App.Tests/A.cs", "namespace N; public class ATests { [Fact] public void M() { } }");

        var target = FilterTerms.Targets("cd src && dotnet test S.slnx --filter 'FullyQualifiedName~ATests'", _root).ShouldHaveSingleItem();

        target.Projects.ShouldBe([Path.Combine(_root, "src", "App.Tests")]);
        target.Terms.ShouldHaveSingleItem().Value.ShouldBe("ATests");
    }

    [Fact]
    public void TheRealPilotGuiTestsHoldNoTestClassNamedTerminalWireframeSweepTestsButTheSweepClasses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull();
        var p = FilterTerms.Load([Path.Combine(dir.FullName, "src", "Pilot.Gui.Tests")], dir.FullName)!;

        Hit(p, "FullyQualifiedName~TerminalWireframeSweepTests").ShouldBeFalse();
        Hit(p, "FullyQualifiedName~TerminalWireframe").ShouldBeTrue();
        p.TestClasses.ShouldNotContain("TerminalWireframeSweepTests");
    }
}
