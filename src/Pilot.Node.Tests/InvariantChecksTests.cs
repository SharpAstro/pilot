using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A filtered dotnet test in a leg's check also runs the Invariant guards (PIL700, PIL715, PIL697: red in CI, green in the plane).</summary>
public sealed class InvariantChecksTests
{
    private const string Widened = "'(FullyQualifiedName~Foo)|Category=Invariant'";

    [Theory]
    [InlineData("dotnet test X.slnx --filter FullyQualifiedName~Foo", $"dotnet test X.slnx --filter {Widened}")]
    [InlineData("dotnet test X.slnx --filter 'FullyQualifiedName~Foo'", $"dotnet test X.slnx --filter {Widened}")]
    [InlineData("dotnet test X.slnx --filter \"FullyQualifiedName~Foo\"", $"dotnet test X.slnx --filter {Widened}")]
    [InlineData("dotnet test X.slnx --filter=FullyQualifiedName~Foo", $"dotnet test X.slnx --filter={Widened}")]
    [InlineData("dotnet test X.slnx --filter=\"FullyQualifiedName~Foo\" -c Release", $"dotnet test X.slnx --filter={Widened} -c Release")]
    public void EachFilterFormIsWidenedAndSingleQuoted(string check, string expected) => InvariantChecks.With(check).ShouldBe(expected);

    [Fact]
    public void EveryDotnetTestSegmentIsWidened()
    {
        InvariantChecks.With("cd src && dotnet test A -c Release --filter FullyQualifiedName~Foo && dotnet test B --filter 'Name~Bar'")
            .ShouldBe("cd src && dotnet test A -c Release --filter '(FullyQualifiedName~Foo)|Category=Invariant' && dotnet test B --filter '(Name~Bar)|Category=Invariant'");
    }

    [Theory]
    [InlineData("dotnet test X.slnx -c Release --no-build")]
    [InlineData("cd src && dotnet build X.slnx -c Release")]
    [InlineData("dotnet build X.slnx --filter Foo")]
    [InlineData("dotnet test X.slnx --filter 'Category=Invariant'")]
    [InlineData("dotnet test X.slnx --filter 'Name~Foo|Category=Invariant'")]
    [InlineData("dotnet test X.slnx --filter \"Name~it's\"")]
    [InlineData("echo --filter Foo")]
    public void ACheckWithNothingToWidenIsLeftAlone(string check) => InvariantChecks.With(check).ShouldBe(check);

    [Fact]
    public void WideningTwiceChangesNothing()
    {
        var once = InvariantChecks.With("dotnet test X.slnx --filter FullyQualifiedName~Foo");
        InvariantChecks.With(once).ShouldBe(once);
    }

    [Fact]
    public void ATraitOnlyPlatformSegmentIsWidenedAndAnyOtherPlatformSegmentIsLeftAlone()
    {
        InvariantChecks.With("dotnet test X --filter-trait Category=Foo -- --x")
            .ShouldBe("dotnet test X --filter-trait Category=Foo --filter-trait Category=Invariant -- --x");
        var once = InvariantChecks.With("dotnet test X --filter-trait Category=Foo");
        InvariantChecks.With(once).ShouldBe(once);
        InvariantChecks.With("dotnet test X --filter-class Foo").ShouldBe("dotnet test X --filter-class Foo");
        InvariantChecks.With("dotnet test X --filter-trait Category=Foo --filter-method A.B").ShouldBe("dotnet test X --filter-trait Category=Foo --filter-method A.B");
    }

    /// <summary>One file says what a dotnet test filter is (TestFilters), so a reader cannot know only VSTest's spelling (pilot #1086).</summary>
    [Fact]
    [Trait("Category", "Invariant")]
    public void TheVsTestFilterOptionIsSpelledInOneFile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "Pilot.slnx")))
        {
            root = root.Parent;
        }
        root.ShouldNotBeNull();
        var literal = "\"--filter\"";
        var sep = Path.DirectorySeparatorChar;
        var found = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Pilot.Node"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Where(f => File.ReadAllText(f).Contains(literal, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root.FullName, f).Replace('\\', '/'))
            .ToList();
        found.ShouldBe(["src/Pilot.Node/Flights/TestFilters.cs"]);
    }
}
