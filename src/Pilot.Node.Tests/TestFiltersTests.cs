using Pilot.Node;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What a dotnet test filter is (pilot #1086): VSTest's --filter and the Microsoft.Testing.Platform options, in both spellings.</summary>
public sealed class TestFiltersTests
{
    [Theory]
    [InlineData("--filter")]
    [InlineData("--filter=A")]
    [InlineData("--FILTER")]
    [InlineData("--filter-class")]
    [InlineData("--filter-not-class=A.B")]
    [InlineData("--filter-method")]
    [InlineData("--filter-not-method")]
    [InlineData("--filter-namespace")]
    [InlineData("--filter-not-namespace")]
    [InlineData("--filter-trait")]
    [InlineData("--Filter-Not-Trait=Category=GUI")]
    [InlineData("--filter-query=/*/*/Foo/*")]
    public void EveryFilterSpellingIsAFilterOption(string word) => TestFilters.IsFilterOption(word).ShouldBeTrue();

    [Theory]
    [InlineData("--filters")]
    [InlineData("--filter-other")]
    [InlineData("-c")]
    [InlineData("X.csproj")]
    public void OtherWordsAreNot(string word) => TestFilters.IsFilterOption(word).ShouldBeFalse();

    [Fact]
    public void ReadGivesEachOptionAndValueIncludingAllValuesOfAMultiValuedOne()
    {
        var read = TestFilters.Read(["X", "--filter-class", "A.B", "C.D", "-c", "Release", "--filter-trait=Category=GUI", "--filter", "FullyQualifiedName~Z"]);

        read.ShouldBe([("--filter-class", "A.B"), ("--filter-class", "C.D"), ("--filter-trait", "Category=GUI"), ("--filter", "FullyQualifiedName~Z")]);
    }

    [Fact]
    public void ReadStopsAtABareDoubleDash() =>
        TestFilters.Read(["--filter-class", "A", "--", "--filter-class", "B"]).ShouldBe([("--filter-class", "A")]);

    [Fact]
    public void TheKeyIsSortedSoArgumentOrderDoesNotMatter() =>
        TestFilters.Key(["--filter-method", "M", "--filter-class", "B", "A"])
            .ShouldBe(TestFilters.Key(["--filter-class=A", "--filter-class", "B", "--filter-method=M"]));

    [Fact]
    public void ADifferentValueIsADifferentKey() =>
        TestFilters.Key(["--filter-class", "A"]).ShouldNotBe(TestFilters.Key(["--filter-class", "B"]));

    [Fact]
    public void ValueCountTakesOneForVsTestAndTheFollowingWordsForPlatformAndNoneForInlineValues()
    {
        TestFilters.ValueCount(["--filter", "A", "B"], 0).ShouldBe(1);
        TestFilters.ValueCount(["--filter-class", "A", "B", "-c"], 0).ShouldBe(2);
        TestFilters.ValueCount(["--filter-class=A", "B"], 0).ShouldBe(0);
        TestFilters.ValueCount(["-c", "A"], 0).ShouldBe(0);
    }

    [Fact]
    public void AMultiValuedFilterIsNoTargetOfADotnetBuild() =>
        DotnetCommand.Targets(["test", "X.csproj", "--filter-class", "A.B", "C.D", "--filter-trait=Category=GUI"]).ShouldBe(["X.csproj"]);
}
